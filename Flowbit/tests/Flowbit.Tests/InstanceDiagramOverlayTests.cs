extern alias FlowbitUi;

using System.Text.Json;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Overlay = FlowbitUi::Flowbit.Ui.Components.Shared.InstanceDiagramOverlay;
using Xunit;

namespace Flowbit.Tests;

public sealed class InstanceDiagramOverlayTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T09:00:00Z");

    [Fact]
    public void RunningInstanceMarksEachActivePositionWithWhatItWaitsFor()
    {
        var overlay = Overlay.Build(Instance("running",
        [
            Position(1, 3, BpmnFlowNodeTypes.UserTask, "active", task: 501),
            Position(2, 4, BpmnFlowNodeTypes.IntermediateMessageCatchEvent, "active"),
            Position(3, 5, BpmnFlowNodeTypes.IntermediateTimerCatchEvent, "active", wait: "timerCatch"),
            Position(4, 6, BpmnFlowNodeTypes.IntermediateConditionalCatchEvent, "active", wait: "conditionalWake"),
            Position(5, 7, BpmnFlowNodeTypes.ServiceTask, "active", wait: "asyncBefore"),
            Position(6, 8, BpmnFlowNodeTypes.ParallelGateway, "active"),
            Position(7, 9, BpmnFlowNodeTypes.EndEvent, "completed", reason: "normalEnd"),
            Position(8, 10, BpmnFlowNodeTypes.UserTask, "cancelled", reason: "gatewayScopeCancelled")
        ]));

        Assert.Equal(
            new (int, string)[]
            {
                (3, "Waiting for user work"),
                (4, "Waiting for a message"),
                (5, "Waiting for a timer"),
                (6, "Queued for background processing"),
                (7, "Queued for background processing"),
                (8, "Waiting at the gateway")
            },
            overlay.Markers.Select(marker => (marker.NodeId, marker.Label)));
        Assert.All(overlay.Markers, marker =>
        {
            Assert.Equal("active", marker.State);
            Assert.Null(marker.Badge);
        });
        Assert.Null(overlay.Find(9));
        Assert.Null(overlay.Find(10));
    }

    [Fact]
    public void SeveralTokensAtOneNodeShareOneMarkerWithACountBadge()
    {
        var overlay = Overlay.Build(Instance("running",
        [
            Position(2, 3, BpmnFlowNodeTypes.UserTask, "active", task: 502),
            Position(1, 3, BpmnFlowNodeTypes.UserTask, "active", task: 501)
        ]));

        var marker = Assert.Single(overlay.Markers);
        Assert.Equal("×2", marker.Badge);
        Assert.Equal("Waiting for user work (2 positions)", marker.Label);
        Assert.Equal(new long[] { 1, 2 }, marker.Positions.Select(position => position.TokenId));
    }

    [Fact]
    public void MultiInstancePositionShowsItsProgress()
    {
        var withPending = Overlay.Build(Instance("running",
            [Position(1, 3, BpmnFlowNodeTypes.UserTask, "active", task: 501, multiInstance: 77)],
            multiInstances: [Progress(77, total: 4, completed: 1, active: 2, pending: 1)]));
        var marker = Assert.Single(withPending.Markers);
        Assert.Equal("1/4", marker.Badge);
        Assert.Equal("Multi-instance work: 1 of 4 completed, 2 active, 1 pending", marker.Label);

        // Older responses carry only the single MultiInstance property.
        var legacyProgress = Overlay.Build(Instance("running",
            [Position(1, 3, BpmnFlowNodeTypes.UserTask, "active", multiInstance: 78)],
            multiInstance: Progress(78, total: 3, completed: 3, active: 0, pending: 0)));
        Assert.Equal("Multi-instance work: 3 of 3 completed, 0 active", Assert.Single(legacyProgress.Markers).Label);

        // A multi-instance task waiting for its asynchronous continuation is not user work.
        var background = Overlay.Build(Instance("running",
            [Position(1, 3, BpmnFlowNodeTypes.UserTask, "active", multiInstance: 77, wait: "asyncAfter")],
            multiInstances: [Progress(77, total: 2, completed: 2, active: 0, pending: 0)]));
        var backgroundMarker = Assert.Single(background.Markers);
        Assert.Null(backgroundMarker.Badge);
        Assert.Equal("Queued for background processing", backgroundMarker.Label);
    }

    [Fact]
    public void CompletedInstanceMarksTheEndsItReachedButNotEarlierCancellations()
    {
        var overlay = Overlay.Build(Instance("completed",
        [
            Position(1, 4, BpmnFlowNodeTypes.UserTask, "cancelled", reason: "gatewayScopeCancelled"),
            Position(2, 9, BpmnFlowNodeTypes.EndEvent, "completed", reason: "normalEnd"),
            Position(3, 10, BpmnFlowNodeTypes.EndEvent, "completed", reason: "normalEnd")
        ]));

        Assert.Equal(new[] { 9, 10 }, overlay.Markers.Select(marker => marker.NodeId));
        Assert.All(overlay.Markers, marker =>
        {
            Assert.Equal("ended", marker.State);
            Assert.Equal("Ended here", marker.Label);
        });
    }

    [Fact]
    public void TerminatedInstanceMarksTheTerminateEndAndTheWorkItCancelled()
    {
        var overlay = Overlay.Build(Instance("completed",
        [
            Position(1, 9, BpmnFlowNodeTypes.TerminateEndEvent, "completed", reason: "terminateEnd"),
            Position(2, 4, BpmnFlowNodeTypes.UserTask, "cancelled", reason: "terminateEnd")
        ]));

        Assert.Equal(("ended", "Terminated here"), Pair(overlay.Find(9)));
        Assert.Equal(("cancelled", "Cancelled by a terminate end event"), Pair(overlay.Find(4)));
    }

    [Fact]
    public void FaultedInstanceMarksTheErrorEndWithItsCodeAndCancelledSiblings()
    {
        var overlay = Overlay.Build(Instance("faulted",
            [
                Position(1, 11, BpmnFlowNodeTypes.ErrorEndEvent, "faulted", reason: "errorEnd"),
                Position(2, 4, BpmnFlowNodeTypes.UserTask, "cancelled", reason: "errorEnd")
            ],
            fault: new FaultInfoDto("E-42", "Credit check failed")));

        Assert.Equal(("faulted", "Faulted here (E-42)"), Pair(overlay.Find(11)));
        Assert.Equal(("cancelled", "Cancelled when the instance faulted"), Pair(overlay.Find(4)));

        var legacyFault = Overlay.Build(Instance("faulted",
            [Position(1, 11, BpmnFlowNodeTypes.ErrorEndEvent, "faulted", reason: "errorEnd")],
            fault: new FaultInfoDto(null, "Legacy fault")));
        Assert.Equal("Faulted here", Assert.Single(legacyFault.Markers).Label);
    }

    [Fact]
    public void CancelledInstanceMarksWhereItsWorkStopped()
    {
        var overlay = Overlay.Build(Instance("cancelled",
        [
            Position(1, 3, BpmnFlowNodeTypes.UserTask, "cancelled", reason: "instanceCancelled"),
            Position(2, 5, BpmnFlowNodeTypes.IntermediateMessageCatchEvent, "cancelled", reason: "instanceCancelled")
        ]));

        Assert.Equal(new[] { 3, 5 }, overlay.Markers.Select(marker => marker.NodeId));
        Assert.All(overlay.Markers, marker => Assert.Equal(("cancelled", "Cancelled with the instance"), Pair(marker)));
    }

    [Fact]
    public void TheStrongestFinalStateWinsWhenPositionsShareANode()
    {
        var overlay = Overlay.Build(Instance("faulted",
        [
            Position(1, 6, BpmnFlowNodeTypes.ErrorEndEvent, "cancelled", reason: "errorEnd"),
            Position(2, 6, BpmnFlowNodeTypes.ErrorEndEvent, "faulted", reason: "errorEnd")
        ]));

        var marker = Assert.Single(overlay.Markers);
        Assert.Equal("faulted", marker.State);
        Assert.Null(marker.Badge);
        Assert.Equal(2, marker.Positions.Count);
    }

    [Fact]
    public void ReactivatedInstanceMarksOnlyWhereItsLastRunFinished()
    {
        // Run 1 ended at node 9; reactivation A (token 3) was cancelled at node 4;
        // reactivation B (token 5) forked token 6, and both reached an end.
        IReadOnlyList<InstanceHistoryDto> history = [Reactivated(70, token: 3), Reactivated(90, token: 5)];
        var overlay = Overlay.Build(Instance("completed",
            [
                Position(1, 9, BpmnFlowNodeTypes.EndEvent, "completed", reason: "normalEnd"),
                Position(3, 4, BpmnFlowNodeTypes.UserTask, "cancelled", reason: "instanceCancelled"),
                Position(5, 10, BpmnFlowNodeTypes.EndEvent, "completed", reason: "normalEnd"),
                Position(6, 11, BpmnFlowNodeTypes.EndEvent, "completed", reason: "normalEnd")
            ],
            history: history));

        Assert.Equal(new[] { 10, 11 }, overlay.Markers.Select(marker => marker.NodeId));

        var running = Overlay.Build(Instance("running",
            [
                Position(1, 9, BpmnFlowNodeTypes.EndEvent, "completed", reason: "normalEnd"),
                Position(5, 3, BpmnFlowNodeTypes.UserTask, "active", task: 80)
            ],
            history: history));
        Assert.Equal(3, Assert.Single(running.Markers).NodeId);
    }

    [Fact]
    public void ReactivatedInstanceKeepsItsLastRunAfterRetentionRemovesItsHistory()
    {
        // The instance records the reactivation's root token, so no history is needed.
        var overlay = Overlay.Build(Instance("cancelled",
            [
                Position(1, 9, BpmnFlowNodeTypes.EndEvent, "completed", reason: "normalEnd"),
                Position(5, 3, BpmnFlowNodeTypes.UserTask, "cancelled", reason: "instanceCancelled")
            ],
            lastReactivationTokenId: 5));

        Assert.Equal(("cancelled", "Cancelled with the instance"), Pair(Assert.Single(overlay.Markers)));
        Assert.Equal(3, overlay.Markers[0].NodeId);
    }

    [Fact]
    public void SeveralMultiInstanceExecutionsAtOneNodeAreCountedAndTheirProgressCombined()
    {
        var overlay = Overlay.Build(Instance("running",
            [
                Position(1, 3, BpmnFlowNodeTypes.UserTask, "active", task: 600, multiInstance: 77),
                Position(2, 3, BpmnFlowNodeTypes.UserTask, "active", task: 700, multiInstance: 78)
            ],
            multiInstances:
            [
                Progress(77, total: 3, completed: 1, active: 2, pending: 0),
                Progress(78, total: 2, completed: 0, active: 1, pending: 1)
            ]));

        var marker = Assert.Single(overlay.Markers);
        Assert.Equal("×2", marker.Badge);
        Assert.Equal("Multi-instance work in 2 executions: 1 of 5 completed, 3 active, 1 pending", marker.Label);

        // Positions that wait for different things are each described.
        var mixed = Overlay.Build(Instance("running",
            [
                Position(1, 3, BpmnFlowNodeTypes.UserTask, "active", task: 600, multiInstance: 77),
                Position(2, 3, BpmnFlowNodeTypes.UserTask, "active", wait: "asyncAfter")
            ],
            multiInstances: [Progress(77, total: 3, completed: 1, active: 2, pending: 0)]));
        var mixedMarker = Assert.Single(mixed.Markers);
        Assert.Equal("×2", mixedMarker.Badge);
        Assert.Equal("Waiting for multi-instance work; Queued for background processing (2 positions)", mixedMarker.Label);
    }

    [Theory]
    [InlineData("completed", BpmnFlowNodeTypes.EndEvent, "ended", "Ended here")]
    [InlineData("cancelled", BpmnFlowNodeTypes.UserTask, "cancelled", "Cancelled here")]
    public void TokensFinishedBeforeTerminationReasonsExistedMarkWhereTheInstanceStopped(
        string status, string nodeType, string state, string label)
    {
        // Such instances had one token, whose status followed the instance's.
        var overlay = Overlay.Build(Instance(status, [Position(1, 9, nodeType, status)]));

        var marker = Assert.Single(overlay.Markers);
        Assert.Equal((9, state, label), (marker.NodeId, marker.State, marker.Label));
    }

    [Theory]
    [InlineData("running", "active", "Current position")]
    [InlineData("completed", "ended", "Ended here")]
    [InlineData("faulted", "faulted", "Faulted here")]
    [InlineData("cancelled", "cancelled", "Cancelled here")]
    public void LegacyInstanceWithoutPositionsMarksItsCurrentNode(string status, string state, string label)
    {
        var overlay = Overlay.Build(Instance(status, [], currentNodeId: 12));

        var marker = Assert.Single(overlay.Markers);
        Assert.Equal((12, state, label), (marker.NodeId, marker.State, marker.Label));
        Assert.Empty(marker.Positions);
    }

    [Fact]
    public void ViewerOverlayProjectsMarkersAndItsSignatureFollowsTheirContent()
    {
        var first = Overlay.Build(Instance("running", [Position(1, 3, BpmnFlowNodeTypes.UserTask, "active")]));
        var same = Overlay.Build(Instance("running", [Position(1, 3, BpmnFlowNodeTypes.UserTask, "active")]));
        var moved = Overlay.Build(Instance("running", [Position(1, 4, BpmnFlowNodeTypes.UserTask, "active")]));

        var json = JsonSerializer.Serialize(first.ViewerOverlay, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal("""{"nodes":[{"id":3,"state":"active","badge":null,"label":"Waiting for user work"}]}""", json);
        Assert.Equal(first.Signature, same.Signature);
        Assert.NotEqual(first.Signature, moved.Signature);
        Assert.Empty(Overlay.Build(Instance("unknown", [], currentNodeId: 3)).Markers);
    }

    private static (string, string) Pair(FlowbitUi::Flowbit.Ui.Components.Shared.InstanceDiagramMarker? marker)
    {
        Assert.NotNull(marker);
        return (marker.State, marker.Label);
    }

    internal static InstanceDetailDto Instance(
        string status,
        IReadOnlyList<ExecutionPositionDto> positions,
        IReadOnlyList<MultiInstanceProgressDto>? multiInstances = null,
        MultiInstanceProgressDto? multiInstance = null,
        FaultInfoDto? fault = null,
        int currentNodeId = 3,
        WorkflowModel? definition = null,
        IReadOnlyList<InstanceHistoryDto>? history = null,
        long? lastReactivationTokenId = null)
    {
        var workflow = new WorkflowDetailDto(17, "Purchase review", "purchase-review", 3, true, true, Now,
            definition ?? new WorkflowModel { Id = "purchase-review", Name = "Purchase review" });
        return new InstanceDetailDto(42, workflow, currentNodeId, "Current", null, status, null, null, "starter",
            Now, Now, [], history ?? [], multiInstance, null, fault)
        {
            ExecutionPositions = positions,
            MultiInstances = multiInstances ?? [],
            LastReactivationTokenId = lastReactivationTokenId
        };
    }

    internal static ExecutionPositionDto Position(
        long token,
        int node,
        string type,
        string status,
        string? reason = null,
        long? task = null,
        long? multiInstance = null,
        string? wait = null) =>
        new(token, node, $"Node {node}", null, type, status, null, reason, task, multiInstance, null, wait);

    // The audit row reactivation writes for the new token that starts the next run.
    internal static InstanceHistoryDto Reactivated(long id, long token) =>
        new(id, token, null, null, null, null, 9, 3, "operator", null, "instanceReactivated", Now);

    internal static MultiInstanceProgressDto Progress(long executionId, int total, int completed, int active, int pending) =>
        new(executionId, "parallel", "active", total, completed, active, pending, 0, null, null, []);
}
