using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

public sealed partial class AutomaticMultiInstanceCoordinator
{
    /// <summary>
    /// Transfers the entry job's capacity reservation to the body, resets its
    /// aggregate once, and installs a durable wake. Initial admission is a new
    /// segment so item locks cannot descend behind reset's shared locks.
    /// </summary>
    /// <param name="callTargetFailure">
    /// A call body's target failure (<c>call_target_unavailable</c>, <c>call_target_incompatible</c>,
    /// or <c>call_depth_exceeded</c>) when the entry could not pin a target. The call adapter
    /// resolves its target under the family lock, before the collection is counted here, so
    /// it passes either a pin in <paramref name="create"/> or this failure. A nonempty body
    /// keeps the pin or fails with this code before any body row exists; an empty body drops
    /// both and completes with <c>[]</c>.
    /// </param>
    public async Task<AutomaticMultiInstanceRecord?> InitializeBodyAsync(
        AutomaticMultiInstanceCreateRecord create, WorkflowModel definition, ActorContext actor,
        WorkflowJobFence entryFence, WorkflowJobCreateRecord controlTemplate,
        IAutomaticMultiInstanceLifecycle lifecycle, CancellationToken ct, string? callTargetFailure = null)
    {
        var call = create.ActivityKind == BpmnFlowNodeTypes.CallActivity;
        if (!call && (create.PinnedChildDefinitionId is not null || create.PinnedChildStartEventId is not null
                || callTargetFailure is not null))
            throw new ArgumentException("Only a call body has a target pin.", nameof(create));
        if (call && (create.PinnedChildDefinitionId is null) != (create.PinnedChildStartEventId is null))
            throw new ArgumentException("A call body's target pin names both its definition and its start.", nameof(create));
        if (call && (create.PinnedChildDefinitionId is null) == (callTargetFailure is null))
            throw new ArgumentException("A call body passes either its target pin or its target failure.", nameof(create));
        RequireLifecycleStores();
        var entry = await jobs.GetForUpdateAsync(entryFence.JobId, ct);
        if (entry is null || entry.Kind is not (WorkflowJobKinds.AsyncBefore or WorkflowJobKinds.MultiInstanceStart)
            || entry.InstanceId != create.InstanceId || entry.TokenId != create.TokenId
            || entry.WorkflowDefinitionId != create.WorkflowDefinitionId || entry.ActivationId != create.ActivationId
            || !MatchesLease(entry, entryFence) || !await jobs.IsLeaseAliveAsync(entryFence, ct))
            throw new WorkflowConflictException("The automatic body entry fence is stale.");
        var existing = await repository.FindOpenAsync(create.InstanceId, create.NodeId, ct);
        if (existing is not null)
        {
            if (existing.TokenId != create.TokenId || existing.ActivationId != create.ActivationId)
            {
                await WaitForEntrySlotAsync(entryFence, ct);
                return null;
            }
            // A committed body already owns its wake/reset. An entry retry may
            // acknowledge ownership but cannot reset the aggregate a second time.
            await CompleteDispatchAsync(entryFence, ct);
            return existing;
        }
        // The adapter chooses the referenced instance-input names; the common
        // core captures their current values and the collection under the exact
        // initialization locks. Never trust an earlier API/attempt's values.
        var requestedInputs = create.InputSnapshot.TryGetProperty("instanceInputs", out var requested)
            && requested.ValueKind == JsonValueKind.Object
            ? requested.EnumerateObject().Select(property => property.Name).ToArray() : [];
        var causalActor = create.ActorContext ?? new NodeExecutionActorRecord(actor.User, actor.Roles.ToArray())
        {
            AuditClaims = ActorContext.CopyAuditClaims(actor.AuditClaims),
            ActingFor = actor.ActingFor, DelegationId = actor.DelegationId
        };
        var input = await CaptureBodyInputAsync(create.InstanceId, create.WorkflowDefinitionId, definition,
            create.NodeId, create.MaxInstances, requestedInputs, create.InitializedAt, causalActor, ct);
        // Only a nonempty call body creates children, so only it needs, and keeps, its pin.
        // Its target failure is the entry's failure; an empty body ignores both.
        if (call && input.TotalCount > 0 && callTargetFailure is not null)
            throw new AutomaticMultiInstanceValueException(callTargetFailure);
        if (call && input.TotalCount == 0)
            create = create with { PinnedChildDefinitionId = null, PinnedChildStartEventId = null };
        var configuration = definition.FlowNodes.Single(node => node.Id == create.NodeId).MultiInstance!;
        var configuredDefault = engineSettings is null ? null
            : (await engineSettings.GetByKeyAsync(AutomaticMultiInstanceLimits.DefaultConcurrencyKey, ct))?.Value;
        var configuredMaximum = engineSettings is null ? null
            : (await engineSettings.GetByKeyAsync(AutomaticMultiInstanceLimits.MaxConcurrencyKey, ct))?.Value;
        var body = await InitializeAsync(create with
        {
            InputSnapshot = input.Snapshot, TotalCount = input.TotalCount, ActorContext = causalActor,
            Mode = configuration.Mode,
            EffectiveConcurrency = AutomaticMultiInstanceLimits.ResolveConcurrency(configuration, input.TotalCount,
                configuredDefault, configuredMaximum)
        }, ct);
        RequireTemplate(body, controlTemplate);
        if (!await repository.EnterBodyWaitAsync(body.Id, body.ActivationId, entry.Id, ct))
            throw new WorkflowConflictException("The automatic body no longer owns its entry token.");
        actor = BodyActor(body, actor);
        await lifecycle.InstallHostTimersAsync(body, ct);
        var empty = JsonSerializer.SerializeToElement(Array.Empty<object>());
        await WriteAggregateAsync(body, definition, actor, empty, checkVersion: false, refreshVersion: false, ct);
        await RecordBodyHistoryAsync(body, actor, InstanceHistoryNotes.AutomaticMultiInstanceStarted, ct);
        await lifecycle.DrainVariableNotificationsAsync(body, ct);
        var active = await BodyStillOwnsTokenAsync(body, ct);
        await CompleteDispatchAsync(entryFence, ct);
        if (active)
            await SignalAsync(body, controlTemplate, body.TotalCount == 0
                ? WorkflowJobKinds.MultiInstanceComplete : WorkflowJobKinds.MultiInstanceDispatch, null, ct);
        return (await repository.GetAsync(body.Id, false, ct))!;
    }

