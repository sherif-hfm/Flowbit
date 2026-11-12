using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>
/// TASK-05C script adapter. It names the instance inputs a body freezes, supplies the
/// staged environment, evaluates NCalc assignments or the sandboxed JavaScript body in
/// a dedicated item context, and hands a candidate or a classified failure to the
/// common coordinator. It never writes runtime variables or advances a token itself.
/// </summary>
public sealed partial class WorkflowEngineService
{
    /// <summary>The stable code a caught script failure leaves in a boundary's error variable.</summary>
    internal const string AutomaticScriptFailureCode = "script_task_failed";

    private const string AutomaticItemFailureIncident = "job_execution_failed";

    // Entry and item jobs of service and script bodies arrive here. Each installed
    // adapter adds one case; every other kind keeps failing closed exactly as before.
    private Task ProcessAutomaticMultiInstanceEntryJobAsync(WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence, FlowNodeModel node, CancellationToken cancellationToken)
    {
        EnsureAutomaticMultiInstanceRuntimeAvailable(node);
        return BpmnFlowNodeTypes.IsScriptTask(node.Type)
            ? ProcessAutomaticScriptEntryAsync(lease, fence, cancellationToken)
            : throw new WorkflowJobInvariantException("The automatic activity entry adapter is not installed.");
    }

    private Task ProcessAutomaticMultiInstanceItemJobAsync(WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence, CancellationToken cancellationToken)
    {
        // A closed switch is a host setting, not a broken job: the item fails like any
        // refused work, keeps its slot, and resumes from its incident on an open host.
        if (automaticMultiInstanceOptions?.Enabled != true)
            throw new WorkflowDomainException(
                $"Automatic multi-instance node #{lease.Job.NodeId} cannot run on this host: automatic multi-instance runs only on a Development host with the release switch open.");
        return BpmnFlowNodeTypes.IsScriptTask(lease.Job.NodeType) && AutomaticMultiInstanceRules.IsAdapterInstalled(lease.Job.NodeType)
            ? ProcessAutomaticScriptItemAsync(lease, fence, cancellationToken)
            : throw new WorkflowJobInvariantException("The automatic activity item adapter is not installed.");
    }

    private async Task ProcessAutomaticScriptEntryAsync(WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence, CancellationToken cancellationToken)
    {
        var coordinator = automaticMultiInstanceCoordinator
            ?? throw new WorkflowJobInvariantException("The automatic script adapter has no body coordinator.");
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
            if (!AutomaticMultiInstanceRules.IsAutomaticBody(node) || !BpmnFlowNodeTypes.IsScriptTask(node.Type))
                throw new WorkflowJobInvariantException("The automatic script entry no longer names a script body.");
            var actor = ReadJobActor(job);
            var multi = node.MultiInstance!;
            var aggregate = definition.Variables.Single(variable =>
                string.Equals(variable.Name, multi.ResultVariable, StringComparison.OrdinalIgnoreCase));
            var stored = await runtime.LoadLatestVariableVersionsAsync(instance.Id, cancellationToken);
            var inputs = AutomaticScriptInstanceInputNames(node, definition, stored.Select(value => value.Name));
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
        await HandleAutomaticScriptEntryFailureAsync(lease, fence, failureCode, cancellationToken);
    }

