using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// A token entering a link throw continues at its catch in the same transaction and on the same gateway branch,
/// whatever moved it onto the throw. Both events are recorded as node visits and history rows; only the flows on
/// either side of the link carry flow evidence.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class LinkEventRuntimeTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const string LinkReaderRole = "LinkActivityReader";

    [Fact]
    public async Task UserAction_HopsToTheCatchAndRecordsBothEventsHistoryAndFlowEvidence()
    {
        // Review -> throw; catch -> gateway (reads FlowInfo, so evidence is recorded) -> pack.
        var model = Model(
            [Node(1, BpmnFlowNodeTypes.StartEvent), Node(2, BpmnFlowNodeTypes.UserTask),
             Link(3, true, "Fulfilment"), Link(4, false, "Fulfilment"), Node(5, BpmnFlowNodeTypes.ExclusiveGateway),
             Node(6, BpmnFlowNodeTypes.UserTask), Node(7, BpmnFlowNodeTypes.EndEvent)],
            [Flow(101, 1, 2), Flow(201, 2, 3), Flow(401, 4, 5),
             new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 6, ConditionPriority = 1,
                 Condition = "FlowInfo(401, 'traversals.count') >= 1" },
             new SequenceFlowModel { Id = 502, SourceRef = 5, TargetRef = 7, IsDefault = true },
             Flow(601, 6, 7)]);
        // Node-activity search visibility comes from this version's own manager roles, so it does not depend on the
        // shared NodeExecution.RequiredRole setting other test classes change.
        model.TaskAssignmentRoles = [LinkReaderRole];
        var instance = await StartAsync(model);

        await TakeAsync(instance.Id, 2, 201);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(6, await ActiveTaskNodeAsync(instance.Id));
        var history = await db.InstanceHistory.AsNoTracking()
            .Where(item => item.InstanceId == instance.Id).OrderBy(item => item.Id).ToListAsync();
        var hop = Assert.Single(history, item => item.Note == InstanceHistoryNotes.LinkThrow);
        Assert.Equal((3, 4, (int?)null), (hop.FromStepId, hop.ToStepId, hop.ActionId));
        Assert.Equal("link-runtime", hop.PerformedBy);
        var leave = Assert.Single(history, item => item.Note == InstanceHistoryNotes.LinkCatch);
        Assert.Equal((4, 5, (int?)null), (leave.FromStepId, leave.ToStepId, leave.ActionId));
        var entered = Assert.Single(history, item => item.FromStepId == 2 && item.ToStepId == 3);
        Assert.True(entered.Id < hop.Id && hop.Id < leave.Id);
        Assert.Contains(history, item => item.FromStepId == 5 && item.ToStepId == 6 && item.Id > leave.Id);

        var visits = await VisitsAsync(instance.Id);
        var throwVisit = Assert.Single(visits, item => item.NodeId == 3);
        Assert.Equal(BpmnFlowNodeTypes.IntermediateLinkThrowEvent, throwVisit.NodeType);
        Assert.Equal((201, (int?)null), (throwVisit.EnteredViaFlowId, throwVisit.ExitedViaFlowId));
        Assert.Equal((NodeExecutionStatuses.Completed, NodeExecutionCompletionReasons.Normal),
            (throwVisit.Status, throwVisit.CompletionReason));
        var catchVisit = Assert.Single(visits, item => item.NodeId == 4);
        Assert.Equal(BpmnFlowNodeTypes.IntermediateLinkCatchEvent, catchVisit.NodeType);
        Assert.Equal(((int?)null, 401), (catchVisit.EnteredViaFlowId, catchVisit.ExitedViaFlowId));
        Assert.Equal((NodeExecutionStatuses.Completed, NodeExecutionCompletionReasons.Normal),
            (catchVisit.Status, catchVisit.CompletionReason));
        Assert.Equal(throwVisit.ExecutionTokenId, catchVisit.ExecutionTokenId);

        var occurrences = await db.SequenceFlowOccurrences.AsNoTracking()
            .Where(item => item.InstanceId == instance.Id).ToListAsync();
        Assert.DoesNotContain(occurrences, item => item.SourceNodeId == 3 || item.TargetNodeId == 4);
        Assert.Single(occurrences, item => item.SequenceFlowId == 201 && item.IsTraversal);
        var catchFlow = Assert.Single(occurrences, item => item.SequenceFlowId == 401);
        Assert.Equal(InstanceHistoryNotes.LinkCatch, catchFlow.Kind);
        Assert.Single(occurrences, item => item.SequenceFlowId == 501);

        await DiagramHistoryEvidenceAssertions.SummaryAsync(fixture, instance.Id, [1, 2, 3, 4, 5, 6],
            (101, 1), (201, 1), (401, 1), (501, 1));

        foreach (var (type, nodeId) in new[] { (BpmnFlowNodeTypes.IntermediateLinkThrowEvent, 3), (BpmnFlowNodeTypes.IntermediateLinkCatchEvent, 4) })
        {
            var found = await SearchAsync($"/api/node-executions?instanceId={instance.Id}&nodeType={type}");
            Assert.Equal(nodeId, Assert.Single(found.Items).NodeId);
        }
    }

    [Fact]
    public async Task TwoThrowsShareOneCatch()
    {
        var model = Model(
            [Node(1, BpmnFlowNodeTypes.StartEvent), Node(2, BpmnFlowNodeTypes.UserTask),
             Link(3, true, "Close"), Link(6, true, "Close"), Link(4, false, "Close"),
             Node(5, BpmnFlowNodeTypes.UserTask), Node(7, BpmnFlowNodeTypes.EndEvent)],
            [Flow(101, 1, 2), Flow(201, 2, 3), Flow(202, 2, 6), Flow(401, 4, 5), Flow(501, 5, 7)]);
        var first = await StartAsync(model);
        var second = await StartExistingAsync(first.Workflow.Id);

        await TakeAsync(first.Id, 2, 201);
        await TakeAsync(second.Id, 2, 202);

        Assert.Equal(5, await ActiveTaskNodeAsync(first.Id));
        Assert.Equal(5, await ActiveTaskNodeAsync(second.Id));
        Assert.Equal([1, 2, 3, 4], (await VisitsAsync(first.Id)).Where(item => item.Status == NodeExecutionStatuses.Completed)
            .Select(item => item.NodeId).Order());
        Assert.Equal([1, 2, 4, 6], (await VisitsAsync(second.Id)).Where(item => item.Status == NodeExecutionStatuses.Completed)
            .Select(item => item.NodeId).Order());
    }

    [Fact]
    public async Task LinkInsideAParallelBranchKeepsItsBranchUntilTheJoin()
    {
        var model = Model(
            [Node(1, BpmnFlowNodeTypes.StartEvent), Node(2, BpmnFlowNodeTypes.ParallelGateway),
             Node(3, BpmnFlowNodeTypes.UserTask), Link(4, true, "Join"), Link(5, false, "Join"),
             Node(8, BpmnFlowNodeTypes.ParallelGateway), Node(9, BpmnFlowNodeTypes.EndEvent)],
            [Flow(101, 1, 2), Flow(201, 2, 3), Flow(202, 2, 4), Flow(301, 3, 8), Flow(501, 5, 8), Flow(801, 8, 9)]);
        var instance = await StartAsync(model);

        var visits = await VisitsAsync(instance.Id);
        var throwVisit = Assert.Single(visits, item => item.NodeId == 4);
        var catchVisit = Assert.Single(visits, item => item.NodeId == 5);
        Assert.NotNull(throwVisit.EntryGatewayBranchId);
        Assert.Equal(throwVisit.EntryGatewayBranchId, throwVisit.ExitGatewayBranchId);
        Assert.Equal(throwVisit.EntryGatewayBranchId, catchVisit.EntryGatewayBranchId);
        Assert.Equal("running", (await DetailAsync(instance.Id)).Status);

        await TakeAsync(instance.Id, 3, 301);

        Assert.Equal("completed", (await DetailAsync(instance.Id)).Status);
        Assert.Single(await VisitsAsync(instance.Id), item => item.NodeId == 8
            && item.CompletionReason == NodeExecutionCompletionReasons.ParallelJoin);
    }

    [Fact]
    public async Task InclusiveMergeWaitsForATokenThatCanStillReachItThroughALink()
    {
        var model = Model(
            [Node(1, BpmnFlowNodeTypes.StartEvent), Node(2, BpmnFlowNodeTypes.InclusiveGateway),
             Node(3, BpmnFlowNodeTypes.UserTask), Node(4, BpmnFlowNodeTypes.UserTask),
             Link(5, true, "Merge"), Link(7, false, "Merge"),
             Node(6, BpmnFlowNodeTypes.InclusiveGateway), Node(8, BpmnFlowNodeTypes.EndEvent), Node(10, BpmnFlowNodeTypes.EndEvent)],
            [Flow(101, 1, 2),
             new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3, Condition = "true" },
             new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 4, Condition = "true" },
             new SequenceFlowModel { Id = 203, SourceRef = 2, TargetRef = 10, IsDefault = true },
             Flow(301, 3, 6), Flow(401, 4, 5), Flow(701, 7, 6), Flow(601, 6, 8)]);
        var instance = await StartAsync(model);

        await TakeAsync(instance.Id, 3, 301);

        // The other token can still reach input 701 through the link, so the merge waits.
        Assert.Equal("running", (await DetailAsync(instance.Id)).Status);
        Assert.Equal(4, await ActiveTaskNodeAsync(instance.Id));
        Assert.DoesNotContain(await VisitsAsync(instance.Id), item => item.NodeId == 8);

        await TakeAsync(instance.Id, 4, 401);

        Assert.Equal("completed", (await DetailAsync(instance.Id)).Status);
        Assert.Single(await VisitsAsync(instance.Id), item => item.NodeId == 6
            && item.CompletionReason == NodeExecutionCompletionReasons.InclusiveMerge);
    }

    [Fact]
    public async Task LoopThroughALinkReturnsToAnEarlierTask()
    {
        var model = Model(
            [Node(1, BpmnFlowNodeTypes.StartEvent), Link(4, false, "Rework"), Node(2, BpmnFlowNodeTypes.UserTask),
             Link(3, true, "Rework"), Node(5, BpmnFlowNodeTypes.EndEvent)],
            [Flow(101, 1, 2), Flow(201, 2, 3), Flow(202, 2, 5), Flow(401, 4, 2)]);
        var instance = await StartAsync(model);

        await TakeAsync(instance.Id, 2, 201);
        await TakeAsync(instance.Id, 2, 201);
        await TakeAsync(instance.Id, 2, 202);

        Assert.Equal("completed", (await DetailAsync(instance.Id)).Status);
        var visits = await VisitsAsync(instance.Id);
        Assert.Equal(3, visits.Count(item => item.NodeId == 2));
        Assert.Equal(2, visits.Count(item => item.NodeId == 3));
        Assert.Equal(2, visits.Count(item => item.NodeId == 4));
        await DiagramHistoryEvidenceAssertions.SummaryAsync(fixture, instance.Id, [1, 2, 3, 4, 5],
            (101, 1), (201, 2), (202, 1), (401, 2));
    }

    [Fact]
    public async Task ALinkOnlyCycleFailsWithTheRoutingCycleError()
    {
        // Review -> throw; catch -> automatic task -> throw with the same name: nothing ever rests.
        var model = Model(
            [Node(1, BpmnFlowNodeTypes.StartEvent), Node(2, BpmnFlowNodeTypes.UserTask),
             Link(3, true, "Spin"), Link(4, false, "Spin"), Node(5, BpmnFlowNodeTypes.Task), Link(6, true, "Spin")],
            [Flow(101, 1, 2), Flow(201, 2, 3), Flow(401, 4, 5), Flow(501, 5, 6)]);
        var instance = await StartAsync(model);
        var taskId = await ActiveTaskIdAsync(instance.Id, 2);

        using var response = await SendAsync(HttpMethod.Post, $"/api/user-tasks/{taskId}/flows/201", new TakeFlowRequest(null));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("Pass-through routing cycle detected.", await response.Content.ReadAsStringAsync());
        Assert.Equal(2, await ActiveTaskNodeAsync(instance.Id));
        Assert.DoesNotContain(await VisitsAsync(instance.Id), item => item.NodeId == 3);
    }

    [Fact]
    public async Task MessageDeliveryArrivesOnAThrow()
    {
        var model = Model(
            [Node(1, BpmnFlowNodeTypes.StartEvent),
             new FlowNodeModel { Id = 2, Name = "Wait", Type = BpmnFlowNodeTypes.IntermediateMessageCatchEvent,
                 Message = new MessageCatchModel { ClientId = "tests-client", ClientSecret = "tests-secret",
                     HeaderName = "X-Correlation", HeaderValue = "accepted" } },
             Link(3, true, "After"), Link(4, false, "After"), Node(5, BpmnFlowNodeTypes.EndEvent)],
            [Flow(101, 1, 2), Flow(201, 2, 3), Flow(401, 4, 5)]);
        var instance = await StartAsync(model);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/instances/{instance.Id}/message")
        {
            Content = JsonContent.Create(new { }, options: JsonOptions)
        };
        request.Headers.Add("X-Client-Id", "tests-client");
        request.Headers.Add("X-Client-Secret", "tests-secret");
        request.Headers.Add("X-Correlation", "accepted");

        using var response = await fixture.Client.SendAsync(request);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        await AssertCrossedLinkAndCompletedAsync(instance.Id, 201, 401);
    }

    [Fact]
    public async Task TimerBoundaryArrivesOnAThrow()
    {
        var harness = new CallActivityHarness(fixture);
        var model = Model(
            [Node(1, BpmnFlowNodeTypes.StartEvent), Node(2, BpmnFlowNodeTypes.UserTask), Node(7, BpmnFlowNodeTypes.EndEvent),
             new FlowNodeModel { Id = 6, Name = "Late", Type = BpmnFlowNodeTypes.TimerBoundaryEvent, AttachedToRef = 2,
                 Timer = new TimerDefinitionModel { TimeDuration = "PT1S" } },
             Link(3, true, "Escalate"), Link(4, false, "Escalate"), Node(5, BpmnFlowNodeTypes.EndEvent)],
            [Flow(101, 1, 2), Flow(201, 2, 7), Flow(601, 6, 3), Flow(401, 4, 5)]);
        var instance = await StartAsync(model);
        long jobId;
        await using (var db = fixture.CreateDbContext())
        {
            jobId = await db.WorkflowJobs.Where(job => job.InstanceId == instance.Id
                && job.Kind == WorkflowJobKinds.TimerBoundary).Select(job => job.Id).SingleAsync();
        }
        await Task.Delay(TimeSpan.FromSeconds(1.5));

        await harness.ProcessAsync(jobId);

        await AssertCrossedLinkAndCompletedAsync(instance.Id, 601, 401);
    }

    [Fact]
    public async Task AdministrativeActionArrivesOnAThrow()
    {
        var model = Model(
            [Node(1, BpmnFlowNodeTypes.StartEvent), Node(2, BpmnFlowNodeTypes.UserTask),
             Link(3, true, "Override"), Link(4, false, "Override"), Node(5, BpmnFlowNodeTypes.UserTask),
             Node(6, BpmnFlowNodeTypes.EndEvent)],
            [Flow(101, 1, 2), Flow(201, 2, 3), Flow(401, 4, 5), Flow(501, 5, 6)]);
        var instance = await StartAsync(model);
        using var positionsResponse = await SendAsync(HttpMethod.Get, $"/api/instances/{instance.Id}/administrative-actions");
        Assert.True(positionsResponse.IsSuccessStatusCode, await positionsResponse.Content.ReadAsStringAsync());
        var position = Assert.Single((await positionsResponse.Content
            .ReadFromJsonAsync<PagedResult<InstanceAdministrativeActionPositionDto>>(JsonOptions))!.Items).Position;

        using var response = await SendAsync(HttpMethod.Post, $"/api/instances/{instance.Id}/administrative-actions",
            new ExecuteInstanceAdministrativeActionRequest
            {
                ExpectedWorkflowDefinitionId = position.WorkflowDefinitionId,
                SourceNodeId = position.NodeId,
                FlowId = 201,
                PositionKind = position.PositionKind,
                PositionId = position.PositionId,
                ExpectedTokenId = position.TokenId,
                ExpectedTokenActivationId = position.TokenActivationId,
                ExpectedPositionUpdatedAt = position.PositionUpdatedAt,
                ExpectedAffectedTaskCount = position.AffectedTaskCount,
                Reason = "Route through the link"
            });

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal(5, await ActiveTaskNodeAsync(instance.Id));
        var visits = await VisitsAsync(instance.Id);
        Assert.Equal(201, Assert.Single(visits, item => item.NodeId == 3).EnteredViaFlowId);
        Assert.Equal(401, Assert.Single(visits, item => item.NodeId == 4).ExitedViaFlowId);
    }

    [Fact]
    public async Task AsyncAfterContinuationArrivesOnAThrow()
    {
        var harness = new CallActivityHarness(fixture);
        var model = Model(
            [Node(1, BpmnFlowNodeTypes.StartEvent),
             new FlowNodeModel { Id = 2, Name = "Score", Type = BpmnFlowNodeTypes.ScriptTask, AsyncAfter = true,
                 Assignments = [new AssignmentModel { Variable = "score", Expression = "1" }] },
             Link(3, true, "Next"), Link(4, false, "Next"), Node(5, BpmnFlowNodeTypes.EndEvent)],
            [Flow(101, 1, 2), Flow(201, 2, 3), Flow(401, 4, 5)]);
        model.Variables = [new VariableModel { Id = 1, Name = "score", DataType = WorkflowVariableTypes.Number, Nullable = true }];
        var instance = await StartAsync(model);
        try
        {
            // A script with asyncAfter runs in its own job first, then continues in an asyncAfter job.
            await harness.DrainAsync(instance.Id);

            await using (var db = fixture.CreateDbContext())
            {
                Assert.True(await db.WorkflowJobs.AnyAsync(job => job.InstanceId == instance.Id
                    && job.Kind == WorkflowJobKinds.AsyncAfter && job.Status == WorkflowJobStatuses.Completed));
            }
            await AssertCrossedLinkAndCompletedAsync(instance.Id, 201, 401);
        }
        finally
        {
            await harness.RetireLeftoverJobsAsync();
        }
    }

    [Fact]
    public async Task FinishedCallArrivesOnAThrow()
    {
        var harness = new CallActivityHarness(fixture);
        try
        {
            var childKey = CallActivityHarness.Key("link-child");
            await harness.PublishAsync(CallActivityHarness.ImmediateChild(childKey));
            var model = CallActivityHarness.Parent(CallActivityHarness.Key("link-parent"), childKey);
            model.FlowNodes.Single(node => node.Id == CallActivityHarness.End).Type = BpmnFlowNodeTypes.IntermediateLinkThrowEvent;
            model.FlowNodes.Single(node => node.Id == CallActivityHarness.End).Name = "Called";
            model.FlowNodes.Add(Link(4, false, "Called"));
            model.FlowNodes.Add(Node(5, BpmnFlowNodeTypes.EndEvent));
            model.SequenceFlows.Add(Flow(401, 4, 5));
            var definition = await harness.PublishAsync(model);
            var instanceId = await harness.StartAsync(definition.Id);
            var invocation = await harness.InvocationAsync(instanceId);

            await harness.ProcessCallJobAsync(invocation.Id, WorkflowJobKinds.CallStart);
            await harness.ProcessCallJobAsync(invocation.Id, WorkflowJobKinds.CallResume);

            await AssertCrossedLinkAndCompletedAsync(instanceId, 201, 401, throwId: CallActivityHarness.End);
        }
        finally
        {
            await harness.RetireLeftoverJobsAsync();
        }
    }

    [Fact]
    public async Task MultiInstanceOutcomeArrivesOnAThrow()
    {
        var model = Model(
            [Node(1, BpmnFlowNodeTypes.StartEvent),
             new FlowNodeModel { Id = 2, Name = "Approvals", Type = BpmnFlowNodeTypes.UserTask,
                 MultiInstance = new MultiInstanceModel { Mode = MultiInstanceModes.Parallel,
                     Source = MultiInstanceSources.Cardinality, CardinalityExpression = "1", ResultVariable = "results" } },
             Link(3, true, "Approved"), Link(4, false, "Approved"), Node(5, BpmnFlowNodeTypes.EndEvent),
             Node(6, BpmnFlowNodeTypes.EndEvent)],
            [Flow(101, 1, 2),
             new SequenceFlowModel { Id = 201, Name = "Approve", SourceRef = 2, TargetRef = 3,
                 CompletionCondition = "CountFlow(201) >= 1", CompletionPriority = 1 },
             new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 6, IsDefault = true, IsSelectable = false },
             Flow(401, 4, 5)]);
        model.Variables = [new VariableModel { Id = 1, Name = "results", DataType = WorkflowVariableTypes.Json,
            DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>()) }];
        var instance = await StartAsync(model);

        await TakeAsync(instance.Id, 2, 201);

        await AssertCrossedLinkAndCompletedAsync(instance.Id, 201, 401);
    }

    private async Task AssertCrossedLinkAndCompletedAsync(long instanceId, int intoThrow, int outOfCatch, int throwId = 3)
    {
        Assert.Equal("completed", (await DetailAsync(instanceId)).Status);
        var visits = await VisitsAsync(instanceId);
        var throwVisit = Assert.Single(visits, item => item.NodeId == throwId);
        Assert.Equal((intoThrow, (int?)null, NodeExecutionStatuses.Completed),
            (throwVisit.EnteredViaFlowId, throwVisit.ExitedViaFlowId, throwVisit.Status));
        var catchVisit = Assert.Single(visits, item => item.NodeId == 4);
        Assert.Equal(((int?)null, outOfCatch, NodeExecutionStatuses.Completed),
            (catchVisit.EnteredViaFlowId, catchVisit.ExitedViaFlowId, catchVisit.Status));
        await using var db = fixture.CreateDbContext();
        Assert.True(await db.InstanceHistory.AnyAsync(item => item.InstanceId == instanceId
            && item.Note == InstanceHistoryNotes.LinkThrow && item.FromStepId == throwId && item.ToStepId == 4));
    }

    private async Task<List<NodeExecutionEntity>> VisitsAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.NodeExecutions.AsNoTracking().Where(item => item.InstanceId == instanceId)
            .OrderBy(item => item.Id).ToListAsync();
    }

    private async Task<int> ActiveTaskNodeAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.UserTasks.Where(item => item.InstanceId == instanceId && item.Status == UserTaskStatuses.Active)
            .Select(item => item.NodeId).SingleAsync();
    }

    private async Task<long> ActiveTaskIdAsync(long instanceId, int nodeId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.UserTasks.Where(item => item.InstanceId == instanceId && item.NodeId == nodeId
            && item.Status == UserTaskStatuses.Active).Select(item => item.Id).FirstAsync();
    }

    private async Task<InstanceDetailDto> StartAsync(WorkflowModel model)
    {
        using var create = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
        Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        var definition = await create.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions);
        return await StartExistingAsync(definition!.Id);
    }

    private async Task<InstanceDetailDto> StartExistingAsync(long workflowId)
    {
        using var start = await SendAsync(HttpMethod.Post, "/api/instances?detail=full", new StartInstanceRequest(workflowId, null, null, null));
        Assert.True(start.StatusCode == HttpStatusCode.Created, await start.Content.ReadAsStringAsync());
        return (await start.Content.ReadFromJsonAsync<InstanceDetailDto>(JsonOptions))!;
    }

    private async Task<InstanceDetailDto> DetailAsync(long instanceId)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/instances/{instanceId}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<InstanceDetailDto>(JsonOptions))!;
    }

    private async Task TakeAsync(long instanceId, int nodeId, int flowId)
    {
        var taskId = await ActiveTaskIdAsync(instanceId, nodeId);
        using var action = await SendAsync(HttpMethod.Post, $"/api/user-tasks/{taskId}/flows/{flowId}", new TakeFlowRequest(null));
        Assert.True(action.StatusCode == HttpStatusCode.OK, await action.Content.ReadAsStringAsync());
    }

    private async Task<PagedResult<NodeExecutionSummaryDto>> SearchAsync(string path)
    {
        using var response = await SendAsync(HttpMethod.Get, path);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PagedResult<NodeExecutionSummaryDto>>(JsonOptions))!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, "link-runtime", ["admin", LinkReaderRole]);
        return await fixture.Client.SendAsync(request);
    }

    private static WorkflowModel Model(List<FlowNodeModel> nodes, List<SequenceFlowModel> flows) => new()
    {
        Id = $"link-runtime-{Guid.NewGuid():N}",
        Name = "Link runtime",
        InitialEventId = 1,
        FlowNodes = nodes,
        SequenceFlows = flows
    };

    private static FlowNodeModel Node(int id, string type) => new() { Id = id, Name = $"Node {id}", Type = type };

    private static FlowNodeModel Link(int id, bool isThrow, string name) => new()
    {
        Id = id,
        Name = name,
        Type = isThrow ? BpmnFlowNodeTypes.IntermediateLinkThrowEvent : BpmnFlowNodeTypes.IntermediateLinkCatchEvent
    };

    private static SequenceFlowModel Flow(int id, int source, int target) => new() { Id = id, SourceRef = source, TargetRef = target };
}
