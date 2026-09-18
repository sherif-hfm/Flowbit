using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Models;

/// <summary>
/// Execution-position projection returned by
/// <c>IWorkflowInstanceProjectionService.BuildExecutionAsync</c>. It carries the
/// five response-facing projection groups: execution positions,
/// multi-instance progress, gateway executions, complex gateway states, and
/// completion.
/// </summary>
public sealed record InstanceExecutionProjection(
    IReadOnlyList<ExecutionPositionDto> ExecutionPositions,
    IReadOnlyList<MultiInstanceProgressDto> MultiInstances,
    IReadOnlyList<GatewayExecutionDto> GatewayExecutions,
    IReadOnlyList<ComplexGatewayStateDto> ComplexGatewayStates,
    CompletionInfoDto? Completion);
