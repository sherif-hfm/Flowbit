using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class InstanceDiagramHistoryQueryServiceTests
{
    [Fact]
    public async Task CountsExactlyOneEndOfEachFlowAndReachesTargetWithoutVisit()
    {
        var snapshot = Snapshot() with
        {
            ReachedNodeIds = [2, 1, 1],
            Exits = [new(1, 101, 3), new(2, 201, 2)],
            Entries = [new(2, 101, 3), new(3, 201, 2)]
        };
        var result = await ReadAsync(snapshot);
        Assert.Equal([1, 2, 3], result.Nodes.Select(node => node.NodeId));
        Assert.Equal([new InstanceDiagramHistoryFlowDto(101, 3), new(201, 2)], result.Flows);
        Assert.Equal("complete", result.Coverage.Status);
        Assert.Equal("currentVersionAllRuns", result.Scope);
        Assert.Equal(snapshot.InstanceUpdatedAt, result.InstanceUpdatedAt);
        Assert.Equal(snapshot.AsOf, result.AsOf);
    }

    [Fact]
    public async Task AsyncAfterUsesTargetEntryAndMultiInstanceTargetUsesGroupedActivations()
    {
        var snapshot = Snapshot();
        snapshot.Definition.FlowNodes[0].AsyncAfter = true;
        snapshot.Definition.FlowNodes[1].MultiInstance = new MultiInstanceModel();
        var result = await ReadAsync(snapshot with
        {
            ReachedNodeIds = [1, 2, 3],
            Exits = [new(1, 101, 100)],
            Entries = [new(3, 201, 7)],
            MultiInstanceEntries = [new(2, 101, 4)]
        });
        Assert.Equal([new InstanceDiagramHistoryFlowDto(101, 4), new(201, 7)], result.Flows);
        Assert.Equal("unknown", result.Coverage.Status);
        Assert.Equal(["multiInstanceEntryUnrecorded"], result.Coverage.Reasons);
    }

    [Fact]
    public async Task OrdinarySourceMarksMultiInstanceArrivalWithoutChildren()
    {
        var snapshot = Snapshot();
        snapshot.Definition.FlowNodes[1].MultiInstance = new MultiInstanceModel();
        var result = await ReadAsync(snapshot with { ReachedNodeIds = [1], Exits = [new(1, 101, 1)] });
        Assert.Equal([1, 2], result.Nodes.Select(node => node.NodeId));
        Assert.Equal(new InstanceDiagramHistoryFlowDto(101, 1), Assert.Single(result.Flows));
        Assert.Equal("complete", result.Coverage.Status);
    }

    [Fact]
    public async Task RejectsUnknownAndWrongEndEvidenceWithoutInventingConnectors()
    {
        var result = await ReadAsync(Snapshot() with
        {
            ReachedNodeIds = [1, 999],
            Exits = [new(2, 101, 1), new(1, 999, 1), new(1, 101, 0)],
            Entries = [new(1, 101, 1)],
            MultiInstanceEntries = [new(2, 101, 1)]
        });
        Assert.Equal([1], result.Nodes.Select(node => node.NodeId));
        Assert.Empty(result.Flows);
        Assert.Equal("partial", result.Coverage.Status);
        Assert.Equal(["unmappedEvidence"], result.Coverage.Reasons);
    }

    [Theory]
    [InlineData(true, false, false, false, "complete", "")]
    [InlineData(true, false, true, false, "complete", "")]
    [InlineData(false, false, false, false, "unknown", "legacyCoverageUnknown")]
    [InlineData(false, false, true, false, "unknown", "legacyCoverageUnknown,retentionCategoryUnknown")]
    [InlineData(false, true, false, false, "partial", "cutover")]
    [InlineData(false, true, true, false, "partial", "cutover,retentionCategoryUnknown")]
    [InlineData(true, false, true, true, "partial", "nodeActivityPruned")]
    [InlineData(false, false, true, true, "partial", "nodeActivityPruned,legacyCoverageUnknown")]
    public async Task CoverageIsConservativeAndCategorySpecific(
        bool fromCreation, bool seeded, bool historyPruned, bool nodePruned, string status, string reasons)
    {
        var now = DateTimeOffset.UtcNow;
        var result = await ReadAsync(Snapshot() with
        {
            NodeActivityFromCreation = fromCreation,
            HasCutoverSeeded = seeded,
            HistoryPrunedAt = historyPruned ? now : null,
            NodeActivityPrunedAt = nodePruned ? now : null
        });
        Assert.Empty(result.Nodes);
        Assert.Empty(result.Flows);
        Assert.Equal(status, result.Coverage.Status);
        Assert.Equal(reasons, string.Join(',', result.Coverage.Reasons));
        Assert.Equal(nodePruned ? now : null, result.Coverage.NodeActivityPrunedAt);
    }

    [Fact]
    public async Task PartialReasonsPrecedeUnknownReasonsAndAllSurvive()
    {
        var snapshot = Snapshot();
        snapshot.Definition.FlowNodes[0].AsyncAfter = true;
        snapshot.Definition.FlowNodes[1].MultiInstance = new MultiInstanceModel();
        var result = await ReadAsync(snapshot with
        {
            NodeActivityFromCreation = false,
            HistoryPrunedAt = DateTimeOffset.UtcNow,
            HasConflictingEntries = true,
            HasUnmappedMultiInstanceNodes = true
        });
        Assert.Equal("partial", result.Coverage.Status);
        Assert.Equal(["unmappedEvidence", "conflictingEvidence", "legacyCoverageUnknown",
            "retentionCategoryUnknown", "multiInstanceEntryUnrecorded"], result.Coverage.Reasons);
    }

    [Fact]
    public async Task CountsAreInt64AndCancellationNeverReturnsPartialEvidence()
    {
        var result = await ReadAsync(Snapshot() with { Exits = [new(1, 101, (long)int.MaxValue + 1)] });
        Assert.Equal((long)int.MaxValue + 1, Assert.Single(result.Flows).TraversalCount);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new InstanceDiagramHistoryQueryService(new StubRepository(Snapshot()))
                .GetAsync(1, cancellation.Token));
    }

    [Fact]
    public async Task MissingInstanceRemainsMissing() => Assert.Null(await
        new InstanceDiagramHistoryQueryService(new StubRepository(null)).GetAsync(1, CancellationToken.None));

    private static async Task<InstanceDiagramHistoryDto> ReadAsync(InstanceDiagramHistorySnapshot snapshot) =>
        Assert.IsType<InstanceDiagramHistoryDto>(await new InstanceDiagramHistoryQueryService(
            new StubRepository(snapshot)).GetAsync(snapshot.InstanceId, CancellationToken.None));

    private static InstanceDiagramHistorySnapshot Snapshot() => new()
    {
        InstanceId = 10, WorkflowId = 20, WorkflowVersion = 2,
        InstanceUpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1), AsOf = DateTimeOffset.UtcNow,
        NodeActivityFromCreation = true,
        Definition = new WorkflowModel
        {
            FlowNodes =
            [
                new() { Id = 1, Type = BpmnFlowNodeTypes.StartEvent },
                new() { Id = 2, Type = BpmnFlowNodeTypes.UserTask },
                new() { Id = 3, Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows = [new() { Id = 101, SourceRef = 1, TargetRef = 2 }, new() { Id = 201, SourceRef = 2, TargetRef = 3 }]
        }
    };

    private sealed class StubRepository(InstanceDiagramHistorySnapshot? snapshot) : IInstanceDiagramHistoryQueryRepository
    {
        public Task<InstanceDiagramHistorySnapshot?> GetAsync(long instanceId, CancellationToken cancellationToken) =>
            Task.FromResult(snapshot);
    }
}