    public async Task<AutomaticMultiInstanceInputCapture> CaptureBodyInputAsync(long instanceId, long definitionId,
        WorkflowModel definition, int nodeId, int maxInstances, IReadOnlyCollection<string> instanceInputNames,
        DateTimeOffset initializedAt, NodeExecutionActorRecord? causalActor, CancellationToken ct)
    {
        RequireLifecycleStores();
        var node = definition.FlowNodes.Single(node => node.Id == nodeId);
        var source = definition.Variables.Single(variable => string.Equals(variable.Name,
            node.MultiInstance!.CollectionVariable, StringComparison.OrdinalIgnoreCase));
        var current = (await runtime!.LoadLatestVariableVersionsAsync(instanceId, ct))
            .ToDictionary(value => value.Name, value => value.Value, StringComparer.OrdinalIgnoreCase);
        var context = await variables.MergeEffectiveValuesAsync(definition, current,
            accessPlans.GetOrAdd(definitionId, definition).SelectAutomaticPhase(nodeId, AutomaticMultiInstanceAccessPhases.Initialize), ct);
        if (!context.TryGetValue(source.Name, out var value))
            throw new AutomaticMultiInstanceValueException(AutomaticMultiInstanceValues.SourceInvalid);
        var collection = AutomaticMultiInstanceValues.CopyCollection(value, source, maxInstances);
        var sharedAliases = definition.Variables.Where(variable => variable.Scope == VariableScopes.Shared)
            .Select(variable => variable.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var inputs = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in instanceInputNames.Distinct(StringComparer.OrdinalIgnoreCase))
            if (!sharedAliases.Contains(name) && current.TryGetValue(name, out var input)) inputs[name] = input;
        long? sourceVersion = null;
        if (source.Scope == VariableScopes.Shared)
        {
            var versions = await variables.LoadSharedValueVersionsAsync(definition, [source.Name], true, ct);
            sourceVersion = versions.TryGetValue(source.Name, out var version) ? version
                : throw new WorkflowConflictException("The automatic source shared value is unavailable.");
        }
        var snapshot = AutomaticMultiInstanceValues.CreateInputSnapshot(collection, inputs, initializedAt,
            source.Scope == VariableScopes.Shared ? source.SharedKey : null, sourceVersion,
            node.Type == BpmnFlowNodeTypes.CallActivity ? new NodeExecutionActorRecord("callActivity", []) : causalActor,
            await WorkflowEngineService.CaptureSequenceFlowInfoAsync(runtime, instanceId, definition, ct));
        return new(snapshot, collection.Count);
    }

