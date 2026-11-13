using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>
/// The item protocol shared by the REST service (TASK-05B) and script (TASK-05C)
/// adapters: body entry from the async-before job, the item-owner lock, publication
/// of a saved candidate, and the authored-failure and refusal exits. Each adapter
/// names the instance inputs its body freezes and produces an item's candidate;
/// nothing here evaluates or invokes an activity, writes runtime variables itself,
/// or advances a token outside the common coordinator.
/// </summary>
public sealed partial class WorkflowEngineService
{
    private const string AutomaticItemFailureIncident = "job_execution_failed";

    // Entry and item jobs of service and script bodies arrive here. Each installed
    // adapter adds one case; every other kind keeps failing closed exactly as before.
    private Task ProcessAutomaticMultiInstanceEntryJobAsync(WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence, FlowNodeModel node, CancellationToken cancellationToken)
    {
        EnsureAutomaticMultiInstanceRuntimeAvailable(node);
        if (BpmnFlowNodeTypes.IsScriptTask(node.Type))
            return ProcessAutomaticBodyEntryAsync(lease, fence, AutomaticScriptInstanceInputNames, cancellationToken);
        if (BpmnFlowNodeTypes.IsServiceTask(node.Type))
            return ProcessAutomaticBodyEntryAsync(lease, fence, AutomaticServiceInstanceInputNames, cancellationToken);
        throw new WorkflowJobInvariantException("The automatic activity entry adapter is not installed.");
    }

    private Task ProcessAutomaticMultiInstanceItemJobAsync(WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence, CancellationToken cancellationToken)
    {
        // A closed switch is a host setting, not a broken job: the item fails like any
        // refused work, keeps its slot, and resumes from its incident on an open host.
        if (automaticMultiInstanceOptions?.Enabled != true)
            throw new WorkflowDomainException(
                $"Automatic multi-instance node #{lease.Job.NodeId} cannot run on this host: automatic multi-instance runs only on a Development host with the release switch open.");
        if (!AutomaticMultiInstanceRules.IsAdapterInstalled(lease.Job.NodeType))
            throw new WorkflowJobInvariantException("The automatic activity item adapter is not installed.");
        if (BpmnFlowNodeTypes.IsScriptTask(lease.Job.NodeType))
            return ProcessAutomaticScriptItemAsync(lease, fence, cancellationToken);
        if (BpmnFlowNodeTypes.IsServiceTask(lease.Job.NodeType))
            return ProcessAutomaticServiceItemAsync(lease, fence, cancellationToken);
        throw new WorkflowJobInvariantException("The automatic activity item adapter is not installed.");
    }

    // The node a body's jobs name must still be an automatic body of that same kind.
    private static bool IsInstalledAutomaticBodyOfKind(FlowNodeModel node, string? jobNodeType) =>
        AutomaticMultiInstanceRules.IsAutomaticBody(node)
        && string.Equals(node.Type, jobNodeType, StringComparison.Ordinal)
        && AutomaticMultiInstanceRules.IsAdapterInstalled(node.Type);

    private static string AutomaticActivityLabel(string? nodeType) =>
        nodeType is not null && BpmnFlowNodeTypes.IsServiceTask(nodeType) ? "Service task" : "Script task";

