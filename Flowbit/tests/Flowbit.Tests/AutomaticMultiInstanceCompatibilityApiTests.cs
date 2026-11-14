using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// TASK-05A compatibility through the HTTP API against PostgreSQL, with real automatic
/// bodies opened by the registered job processor:
/// administrative discovery, candidate search, and batches never reach a body; an open body
/// blocks a version change and a settled one does not block reactivation; and the diagram
/// history counts a body's activation once, whether it has items or none.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class AutomaticMultiInstanceCompatibilityApiTests(PostgresApiFixture fixture) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly List<long> roots = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Other suites lease specific jobs from this shared database.
        await using var db = fixture.CreateDbContext();
        var instanceIds = await db.WorkflowInstances.AsNoTracking()
            .Where(instance => roots.Contains(instance.Id)
                || instance.CallRootInstanceId != null && roots.Contains(instance.CallRootInstanceId.Value))
            .Select(instance => instance.Id).ToListAsync();
        var distant = DateTimeOffset.UtcNow.AddYears(10);
        await db.WorkflowJobs
            .Where(job => job.InstanceId != null && instanceIds.Contains(job.InstanceId.Value)
                && (job.Status == WorkflowJobStatuses.Queued || job.Status == WorkflowJobStatuses.Retry
                    || job.Status == WorkflowJobStatuses.Running || job.Status == WorkflowJobStatuses.ResultReady))
            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.DueAt, distant)
                .SetProperty(job => job.LeaseExpiresAt, job => job.LeaseExpiresAt == null ? null : distant));
    }

    private WebApplicationFactory<Program> OpenHost() =>
        fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
        }));

    private static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);

    private static string Key(string name) => $"compat-mi-{name}-{Guid.NewGuid():N}";

    private static MultiInstanceOutputVariableModel Local(string name, string type = "number") => new()
    {
        Name = name, DataType = type, IsArray = false, Nullable = false
    };

    // An automatic body of the given kind over `items`, publishing `results`. Opening one
    // (entry and dispatch) never calls a service or starts a child.
    private static FlowNodeModel Body(int id, string kind, string childKey = "") => kind switch
    {
        BpmnFlowNodeTypes.ScriptTask => new()
        {
            Id = id, Name = "Double each", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true,
            ScriptFormat = ScriptFormats.NCalc,
            MultiInstance = Multi(Local("doubled")),
            Assignments = [new() { Variable = "doubled", Expression = "[mi.item] * 2" }]
        },
        BpmnFlowNodeTypes.ServiceTask => new()
        {
            Id = id, Name = "Notify each", Type = BpmnFlowNodeTypes.ServiceTask, AsyncBefore = true,
            MultiInstance = Multi(Local("noticeId", "string")),
            Service = new()
            {
                Type = "rest", Method = "POST", Url = "https://notifications.example.test/send", TimeoutSeconds = 30,
                Body = "{\"item\":${mi.item}}",
                OutputMappings = [new() { Variable = "noticeId", Path = "id", DataType = "string", IsArray = false, Required = true }]
            }
        },
        BpmnFlowNodeTypes.CallActivity => new()
        {
            Id = id, Name = "Call each", Type = BpmnFlowNodeTypes.CallActivity,
            MultiInstance = Multi(Local("echo")),
            CallActivity = new()
            {
                WorkflowKey = childKey,
                InputMappings = [CallActivityHarness.Mapping("mi.item", "amount")],
                OutputMappings = [CallActivityHarness.Mapping("amount", "echo")]
            }
        },
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    private static MultiInstanceModel Multi(MultiInstanceOutputVariableModel output) => new()
    {
        Mode = "parallel", Source = "collection", CollectionVariable = "items", CompletionEvaluation = "afterAll",
        MaxConcurrency = 4, ResultVariable = "results", OutputVariables = [output]
    };

    private static List<VariableModel> ItemVariables(params int[] items) =>
    [
        new() { Id = 1, Name = "items", DataType = "number", IsArray = true, DefaultValue = Json(items) },
        new() { Id = 2, Name = "results", DataType = "json", DefaultValue = Json(Array.Empty<object>()) }
    ];

    private static WorkflowModel Child(string key) =>
        CallActivityHarness.ImmediateChild(key, [CallActivityHarness.Input("amount")]);

    // Start → body (2) → review (3) → end (4): the review keeps the instance running once
    // the body settles, so a version change can be tried before and after.
    private static WorkflowModel BodyThenReview(string key, string kind, string childKey, params int[] items) => new()
    {
        Id = key, Name = key, InitialEventId = 1, Variables = ItemVariables(items),
        FlowNodes =
        [
            new() { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            Body(2, kind, childKey),
            new() { Id = 3, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
            new() { Id = 4, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new() { Id = 101, SourceRef = 1, TargetRef = 2 },
            new() { Id = 201, SourceRef = 2, TargetRef = 3 },
            new() { Id = 301, Name = "Finish", SourceRef = 3, TargetRef = 4 }
        ]
    };

    [Fact]
    public async Task Administrative_discovery_candidates_and_batches_offer_the_human_execution_but_never_the_open_body()
    {
        await using var host = OpenHost();
        // A parallel split opens a human multi-instance task (2) beside a script body (5).
        var model = new WorkflowModel
        {
            Id = Key("admin"), Name = "Administrative exclusion", InitialEventId = 1, Variables = ItemVariables(1, 2, 3),
            FlowNodes =
            [
                new() { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new() { Id = 10, Name = "Split", Type = BpmnFlowNodeTypes.ParallelGateway },
                new()
                {
                    Id = 2, Name = "Approve", Type = BpmnFlowNodeTypes.UserTask,
                    MultiInstance = new()
                    {
                        Mode = "parallel", Source = MultiInstanceSources.Cardinality, CardinalityExpression = "2",
                        CompletionEvaluation = MultiInstanceCompletionEvaluations.AfterAll, ResultVariable = "approvals"
                    }
                },
                new() { Id = 3, Name = "Approved", Type = BpmnFlowNodeTypes.EndEvent },
                Body(5, BpmnFlowNodeTypes.ScriptTask),
                new() { Id = 6, Name = "Doubled", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new() { Id = 101, SourceRef = 1, TargetRef = 10 },
                new() { Id = 1001, SourceRef = 10, TargetRef = 2 },
                new() { Id = 1002, SourceRef = 10, TargetRef = 5 },
                new() { Id = 201, Name = "Approve", SourceRef = 2, TargetRef = 3, CompletionCondition = "CountFlow(201) >= 2", CompletionPriority = 1 },
                new() { Id = 202, Name = "Fallback", SourceRef = 2, TargetRef = 3, IsDefault = true, IsSelectable = false },
                new() { Id = 501, SourceRef = 5, TargetRef = 6 }
            ]
        };
        model.Variables.Add(new() { Id = 3, Name = "approvals", DataType = "json", DefaultValue = Json(Array.Empty<object>()) });
        var workflow = await PublishAsync(host, model);
        var instanceId = await StartAsync(host, workflow.Id);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);

        long humanId, bodyId, bodyTokenId;
        Guid bodyActivationId;
        DateTimeOffset bodyUpdatedAt;
        await using (var db = fixture.CreateDbContext())
        {
            var executions = await db.MultiInstanceExecutions.AsNoTracking().Where(row => row.InstanceId == instanceId)
                .ToListAsync();
            humanId = executions.Single(row => row.ActivityKind == BpmnFlowNodeTypes.UserTask).Id;
            var body = executions.Single(row => row.ActivityKind == BpmnFlowNodeTypes.ScriptTask);
            Assert.Equal(MultiInstanceRecordStatuses.Active, body.Status);
            (bodyId, bodyTokenId, bodyUpdatedAt) = (body.Id, body.TokenId, body.UpdatedAt);
            bodyActivationId = await db.ExecutionTokens.Where(row => row.Id == bodyTokenId).Select(row => row.ActivationId).SingleAsync();
            Assert.Equal(3, await db.MultiInstanceActivityItems.CountAsync(row => row.MultiInstanceExecutionId == bodyId));
        }

        // Instance discovery has no source-node filter: it lists the human parent only.
        using (var listed = await SendAsync(host, HttpMethod.Get, $"/api/instances/{instanceId}/administrative-actions?page=1&pageSize=50"))
        {
            var page = await ReadAsync<PagedResult<InstanceAdministrativeActionPositionDto>>(listed);
            var position = Assert.Single(page.Items);
            Assert.Equal(1, page.TotalCount);
            Assert.Equal(AdministrativeActionPositionKinds.MultiInstanceExecution, position.Position.PositionKind);
            Assert.Equal(humanId, position.Position.PositionId);
            Assert.Equal(2, position.Position.NodeId);
        }

        // Candidate search finds the human execution, and refuses the body's node outright.
        var candidates = await SearchAsync(host, workflow.Id, 2);
        Assert.Equal(humanId, Assert.Single(candidates.Items).PositionId);
        Assert.Equal(1, candidates.TotalCount);
        using (var bodyNode = await SendAsync(host, HttpMethod.Post, "/api/administrative-actions/candidates/search",
                   new AdministrativeActionCandidateSearchRequest { WorkflowDefinitionId = workflow.Id, SourceNodeId = 5, Page = 1, PageSize = 20 }))
            await ExpectAsync(bodyNode, HttpStatusCode.BadRequest, "is not a user task");

        // An explicit selection naming the body's execution freezes nothing.
        var body5 = new AdministrativeActionPositionReferenceDto(AdministrativeActionPositionKinds.MultiInstanceExecution, bodyId);
        using (var forged = await SendAsync(host, HttpMethod.Post, "/api/administrative-action-batches",
                   BatchRequest(workflow.Id, new(AdministrativeActionBatchSelectionModes.Explicit, [body5], null, null))))
            await ExpectAsync(forged, HttpStatusCode.BadRequest, "no longer match");
        using (var mixed = await SendAsync(host, HttpMethod.Post, "/api/administrative-action-batches",
                   BatchRequest(workflow.Id, new(AdministrativeActionBatchSelectionModes.Explicit,
                       [new(AdministrativeActionPositionKinds.MultiInstanceExecution, humanId), body5], null, null))))
            await ExpectAsync(mixed, HttpStatusCode.BadRequest, "no longer match");

        // The immediate instance action cannot address the body as a multi-instance position,
        // even with every fence copied from the body itself.
        using (var direct = await SendAsync(host, HttpMethod.Post, $"/api/instances/{instanceId}/administrative-actions",
                   new ExecuteInstanceAdministrativeActionRequest
                   {
                       ExpectedWorkflowDefinitionId = workflow.Id, SourceNodeId = 2, FlowId = 201,
                       PositionKind = AdministrativeActionPositionKinds.MultiInstanceExecution, PositionId = bodyId,
                       ExpectedTokenId = bodyTokenId, ExpectedTokenActivationId = bodyActivationId,
                       ExpectedPositionUpdatedAt = bodyUpdatedAt,
                       MultiInstanceMode = AdministrativeActionMultiInstanceModes.ForceParent,
                       ExpectedAffectedTaskCount = 3, Reason = "Forged body position"
                   }))
            Assert.Equal(HttpStatusCode.NotFound, direct.StatusCode);

        // An all-matching batch over the human node freezes, prepares and moves only the human parent.
        var batch = await CreateBatchAsync(host, BatchRequest(workflow.Id, new(AdministrativeActionBatchSelectionModes.AllMatching, null,
            new AdministrativeActionCandidateSearchRequest { WorkflowDefinitionId = workflow.Id, SourceNodeId = 2, Page = 1, PageSize = 50 },
            null)));
        Assert.Equal(1, batch.Summary.TotalItemCount);
        await ProcessAsync(host, batch.PreparationJobId!.Value);
        batch = await GetBatchAsync(host, batch.Summary.Id);
        var item = Assert.Single(await GetBatchItemsAsync(host, batch.Summary.Id));
        Assert.Equal(humanId, item.PositionId);
        Assert.Equal(AdministrativeActionPositionKinds.MultiInstanceExecution, item.PositionKind);
        using (var confirm = await SendAsync(host, HttpMethod.Post, $"/api/administrative-action-batches/{batch.Summary.Id}/confirm",
                   new ConfirmAdministrativeActionBatchRequest(batch.Summary.EligibleItemCount, batch.Summary.TotalAffectedTaskCount,
                       batch.Summary.UpdatedAt)))
            batch = await ReadAsync<AdministrativeActionBatchDetailDto>(confirm);
        await ProcessAsync(host, batch.ExecutionJobId!.Value);
        Assert.Equal(AdministrativeActionBatchStatuses.Completed, (await GetBatchAsync(host, batch.Summary.Id)).Summary.Status);

        await using (var db = fixture.CreateDbContext())
        {
            // forceParent stops the human children without votes and moves the parent once.
            Assert.Equal(MultiInstanceRecordStatuses.Interrupted,
                (await db.MultiInstanceExecutions.SingleAsync(row => row.Id == humanId)).Status);
            Assert.Contains(await db.ExecutionTokens.Where(row => row.InstanceId == instanceId).Select(row => row.NodeId).ToListAsync(),
                nodeId => nodeId == 3);
            // The body and its items are untouched by every administrative path above.
            Assert.Equal(MultiInstanceRecordStatuses.Active, (await db.MultiInstanceExecutions.SingleAsync(row => row.Id == bodyId)).Status);
            Assert.All(await db.MultiInstanceActivityItems.Where(row => row.MultiInstanceExecutionId == bodyId).ToListAsync(),
                row => Assert.Null(row.CompletedAt));
            Assert.Equal(WorkflowInstanceStatuses.Running, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        }

        // The body still finishes on its own.
        await DrainAsync(host, instanceId);
        await using var after = fixture.CreateDbContext();
        Assert.Equal(MultiInstanceRecordStatuses.Completed, (await after.MultiInstanceExecutions.SingleAsync(row => row.Id == bodyId)).Status);
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await after.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        Assert.Equal(new[] { 2, 4, 6 }, (await LatestAsync(instanceId, "results")).EnumerateArray()
            .Select(row => row.GetProperty("variables").GetProperty("doubled").GetInt32()));
    }

    [Theory]
    [InlineData(BpmnFlowNodeTypes.ScriptTask)]
    [InlineData(BpmnFlowNodeTypes.ServiceTask)]
    [InlineData(BpmnFlowNodeTypes.CallActivity)]
    public async Task A_version_change_is_blocked_from_queued_entry_until_the_body_settles(string kind)
    {
        await using var host = OpenHost();
        var childKey = Key("child");
        if (kind == BpmnFlowNodeTypes.CallActivity) await PublishAsync(host, Child(childKey));
        var source = await PublishAsync(host, BodyThenReview(Key("version"), kind, childKey, 1, 2));
        // The target differs only in a label, so nothing but the open body could block it.
        var changed = BodyThenReview(source.WorkflowKey, kind, childKey, 1, 2);
        changed.FlowNodes.Single(node => node.Id == 3).Name = "Review again";
        var target = await PublishVersionAsync(host, source.Id, changed);
        var instanceId = await StartAsync(host, source.Id);
        var entryKind = kind == BpmnFlowNodeTypes.CallActivity ? WorkflowJobKinds.MultiInstanceStart : WorkflowJobKinds.AsyncBefore;

        // A queued entry has no body row yet; it still blocks.
        var queued = await PreviewAsync(host, instanceId, target.Id);
        Assert.False(queued.Compatible);
        var entryBlocker = Assert.Single(queued.Blockers, issue => issue.Code == WorkflowVersionCompatibilityCodes.AutomaticMultiInstanceOpen);
        Assert.Equal(2, entryBlocker.NodeId);
        Assert.Contains("open entry or continuation work", entryBlocker.Message);

        await ProcessNextAsync(host, instanceId, entryKind);
        await ProcessNextAsync(host, instanceId, WorkflowJobKinds.MultiInstanceDispatch);
        long bodyId;
        await using (var db = fixture.CreateDbContext())
            bodyId = (await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == instanceId && row.ActivityKind == kind)).Id;
        var open = await PreviewAsync(host, instanceId, target.Id);
        Assert.False(open.Compatible);
        var bodyBlocker = Assert.Single(open.Blockers, issue => issue.Code == WorkflowVersionCompatibilityCodes.AutomaticMultiInstanceOpen);
        Assert.Equal(2, bodyBlocker.NodeId);
        Assert.Equal(bodyId, bodyBlocker.StateId);
        Assert.Contains($"open body execution #{bodyId}", bodyBlocker.Message);

        using (var refused = await SendAsync(host, HttpMethod.Post, $"/api/instances/{instanceId}/version-change",
                   new ChangeInstanceVersionRequest(target.Id, source.Id, open.ExpectedUpdatedAt, "Move during the body")))
            await ExpectAsync(refused, HttpStatusCode.Conflict, WorkflowVersionCompatibilityCodes.AutomaticMultiInstanceOpen);
        await using (var db = fixture.CreateDbContext())
            Assert.Equal(source.Id, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).WorkflowDefinitionId);

        if (kind == BpmnFlowNodeTypes.ServiceTask)
        {
            // No receiver answers on this host; the items stay queued. The body settles only once
            // its items run, so stop here: the blocker's lifting is pinned by the other kinds.
            return;
        }
        await DrainAsync(host, instanceId);
        var settled = await PreviewAsync(host, instanceId, target.Id);
        Assert.DoesNotContain(settled.Blockers, issue => issue.Code == WorkflowVersionCompatibilityCodes.AutomaticMultiInstanceOpen);
        Assert.True(settled.Compatible, string.Join("; ", settled.Blockers.Select(issue => issue.Code + ": " + issue.Message)));
        using (var moved = await SendAsync(host, HttpMethod.Post, $"/api/instances/{instanceId}/version-change",
                   new ChangeInstanceVersionRequest(target.Id, source.Id, settled.ExpectedUpdatedAt, "Move after the body")))
            await ExpectAsync(moved, HttpStatusCode.OK);
        await using var after = fixture.CreateDbContext();
        Assert.Equal(target.Id, (await after.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).WorkflowDefinitionId);
        Assert.Equal(MultiInstanceRecordStatuses.Completed,
            (await after.MultiInstanceExecutions.SingleAsync(row => row.Id == bodyId)).Status);
    }

    [Fact]
    public async Task Reactivation_runs_the_body_again_after_a_completed_or_cancelled_body_and_an_active_body_row_blocks_it()
    {
        await using var host = OpenHost();
        // Start → review (2) → script body (3) → end (4).
        var model = new WorkflowModel
        {
            Id = Key("reactivate"), Name = "Reactivation past a body", InitialEventId = 1, Variables = ItemVariables(4, 5),
            FlowNodes =
            [
                new() { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new() { Id = 2, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
                Body(3, BpmnFlowNodeTypes.ScriptTask),
                new() { Id = 4, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new() { Id = 101, SourceRef = 1, TargetRef = 2 },
                new() { Id = 201, Name = "Approve", SourceRef = 2, TargetRef = 3 },
                new() { Id = 301, SourceRef = 3, TargetRef = 4 }
            ]
        };
        var workflow = await PublishAsync(host, model);

        // Completed: the body ran to the end, so nothing of it blocks the review's reactivation.
        var completedId = await StartAsync(host, workflow.Id);
        await TakeReviewAsync(host, completedId);
        await DrainAsync(host, completedId);
        await AssertStatusAsync(completedId, WorkflowInstanceStatuses.Completed);
        var completedPreview = await ReactivationPreviewAsync(host, completedId);
        Assert.True(completedPreview.CanReactivate, string.Join("; ", completedPreview.Blockers.Select(issue => issue.Code)));
        await ReactivateAsync(host, completedId, completedPreview);
        await TakeReviewAsync(host, completedId);
        await DrainAsync(host, completedId);
        await AssertStatusAsync(completedId, WorkflowInstanceStatuses.Completed);
        await using (var db = fixture.CreateDbContext())
        {
            // Each run opened its own body; one open body per node never collided.
            var bodies = await db.MultiInstanceExecutions.Where(row => row.InstanceId == completedId).OrderBy(row => row.Id).ToListAsync();
            Assert.Equal(2, bodies.Count);
            Assert.All(bodies, body => Assert.Equal(MultiInstanceRecordStatuses.Completed, body.Status));
            Assert.Equal(2, bodies.Select(body => body.TokenId).Distinct().Count());
        }

        // Cancelled while the body was open: cancellation settled it, so it does not block either.
        var cancelledId = await StartAsync(host, workflow.Id);
        await TakeReviewAsync(host, cancelledId);
        await ProcessNextAsync(host, cancelledId, WorkflowJobKinds.AsyncBefore);
        await ProcessNextAsync(host, cancelledId, WorkflowJobKinds.MultiInstanceDispatch);
        using (var cancelled = await SendAsync(host, HttpMethod.Post, $"/api/instances/{cancelledId}/cancel"))
            await ExpectAsync(cancelled, HttpStatusCode.NoContent);
        long cancelledBodyId;
        await using (var db = fixture.CreateDbContext())
        {
            var body = await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == cancelledId);
            Assert.Equal(MultiInstanceRecordStatuses.Cancelled, body.Status);
            cancelledBodyId = body.Id;
        }
        var cancelledPreview = await ReactivationPreviewAsync(host, cancelledId);
        Assert.True(cancelledPreview.CanReactivate, string.Join("; ", cancelledPreview.Blockers.Select(issue => issue.Code)));

        // Fail closed: a body row still active on a terminal instance (reachable only by a
        // settlement defect or a manual edit, made here directly) blocks reactivation.
        await using (var db = fixture.CreateDbContext())
            await db.MultiInstanceExecutions.Where(row => row.Id == cancelledBodyId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, MultiInstanceRecordStatuses.Active));
        var corrupt = await ReactivationPreviewAsync(host, cancelledId);
        Assert.False(corrupt.CanReactivate);
        Assert.Contains(corrupt.Blockers, issue => issue.Code == "active_runtime_artifacts");
        using (var refused = await SendAsync(host, HttpMethod.Post, $"/api/instances/{cancelledId}/reactivation",
                   new ReactivateInstanceRequest(2, corrupt.WorkflowId, corrupt.ExpectedUpdatedAt, "Reopen over an open body")))
            Assert.False(refused.IsSuccessStatusCode);
        await using (var db = fixture.CreateDbContext())
            await db.MultiInstanceExecutions.Where(row => row.Id == cancelledBodyId)
                .ExecuteUpdateAsync(setters => setters.SetProperty(row => row.Status, MultiInstanceRecordStatuses.Cancelled));

        await ReactivateAsync(host, cancelledId, await ReactivationPreviewAsync(host, cancelledId));
        await TakeReviewAsync(host, cancelledId);
        await DrainAsync(host, cancelledId);
        await AssertStatusAsync(cancelledId, WorkflowInstanceStatuses.Completed);
        Assert.Equal(new[] { 8, 10 }, (await LatestAsync(cancelledId, "results")).EnumerateArray()
            .Select(row => row.GetProperty("variables").GetProperty("doubled").GetInt32()));
    }

    [Fact]
    public async Task Reactivating_a_parent_whose_call_body_was_cancelled_waits_until_its_children_end()
    {
        await using var host = OpenHost();
        var childKey = Key("waiting-child");
        await PublishAsync(host, CallActivityHarness.WaitingChild(childKey, [CallActivityHarness.Input("amount")]));
        // Start → review (2) → call body (3) → end (4).
        var workflow = await PublishAsync(host, new WorkflowModel
        {
            Id = Key("call-reactivate"), Name = "Reactivation past a call body", InitialEventId = 1, Variables = ItemVariables(4, 5),
            FlowNodes =
            [
                new() { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new() { Id = 2, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
                Body(3, BpmnFlowNodeTypes.CallActivity, childKey),
                new() { Id = 4, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new() { Id = 101, SourceRef = 1, TargetRef = 2 },
                new() { Id = 201, Name = "Approve", SourceRef = 2, TargetRef = 3 },
                new() { Id = 301, SourceRef = 3, TargetRef = 4 }
            ]
        });
        var parentId = await StartAsync(host, workflow.Id);
        await TakeReviewAsync(host, parentId);
        await DrainAsync(host, parentId);
        var firstChildren = await RunningChildrenAsync(parentId);
        Assert.Equal(2, firstChildren.Count);

        using (var cancelled = await SendAsync(host, HttpMethod.Post, $"/api/instances/{parentId}/cancel"))
            await ExpectAsync(cancelled, HttpStatusCode.NoContent);
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(MultiInstanceRecordStatuses.Cancelled,
                (await db.MultiInstanceExecutions.SingleAsync(row => row.InstanceId == parentId)).Status);
            // The children are told to stop by message; until it is processed they still run.
            Assert.Equal(2, await db.WorkflowInstances.CountAsync(row => firstChildren.Contains(row.Id) && row.Status == WorkflowInstanceStatuses.Running));
        }
        var waiting = await ReactivationPreviewAsync(host, parentId);
        Assert.False(waiting.CanReactivate);
        Assert.Contains(waiting.Blockers, issue => issue.Code == "running_call_descendants");
        // An item's child belongs to its parent's call tree and is never reactivated on its own.
        Assert.Contains((await ReactivationPreviewAsync(host, firstChildren[0])).Blockers, issue => issue.Code == "linked_call_child");

        await DrainAsync(host, parentId);
        await using (var db = fixture.CreateDbContext())
            Assert.Equal(2, await db.WorkflowInstances.CountAsync(row => firstChildren.Contains(row.Id) && row.Status == WorkflowInstanceStatuses.Cancelled));
        var ready = await ReactivationPreviewAsync(host, parentId);
        Assert.True(ready.CanReactivate, string.Join("; ", ready.Blockers.Select(issue => issue.Code)));
        await ReactivateAsync(host, parentId, ready);

        // The reactivated run opens a new body that starts new children.
        await TakeReviewAsync(host, parentId);
        await DrainAsync(host, parentId);
        var secondChildren = await RunningChildrenAsync(parentId);
        Assert.Equal(2, secondChildren.Count);
        Assert.Empty(secondChildren.Intersect(firstChildren));
        foreach (var child in secondChildren)
        {
            using var approved = await SendAsync(host, HttpMethod.Post, $"/api/instances/{child}/flows/{CallActivityHarness.ChildFlow}", new { });
            await ExpectAsync(approved, HttpStatusCode.OK);
        }
        await DrainAsync(host, parentId);
        await AssertStatusAsync(parentId, WorkflowInstanceStatuses.Completed);
        Assert.Equal(new[] { 4, 5 }, (await LatestAsync(parentId, "results")).EnumerateArray()
            .Select(row => row.GetProperty("variables").GetProperty("echo").GetInt32()));
        await using var after = fixture.CreateDbContext();
        Assert.Equal(new[] { MultiInstanceRecordStatuses.Cancelled, MultiInstanceRecordStatuses.Completed },
            await after.MultiInstanceExecutions.Where(row => row.InstanceId == parentId).OrderBy(row => row.Id)
                .Select(row => row.Status).ToListAsync());
    }

    private async Task<List<long>> RunningChildrenAsync(long root)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowInstances.Where(row => row.CallRootInstanceId == root && row.Status == WorkflowInstanceStatuses.Running)
            .OrderBy(row => row.Id).Select(row => row.Id).ToListAsync();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Diagram_history_counts_a_body_activation_once_whatever_its_item_count(bool asyncAfterSource)
    {
        await using var host = OpenHost();
        WorkflowModel Model(params int[] items)
        {
            var model = new WorkflowModel
            {
                Id = Key("diagram"), Name = "Diagram history of a body", InitialEventId = 1, Variables = ItemVariables(items),
                FlowNodes =
                [
                    new() { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                    Body(2, BpmnFlowNodeTypes.ScriptTask),
                    new() { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
                ],
                SequenceFlows = [new() { Id = 101, SourceRef = 1, TargetRef = 2 }, new() { Id = 201, SourceRef = 2, TargetRef = 3 }]
            };
            if (asyncAfterSource)
            {
                model.FlowNodes.Add(new() { Id = 5, Name = "Deferred source", Type = BpmnFlowNodeTypes.Task, AsyncAfter = true });
                model.SequenceFlows[0].TargetRef = 5;
                model.SequenceFlows.Add(new() { Id = 501, SourceRef = 5, TargetRef = 2 });
            }
            return model;
        }
        var entryFlow = asyncAfterSource ? 501 : 101;

        var full = await StartAsync(host, (await PublishAsync(host, Model(1, 2, 3))).Id);
        await DrainAsync(host, full);
        await AssertStatusAsync(full, WorkflowInstanceStatuses.Completed);
        var fullHistory = await HistoryAsync(host, full);
        // Three item visits are one activation: the entry and the exit each count once.
        Assert.Equal(1, fullHistory.Flows.Single(flow => flow.FlowId == entryFlow).TraversalCount);
        Assert.Equal(1, fullHistory.Flows.Single(flow => flow.FlowId == 201).TraversalCount);
        Assert.Contains(fullHistory.Nodes, node => node.NodeId == 2);
        Assert.Contains(fullHistory.Nodes, node => node.NodeId == 3);
        Assert.Equal(asyncAfterSource ? "unknown" : "complete", fullHistory.Coverage.Status);
        if (asyncAfterSource) Assert.Contains("multiInstanceEntryUnrecorded", fullHistory.Coverage.Reasons);
        await using (var db = fixture.CreateDbContext())
            Assert.Equal(3, await db.NodeExecutions.CountAsync(row => row.InstanceId == full && row.NodeId == 2
                && row.ExecutionKind == "automaticActivityItem"));

        var empty = await StartAsync(host, (await PublishAsync(host, Model())).Id);
        await DrainAsync(host, empty);
        await AssertStatusAsync(empty, WorkflowInstanceStatuses.Completed);
        var emptyHistory = await HistoryAsync(host, empty);
        await using (var db = fixture.CreateDbContext())
            Assert.False(await db.NodeExecutions.AnyAsync(row => row.InstanceId == empty && row.NodeId == 2));
        // The exit is counted from the target's entry, so it holds without any item visit.
        Assert.Equal(1, emptyHistory.Flows.Single(flow => flow.FlowId == 201).TraversalCount);
        Assert.Contains(emptyHistory.Nodes, node => node.NodeId == 3);
        if (asyncAfterSource)
        {
            // An empty body leaves no item to record its entry; the coverage says so.
            Assert.DoesNotContain(emptyHistory.Flows, flow => flow.FlowId == entryFlow);
            Assert.Equal("unknown", emptyHistory.Coverage.Status);
            Assert.Contains("multiInstanceEntryUnrecorded", emptyHistory.Coverage.Reasons);
        }
        else
        {
            // The start's exit counts the entry and marks the body reached.
            Assert.Equal(1, emptyHistory.Flows.Single(flow => flow.FlowId == entryFlow).TraversalCount);
            Assert.Contains(emptyHistory.Nodes, node => node.NodeId == 2);
            Assert.Equal("complete", emptyHistory.Coverage.Status);
        }
    }

    private static CreateAdministrativeActionBatchRequest BatchRequest(long workflowId, AdministrativeActionBatchSelectionDto selection) =>
        new(workflowId, 2, AdministrativeActionKinds.DirectFlow, 201, null, AdministrativeActionMultiInstanceModes.ForceParent,
            "Automatic body exclusion", null, selection, null);

    private async Task<PagedResult<AdministrativeActionCandidateDto>> SearchAsync(WebApplicationFactory<Program> host, long workflowId, int nodeId)
    {
        using var response = await SendAsync(host, HttpMethod.Post, "/api/administrative-actions/candidates/search",
            new AdministrativeActionCandidateSearchRequest { WorkflowDefinitionId = workflowId, SourceNodeId = nodeId, Page = 1, PageSize = 50 });
        return await ReadAsync<PagedResult<AdministrativeActionCandidateDto>>(response);
    }

    private async Task<AdministrativeActionBatchDetailDto> CreateBatchAsync(WebApplicationFactory<Program> host,
        CreateAdministrativeActionBatchRequest request)
    {
        using var response = await SendAsync(host, HttpMethod.Post, "/api/administrative-action-batches", request);
        return await ReadAsync<AdministrativeActionBatchDetailDto>(response, HttpStatusCode.Accepted);
    }

    private async Task<AdministrativeActionBatchDetailDto> GetBatchAsync(WebApplicationFactory<Program> host, long batchId)
    {
        using var response = await SendAsync(host, HttpMethod.Get, $"/api/administrative-action-batches/{batchId}");
        return await ReadAsync<AdministrativeActionBatchDetailDto>(response);
    }

    private async Task<IReadOnlyList<AdministrativeActionBatchItemDto>> GetBatchItemsAsync(WebApplicationFactory<Program> host, long batchId)
    {
        using var response = await SendAsync(host, HttpMethod.Get, $"/api/administrative-action-batches/{batchId}/items?page=1&pageSize=200");
        return (await ReadAsync<PagedResult<AdministrativeActionBatchItemDto>>(response)).Items;
    }

    private async Task<InstanceVersionChangePreviewDto> PreviewAsync(WebApplicationFactory<Program> host, long instanceId, long targetId)
    {
        using var response = await SendAsync(host, HttpMethod.Post, $"/api/instances/{instanceId}/version-change/preview",
            new PreviewInstanceVersionChangeRequest(targetId));
        return await ReadAsync<InstanceVersionChangePreviewDto>(response);
    }

    private async Task<InstanceReactivationPreviewDto> ReactivationPreviewAsync(WebApplicationFactory<Program> host, long instanceId)
    {
        using var response = await SendAsync(host, HttpMethod.Get, $"/api/instances/{instanceId}/reactivation");
        return await ReadAsync<InstanceReactivationPreviewDto>(response);
    }

    private async Task ReactivateAsync(WebApplicationFactory<Program> host, long instanceId, InstanceReactivationPreviewDto preview)
    {
        using var response = await SendAsync(host, HttpMethod.Post, $"/api/instances/{instanceId}/reactivation",
            new ReactivateInstanceRequest(2, preview.WorkflowId, preview.ExpectedUpdatedAt, "Review once more"));
        await ExpectAsync(response, HttpStatusCode.OK);
    }

    private async Task<InstanceDiagramHistoryDto> HistoryAsync(WebApplicationFactory<Program> host, long instanceId)
    {
        using var response = await SendAsync(host, HttpMethod.Get, $"/api/instances/{instanceId}/diagram-history");
        return await ReadAsync<InstanceDiagramHistoryDto>(response);
    }

    private async Task TakeReviewAsync(WebApplicationFactory<Program> host, long instanceId)
    {
        long taskId;
        await using (var db = fixture.CreateDbContext())
            taskId = await db.UserTasks.Where(task => task.InstanceId == instanceId && task.Status == "active")
                .Select(task => task.Id).SingleAsync();
        using var response = await SendAsync(host, HttpMethod.Post, $"/api/user-tasks/{taskId}/flows/201", new TakeFlowRequest(null));
        await ExpectAsync(response, HttpStatusCode.OK);
    }

    private async Task AssertStatusAsync(long instanceId, string status)
    {
        await using var db = fixture.CreateDbContext();
        Assert.Equal(status, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
    }

    private static async Task<HttpResponseMessage> SendAsync(WebApplicationFactory<Program> host, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, body.GetType(), options: JsonOptions);
        ApiTestAuth.Authorize(request, "compat-admin", "admin");
        request.Headers.TryAddWithoutValidation("X-Test-Suppress-Admin", "true");
        return await host.CreateClient().SendAsync(request);
    }

    private static async Task ExpectAsync(HttpResponseMessage response, HttpStatusCode expected, string? contains = null)
    {
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == expected, $"Expected {expected}; got {response.StatusCode}: {text}");
        if (contains is not null) Assert.Contains(contains, text);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        await ExpectAsync(response, expected);
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
    }

    private async Task<WorkflowDetailDto> PublishAsync(WebApplicationFactory<Program> host, WorkflowModel definition)
    {
        using var response = await SendAsync(host, HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(definition, true));
        return await ReadAsync<WorkflowDetailDto>(response, HttpStatusCode.Created);
    }

    private async Task<WorkflowDetailDto> PublishVersionAsync(WebApplicationFactory<Program> host, long previousId, WorkflowModel definition)
    {
        using var response = await SendAsync(host, HttpMethod.Put, $"/api/workflows/{previousId}", new UpdateWorkflowRequest(definition, true));
        return await ReadAsync<WorkflowDetailDto>(response);
    }

    private async Task<long> StartAsync(WebApplicationFactory<Program> host, long workflowId)
    {
        using var response = await SendAsync(host, HttpMethod.Post, "/api/instances?detail=full", new StartInstanceRequest(workflowId, null, null, null));
        var started = await ReadAsync<InstanceDetailDto>(response, HttpStatusCode.Created);
        roots.Add(started.Id);
        return started.Id;
    }

    private async Task<List<long>> TreeAsync(long root)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowInstances.AsNoTracking()
            .Where(instance => instance.Id == root || instance.CallRootInstanceId == root)
            .Select(instance => instance.Id).ToListAsync();
    }

    private async Task ProcessNextAsync(WebApplicationFactory<Program> host, long instanceId, string kind)
    {
        await using var db = fixture.CreateDbContext();
        var jobId = await db.WorkflowJobs.AsNoTracking()
            .Where(job => job.InstanceId == instanceId && job.Kind == kind
                && (job.Status == WorkflowJobStatuses.Queued || job.Status == WorkflowJobStatuses.Retry))
            .OrderBy(job => job.Id).Select(job => job.Id).FirstAsync();
        await ProcessAsync(host, jobId);
    }

    // Leases and runs one job. Workers lease due control work first, so another due control
    // job of the same call tree may run first, as a Worker would run it.
    private async Task ProcessAsync(WebApplicationFactory<Program> host, long jobId)
    {
        for (var attempt = 0; attempt < 16; attempt++)
        {
            long? targetInstance;
            await using (var promote = fixture.CreateDbContext())
            {
                // Due a minute ago, so the job is due even when the database clock runs behind this machine's.
                await promote.WorkflowJobs.Where(job => job.Id == jobId).ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Priority, 1_000_000)
                    .SetProperty(job => job.DueAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
                targetInstance = await promote.WorkflowJobs.Where(job => job.Id == jobId).Select(job => job.InstanceId).SingleAsync();
            }
            await using var scope = host.Services.CreateAsyncScope();
            var leases = await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().LeaseRunnableAsync(
                new WorkflowJobLeaseRequest($"compat-mi:{Guid.NewGuid():N}", MaxCount: 1, MaxActivityCount: 1,
                    MaxPerInstance: 4, LeaseDuration: TimeSpan.FromMinutes(1)), CancellationToken.None);
            var lease = Assert.Single(leases);
            if (lease.Job.Id != jobId)
            {
                var tree = roots.SelectMany(root => TreeAsync(root).GetAwaiter().GetResult()).ToHashSet();
                Assert.True(lease.Job.QueueClass == WorkflowJobClasses.Control && lease.Job.InstanceId is long leased
                    && (leased == targetInstance || tree.Contains(leased)),
                    $"Leased #{lease.Job.Id} ({lease.Job.Kind}) instead of #{jobId}.");
            }
            await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(lease, CancellationToken.None);
            if (lease.Job.Id == jobId) return;
        }
        Assert.Fail($"Job #{jobId} was not leased.");
    }

    // Runs runnable jobs of the instance and its call children until none remains.
    private async Task DrainAsync(WebApplicationFactory<Program> host, long root, int maxJobs = 64)
    {
        for (var processed = 0; processed < maxJobs; processed++)
        {
            var tree = await TreeAsync(root);
            await using var db = fixture.CreateDbContext();
            var next = await db.WorkflowJobs.AsNoTracking()
                .Where(job => job.InstanceId != null && tree.Contains(job.InstanceId.Value)
                    && (job.Status == WorkflowJobStatuses.Queued || job.Status == WorkflowJobStatuses.Retry))
                .OrderBy(job => job.Id).Select(job => (long?)job.Id).FirstOrDefaultAsync();
            if (next is not long jobId) return;
            await ProcessAsync(host, jobId);
        }
        throw new InvalidOperationException($"Instance #{root} still had runnable jobs after {maxJobs}.");
    }

    private async Task<JsonElement> LatestAsync(long instanceId, string name)
    {
        await using var db = fixture.CreateDbContext();
        return (await db.InstanceVariables.Where(row => row.InstanceId == instanceId && row.VariableName == name)
            .OrderByDescending(row => row.Id).FirstAsync()).ValueJson.RootElement.Clone();
    }
}
