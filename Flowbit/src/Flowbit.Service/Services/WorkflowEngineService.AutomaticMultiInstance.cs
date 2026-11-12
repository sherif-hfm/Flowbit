using System.Text.Json;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

public sealed partial class WorkflowEngineService
{
    private async Task ProcessAutomaticMultiInstanceControlJobAsync(WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence, CancellationToken cancellationToken)
    {
        if (automaticMultiInstances is null || automaticMultiInstanceCoordinator is null
            || lease.Job.MultiInstanceExecutionId is not long executionId)
            throw new WorkflowJobInvariantException("The automatic control job has no body coordinator or ownership.");
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var instance = await GetInstanceForJobUpdateAsync(lease.Job.InstanceId!.Value, cancellationToken)
            ?? throw new WorkflowConflictException("The automatic body instance no longer exists.");
        var token = await runtime.GetExecutionTokenAsync(lease.Job.TokenId!.Value, true, cancellationToken);
        var body = await automaticMultiInstances.GetAsync(executionId, true, cancellationToken);
        if (instance.Status != WorkflowInstanceStatuses.Running || token is null || body is null
            || token.Status != ExecutionTokenRecordStatuses.Active || token.ActivationId != lease.Job.ActivationId
            || token.WaitingMultiInstanceExecutionId != executionId || body.TokenId != token.Id
            || body.Status != MultiInstanceRecordStatuses.Active || body.WorkflowDefinitionId != instance.WorkflowDefinitionId)
            throw new WorkflowConflictException("The automatic control job no longer owns its body.");
        var currentJob = await jobs.GetForUpdateAsync(lease.Job.Id, cancellationToken)
            ?? throw new WorkflowConflictException("The automatic control job no longer exists.");
        EnsureJobFence(currentJob, fence);
        var workflow = await GetWorkflowAsync(body.WorkflowDefinitionId, cancellationToken);
        var node = GetFlowNode(workflow.Definition, body.NodeId);
        var actor = ReadJobActor(currentJob);
        if (currentJob.Kind == WorkflowJobKinds.MultiInstanceDispatch)
        {
            var configured = await engineSettings.GetByKeyAsync("Workflow.MultiInstance.MaxInstances", cancellationToken);
            var template = BuildJobCreate(instance, token, node, WorkflowJobKinds.MultiInstanceItem,
                WorkflowJobKinds.MultiInstanceItem, actor, timeProvider.GetUtcNow());
            await automaticMultiInstanceCoordinator.DispatchAsync(body.Id, body.ActivationId, fence, template,
                WorkflowJobCapacity.ResolveOpenJobLimit(configured?.Value), cancellationToken);
        }
        else
        {
            await automaticMultiInstanceCoordinator.CompleteBodyAsync(body.Id, body.ActivationId, fence,
                workflow.Definition, actor, CreateAutomaticMultiInstanceLifecycle(instance, workflow.Definition, actor),
                cancellationToken);
        }
        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private void EnsureAutomaticMultiInstanceRuntimeAvailable(FlowNodeModel node) =>
        EnsureAutomaticMultiInstanceRuntimeAvailable(node, automaticMultiInstanceOptions?.Enabled == true);

    // A body runs only on a host whose release switch is open (Development with
    // WorkflowAutomaticMultiInstance:AllowInDevelopment) and only through its kind's
    // installed adapter. Otherwise it fails closed at every entry path, so a body
    // published elsewhere cannot run here and can never fall through to one
    // ordinary activity.
    internal static void EnsureAutomaticMultiInstanceRuntimeAvailable(FlowNodeModel node, bool released)
    {
        if (!AutomaticMultiInstanceRules.IsAutomaticBody(node)) return;
        if (!released)
            throw new WorkflowDomainException(
                $"Automatic multi-instance node #{node.Id} cannot run on this host: automatic multi-instance runs only on a Development host with the release switch open.");
        if (!AutomaticMultiInstanceRules.IsAdapterInstalled(node.Type))
            throw new WorkflowDomainException(
                $"Automatic multi-instance node #{node.Id} cannot execute until its activity adapters and runtime integration are installed.");
    }

    internal static Dictionary<string, JsonElement> CreateAutomaticLocalBuffer(
        IReadOnlyList<MultiInstanceOutputVariableModel> outputs,
        IReadOnlyDictionary<string, JsonElement> context) =>
        ResolveVariables(outputs.Select(AutomaticMultiInstanceRules.ToVariable).ToArray(), null,
            new Dictionary<string, JsonElement>(context, StringComparer.OrdinalIgnoreCase),
            materializeNullableNullDefaults: true);

    internal static JsonElement ValidateAutomaticLocalResult(
        IReadOnlyList<MultiInstanceOutputVariableModel> outputs,
        IReadOnlyDictionary<string, JsonElement> candidate,
        IReadOnlyDictionary<string, JsonElement> context)
    {
        var declarations = outputs.Select(AutomaticMultiInstanceRules.ToVariable).ToArray();
        var values = new Dictionary<string, JsonElement>(candidate, StringComparer.OrdinalIgnoreCase);
        var names = declarations.Select(output => output.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (values.Keys.Any(name => !names.Contains(name)))
            throw new AutomaticMultiInstanceValueException(AutomaticMultiInstanceValues.ResultInvalid);
        foreach (var output in declarations)
        {
            if (!values.ContainsKey(output.Name) && output.Nullable)
                values[output.Name] = JsonSerializer.SerializeToElement<object?>(null);
            if (!values.TryGetValue(output.Name, out var value)
                || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined && !output.Nullable)
                throw new AutomaticMultiInstanceValueException(AutomaticMultiInstanceValues.ResultInvalid);
        }
        ValidateVariableValues(declarations, values, enforceRequired: false);
        ValidateResolvedVariableRules(declarations, values,
            new Dictionary<string, JsonElement>(context, StringComparer.OrdinalIgnoreCase));
        return JsonSerializer.SerializeToElement(values);
    }

    internal static void ValidateAutomaticAggregate(VariableModel target, JsonElement value,
        IReadOnlyDictionary<string, JsonElement> context)
    {
        try
        {
            var values = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase) { [target.Name] = value };
            ValidateVariableValues([target], values, enforceRequired: false);
            ValidateResolvedVariableRules([target], values,
                new Dictionary<string, JsonElement>(context, StringComparer.OrdinalIgnoreCase));
        }
        catch (WorkflowDomainException)
        {
            // Authored expression text and values must not leak into a body's diagnostic.
            throw new AutomaticMultiInstanceValueException(AutomaticMultiInstanceValues.ResultInvalid);
        }
    }
}
