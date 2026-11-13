using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.Frozen;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>
/// Shared item-state and publication operations. The engine owns the ambient
/// transaction and instance/gateway/token locks. Adapters supply staged inputs
/// and candidate writes; they never settle an item or advance its parent.
/// Saving a candidate is a separate committed phase from publishing it.
/// </summary>
public sealed partial class AutomaticMultiInstanceCoordinator(
    IAutomaticMultiInstanceRepository repository,
    IWorkflowJobRepository jobs,
    IWorkflowVariableStore variables,
    ISharedVariableAccessPlanCache accessPlans,
    IWorkflowRuntimeRepository? runtime = null,
    IUnitOfWork? unitOfWork = null,
    IEngineSettingsRepository? engineSettings = null,
    ICallActivityRepository? callActivities = null)
{
    public static TimeSpan DispatchBackoff(long leaseGeneration) => TimeSpan.FromSeconds(
        Math.Min(60, 5 * (1L << (int)(Math.Clamp(leaseGeneration, 1, 5) - 1))));

    /// <summary>
    /// A second activation of one node waits on its original entry job. Its
    /// conditional subscriptions survive, and it consumes no activity attempt.
    /// The caller has already fenced the owning token under runtime locks.
    /// </summary>
    public async Task<TimeSpan> WaitForEntrySlotAsync(WorkflowJobFence fence, CancellationToken cancellationToken)
    {
        var entry = await jobs.GetForUpdateAsync(fence.JobId, cancellationToken);
        if (entry is null || entry.Kind is not (WorkflowJobKinds.AsyncBefore or WorkflowJobKinds.MultiInstanceStart)
            || entry.MultiInstanceExecutionId is not null || entry.AutomaticMultiInstanceItemId is not null
            || entry.WorkerId != fence.WorkerId || entry.LeaseToken != fence.LeaseToken
            || entry.LeaseGeneration != fence.LeaseGeneration || !await jobs.IsLeaseAliveAsync(fence, cancellationToken))
            throw new WorkflowConflictException("The automatic body entry lease is stale.");
        var delay = DispatchBackoff(fence.LeaseGeneration);
        if (entry.Status != WorkflowJobStatuses.ResultReady && !await jobs.SaveResultReadyAsync(fence,
                new WorkflowJobResultRecord(JsonSerializer.SerializeToElement(new { kind = entry.Kind, waitingFor = "openBody" }),
                    null, null, null), cancellationToken))
            throw new WorkflowConflictException("The automatic entry wait lost its lease.");
        if (!await jobs.ReleaseResultReadyLeaseAsync(fence, delay, cancellationToken))
            throw new WorkflowConflictException("The automatic entry wait lost its lease.");
        return delay;
    }

    /// <summary>
    /// Drives one reserved dispatch wake. The caller holds the owning runtime locks
    /// and transaction. Capacity waits retain this job in resultReady, so lease
    /// recovery is restart-safe and never consumes another activity attempt.
    /// </summary>
    public async Task<AutomaticMultiInstanceDispatchResult> DispatchAsync(long executionId, Guid activationId,
        WorkflowJobFence fence, WorkflowJobCreateRecord itemTemplate, long openJobLimit,
        CancellationToken cancellationToken)
    {
        var body = await RequireBodyAsync(executionId, activationId, cancellationToken);
        RequireTemplate(body, itemTemplate);
        var job = await jobs.GetForUpdateAsync(fence.JobId, cancellationToken);
        if (job is null || job.Kind != WorkflowJobKinds.MultiInstanceDispatch
            || job.InstanceId != body.InstanceId || job.TokenId != body.TokenId
            || job.WorkflowDefinitionId != body.WorkflowDefinitionId || job.NodeId != body.NodeId
            || job.ActivationId != activationId || job.MultiInstanceExecutionId != body.Id
            || job.MultiInstanceControlGeneration != body.ControlGeneration
            || job.AutomaticMultiInstanceItemId is not null || job.WorkerId != fence.WorkerId
            || job.LeaseToken != fence.LeaseToken || job.LeaseGeneration != fence.LeaseGeneration
            || !await jobs.IsLeaseAliveAsync(fence, cancellationToken))
            throw new WorkflowConflictException("The automatic dispatch generation or lease is stale.");
        if (body.ActivityKind == BpmnFlowNodeTypes.CallActivity)
            throw new InvalidOperationException("Call dispatch requires invocation admission.");
        var progress = (await repository.GetProgressAsync(body.InstanceId, cancellationToken))
            .Single(item => item.Execution.Id == body.Id);
        if (body.CompletedCount == body.TotalCount)
        {
            await CompleteDispatchAsync(fence, cancellationToken);
            await SignalAsync(body, itemTemplate, WorkflowJobKinds.MultiInstanceComplete, null, cancellationToken);
            return new("completing", 0, null);
        }
        if (progress.Execution.FailedCount > 0 || body.AdmissionBlockedReason == "itemIncident")
        {
            await CompleteDispatchAsync(fence, cancellationToken);
            return new("itemIncident", 0, null);
        }
        if (progress.PendingCount == 0 || progress.ActiveCount >= body.EffectiveConcurrency)
        {
            await CompleteDispatchAsync(fence, cancellationToken);
            return new("waitingForItems", 0, null);
        }
        var open = await jobs.CountOpenByInstanceAsync(body.InstanceId, cancellationToken);
        if (open >= openJobLimit)
        {
            if (!await repository.SetAdmissionBlockedReasonAsync(body.Id, activationId, "queueCapacity", cancellationToken))
                throw new WorkflowConflictException("The automatic body no longer owns its dispatch reservation.");
            var delay = DispatchBackoff(fence.LeaseGeneration);
            if (delay == TimeSpan.FromSeconds(60) && progress.ActiveCount == 0)
            {
                if (await jobs.OpenIncidentAsync(fence, "job_capacity_exceeded",
                    "Automatic multi-instance admission is blocked with no item in flight.",
                    "Increase available instance job capacity or cancel competing work, then retry admission.", cancellationToken) is null)
                    throw new WorkflowConflictException("The automatic dispatch lease was lost before its capacity incident.");
                return new("capacityIncident", 0, null);
            }
            if (job.Status != WorkflowJobStatuses.ResultReady
                && !await jobs.SaveResultReadyAsync(fence,
                    new WorkflowJobResultRecord(JsonSerializer.SerializeToElement(new { kind = WorkflowJobKinds.MultiInstanceDispatch }), null, null, null),
                    cancellationToken))
                throw new WorkflowConflictException("The automatic dispatch lease was lost before its wait was saved.");
            if (!await jobs.ReleaseResultReadyLeaseAsync(fence, delay, cancellationToken))
                throw new WorkflowConflictException("The automatic dispatch lease was lost before its durable wake was saved.");
            return new("queueCapacity", 0, delay);
        }
        // The reservation already occupies this job's slot. Completing it grants
        // no extra capacity credit. Refill and any replacement wake stay atomic.
        await CompleteDispatchAsync(fence, cancellationToken);
        if (!await repository.SetAdmissionBlockedReasonAsync(body.Id, activationId, null, cancellationToken))
            throw new WorkflowConflictException("The automatic body no longer owns its admission window.");
        var admitted = await AdmitJobsAsync(body.Id, activationId, itemTemplate, openJobLimit, 0, cancellationToken);
        return new("admitted", admitted.Count, null);
    }

    private async Task CompleteDispatchAsync(WorkflowJobFence fence, CancellationToken cancellationToken)
    {
        if (!await jobs.CompleteAsync(fence, cancellationToken))
            throw new WorkflowConflictException("The automatic dispatch lease was lost before completion.");
    }

    public async Task<AutomaticMultiInstanceRecord> InitializeAsync(
        AutomaticMultiInstanceCreateRecord create, CancellationToken cancellationToken)
    {
        var snapshot = create.InputSnapshot;
        AutomaticMultiInstanceValues.RequireSize(snapshot, AutomaticMultiInstanceRules.MaxSnapshotBytes,
            AutomaticMultiInstanceValues.SourceInvalid);
        if (!snapshot.TryGetProperty("collection", out var collection)
            || collection.ValueKind != JsonValueKind.Array || collection.GetArrayLength() != create.TotalCount)
            throw new AutomaticMultiInstanceValueException(AutomaticMultiInstanceValues.SourceInvalid);
        var items = collection.EnumerateArray().ToArray();
        var minimum = AutomaticMultiInstanceValues.BuildAggregate(items,
            Enumerable.Range(0, items.Length).Select(index => new AutomaticMultiInstanceResultRow(
                index, "completed", JsonSerializer.SerializeToElement(new { }))).ToArray());
        var open = await repository.FindOpenAsync(create.InstanceId, create.NodeId, cancellationToken);
        if (open is not null)
        {
            if (open.TokenId == create.TokenId && open.ActivationId == create.ActivationId) return open;
            throw new WorkflowConflictException("Another automatic body owns this node's initialization slot.");
        }
        return await repository.CreateAsync(create with
        {
            ResultBytes = AutomaticMultiInstanceValues.RequireSize(minimum,
                AutomaticMultiInstanceRules.MaxResultBytes, AutomaticMultiInstanceValues.ResultTooLarge)
        }, cancellationToken);
    }

    /// <summary>Only REST/script item jobs use this path; call admission owns invocation reservations.</summary>
    public async Task<IReadOnlyList<AutomaticMultiInstanceItemRecord>> AdmitJobsAsync(
        long executionId, Guid activationId, WorkflowJobCreateRecord template,
        long openJobLimit, int completingJobCredits, CancellationToken cancellationToken)
    {
        var body = await RequireBodyAsync(executionId, activationId, cancellationToken);
        if (body.ActivityKind == BpmnFlowNodeTypes.CallActivity)
            throw new InvalidOperationException("Call items require invocation admission.");
        RequireTemplate(body, template);
        if (body.AdmissionBlockedReason == "itemIncident" || body.FailedCount > 0 || body.Phase != "running") return [];
        var progress = (await repository.GetProgressAsync(body.InstanceId, cancellationToken))
            .Single(item => item.Execution.Id == body.Id);
        // Progress counts an item whose job has an open incident as blocked: admission
        // pauses until no item-level incident remains, while admitted work settles.
        if (progress.PendingCount == 0 || progress.Execution.FailedCount > 0) return [];
        var open = await jobs.CountOpenByInstanceAsync(body.InstanceId, cancellationToken);
        var available = Math.Max(0, openJobLimit - Math.Max(0, open - completingJobCredits));
        var count = (int)Math.Min(available, Math.Min(progress.PendingCount,
            Math.Max(0, body.EffectiveConcurrency - progress.ActiveCount - body.FailedCount)));
        if (count == 0)
        {
            if (progress.ActiveCount < body.EffectiveConcurrency)
                await SignalAsync(body, template, WorkflowJobKinds.MultiInstanceDispatch, "queueCapacity", cancellationToken);
            return [];
        }
        var admitted = await repository.AdmitAsync(body.Id, body.ActivationId, count, cancellationToken);
        await jobs.EnqueueManyAsync(admitted.Select(item => template with
        {
            Kind = WorkflowJobKinds.MultiInstanceItem, Phase = WorkflowJobKinds.MultiInstanceItem,
            QueueClass = WorkflowJobClasses.Activity, MultiInstanceExecutionId = body.Id,
            AutomaticMultiInstanceItemId = item.Id, MultiInstanceControlGeneration = null
        }).ToArray(), cancellationToken);
        if (admitted.Count < progress.PendingCount && count < body.EffectiveConcurrency - progress.ActiveCount)
            await SignalAsync(body, template, WorkflowJobKinds.MultiInstanceDispatch, "queueCapacity", cancellationToken);
        return admitted;
    }

    public async Task<bool> SaveStageAsync(long executionId, long itemId, Guid activationId,
        AutomaticMultiInstanceItemFence fence, JsonElement stagedInput, CancellationToken cancellationToken)
    {
        var (body, item, _) = await RequireItemFenceAsync(executionId, itemId, activationId, fence, cancellationToken);
        if (item.StagedInput is not null) return false;
        AutomaticMultiInstanceValues.RequireSize(stagedInput, AutomaticMultiInstanceRules.MaxSnapshotBytes,
            AutomaticMultiInstanceValues.SourceInvalid);
        return await repository.SaveStageAsync(body.Id, item.Id, body.ActivationId, stagedInput, cancellationToken);
    }

    /// <summary>
    /// Captures and persists fresh shared/config/time values once per item. A
    /// retry reconstructs its context from that saved stage and the retained body
    /// snapshot; it never rereads shared inputs or copies the whole collection to
    /// every item row. Adapters invoke only after the caller commits this stage.
    /// </summary>
    public async Task<AutomaticMultiInstanceItemContext> CaptureItemContextAsync(
        long executionId, long itemId, Guid activationId, AutomaticMultiInstanceItemFence fence, WorkflowModel definition,
        IReadOnlyDictionary<string, JsonElement> environmentValues, DateTimeOffset stagedAt,
        CancellationToken cancellationToken)
    {
        var (body, item, job) = await RequireItemFenceAsync(executionId, itemId, activationId, fence, cancellationToken);
        var node = definition.FlowNodes.Single(node => node.Id == body.NodeId);
        if (!AutomaticMultiInstanceRules.IsAutomaticBody(node) || node.Type != body.ActivityKind)
            throw new WorkflowConflictException("The automatic item definition does not own this body.");
        var snapshot = await repository.GetInputSnapshotAsync(body.Id, cancellationToken)
            ?? throw new WorkflowConflictException("The automatic body input snapshot is unavailable.");
        var scope = accessPlans.GetOrAdd(body.WorkflowDefinitionId, definition)
            .SelectAutomaticPhase(node.Id, AutomaticMultiInstanceAccessPhases.StageItem);
        AutomaticMultiInstanceStagedContext stage;
        if (item.StagedInput is { } saved)
        {
            if (!saved.TryGetProperty("kind", out var kind) || kind.GetString() != "automaticItemContext")
                throw new WorkflowConflictException("The automatic item has an incompatible saved input contract.");
            stage = saved.GetProperty("context").Deserialize<AutomaticMultiInstanceStagedContext>()
                ?? throw new WorkflowConflictException("The automatic item saved context is unavailable.");
        }
        else
        {
            if (environmentValues.Keys.Any(name => !name.StartsWith("config.", StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith("setting.", StringComparison.OrdinalIgnoreCase)
                    && !name.StartsWith("sys.", StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("Only config, setting, and system context may be supplied while staging an item.", nameof(environmentValues));
            var shared = await variables.MergeEffectiveValuesAsync(definition,
                new Dictionary<string, JsonElement>(), scope, cancellationToken);
            var versions = await variables.LoadSharedValueVersionsAsync(definition,
                scope.AutomaticPhase!.ProducerAliases.ToArray(), true, cancellationToken);
            if (scope.AutomaticPhase.ProducerAliases.Any(alias => !versions.ContainsKey(alias)))
                throw new WorkflowConflictException("An automatic item shared output fence is unavailable.");
            var environment = environmentValues.Where(pair => !pair.Key.Equals("sys.jobId", StringComparison.OrdinalIgnoreCase)
                    && !pair.Key.Equals("sys.jobAttempt", StringComparison.OrdinalIgnoreCase)
                    && (body.ActivityKind != BpmnFlowNodeTypes.CallActivity
                        || !pair.Key.StartsWith("sys.claim.", StringComparison.OrdinalIgnoreCase)
                            && !pair.Key.Equals("sys.user", StringComparison.OrdinalIgnoreCase)
                            && !pair.Key.Equals("sys.roles", StringComparison.OrdinalIgnoreCase)))
                .ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase);
            stage = new(shared, environment,
                versions, stagedAt);
            var persisted = JsonSerializer.SerializeToElement(new { kind = "automaticItemContext", context = stage });
            AutomaticMultiInstanceValues.RequireSize(persisted, AutomaticMultiInstanceRules.MaxSnapshotBytes,
                AutomaticMultiInstanceValues.SourceInvalid);
            if (!await repository.SaveStageAsync(body.Id, item.Id, activationId, persisted, cancellationToken))
                throw new WorkflowConflictException("The automatic item lost staging ownership.");
        }
        var values = AutomaticMultiInstanceValues.CreateItemContext(snapshot, item.ItemIndex,
            stage.SharedValues.Concat(stage.EnvironmentValues).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase),
            node.MultiInstance!.CollectionVariable!);
        AddFrozenCollectionAliases(values, snapshot, scope.AutomaticPhase!);
        if (body.ActivityKind == BpmnFlowNodeTypes.CallActivity)
        {
            foreach (var key in values.Keys.Where(key => key.StartsWith("sys.claim.", StringComparison.OrdinalIgnoreCase)
                || key.Equals("sys.jobId", StringComparison.OrdinalIgnoreCase)
                || key.Equals("sys.jobAttempt", StringComparison.OrdinalIgnoreCase)).ToArray()) values.Remove(key);
            values["sys.user"] = JsonSerializer.SerializeToElement("callActivity");
            values["sys.roles"] = JsonSerializer.SerializeToElement(Array.Empty<string>());
        }
        else
        {
            values["sys.jobId"] = JsonSerializer.SerializeToElement(job.Id);
            values["sys.jobAttempt"] = JsonSerializer.SerializeToElement(job.AttemptCount);
        }
        // Defaults belong to this execution attempt, while inputs remain frozen.
        var locals = WorkflowEngineService.CreateAutomaticLocalBuffer(node.MultiInstance.OutputVariables!, values);
        foreach (var local in locals) values[local.Key] = local.Value.Clone();
        return new(values.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase),
            locals.ToFrozenDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase),
            stage.SharedOutputValueVersions.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase), stage.StagedAt)
        {
            FlowInfo = snapshot.TryGetProperty("flowInfo", out var flowInfo) && flowInfo.ValueKind != JsonValueKind.Null
                ? new SequenceFlowInfoSnapshot(flowInfo.GetProperty("KnownFlowIds").Deserialize<int[]>()!,
                    flowInfo.GetProperty("Summaries").Deserialize<Dictionary<int, SequenceFlowRuntimeSummary>>()!.Values) : null
        };
    }

    private static void AddFrozenCollectionAliases(IDictionary<string, JsonElement> context, JsonElement snapshot,
        SharedVariableAutomaticPhaseAccessPlan phase)
    {
        foreach (var alias in phase.FrozenReadAliases!) context[alias] = snapshot.GetProperty("collection").Clone();
    }

    /// <summary>Commit this call before attempting shared publication. Replays cannot replace an accepted candidate.</summary>
    public async Task<bool> SaveCandidateAsync(long executionId, long itemId, Guid activationId,
        AutomaticMultiInstanceItemFence fence, WorkflowModel definition, AutomaticMultiInstanceCandidate candidate,
        CancellationToken cancellationToken)
    {
        var (body, item, _) = await RequireItemFenceAsync(executionId, itemId, activationId, fence, cancellationToken, requireDelivery: true);
        if (item.Candidate is not null) return false;
        if (item.StagedInput is null) throw new WorkflowConflictException("The automatic item has no staged input.");
        var node = definition.FlowNodes.Single(node => node.Id == body.NodeId);
        ValidateSharedTargets(definition, node, candidate);
        var validationContext = candidate.ValidationContext.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        foreach (var stagedOutput in candidate.LocalVariables.Concat(candidate.SharedWrites)) validationContext[stagedOutput.Key] = stagedOutput.Value;
        var locals = WorkflowEngineService.ValidateAutomaticLocalResult(node.MultiInstance!.OutputVariables!,
            candidate.LocalVariables, validationContext);
        var value = JsonSerializer.SerializeToElement(candidate with
        {
            LocalVariables = locals.Deserialize<Dictionary<string, JsonElement>>()!
        });
        // The item row's check constraint bounds both columns together.
        AutomaticMultiInstanceValues.RequireSize(JsonbText.ByteCount(item.StagedInput.Value) + JsonbText.ByteCount(value),
            AutomaticMultiInstanceRules.MaxSnapshotBytes, AutomaticMultiInstanceValues.ResultTooLarge);
        return await repository.SaveCandidateAsync(body.Id, item.Id, body.ActivationId, value, cancellationToken);
    }

    /// <summary>
    /// Atomically publishes the saved shared write batch, accepts locals, and queues
    /// a reserved control job. Capacity cannot roll back a successful item into replay.
    /// </summary>
    public async Task<bool> PublishCandidateAsync(long executionId, long itemId, Guid activationId,
        AutomaticMultiInstanceItemFence fence, WorkflowModel definition, ActorContext causalActor,
        WorkflowJobCreateRecord controlTemplate, CancellationToken cancellationToken)
    {
        var (body, item, job) = await RequireItemFenceAsync(executionId, itemId, activationId, fence, cancellationToken, requireDelivery: true);
        causalActor = BodyActor(body, causalActor);
        RequireTemplate(body, controlTemplate);
        var candidate = item.Candidate?.Deserialize<AutomaticMultiInstanceCandidate>()
            ?? throw new WorkflowConflictException("The automatic item has no saved candidate.");
        var node = definition.FlowNodes.Single(node => node.Id == body.NodeId);
        ValidateSharedTargets(definition, node, candidate);
        var plan = accessPlans.GetOrAdd(body.WorkflowDefinitionId, definition);
        // Replace a dispatch wake before shared locks. The whole replacement rolls
        // back on conflict, and the accepted result always installs its next wake.
        await repository.SupersedeControlJobsAsync(body.Id, body.ActivationId, null, cancellationToken);
        // Publication never reacquires the source, aggregate, or later admission
        // dependencies. Each durable phase has its own ordinal lock set.
        var scope = plan.SelectAutomaticPhase(node.Id, AutomaticMultiInstanceAccessPhases.PublishItem);
        var savedContext = candidate.ValidationContext.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
        if (scope.AutomaticPhase!.FrozenReadAliases?.Count > 0)
        {
            var snapshot = await repository.GetInputSnapshotAsync(body.Id, cancellationToken)
                ?? throw new WorkflowConflictException("The automatic body input snapshot is unavailable.");
            AddFrozenCollectionAliases(savedContext, snapshot, scope.AutomaticPhase);
        }
        var publicationContext = await variables.MergeEffectiveValuesAsync(definition,
            savedContext, scope, cancellationToken);
        foreach (var value in candidate.LocalVariables.Concat(candidate.SharedWrites))
            publicationContext[value.Key] = value.Value;
        _ = WorkflowEngineService.ValidateAutomaticLocalResult(node.MultiInstance!.OutputVariables!,
            candidate.LocalVariables, publicationContext);
        var producedNames = (candidate.MappedTargets ?? candidate.LocalVariables.Keys.Concat(candidate.SharedWrites.Keys))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in node.Service?.OutputMappings ?? [])
        {
            // Only a value a mapping produced is checked again. A shared target the
            // response did not fill holds another writer's current value, and a local
            // one keeps its output default; this mapping accepted neither.
            if (string.IsNullOrWhiteSpace(mapping.Validation) || !producedNames.Contains(mapping.Variable)
                || !publicationContext.TryGetValue(mapping.Variable, out var value))
                continue;
            var target = definition.Variables.SingleOrDefault(variable => variable.Name.Equals(mapping.Variable, StringComparison.OrdinalIgnoreCase))
                ?? node.MultiInstance.OutputVariables!.Select(AutomaticMultiInstanceRules.ToVariable)
                    .Single(variable => variable.Name.Equals(mapping.Variable, StringComparison.OrdinalIgnoreCase));
            WorkflowEngineService.ValidateAutomaticAggregate(new VariableModel
            {
                Name = target.Name, DataType = target.DataType, IsArray = target.IsArray,
                Nullable = target.Nullable, Validation = mapping.Validation
            }, value, publicationContext);
        }
        var actual = await variables.LoadSharedValueVersionsAsync(definition,
            candidate.SharedWrites.Keys.ToArray(), true, cancellationToken);
        if (!job.PublicationFenceRefreshRequested && candidate.SharedOutputValueVersions.Any(expected =>
                !actual.TryGetValue(expected.Key, out var current) || current != expected.Value))
            throw new AutomaticMultiInstancePublicationConflictException();
        var locals = JsonSerializer.SerializeToElement(candidate.LocalVariables);
        // These locals replace the "{}" the minimum envelope counted for this row.
        var localBytes = JsonbText.ByteCount(locals) - 2;
        if (body.ResultBytes + (long)localBytes > AutomaticMultiInstanceRules.MaxResultBytes)
            throw new AutomaticMultiInstanceValueException(AutomaticMultiInstanceValues.ResultTooLarge);
        var results = await variables.WriteAsync(definition, body.WorkflowDefinitionId, body.InstanceId,
            candidate.SharedWrites.Select(pair => new WorkflowVariableWrite(pair.Key, pair.Value,
                SetBy: causalActor.User, NodeExecutionId: item.NodeExecutionId)).ToArray(), causalActor, cancellationToken);
        var changed = await repository.CompleteItemAsync(body.Id, item.Id, body.ActivationId, locals,
            JsonSerializer.SerializeToElement(results),
            (int)localBytes, cancellationToken);
        if (!changed) throw new WorkflowConflictException("The automatic item lost publication ownership.");
        if (job.PublicationFenceRefreshRequested
            && !await repository.AcknowledgePublicationFenceRefreshAsync(body.Id, job.Id, body.ActivationId,
                fence.LeaseToken, fence.LeaseGeneration, cancellationToken))
            throw new WorkflowConflictException("The automatic publication recovery fence is stale.");
        var currentBody = await RequireBodyAsync(body.Id, body.ActivationId, cancellationToken);
        await SignalAsync(currentBody, controlTemplate,
            currentBody.CompletedCount == currentBody.TotalCount ? WorkflowJobKinds.MultiInstanceComplete : WorkflowJobKinds.MultiInstanceDispatch,
            currentBody.FailedCount > 0 || currentBody.AdmissionBlockedReason == "itemIncident"
                ? "itemIncident" : null, cancellationToken);
        // Call adapters settle the invocation and acknowledge the child message
        // in this transaction. The common core never owns child messaging.
        if (fence.CallInvocationId is null && !await jobs.CompleteAsync(fence.Job, cancellationToken))
            throw new WorkflowConflictException("The automatic item lost its job lease during publication.");
        return true;
    }

    public async Task<JsonElement> BuildAggregateAsync(long executionId, Guid activationId,
        bool partial, CancellationToken cancellationToken)
    {
        var body = await RequireBodyAsync(executionId, activationId, cancellationToken);
        var snapshot = await repository.GetInputSnapshotAsync(body.Id, cancellationToken)
            ?? throw new WorkflowConflictException("The automatic body input snapshot is unavailable.");
        var rows = new List<AutomaticMultiInstanceResultRow>();
        for (var skip = 0; skip < body.TotalCount; skip += 200)
        {
            var page = await repository.GetItemResultsAsync(body.Id, skip, 200, cancellationToken);
            rows.AddRange(page.Select(item => new AutomaticMultiInstanceResultRow(item.ItemIndex,
                item.Status, item.Result ?? JsonSerializer.SerializeToElement(new { }))));
        }
        return AutomaticMultiInstanceValues.BuildAggregate(snapshot.GetProperty("collection").EnumerateArray().ToArray(), rows, partial);
    }

    private async Task SignalAsync(AutomaticMultiInstanceRecord body, WorkflowJobCreateRecord template,
        string kind, string? blockedReason, CancellationToken cancellationToken)
    {
        var open = await jobs.ListOpenByInstanceAsync(body.InstanceId, false, cancellationToken);
        if (open.Any(job => job.MultiInstanceExecutionId == body.Id && job.ActivationId == body.ActivationId
            && job.MultiInstanceControlGeneration == body.ControlGeneration && job.Kind == kind)) return;
        await repository.SupersedeControlJobsAsync(body.Id, body.ActivationId, null, cancellationToken);
        var phase = kind == WorkflowJobKinds.MultiInstanceComplete ? "completing" : "running";
        var generation = await repository.SetControlStateAsync(body.Id, body.ActivationId, phase, blockedReason, cancellationToken);
        await jobs.EnqueueAsync(template with
        {
            Kind = kind, Phase = kind, QueueClass = WorkflowJobClasses.Control,
            MultiInstanceExecutionId = body.Id, AutomaticMultiInstanceItemId = null,
            MultiInstanceControlGeneration = generation, MaxAttempts = 4,
            RetryDelays = [TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5)],
            FailureHandling = WorkflowJobFailureHandling.BoundaryFirst
        }, cancellationToken);
    }

    private async Task<AutomaticMultiInstanceRecord> RequireBodyAsync(long executionId, Guid activationId, CancellationToken cancellationToken)
    {
        var body = await repository.GetAsync(executionId, true, cancellationToken);
        if (body is null || body.ActivationId != activationId || body.Status != MultiInstanceRecordStatuses.Active)
            throw new WorkflowConflictException("The automatic body activation is no longer open.");
        return body;
    }

    private async Task<(AutomaticMultiInstanceRecord Body, AutomaticMultiInstanceItemRecord Item, WorkflowJobRecord Job)>
        RequireItemFenceAsync(long executionId, long itemId, Guid activationId, AutomaticMultiInstanceItemFence fence,
            CancellationToken cancellationToken, bool requireDelivery = false)
    {
        var body = await RequireBodyAsync(executionId, activationId, cancellationToken);
        // A saved stage is reusable only while the activation is still current.
        // Runtime repositories may hold tracked tokens from an earlier phase.
        if (!await repository.HasCurrentOwnerAsync(body.Id, activationId, cancellationToken))
            throw new WorkflowConflictException("The automatic item's owning activation is stale.");
        var item = await repository.GetItemAsync(body.Id, itemId, true, cancellationToken);
        var job = await jobs.GetForUpdateAsync(fence.JobId, cancellationToken);
        if (fence.CallInvocationId is { } invocationId)
        {
            await RequireCallItemFenceAsync(body, item, job, invocationId, fence.Job, requireDelivery, cancellationToken);
            return (body, item!, job!);
        }
        if (body.ActivityKind == BpmnFlowNodeTypes.CallActivity || item is null || item.Status != "active" || job is null
            || job.Kind != WorkflowJobKinds.MultiInstanceItem || job.InstanceId != body.InstanceId
            || job.TokenId != body.TokenId || job.WorkflowDefinitionId != body.WorkflowDefinitionId
            || job.NodeId != body.NodeId || job.NodeType != body.ActivityKind
            || job.ActivationId != activationId || job.MultiInstanceExecutionId != body.Id
            || job.AutomaticMultiInstanceItemId != item.Id || job.WorkerId != fence.WorkerId
            || job.LeaseToken != fence.LeaseToken || job.LeaseGeneration != fence.LeaseGeneration
            || !await jobs.IsLeaseAliveAsync(fence, cancellationToken))
            throw new WorkflowConflictException("The automatic item job fence is stale.");
        return (body, item, job);
    }

    private static void RequireTemplate(AutomaticMultiInstanceRecord body, WorkflowJobCreateRecord template)
    {
        if (template.InstanceId != body.InstanceId || template.TokenId != body.TokenId
            || template.WorkflowDefinitionId != body.WorkflowDefinitionId || template.ActivationId != body.ActivationId
            || template.NodeId != body.NodeId || template.NodeType != body.ActivityKind)
            throw new WorkflowConflictException("The automatic job template does not own this body.");
    }

    private static void ValidateSharedTargets(WorkflowModel definition, FlowNodeModel node, AutomaticMultiInstanceCandidate candidate)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var reserved = new[] { node.MultiInstance!.CollectionVariable, node.MultiInstance.ResultVariable }
            .Concat(definition.FlowNodes.Where(boundary => boundary.AttachedToRef == node.Id).Select(boundary => boundary.ErrorVariable))
            .Where(name => name is not null).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reservedKeys = definition.Variables.Where(variable => reserved.Contains(variable.Name)
                && variable.Scope == VariableScopes.Shared && variable.SharedKey is not null)
            .Select(variable => variable.SharedKey!).ToHashSet(StringComparer.Ordinal);
        var allowed = AutomaticMultiInstanceRules.ItemOutputTargets(node).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var dynamicScript = BpmnFlowNodeTypes.IsScriptTask(node.Type) && node.ScriptFormat == ScriptFormats.JavaScript;
        foreach (var pair in candidate.SharedWrites)
        {
            var binding = definition.Variables.SingleOrDefault(variable => string.Equals(variable.Name, pair.Key, StringComparison.OrdinalIgnoreCase));
            if (binding is null || binding.Scope != VariableScopes.Shared || binding.Access != SharedVariableAccessModes.ReadWrite
                || reserved.Contains(binding.Name) || reservedKeys.Contains(binding.SharedKey!)
                || !dynamicScript && !allowed.Contains(binding.Name)
                || !names.Add(binding.Name) || !keys.Add(binding.SharedKey!)
                || !candidate.SharedOutputValueVersions.ContainsKey(pair.Key))
                throw new AutomaticMultiInstanceValueException(AutomaticMultiInstanceValues.ResultInvalid);
        }
        if (candidate.SharedOutputValueVersions.Count != candidate.SharedWrites.Count)
            throw new AutomaticMultiInstanceValueException(AutomaticMultiInstanceValues.ResultInvalid);
    }
}