    /// <summary>
    /// Captures an adapter-selected error boundary before any aggregate write.
    /// Siblings stop immediately; only the reserved completion job may publish
    /// the saved partial result and route the boundary after this commits.
    /// </summary>
    /// <param name="childFaultCode">A call item's child fault code, kept on its settled invocation.</param>
    public async Task<bool> CaptureItemFailureAsync(long executionId, long itemId, Guid activationId,
        AutomaticMultiInstanceItemFence fence, AutomaticMultiInstanceCompletionIntent intent, ActorContext actor,
        WorkflowJobCreateRecord controlTemplate, IAutomaticMultiInstanceLifecycle lifecycle, CancellationToken ct,
        string? failureDescription = null, string? childFaultCode = null)
    {
        RequireLifecycleStores();
        var (body, _, _) = await RequireItemFenceAsync(executionId, itemId, activationId, fence, ct);
        RequireTemplate(body, controlTemplate);
        if (intent.FailedItemId != itemId
            || string.IsNullOrWhiteSpace(intent.FailureCode) || intent.FailureCode.Length > 100)
            throw new ArgumentException("Invalid automatic body completion intent.", nameof(intent));
        if (!await repository.SaveCompletionIntentAsync(body.Id, activationId, JsonSerializer.SerializeToElement(intent), ct))
            return false;
        // A failed call item settles its own invocation first, so abandoning the body's
        // other calls sends no cancel message to a child that has already finished.
        if (fence.CallInvocationId is long ownInvocation
            && !await callActivities!.SettleAsync(ownInvocation, CallInvocationStates.Failed, intent.FailureCode,
                failureDescription ?? "Automatic call item failed.", childFaultCode, ct))
            throw new WorkflowConflictException("The automatic call item's invocation was settled during its failure.");
        await lifecycle.CloseHostSubscriptionsAsync(body, ct);
        // An adapter may name what failed, such as a REST item's HTTP status. The text lands
        // on the item and its node visit, so it never carries request data, response data,
        // or values.
        if (!await repository.FailItemAsync(body.Id, itemId, activationId, intent.FailureCode,
                failureDescription
                ?? (intent.BoundaryNodeId is null ? "Automatic activity item result was refused." : "Automatic activity item failed."), ct,
                intent.BoundaryNodeId is null ? "multiInstanceInterrupt" : "boundaryCaught"))
            throw new WorkflowConflictException("The automatic failure lost its item fence.");
        await repository.CancelUnfinishedItemsAsync(body.Id, activationId, "multiInstanceInterrupt", ct);
        await jobs.CancelOtherJobsByTokenIdsAsync(body.InstanceId, [body.TokenId], fence.JobId,
            "multiInstanceInterrupt", ct);
        await lifecycle.AbandonItemCallsAsync(body, ct);
        await RecordBodyHistoryAsync(body, BodyActor(body, actor), InstanceHistoryNotes.AutomaticMultiInstanceFailed, ct,
            intent.FailureCode);
        body = await RequireBodyAsync(body.Id, activationId, ct);
        await SignalAsync(body, controlTemplate, WorkflowJobKinds.MultiInstanceComplete, null, ct);
        if (fence.CallInvocationId is null) await CompleteDispatchAsync(fence.Job, ct);
        return true;
    }

