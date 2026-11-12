using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Flowbit.Tests;

public sealed partial class AutomaticMultiInstancePersistenceTests
{
    [Fact]
    public async Task Child_owned_call_message_publishes_through_common_core_and_adapter_keeps_settlement_ownership()
    {
        await using var db = fixture.CreateDbContext();
        var create = (await CreateOwnerAsync(db, 1, 1)) with { ActivityKind = "callActivity" };
        var token = await db.ExecutionTokens.FindAsync(create.TokenId);
        token!.NodeType = "callActivity";
        var storedDefinition = (await db.WorkflowDefinitions.FindAsync(create.WorkflowDefinitionId))!;
        var definition = storedDefinition.Definition;
        definition.Variables = [new() { Name = "items", DataType = "number", IsArray = true }, new() { Name = "results", DataType = "json" }];
        definition.FlowNodes = [new() { Id = create.NodeId, Type = "callActivity", CallActivity = new() { WorkflowKey = definition.Id },
            MultiInstance = new() { CollectionVariable = "items", ResultVariable = "results", OutputVariables = [] } }];
        await db.SaveChangesAsync();
        var repo = new AutomaticMultiInstanceRepository(db);
        var jobs = new WorkflowJobRepository(db, fixture.DataSource);
        var calls = new CallActivityRepository(db);
        var runtime = new WorkflowRuntimeRepository(db);
        var coordinator = new AutomaticMultiInstanceCoordinator(repo, jobs,
            new WorkflowVariableStore(runtime, new SharedVariableRepository(db)),
            fixture.Factory.Services.GetRequiredService<ISharedVariableAccessPlanCache>(), runtime,
            callActivities: calls);
        var template = new WorkflowJobCreateRecord
        {
            InstanceId = create.InstanceId, TokenId = create.TokenId, WorkflowDefinitionId = create.WorkflowDefinitionId,
            WorkflowKey = storedDefinition.WorkflowKey, ActivationId = create.ActivationId, NodeId = create.NodeId,
            NodeName = "Call", NodeType = "callActivity", Kind = "multiInstanceDispatch", Phase = "multiInstanceDispatch",
            QueueClass = "control", DueAt = DateTimeOffset.UtcNow
        };
        AutomaticMultiInstanceRecord body;
        AutomaticMultiInstanceItemRecord item;
        AutomaticMultiInstanceItemFence fence;
        long invocationId;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            body = await repo.CreateAsync(create with
                { PinnedChildDefinitionId = create.WorkflowDefinitionId, PinnedChildStartEventId = 1 }, default);
            item = Assert.Single(await repo.AdmitAsync(body.Id, body.ActivationId, 1, default));
            var child = new WorkflowInstanceEntity { WorkflowDefinitionId = create.WorkflowDefinitionId,
                WorkflowKey = storedDefinition.WorkflowKey, Status = "completed", FinishedAt = DateTimeOffset.UtcNow };
            db.WorkflowInstances.Add(child);
            await db.SaveChangesAsync();
            ownedInstanceIds.Add(child.Id);
            var childToken = new ExecutionTokenEntity { InstanceId = child.Id, NodeId = 99, NodeName = "End",
                NodeType = "endEvent", Status = "ended" };
            db.ExecutionTokens.Add(childToken);
            await db.SaveChangesAsync();
            var invocation = new CallActivityExecutionEntity
            {
                ParentInstanceId = body.InstanceId, ParentTokenId = body.TokenId, ParentDefinitionId = body.WorkflowDefinitionId,
                ParentNodeId = body.NodeId, ActivationId = body.ActivationId, RootInstanceId = body.InstanceId, ChildDepth = 1,
                MultiInstanceExecutionId = body.Id, AutomaticMultiInstanceItemId = item.Id,
                ChildInstanceId = child.Id, ChildDefinitionId = create.WorkflowDefinitionId, ChildStartEventId = 1,
                State = "running", CapacityReserved = true, TargetWorkflowKey = storedDefinition.WorkflowKey,
                StartedAt = DateTimeOffset.UtcNow, AdmittedAt = DateTimeOffset.UtcNow
            };
            db.CallActivityExecutions.Add(invocation);
            await db.SaveChangesAsync();
            invocationId = invocation.Id;
            await calls.AddResultAsync(new(child.Id, invocation.Id, "completed", null, null, null, null, DateTimeOffset.UtcNow), default);
            var message = await jobs.EnqueueAsync(template with { InstanceId = child.Id, TokenId = childToken.Id,
                ActivationId = childToken.ActivationId, NodeId = 99, NodeType = "endEvent", Kind = "callResume", Phase = "callResume",
                CallInvocationId = invocation.Id }, default);
            var leaseToken = Guid.NewGuid();
            await db.WorkflowJobs.Where(job => job.Id == message.Id).ExecuteUpdateAsync(update => update
                .SetProperty(job => job.Status, "running").SetProperty(job => job.WorkerId, "call-test")
                .SetProperty(job => job.LeaseToken, leaseToken).SetProperty(job => job.LeaseGeneration, 1)
                .SetProperty(job => job.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(10))
                .SetProperty(job => job.PublicationFenceRefreshRequested, true));
            db.ChangeTracker.Clear();
            fence = new(new(message.Id, "call-test", leaseToken, 1), invocation.Id);
            // ItemIndex is optional read-side enrichment, not invocation ownership.
            Assert.Null((await calls.GetInvocationAsync(invocation.Id, default))!.ItemIndex);
            // Lease checks use an independent database connection, just like a
            // Worker heartbeat. The claimed transport must already be committed.
            await tx.CommitAsync();
        }
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            Assert.True(await coordinator.SaveStageAsync(body.Id, item.Id, body.ActivationId, fence, Json("{}"), default));
            Assert.True(await coordinator.SaveCandidateAsync(body.Id, item.Id, body.ActivationId, fence, definition,
                new(new Dictionary<string, JsonElement>(), new Dictionary<string, JsonElement>(),
                    new Dictionary<string, long>(), new Dictionary<string, JsonElement>()), default));
            await tx.CommitAsync();
        }
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            Assert.True(await coordinator.PublishCandidateAsync(body.Id, item.Id, body.ActivationId, fence, definition,
                new("caller", ["admin"], new Dictionary<string, string>()), template, default));
            var message = await jobs.GetForUpdateAsync(fence.JobId, default);
            Assert.Equal("running", message!.Status);
            Assert.False(message.PublicationFenceRefreshRequested);
            Assert.Equal("running", (await calls.GetInvocationAsync(invocationId, default))!.State);
            Assert.True(await calls.SettleAsync(invocationId, "completed", null, null, null, default));
            Assert.True(await jobs.CompleteAsync(fence.Job, default));
            await tx.CommitAsync();
        }
        await using var replay = await db.Database.BeginTransactionAsync();
        await Assert.ThrowsAsync<WorkflowConflictException>(() => coordinator.PublishCandidateAsync(body.Id, item.Id,
            body.ActivationId, fence, definition, new("caller", [], new Dictionary<string, string>()), template, default));
        Assert.Equal(1, (await repo.GetAsync(body.Id, false, default))!.CompletedCount);
        Assert.Equal("callActivity", (await db.NodeExecutions.AsNoTracking().SingleAsync(row => row.AutomaticMultiInstanceItemId == item.Id)).CompletedBy);
    }

    [Theory]
    [InlineData(true, "output_version_conflict", true)]
    [InlineData(true, "job_conflict", false)]
    [InlineData(false, "output_version_conflict", false)]
    public async Task Call_item_delivery_retry_refreshes_fences_only_for_a_saved_candidate_conflict(
        bool candidateSaved, string incidentType, bool refresh)
    {
        await using var db = fixture.CreateDbContext();
        var create = (await CreateOwnerAsync(db, 1, 1)) with { ActivityKind = "callActivity" };
        var token = await db.ExecutionTokens.FindAsync(create.TokenId);
        token!.NodeType = "callActivity";
        await db.SaveChangesAsync();
        var key = (await db.WorkflowDefinitions.FindAsync(create.WorkflowDefinitionId))!.WorkflowKey;
        var repo = new AutomaticMultiInstanceRepository(db);
        var jobs = new WorkflowJobRepository(db, fixture.DataSource);
        WorkflowJobRecord incidentJob;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var body = await repo.CreateAsync(create with { PinnedChildDefinitionId = create.WorkflowDefinitionId, PinnedChildStartEventId = 1 }, default);
            var item = Assert.Single(await repo.AdmitAsync(body.Id, body.ActivationId, 1, default));
            if (candidateSaved)
            {
                Assert.True(await repo.SaveStageAsync(body.Id, item.Id, body.ActivationId, Json("{}"), default));
                Assert.True(await repo.SaveCandidateAsync(body.Id, item.Id, body.ActivationId, Json("{\"accepted\":1}"), default));
            }
            var child = new WorkflowInstanceEntity { WorkflowDefinitionId = create.WorkflowDefinitionId, WorkflowKey = key,
                Status = "completed", FinishedAt = DateTimeOffset.UtcNow };
            db.WorkflowInstances.Add(child);
            await db.SaveChangesAsync();
            ownedInstanceIds.Add(child.Id);
            var childToken = new ExecutionTokenEntity { InstanceId = child.Id, NodeId = 99, NodeName = "End",
                NodeType = "endEvent", Status = "ended" };
            db.ExecutionTokens.Add(childToken);
            await db.SaveChangesAsync();
            var invocation = new CallActivityExecutionEntity
            {
                ParentInstanceId = body.InstanceId, ParentTokenId = body.TokenId, ParentDefinitionId = body.WorkflowDefinitionId,
                ParentNodeId = body.NodeId, ActivationId = body.ActivationId, RootInstanceId = body.InstanceId, ChildDepth = 1,
                MultiInstanceExecutionId = body.Id, AutomaticMultiInstanceItemId = item.Id,
                ChildInstanceId = child.Id, ChildDefinitionId = create.WorkflowDefinitionId, ChildStartEventId = 1,
                State = "running", CapacityReserved = true, TargetWorkflowKey = key,
                StartedAt = DateTimeOffset.UtcNow, AdmittedAt = DateTimeOffset.UtcNow
            };
            db.CallActivityExecutions.Add(invocation);
            await db.SaveChangesAsync();
            incidentJob = await jobs.EnqueueIncidentAsync(new()
            {
                InstanceId = child.Id, TokenId = childToken.Id, WorkflowDefinitionId = create.WorkflowDefinitionId,
                WorkflowKey = key, ActivationId = childToken.ActivationId, NodeId = 99, NodeName = "End", NodeType = "endEvent",
                Kind = "callResume", Phase = "callResume", QueueClass = "control", DueAt = DateTimeOffset.UtcNow,
                CallInvocationId = invocation.Id
            }, incidentType, "Publication blocked", null, default);
            await tx.CommitAsync();
        }
        db.ChangeTracker.Clear();

        var retried = await jobs.RetryIncidentAsync(incidentJob.IncidentId!.Value, "admin", DateTimeOffset.UtcNow, default);

        Assert.Equal(refresh, retried!.PublicationFenceRefreshRequested);
        Assert.Equal(WorkflowJobStatuses.Queued, retried.Status);
    }

    [Fact]
    public async Task Instance_list_positions_name_the_open_body_of_their_own_activation()
    {
        await using var db = fixture.CreateDbContext();
        var create = await CreateOwnerAsync(db, 1, 1);
        var repo = new AutomaticMultiInstanceRepository(db);
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var body = await repo.CreateAsync(create, default);
            Assert.True(await repo.EnterBodyWaitAsync(body.Id, body.ActivationId, null, default));
            await tx.CommitAsync();
        }
        db.ChangeTracker.Clear();
        var runtime = new WorkflowRuntimeRepository(db);
        var key = (await db.WorkflowDefinitions.FindAsync(create.WorkflowDefinitionId))!.WorkflowKey;
        async Task<InstanceExecutionPositionRecord> PositionAsync() => Assert.Single(Assert.Single((await runtime.ListInstancesAsync(
            status: null, instanceId: create.InstanceId, workflowId: null, workflowKey: key, businessKey: null, nodeId: null,
            nodeExternalId: null, variableFilter: null, sort: [new InstanceSortCriterion(InstanceSortField.Id, SortDirection.Ascending)],
            authorization: new InstanceListAuthorization(IsGlobalReader: true, LowerCallerRoles: []),
            cursor: null, includeVariables: false, page: 1, pageSize: 50, CancellationToken.None)).Items).ExecutionPositions!);

        var position = await PositionAsync();
        var opened = await db.MultiInstanceExecutions.SingleAsync(row => row.TokenId == create.TokenId);
        Assert.Equal((opened.Id, "serviceTask"), (position.MultiInstanceExecutionId!.Value, position.MultiInstanceActivityKind!));

        // A token that entered the node again has a new activation and no body yet.
        await db.ExecutionTokens.Where(token => token.Id == create.TokenId).ExecuteUpdateAsync(update => update
            .SetProperty(token => token.ActivationId, Guid.NewGuid())
            .SetProperty(token => token.WaitState, (string?)null)
            .SetProperty(token => token.WaitingMultiInstanceExecutionId, (long?)null));
        position = await PositionAsync();
        Assert.Null(position.MultiInstanceExecutionId);
        Assert.Null(position.MultiInstanceActivityKind);
    }

    [Fact]
    public async Task Open_item_incident_blocks_admission_until_it_is_retried()
    {
        await using var db = fixture.CreateDbContext();
        var create = await CreateOwnerAsync(db, 4, 3);
        var key = (await db.WorkflowDefinitions.FindAsync(create.WorkflowDefinitionId))!.WorkflowKey;
        var repo = new AutomaticMultiInstanceRepository(db);
        var jobs = new WorkflowJobRepository(db, fixture.DataSource);
        var runtime = new WorkflowRuntimeRepository(db);
        var coordinator = new AutomaticMultiInstanceCoordinator(repo, jobs,
            new WorkflowVariableStore(runtime, new SharedVariableRepository(db)),
            fixture.Factory.Services.GetRequiredService<ISharedVariableAccessPlanCache>(), runtime);
        var template = new WorkflowJobCreateRecord
        {
            InstanceId = create.InstanceId, TokenId = create.TokenId, WorkflowDefinitionId = create.WorkflowDefinitionId,
            WorkflowKey = key, ActivationId = create.ActivationId, NodeId = create.NodeId, NodeName = create.NodeName,
            NodeType = create.ActivityKind, Kind = "multiInstanceItem", Phase = "multiInstanceItem",
            QueueClass = "activity", DueAt = DateTimeOffset.UtcNow
        };
        AutomaticMultiInstanceRecord body;
        WorkflowJobRecord incidentJob;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            body = await repo.CreateAsync(create, default);
            var items = await repo.AdmitAsync(body.Id, body.ActivationId, 3, default);
            // Item 0's job exhausted its attempts; item 1 succeeded and freed its slot.
            incidentJob = await jobs.EnqueueIncidentAsync(template with
            {
                MultiInstanceExecutionId = body.Id, AutomaticMultiInstanceItemId = items[0].Id
            }, "job_failed", "Remote call failed", null, default);
            Assert.True(await repo.SaveStageAsync(body.Id, items[1].Id, body.ActivationId, Json("{}"), default));
            Assert.True(await repo.SaveCandidateAsync(body.Id, items[1].Id, body.ActivationId, Json("{}"), default));
            Assert.True(await repo.CompleteItemAsync(body.Id, items[1].Id, body.ActivationId, Json("{}"), null, 0, default));
            var progress = Assert.Single(await repo.GetProgressAsync(body.InstanceId, default));
            Assert.Equal((1, 1, 1, "itemIncident"),
                (progress.ActiveCount, progress.PendingCount, progress.Execution.FailedCount, progress.Execution.AdmissionBlockedReason!));
            Assert.Empty(await coordinator.AdmitJobsAsync(body.Id, body.ActivationId, template, 2000, 0, default));
            await tx.CommitAsync();
        }
        db.ChangeTracker.Clear();

        Assert.NotNull(await jobs.RetryIncidentAsync(incidentJob.IncidentId!.Value, "admin", DateTimeOffset.UtcNow, default));

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            var progress = Assert.Single(await repo.GetProgressAsync(body.InstanceId, default));
            Assert.Equal((2, 0, null), (progress.ActiveCount, progress.Execution.FailedCount, progress.Execution.AdmissionBlockedReason));
            var admitted = Assert.Single(await coordinator.AdmitJobsAsync(body.Id, body.ActivationId, template, 2000, 0, default));
            Assert.Equal(3, admitted.ItemIndex);
            await tx.CommitAsync();
        }
    }

    [Fact]
    public async Task Current_owner_fence_ignores_a_tracked_token_from_an_earlier_phase()
    {
        await using var db = fixture.CreateDbContext();
        var create = await CreateOwnerAsync(db, 1, 1);
        await using var tx = await db.Database.BeginTransactionAsync();
        var repo = new AutomaticMultiInstanceRepository(db);
        var body = await repo.CreateAsync(create, default);
        Assert.True(await repo.HasCurrentOwnerAsync(body.Id, body.ActivationId, default));
        var tracked = await db.ExecutionTokens.FindAsync(body.TokenId);
        await db.ExecutionTokens.Where(row => row.Id == body.TokenId).ExecuteUpdateAsync(update => update
            .SetProperty(row => row.ActivationId, Guid.NewGuid()));
        Assert.Equal(body.ActivationId, tracked!.ActivationId);
        Assert.False(await repo.HasCurrentOwnerAsync(body.Id, body.ActivationId, default));
    }

    [Theory]
    [InlineData("serviceTask")]
    [InlineData("callActivity")]
    public async Task Item_ledger_preserves_entry_and_completion_identity_without_duplicate_traversals(string kind)
    {
        await using var db = fixture.CreateDbContext();
        var create = (await CreateOwnerAsync(db, 3, 3)) with
        {
            ActivityKind = kind,
            ActorContext = new("alice", ["operator"])
            {
                AuditClaims = new Dictionary<string, string[]> { ["department"] = ["finance"] },
                ActingFor = "owner", DelegationId = 17
            }
        };
        var token = await db.ExecutionTokens.FindAsync(create.TokenId);
        token!.NodeType = kind;
        token.ArrivedViaFlowId = 103;
        await db.SaveChangesAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var repo = new AutomaticMultiInstanceRepository(db);
        var body = await repo.CreateAsync(kind == "callActivity"
            ? create with { PinnedChildDefinitionId = create.WorkflowDefinitionId, PinnedChildStartEventId = 1 }
            : create, default);
        var items = await repo.AdmitAsync(body.Id, body.ActivationId, 3, default);
        await repo.SaveStageAsync(body.Id, items[0].Id, body.ActivationId, Json("{}"), default);
        await repo.SaveCandidateAsync(body.Id, items[0].Id, body.ActivationId, Json("{}"), default);
        await repo.CompleteItemAsync(body.Id, items[0].Id, body.ActivationId, Json("{}"), null, 0, default);
        await repo.FailItemAsync(body.Id, items[1].Id, body.ActivationId, "failure", "Failed", default);
        var before = await db.NodeExecutions.AsNoTracking().Where(row => row.MultiInstanceExecutionId == body.Id
            && row.Status != "active").OrderBy(row => row.ItemIndex).ToArrayAsync();
        Assert.Equal(2, before.Length);
        foreach (var row in before)
        {
            Assert.Equal(103, row.EnteredViaFlowId);
            Assert.Equal(kind == "callActivity" ? "callActivity" : "alice", row.CompletedBy);
            Assert.Equal(row.TriggeredBy, row.CompletedBy);
            Assert.Equal(row.TriggeredByRolesJson!.RootElement.GetRawText(), row.CompletedByRolesJson!.RootElement.GetRawText());
            if (kind == "callActivity")
            {
                Assert.Null(row.CompletedByClaimsJson);
                Assert.Null(row.CompletedActingFor);
            }
            else
            {
                Assert.Equal("finance", row.CompletedByClaimsJson!.RootElement.GetProperty("department")[0].GetString());
                Assert.Equal("owner", row.CompletedActingFor);
                Assert.Equal(17, row.CompletedDelegationId);
            }
        }
        var evidence = await new InstanceDiagramHistoryQueryRepository(db).GetAsync(body.InstanceId, default);
        var entry = Assert.Single(evidence!.MultiInstanceEntries);
        Assert.Equal(103, entry.FlowId);
        Assert.Equal(1, entry.Count);
        await repo.CancelUnfinishedItemsAsync(body.Id, body.ActivationId, "instanceCancelled", default);
        var after = await db.NodeExecutions.AsNoTracking().SingleAsync(row => row.Id == before[0].Id);
        Assert.Equal("completed", after.Status);
        Assert.Equal(before[0].CompletedAt, after.CompletedAt);
        Assert.Equal(before[0].CompletedBy, after.CompletedBy);
        var retained = await repo.GetItemAsync(body.Id, items[0].Id, false, default);
        Assert.Equal(before[0].Id, retained!.NodeExecutionId);
        await db.NodeExecutions.Where(row => row.Id == before[0].Id).ExecuteDeleteAsync();
        Assert.Null((await repo.GetItemAsync(body.Id, items[0].Id, false, default))!.NodeExecutionId);
        Assert.NotNull((await repo.GetItemAsync(body.Id, items[0].Id, false, default))!.Result);
    }

    [Theory]
    [InlineData("output_version_conflict", true)]
    [InlineData("job_conflict", false)]
    public async Task Current_control_incident_is_visible_until_retry_and_only_output_conflict_refreshes(string type, bool refresh)
    {
        await using var db = fixture.CreateDbContext();
        var create = await CreateOwnerAsync(db, 0, 0);
        var repo = new AutomaticMultiInstanceRepository(db);
        var jobs = new WorkflowJobRepository(db, fixture.DataSource);
        WorkflowJobRecord incidentJob;
        AutomaticMultiInstanceRecord body;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            body = await repo.CreateAsync(create, default);
            await repo.EnterBodyWaitAsync(body.Id, body.ActivationId, null, default);
            var generation = await repo.SetControlStateAsync(body.Id, body.ActivationId, "completing", null, default);
            incidentJob = await jobs.EnqueueIncidentAsync(new()
            {
                InstanceId = body.InstanceId, TokenId = body.TokenId, WorkflowDefinitionId = body.WorkflowDefinitionId,
                WorkflowKey = (await db.WorkflowDefinitions.FindAsync(body.WorkflowDefinitionId))!.WorkflowKey,
                ActivationId = body.ActivationId, NodeId = body.NodeId, NodeName = "Body", NodeType = "serviceTask",
                MultiInstanceExecutionId = body.Id, MultiInstanceControlGeneration = generation,
                Kind = "multiInstanceComplete", Phase = "multiInstanceComplete", QueueClass = "control", DueAt = DateTimeOffset.UtcNow
            }, type, "Blocked publication", null, default);
            await tx.CommitAsync();
        }
        var progress = Assert.Single(await repo.GetProgressAsync(body.InstanceId, default));
        Assert.Equal(incidentJob.Id, progress.ControlJobId);
        Assert.Equal(incidentJob.IncidentId, progress.ControlIncidentId);
        Assert.Equal(type, progress.ControlIncidentType);
        Assert.Equal(0, progress.Execution.FailedCount);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var runtime = new WorkflowRuntimeRepository(db);
        var projection = new WorkflowInstanceProjectionService(runtime, new WorkflowDefinitionRepository(db, cache), automaticMultiInstances: repo);
        var projected = await projection.BuildExecutionAsync((await runtime.GetInstanceAsync(body.InstanceId, default))!, false, default);
        Assert.Equal(incidentJob.IncidentId, Assert.Single(projected.AutomaticMultiInstances).ControlIncidentId);
        db.ChangeTracker.Clear();
        var retried = await jobs.RetryIncidentAsync(incidentJob.IncidentId!.Value, "admin", DateTimeOffset.UtcNow, default);
        Assert.Equal(refresh, retried!.PublicationFenceRefreshRequested);
        Assert.Null(Assert.Single(await repo.GetProgressAsync(body.InstanceId, default)).ControlIncidentId);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Reentry_does_not_attach_a_prior_body_to_a_new_activation(bool includeHistory)
    {
        await using var db = fixture.CreateDbContext();
        var create = await CreateOwnerAsync(db, 0, 0);
        var repo = new AutomaticMultiInstanceRepository(db);
        await using var tx = await db.Database.BeginTransactionAsync();
        var body = await repo.CreateAsync(create, default);
        await repo.SettleAsync(body.Id, body.ActivationId, "completed", "completed", default);
        var runtime = new WorkflowRuntimeRepository(db);
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var projection = new WorkflowInstanceProjectionService(runtime, new WorkflowDefinitionRepository(db, cache), automaticMultiInstances: repo);
        var instance = (await runtime.GetInstanceAsync(body.InstanceId, default))!;
        var before = await projection.BuildExecutionAsync(instance, includeHistory, default);
        Assert.Equal(body.Id, Assert.Single(before.ExecutionPositions).MultiInstanceExecutionId);
        var activation = Guid.NewGuid();
        var entryJob = await new WorkflowJobRepository(db, fixture.DataSource).EnqueueAsync(new()
        {
            InstanceId = body.InstanceId, TokenId = body.TokenId, WorkflowDefinitionId = body.WorkflowDefinitionId,
            WorkflowKey = (await db.WorkflowDefinitions.FindAsync(body.WorkflowDefinitionId))!.WorkflowKey,
            ActivationId = activation, NodeId = body.NodeId, NodeName = "Body", NodeType = "serviceTask",
            Kind = "asyncBefore", Phase = "asyncBefore", QueueClass = "activity", DueAt = DateTimeOffset.UtcNow
        }, default);
        await db.ExecutionTokens.Where(row => row.Id == body.TokenId).ExecuteUpdateAsync(update => update
            .SetProperty(row => row.ActivationId, activation).SetProperty(row => row.WaitState, "asyncBefore")
            .SetProperty(row => row.WaitingJobId, entryJob.Id));
        db.ChangeTracker.Clear();
        var after = await projection.BuildExecutionAsync(instance, includeHistory, default);
        Assert.Null(Assert.Single(after.ExecutionPositions).MultiInstanceExecutionId);
        Assert.Null(Assert.Single(after.ExecutionPositions).MultiInstanceActivityKind);
        Assert.Single(after.AutomaticMultiInstances);
    }

    [Fact]
    public async Task Async_after_incident_of_a_settled_body_is_retried_as_an_ordinary_job()
    {
        await using var db = fixture.CreateDbContext();
        var create = await CreateOwnerAsync(db, 0, 0);
        var repo = new AutomaticMultiInstanceRepository(db);
        var jobs = new WorkflowJobRepository(db, fixture.DataSource);
        WorkflowJobRecord incidentJob;
        AutomaticMultiInstanceRecord body;
        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            body = await repo.CreateAsync(create, default);
            Assert.True(await repo.SettleAsync(body.Id, body.ActivationId, "completed", "multiInstanceCompleted", default));
            // Async-after runs after settlement and names the body for its
            // traversal correlation, as the engine's continuation does.
            incidentJob = await jobs.EnqueueIncidentAsync(new()
            {
                InstanceId = body.InstanceId, TokenId = body.TokenId, WorkflowDefinitionId = body.WorkflowDefinitionId,
                WorkflowKey = (await db.WorkflowDefinitions.FindAsync(body.WorkflowDefinitionId))!.WorkflowKey,
                ActivationId = body.ActivationId, NodeId = body.NodeId, NodeName = "Body", NodeType = "serviceTask",
                MultiInstanceExecutionId = body.Id, Kind = "asyncAfter", Phase = "asyncAfter",
                QueueClass = "activity", DueAt = DateTimeOffset.UtcNow
            }, "job_failed", "Continuation failed", null, default);
            await tx.CommitAsync();
        }
        db.ChangeTracker.Clear();

        var retried = await jobs.RetryIncidentAsync(incidentJob.IncidentId!.Value, "admin", DateTimeOffset.UtcNow, default);

        Assert.NotNull(retried);
        Assert.Equal(WorkflowJobStatuses.Queued, retried!.Status);
        Assert.False(retried.PublicationFenceRefreshRequested);
        Assert.Equal("completed", (await repo.GetAsync(body.Id, false, default))!.Status);
    }
}
