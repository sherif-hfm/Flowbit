using Flowbit.Service.Ai;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// Every analysis that reads the workflow graph must answer for a link exactly as it answers for the same
/// definition with the link replaced by two automatic tasks joined by a sequence flow
/// (<see cref="LinkEventFixtures.WithoutLinks"/>).
/// </summary>
public sealed class LinkEventEquivalenceTests
{
    [Fact]
    public void JoinCancellation_AcceptsAMergeInputReachedThroughALink()
    {
        var model = Model(
            [Node(1, BpmnFlowNodeTypes.StartEvent), Node(2, BpmnFlowNodeTypes.ParallelGateway),
             Node(3, BpmnFlowNodeTypes.UserTask), Link(4, true, "Join"), Link(5, false, "Join"),
             new FlowNodeModel { Id = 6, Name = "Merge", Type = BpmnFlowNodeTypes.ParallelGateway,
                 JoinCancellation = new JoinCancellationModel { GatewayRef = 2 } },
             Node(7, BpmnFlowNodeTypes.EndEvent)],
            [Flow(101, 1, 2), Flow(201, 2, 3), Flow(202, 2, 4), Flow(301, 3, 6), Flow(501, 5, 6), Flow(601, 6, 7)]);

        AssertSameValidation(model);
        LinkEventRulesTests.Validate(model);
    }

    [Fact]
    public void ScopedInterrupt_AcceptsAnInterruptReachedThroughALink()
    {
        var model = Model(
            [Node(1, BpmnFlowNodeTypes.StartEvent), Node(2, BpmnFlowNodeTypes.ParallelGateway),
             Node(3, BpmnFlowNodeTypes.UserTask), Node(4, BpmnFlowNodeTypes.UserTask),
             Link(5, true, "Stop"), Link(6, false, "Stop"),
             new FlowNodeModel { Id = 7, Name = "Interrupt", Type = BpmnFlowNodeTypes.ScopedInterruptEvent, GatewayRef = 2 },
             Node(8, BpmnFlowNodeTypes.EndEvent), Node(9, BpmnFlowNodeTypes.EndEvent)],
            [Flow(101, 1, 2), Flow(201, 2, 3), Flow(202, 2, 4), Flow(301, 3, 8), Flow(401, 4, 5),
             Flow(601, 6, 7), Flow(701, 7, 9)]);

        AssertSameValidation(model);
        LinkEventRulesTests.Validate(model);
    }

    [Fact]
    public void ScopedInterrupt_RejectsAnInterruptOutsideItsScopeEvenThroughALink()
    {
        // The throw sits before the split, so the interrupt is never inside the split's scope.
        var model = Model(
            [Node(1, BpmnFlowNodeTypes.StartEvent), Node(2, BpmnFlowNodeTypes.UserTask),
             Link(3, true, "Stop"), Link(4, false, "Stop"),
             new FlowNodeModel { Id = 5, Name = "Interrupt", Type = BpmnFlowNodeTypes.ScopedInterruptEvent, GatewayRef = 6 },
             Node(6, BpmnFlowNodeTypes.ParallelGateway), Node(7, BpmnFlowNodeTypes.UserTask),
             Node(8, BpmnFlowNodeTypes.UserTask), Node(9, BpmnFlowNodeTypes.EndEvent), Node(10, BpmnFlowNodeTypes.EndEvent)],
            [Flow(101, 1, 2), Flow(201, 2, 3), Flow(202, 2, 6), Flow(401, 4, 5), Flow(501, 5, 9),
             Flow(601, 6, 7), Flow(602, 6, 8), Flow(701, 7, 10), Flow(801, 8, 10)]);

        var error = AssertSameValidation(model);

        Assert.Equal("Scoped interrupt event #5 must be structurally reachable from referenced gateway split #6.", error);
    }

