using System.Text.Json;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// Link-event pairing and definition validation: the shared rules report every fixture's problems in order,
/// the API rejects the first, and authored input cannot carry settings a link event has no use for.
/// </summary>
public sealed class LinkEventRulesTests
{
    public static TheoryData<string> CaseNames() => new(LinkEventFixtures.Cases().Select(item => item.Case));

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Rules_ReportEveryFixtureProblemInOrder(string name)
    {
        var (_, model, problems) = LinkEventFixtures.Cases().Single(item => item.Case == name);

        Assert.Equal(problems, LinkEventRules.FindProblems(model));
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Api_RejectsTheFirstProblemOfEachFixture(string name)
    {
        var (_, model, problems) = LinkEventFixtures.Cases().Single(item => item.Case == name);

        if (problems.Length == 0)
        {
            Validate(model);
            return;
        }

        var error = Assert.Throws<WorkflowDomainException>(() => Validate(model));
        Assert.Equal(problems[0], error.Message);
    }

    [Fact]
    public void Index_ResolvesEachThrowToTheFirstCatchWithItsExactName()
    {
        var model = LinkEventFixtures.Base("  Fulfilment ", "Fulfilment");
        model.FlowNodes.Add(new FlowNodeModel { Id = 6, Name = "Fulfilment", Type = BpmnFlowNodeTypes.IntermediateLinkThrowEvent });
        model.FlowNodes.Add(new FlowNodeModel { Id = 7, Name = "Fulfilment", Type = BpmnFlowNodeTypes.IntermediateLinkCatchEvent });
        model.FlowNodes.Add(new FlowNodeModel { Id = 8, Name = "fulfilment", Type = BpmnFlowNodeTypes.IntermediateLinkThrowEvent });
        model.FlowNodes.Add(new FlowNodeModel { Id = 9, Name = " ", Type = BpmnFlowNodeTypes.IntermediateLinkThrowEvent });

        var index = LinkEventRules.Index(model);

        Assert.Equal([new LinkEventEdge(3, 4), new LinkEventEdge(6, 4)], index.Edges);
        Assert.Equal(4, index.CatchOf(3));
        Assert.Null(index.CatchOf(8));
        Assert.Null(index.CatchOf(9));
        Assert.Null(index.CatchOf(4));
        Assert.Equal([3, 6], index.ThrowsOf(4));
        Assert.Empty(index.ThrowsOf(7));
        Assert.Equal(index.Edges, LinkEventRules.ImplicitEdges(model));
        Assert.Equal("Fulfilment", LinkEventRules.NameKey("\u2028 Fulfilment\u0085"));
        Assert.Equal("\uFEFFFulfilment", LinkEventRules.NameKey("\uFEFFFulfilment"));
        Assert.Null(LinkEventRules.NameKey(" \u3000"));
        Assert.Null(LinkEventRules.NameKey(null));
    }

    [Fact]
    public void BpmnTypes_TreatBothLinkEventsAsSupportedPassThroughEvents()
    {
        foreach (var type in new[] { BpmnFlowNodeTypes.IntermediateLinkThrowEvent, BpmnFlowNodeTypes.IntermediateLinkCatchEvent })
        {
            Assert.True(BpmnFlowNodeTypes.IsSupported(type));
            Assert.True(BpmnFlowNodeTypes.IsPassThrough(type));
            Assert.True(BpmnFlowNodeTypes.IsLinkEvent(type));
            Assert.False(BpmnFlowNodeTypes.IsEntry(type));
            Assert.False(BpmnFlowNodeTypes.IsAsyncCapableTask(type));
            // The node-type columns hold 64 characters and the history Kind 32.
            Assert.True(type.Length <= 32);
        }

        Assert.True(BpmnFlowNodeTypes.IsLinkThrow(BpmnFlowNodeTypes.IntermediateLinkThrowEvent));
        Assert.False(BpmnFlowNodeTypes.IsLinkThrow(BpmnFlowNodeTypes.IntermediateLinkCatchEvent));
        Assert.True(BpmnFlowNodeTypes.IsLinkCatch(BpmnFlowNodeTypes.IntermediateLinkCatchEvent));
        Assert.False(BpmnFlowNodeTypes.IsLinkEvent(BpmnFlowNodeTypes.IntermediateMessageCatchEvent));
    }

    public static TheoryData<string> UnsupportedSettings() => new(NodeSettings().Select(item => item.Setting));

    [Theory]
    [MemberData(nameof(UnsupportedSettings))]
    public void Api_RejectsSettingsALinkEventCannotUse(string setting)
    {
        var apply = NodeSettings().Single(item => item.Setting == setting).Apply;
        foreach (var (nodeId, label) in new[] { (3, "Link throw event #3"), (4, "Link catch event #4") })
        {
            var model = LinkEventFixtures.Base();
            apply(model.FlowNodes.Single(node => node.Id == nodeId));

            var error = Assert.Throws<WorkflowDomainException>(() => Validate(model));

            Assert.Equal($"{label} cannot define task, role, variable, event, or multi-instance metadata.", error.Message);
        }
    }

    [Fact]
    public void Api_RejectsMultiInstanceOnALinkEventBeforeNormalizationCanDropIt()
    {
        var model = LinkEventFixtures.Base();
        model.FlowNodes.Single(node => node.Id == 4).MultiInstance = new MultiInstanceModel
        {
            Mode = MultiInstanceModes.Parallel,
            Source = MultiInstanceSources.Cardinality,
            CardinalityExpression = "2"
        };

        var error = Assert.Throws<WorkflowDomainException>(() => Validate(model));

        Assert.Equal("Flow node #4 cannot define multiInstance.", error.Message);
    }

    [Fact]
    public void Migrator_KeepsLinkNamesAndClearsSettingsLinkEventsCannotUse()
    {
        var model = LinkEventFixtures.Base("  Fulfilment ", "Fulfilment\u2028");
        var throwNode = model.FlowNodes.Single(node => node.Id == 3);
        throwNode.Roles = ["Clerk"];
        throwNode.Variables = [new VariableModel { Id = 1, Name = "note", DataType = WorkflowVariableTypes.String }];
        throwNode.AsyncBefore = true;
        throwNode.Timer = new TimerDefinitionModel { TimeDuration = "PT1H" };
        throwNode.Script = "x";
        throwNode.ErrorVariable = "failure";
        throwNode.AttachedToRef = 2;

        WorkflowModelMigrator.Normalize(model);

        Assert.Equal("  Fulfilment ", throwNode.Name);
        Assert.Equal("Fulfilment\u2028", model.FlowNodes.Single(node => node.Id == 4).Name);
        Assert.Empty(throwNode.Roles);
        Assert.Empty(throwNode.Variables);
        Assert.False(throwNode.AsyncBefore);
        Assert.Null(throwNode.Timer);
        Assert.Null(throwNode.Script);
        Assert.Null(throwNode.ErrorVariable);
        Assert.Null(throwNode.AttachedToRef);
        Assert.Equal(new LinkEventEdge(3, 4), Assert.Single(LinkEventRules.ImplicitEdges(model)));
    }

    [Fact]
    public void Migrator_RoundTripsALinkDefinitionUnchanged()
    {
        var model = LinkEventFixtures.Base();
        WorkflowModelMigrator.Normalize(model);
        var first = JsonSerializer.Serialize(model);

        var reloaded = JsonSerializer.Deserialize<WorkflowModel>(first)!;
        WorkflowModelMigrator.Normalize(reloaded);

        Assert.Equal(first, JsonSerializer.Serialize(reloaded));
    }

    [Fact]
    public void Api_RejectsAnErrorBoundaryAttachedToALinkEvent()
    {
        var model = LinkEventFixtures.Base();
        model.FlowNodes.Add(new FlowNodeModel { Id = 6, Name = "Failed", Type = BpmnFlowNodeTypes.ErrorBoundaryEvent, AttachedToRef = 4 });
        model.FlowNodes.Add(new FlowNodeModel { Id = 7, Name = "Handled", Type = BpmnFlowNodeTypes.EndEvent });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 601, SourceRef = 6, TargetRef = 7 });

