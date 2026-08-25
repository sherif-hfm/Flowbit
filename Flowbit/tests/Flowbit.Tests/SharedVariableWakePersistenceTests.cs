using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class SharedVariableWakePersistenceTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task FailedExpansionReturnsToPendingAndCanBeLeasedAgain()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
        await using var transaction = await db.Database.BeginTransactionAsync();

        // Isolate acquisition order inside the rollback-only transaction.
        await db.SharedVariableWakes.ExecuteUpdateAsync(setters => setters
            .SetProperty(wake => wake.Status, SharedVariableWakeStatuses.Completed)
            .SetProperty(wake => wake.LeaseToken, (Guid?)null)
            .SetProperty(wake => wake.LeasedBy, (string?)null)
            .SetProperty(wake => wake.LeaseExpiresAt, (DateTimeOffset?)null));

        var key = $"tests.retry.{Guid.NewGuid():N}";
        await repository.CreateAsync(
            new SharedVariableCreateCommand(
                key,
                "number",
                IsArray: false,
                Nullable: false,
                Validation: null,
                Description: null,
                HasValue: true,
                JsonSerializer.SerializeToElement(1),
                new SharedVariableCallerRecord(
                    SharedVariableCallerKinds.System,
                    "shared-variable-wake-test",
                    [],
                    []),
                SharedVariableSources.System,
                RequestId: null,
                RequestFingerprint: null,
                Reason: null),
            CancellationToken.None);

        var firstLeaseAt = DateTimeOffset.UtcNow.AddSeconds(1);
        var first = Assert.Single(await repository.LeaseWakeExpansionsAsync(
            new SharedVariableWakeLeaseRequest(
                "retry-test-1",
                MaxCount: 10,
                TimeSpan.FromMinutes(1),
                firstLeaseAt),
            CancellationToken.None));

        await repository.CompleteWakeExpansionAsync(
            new SharedVariableWakeFence(first.Id, first.LeaseToken, first.LeaseGeneration),
            "transient failure",
            CancellationToken.None);

        var retry = await db.SharedVariableWakes.AsNoTracking()
            .SingleAsync(wake => wake.Id == first.Id);
        Assert.Equal(SharedVariableWakeStatuses.Pending, retry.Status);
        Assert.Null(retry.LeaseToken);
        Assert.Null(retry.LeasedBy);
        Assert.Null(retry.LeaseExpiresAt);
        Assert.Equal("transient failure", retry.LastError);
        Assert.True(retry.AvailableAt > DateTimeOffset.UtcNow.AddMilliseconds(-100));

        var second = Assert.Single(await repository.LeaseWakeExpansionsAsync(
            new SharedVariableWakeLeaseRequest(
                "retry-test-2",
                MaxCount: 10,
                TimeSpan.FromMinutes(1),
                retry.AvailableAt.AddMilliseconds(1)),
            CancellationToken.None));
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.LeaseGeneration + 1, second.LeaseGeneration);
        Assert.Equal(first.AttemptCount + 1, second.AttemptCount);

        await repository.CompleteWakeExpansionAsync(
            new SharedVariableWakeFence(second.Id, second.LeaseToken, second.LeaseGeneration),
            error: null,
            CancellationToken.None);
        var completed = await db.SharedVariableWakes.AsNoTracking()
            .SingleAsync(wake => wake.Id == first.Id);
        Assert.Equal(SharedVariableWakeStatuses.Completed, completed.Status);
        Assert.NotNull(completed.CompletedAt);

        await transaction.RollbackAsync();
    }
}
