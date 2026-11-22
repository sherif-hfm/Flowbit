using System.Text.Json;
using System.Text.RegularExpressions;
using Flowbit.Service.Ai;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// Section 6 of the EVENT-04B plan: both new types are ordinary nodes with flows for the
/// graph analyses, and neither touches a shared variable.
/// </summary>
public sealed class SignalThrowAnalysisTests
{
    [Theory]
    [InlineData(BpmnFlowNodeTypes.IntermediateSignalThrowEvent)]
    [InlineData(BpmnFlowNodeTypes.SignalEndEvent)]
    public void AccessPlan_IsEmptyForBothTypes(string type)
    {
        var model = SharedModel(type);

        var plan = SharedVariableAccessPlanner.Build(model, ConditionalEventDependencyPlan.Empty);

        Assert.True(plan.ForNode(2).TouchesSharedVariables);
        Assert.False(plan.ForNode(3).TouchesSharedVariables);
        Assert.Empty(plan.ForNode(3).LockAliases);
    }

    /// <summary>
    /// The lock-order proof follows a throw's one flow inside the same transaction, so a
    /// descending pair of shared keys on either side of it is still found; a signal end event
    /// ends the path like any end event.
    /// </summary>
    [Fact]
    public void LockOrderProof_FollowsTheThrowAndEndsAtTheSignalEndEvent()
    {
        var through = SharedModel(BpmnFlowNodeTypes.IntermediateSignalThrowEvent);
        through.Variables.Add(new VariableModel
        {
            Id = 2, Name = "first", Scope = VariableScopes.Shared, SharedKey = "a.first",
            Access = SharedVariableAccessModes.ReadWrite, DataType = WorkflowVariableTypes.Number, Nullable = true
        });
        through.FlowNodes.Add(new FlowNodeModel
        {
            Id = 5, Name = "Write first", Type = BpmnFlowNodeTypes.ScriptTask,
            Assignments = [new AssignmentModel { Variable = "first", Expression = "1" }]
        });
        through.SequenceFlows.Single(flow => flow.SourceRef == 3).TargetRef = 5;
        through.SequenceFlows.Add(new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 4 });

        var error = Assert.Throws<WorkflowDomainException>(() => ProveLockOrder(through));
        Assert.Contains("a.first", error.Message, StringComparison.Ordinal);
        Assert.Contains("z.last", error.Message, StringComparison.Ordinal);

        ProveLockOrder(SharedModel(BpmnFlowNodeTypes.SignalEndEvent));
    }

    [Theory]
    [InlineData(BpmnFlowNodeTypes.IntermediateSignalThrowEvent)]
    [InlineData(BpmnFlowNodeTypes.SignalEndEvent)]
    public void ProposalReachability_TreatsBothTypesAsOrdinaryNodes(string type)
    {
        var model = SharedModel(type);

        Assert.Empty(WorkflowAiAuthoringService.UnreachableNodes(model, null));
    }

    [Fact]
    public void EngineSettingsReference_StatesTheNumberOfSettingsItLists()
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot(), "docs", "engine-settings.md"));
        var stated = int.Parse(Regex.Match(text, @"\*\*(\d+) current built-in settings\*\*").Groups[1].Value);
        var overview = text[text.IndexOf("## Overview", StringComparison.Ordinal)..text.IndexOf("## Storage", StringComparison.Ordinal)];
        var rows = Regex.Matches(overview, @"^\| \[", RegexOptions.Multiline).Count;
        Assert.Equal(rows, stated);
        Assert.Contains("[Signals.MaxOpenWorkflowBroadcasts](#signalsmaxopenworkflowbroadcasts)", overview, StringComparison.Ordinal);
    }

    /// <summary>
    /// Start #1, a script #2 that writes the shared key z.last, the node #3 of
    /// <paramref name="type"/>, and, for a throw, end #4.
    /// </summary>
    private static WorkflowModel SharedModel(string type)
    {
        var model = new WorkflowModel
        {
            Id = "signal-analysis",
            Name = "Signal analysis",
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1, Name = "last", Scope = VariableScopes.Shared, SharedKey = "z.last",
                    Access = SharedVariableAccessModes.ReadWrite, DataType = WorkflowVariableTypes.Number, Nullable = true
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2, Name = "Write last", Type = BpmnFlowNodeTypes.ScriptTask,
                    Assignments = [new AssignmentModel { Variable = "last", Expression = "1" }]
                },
                new FlowNodeModel
                {
                    Id = 3, Name = "Announce", Type = type,
                    Signal = new SignalEventModel
                    {
                        Name = "orders.done",
                        Scope = new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows },
                        Payload = []
                    }
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 }
            ]
        };
        if (type == BpmnFlowNodeTypes.IntermediateSignalThrowEvent)
        {
            model.FlowNodes.Add(new FlowNodeModel { Id = 4, Name = "End", Type = BpmnFlowNodeTypes.EndEvent });
            model.SequenceFlows.Add(new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 });
        }

        return model;
    }

    private static void ProveLockOrder(WorkflowModel definition) =>
        SharedVariableTransactionLockOrderValidator.Validate(
            definition,
            SharedVariableAccessPlanner.Build(definition, ConditionalEventDependencyPlan.Empty),
            ConditionalEventDependencyPlan.Empty);

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md")))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
