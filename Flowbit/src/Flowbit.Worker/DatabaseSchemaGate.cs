using Flowbit.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;

namespace Flowbit.Worker;

/// <summary>
/// Migration identifiers this Worker build expects but the database lacks,
/// and applied identifiers this build does not contain.
/// </summary>
public sealed record DatabaseSchemaStatus(
    IReadOnlyList<string> PendingMigrations,
    IReadOnlyList<string> UnknownAppliedMigrations);

/// <summary>
/// Holds durable processing until every migration this Worker build contains
/// has been applied. Migrations must precede upgraded Workers; the gate turns
/// a premature start into a visible not-ready wait instead of failed jobs.
/// </summary>
public sealed class DatabaseSchemaGate(
    Func<CancellationToken, Task<DatabaseSchemaStatus>> readStatus,
    TimeProvider timeProvider,
    ILogger<DatabaseSchemaGate> logger,
    TimeSpan? recheckInterval = null) : BackgroundService
{
    private static readonly TimeSpan DefaultRecheckInterval = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan WarningInterval = TimeSpan.FromMinutes(1);

    private readonly TaskCompletionSource _ready =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _pendingMigrationCount = -1;

    public bool IsReady => _ready.Task.IsCompletedSuccessfully;

    /// <summary>
    /// Pending migrations seen by the latest successful check, or -1 before
    /// the migration history has been read.
    /// </summary>
    public int PendingMigrationCount => Volatile.Read(ref _pendingMigrationCount);

    public Task WaitUntilReadyAsync(CancellationToken cancellationToken) =>
        _ready.Task.WaitAsync(cancellationToken);

    public static async Task<DatabaseSchemaStatus> ReadMigrationStatusAsync(
        IServiceScopeFactory scopeFactory,
        CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AppDbContext>().Database;
        var applied = (await database.GetAppliedMigrationsAsync(cancellationToken))
            .ToHashSet(StringComparer.Ordinal);
        var known = database.GetMigrations().ToArray();
        return new DatabaseSchemaStatus(
            known.Where(migration => !applied.Contains(migration)).ToArray(),
            applied.Except(known, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Each kind of warning repeats at most once per WarningInterval.
        DateTimeOffset? lastPendingWarningAt = null;
        DateTimeOffset? lastFailureWarningAt = null;
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    var status = await readStatus(stoppingToken);
                    Volatile.Write(ref _pendingMigrationCount, status.PendingMigrations.Count);
                    if (status.PendingMigrations.Count == 0)
                    {
                        if (status.UnknownAppliedMigrations.Count > 0)
                        {
                            logger.LogWarning(
                                "The database has {Count} applied migrations that this Worker build does not contain: {Migrations}. Confirm this Worker version supports the newer schema.",
                                status.UnknownAppliedMigrations.Count,
                                string.Join(", ", status.UnknownAppliedMigrations));
                        }
                        _ready.TrySetResult();
                        return;
                    }

                    var now = timeProvider.GetUtcNow();
                    if (lastPendingWarningAt is null
                        || now - lastPendingWarningAt >= WarningInterval)
                    {
                        logger.LogWarning(
                            "Durable processing is paused until {Count} pending database migrations are applied: {Migrations}. The Worker does not apply migrations.",
                            status.PendingMigrations.Count,
                            string.Join(", ", status.PendingMigrations));
                        lastPendingWarningAt = now;
                    }
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    var now = timeProvider.GetUtcNow();
                    if (lastFailureWarningAt is null
                        || now - lastFailureWarningAt >= WarningInterval)
                    {
                        logger.LogWarning(
                            exception,
                            "Could not read the database migration history; durable processing stays paused.");
                        lastFailureWarningAt = now;
                    }
                }

                await Task.Delay(
                    recheckInterval ?? DefaultRecheckInterval,
                    timeProvider,
                    stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Normal host shutdown before the schema became ready.
        }
    }
}