    /// <summary>
    /// Refuses an item result that retrying cannot change, such as one that would take
    /// the body past its result limit (<see cref="AutomaticMultiInstanceValues.ResultTooLarge"/>
    /// from <see cref="PublishCandidateAsync"/>). The body stops at once, as for a caught
    /// failure, but no route is chosen here: completion publishes nothing, then takes the
    /// body's catch-all error boundary or faults the instance with the code. Call it in a
    /// fresh transaction after the refused publication rolled back.
    /// </summary>
    public Task<bool> RefuseItemResultAsync(long executionId, long itemId, Guid activationId,
        AutomaticMultiInstanceItemFence fence, string failureCode, ActorContext actor,
        WorkflowJobCreateRecord controlTemplate, IAutomaticMultiInstanceLifecycle lifecycle, CancellationToken ct) =>
        CaptureItemFailureAsync(executionId, itemId, activationId, fence,
            new AutomaticMultiInstanceCompletionIntent(null, failureCode, itemId), actor, controlTemplate, lifecycle, ct);

    /// <summary>
    /// Stops the body for a call item whose child faulted or was cancelled on its own, when
    /// no error boundary catches it. Completion publishes nothing and faults the instance
    /// with <paramref name="failureCode"/>, the parent's normal terminal path.
    /// </summary>
    public Task<bool> FailCallItemAsync(long executionId, long itemId, Guid activationId,
        AutomaticMultiInstanceItemFence fence, string failureCode, string description, string? childFaultCode,
        ActorContext actor, WorkflowJobCreateRecord controlTemplate, IAutomaticMultiInstanceLifecycle lifecycle,
        CancellationToken ct) =>
        CaptureItemFailureAsync(executionId, itemId, activationId, fence,
            new AutomaticMultiInstanceCompletionIntent(null, failureCode, itemId), actor, controlTemplate, lifecycle, ct,
            description, childFaultCode);

    /// <summary>
    /// Publishes saved results and advances the graph in one caller transaction.
    /// Version conflicts retain the body/items and are publication-only incidents.
    /// Graph continuation failures roll back this phase without invoking items.
    /// </summary>
    public async Task<AutomaticMultiInstanceCompletion?> CompleteBodyAsync(long executionId, Guid activationId,
        WorkflowJobFence fence, WorkflowModel definition, ActorContext actor,
        IAutomaticMultiInstanceLifecycle lifecycle, CancellationToken ct)
    {
        RequireLifecycleStores();
        var body = await RequireBodyAsync(executionId, activationId, ct);
        var job = await jobs.GetForUpdateAsync(fence.JobId, ct);
        if (job is null || job.Kind != WorkflowJobKinds.MultiInstanceComplete
            || job.MultiInstanceExecutionId != body.Id || job.MultiInstanceControlGeneration != body.ControlGeneration
            || job.InstanceId != body.InstanceId || job.TokenId != body.TokenId
            || job.WorkflowDefinitionId != body.WorkflowDefinitionId || job.ActivationId != body.ActivationId
            || !MatchesLease(job, fence) || !await jobs.IsLeaseAliveAsync(fence, ct)
            || !await BodyStillOwnsTokenAsync(body, ct))
            throw new WorkflowConflictException("The automatic completion fence is stale.");
        var intent = body.CompletionIntent?.Deserialize<AutomaticMultiInstanceCompletionIntent>();
        if (intent is null && body.CompletedCount != body.TotalCount)
            throw new WorkflowConflictException("Automatic items have not all completed.");
        actor = BodyActor(body, actor);
        string? failure = null;
        var published = false;
        // A refused result has no selected route and publishes nothing, like an
        // aggregate that cannot be accepted.
        var unrouted = intent is { BoundaryNodeId: null };
        if (unrouted)
        {
            failure = intent!.FailureCode;
        }
        else try
        {
            var aggregate = await BuildAggregateAsync(body.Id, activationId, intent is not null, ct);
            await WriteAggregateAsync(body, definition, actor, aggregate, checkVersion: true,
                refreshVersion: job.PublicationFenceRefreshRequested, ct);
            published = true;
        }
        catch (AutomaticMultiInstanceValueException error)
        {
            // The frozen assembled value cannot improve on retry. The engine
            // handles the existing/selected error route or faults the instance.
            failure = error.Code;
        }
        if (published) await lifecycle.DrainVariableNotificationsAsync(body, ct);
        if (!await BodyStillOwnsTokenAsync(body, ct))
        {
            // An accepted instance write may itself interrupt the body. Its
            // boundary owns continuation; keep that write and suppress this route.
            await CompleteDispatchAsync(fence, ct);
            return null;
        }
        await lifecycle.CloseHostSubscriptionsAsync(body, ct);
        if (job.PublicationFenceRefreshRequested && !await repository.AcknowledgePublicationFenceRefreshAsync(
                body.Id, job.Id, activationId, fence.LeaseToken, fence.LeaseGeneration, ct))
            throw new WorkflowConflictException("The aggregate publication recovery fence is stale.");
        var completion = new AutomaticMultiInstanceCompletion(unrouted ? null : intent, published, failure);
        if (!await repository.SettleAsync(body.Id, activationId,
                intent is null && failure is null ? "completed" : "interrupted",
                intent is null && failure is null ? "multiInstanceCompleted" : "multiInstanceInterrupt", ct)
            || !await repository.ClearBodyWaitAsync(body.Id, activationId, ct))
            throw new WorkflowConflictException("The automatic body lost completion ownership.");
        await RecordBodyHistoryAsync(body, actor, InstanceHistoryNotes.AutomaticMultiInstanceCompleted, ct, failure);
        await lifecycle.ContinueAsync(body, completion, ct);
        await CompleteDispatchAsync(fence, ct);
        return completion;
    }

