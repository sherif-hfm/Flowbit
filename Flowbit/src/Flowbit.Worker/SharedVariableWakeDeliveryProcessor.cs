using Flowbit.Service.Models;
using Flowbit.Service.Services;

namespace Flowbit.Worker;

/// <summary>
/// Keeps the worker scheduler independent of the workflow engine's concrete
/// implementation while preserving one dependency-injection scope per delivery.
/// </summary>
public interface ISharedVariableWakeDeliveryProcessor
{
    Task ProcessAsync(
        SharedVariableWakeDeliveryRecord delivery,
        CancellationToken cancellationToken);
}

internal sealed class SharedVariableWakeDeliveryProcessor(
    WorkflowEngineService engine) : ISharedVariableWakeDeliveryProcessor
{
    public Task ProcessAsync(
        SharedVariableWakeDeliveryRecord delivery,
        CancellationToken cancellationToken) =>
        engine.ProcessSharedVariableWakeDeliveryAsync(delivery, cancellationToken);
}
