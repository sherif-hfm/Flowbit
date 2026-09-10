using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class RetentionRepositoryTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task OperationalDefaultsInitializeOnceAndCannotOverwriteSavedKeepForever()
    {
        await using var scope = await Scope.CreateAsync(fixture);
        await using (var db = fixture.CreateDbContext())
            await db.RetentionPolicies.Where(p => p.Category == RetentionCategories.CompletedJobs
                || p.Category == RetentionCategories.ResolvedIncidents)
                .ExecuteUpdateAsync(set => set.SetProperty(p => p.IsInitialized, false));
        await scope.Repository.InitializeAsync(30, 90, default);
        var policies = (await scope.Repository.GetAsync(default)).Policies;
        Assert.Equal(30, policies.Single(p => p.Category == RetentionCategories.CompletedJobs).RetentionDays);
        Assert.Equal(90, policies.Single(p => p.Category == RetentionCategories.ResolvedIncidents).RetentionDays);
        var previous = policies.Single(p => p.Category == RetentionCategories.CompletedJobs);
        await scope.PolicyAsync(RetentionCategories.CompletedJobs, null);
        await scope.Repository.InitializeAsync(3, 9, default);
        Assert.Null((await scope.Repository.GetAsync(default)).Policies.Single(p => p.Category == RetentionCategories.CompletedJobs).RetentionDays);
        // Persisted policies no longer depend on legacy bootstrap inputs.
        await scope.Repository.InitializeAsync(-1, int.MaxValue, default);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => scope.Repository.UpdatePolicyAsync(
            previous.Category, new(1, previous.Revision), "test", default));
        await Assert.ThrowsAsync<WorkflowDomainException>(() => scope.Repository.UpdatePolicyAsync(
            "unknown", new(1, 1), "test", default));
    }

    [Fact]
    public async Task HistoryUsesFinishClockAndPreservesPermanentMessageReceiptsAndFlowSummaries()
    {
        await using var scope = await Scope.CreateAsync(fixture);
        var old = scope.Now.AddDays(-60);
        var (definition, instances) = await scope.WorkflowAsync("completed", "cancelled", "faulted", "running", "completed");
        long pinnedId;
        await using (var db = fixture.CreateDbContext())
        {
            var recent = await db.WorkflowInstances.FindAsync(instances[4]);
            recent!.FinishedAt = scope.Now.AddDays(-2);
            var histories = instances.Select(id => new InstanceHistoryEntity
            {
                InstanceId = id, WorkflowDefinitionId = definition.Id, FromStepId = 1, ToStepId = 2,
                PerformedAt = old
            }).ToArray();
            db.InstanceHistory.AddRange(histories);
            var pinned = new InstanceHistoryEntity { InstanceId = instances[0], WorkflowDefinitionId = definition.Id,
                FromStepId = 1, ToStepId = 2, PerformedAt = old };
            db.InstanceHistory.Add(pinned);
            db.SequenceFlowOccurrences.Add(new() { InstanceId = instances[0], WorkflowDefinitionId = definition.Id,
                SequenceFlowId = 1, SourceNodeId = 1, TargetNodeId = 2, Kind = "automatic", IsTraversal = true, OccurredAt = old });
            db.SequenceFlowSummaries.Add(new() { InstanceId = instances[0], SequenceFlowId = 1, TraversalCount = 9 });
            await db.SaveChangesAsync();
            pinnedId = pinned.Id;
            db.MessageDeliveryReceipts.Add(new() { InstanceId = instances[0], IdempotencyKey = "permanent",
                WaitHistoryId = pinned.Id, SourceNodeId = 2, CorrelationHeaderName = "Idempotency-Key" });
            await db.SaveChangesAsync();
        }
        await scope.PolicyAsync(RetentionCategories.WorkflowHistory, 30);
        var preview = await scope.Repository.PreviewAsync(new(RetentionCategories.WorkflowHistory, 30), default);
        Assert.True(preview.Tables.Single(t => t.Table == "instance_history").ProtectedCount >= 1);
        await scope.RunAsync();
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(new[] { pinnedId }, await verify.InstanceHistory.Where(h => h.InstanceId == instances[0]).Select(h => h.Id).ToArrayAsync());
        Assert.False(await verify.SequenceFlowOccurrences.AnyAsync(h => h.InstanceId == instances[0]));
        Assert.Equal(9, (await verify.SequenceFlowSummaries.SingleAsync(h => h.InstanceId == instances[0])).TraversalCount);
        Assert.Equal(1, await verify.MessageDeliveryReceipts.CountAsync(h => h.InstanceId == instances[0]));
        var owned = await verify.WorkflowInstances.Where(i => instances.Contains(i.Id)).OrderBy(i => i.Id).ToArrayAsync();
        Assert.All(owned.Take(3), item => { Assert.NotNull(item.HistoryPrunedAt); Assert.Equal(old, item.FinishedAt); });
        Assert.All(owned.Skip(3), item => Assert.Null(item.HistoryPrunedAt));
        Assert.Equal(2, await verify.InstanceHistory.CountAsync(h => h.InstanceId == instances[3] || h.InstanceId == instances[4]));
    }

    [Fact]
    public async Task VariableAndNodePruningPreservesProjectionSourcesAndKeyedAuditReceipts()
    {
        await using var scope = await Scope.CreateAsync(fixture);
        var (definition, instances) = await scope.WorkflowAsync("completed");
        var instanceId = instances[0];
        long oldVisit, currentVisit, oldVariable, currentVariable, keyedAudit, oldAudit;
        await using (var db = fixture.CreateDbContext())
        {
            var token = new ExecutionTokenEntity { InstanceId = instanceId, NodeId = 1, NodeName = "task",
                NodeType = "task", Status = "completed" };
            db.ExecutionTokens.Add(token);
            await db.SaveChangesAsync();
            var visits = Enumerable.Range(0, 2).Select(_ => new NodeExecutionEntity
            {
                InstanceId = instanceId, WorkflowDefinitionId = definition.Id, ExecutionTokenId = token.Id,
                NodeId = 1, NodeName = "task", NodeType = "task", Status = "completed", CompletionReason = "normal",
                CreatedAt = scope.Now.AddDays(-61), StartedAt = scope.Now.AddDays(-61),
                UpdatedAt = scope.Now.AddDays(-60), CompletedAt = scope.Now.AddDays(-60)
            }).ToArray();
            var audits = new[] { new InstanceVariableUpdateAuditEntity(), new InstanceVariableUpdateAuditEntity { IdempotencyKey = "keep" } };
            foreach (var audit in audits) { audit.InstanceId = instanceId; audit.WorkflowDefinitionId = definition.Id;
                audit.PerformedBy = "test"; audit.PerformedAt = scope.Now.AddDays(-60); }
            db.NodeExecutions.AddRange(visits); db.InstanceVariableUpdates.AddRange(audits);
            await db.SaveChangesAsync();
            oldVisit = visits[0].Id; currentVisit = visits[1].Id; oldAudit = audits[0].Id; keyedAudit = audits[1].Id;
            var history = new[]
            {
                new InstanceVariableEntity { InstanceId = instanceId, NodeExecutionId = oldVisit,
                    InstanceVariableUpdateAuditId = oldAudit, VariableName = "value", ValueJson = JsonDocument.Parse("1") },
                new InstanceVariableEntity { InstanceId = instanceId, NodeExecutionId = currentVisit,
                    InstanceVariableUpdateAuditId = keyedAudit, VariableName = "value", ValueJson = JsonDocument.Parse("2") }
            };
            db.InstanceVariables.AddRange(history); await db.SaveChangesAsync();
            oldVariable = history[0].Id; currentVariable = history[1].Id;
            // A projection that no longer has history is still independently preserved.
            db.InstanceVariableCurrentValues.Add(new() { InstanceId = instanceId, VariableName = "legacy",
                SourceVariableId = long.MaxValue, ValueJson = JsonDocument.Parse("42"), SetAt = scope.Now });
            await db.SaveChangesAsync();
        }
        foreach (var category in new[] { RetentionCategories.VariableHistory, RetentionCategories.NodeActivity, RetentionCategories.AdministrativeAudits })
            await scope.PolicyAsync(category, 30);
        await scope.RunAsync();
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.InstanceVariables.AnyAsync(v => v.Id == oldVariable));
        Assert.True(await verify.InstanceVariables.AnyAsync(v => v.Id == currentVariable));
        Assert.False(await verify.NodeExecutions.AnyAsync(v => v.Id == oldVisit));
        Assert.True(await verify.NodeExecutions.AnyAsync(v => v.Id == currentVisit));
        Assert.False(await verify.InstanceVariableUpdates.AnyAsync(v => v.Id == oldAudit));
        Assert.True(await verify.InstanceVariableUpdates.AnyAsync(v => v.Id == keyedAudit));
        Assert.Equal(2, await verify.InstanceVariableCurrentValues.CountAsync(v => v.InstanceId == instanceId));
        Assert.Equal("42", (await verify.InstanceVariableCurrentValues.SingleAsync(v => v.InstanceId == instanceId && v.VariableName == "legacy")).ValueJson.RootElement.GetRawText());
    }

    [Fact]
    public async Task SharedRevisionPruningPreservesCurrentValueMetadataAndReplayRevisions()
    {
        await using var scope = await Scope.CreateAsync(fixture);
        var (definition, instances) = await scope.WorkflowAsync("completed", "running");
        long sharedId;
        long[] revisions;
        await using (var db = fixture.CreateDbContext())
        {
            var variable = new SharedVariableEntity { Key = scope.Prefix, DataType = "string", Status = "active",
                CurrentRevision = 1, ValueRevision = 0, CreatedByKind = "user", CreatedById = "test",
                UpdatedByKind = "user", UpdatedById = "test" };
            db.SharedVariables.Add(variable); await db.SaveChangesAsync();
            sharedId = variable.Id; scope.SharedIds.Add(sharedId);
            // Unique global revision numbers, independent of whichever tests ran first.
            var first = 1_000_000_000L + sharedId * 100;
            var rows = Enumerable.Range(1, 7).Select(n => new SharedVariableRevisionEntity
            {
                SharedVariableId = sharedId, Revision = first + n, Operation = "set", HasValue = true,
                ValueChanged = true, ValueJson = JsonDocument.Parse("\"value\""), CallerKind = "user", CallerId = "test",
                Source = "test", CreatedAt = scope.Now.AddDays(-60),
                InstanceId = n == 1 ? instances[0] : n == 7 ? instances[1] : null,
                WorkflowDefinitionId = n is 1 or 7 ? definition.Id : null
            }).ToArray();
            db.SharedVariableRevisions.AddRange(rows); await db.SaveChangesAsync();
            revisions = rows.Select(r => r.Id).ToArray();
            variable.CurrentRevision = rows[5].Revision; variable.ValueRevision = rows[4].Revision;
            db.SharedVariableCurrentValues.Add(new() { SharedVariableId = sharedId,
                SourceRevisionId = rows[3].Id, Revision = rows[3].Revision, ValueJson = JsonDocument.Parse("\"value\"") });
            db.SharedVariableRequests.Add(new() { SharedVariableId = sharedId, CallerKind = "user", CallerId = "test",
                RequestId = scope.Prefix, Operation = "set", SharedKey = scope.Prefix,
                RequestHash = new byte[32], ResultRevision = rows[2].Revision });
            await db.SaveChangesAsync();
        }
        await scope.PolicyAsync(RetentionCategories.SharedVariableHistory, 30);
        await scope.RunAsync();
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(revisions.Skip(2).Order(), await verify.SharedVariableRevisions.Where(r => r.SharedVariableId == sharedId).OrderBy(r => r.Id).Select(r => r.Id).ToArrayAsync());
        Assert.NotNull((await verify.SharedVariables.FindAsync(sharedId))!.HistoryPrunedAt);
        Assert.NotNull((await verify.WorkflowInstances.FindAsync(instances[0]))!.HistoryPrunedAt);
        Assert.Null((await verify.WorkflowInstances.FindAsync(instances[1]))!.HistoryPrunedAt);
        Assert.True(await verify.SharedVariableRequests.AnyAsync(r => r.SharedVariableId == sharedId));
    }

    [Fact]
    public async Task JobsKeepForeverAlsoProtectsOrphanSnapshotsAndEnabledCleanupReportsCascadedAttempts()
    {
        await using var scope = await Scope.CreateAsync(fixture);
        var (definition, _) = await scope.WorkflowAsync();
        long snapshotId, jobId;
        await using (var db = fixture.CreateDbContext())
        {
            var snapshot = new WorkflowJobSnapshotEntity { Kind = "rest", EvaluationTime = scope.Now,
                CreatedAt = scope.Now, SizeBytes = 2 };
            db.WorkflowJobSnapshots.Add(snapshot); await db.SaveChangesAsync(); snapshotId = snapshot.Id;
            scope.SnapshotIds.Add(snapshotId);
            var job = new WorkflowJobEntity { WorkflowDefinitionId = definition.Id, WorkflowKey = scope.Prefix,
                ActivationId = Guid.NewGuid(), NodeId = 1, NodeName = "task", NodeType = "serviceTask",
                Kind = "asyncBefore", QueueClass = "control", Phase = "before", Status = "completed",
                DueAt = scope.Now.AddDays(-60), CreatedAt = scope.Now.AddDays(-60), UpdatedAt = scope.Now.AddDays(-60),
                CompletedAt = scope.Now.AddDays(-60), MaxAttempts = 1, AttemptCount = 1,
                Attempts = [new() { AttemptNumber = 1, LeaseGeneration = 1, Status = "completed", WorkerId = "old",
                    StartedAt = scope.Now.AddDays(-60), FinishedAt = scope.Now.AddDays(-60) }] };
            db.WorkflowJobs.Add(job); await db.SaveChangesAsync(); jobId = job.Id;
        }
        await scope.RunAsync();
        await using (var db = fixture.CreateDbContext())
        { Assert.True(await db.WorkflowJobSnapshots.AnyAsync(s => s.Id == snapshotId)); Assert.True(await db.WorkflowJobs.AnyAsync(j => j.Id == jobId)); }
        await scope.PolicyAsync(RetentionCategories.CompletedJobs, 30);
        var preview = await scope.Repository.PreviewAsync(new(RetentionCategories.CompletedJobs, 30), default);
        Assert.True(preview.Tables.Single(t => t.Table == "workflow_job_attempts").EligibleCount >= 1);
        Assert.True(preview.Tables.Single(t => t.Table == "workflow_job_snapshots").EligibleCount >= 1);
        var ticks = await scope.RunAsync();
        Assert.Contains(ticks, t => t.DeletedByTable.GetValueOrDefault("workflow_jobs") >= 1
            && t.DeletedByTable.GetValueOrDefault("workflow_job_attempts") >= 1);
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.WorkflowJobs.AnyAsync(j => j.Id == jobId));
        Assert.False(await verify.WorkflowJobAttempts.AnyAsync(j => j.JobId == jobId));
        Assert.False(await verify.WorkflowJobSnapshots.AnyAsync(s => s.Id == snapshotId));
    }

    [Fact]
    public async Task ProtectedRowsCannotStarveLaterHistoryAcrossBoundedRuns()
    {
        await using var scope = await Scope.CreateAsync(fixture);
        var (definition, instances) = await scope.WorkflowAsync("completed");
        long last;
        await using (var db = fixture.CreateDbContext())
        {
            var rows = Enumerable.Range(0, 24).Select(_ => new InstanceHistoryEntity { InstanceId = instances[0],
                WorkflowDefinitionId = definition.Id, FromStepId = 1, ToStepId = 2 }).ToArray();
            db.InstanceHistory.AddRange(rows); await db.SaveChangesAsync(); last = rows[^1].Id;
            db.MessageDeliveryReceipts.AddRange(rows.Take(23).Select(row => new MessageDeliveryReceiptEntity
            { InstanceId = instances[0], IdempotencyKey = row.Id.ToString(), WaitHistoryId = row.Id,
                CorrelationHeaderName = "Idempotency-Key" }));
            await db.SaveChangesAsync();
        }
        await scope.PolicyAsync(RetentionCategories.WorkflowHistory, 30);
        await scope.RunAsync(batchSize: 1);
        Assert.Equal("budgetReached", (await scope.Repository.GetAsync(default)).LastRun!.Status);
        await scope.RunAsync(batchSize: 1);
        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.InstanceHistory.AnyAsync(h => h.Id == last));
        Assert.Equal(23, await verify.InstanceHistory.CountAsync(h => h.InstanceId == instances[0]));
    }

    [Fact]
    public async Task MigrationDowngradeCannotRemovePermanentPruningMarkers()
    {
        await using var scope = await Scope.CreateAsync(fixture);
        var (_, instances) = await scope.WorkflowAsync("completed");
        await using var db = fixture.CreateDbContext();
        await db.WorkflowInstances.Where(i => i.Id == instances[0])
            .ExecuteUpdateAsync(set => set.SetProperty(i => i.HistoryPrunedAt, scope.Now));
        try
        {
            var exception = await Assert.ThrowsAsync<PostgresException>(() => db.GetService<IMigrator>()
                .MigrateAsync("20260905143652_AddUserTaskRolePolicies"));
            Assert.Contains("Cannot downgrade retention", exception.MessageText);
            Assert.True(await db.RetentionCoordinators.AnyAsync());
            Assert.NotNull((await db.WorkflowInstances.FindAsync(instances[0]))!.HistoryPrunedAt);
        }
        finally
        {
            // EF can commit newer migrations' Down operations before the retention
            // guard rejects its own downgrade. Restore the shared fixture for the
            // current model even when an assertion above fails.
            await db.Database.MigrateAsync();
        }
        Assert.Empty(await db.Database.GetPendingMigrationsAsync());
    }

    [Fact]
    public async Task BudgetContinuationKeepsOriginalCutoffSoNewlyAgedHistoryCannotStarveLaterTables()
    {
        await using var scope = await Scope.CreateAsync(fixture);
        var (definition, instances) = await scope.WorkflowAsync("completed", "completed");
        await using (var db = fixture.CreateDbContext())
        {
            var recent = await db.WorkflowInstances.FindAsync(instances[1]);
            recent!.FinishedAt = scope.Now.AddDays(-29);
            db.InstanceHistory.AddRange(Enumerable.Range(0, 23).Select(_ => new InstanceHistoryEntity
            { InstanceId = instances[0], WorkflowDefinitionId = definition.Id, FromStepId = 1, ToStepId = 2 }));
            db.InstanceHistory.AddRange(Enumerable.Range(0, 25).Select(_ => new InstanceHistoryEntity
            { InstanceId = instances[1], WorkflowDefinitionId = definition.Id, FromStepId = 1, ToStepId = 2 }));
            db.SequenceFlowOccurrences.Add(new() { InstanceId = instances[0], WorkflowDefinitionId = definition.Id,
                SequenceFlowId = 1, SourceNodeId = 1, TargetNodeId = 2, Kind = "automatic", IsTraversal = true });
            await db.SaveChangesAsync();
        }
        await scope.PolicyAsync(RetentionCategories.WorkflowHistory, 30);
        await scope.RunAsync(batchSize: 1);
        var first = (await scope.Repository.GetAsync(default)).LastRun!;
        Assert.Equal("budgetReached", first.Status);
        var originalCutoff = first.Categories.Single(c => c.Category == RetentionCategories.WorkflowHistory).Cutoff;

        scope.AdvanceTime(TimeSpan.FromDays(2));
        await scope.RunAsync(batchSize: 1);
        var continued = (await scope.Repository.GetAsync(default)).LastRun!;
        Assert.Equal("succeeded", continued.Status);
        Assert.Equal(originalCutoff, continued.Categories.Single(c => c.Category == RetentionCategories.WorkflowHistory).Cutoff);
        await using (var verify = fixture.CreateDbContext())
        {
            Assert.False(await verify.InstanceHistory.AnyAsync(h => h.InstanceId == instances[0]));
            Assert.False(await verify.SequenceFlowOccurrences.AnyAsync(h => h.InstanceId == instances[0]));
            Assert.Equal(25, await verify.InstanceHistory.CountAsync(h => h.InstanceId == instances[1]));
        }

        // Once that scan finishes, the next scan gets a fresh cutoff and can
        // collect rows that became old enough while the previous scan ran.
        await scope.RunAsync();
        await using var final = fixture.CreateDbContext();
        Assert.False(await final.InstanceHistory.AnyAsync(h => h.InstanceId == instances[1]));
    }

    [Fact]
    public async Task BudgetContinuationDoesNotChaseNewOrphanSnapshotsPastItsCapturedTableFrontier()
    {
        await using var scope = await Scope.CreateAsync(fixture);
        async Task<long[]> AddSnapshotsAsync(int count)
        {
            await using var db = fixture.CreateDbContext();
            var snapshots = Enumerable.Range(0, count).Select(_ => new WorkflowJobSnapshotEntity
            { Kind = "rest", EvaluationTime = scope.Now, CreatedAt = scope.Now, SizeBytes = 2 }).ToArray();
            db.WorkflowJobSnapshots.AddRange(snapshots);
            await db.SaveChangesAsync();
            var ids = snapshots.Select(s => s.Id).ToArray();
            scope.SnapshotIds.AddRange(ids);
            return ids;
        }

        var originalIds = await AddSnapshotsAsync(23);
        await scope.PolicyAsync(RetentionCategories.CompletedJobs, 30);
        await scope.RunAsync(batchSize: 1);
        Assert.Equal("budgetReached", (await scope.Repository.GetAsync(default)).LastRun!.Status);
        var newlyCreatedIds = await AddSnapshotsAsync(25);
        await scope.RunAsync();
        Assert.Equal("succeeded", (await scope.Repository.GetAsync(default)).LastRun!.Status);
        await using (var verify = fixture.CreateDbContext())
        {
            Assert.False(await verify.WorkflowJobSnapshots.AnyAsync(s => originalIds.Contains(s.Id)));
            Assert.Equal(25, await verify.WorkflowJobSnapshots.CountAsync(s => newlyCreatedIds.Contains(s.Id)));
        }
        await scope.RunAsync();
        await using var final = fixture.CreateDbContext();
        Assert.False(await final.WorkflowJobSnapshots.AnyAsync(s => newlyCreatedIds.Contains(s.Id)));
    }

    private sealed class Scope(PostgresApiFixture fixture) : IAsyncDisposable
    {
        private readonly RetentionDataSource _source = new(fixture.ConnectionString);
        public string Prefix { get; } = $"retention-{Guid.NewGuid():N}";
        public DateTimeOffset Now { get; } = new(DateTimeOffset.UtcNow.UtcTicks / 10 * 10, TimeSpan.Zero);
        public List<long> SharedIds { get; } = [];
        public List<long> SnapshotIds { get; } = [];
        public RetentionRepository Repository { get; private set; } = null!;
        private FixedTime _clock = null!;
        private string WorkerId { get; } = Guid.NewGuid().ToString();
        public static async Task<Scope> CreateAsync(PostgresApiFixture fixture)
        {
            var scope = new Scope(fixture);
            scope._clock = new FixedTime(scope.Now);
            scope.Repository = new(scope._source, scope._clock);
            await scope.ResetAsync(); return scope;
        }

        public void AdvanceTime(TimeSpan elapsed) => _clock.Advance(elapsed);

        public async Task PolicyAsync(string category, int? days)
        {
            var policy = (await Repository.GetAsync(default)).Policies.Single(p => p.Category == category);
            await Repository.UpdatePolicyAsync(category, new(days, policy.Revision), "retention-test", default);
        }

        public async Task<List<RetentionTickResult>> RunAsync(int batchSize = 250)
        {
            await Repository.RequestRunAsync("retention-test", default);
            var ticks = new List<RetentionTickResult>();
            for (var index = 0; index < 300; index++)
            {
                var tick = await Repository.ProcessBatchAsync(new() { WorkerId = WorkerId, BatchSize = batchSize,
                    MaxRunnableJobs = int.MaxValue, MaxQueueLagSeconds = int.MaxValue }, default);
                ticks.Add(tick);
                if (!tick.HasActiveRun) return ticks;
            }
            throw new InvalidOperationException("Retention run did not finish within its bounded test budget.");
        }

        public async Task<(WorkflowDefinitionEntity Definition, long[] Instances)> WorkflowAsync(params string[] statuses)
        {
            await using var db = fixture.CreateDbContext();
            var definition = new WorkflowDefinitionEntity { Name = Prefix, WorkflowKey = Prefix, Version = 1,
                Definition = new WorkflowModel { Id = Prefix, Name = Prefix }, IsPublished = true };
            db.WorkflowDefinitions.Add(definition); await db.SaveChangesAsync();
            var instances = statuses.Select(status => new WorkflowInstanceEntity { WorkflowDefinitionId = definition.Id,
                WorkflowKey = Prefix, Status = status, CreatedAt = Now.AddDays(-90), UpdatedAt = Now.AddDays(-60),
                FinishedAt = status == "running" ? null : Now.AddDays(-60) }).ToArray();
            db.WorkflowInstances.AddRange(instances); await db.SaveChangesAsync();
            return (definition, instances.Select(i => i.Id).ToArray());
        }

        private async Task ResetAsync()
        {
            await using var db = fixture.CreateDbContext();
            await db.RetentionPolicies.ExecuteUpdateAsync(set => set.SetProperty(p => p.RetentionDays, (int?)null)
                .SetProperty(p => p.IsInitialized, true).SetProperty(p => p.Revision, p => p.Revision + 1));
            await db.RetentionCoordinators.ExecuteUpdateAsync(set => set.SetProperty(p => p.CurrentRunJson, (string?)null)
                .SetProperty(p => p.LastRunJson, (string?)null).SetProperty(p => p.LeaseOwner, (string?)null)
                .SetProperty(p => p.LeaseExpiresAt, (DateTimeOffset?)null).SetProperty(p => p.NextScheduledAt, Now.AddDays(1)));
        }

        public async ValueTask DisposeAsync()
        {
            await ResetAsync();
            await using var db = fixture.CreateDbContext();
            await db.SharedVariableRequests.Where(x => SharedIds.Contains(x.SharedVariableId)).ExecuteDeleteAsync();
            await db.SharedVariableCurrentValues.Where(x => SharedIds.Contains(x.SharedVariableId)).ExecuteDeleteAsync();
            await db.SharedVariableRevisions.Where(x => SharedIds.Contains(x.SharedVariableId)).ExecuteDeleteAsync();
            await db.SharedVariables.Where(x => SharedIds.Contains(x.Id)).ExecuteDeleteAsync();
            var ids = await db.WorkflowInstances.Where(x => x.WorkflowKey == Prefix).Select(x => x.Id).ToArrayAsync();
            await db.WorkflowJobs.Where(x => x.WorkflowKey == Prefix).ExecuteDeleteAsync();
            await db.WorkflowJobSnapshots.Where(x => SnapshotIds.Contains(x.Id)).ExecuteDeleteAsync();
            await db.MessageDeliveryReceipts.Where(x => ids.Contains(x.InstanceId)).ExecuteDeleteAsync();
            await db.InstanceVariables.Where(x => ids.Contains(x.InstanceId)).ExecuteDeleteAsync();
            await db.NodeExecutions.Where(x => ids.Contains(x.InstanceId)).ExecuteDeleteAsync();
            await db.WorkflowInstances.Where(x => x.WorkflowKey == Prefix).ExecuteDeleteAsync();
            await db.WorkflowDefinitions.Where(x => x.WorkflowKey == Prefix).ExecuteDeleteAsync();
            await _source.DisposeAsync();
        }
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance(TimeSpan elapsed) => now += elapsed;
    }
}
