using Flowbit.Service.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// Every test here leaves no unfinished delivery job behind: other classes sharing the
/// database lease one specific job and would receive it instead.
/// </summary>
public sealed partial class SignalThrowRuntimeTests : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => SignalDeliveryJobLeftovers.RetireAndAssertNoneAsync(fixture);
}

/// <summary>
/// Signal tests share the fixture's database with classes whose helpers lease one specific
/// job, so a delivery job a signal test leaves queued, or leased and abandoned, makes a later
/// class fail. A leftover is retired, so no later class receives it, and then reported, so
/// the test that left it fails instead.
/// </summary>
internal static class SignalDeliveryJobLeftovers
{
    /// <summary>
    /// Retires leftovers without failing, for the EVENT-04 classes whose tests leave
    /// deliveries undelivered on purpose, such as a broadcast still delivering for a check.
    /// </summary>
    public static async Task RetireAsync(PostgresApiFixture fixture)
    {
        await using var db = fixture.CreateDbContext();
        var horizon = DateTimeOffset.UtcNow.AddYears(1);
        var distant = DateTimeOffset.UtcNow.AddYears(10);
        await db.WorkflowJobs
            .Where(job => job.Kind == WorkflowJobKinds.SignalBroadcastDeliver
                && (job.Status == WorkflowJobStatuses.Queued || job.Status == WorkflowJobStatuses.Retry
                    || job.Status == WorkflowJobStatuses.Running || job.Status == WorkflowJobStatuses.ResultReady)
                && job.DueAt < horizon)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.DueAt, distant)
                .SetProperty(job => job.LeaseExpiresAt, job => job.LeaseExpiresAt == null ? null : distant));
    }

    public static async Task RetireAndAssertNoneAsync(PostgresApiFixture fixture)
    {
        await using var db = fixture.CreateDbContext();
        var horizon = DateTimeOffset.UtcNow.AddYears(1);
        var leftovers = await db.WorkflowJobs
            .Where(job => job.Kind == WorkflowJobKinds.SignalBroadcastDeliver
                && (job.Status == WorkflowJobStatuses.Queued || job.Status == WorkflowJobStatuses.Retry
                    || job.Status == WorkflowJobStatuses.Running || job.Status == WorkflowJobStatuses.ResultReady)
                && job.DueAt < horizon)
            .Select(job => job.Id)
            .ToListAsync();
        if (leftovers.Count == 0)
        {
            return;
        }

        var distant = DateTimeOffset.UtcNow.AddYears(10);
        await db.WorkflowJobs
            .Where(job => leftovers.Contains(job.Id))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.DueAt, distant)
                .SetProperty(job => job.LeaseExpiresAt, job => job.LeaseExpiresAt == null ? null : distant));
        Assert.Fail($"The test left unfinished signal delivery jobs: {string.Join(", ", leftovers)}.");
    }
}
