using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;

namespace Flowbit.Worker;

/// <summary>
/// Expands deployment-wide shared-variable revisions into fenced conditional
/// deliveries and processes each affected instance independently. PostgreSQL
/// leases make the loop restart-safe across worker replicas.
/// </summary>
public sealed class SharedVariableWakeDispatcher(
    IServiceScopeFactory scopeFactory,
    WorkerOptions options,
    TimeProvider timeProvider,
    ILogger<SharedVariableWakeDispatcher> logger) : BackgroundService
{
    private readonly string workerId =
        $"{Environment.MachineName}:{Environment.ProcessId}:shared:{Guid.NewGuid():N}";

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var didWork = await ProcessRoundAsync(stoppingToken);
                if (!didWork)
                {
                    await Task.Delay(
                        TimeSpan.FromMilliseconds(options.PollMilliseconds),
                        stoppingToken);
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown.
        }
    }

    private async Task<bool> ProcessRoundAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<SharedVariableWakeRecord> expansions;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider
                .GetRequiredService<ISharedVariableRepository>();
            expansions = await repository.LeaseWakeExpansionsAsync(
                LeaseRequest(),
                cancellationToken);
        }

        foreach (var expansion in expansions)
        {
            await ProcessExpansionAsync(expansion, cancellationToken);
        }

        IReadOnlyList<SharedVariableWakeDeliveryRecord> deliveries;
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider
                .GetRequiredService<ISharedVariableRepository>();
            deliveries = await repository.LeaseWakeDeliveriesAsync(
                LeaseRequest(),
                cancellationToken);
        }

        foreach (var delivery in deliveries)
        {
            await ProcessDeliveryAsync(delivery, cancellationToken);
        }

        return expansions.Count > 0 || deliveries.Count > 0;
    }

    private async Task ProcessExpansionAsync(
        SharedVariableWakeRecord wake,
        CancellationToken cancellationToken)
    {
        var fence = new SharedVariableWakeFence(
            wake.Id,
            wake.LeaseToken,
            wake.LeaseGeneration);
        string? error = null;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var repository = scope.ServiceProvider
                .GetRequiredService<ISharedVariableRepository>();
            await repository.ExpandWakeAsync(fence, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            logger.LogError(
                ex,
                "Shared-variable wake expansion {WakeId} failed.",
                wake.Id);
        }

        await using var completionScope = scopeFactory.CreateAsyncScope();
        var completionRepository = completionScope.ServiceProvider
            .GetRequiredService<ISharedVariableRepository>();
        await completionRepository.CompleteWakeExpansionAsync(
            fence,
            error,
            cancellationToken);
    }

    private async Task ProcessDeliveryAsync(
        SharedVariableWakeDeliveryRecord delivery,
        CancellationToken cancellationToken)
    {
        var fence = new SharedVariableWakeFence(
            delivery.Id,
            delivery.LeaseToken,
            delivery.LeaseGeneration);
        string? error = null;
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var engine = scope.ServiceProvider.GetRequiredService<WorkflowEngineService>();
            await engine.ProcessSharedVariableWakeDeliveryAsync(
                delivery,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            logger.LogError(
                ex,
                "Shared-variable wake delivery {DeliveryId} for instance {InstanceId} failed.",
                delivery.Id,
                delivery.InstanceId);
        }

        await using var completionScope = scopeFactory.CreateAsyncScope();
        var completionRepository = completionScope.ServiceProvider
            .GetRequiredService<ISharedVariableRepository>();
        await completionRepository.CompleteWakeDeliveryAsync(
            fence,
            error,
            cancellationToken);
    }

    private SharedVariableWakeLeaseRequest LeaseRequest() => new(
        workerId,
        options.BatchSize,
        options.LeaseDuration,
        timeProvider.GetUtcNow());
}