    private async Task ProcessAutomaticBodyEntryAsync(WorkflowJobLeaseRecord lease, WorkflowJobFence fence,
        Func<FlowNodeModel, WorkflowModel, IEnumerable<string>, IReadOnlyList<string>> instanceInputNames,
        CancellationToken cancellationToken)
    {
        var coordinator = automaticMultiInstanceCoordinator
            ?? throw new WorkflowJobInvariantException("The automatic activity adapter has no body coordinator.");
        string? failureCode = null;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            var instance = await GetInstanceForJobUpdateAsync(lease.Job.InstanceId!.Value, cancellationToken)
                ?? throw new WorkflowConflictException("The workflow instance no longer exists.");
            var token = await runtime.GetExecutionTokenAsync(lease.Job.TokenId!.Value, true, cancellationToken);
            var job = await jobs.GetForUpdateAsync(lease.Job.Id, cancellationToken)
                ?? throw new WorkflowConflictException("The workflow job no longer exists.");
            EnsureJobFence(job, fence);
            token = RequireFencedToken(instance, job, token);
            var definition = (await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken)).Definition;
            var node = GetFlowNode(definition, token.NodeId);
            if (!IsInstalledAutomaticBodyOfKind(node, lease.Job.NodeType))
                throw new WorkflowJobInvariantException("The automatic entry no longer names a body of its activity kind.");
            var actor = ReadJobActor(job);
            var multi = node.MultiInstance!;
            var aggregate = definition.Variables.Single(variable =>
                string.Equals(variable.Name, multi.ResultVariable, StringComparison.OrdinalIgnoreCase));
            var stored = await runtime.LoadLatestVariableVersionsAsync(instance.Id, cancellationToken);
            var inputs = instanceInputNames(node, definition, stored.Select(value => value.Name));
            var configured = await engineSettings.GetByKeyAsync("Workflow.MultiInstance.MaxInstances", cancellationToken);
            var create = new AutomaticMultiInstanceCreateRecord
            {
                InstanceId = instance.Id,
                TokenId = token.Id,
                WorkflowDefinitionId = instance.WorkflowDefinitionId,
                ActivationId = token.ActivationId,
                NodeId = node.Id,
                NodeName = node.Name,
                NodeExternalId = node.ExternalId,
                ActivityKind = node.Type,
                Mode = multi.Mode,
                ResultVariable = aggregate.Name!,
                AggregateScope = aggregate.Scope == VariableScopes.Shared ? VariableScopes.Shared : VariableScopes.Instance,
                AggregateSharedKey = aggregate.Scope == VariableScopes.Shared ? aggregate.SharedKey : null,
                AggregateValueVersion = 0,
                MaxInstances = configured is not null && int.TryParse(configured.Value, out var parsed) && parsed > 0 ? parsed : 1000,
                EffectiveConcurrency = 0,
                TotalCount = 0,
                ResultBytes = 2,
                // The coordinator reads only the names; it captures their values and the
                // collection under the initialization locks.
                InputSnapshot = JsonSerializer.SerializeToElement(new
                {
                    collection = Array.Empty<object>(),
                    instanceInputs = inputs.ToDictionary(name => name, _ => (object?)null)
                }),
                InitializedAt = timeProvider.GetUtcNow()
            };
            try
            {
                await coordinator.InitializeBodyAsync(create, definition, actor, fence,
                    BuildJobCreate(instance, token, node, WorkflowJobKinds.MultiInstanceDispatch,
                        WorkflowJobKinds.MultiInstanceDispatch, actor, timeProvider.GetUtcNow()),
                    CreateAutomaticMultiInstanceLifecycle(instance, definition, actor), cancellationToken);
            }
            catch (AutomaticMultiInstanceValueException error)
            {
                failureCode = error.Code;
            }