    private async Task WriteAggregateAsync(AutomaticMultiInstanceRecord body, WorkflowModel definition,
        ActorContext actor, JsonElement aggregate, bool checkVersion, bool refreshVersion, CancellationToken ct)
    {
        var target = definition.Variables.SingleOrDefault(variable =>
            string.Equals(variable.Name, body.ResultVariable, StringComparison.OrdinalIgnoreCase))
            ?? throw new WorkflowConflictException("The automatic aggregate target is unavailable.");
        var targetScope = target.Scope == VariableScopes.Shared ? VariableScopes.Shared : VariableScopes.Instance;
        if (targetScope != body.AggregateScope || target.SharedKey != body.AggregateSharedKey)
            throw new WorkflowConflictException("The automatic aggregate binding changed.");
        var instanceValues = (await runtime!.LoadLatestVariableVersionsAsync(body.InstanceId, ct))
            .ToDictionary(value => value.Name, value => value.Value, StringComparer.OrdinalIgnoreCase);
        var plan = accessPlans.GetOrAdd(body.WorkflowDefinitionId, definition);
        var phase = checkVersion ? AutomaticMultiInstanceAccessPhases.Complete : AutomaticMultiInstanceAccessPhases.Initialize;
        var access = plan.SelectAutomaticPhase(body.NodeId, phase);
        var sourceAlias = definition.FlowNodes.Single(node => node.Id == body.NodeId).MultiInstance!.CollectionVariable!;
        if (checkVersion)
        {
            var snapshot = await repository.GetInputSnapshotAsync(body.Id, ct)
                ?? throw new WorkflowConflictException("The automatic body input snapshot is unavailable.");
            instanceValues[sourceAlias] = snapshot.GetProperty("collection").Clone();
            AddFrozenCollectionAliases(instanceValues, snapshot, access.AutomaticPhase!);
        }
        var context = await variables.MergeEffectiveValuesAsync(definition, instanceValues,
            access, ct);
        if (target.Scope == VariableScopes.Shared)
        {
            var live = (await variables.DescribeBindingsAsync(definition, ct))
                .SingleOrDefault(binding => string.Equals(binding.Alias, target.Name, StringComparison.OrdinalIgnoreCase));
            if (live is null || live.Status != "active" || live.Access != SharedVariableAccessModes.ReadWrite
                || live.DataType != target.DataType || live.IsArray != target.IsArray || live.Nullable != target.Nullable)
                throw new WorkflowConflictException("The automatic aggregate catalog contract is unavailable or changed.");
            try
            {
                SharedVariableValueValidator.Validate(live.Key, live.DataType, live.IsArray, live.Nullable, live.Validation, aggregate);
            }
            catch (WorkflowDomainException)
            {
                throw new AutomaticMultiInstanceValueException(AutomaticMultiInstanceValues.ResultInvalid);
            }
        }
        WorkflowEngineService.ValidateAutomaticAggregate(target, aggregate, context);
        if (checkVersion && !refreshVersion && await AggregateVersionAsync(body, definition, ct) != body.AggregateValueVersion)
            throw new AutomaticMultiInstancePublicationConflictException();
        await variables.WriteAsync(definition, body.WorkflowDefinitionId, body.InstanceId,
            [new WorkflowVariableWrite(target.Name, aggregate, SetBy: actor.User)], actor, ct);
        await unitOfWork!.SaveChangesAsync(ct);
        if (!await repository.SetAggregateVersionAsync(body.Id, body.ActivationId,
                await AggregateVersionAsync(body, definition, ct), ct))
            throw new WorkflowConflictException("The automatic aggregate lost body ownership.");
    }