    /// <summary>
    /// An invalid or oversized source is a pre-fan-out failure of the entry job. Like an
    /// ordinary script's failure it follows the node's job policy: retry, then the
    /// catch-all error boundary, or an incident when there is none. The boundary is
    /// taken exactly as a body's own error route takes it, under the Error-phase locks
    /// with the error target validated, never through the node's ordinary lock plan.
    /// </summary>
    private async Task HandleAutomaticScriptEntryFailureAsync(WorkflowJobLeaseRecord lease,
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

    // Instance, token, body, then the job: the order every automatic control job uses.
    private async Task<AutomaticItemOwner> LockAutomaticScriptItemOwnerAsync(WorkflowJobLeaseRecord lease,
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
        var job = await jobs.GetForUpdateAsync(lease.Job.Id, cancellationToken)
            ?? throw new WorkflowConflictException("The automatic item job no longer exists.");
        EnsureJobFence(job, fence);
        var definition = (await GetWorkflowAsync(body.WorkflowDefinitionId, cancellationToken)).Definition;
        var node = GetFlowNode(definition, body.NodeId);
        if (!AutomaticMultiInstanceRules.IsAutomaticBody(node) || !BpmnFlowNodeTypes.IsScriptTask(node.Type))
            throw new WorkflowJobInvariantException("The automatic item no longer names a script body.");
        var actor = ReadJobActor(job);
        return new(instance, token, body, job, definition, node, actor, itemId)
        {
            ControlTemplate = BuildJobCreate(instance, token, node, WorkflowJobKinds.MultiInstanceDispatch,
                WorkflowJobKinds.MultiInstanceDispatch, actor, timeProvider.GetUtcNow())
        };
    }

    /// <summary>
    /// Stage, evaluate, save, and publish one item, in that order and in separate
    /// transactions. No runtime or shared row lock is held while the script runs, a
    /// saved candidate is never evaluated again, and each failure takes exactly one
    /// coordinator exit.
    /// </summary>
    private async Task ProcessAutomaticScriptItemAsync(WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence, CancellationToken cancellationToken)
    {
        var coordinator = automaticMultiInstanceCoordinator
            ?? throw new WorkflowJobInvariantException("The automatic script adapter has no body coordinator.");
        AutomaticMultiInstanceItemContext? context = null;
        string? stagingRefusal = null;
        FlowNodeModel node;
        WorkflowModel definition;
        long executionId;
        Guid activationId;
        long itemId;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            var owner = await LockAutomaticScriptItemOwnerAsync(lease, fence, cancellationToken);
            (node, definition, executionId, activationId, itemId) =
                (owner.Node, owner.Definition, owner.Body.Id, owner.Body.ActivationId, owner.ItemId);
            var item = await automaticMultiInstances!.GetItemAsync(executionId, itemId, true, cancellationToken)
                ?? throw new WorkflowConflictException("The automatic item no longer exists.");
            // Every retry after a committed candidate goes straight to publication.
            if (item.Candidate is null)
            {
                var stagedAt = timeProvider.GetUtcNow();
                await LoadSettingsAsync(cancellationToken);
                try
                {
                    context = await coordinator.CaptureItemContextAsync(executionId, itemId, activationId, fence,
                        definition, AutomaticScriptEnvironment(owner, stagedAt), stagedAt, cancellationToken);
                }
                catch (AutomaticMultiInstanceValueException error)
                {
                    // A staged input over the item limit is the same on every attempt.
                    stagingRefusal = error.Code;
                }
            }

            if (stagingRefusal is null)
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
        }
        unitOfWork.DiscardChanges();
        if (stagingRefusal is not null)
        {
            await RefuseAutomaticScriptItemAsync(lease, fence, stagingRefusal, cancellationToken);
            return;
        }

        if (context is not null)
        {
            AutomaticMultiInstanceCandidate candidate;
            try
            {
                candidate = EvaluateAutomaticScriptItem(scriptEvaluator, node, definition, context, cancellationToken);
            }
            catch (AutomaticScriptItemException failure)
            {
                await HandleAutomaticScriptItemFailureAsync(lease, fence, failure.Code, failure.Message, cancellationToken);
                return;
            }

            string? refusal = null;
            await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
            {
                _ = await LockAutomaticScriptItemOwnerAsync(lease, fence, cancellationToken);
                try
                {
                    await coordinator.SaveCandidateAsync(executionId, itemId, activationId, fence, definition,
                        candidate, cancellationToken);
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
            if (refusal == AutomaticMultiInstanceValues.ResultInvalid)
            {
                await HandleAutomaticScriptItemFailureAsync(lease, fence, refusal,
                    $"Script task #{node.Id} item result does not satisfy its declarations.", cancellationToken);
                return;
            }
            if (refusal is not null)
            {
                await RefuseAutomaticScriptItemAsync(lease, fence, refusal, cancellationToken);
                return;
            }
        }

        await PublishAutomaticScriptItemAsync(lease, fence, cancellationToken);
    }

    private async Task PublishAutomaticScriptItemAsync(WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence, CancellationToken cancellationToken)
    {
        string? refusal = null;
        string? invalid = null;
        var conflict = false;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            var owner = await LockAutomaticScriptItemOwnerAsync(lease, fence, cancellationToken);
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
                // A local validation that reads a shared alias can pass at evaluation and
                // fail against the values current at publication. The saved candidate
                // cannot change, so this is its authored output failure.
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
            // refreshed fences without evaluating the script again.
            if (await jobs.OpenIncidentAsync(fence, "output_version_conflict",
                    $"Automatic item shared output conflict at node #{lease.Job.NodeId}.",
                    "A shared output changed after this item staged. Retry publishes the saved result against the current value and can replace a newer one.",
                    cancellationToken) is not null)
                WorkflowJobRuntimeTelemetry.RecordIncident();
            return;
        }
        if (refusal is not null)
        {
            await RefuseAutomaticScriptItemAsync(lease, fence, refusal, cancellationToken);
            return;
        }
        await HandleAutomaticScriptItemFailureAsync(lease, fence, AutomaticMultiInstanceValues.ResultInvalid,
            $"Script task #{lease.Job.NodeId} item result does not satisfy its declarations at publication.",
            cancellationToken);
    }

    /// <summary>
    /// An authored failure follows the node's job policy. <c>boundaryFirst</c> takes the
    /// catch-all error boundary now; <c>retryFirst</c> or a missing boundary retries the
    /// same item job with fresh buffers, then opens an incident that blocks admission.
    /// </summary>
    private async Task HandleAutomaticScriptItemFailureAsync(WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence, string code, string reason, CancellationToken cancellationToken)
    {
        var outcome = TaskExecutionOutcome.Fail(reason);
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            var owner = await LockAutomaticScriptItemOwnerAsync(lease, fence, cancellationToken);
            var boundary = SelectErrorBoundary(owner.Definition, owner.Node.Id, outcome).Boundary;
            if (!ShouldRetryBeforeBoundary(owner.Job, owner.Definition, owner.Instance.Id, owner.Node, outcome)
                && boundary is not null)
            {
                if (!await automaticMultiInstanceCoordinator!.CaptureItemFailureAsync(owner.Body.Id, owner.ItemId,
                        owner.Body.ActivationId, fence,
                        new AutomaticMultiInstanceCompletionIntent(boundary.Id, code, owner.ItemId), owner.Actor,
                        owner.ControlTemplate,
                        CreateAutomaticMultiInstanceLifecycle(owner.Instance, owner.Definition, owner.Actor),
                        cancellationToken))
                    throw new WorkflowConflictException("Another failure already stopped this automatic body.");
                await unitOfWork.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }
        }

        unitOfWork.DiscardChanges();
        await HandleProcessingFailureAsync(lease, fence, AutomaticItemFailureIncident,
            LimitJobFailure(reason), cancellationToken);
    }

