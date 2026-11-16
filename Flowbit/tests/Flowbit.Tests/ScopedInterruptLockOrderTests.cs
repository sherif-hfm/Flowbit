using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// A token that the engine moves along a flow into a scoped interrupt passes through it at once: the engine queues
/// it at the interrupt's continuation in the same routing step. The shared-variable lock-order proof must queue it
/// there too, or it processes that continuation after branches the engine processes later and misjudges the order
/// in which shared rows are locked. Each shape runs on PostgreSQL to record the engine's queue order, and is given
/// to the proof with its two shared keys in both orders.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class ScopedInterruptLockOrderTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private const int Continuation = 7;
    private const int Later = 11;

    // The engine processes the interrupt's continuation (#7) before #11, so 'z' at #7 and then 'a' at #11 descends.
    [Theory]
    [InlineData("tests.lock.z", "tests.lock.a", true)]
    [InlineData("tests.lock.a", "tests.lock.z", false)]
    public void Proof_QueuesTheContinuationOfAnInterruptEnteredFromATask(string continuationKey, string laterKey, bool descending)
    {
        var problem = LockOrderProblem(InterruptAfterTask(
            SharedScript(Continuation, "continuation"),
            SharedScript(Later, "later"),
            [Shared(1, "continuation", continuationKey), Shared(2, "later", laterKey)]));

        AssertProblem(problem, descending);
    }

    // A write that releases an atomic conditional catch moves its token as a flow does. The released token's
    // service and script tasks are forced durable, but a user task still locks the shared rows its submitted
    // values write when the token enters it, as every node the engine processes does. The proof also lets the
    // start event's initialization of 'ready' release the catch before it holds a token, which already puts #7
    // first, so here only the safe order showed the old difference: it was refused.
    [Theory]
    [InlineData("tests.lock.z", "tests.lock.a", true)]
    [InlineData("tests.lock.a", "tests.lock.z", false)]
    public void Proof_QueuesTheContinuationOfAnInterruptEnteredByAConditionalRelease(string continuationKey, string laterKey, bool descending)
    {
        var continuation = Review(Continuation);
        continuation.Variables = [new VariableModel { Id = 1, Name = "continuation", DataType = WorkflowVariableTypes.Number }];
        var problem = LockOrderProblem(InterruptAfterConditionalRelease(
            continuation,
            SharedScript(Later, "later"),
            [Shared(1, "continuation", continuationKey), Shared(2, "later", laterKey), Ready()]));

        AssertProblem(problem, descending);
    }

    [Fact]
    public async Task Engine_QueuesTheContinuationOfAnInterruptEnteredFromATaskAtOnce()
    {
        var instance = await StartAsync(InterruptAfterTask(
            OrderScript(Continuation, "C"), OrderScript(Later, "L"), [Order()]));

        Assert.Equal(WorkflowInstanceStatuses.Completed, instance.Status);
        Assert.Equal("-CL", await OrderAsync(instance.Id));
        await AssertArrivesBeforeTheLaterBranchAsync(instance.Id);
    }

    [Fact]
    public async Task Engine_QueuesTheContinuationOfAnInterruptEnteredByAConditionalReleaseAtOnce()
    {
        var instance = await StartAsync(InterruptAfterConditionalRelease(
            Review(Continuation), OrderScript(Later, "L"), [Order(), Ready()]));

        Assert.Equal(WorkflowInstanceStatuses.Running, instance.Status);
        Assert.Equal("-L", await OrderAsync(instance.Id));
        await using (var db = fixture.CreateDbContext())
        {
            Assert.True(await db.UserTasks.AnyAsync(item => item.InstanceId == instance.Id && item.NodeId == Continuation
                && item.Status == UserTaskStatuses.Active));
        }
        await AssertArrivesBeforeTheLaterBranchAsync(instance.Id);
    }

    private static void AssertProblem(string? problem, bool descending)
    {
        Assert.Equal(descending, problem is not null);
        if (descending)
        {
            Assert.Contains("node #11", problem, StringComparison.Ordinal);
            Assert.Contains("catalog key 'tests.lock.a' after key 'tests.lock.z'", problem, StringComparison.Ordinal);
        }
    }

    // The interrupt records its step into #7 before the second automatic task (#10) moves its token to #11.
    private async Task AssertArrivesBeforeTheLaterBranchAsync(long instanceId)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/instances/{instanceId}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var detail = await response.Content.ReadFromJsonAsync<InstanceDetailDto>(JsonOptions);
        var steps = detail!.History.OrderBy(item => item.Id).Select(item => $"{item.FromNodeId}->{item.ToNodeId}").ToList();
        var interrupt = steps.IndexOf("5->7");
        var laterBranch = steps.IndexOf("10->11");
        Assert.True(interrupt >= 0 && laterBranch > interrupt, string.Join(", ", steps));
    }

    // Outer fork #2: flow 201 enters inner fork #3; flow 202 runs automatic tasks #9 and #10, then #11.
    // Inner fork #3: flow 301 runs automatic task #4 into scoped interrupt #5 (gatewayRef #3), which continues
    // at #7; flow 302 waits at user task #6, which the interrupt cancels.
    // The engine queues the token at #7 while it routes #4, before #10 moves its token to #11.
    private static WorkflowModel InterruptAfterTask(FlowNodeModel continuation, FlowNodeModel later, List<VariableModel> variables) =>
        Model(variables, Node(4, BpmnFlowNodeTypes.Task), Review(6), continuation, later);

    // As above, but #4 is an atomic conditional catch on 'ready', and #6 is a script that sets it. The write releases
    // the catch's token into the interrupt, and the engine queues it at #7 within the script's step.
    private static WorkflowModel InterruptAfterConditionalRelease(FlowNodeModel continuation, FlowNodeModel later, List<VariableModel> variables) =>
        Model(
            variables,
            new FlowNodeModel
            {
                Id = 4,
                Name = "Wait until ready",
                Type = BpmnFlowNodeTypes.IntermediateConditionalCatchEvent,
                Conditional = new ConditionalDefinitionModel { Condition = "ready == true" }
            },
            new FlowNodeModel
            {
                Id = 6,
                Name = "Set ready",
                Type = BpmnFlowNodeTypes.ScriptTask,
                ScriptFormat = ScriptFormats.NCalc,
                Assignments = [new AssignmentModel { Variable = "ready", Expression = "true" }]
            },
            continuation,
            later);

    private static WorkflowModel Model(
        List<VariableModel> variables, FlowNodeModel beforeInterrupt, FlowNodeModel sibling, FlowNodeModel continuation, FlowNodeModel later) => new()
    {
        Id = $"scoped-interrupt-lock-order-{Guid.NewGuid():N}",
        Name = "Scoped interrupt lock order",
        InitialEventId = 1,
        Variables = variables,
        FlowNodes =
        [
            Node(1, BpmnFlowNodeTypes.StartEvent),
            Node(2, BpmnFlowNodeTypes.ParallelGateway),
            Node(3, BpmnFlowNodeTypes.ParallelGateway),
            beforeInterrupt,
            new FlowNodeModel { Id = 5, Name = "Leave the inner scope", Type = BpmnFlowNodeTypes.ScopedInterruptEvent, GatewayRef = 3 },
            sibling,
            continuation,
            Node(8, BpmnFlowNodeTypes.EndEvent),
            Node(9, BpmnFlowNodeTypes.Task),
            Node(10, BpmnFlowNodeTypes.Task),
            later,
            Node(12, BpmnFlowNodeTypes.EndEvent),
            Node(13, BpmnFlowNodeTypes.EndEvent)
        ],
        SequenceFlows =
        [
            Flow(101, 1, 2), Flow(201, 2, 3), Flow(202, 2, 9), Flow(301, 3, 4), Flow(302, 3, 6), Flow(401, 4, 5),
            Flow(501, 5, 7), Flow(601, 6, 13), Flow(701, 7, 8), Flow(901, 9, 10), Flow(1001, 10, 11), Flow(1101, 11, 12)
        ]
    };

    private static FlowNodeModel Node(int id, string type) => new() { Id = id, Name = $"Node {id}", Type = type };

    private static FlowNodeModel Review(int id) => new() { Id = id, Name = $"Review {id}", Type = BpmnFlowNodeTypes.UserTask };

    private static FlowNodeModel SharedScript(int id, string alias) => new()
    {
        Id = id,
        Name = $"Write {alias}",
        Type = BpmnFlowNodeTypes.ScriptTask,
        ScriptFormat = ScriptFormats.NCalc,
        Assignments = [new AssignmentModel { Variable = alias, Expression = "1" }]
    };

    private static FlowNodeModel OrderScript(int id, string mark) => new()
    {
        Id = id,
        Name = $"Record {mark}",
        Type = BpmnFlowNodeTypes.ScriptTask,
        ScriptFormat = ScriptFormats.JavaScript,
        Script = $"execution.setVariable('order', execution.getVariable('order') + '{mark}');"
    };

    private static VariableModel Shared(int id, string alias, string key) => new()
    {
        Id = id,
        Name = alias,
        Scope = VariableScopes.Shared,
        SharedKey = key,
        Access = SharedVariableAccessModes.ReadWrite,
        DataType = WorkflowVariableTypes.Number,
        Nullable = true
    };

    private static VariableModel Order() => new()
    {
        Id = 3,
        Name = "order",
        DataType = WorkflowVariableTypes.String,
        DefaultValue = JsonSerializer.SerializeToElement("-")
    };

    private static VariableModel Ready() => new()
    {
        Id = 4,
        Name = "ready",
        DataType = WorkflowVariableTypes.Boolean,
        DefaultValue = JsonSerializer.SerializeToElement(false)
    };

    private static SequenceFlowModel Flow(int id, int source, int target) => new() { Id = id, SourceRef = source, TargetRef = target };

    private static string? LockOrderProblem(WorkflowModel model)
    {
        WorkflowModelMigrator.Normalize(model);
        var conditionalPlan = new ConditionalEventDefinitionAnalyzer().Analyze(model);
        try
        {
            SharedVariableTransactionLockOrderValidator.Validate(
                model,
                SharedVariableAccessPlanner.Build(model, conditionalPlan),
                conditionalPlan);
            return null;
        }
        catch (WorkflowDomainException exception)
        {
            return exception.Message;
        }
    }

    private async Task<string?> OrderAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        var value = await db.InstanceVariableCurrentValues
            .Where(item => item.InstanceId == instanceId && item.VariableName == "order")
            .Select(item => item.ValueJson)
            .SingleAsync();
        return value.RootElement.GetString();
    }

    private async Task<InstanceDetailDto> StartAsync(WorkflowModel model)
    {
        using var create = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
        Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        var definition = await create.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions);
        using var start = await SendAsync(HttpMethod.Post, "/api/instances?detail=full",
            new StartInstanceRequest(definition!.Id, null, null, null));
        Assert.True(start.StatusCode == HttpStatusCode.Created, await start.Content.ReadAsStringAsync());
        return (await start.Content.ReadFromJsonAsync<InstanceDetailDto>(JsonOptions))!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, "scoped-interrupt-lock-order", ["admin"]);
        return await fixture.Client.SendAsync(request);
    }
}