    private async Task<long> AggregateVersionAsync(AutomaticMultiInstanceRecord body, WorkflowModel definition, CancellationToken ct)
    {
        if (body.AggregateScope == VariableScopes.Shared)
        {
            var versions = await variables.LoadSharedValueVersionsAsync(definition, [body.ResultVariable], true, ct);
            return versions.TryGetValue(body.ResultVariable, out var version) ? version
                : throw new WorkflowConflictException("The automatic aggregate shared value is unavailable.");
        }
        return (await runtime!.LoadLatestVariableVersionsAsync(body.InstanceId, ct))
            .SingleOrDefault(value => string.Equals(value.Name, body.ResultVariable, StringComparison.OrdinalIgnoreCase))?.Version ?? 0;
    }

    private async Task<bool> BodyStillOwnsTokenAsync(AutomaticMultiInstanceRecord body, CancellationToken ct)
    {
        var current = await repository.GetAsync(body.Id, true, ct);
        var token = await runtime!.GetExecutionTokenAsync(body.TokenId, false, ct);
        return current?.Status == MultiInstanceRecordStatuses.Active && current.ActivationId == body.ActivationId
            && token?.Status == ExecutionTokenRecordStatuses.Active && token.ActivationId == body.ActivationId
            && token.WaitingMultiInstanceExecutionId == body.Id;
    }

    private Task RecordBodyHistoryAsync(AutomaticMultiInstanceRecord body, ActorContext actor,
        string note, CancellationToken ct, string? failure = null) => runtime!.AddTokenHistoryAsync(
        body.InstanceId, body.TokenId, null, body.NodeId, body.NodeId, actor.User,
        new Dictionary<string, JsonElement>
        {
            ["executionId"] = JsonSerializer.SerializeToElement(body.Id),
            ["totalCount"] = JsonSerializer.SerializeToElement(body.TotalCount),
            ["purpose"] = JsonSerializer.SerializeToElement(note == InstanceHistoryNotes.AutomaticMultiInstanceCompleted
                ? "settlement" : note == InstanceHistoryNotes.AutomaticMultiInstanceStarted ? "initialization" : "failure"),
            ["failureCode"] = JsonSerializer.SerializeToElement(failure)
        }, note, ct, actor.ActingFor, actor.DelegationId, actorClaims: actor.AuditClaims);

    private static ActorContext BodyActor(AutomaticMultiInstanceRecord body, ActorContext actor) =>
        body.ActivityKind == BpmnFlowNodeTypes.CallActivity ? new ActorContext("callActivity", [], new Dictionary<string, string>()) : actor;

    private static bool MatchesLease(WorkflowJobRecord job, WorkflowJobFence fence) =>
        job.WorkerId == fence.WorkerId && job.LeaseToken == fence.LeaseToken && job.LeaseGeneration == fence.LeaseGeneration;

    private void RequireLifecycleStores()
    {
        if (runtime is null || unitOfWork is null)
            throw new InvalidOperationException("Automatic body lifecycle requires runtime persistence and an ambient unit of work.");
    }
}

public sealed record AutomaticMultiInstanceInputCapture(JsonElement Snapshot, int TotalCount);