            if (failureCode is null)
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }
        }

        unitOfWork.DiscardChanges();
        await HandleAutomaticEntryFailureAsync(lease, fence, failureCode, cancellationToken);
    }

    /// <summary>
    /// An invalid or oversized source is a pre-fan-out failure of the entry job. Like an
    /// ordinary activity failure it follows the node's job policy: retry, then the
    /// catch-all error boundary, or an incident when there is none. The failure has no
    /// HTTP status, so a REST body's status-matched boundaries never take it. The
    /// boundary is taken exactly as a body's own error route takes it, under the
    /// Error-phase locks with the error target validated, never through the node's
    /// ordinary lock plan.
    /// </summary>
    private async Task HandleAutomaticEntryFailureAsync(WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence, string code, CancellationToken cancellationToken)
    {
        var outcome = TaskExecutionOutcome.Fail(code);
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            var instance = await GetInstanceForJobUpdateAsync(lease.Job.InstanceId!.Value, cancellationToken)
                ?? throw new WorkflowConflictException("The workflow instance no longer exists.");
            var token = await runtime.GetExecutionTokenAsync(lease.Job.TokenId!.Value, true, cancellationToken);
            var job = await jobs.GetForUpdateAsync(lease.Job.Id, cancellationToken)
                ?? throw new WorkflowConflictException("The workflow job no longer exists.");
            EnsureJobFence(job, fence);
            token = RequireFencedToken(instance, job, token);
            var definition = (await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken)).Definition;
            var node = GetFlowNode(definition, token.NodeId);
            if (!ShouldRetryBeforeBoundary(job, definition, instance.Id, node, outcome)
                && SelectErrorBoundary(definition, node.Id, outcome).Boundary is { } boundary)
            {
                var actor = ReadJobActor(job);
                if (!await runtime.ClearExecutionTokenWaitAsync(token.Id, token.ActivationId,
                        ExecutionTokenWaitStates.AsyncBefore, job.Id, null, cancellationToken))
                    throw new WorkflowConflictException("The workflow job no longer owns the token wait.");
                // Error capture takes precedence over the host's other boundaries, as it
                // does for any failed activity. No timer exists before a body does.
                await CloseAutomaticMultiInstanceBoundariesAsync(instance.Id, token.Id, cancellationToken);
                var waiting = await runtime.GetExecutionTokenAsync(token.Id, false, cancellationToken)
                    ?? throw new WorkflowConflictException("The automatic entry token no longer exists.");
                if (await EnterAutomaticErrorBoundaryAsync(instance, waiting, node, boundary, definition, actor, code,
                        executionId: null, new() { ["phase"] = JsonSerializer.SerializeToElement("entry") },
                        cancellationToken)
                    && await IsInstanceRunningAsync(instance.Id, cancellationToken))
                {
                    var flowInfo = await LoadSequenceFlowInfoAsync(instance.Id, definition, cancellationToken);
                    var resumed = await ResolvePassThroughAsync(instance, definition, actor, flowInfo, token.Id,
                        cancellationToken, forceDurableActivities: true);
                    await EnsureMultiInstanceInitializedAsync(resumed, definition, actor, cancellationToken);
                    _ = await ApplyUserTaskOwnershipInheritanceAsync(resumed, definition, cancellationToken);
                }
                if (!await jobs.CompleteAsync(fence, cancellationToken))
                    throw new WorkflowConflictException("The workflow job lease was lost before completion.");
                await unitOfWork.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }
        }

        unitOfWork.DiscardChanges();
        await HandleProcessingFailureAsync(lease, fence, AutomaticItemFailureIncident,
            $"Automatic multi-instance entry failed: {code}.", cancellationToken);
    }

    private sealed record AutomaticItemOwner(
        WorkflowInstanceRecord Instance,
        ExecutionTokenRecord Token,
        AutomaticMultiInstanceRecord Body,
        WorkflowJobRecord Job,
        WorkflowModel Definition,
        FlowNodeModel Node,
        ActorContext Actor,
        long ItemId)
    {
        public WorkflowJobCreateRecord ControlTemplate { get; init; } = null!;
    }

    // Instance, token, body, item, then the job: the documented runtime order, in which
    // body and item rows are locked before any job or shared row.
    private async Task<AutomaticItemOwner> LockAutomaticItemOwnerAsync(WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence, CancellationToken cancellationToken)
    {
        if (automaticMultiInstances is null
            || lease.Job.MultiInstanceExecutionId is not long executionId
            || lease.Job.AutomaticMultiInstanceItemId is not long itemId)
            throw new WorkflowJobInvariantException("The automatic item job has no body or item ownership.");
        var instance = await GetInstanceForJobUpdateAsync(lease.Job.InstanceId!.Value, cancellationToken)
            ?? throw new WorkflowConflictException("The automatic body instance no longer exists.");
        var token = await runtime.GetExecutionTokenAsync(lease.Job.TokenId!.Value, true, cancellationToken);
        var body = await automaticMultiInstances.GetAsync(executionId, true, cancellationToken);
        if (instance.Status != WorkflowInstanceStatuses.Running || token is null || body is null
            || token.Status != ExecutionTokenRecordStatuses.Active || token.ActivationId != lease.Job.ActivationId
            || token.WaitingMultiInstanceExecutionId != executionId || body.TokenId != token.Id
            || body.Status != MultiInstanceRecordStatuses.Active || body.ActivationId != token.ActivationId
            || body.WorkflowDefinitionId != instance.WorkflowDefinitionId)
            throw new WorkflowConflictException("The automatic item job no longer owns its body.");
        _ = await automaticMultiInstances.GetItemAsync(executionId, itemId, true, cancellationToken)
            ?? throw new WorkflowConflictException("The automatic item no longer exists.");
        var job = await jobs.GetForUpdateAsync(lease.Job.Id, cancellationToken)
            ?? throw new WorkflowConflictException("The automatic item job no longer exists.");
        EnsureJobFence(job, fence);
        var definition = (await GetWorkflowAsync(body.WorkflowDefinitionId, cancellationToken)).Definition;
        var node = GetFlowNode(definition, body.NodeId);
        if (!IsInstalledAutomaticBodyOfKind(node, lease.Job.NodeType))
            throw new WorkflowJobInvariantException("The automatic item no longer names a body of its activity kind.");
        var actor = ReadJobActor(job);
        return new(instance, token, body, job, definition, node, actor, itemId)
        {
            ControlTemplate = BuildJobCreate(instance, token, node, WorkflowJobKinds.MultiInstanceDispatch,
                WorkflowJobKinds.MultiInstanceDispatch, actor, timeProvider.GetUtcNow())
        };
    }

    /// <summary>
    /// Commits a validated candidate in its own transaction, before publication, so a
    /// later failure can never lose the only record of a successful activity. Returns
    /// the refusal code when the coordinator refuses it, or null once it is saved.
    /// </summary>
    private async Task<string?> SaveAutomaticItemCandidateAsync(WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence, AutomaticMultiInstanceCandidate candidate, CancellationToken cancellationToken)
    {
        string? refusal = null;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            var owner = await LockAutomaticItemOwnerAsync(lease, fence, cancellationToken);
            try
            {
                await automaticMultiInstanceCoordinator!.SaveCandidateAsync(owner.Body.Id, owner.ItemId,
                    owner.Body.ActivationId, fence, owner.Definition, candidate, cancellationToken);
            }
            catch (AutomaticMultiInstanceValueException error)
            {
                refusal = error.Code;
            }
            catch (WorkflowDomainException)
            {
                // The adapter already validated the candidate; the coordinator's
                // second check reports the same authored output failure.
                refusal = AutomaticMultiInstanceValues.ResultInvalid;
            }

            if (refusal is null)
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
        }
        unitOfWork.DiscardChanges();
        return refusal;
    }

    private async Task PublishAutomaticItemAsync(WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence, CancellationToken cancellationToken)
    {
        string? refusal = null;
        string? invalid = null;
        var conflict = false;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            var owner = await LockAutomaticItemOwnerAsync(lease, fence, cancellationToken);
            try
            {
                await automaticMultiInstanceCoordinator!.PublishCandidateAsync(owner.Body.Id, owner.ItemId,
                    owner.Body.ActivationId, fence, owner.Definition, owner.Actor, owner.ControlTemplate, cancellationToken);
            }
            catch (AutomaticMultiInstancePublicationConflictException)
            {
                conflict = true;
            }
            catch (AutomaticMultiInstanceValueException error) when (error.Code == AutomaticMultiInstanceValues.ResultTooLarge)
            {
                refusal = error.Code;
            }
            catch (Exception error) when (error is AutomaticMultiInstanceValueException or WorkflowDomainException)
            {
                // A local or mapping validation that reads a shared alias can pass when
                // the candidate is built and fail against the values current at
                // publication. The saved candidate cannot change, so this is its
                // authored output failure.
                invalid = error.Message;
            }

            if (!conflict && refusal is null && invalid is null)
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }
        }

        unitOfWork.DiscardChanges();
        if (conflict)
        {
            WorkflowJobRuntimeTelemetry.RecordConflict();
            // The candidate stays saved: an authorized retry publishes it against
            // refreshed fences without running the activity again.
            if (await jobs.OpenIncidentAsync(fence, "output_version_conflict",
                    $"Automatic item shared output conflict at node #{lease.Job.NodeId}.",
                    "A shared output changed after this item staged. Retry publishes the saved result against the current value and can replace a newer one.",
                    cancellationToken) is not null)
                WorkflowJobRuntimeTelemetry.RecordIncident();
            return;
        }
        if (refusal is not null)
        {
            await RefuseAutomaticItemAsync(lease, fence, refusal, cancellationToken);
            return;
        }
        // The saved candidate holds no response status, so only a catch-all boundary
        // can take this failure.
        await HandleAutomaticItemFailureAsync(lease, fence, AutomaticMultiInstanceValues.ResultInvalid,
            TaskExecutionOutcome.Fail(
                $"{AutomaticActivityLabel(lease.Job.NodeType)} #{lease.Job.NodeId} item result does not satisfy its declarations at publication."),
            cancellationToken, "The saved item result failed its validation when it was published.");
    }

    /// <summary>
    /// An authored failure follows the node's job policy. <c>boundaryFirst</c> takes the
    /// error boundary its outcome selects now (a REST outcome may carry an HTTP status
    /// that an exact or range boundary matches); <c>retryFirst</c> or a missing boundary
    /// retries the same item job, then opens an incident that blocks admission. The
    /// boundary's error variable receives <paramref name="code"/>, never the reason. A
    /// caught failure records <paramref name="itemDescription"/> on the item and its node
    /// visit when the adapter supplies one; it must hold no request or response data.
    /// </summary>
    private async Task HandleAutomaticItemFailureAsync(WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence, string code, TaskExecutionOutcome outcome, CancellationToken cancellationToken,
        string? itemDescription = null)
    {
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            var owner = await LockAutomaticItemOwnerAsync(lease, fence, cancellationToken);
            var boundary = SelectErrorBoundary(owner.Definition, owner.Node.Id, outcome).Boundary;
            if (!ShouldRetryBeforeBoundary(owner.Job, owner.Definition, owner.Instance.Id, owner.Node, outcome)
                && boundary is not null)
            {
                if (!await automaticMultiInstanceCoordinator!.CaptureItemFailureAsync(owner.Body.Id, owner.ItemId,
                        owner.Body.ActivationId, fence,
                        new AutomaticMultiInstanceCompletionIntent(boundary.Id, code, owner.ItemId), owner.Actor,
                        owner.ControlTemplate,
                        CreateAutomaticMultiInstanceLifecycle(owner.Instance, owner.Definition, owner.Actor),
                        cancellationToken, itemDescription))
                    throw new WorkflowConflictException("Another failure already stopped this automatic body.");
                await unitOfWork.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }
        }

        unitOfWork.DiscardChanges();
        await HandleProcessingFailureAsync(lease, fence, AutomaticItemFailureIncident,
            LimitJobFailure(outcome.Reason ?? code), cancellationToken);
    }

    // A result retrying cannot change is refused in a fresh transaction. It is never
    // retried and never waits in an incident.
    private async Task RefuseAutomaticItemAsync(WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence, string code, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var owner = await LockAutomaticItemOwnerAsync(lease, fence, cancellationToken);
        if (!await automaticMultiInstanceCoordinator!.RefuseItemResultAsync(owner.Body.Id, owner.ItemId,
                owner.Body.ActivationId, fence, code, owner.Actor, owner.ControlTemplate,
                CreateAutomaticMultiInstanceLifecycle(owner.Instance, owner.Definition, owner.Actor),
                cancellationToken))
            throw new WorkflowConflictException("Another failure already stopped this automatic body.");
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Stages an item once: captures its shared, configuration, and clock values through
    /// the coordinator in a committed transaction, or returns the saved context on a
    /// retry. A null context means the item already has a saved candidate and goes
    /// straight to publication; a refusal code means the staged input is too large.
    /// </summary>
    private async Task<(AutomaticMultiInstanceItemContext? Context, string? Refusal, FlowNodeModel Node, WorkflowModel Definition)>
        StageAutomaticItemAsync(WorkflowJobLeaseRecord lease, WorkflowJobFence fence,
            Func<AutomaticItemOwner, DateTimeOffset, Dictionary<string, JsonElement>> environment,
            CancellationToken cancellationToken)
    {
        var coordinator = automaticMultiInstanceCoordinator
            ?? throw new WorkflowJobInvariantException("The automatic activity adapter has no body coordinator.");
        AutomaticMultiInstanceItemContext? context = null;
        string? refusal = null;
        FlowNodeModel node;
        WorkflowModel definition;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            var owner = await LockAutomaticItemOwnerAsync(lease, fence, cancellationToken);
            (node, definition) = (owner.Node, owner.Definition);
            var item = await automaticMultiInstances!.GetItemAsync(owner.Body.Id, owner.ItemId, true, cancellationToken)
                ?? throw new WorkflowConflictException("The automatic item no longer exists.");
            // Every retry after a committed candidate goes straight to publication.
            if (item.Candidate is null)
            {
                var stagedAt = timeProvider.GetUtcNow();
                await LoadSettingsAsync(cancellationToken);
                try
                {
                    context = await coordinator.CaptureItemContextAsync(owner.Body.Id, owner.ItemId,
                        owner.Body.ActivationId, fence, definition, environment(owner, stagedAt), stagedAt, cancellationToken);
                }
                catch (AutomaticMultiInstanceValueException error)
                {
                    // A staged input over the item limit is the same on every attempt.
                    refusal = error.Code;
                }
            }

            if (refusal is null)
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
        }
        unitOfWork.DiscardChanges();
        return (context, refusal, node, definition);
    }

    /// <summary>
    /// The configuration, setting, and system values an item captures when it first
    /// stages and keeps for its retries: all of them, or only the referenced names.
    /// </summary>
    private Dictionary<string, JsonElement> AutomaticItemEnvironment(AutomaticItemOwner owner, DateTimeOffset stagedAt,
        IReadOnlyCollection<string>? referenced)
    {
        var all = BuildContextMap(owner.Actor, owner.Instance with
        {
            ActiveTokenId = owner.Token.Id,
            CurrentStepId = owner.Token.NodeId,
            CurrentNodeExecutionId = owner.Token.CurrentNodeExecutionId
        }, owner.Definition, owner.Node, stagedAt);
        return referenced is null
            ? all
            : all.Where(pair => referenced.Contains(pair.Key))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    // The instance variables a body freezes, from the names its expressions reference:
    // never its own aggregate target, which an item never reads, nor its collection,
    // which the snapshot already holds once, nor a context name or a local output.
    private static IReadOnlyList<string> AutomaticInstanceInputNames(FlowNodeModel node, IEnumerable<string> names)
    {
        var multi = node.MultiInstance!;
        return names
            .Where(name => !string.IsNullOrWhiteSpace(name) && !IsAutomaticContextName(name)
                && !string.Equals(name, multi.ResultVariable, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, multi.CollectionVariable, StringComparison.OrdinalIgnoreCase)
                && !AutomaticMultiInstanceRules.IsLocalOutput(node, name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Names the local outputs' default templates and validations reference.
    private static void AddAutomaticLocalOutputReferences(FlowNodeModel node, ISet<string> names)
    {
        foreach (var output in node.MultiInstance?.OutputVariables ?? [])
        {
            if (output is null) continue;
            names.UnionWith(SequenceFlowConditionEvaluator.ParameterNames(output.Validation));
            names.UnionWith(ServiceTaskTemplating.GetPlaceholderNames(DefaultTemplates(output.DefaultValue)));
        }
    }

    private static string?[] DefaultTemplates(JsonElement? value) => value switch
    {
        { ValueKind: JsonValueKind.String } text => [text.GetString()],
        { ValueKind: JsonValueKind.Array } array => array.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()).ToArray(),
        _ => []
    };

    private static bool IsAutomaticContextName(string name) =>
        name.StartsWith("sys.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("config.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("setting.", StringComparison.OrdinalIgnoreCase)
        || name.StartsWith("mi.", StringComparison.OrdinalIgnoreCase);

    // Only the names local validations reference, never the whole evaluation context.
    // Locals and shared writes are added back by the coordinator from the candidate.
    private static Dictionary<string, JsonElement> AutomaticValidationContext(IEnumerable<string?> validations,
        IReadOnlyDictionary<string, JsonElement> overlay, IReadOnlyDictionary<string, JsonElement> locals,
        IReadOnlyDictionary<string, JsonElement> sharedWrites)
    {
        var referenced = validations
            .SelectMany(SequenceFlowConditionEvaluator.ParameterNames)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return overlay.Where(pair => referenced.Contains(pair.Key)
                && !locals.ContainsKey(pair.Key) && !sharedWrites.ContainsKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase);
    }
}
