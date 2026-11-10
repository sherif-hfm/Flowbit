using System.Net.Http.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Shared.Dtos;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

internal static class DiagramHistoryEvidenceAssertions
{
    // These fixtures have ordinary sources: every traversal must have exactly
    // one committed source exit, including a boundary's outgoing flow.
    internal static async Task ExitsAsync(
        PostgresApiFixture fixture, AppDbContext db, long instanceId,
        params (int NodeId, int FlowId, int Count)[] expected)
    {
        var visits = await db.NodeExecutions.AsNoTracking()
            .Where(item => item.InstanceId == instanceId).ToListAsync();
        var actual = visits.Where(item => item.ExitedViaFlowId is not null)
            .GroupBy(item => (item.NodeId, FlowId: item.ExitedViaFlowId!.Value))
            .Select(group => (group.Key.NodeId, group.Key.FlowId, Count: group.Count()))
            .OrderBy(item => item.FlowId).ToArray();
        Assert.Equal(expected.OrderBy(item => item.FlowId).ToArray(), actual);
        Assert.All(visits.Where(item => item.ExitedViaFlowId is not null),
            item => Assert.Equal(NodeExecutionKinds.Node, item.ExecutionKind));
        await SummaryAsync(fixture, instanceId, visits.Select(item => item.NodeId).Distinct().ToArray(),
            expected.Select(item => (item.FlowId, (long)item.Count)).ToArray());
    }

    internal static async Task SummaryAsync(
        PostgresApiFixture fixture, long instanceId, IReadOnlyCollection<int> expectedNodes,
        params (int FlowId, long Count)[] expectedFlows)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/api/instances/{instanceId}/diagram-history");
        ApiTestAuth.Authorize(request, "diagram-evidence", ["Manager"]);
        using var response = await fixture.Client.SendAsync(request);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var summary = await response.Content.ReadFromJsonAsync<InstanceDiagramHistoryDto>();
        Assert.NotNull(summary);
        Assert.Equal(instanceId, summary.InstanceId);
        Assert.Equal(expectedNodes.Order(), summary.Nodes.Select(item => item.NodeId));
        Assert.Equal(expectedFlows.OrderBy(item => item.FlowId)
            .Select(item => new InstanceDiagramHistoryFlowDto(item.FlowId, item.Count)), summary.Flows);
    }
}