    // A result retrying cannot change is refused in a fresh transaction. It is never
    // retried and never waits in an incident.
    private async Task RefuseAutomaticScriptItemAsync(WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence, string code, CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var owner = await LockAutomaticScriptItemOwnerAsync(lease, fence, cancellationToken);
        if (!await automaticMultiInstanceCoordinator!.RefuseItemResultAsync(owner.Body.Id, owner.ItemId,
                owner.Body.ActivationId, fence, code, owner.Actor, owner.ControlTemplate,
                CreateAutomaticMultiInstanceLifecycle(owner.Instance, owner.Definition, owner.Actor),
                cancellationToken))
            throw new WorkflowConflictException("Another failure already stopped this automatic body.");
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// The instance variables a body freezes. NCalc freezes the names its assignments,
    /// local default templates, and local validations reference. JavaScript can read any
    /// name, so it freezes every stored instance variable, listed under the instance lock.
    /// Neither freezes the body's own aggregate target, which an item never reads, nor
    /// its collection, which the snapshot already holds once and every item reads from
    /// there.
    /// </summary>
    internal static IReadOnlyList<string> AutomaticScriptInstanceInputNames(FlowNodeModel node,
        WorkflowModel definition, IEnumerable<string> storedNames)
    {
        var multi = node.MultiInstance!;
        IEnumerable<string> names = string.Equals(node.ScriptFormat, ScriptFormats.JavaScript, StringComparison.Ordinal)
            ? storedNames
            : AutomaticScriptReferencedNames(node);
        return names
            .Where(name => !string.IsNullOrWhiteSpace(name) && !IsAutomaticContextName(name)
                && !string.Equals(name, multi.ResultVariable, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, multi.CollectionVariable, StringComparison.OrdinalIgnoreCase)
                && !AutomaticMultiInstanceRules.IsLocalOutput(node, name))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    // Names an NCalc body's expressions and templates reference, context included.
    private static IReadOnlyCollection<string> AutomaticScriptReferencedNames(FlowNodeModel node)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var assignment in node.Assignments ?? [])
            names.UnionWith(SequenceFlowConditionEvaluator.ParameterNames(assignment?.Expression));
        foreach (var output in node.MultiInstance?.OutputVariables ?? [])
        {
            if (output is null) continue;
            names.UnionWith(SequenceFlowConditionEvaluator.ParameterNames(output.Validation));
            names.UnionWith(ServiceTaskTemplating.GetPlaceholderNames(DefaultTemplates(output.DefaultValue)));
        }
        return names;
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

    /// <summary>
    /// The config, setting, and system values an item captures when it first stages and
    /// keeps for its retries. NCalc captures only the names it references; JavaScript can
    /// read any name, so every item stores all of them within its 1 MiB input limit.
    /// </summary>
    private Dictionary<string, JsonElement> AutomaticScriptEnvironment(AutomaticItemOwner owner, DateTimeOffset stagedAt)
    {
        var all = BuildContextMap(owner.Actor, owner.Instance with
        {
            ActiveTokenId = owner.Token.Id,
            CurrentStepId = owner.Token.NodeId,
            CurrentNodeExecutionId = owner.Token.CurrentNodeExecutionId
        }, owner.Definition, owner.Node, stagedAt);
        if (string.Equals(owner.Node.ScriptFormat, ScriptFormats.JavaScript, StringComparison.Ordinal))
            return all;
        var referenced = AutomaticScriptReferencedNames(owner.Node);
        return all.Where(pair => referenced.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Evaluates one item over its frozen context and returns the candidate the coordinator
    /// saves. Writes resolve only to declared locals or writable shared aliases; nothing
    /// else, an ordinary instance variable included, can be written from an item. Throws
    /// <see cref="AutomaticScriptItemException"/> for every authored failure.
    /// </summary>
    internal static AutomaticMultiInstanceCandidate EvaluateAutomaticScriptItem(IScriptEvaluator evaluator,
        FlowNodeModel node, WorkflowModel definition, AutomaticMultiInstanceItemContext context,
        CancellationToken cancellationToken)
    {
        var multi = node.MultiInstance!;
        var outputs = multi.OutputVariables ?? [];
        var overlay = context.Values.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        // An item never reads its own aggregate target, whatever its scope.
        overlay.Remove(multi.ResultVariable ?? string.Empty);
        var writer = new AutomaticScriptItemWriter(node, definition, overlay,
            context.LocalVariables.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase));
        try
        {
            if (string.Equals(node.ScriptFormat, ScriptFormats.JavaScript, StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(node.Script))
                    throw new WorkflowDomainException($"Script task #{node.Id} failed: the JavaScript body is missing.");
                var result = evaluator.Evaluate(node.Script,
                    new AutomaticScriptItemContext(overlay, writer, context.FlowInfo), cancellationToken);
                if (!result.Success)
                    throw new WorkflowDomainException($"Script task #{node.Id} failed: {result.Error}");
            }
            else
            {
                foreach (var assignment in node.Assignments ?? [])
                {
                    if (assignment is null || string.IsNullOrWhiteSpace(assignment.Variable))
                        throw new WorkflowDomainException($"Script task #{node.Id} has an invalid assignment target.");
                    writer.Write(assignment.Variable, SequenceFlowConditionEvaluator.EvaluateValue(
                        assignment.Expression, overlay, flowInfo: context.FlowInfo));
                }
            }
        }
        catch (Exception error) when (error is WorkflowDomainException or FormatException or OverflowException
                                          or InvalidCastException
                                          || error is InvalidOperationException and not AutomaticMultiInstanceValueException)
        {
            // The same failures an ordinary script reports, including coercion errors.
            throw new AutomaticScriptItemException(AutomaticScriptFailureCode, error.Message);
        }

        var validationContext = AutomaticLocalValidationContext(outputs, overlay, writer);
        var finalContext = new Dictionary<string, JsonElement>(validationContext, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in writer.Locals.Concat(writer.SharedWrites)) finalContext[pair.Key] = pair.Value;
        try
        {
            // The same catalog contract publication enforces, checked before saving.
            foreach (var (alias, value) in writer.SharedWrites)
            {
                var binding = writer.Binding(alias);
                SharedVariableValueValidator.Validate(binding.SharedKey ?? binding.Name, binding.DataType,
                    binding.IsArray, binding.Nullable, binding.Validation, value);
            }
            _ = ValidateAutomaticLocalResult(outputs, writer.Locals, finalContext);
        }
        catch (AutomaticMultiInstanceValueException)
        {
            throw new AutomaticScriptItemException(AutomaticMultiInstanceValues.ResultInvalid,
                $"Script task #{node.Id} item left a non-nullable local output without a value.");
        }
        catch (WorkflowDomainException error)
        {
            throw new AutomaticScriptItemException(AutomaticMultiInstanceValues.ResultInvalid, error.Message);
        }

        return new AutomaticMultiInstanceCandidate(
            writer.Locals,
            writer.SharedWrites,
            // Staging fenced every possible producer; the candidate keeps exactly the
            // aliases this item wrote.
            writer.SharedWrites.Keys.ToDictionary(alias => alias,
                alias => context.SharedOutputValueVersions.TryGetValue(alias, out var version)
                    ? version
                    : throw new AutomaticScriptItemException(AutomaticScriptFailureCode,
                        $"Script task #{node.Id} item cannot write '{alias}': it has no output fence."),
                StringComparer.OrdinalIgnoreCase),
            validationContext);
    }

    // Only the names local validations reference, never the whole evaluation context.
    // Locals and shared writes are added back by the coordinator from the candidate.
    private static Dictionary<string, JsonElement> AutomaticLocalValidationContext(
        IReadOnlyList<MultiInstanceOutputVariableModel> outputs, IReadOnlyDictionary<string, JsonElement> overlay,
        AutomaticScriptItemWriter writer)
    {
        var referenced = outputs.Where(output => output is not null)
            .SelectMany(output => SequenceFlowConditionEvaluator.ParameterNames(output.Validation))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return overlay.Where(pair => referenced.Contains(pair.Key)
                && !writer.Locals.ContainsKey(pair.Key) && !writer.SharedWrites.ContainsKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Resolves an item's write targets: its declared locals, then writable shared
    /// aliases other than the body's collection, aggregate, and error targets. Every
    /// write is coerced like an ordinary script write, and later reads see it.
    /// </summary>
    private sealed class AutomaticScriptItemWriter
    {
        private readonly FlowNodeModel node;
        private readonly Dictionary<string, JsonElement> overlay;
        private readonly Dictionary<string, VariableModel> locals;
        private readonly Dictionary<string, VariableModel> process;
        private readonly HashSet<string> reservedNames;
        private readonly HashSet<string> reservedKeys;
        private readonly Dictionary<string, string> writtenKeys = new(StringComparer.Ordinal);

        public AutomaticScriptItemWriter(FlowNodeModel node, WorkflowModel definition,
            Dictionary<string, JsonElement> overlay, Dictionary<string, JsonElement> localDefaults)
        {
            this.node = node;
            this.overlay = overlay;
            Locals = localDefaults;
            locals = (node.MultiInstance!.OutputVariables ?? []).Where(output => output is not null)
                .Select(AutomaticMultiInstanceRules.ToVariable)
                .ToDictionary(variable => variable.Name!, StringComparer.OrdinalIgnoreCase);
            process = definition.Variables.Where(variable => !string.IsNullOrWhiteSpace(variable.Name))
                .GroupBy(variable => variable.Name!, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
            reservedNames = new[] { node.MultiInstance.CollectionVariable, node.MultiInstance.ResultVariable }
                .Concat(definition.FlowNodes.Where(boundary => boundary.AttachedToRef == node.Id
                        && BpmnFlowNodeTypes.IsErrorBoundary(boundary.Type))
                    .Select(boundary => boundary.ErrorVariable))
                .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            reservedKeys = process.Values.Where(variable => reservedNames.Contains(variable.Name!)
                    && variable.Scope == VariableScopes.Shared && variable.SharedKey is not null)
                .Select(variable => variable.SharedKey!).ToHashSet(StringComparer.Ordinal);
        }

        public Dictionary<string, JsonElement> Locals { get; }

        public Dictionary<string, JsonElement> SharedWrites { get; } = new(StringComparer.OrdinalIgnoreCase);

        public VariableModel Binding(string alias) => process[alias];

        public void Write(string? name, object? raw)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new WorkflowDomainException("execution.setVariable requires a variable name.");
            if (locals.TryGetValue(name, out var local))
            {
                var value = CoerceScriptValue(raw, local);
                Locals[local.Name!] = value;
                overlay[local.Name!] = value;
                return;
            }

            var owner = $"Script task #{node.Id} item";
            if (IsAutomaticContextName(name))
                throw new WorkflowDomainException($"{owner} cannot write context value '{name}'.");
            if (!process.TryGetValue(name, out var target))
                throw new WorkflowDomainException(
                    $"{owner} writes '{name}', which is not a declared local output or readWrite shared alias.");
            if (reservedNames.Contains(target.Name!)
                || target.Scope == VariableScopes.Shared && target.SharedKey is not null && reservedKeys.Contains(target.SharedKey))
                throw new WorkflowDomainException(
                    $"{owner} cannot write '{target.Name}'; it is this body's collection, aggregate, or boundary error target.");
            if (target.Scope != VariableScopes.Shared)
                throw new WorkflowDomainException(
                    $"{owner} cannot write instance variable '{target.Name}'; an item writes only its declared local outputs or readWrite shared aliases.");
            if (target.Access != SharedVariableAccessModes.ReadWrite)
                throw new WorkflowDomainException($"{owner} cannot write read-only shared variable '{target.Name}'.");
            if (writtenKeys.TryGetValue(target.SharedKey!, out var other)
                && !string.Equals(other, target.Name, StringComparison.OrdinalIgnoreCase))
                throw new WorkflowDomainException(
                    $"{owner} writes shared key '{target.SharedKey}' through both '{other}' and '{target.Name}'.");
            var coerced = CoerceScriptValue(raw, target);
            writtenKeys[target.SharedKey!] = target.Name!;
            SharedWrites[target.Name!] = coerced;
            overlay[target.Name!] = coerced;
        }
    }

    // The item's IScriptContext. Reads see frozen inputs, staged shared and context
    // values, mi.*, and this item's own writes. JavaScript values cross the bridge as
    // copies, so mutating one changes nothing without an explicit setVariable.
    private sealed class AutomaticScriptItemContext(
        Dictionary<string, JsonElement> overlay,
        AutomaticScriptItemWriter writer,
        SequenceFlowInfoSnapshot? flowInfo) : IScriptContext
    {
        public bool TryGetVariable(string name, out JsonElement value) => overlay.TryGetValue(name, out value);

        public bool HasVariable(string name) => overlay.ContainsKey(name);

        public IReadOnlyDictionary<string, JsonElement> GetVariables() => overlay;

        public SequenceFlowRuntimeSummary GetFlowInfo(int flowId)
        {
            if (flowInfo is null)
                throw new WorkflowDomainException(
                    "execution.getFlowInfo is not available because this workflow definition does not use FlowInfo.");
            if (!flowInfo.TryGetSummary(flowId, out var summary))
                throw new WorkflowDomainException($"execution.getFlowInfo references unknown sequence flow #{flowId}.");
            return summary;
        }

        public void SetVariable(string name, JsonElement rawValue) => writer.Write(name, JsonElementToObject(rawValue));
    }

    /// <summary>An authored item failure: a script error or a result that breaks its declarations.</summary>
    internal sealed class AutomaticScriptItemException(string code, string message) : Exception(message)
    {
        public string Code { get; } = code;
    }
}