/// <param name="MappedTargets">
/// The targets a REST item's output mappings filled, from the response or a mapping
/// default: publication repeats only their mapping validations. Null for a script item
/// and for a candidate saved before it was recorded, which repeats every mapping whose
/// target holds a local value or a shared write.
/// </param>
public sealed record AutomaticMultiInstanceCandidate(
    IReadOnlyDictionary<string, JsonElement> LocalVariables,
    IReadOnlyDictionary<string, JsonElement> SharedWrites,
    IReadOnlyDictionary<string, long> SharedOutputValueVersions,
    IReadOnlyDictionary<string, JsonElement> ValidationContext,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    IReadOnlyCollection<string>? MappedTargets = null);

public sealed record AutomaticMultiInstanceDispatchResult(string State, int AdmittedCount, TimeSpan? RetryAfter);

public sealed record AutomaticMultiInstanceItemContext(
    IReadOnlyDictionary<string, JsonElement> Values,
    IReadOnlyDictionary<string, JsonElement> LocalVariables,
    IReadOnlyDictionary<string, long> SharedOutputValueVersions,
    DateTimeOffset StagedAt)
{
    public SequenceFlowInfoSnapshot? FlowInfo { get; init; }
}

internal sealed record AutomaticMultiInstanceStagedContext(
    IReadOnlyDictionary<string, JsonElement> SharedValues,
    IReadOnlyDictionary<string, JsonElement> EnvironmentValues,
    IReadOnlyDictionary<string, long> SharedOutputValueVersions,
    DateTimeOffset StagedAt);

public sealed class AutomaticMultiInstancePublicationConflictException()
    : InvalidOperationException("output_version_conflict");
