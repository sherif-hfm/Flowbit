using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Microsoft.Extensions.Caching.Memory;
using Flowbit.Service.Services;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed partial class AutomaticMultiInstancePersistenceTests(PostgresApiFixture fixture) : IAsyncLifetime
{
    private readonly List<long> ownedInstanceIds = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // This shared database also hosts worker acquisition tests. Retained
        // evidence is harmless, but a queued test job must not win their lease.
        await using var db = fixture.CreateDbContext();
        var jobs = new WorkflowJobRepository(db, fixture.DataSource);
        foreach (var instanceId in ownedInstanceIds)
            await jobs.CancelByInstanceAsync(instanceId, "testCleanup", default);
    }

    [Fact]
    public async Task AutomaticItemsPreserveIdentityWindowCandidatesAndHumanIsolation()
    {
        var counter = new ApiDbCommandCounter();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(fixture.DataSource, FlowbitDatabase.ConfigureProvider).AddInterceptors(counter).Options);
        var create = await CreateOwnerAsync(db, 3, 1);
        var repository = new AutomaticMultiInstanceRepository(db);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var body = await repository.CreateAsync(create, default);
        Assert.Equal(body.Id, (await repository.CreateAsync(create, default)).Id);
        Assert.True(await repository.EnterBodyWaitAsync(body.Id, create.ActivationId, null, default));
        Assert.True(await repository.EnterBodyWaitAsync(body.Id, create.ActivationId, null, default));
        Assert.False(await repository.ClearBodyWaitAsync(body.Id, Guid.NewGuid(), default));
        Assert.Equal(3, await db.MultiInstanceActivityItems.CountAsync(x => x.MultiInstanceExecutionId == body.Id));
        Assert.Equal(3, await db.NodeExecutions.CountAsync(x => x.MultiInstanceExecutionId == body.Id && x.ExecutionKind == "automaticActivityItem"));
        Assert.Empty(await db.UserTasks.Where(x => x.InstanceId == create.InstanceId).ToArrayAsync());
        var human = new WorkflowRuntimeRepository(db);
        Assert.Equal(body.Id, (await human.GetExecutionTokenAsync(create.TokenId, false, default))!.WaitingMultiInstanceExecutionId);
        Assert.Null(await human.GetMultiInstanceAsync(body.Id, false, default));
        Assert.Empty(await human.ListCurrentMultiInstancesAsync(create.InstanceId, default));
        Assert.Empty(await human.GetMultiInstanceProgressAsync([body.Id], default));

        var first = Assert.Single(await repository.AdmitAsync(body.Id, create.ActivationId, 1000, default));
        Assert.Equal(0, first.ItemIndex);
        Assert.Empty(await repository.AdmitAsync(body.Id, create.ActivationId, 1000, default));
        Assert.True(await repository.SaveStageAsync(body.Id, first.Id, create.ActivationId, Json("{\"frozen\":1}"), default));
        Assert.True(await repository.SaveCandidateAsync(body.Id, first.Id, create.ActivationId, Json("{\"variables\":{\"answer\":42}}"), default));
        Assert.True(await repository.BlockItemAsync(body.Id, first.Id, create.ActivationId, "output_version_conflict", "Publication conflict", default));
        Assert.Empty(await repository.AdmitAsync(body.Id, create.ActivationId, 1000, default));
        var blocked = await repository.GetItemAsync(body.Id, first.Id, false, default);
        Assert.Equal("active", blocked!.Status);
        Assert.Equal("publication", blocked.Phase);
        Assert.NotNull(blocked.Candidate);
        Assert.Equal(1, Assert.Single(await repository.GetProgressAsync(create.InstanceId, default)).Execution.FailedCount);
        Assert.True(await repository.CompleteItemAsync(body.Id, first.Id, create.ActivationId, Json("{\"answer\":42}"), null, 13, default));
        Assert.False(await repository.CompleteItemAsync(body.Id, first.Id, create.ActivationId, Json("{\"answer\":43}"), null, 13, default));
        await repository.SetControlStateAsync(body.Id, create.ActivationId, "dispatch", null, default);
        var second = Assert.Single(await repository.AdmitAsync(body.Id, create.ActivationId, 1000, default));
        Assert.Equal(1, second.ItemIndex);
        Assert.True(await repository.SaveCompletionIntentAsync(body.Id, create.ActivationId, Json("{\"boundaryNodeId\":30}"), default));
        Assert.False(await repository.SaveCompletionIntentAsync(body.Id, create.ActivationId, Json("{\"boundaryNodeId\":31}"), default));
        Assert.Equal(30, (await repository.GetAsync(body.Id, false, default))!.CompletionIntent!.Value.GetProperty("boundaryNodeId").GetInt32());
        Assert.Equal(2, await repository.CancelUnfinishedItemsAsync(body.Id, create.ActivationId, "instanceCancelled", default));
        Assert.True(await repository.SettleAsync(body.Id, create.ActivationId, "cancelled", "instanceCancelled", default));
        await human.SetExecutionTokenStatusAsync(create.TokenId, "cancelled", "instanceCancelled",
            new NodeExecutionCompletionRecord("cancelled", "instanceCancelled", null, null, null, new("system", [])), default);
        await db.SaveChangesAsync();
        Assert.Null((await human.GetExecutionTokenAsync(create.TokenId, false, default))!.WaitingMultiInstanceExecutionId);
        Assert.False(await repository.SaveCandidateAsync(body.Id, second.Id, create.ActivationId, Json("{}"), default));
        Assert.False(await repository.SaveStageAsync(body.Id, second.Id, create.ActivationId, Json("{}"), default));
        Assert.False(await repository.CompleteItemAsync(body.Id, second.Id, create.ActivationId, Json("{}"), null, 0, default));
        Assert.False(await repository.FailItemAsync(body.Id, second.Id, create.ActivationId, "lateFailure", "Late work", default));
        Assert.Empty(await repository.AdmitAsync(body.Id, create.ActivationId, 1, default));
        var progress = Assert.Single(await repository.GetProgressAsync(create.InstanceId, default));
        Assert.Equal(1, progress.Execution.CompletedCount);
        Assert.Equal(2, progress.Execution.CancelledCount);
        Assert.Equal(0, progress.ActiveCount);
        var page = await repository.GetItemsAsync(body.Id, 0, 2, false, default);
        Assert.Equal(3, page.TotalCount);
        Assert.All(page.Items, item => { Assert.Null(item.StagedInput); Assert.Null(item.Candidate); Assert.Null(item.Result); });
        Assert.Equal(42, (await repository.GetItemAsync(body.Id, first.Id, false, default))!.Result!.Value.GetProperty("answer").GetInt32());
        counter.Reset(captureReaderCommandTexts: true);
        var results = await repository.GetItemResultsAsync(body.Id, 0, 2, default);
        Assert.Equal(new[] { 0, 1 }, results.Select(item => item.ItemIndex));
        Assert.Equal(42, results[0].Result!.Value.GetProperty("answer").GetInt32());
        Assert.Null(results[1].Result);
        var resultSql = Assert.Single(counter.ReaderCommandTexts);
        Assert.DoesNotContain("StagedInputJson", resultSql);
        Assert.DoesNotContain("CandidateJson", resultSql);
        Assert.DoesNotContain("SharedWriteCorrelationsJson", resultSql);
        counter.Reset();
        var queries = new AutomaticMultiInstanceQueryRepository(db);
        var summaries = await queries.ListItemsAsync(body.Id, 2, 2, new(true, []), default);
        Assert.Equal(3, summaries!.TotalCount);
        Assert.Equal(2, Assert.Single(summaries.Items).ItemIndex);
        Assert.Null(await queries.GetItemAsync(body.Id, first.Id, new(false, []), default));
        Assert.Null(await queries.GetItemAsync(body.Id + 100000, first.Id, new(true, []), default));
        var payload = await queries.GetItemAsync(body.Id, first.Id, new(true, []), default);
        Assert.Equal(0, payload!.Item!.Value.GetInt32());
        await transaction.CommitAsync();
    }

    [Fact]
    public async Task EmptyBodyReservesCapacityAndKeepsSnapshotWithoutAnyJob()
    {
        await using var db = fixture.CreateDbContext();
        var create = await CreateOwnerAsync(db, 0, 0);
        var repository = new AutomaticMultiInstanceRepository(db);
        var jobs = new WorkflowJobRepository(db, fixture.DataSource);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var body = await repository.CreateAsync(create, default);
        Assert.True(await repository.EnterBodyWaitAsync(body.Id, create.ActivationId, null, default));
        Assert.Equal(1, await jobs.CountOpenByInstanceAsync(create.InstanceId, default));
        Assert.Empty(await db.NodeExecutions.Where(x => x.InstanceId == create.InstanceId).ToArrayAsync());
        Assert.Empty((await repository.GetItemsAsync(body.Id, 0, 50, false, default)).Items);
        Assert.Equal("[]", (await repository.GetInputSnapshotAsync(body.Id, default))!.Value.GetProperty("collection").GetRawText());
        Assert.True(await repository.SettleAsync(body.Id, create.ActivationId, "completed", "multiInstanceCompleted", default));
        Assert.True(await repository.ClearBodyWaitAsync(body.Id, create.ActivationId, default));
        Assert.Equal(0, await jobs.CountOpenByInstanceAsync(create.InstanceId, default));
        await transaction.CommitAsync();
        Assert.True(await db.WorkflowJobSnapshots.AnyAsync(x => x.Id == body.InputSnapshotId));
    }

    [Fact]
    public async Task DatabaseRefusesSecondOpenBodyAndCrossParentItemReferences()
    {
        await using var db = fixture.CreateDbContext();
        var create = await CreateOwnerAsync(db, 1, 1);
        long bodyId;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            bodyId = (await new AutomaticMultiInstanceRepository(db).CreateAsync(create, default)).Id;
            await tx.CommitAsync();
        }
        var original = await db.MultiInstanceExecutions.AsNoTracking().SingleAsync(x => x.Id == bodyId);
        original.Id = 0; original.ActivationId = Guid.NewGuid();
        db.MultiInstanceExecutions.Add(original);
        var duplicate = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, ((PostgresException)duplicate.InnerException!).SqlState);
        db.ChangeTracker.Clear();
        var other = await CreateOwnerAsync(db, 1, 1);
        var item = await db.MultiInstanceActivityItems.SingleAsync(x => x.MultiInstanceExecutionId == bodyId);
        var ownership = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE flowbit.multi_instance_activity_items SET \"InstanceId\" = {other.InstanceId} WHERE \"Id\" = {item.Id}"));
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, ownership.SqlState);
    }

    [Fact]
    public async Task MigrationValidatedLargeTableChecksAndSeededConcurrencySettings()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT count(*) FROM pg_constraint
            WHERE conname IN ('CK_workflow_jobs_automatic_control_shape', 'CK_workflow_jobs_automatic_item_shape',
                'CK_node_executions_execution_kind', 'CK_node_executions_multi_instance_shape') AND convalidated
            """, connection);
        Assert.Equal(4L, await command.ExecuteScalarAsync());
        await using var db = fixture.CreateDbContext();
        var settings = await db.EngineSettings.Where(x => x.Namespace == "Workflow.MultiInstance" &&
            (x.Key == "DefaultConcurrency" || x.Key == "MaxConcurrency"))
            .ToDictionaryAsync(x => x.Key, x => x.Value);
        Assert.Equal("4", settings["DefaultConcurrency"]);
        Assert.Equal("1000", settings["MaxConcurrency"]);
    }

    [Theory]
    [InlineData("output_version_conflict", true)]
    [InlineData("job_conflict", false)]
    public async Task PublicationRetryKeepsCandidatesSnapshotsAndOtherIncidentBackpressure(string incidentType, bool refresh)
    {
        await using var db = fixture.CreateDbContext();
        var create = await CreateOwnerAsync(db, 2, 2);
        var repository = new AutomaticMultiInstanceRepository(db);
        var jobs = new WorkflowJobRepository(db, fixture.DataSource);
        AutomaticMultiInstanceRecord body;
        var incidents = new List<WorkflowJobRecord>();
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            body = await repository.CreateAsync(create, default);
            var items = await repository.AdmitAsync(body.Id, create.ActivationId, 2, default);
            foreach (var item in items)
            {
                await repository.SaveStageAsync(body.Id, item.Id, create.ActivationId, Json("{\"input\":1}"), default);
                await repository.SaveCandidateAsync(body.Id, item.Id, create.ActivationId, Json("{\"accepted\":42}"), default);
                await repository.BlockItemAsync(body.Id, item.Id, create.ActivationId, "output_version_conflict", "Conflict", default);
                incidents.Add(await jobs.EnqueueIncidentAsync(new()
                {
                    InstanceId = create.InstanceId, TokenId = create.TokenId, WorkflowDefinitionId = create.WorkflowDefinitionId,
                    WorkflowKey = (await db.WorkflowInstances.FindAsync(create.InstanceId))!.WorkflowKey,
                    MultiInstanceExecutionId = body.Id, AutomaticMultiInstanceItemId = item.Id, ActivationId = create.ActivationId,
                    NodeId = create.NodeId, NodeName = create.NodeName, NodeType = create.ActivityKind,
                    Kind = "multiInstanceItem", Phase = "multiInstanceItem", QueueClass = "activity", DueAt = DateTimeOffset.UtcNow,
                    SnapshotId = body.InputSnapshotId
                }, incidentType, "Conflict", null, default));
            }
            await transaction.CommitAsync();
        }
        db.ChangeTracker.Clear();
        var retry = await jobs.RetryIncidentAsync(incidents[0].IncidentId!.Value, "admin", DateTimeOffset.UtcNow, default);
        Assert.Equal(refresh, retry!.PublicationFenceRefreshRequested);
        Assert.Equal(body.InputSnapshotId, retry.SnapshotId);
        Assert.Equal("itemIncident", (await repository.GetAsync(body.Id, false, default))!.AdmissionBlockedReason);
        Assert.Equal(42, (await repository.GetItemAsync(body.Id, retry.AutomaticMultiInstanceItemId!.Value, false, default))!
            .Candidate!.Value.GetProperty("accepted").GetInt32());
        db.ChangeTracker.Clear();
        await jobs.RetryIncidentAsync(incidents[1].IncidentId!.Value, "admin", DateTimeOffset.UtcNow, default);
        Assert.Null((await repository.GetAsync(body.Id, false, default))!.AdmissionBlockedReason);
        var page = await jobs.SearchJobsAsync(new WorkflowJobQuery { InstanceId = create.InstanceId }, default);
        Assert.All(page.Items, job => { Assert.NotNull(job.AutomaticMultiInstanceItemId); Assert.Equal(refresh, job.PublicationFenceRefreshRequested); });
    }

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    [Theory]
    [InlineData("serviceTask")]
    [InlineData("scriptTask")]
    [InlineData("callActivity")]
    public async Task AutomaticTokenEntryNeverCreatesAParentLedgerVisit(string activityKind)
    {
        await using var db = fixture.CreateDbContext();
        var owner = await CreateOwnerAsync(db, 1, 1);
        var key = (await db.WorkflowDefinitions.FindAsync(owner.WorkflowDefinitionId))!.WorkflowKey;
        var runtime = new WorkflowRuntimeRepository(db);
        var node = new CurrentNodeSnapshot(20, "Automatic body", null, activityKind, [], false, false, null,
            IsMultiInstance: true, AsyncBefore: activityKind != "callActivity");
        var actor = new NodeExecutionActorRecord("starter", []);
        var instance = await runtime.AddInstanceAsync(owner.WorkflowDefinitionId, key, null, null, null,
            node, actor.User, actor.Roles, default);
        var single = await runtime.AddExecutionTokenAsync(instance.Id, node, null, null, actor, default);
        Assert.Null(single.CurrentNodeExecutionId);
        var batch = await runtime.AddExecutionTokensAsync(instance.Id,
            [new(node, null, null, actor), new(node with { Id = 21 }, null, null, actor)], default);
        Assert.All(batch, token => Assert.Null(token.CurrentNodeExecutionId));
        Assert.Empty(await db.NodeExecutions.Where(visit => visit.InstanceId == instance.Id).ToArrayAsync());
        Assert.Empty(await db.UserTasks.Where(task => task.InstanceId == instance.Id).ToArrayAsync());

        // A normal visit still exists and closes normally when the graph moves
        // into an automatic body. The new activation adds no ordinary visit.
        var ordinary = await runtime.AddExecutionTokenAsync(instance.Id,
            new CurrentNodeSnapshot(10, "Ordinary task", null, "task", [], false, false, null), null, null, actor, default);
        Assert.NotNull(ordinary.CurrentNodeExecutionId);
        await runtime.UpdateExecutionTokenAsync(ordinary.Id, node, "active", null, 101, null, null, actor, null, default);
        Assert.Null((await runtime.GetExecutionTokenAsync(ordinary.Id, false, default))!.CurrentNodeExecutionId);
        var visit = Assert.Single(await db.NodeExecutions.Where(row => row.InstanceId == instance.Id).ToArrayAsync());
        Assert.Equal("task", visit.NodeType);
        Assert.Equal("completed", visit.Status);
    }

    [Fact]
    public async Task LateItemResultCannotPublishAfterParentActivationChanges()
    {
        await using var db = fixture.CreateDbContext();
        var create = await CreateOwnerAsync(db, 1, 1);
        var repository = new AutomaticMultiInstanceRepository(db);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var body = await repository.CreateAsync(create, default);
        var item = Assert.Single(await repository.AdmitAsync(body.Id, create.ActivationId, 1, default));
        Assert.True(await repository.SaveStageAsync(body.Id, item.Id, create.ActivationId, Json("{}"), default));
        Assert.True(await repository.SaveCandidateAsync(body.Id, item.Id, create.ActivationId, Json("{\"accepted\":1}"), default));
        (await db.ExecutionTokens.FindAsync(create.TokenId))!.ActivationId = Guid.NewGuid();
        await db.SaveChangesAsync();
        Assert.False(await repository.CompleteItemAsync(body.Id, item.Id, create.ActivationId, Json("{\"answer\":1}"), null, 1, default));
        Assert.False(await repository.SaveCandidateAsync(body.Id, item.Id, create.ActivationId, Json("{\"accepted\":2}"), default));
        Assert.Empty(await repository.AdmitAsync(body.Id, create.ActivationId, 1, default));
        // Cleanup of the superseded activation still runs and leaves its evidence.
        Assert.Equal(1, await repository.CancelUnfinishedItemsAsync(body.Id, create.ActivationId, "instanceCancelled", default));
        Assert.True(await repository.SettleAsync(body.Id, create.ActivationId, "cancelled", "instanceCancelled", default));
        Assert.Null((await repository.GetItemAsync(body.Id, item.Id, false, default))!.Result);
        await transaction.CommitAsync();
    }

    [Fact]
    public async Task OpenCallBodyPinProtectsTargetBeforeAnyInvocationExists()
    {
        await using var db = fixture.CreateDbContext();
        var create = (await CreateOwnerAsync(db, 1, 1)) with { ActivityKind = "callActivity" };
        (await db.ExecutionTokens.FindAsync(create.TokenId))!.NodeType = "callActivity";
        var targetKey = $"pinned-mi-target-{Guid.NewGuid():N}";
        var target = new WorkflowDefinitionEntity { Name = targetKey, WorkflowKey = targetKey, Version = 1,
            IsPublished = true, Definition = new WorkflowModel { Id = targetKey, Name = targetKey } };
        db.WorkflowDefinitions.Add(target);
        await db.SaveChangesAsync();
        var repository = new AutomaticMultiInstanceRepository(db);
        AutomaticMultiInstanceRecord body;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            // A nonempty call body cannot be created without its pin.
            await Assert.ThrowsAsync<ArgumentException>(() => repository.CreateAsync(create, default));
            body = await repository.CreateAsync(create with { PinnedChildDefinitionId = target.Id, PinnedChildStartEventId = 1 }, default);
            Assert.Equal((target.Id, 1, "pinned"), (body.PinnedChildDefinitionId!.Value, body.PinnedChildStartEventId!.Value, body.PinState!));
            await transaction.CommitAsync();
        }
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var definitions = new WorkflowDefinitionRepository(db, cache);
        Assert.False((await definitions.AssessDeletionAsync(target.Id, default))!.Allowed);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => definitions.DeleteAsync(target.Id, default));
        await Assert.ThrowsAsync<WorkflowDomainException>(() => definitions.SetPublishedAsync(target.Id, false, default));
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            await repository.CancelUnfinishedItemsAsync(body.Id, body.ActivationId, "instanceCancelled", default);
            await repository.SettleAsync(body.Id, body.ActivationId, "cancelled", "instanceCancelled", default);
            await transaction.CommitAsync();
        }
        Assert.Equal("released", (await repository.GetAsync(body.Id, false, default))!.PinState);
        Assert.True(await definitions.SetPublishedAsync(target.Id, false, default));
        Assert.False((await definitions.AssessDeletionAsync(target.Id, default))!.Allowed);
    }

    [Fact]
    public async Task DatabaseRefusesUnpinnedCallBodiesAndHalfOwnedInvocations()
    {
        await using var db = fixture.CreateDbContext();
        var create = (await CreateOwnerAsync(db, 1, 1)) with { ActivityKind = "callActivity" };
        (await db.ExecutionTokens.FindAsync(create.TokenId))!.NodeType = "callActivity";
        await db.SaveChangesAsync();
        var repository = new AutomaticMultiInstanceRepository(db);
        await using var transaction = await db.Database.BeginTransactionAsync();
        var body = await repository.CreateAsync(create with
            { PinnedChildDefinitionId = create.WorkflowDefinitionId, PinnedChildStartEventId = 1 }, default);
        await db.Database.ExecuteSqlRawAsync("SAVEPOINT pin");
        var pin = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE flowbit.multi_instance_executions SET \"PinnedChildDefinitionId\" = NULL WHERE \"Id\" = {body.Id}"));
        Assert.Equal("CK_multi_instance_executions_call_pin_shape", pin.ConstraintName);
        await db.Database.ExecuteSqlRawAsync("ROLLBACK TO SAVEPOINT pin");
        var released = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE flowbit.multi_instance_executions SET \"PinState\" = 'released' WHERE \"Id\" = {body.Id}"));
        Assert.Equal("CK_multi_instance_executions_call_pin_shape", released.ConstraintName);
        await db.Database.ExecuteSqlRawAsync("ROLLBACK TO SAVEPOINT pin");
        var half = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO flowbit.call_activity_executions
                ("ParentInstanceId", "ParentTokenId", "ActivationId", "ParentNodeId", "RootInstanceId", "ChildDepth",
                 "ParentDefinitionId", "TargetWorkflowKey", "State", "MultiInstanceExecutionId")
            VALUES ({create.InstanceId}, {create.TokenId}, {Guid.NewGuid()}, {create.NodeId}, {create.InstanceId}, 1,
                    {create.WorkflowDefinitionId}, 'target', 'queued', {body.Id})
            """));
        Assert.Equal("CK_call_activity_executions_automatic_item_pair", half.ConstraintName);
        // Deliberately roll back test-owned runtime state.
    }

    [Fact]
    public async Task SettledBodyRetainsItsOwningDefinitionAfterTheInstanceChangesVersion()
    {
        await using var db = fixture.CreateDbContext();
        var create = await CreateOwnerAsync(db, 0, 0);
        var repository = new AutomaticMultiInstanceRepository(db);
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var body = await repository.CreateAsync(create, default);
            Assert.True(await repository.SettleAsync(body.Id, body.ActivationId, "completed", "multiInstanceCompleted", default));
            await transaction.CommitAsync();
        }
        var original = await db.WorkflowDefinitions.SingleAsync(row => row.Id == create.WorkflowDefinitionId);
        var next = new WorkflowDefinitionEntity
        {
            WorkflowKey = original.WorkflowKey, Name = original.Name, Version = 2,
            Definition = original.Definition
        };
        db.WorkflowDefinitions.Add(next);
        await db.SaveChangesAsync();
        (await db.WorkflowInstances.FindAsync(create.InstanceId))!.WorkflowDefinitionId = next.Id;
        await db.SaveChangesAsync();
        Assert.Empty(await db.NodeExecutions.Where(row => row.WorkflowDefinitionId == original.Id).ToArrayAsync());
        Assert.Empty(await db.WorkflowJobs.Where(row => row.WorkflowDefinitionId == original.Id).ToArrayAsync());
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var definitions = new WorkflowDefinitionRepository(db, cache);
        Assert.False((await definitions.AssessDeletionAsync(original.Id, default))!.Allowed);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => definitions.DeleteAsync(original.Id, default));
        Assert.True(await db.WorkflowDefinitions.AnyAsync(row => row.Id == original.Id));
    }

    private async Task<AutomaticMultiInstanceCreateRecord> CreateOwnerAsync(Flowbit.Infrastructure.Data.AppDbContext db, int count, int concurrency)
    {
        var key = $"automatic-mi-{Guid.NewGuid():N}";
        var definition = new WorkflowDefinitionEntity { Name = key, WorkflowKey = key, Version = 1,
            Definition = new WorkflowModel { Id = key, Name = key } };
        db.WorkflowDefinitions.Add(definition);
        await db.SaveChangesAsync();
        var instance = new WorkflowInstanceEntity { WorkflowDefinitionId = definition.Id, WorkflowKey = key,
            Status = "running", StartedBy = "starter" };
        db.WorkflowInstances.Add(instance);
        await db.SaveChangesAsync();
        ownedInstanceIds.Add(instance.Id);
        var token = new ExecutionTokenEntity { InstanceId = instance.Id, NodeId = 2, NodeName = "Automatic work", NodeType = "serviceTask" };
        db.ExecutionTokens.Add(token);
        await db.SaveChangesAsync();
        return new() { InstanceId = instance.Id, TokenId = token.Id, WorkflowDefinitionId = definition.Id,
            ActivationId = token.ActivationId, NodeId = 2, NodeName = token.NodeName, ActivityKind = "serviceTask",
            Mode = concurrency == 1 ? "sequential" : "parallel", ResultVariable = "results", AggregateScope = "instance",
            AggregateValueVersion = 0, MaxInstances = 1000, EffectiveConcurrency = concurrency, TotalCount = count,
            ResultBytes = 2, InputSnapshot = JsonSerializer.SerializeToElement(new { collection = Enumerable.Range(0, count).ToArray() }) };
    }
}
