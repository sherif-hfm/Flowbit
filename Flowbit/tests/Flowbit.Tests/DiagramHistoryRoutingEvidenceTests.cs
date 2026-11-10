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

/// <summary>Characterizes the committed evidence on which diagram-history counting relies.</summary>
[Collection(PostgresApiCollection.Name)]
public sealed class DiagramHistoryRoutingEvidenceTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoopCountsEachExitOnceRegardlessOfFlowInfo(bool flowInfo)
    {
        var model = Model(
            [Node(1, BpmnFlowNodeTypes.StartEvent), Node(2, BpmnFlowNodeTypes.UserTask),
             Node(3, BpmnFlowNodeTypes.ExclusiveGateway), Node(4, BpmnFlowNodeTypes.EndEvent)],
            [Flow(101, 1, 2), Flow(201, 2, 3), Flow(202, 2, 4), Flow(301, 3, 2), Flow(302, 3, 4)]);
        model.SequenceFlows.Single(flow => flow.Id == 302).IsDefault = true;
        model.SequenceFlows.Single(flow => flow.Id == 301).ConditionPriority = 1;
        model.SequenceFlows.Single(flow => flow.Id == 301).Condition = flowInfo
            ? "FlowInfo(101, 'traversals.count') >= 1" : "true";
        var instance = await StartAsync(model);
        for (var index = 0; index < 3; index++)
        {
            await TakeAsync(instance.Id, 2, 201);
        }
        await TakeAsync(instance.Id, 2, 202);
        await AssertExitsAsync(instance.Id, model, (101, 1), (201, 3), (301, 3), (202, 1));
        await using var db = fixture.CreateDbContext();
        Assert.Equal(flowInfo, await db.SequenceFlowOccurrences.AnyAsync(item => item.InstanceId == instance.Id));
    }

    [Theory]
    [InlineData(BpmnFlowNodeTypes.ParallelGateway)]
    [InlineData(BpmnFlowNodeTypes.InclusiveGateway)]
    public async Task ForkCopiesAndJoinLosersDoNotDoubleCountTraversals(string gatewayType)
    {
        var model = Model(
            [Node(1, BpmnFlowNodeTypes.StartEvent), Node(2, gatewayType),
             Node(3, BpmnFlowNodeTypes.UserTask), Node(4, BpmnFlowNodeTypes.Task),
             Node(5, gatewayType), Node(6, BpmnFlowNodeTypes.EndEvent)],
            [Flow(101, 1, 2), Flow(201, 2, 3), Flow(202, 2, 4), Flow(301, 3, 5),
             Flow(401, 4, 5), Flow(501, 5, 6)]);
        if (gatewayType == BpmnFlowNodeTypes.InclusiveGateway)
        {
            model.SequenceFlows.Single(flow => flow.Id == 201).Condition = "true";
            model.SequenceFlows.Single(flow => flow.Id == 202).Condition = "true";
            model.FlowNodes.Add(Node(7, BpmnFlowNodeTypes.EndEvent));
            model.SequenceFlows.Add(new SequenceFlowModel { Id = 203, SourceRef = 2, TargetRef = 7, IsDefault = true });
        }
        var instance = await StartAsync(model);
        await TakeAsync(instance.Id, 3, 301);
        await AssertExitsAsync(instance.Id, model, (101, 1), (201, 1), (202, 1), (301, 1), (401, 1), (501, 1));
        await using var db = fixture.CreateDbContext();
        var visits = await db.NodeExecutions.Where(item => item.InstanceId == instance.Id).ToListAsync();
        Assert.Equal(2, visits.Count(item => item.NodeId == 2));
        Assert.Single(visits, item => item.NodeId == 2 && item.EnteredViaFlowId == null);
        Assert.Single(visits, item => item.NodeId == 5 && item.Status == NodeExecutionStatuses.Merged);
    }

    [Fact]
    public async Task ComplexActivationAndResetRecordEveryOutputOnceAcrossRepeatedCycles()
    {
        var model = Model(
            [Node(1, BpmnFlowNodeTypes.StartEvent), Node(2, BpmnFlowNodeTypes.ExclusiveGateway),
             new FlowNodeModel { Id = 3, Name = "Complex", Type = BpmnFlowNodeTypes.ComplexGateway,
                 ActivationCondition = "IncomingCount(201) >= 1" },
             Node(4, BpmnFlowNodeTypes.UserTask), Node(5, BpmnFlowNodeTypes.UserTask),
             Node(6, BpmnFlowNodeTypes.EndEvent)],
            [Flow(101, 1, 2), Flow(401, 4, 2), Flow(402, 4, 6), Flow(501, 5, 2),
             Flow(201, 2, 3), Flow(301, 3, 4), Flow(302, 3, 5)]);
        model.SequenceFlows.Single(flow => flow.Id == 301).Condition = "[gateway.waitingForStart]";
        model.SequenceFlows.Single(flow => flow.Id == 302).Condition = "not [gateway.waitingForStart]";
        var instance = await StartAsync(model);
        await TakeAsync(instance.Id, 4, 401);
        await TakeAsync(instance.Id, 5, 501);
        await AssertExitsAsync(instance.Id, model, (101, 1), (201, 3), (301, 2), (401, 1), (302, 1), (501, 1));
    }

    [Fact]
    public async Task ScopedInterruptAndStaleInterruptCountOnlyTheirActualContinuations()
    {
        var model = ParallelGatewayApiTests.CreateParallelWorkflow(includeStaleInterrupt: true);
        var instance = await StartAsync(model);
        await TakeAsync(instance.Id, 3, 302);
        await TakeAsync(instance.Id, 10, 1001);
        await AssertExitsAsync(instance.Id, model, (101, 1), (201, 1), (202, 1), (203, 1),
            (401, 1), (501, 1), (302, 1), (901, 1), (1001, 1), (1201, 1));
    }

    [Fact]
    public async Task CaughtScriptFailureHasNoInventedHostToBoundaryTraversal()
    {
        var model = Model(
            [Node(1, BpmnFlowNodeTypes.StartEvent),
             new FlowNodeModel { Id = 2, Name = "Failure", Type = BpmnFlowNodeTypes.ScriptTask,
                 ScriptFormat = "javascript", Script = "throw new Error('expected');" },
             Node(3, BpmnFlowNodeTypes.EndEvent),
             new FlowNodeModel { Id = 4, Name = "Catch", Type = BpmnFlowNodeTypes.ErrorBoundaryEvent, AttachedToRef = 2 },
             Node(5, BpmnFlowNodeTypes.EndEvent)],
            [Flow(101, 1, 2), Flow(201, 2, 3), Flow(401, 4, 5)]);
        var instance = await StartAsync(model);
        await AssertExitsAsync(instance.Id, model, (101, 1), (401, 1));
        await using var db = fixture.CreateDbContext();
        var boundary = await db.NodeExecutions.SingleAsync(item => item.InstanceId == instance.Id && item.NodeId == 4);
        Assert.Null(boundary.EnteredViaFlowId);
        var host = await db.NodeExecutions.SingleAsync(item => item.InstanceId == instance.Id && item.NodeId == 2);
        Assert.Null(host.ExitedViaFlowId);
    }

    [Fact]
    public async Task ReactivationReachesPriorTaskWithoutInventingAnIncomingTraversal()
    {
        var model = Model(
            [Node(1, BpmnFlowNodeTypes.StartEvent), Node(2, BpmnFlowNodeTypes.UserTask), Node(3, BpmnFlowNodeTypes.EndEvent)],
            [Flow(101, 1, 2), Flow(201, 2, 3)]);
        var instance = await StartAsync(model);
        await TakeAsync(instance.Id, 2, 201);
        using var previewRequest = new HttpRequestMessage(HttpMethod.Get, $"/api/instances/{instance.Id}/reactivation");
        ApiTestAuth.Authorize(previewRequest, "diagram-evidence", ["Manager"]);
        using var previewResponse = await fixture.Client.SendAsync(previewRequest);
        Assert.True(previewResponse.IsSuccessStatusCode, await previewResponse.Content.ReadAsStringAsync());
        var preview = (await previewResponse.Content.ReadFromJsonAsync<InstanceReactivationPreviewDto>(JsonOptions))!;
        using var response = await SendAsync($"/api/instances/{instance.Id}/reactivation",
            new ReactivateInstanceRequest(2, preview.WorkflowId, preview.ExpectedUpdatedAt, "Characterize recorded history"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var reopened = (await response.Content.ReadFromJsonAsync<InstanceDetailDto>(JsonOptions))!;
        Assert.True(reopened.UpdatedAt > preview.ExpectedUpdatedAt);
        await AssertExitsAsync(instance.Id, model, (101, 1), (201, 1));
        await using var db = fixture.CreateDbContext();
        var newVisit = await db.NodeExecutions.SingleAsync(item => item.InstanceId == instance.Id && item.Status == NodeExecutionStatuses.Active);
        Assert.Equal(2, newVisit.NodeId);
        Assert.Null(newVisit.EnteredViaFlowId);
        await TakeAsync(instance.Id, 2, 201);
        await AssertExitsAsync(instance.Id, model, (101, 1), (201, 2));
    }

    [Fact]
    public async Task CallResultDeliveryRecordsOneParentExit()
    {
        var harness = new CallActivityHarness(fixture);
        try
        {
            var childKey = CallActivityHarness.Key("history-child");
            await harness.PublishAsync(CallActivityHarness.ImmediateChild(childKey));
            var model = CallActivityHarness.Parent(CallActivityHarness.Key("history-parent"), childKey);
            var definition = await harness.PublishAsync(model);
            var instanceId = await harness.StartAsync(definition.Id);
            var invocation = await harness.InvocationAsync(instanceId);
            await harness.ProcessCallJobAsync(invocation.Id, WorkflowJobKinds.CallStart);
            await harness.ProcessCallJobAsync(invocation.Id, WorkflowJobKinds.CallResume);
            await AssertExitsAsync(instanceId, model, (101, 1), (201, 1));
            var children = await harness.CallsAsync(instanceId);
            Assert.Single(children.Items);
        }
        finally
        {
            await harness.RetireLeftoverJobsAsync();
        }
    }

    private async Task AssertExitsAsync(long instanceId, WorkflowModel definition, params (int FlowId, long Count)[] expected)
    {
        await using var db = fixture.CreateDbContext();
        var visits = await db.NodeExecutions.AsNoTracking().Where(item => item.InstanceId == instanceId).ToListAsync();
        var counts = visits.Where(item => item.ExecutionKind == NodeExecutionKinds.Node && item.ExitedViaFlowId != null)
            .GroupBy(item => item.ExitedViaFlowId!.Value).ToDictionary(group => group.Key, group => group.LongCount());
        Assert.Equal(expected.OrderBy(item => item.FlowId), counts.OrderBy(item => item.Key).Select(item => (item.Key, item.Value)));
        foreach (var visit in visits.Where(item => item.ExitedViaFlowId != null))
        {
            Assert.Equal(visit.NodeId, Assert.Single(definition.SequenceFlows, flow => flow.Id == visit.ExitedViaFlowId).SourceRef);
        }
        await DiagramHistoryEvidenceAssertions.SummaryAsync(fixture, instanceId,
            visits.Select(item => item.NodeId).Distinct().ToArray(), expected);
    }

    private async Task<InstanceDetailDto> StartAsync(WorkflowModel model)
    {
        using var create = await SendAsync("/api/workflows", new CreateWorkflowRequest(model, true));
        Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        var definition = await create.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions);
        using var start = await SendAsync("/api/instances?detail=full", new StartInstanceRequest(definition!.Id, null, null, null));
        Assert.True(start.StatusCode == HttpStatusCode.Created, await start.Content.ReadAsStringAsync());
        return (await start.Content.ReadFromJsonAsync<InstanceDetailDto>(JsonOptions))!;
    }

    private async Task TakeAsync(long instanceId, int nodeId, int flowId)
    {
        await using var db = fixture.CreateDbContext();
        var taskId = await db.UserTasks.Where(item => item.InstanceId == instanceId && item.NodeId == nodeId
            && item.Status == UserTaskStatuses.Active).Select(item => item.Id).SingleAsync();
        var previousRevision = await db.WorkflowInstances.Where(item => item.Id == instanceId).Select(item => item.UpdatedAt).SingleAsync();
        using var action = await SendAsync($"/api/user-tasks/{taskId}/flows/{flowId}", new TakeFlowRequest(null));
        Assert.True(action.StatusCode == HttpStatusCode.OK, await action.Content.ReadAsStringAsync());
        Assert.True(await db.WorkflowInstances.Where(item => item.Id == instanceId).Select(item => item.UpdatedAt).SingleAsync() > previousRevision);
    }

    private async Task<HttpResponseMessage> SendAsync(string path, object body)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body, options: JsonOptions) };
        ApiTestAuth.Authorize(request, "diagram-evidence", ["Manager"]);
        return await fixture.Client.SendAsync(request);
    }

    private static WorkflowModel Model(List<FlowNodeModel> nodes, List<SequenceFlowModel> flows) => new()
    {
        Id = $"diagram-routing-{Guid.NewGuid():N}", Name = "Diagram routing evidence", InitialEventId = 1,
        FlowNodes = nodes, SequenceFlows = flows
    };
    private static FlowNodeModel Node(int id, string type) => new() { Id = id, Name = $"Node {id}", Type = type };
    private static SequenceFlowModel Flow(int id, int source, int target) => new() { Id = id, SourceRef = source, TargetRef = target };
}