    [Theory]
    [InlineData("tests.lock.z", "tests.lock.a", true)]
    [InlineData("tests.lock.a", "tests.lock.z", false)]
    public void LockOrder_FollowsALinkAsTwoQueuedRoutingSteps(string firstKey, string secondKey, bool descending)
    {
        // Fork: the first branch writes one key; the second crosses a link, then writes the other.
        var model = Model(
            [Node(1, BpmnFlowNodeTypes.StartEvent), Node(2, BpmnFlowNodeTypes.ParallelGateway),
             Script(3, "first"), Link(4, true, "Later"), Link(5, false, "Later"), Script(6, "second"),
             Node(7, BpmnFlowNodeTypes.EndEvent), Node(8, BpmnFlowNodeTypes.EndEvent)],
            [Flow(101, 1, 2), Flow(201, 2, 3), Flow(202, 2, 4), Flow(301, 3, 7), Flow(501, 5, 6), Flow(601, 6, 8)]);
        model.Variables =
        [
            Shared(1, "first", firstKey),
            Shared(2, "second", secondKey)
        ];

        var linked = LockOrderProblem(model);
        var flowEquivalent = LockOrderProblem(LinkEventFixtures.WithoutLinks(model));

        Assert.Equal(flowEquivalent, linked);
        Assert.Equal(descending, linked is not null);
        if (descending)
        {
            Assert.Contains("tests.lock.a", linked, StringComparison.Ordinal);
            Assert.Contains("node #6", linked, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Reactivation_RefusesATargetThatReachesAHazardThroughALink()
    {
        var model = Model(
            [Node(1, BpmnFlowNodeTypes.StartEvent), Node(5, BpmnFlowNodeTypes.ParallelGateway),
             Node(2, BpmnFlowNodeTypes.UserTask), Node(6, BpmnFlowNodeTypes.UserTask),
             Link(3, true, "Stop"), Link(4, false, "Stop"),
             new FlowNodeModel { Id = 7, Name = "Interrupt", Type = BpmnFlowNodeTypes.ScopedInterruptEvent, GatewayRef = 5 },
             Node(8, BpmnFlowNodeTypes.EndEvent), Node(9, BpmnFlowNodeTypes.EndEvent)],
            [Flow(101, 1, 5), Flow(501, 5, 2), Flow(502, 5, 6), Flow(201, 2, 3), Flow(601, 6, 9),
             Flow(401, 4, 7), Flow(701, 7, 8)]);
        LinkEventRulesTests.Validate(model);

        var linked = WorkflowEngineService.FindUnsafeReactivationTopology(model, 2);
        var flowEquivalent = WorkflowEngineService.FindUnsafeReactivationTopology(LinkEventFixtures.WithoutLinks(model), 2);

        Assert.Equal(
            "Reactivation at this task can reach scoped interrupt #7 without first creating its required gateway split scope.",
            linked);
        Assert.Equal(flowEquivalent, linked);
        Assert.Null(WorkflowEngineService.FindUnsafeReactivationTopology(model, 6));
    }

    [Fact]
    public void GatewayTopology_LetsATokenOnAThrowStillReachAMergeInputAfterItsCatch()
    {
        var model = Model(
            [Node(1, BpmnFlowNodeTypes.StartEvent), Node(2, BpmnFlowNodeTypes.InclusiveGateway),
             Node(3, BpmnFlowNodeTypes.UserTask), Link(4, true, "Late"), Link(5, false, "Late"),
             Node(6, BpmnFlowNodeTypes.InclusiveGateway), Node(7, BpmnFlowNodeTypes.EndEvent)],
            [Flow(101, 1, 2), Flow(201, 2, 3), Flow(202, 2, 4), Flow(301, 3, 6), Flow(501, 5, 6), Flow(601, 6, 7)]);

        var linked = new GatewayTopologyIndex(model);
        var flowEquivalent = new GatewayTopologyIndex(LinkEventFixtures.WithoutLinks(model));

        foreach (var from in new[] { 2, 3, 4, 5 })
        {
            Assert.Equal(flowEquivalent.CanReachAnyInput(6, from, [501]), linked.CanReachAnyInput(6, from, [501]));
            Assert.Equal(flowEquivalent.CanReachAnyInput(6, from, [301]), linked.CanReachAnyInput(6, from, [301]));
        }
        Assert.True(linked.CanReachAnyInput(6, 4, [501]));
        Assert.False(linked.CanReachAnyInput(6, 4, [301]));
        Assert.Equal(5, linked.LinkCatchOf(4));
        Assert.Null(linked.LinkCatchOf(5));
    }

    [Fact]
    public void AiProposal_ReachesACatchOnlyThroughItsThrow()
    {
        var model = LinkEventFixtures.Base();

        Assert.Empty(WorkflowAiAuthoringService.UnreachableNodes(model, null));
        Assert.Empty(WorkflowAiAuthoringService.UnreachableNodes(LinkEventFixtures.WithoutLinks(model), null));

        model.FlowNodes.Single(node => node.Id == 3).Name = "Elsewhere";
        Assert.Equal([4, 5], WorkflowAiAuthoringService.UnreachableNodes(model, null).Select(node => node.Id));
    }

    private static string? AssertSameValidation(WorkflowModel model)
    {
        var linked = ValidationProblem(model);
        Assert.Equal(ValidationProblem(LinkEventFixtures.WithoutLinks(model)), linked);
        return linked;
    }

    private static string? ValidationProblem(WorkflowModel model)
    {
        try
        {
            LinkEventRulesTests.Validate(Copy(model));
            return null;
        }
        catch (WorkflowDomainException exception)
        {
            return exception.Message;
        }
    }

    private static string? LockOrderProblem(WorkflowModel model)
    {
        var copy = Copy(model);
        WorkflowModelMigrator.Normalize(copy);
        var analyzer = new ConditionalEventDefinitionAnalyzer();
        var conditionalPlan = analyzer.Analyze(copy);
        try
        {
            SharedVariableTransactionLockOrderValidator.Validate(
                copy,
                SharedVariableAccessPlanner.Build(copy, conditionalPlan),
                conditionalPlan);
            return null;
        }
        catch (WorkflowDomainException exception)
        {
            return exception.Message;
        }
    }

    private static WorkflowModel Copy(WorkflowModel model) =>
        System.Text.Json.JsonSerializer.Deserialize<WorkflowModel>(System.Text.Json.JsonSerializer.Serialize(model))!;

    private static WorkflowModel Model(List<FlowNodeModel> nodes, List<SequenceFlowModel> flows) => new()
    {
        Id = $"link-equivalence-{Guid.NewGuid():N}",
        Name = "Link equivalence",
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

    private static FlowNodeModel Script(int id, string variable) => new()
    {
        Id = id,
        Name = $"Write {variable}",
        Type = BpmnFlowNodeTypes.ScriptTask,
        Assignments = [new AssignmentModel { Variable = variable, Expression = "1" }]
    };

    private static VariableModel Shared(int id, string name, string key) => new()
    {
        Id = id,
        Name = name,
        Scope = VariableScopes.Shared,
        SharedKey = key,
        Access = SharedVariableAccessModes.ReadWrite,
        DataType = WorkflowVariableTypes.Number,
        Nullable = true
    };

    private static SequenceFlowModel Flow(int id, int source, int target) => new() { Id = id, SourceRef = source, TargetRef = target };
}
