using System.Text.Json;
using Flowbit.Infrastructure.Data.Migrations;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class ActorAuditClaimsPersistenceTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task SequentialChildrenCaptureCreationActivationCompletionAndBulkCancellationActors()
    {
        await using var db = fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var definition = NewDefinition();
        db.WorkflowDefinitions.Add(definition);
        await db.SaveChangesAsync();
        var repository = new WorkflowRuntimeRepository(db);
        var creator = Actor("creator", "creation");
        var instance = await repository.AddInstanceAsync(definition.Id, definition.WorkflowKey,
            null, null, null, Snapshot(1, "startEvent"), creator.User, creator.Roles,
            CancellationToken.None, creator.AuditClaims);
        var node = Snapshot(2, "userTask") with { IsMultiInstance = true };
        var token = await repository.AddExecutionTokenAsync(instance.Id, node, null, 1,
            creator, CancellationToken.None);
        var execution = await repository.AddMultiInstanceAsync(instance.Id, token.Id, node,
            new MultiInstanceModel
            {
                Mode = "sequential", Source = "cardinality", CardinalityExpression = "3",
                ResultVariable = "results"
            }, [null, null, null], [10], creator, CancellationToken.None);
        var tasks = await db.UserTasks.Where(task => task.MultiInstanceExecutionId == execution.Id)
            .OrderBy(task => task.ItemIndex).ToListAsync();
        var entries = await db.NodeExecutions.Where(entry => entry.MultiInstanceExecutionId == execution.Id)
            .OrderBy(entry => entry.ItemIndex).ToListAsync();
        Assert.Equal(3, entries.Count);
        Assert.All(entries, entry => AssertClaims(entry.TriggeredByClaimsJson, "creation"));
        Assert.Null(entries[1].StartedAt);

        var voter = Actor("voter", "vote");
        await repository.CompleteMultiInstanceItemAsync(tasks[0].Id, 10, voter.User!, voter.Roles,
            [], CancellationToken.None, actorClaims: voter.AuditClaims);
        await repository.ActivateNextMultiInstanceItemAsync(execution.Id, voter, CancellationToken.None);
        await db.SaveChangesAsync();
        AssertClaims(entries[0].TriggeredByClaimsJson, "creation");
        AssertClaims(entries[0].CompletedByClaimsJson, "vote");
        Assert.Equal("voter", entries[1].TriggeredBy);
        AssertClaims(entries[1].TriggeredByClaimsJson, "vote");
        Assert.NotNull(entries[1].StartedAt);

        var interrupter = Actor("interrupter", "interrupt");
        await repository.CloseMultiInstanceAsync(execution.Id, 10, "interrupt", interrupter,
            CancellationToken.None);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var stored = await db.NodeExecutions.AsNoTracking()
            .Where(entry => entry.MultiInstanceExecutionId == execution.Id)
            .OrderBy(entry => entry.ItemIndex).ToListAsync();
        AssertClaims(stored[0].CompletedByClaimsJson, "vote");
        Assert.Equal(NodeExecutionStatuses.Completed, stored[0].Status);
        Assert.All(stored.Skip(1), entry =>
        {
            Assert.Equal(NodeExecutionStatuses.Cancelled, entry.Status);
            Assert.Equal("interrupter", entry.CompletedBy);
            AssertClaims(entry.CompletedByClaimsJson, "interrupt");
        });
        AssertClaims(stored[1].TriggeredByClaimsJson, "vote");
        AssertClaims(stored[2].TriggeredByClaimsJson, "creation");
        Assert.Null(stored[2].StartedAt);
    }

    [Fact]
    public async Task AdministrativeCompletionOfPendingChildCapturesBothActorSnapshots()
    {
        await using var db = fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var definition = NewDefinition();
        db.WorkflowDefinitions.Add(definition);
        await db.SaveChangesAsync();
        var repository = new WorkflowRuntimeRepository(db);
        var creator = Actor("creator", "creation");
        var instance = await repository.AddInstanceAsync(definition.Id, definition.WorkflowKey,
            null, null, null, Snapshot(1, "startEvent"), creator.User, creator.Roles,
            CancellationToken.None, creator.AuditClaims);
        var node = Snapshot(2, "userTask") with { IsMultiInstance = true };
        var token = await repository.AddExecutionTokenAsync(instance.Id, node, null, 1,
            creator, CancellationToken.None);
        var execution = await repository.AddMultiInstanceAsync(instance.Id, token.Id, node,
            new MultiInstanceModel
            {
                Mode = "sequential", Source = "cardinality", CardinalityExpression = "2",
                ResultVariable = "results"
            }, [null, null], [10], creator, CancellationToken.None);
        var pending = await db.UserTasks.SingleAsync(task =>
            task.MultiInstanceExecutionId == execution.Id && task.ItemIndex == 1);
        var administrator = Actor("administrator", "administration");
        await repository.CompleteMultiInstanceItemAsync(pending.Id, 10, administrator.User!,
            administrator.Roles, [], CancellationToken.None,
            completionKind: NodeExecutionCompletionReasons.AdministrativeAction,
            actorClaims: administrator.AuditClaims);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();
        var entry = await db.NodeExecutions.AsNoTracking().SingleAsync(row => row.UserTaskId == pending.Id);
        Assert.Equal(NodeExecutionStatuses.Completed, entry.Status);
        Assert.Equal(entry.StartedAt, entry.CompletedAt);
        Assert.Equal("administrator", entry.TriggeredBy);
        Assert.Equal("administrator", entry.CompletedBy);
        AssertClaims(entry.TriggeredByClaimsJson, "administration");
        AssertClaims(entry.CompletedByClaimsJson, "administration");
    }

    [Fact]
    public async Task AdditiveMigrationKeepsExistingHistoryAndExecutionSnapshotsUnavailable()
    {
        await using var db = fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var definition = NewDefinition();
        db.WorkflowDefinitions.Add(definition);
        await db.SaveChangesAsync();
        var repository = new WorkflowRuntimeRepository(db);
        var instance = await repository.AddInstanceAsync(definition.Id, definition.WorkflowKey,
            null, null, null, Snapshot(1, "startEvent"), "legacy", [], CancellationToken.None);
        await repository.AddHistoryAsync(instance.Id, null, 1, 1, "legacy", null, "start",
            CancellationToken.None);
        await db.SaveChangesAsync();

        var migration = new AddActorAuditClaims();
        Assert.Equal(3, migration.UpOperations.Count);
        Assert.All(migration.UpOperations, operation =>
        {
            var column = Assert.IsType<AddColumnOperation>(operation);
            Assert.Equal("jsonb", column.ColumnType);
            Assert.True(column.IsNullable);
            Assert.Null(column.DefaultValue);
            Assert.Null(column.DefaultValueSql);
        });
        var generator = db.GetService<IMigrationsSqlGenerator>();
        foreach (var operationGroup in new[] { migration.DownOperations, migration.UpOperations })
        {
            foreach (var command in generator.Generate(operationGroup, db.Model))
            {
                await db.Database.ExecuteSqlRawAsync(command.CommandText);
            }
        }
        db.ChangeTracker.Clear();
        var history = Assert.Single(await repository.ListHistoryAsync(instance.Id, CancellationToken.None));
        Assert.Equal("legacy", history.PerformedBy);
        Assert.Null(history.ActorClaims);
        var entry = await db.NodeExecutions.SingleAsync(row => row.InstanceId == instance.Id);
        Assert.Equal("legacy", entry.TriggeredBy);
        Assert.Null(entry.TriggeredByClaimsJson);
        Assert.Null(entry.CompletedByClaimsJson);
    }

    private static NodeExecutionActorRecord Actor(string user, string value) => new(user, [])
    {
        AuditClaims = new Dictionary<string, string[]> { ["department"] = [value, "shared"] }
    };

    private static void AssertClaims(JsonDocument? document, string value)
    {
        Assert.NotNull(document);
        Assert.Equal(new[] { value, "shared" }, document.RootElement.GetProperty("department")
            .EnumerateArray().Select(item => item.GetString()).ToArray());
    }

    private static CurrentNodeSnapshot Snapshot(int id, string type) =>
        new(id, type, null, type, [], false, false, null);

    private static WorkflowDefinitionEntity NewDefinition()
    {
        var key = $"actor-audit-persistence-{Guid.NewGuid():N}";
        return new WorkflowDefinitionEntity
        {
            Name = key, WorkflowKey = key, Version = 1, IsPublished = true,
            Definition = new WorkflowModel
            {
                Id = key, Name = key,
                FlowNodes = [new() { Id = 1, Type = "startEvent" }, new() { Id = 2, Type = "userTask" }, new() { Id = 3, Type = "endEvent" }],
                SequenceFlows = [new() { Id = 1, SourceRef = 1, TargetRef = 2 }, new() { Id = 10, SourceRef = 2, TargetRef = 3 }]
            }
        };
    }
}
