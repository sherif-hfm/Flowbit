using System.Text.Json;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;

namespace Flowbit.Service.Abstractions;

/// <summary>
/// Scope-aware variable coordinator used by the workflow engine. It keeps the
/// existing instance history store and the deployment-wide shared catalog as
/// separate persistence models while presenting one alias map to expressions.
/// </summary>
public interface IWorkflowVariableStore
{
    Task<Dictionary<string, JsonElement>> MergeEffectiveValuesAsync(
        WorkflowModel definition,
        IReadOnlyDictionary<string, JsonElement> instanceValues,
        bool lockSharedValues,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<WorkflowVariableWriteResult>> WriteAsync(
        WorkflowModel definition,
        long workflowDefinitionId,
        long instanceId,
        IReadOnlyCollection<WorkflowVariableWrite> writes,
        ActorContext actor,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SharedVariableBindingMetadataDto>> DescribeBindingsAsync(
        WorkflowModel definition,
        CancellationToken cancellationToken);

    Task<IReadOnlyDictionary<string, long>> LoadSharedRevisionsAsync(
        WorkflowModel definition,
        bool lockForUpdate,
        CancellationToken cancellationToken);
}