        var error = Assert.Throws<WorkflowDomainException>(() => Validate(model));

        Assert.Contains("#6", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Api_AcceptsALoopThroughALinkWithAUserTask()
    {
        var model = LinkEventFixtures.Base();
        // Review -> throw; catch -> rework -> review, so the catch comes before its throw.
        model.FlowNodes.Add(new FlowNodeModel { Id = 6, Name = "Rework", Type = BpmnFlowNodeTypes.UserTask });
        model.SequenceFlows.Single(flow => flow.Id == 401).TargetRef = 6;
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 601, Name = "Resubmit", SourceRef = 6, TargetRef = 2 });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 202, Name = "Approve", SourceRef = 2, TargetRef = 5 });

        Validate(model);
    }

    internal static void Validate(WorkflowModel model) => TimerExpressionDefinitionTests.Validate(model);

    private static IEnumerable<(string Setting, Action<FlowNodeModel> Apply)> NodeSettings()
    {
        yield return ("roles", node => node.Roles = ["Clerk"]);
        yield return ("requiresClaim", node => node.RequiresClaim = true);
        yield return ("variables", node => node.Variables = [new VariableModel { Id = 1, Name = "note", DataType = WorkflowVariableTypes.String }]);
        yield return ("a script", node => node.Script = "x");
        yield return ("assignments", node => node.Assignments = [new AssignmentModel { Variable = "x", Expression = "1" }]);
        yield return ("a service", node => node.Service = new ServiceTaskModel());
        yield return ("a message", node => node.Message = new MessageCatchModel());
        yield return ("an assignee", node => node.AssigneeExpression = "'alice'");
        yield return ("an attachment", node => node.AttachedToRef = 2);
        yield return ("an error variable", node => node.ErrorVariable = "failure");
        yield return ("asyncBefore", node => node.AsyncBefore = true);
        yield return ("asyncAfter", node => node.AsyncAfter = true);
        yield return ("a timer", node => node.Timer = new TimerDefinitionModel { TimeDuration = "PT1H" });
        yield return ("a conditional", node => node.Conditional = new ConditionalDefinitionModel { Condition = "true" });
        yield return ("cancelActivity", node => node.CancelActivity = true);
        yield return ("a due date", node => node.DueDateExpression = "P1D");
        yield return ("a priority", node => node.PriorityExpression = "1");
    }
}
