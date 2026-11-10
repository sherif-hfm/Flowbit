using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class InstanceDiagramHistoryApiTests(PostgresApiFixture fixture)
    : InstanceDiagramHistoryApiTestBase(fixture)
{
    [Fact]
    public async Task SummaryUsesExactlyInstanceDetailReadBoundaryWhileExecutionsStayRestricted()
    {
        var model = Model();
        // Other collection tests change the global activity-reader role. Give
        // this reader an explicit version-local scope without changing settings.
        model.TaskAssignmentRoles.Add("admin");
        var instance = await CreateAndStartAsync(model);
        foreach (var user in new string?[] { null, "reviewer", "administrator", new('x', 301) })
        {
            using var detail = await SendAsync(HttpMethod.Get, $"/api/instances/{instance.Id}", user: user);
            using var summary = await SendAsync(HttpMethod.Get, $"/api/instances/{instance.Id}/diagram-history", user: user);
            Assert.Equal(detail.StatusCode, summary.StatusCode);
            if (user is null) Assert.Equal(HttpStatusCode.Unauthorized, summary.StatusCode);
            else if (user.Length < 301) Assert.Equal(HttpStatusCode.OK, summary.StatusCode);
            else Assert.NotEqual(HttpStatusCode.OK, summary.StatusCode);
        }
        using var executions = await SendAsync(HttpMethod.Get, $"/api/node-executions?instanceId={instance.Id}", user: "reviewer");
        Assert.Empty((await ReadAsync<PagedResult<NodeExecutionSummaryDto>>(executions)).Items);
        using var administratorExecutions = await SendAsync(HttpMethod.Get, $"/api/node-executions?instanceId={instance.Id}");
        Assert.NotEmpty((await ReadAsync<PagedResult<NodeExecutionSummaryDto>>(administratorExecutions)).Items);
        using var missing = await SendAsync(HttpMethod.Get, "/api/instances/9223372036854775807/diagram-history");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
    }

    [Fact]
    public async Task LoopsAggregateAllVisitsOnceInOneQueryWithoutDetailedData()
    {
        var model = Model();
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 2, Name = "Again" });
        var instance = await CreateAndStartAsync(model);
        for (var index = 0; index < 55; index++) await TakeAsync(instance.Id, 202);
        await TakeAsync(instance.Id, 201);
        await using var db = Fixture.CreateDbContext();
        var expectedRevision = await db.WorkflowInstances.Where(row => row.Id == instance.Id)
            .Select(row => row.UpdatedAt).SingleAsync();
        var databaseBefore = await db.Database.SqlQueryRaw<DateTimeOffset>(
            "SELECT statement_timestamp() AS \"Value\"").SingleAsync();
        Fixture.CommandCounter.Reset(captureReaderCommandTexts: true);
        using var response = await SendAsync(HttpMethod.Get, $"/api/instances/{instance.Id}/diagram-history", user: "reviewer");
        var result = await ReadAsync<InstanceDiagramHistoryDto>(response);
        Assert.Single(Fixture.CommandCounter.ReaderCommandTexts);
        Assert.Contains("GROUP BY", Fixture.CommandCounter.ReaderCommandTexts[0]);
        Assert.Equal([1, 2, 3], result.Nodes.Select(node => node.NodeId));
        Assert.Equal([new InstanceDiagramHistoryFlowDto(101, 1), new(201, 1), new(202, 55)], result.Flows);
        Assert.Equal("complete", result.Coverage.Status);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(["instanceId", "workflowId", "workflowVersion", "instanceUpdatedAt", "asOf", "scope", "coverage", "nodes", "flows"],
            document.RootElement.EnumerateObject().Select(property => property.Name));
        var databaseAfter = await db.Database.SqlQueryRaw<DateTimeOffset>(
            "SELECT statement_timestamp() AS \"Value\"").SingleAsync();
        Assert.Equal(expectedRevision, result.InstanceUpdatedAt);
        // AsOf uses the database clock; the revision was written by the API
        // host clock and need not sort before it when the hosts are skewed.
        Assert.InRange(result.AsOf, databaseBefore, databaseAfter);
    }

    [Fact]
    public async Task PersistedLegacyDefinitionUsesTheSameNormalizedGraphAsInstanceDetail()
    {
        // Simulate an existing JSONB snapshot from before the BPMN model. The
        // current creation endpoint normalizes imports before storing them and
        // therefore cannot reproduce this upgrade path.
        var legacy = new WorkflowModel
        {
            Id = $"diagram-legacy-{Guid.NewGuid():N}", Name = "Legacy diagram history", LegacyInitialStepId = 1,
            LegacySteps =
            [
                new() { Id = 1, Name = "Start", Type = "start", NextStepId = 2 },
                new()
                {
                    Id = 2, Name = "Review", Type = "task",
                    Actions = [new() { Id = 201, Name = "Complete", ToStepId = 3 }]
                },
                new() { Id = 3, Name = "Done", Type = "end" }
            ]
        };
        long workflowId;
        await using (var db = Fixture.CreateDbContext())
        {
            var definition = new WorkflowDefinitionEntity
            {
                Name = legacy.Name, WorkflowKey = legacy.Id, Version = 1,
                Definition = legacy, IsPublished = true
            };
            db.WorkflowDefinitions.Add(definition);
            await db.SaveChangesAsync();
            workflowId = definition.Id;
        }

        var instance = await StartAsync(workflowId);
        using var detailResponse = await SendAsync(HttpMethod.Get, $"/api/instances/{instance.Id}");
        var detail = await ReadAsync<InstanceDetailDto>(detailResponse);
        Assert.Equal([1, 2, 3], detail.Workflow.Definition.FlowNodes.Select(node => node.Id));
        Assert.Equal([1, 201], detail.Workflow.Definition.SequenceFlows.Select(flow => flow.Id));
        Assert.Null(detail.Workflow.Definition.LegacySteps);

        var waiting = await HistoryAsync(instance.Id);
        Assert.Equal([1, 2], waiting.Nodes.Select(node => node.NodeId));
        Assert.Equal(new InstanceDiagramHistoryFlowDto(1, 1), Assert.Single(waiting.Flows));
        Assert.Equal("complete", waiting.Coverage.Status);
        Assert.Empty(waiting.Coverage.Reasons);

        await TakeAsync(instance.Id, 201);
        var completed = await HistoryAsync(instance.Id);
        Assert.Equal([1, 2, 3], completed.Nodes.Select(node => node.NodeId));
        Assert.Equal([new InstanceDiagramHistoryFlowDto(1, 1), new(201, 1)], completed.Flows);
        Assert.Equal("complete", completed.Coverage.Status);

        await using var storedDb = Fixture.CreateDbContext();
        var stored = await storedDb.WorkflowDefinitions.AsNoTracking().SingleAsync(row => row.Id == workflowId);
        Assert.Empty(stored.Definition.FlowNodes);
        Assert.Empty(stored.Definition.SequenceFlows);
        Assert.Equal(3, stored.Definition.LegacySteps!.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MultiInstanceEntriesCountActivationsIncludingPendingChildrenAndVotesAreNotTraversals(bool asyncAfterSource)
    {
        var model = Model(multiInstance: true);
        if (asyncAfterSource) AddAsyncSource(model);
        var instance = await CreateAndStartAsync(model);
        if (asyncAfterSource) await ProcessAsync(instance.Id, WorkflowJobKinds.AsyncAfter);
        var initial = await HistoryAsync(instance.Id);
        Assert.Equal(1, initial.Flows.Single(flow => flow.FlowId == (asyncAfterSource ? 501 : 101)).TraversalCount);
        Assert.Equal(asyncAfterSource ? "unknown" : "complete", initial.Coverage.Status);
        if (asyncAfterSource) Assert.Contains("multiInstanceEntryUnrecorded", initial.Coverage.Reasons);
        await TakeAsync(instance.Id, 201);
        Assert.DoesNotContain((await HistoryAsync(instance.Id)).Flows, flow => flow.FlowId == 201);
        await TakeAsync(instance.Id, 201);
        var complete = await HistoryAsync(instance.Id);
        Assert.Equal(1, complete.Flows.Single(flow => flow.FlowId == 201).TraversalCount);
        Assert.Equal(1, complete.Flows.Single(flow => flow.FlowId == (asyncAfterSource ? 501 : 101)).TraversalCount);
    }

    [Fact]
    public async Task DeferredFanoutChangesSummaryAndRevisionAndConflictingChildrenAreOmitted()
    {
        var model = Model(multiInstance: true);
        model.FlowNodes[1].AsyncBefore = true;
        AddAsyncSource(model);
        var instance = await CreateAndStartAsync(model);
        await ProcessAsync(instance.Id, WorkflowJobKinds.AsyncAfter);
        var waiting = await HistoryAsync(instance.Id);
        Assert.DoesNotContain(waiting.Nodes, node => node.NodeId == 2);
        Assert.DoesNotContain(waiting.Flows, flow => flow.FlowId == 501);
        await ProcessAsync(instance.Id, WorkflowJobKinds.AsyncBefore);
        var active = await HistoryAsync(instance.Id);
        Assert.True(active.InstanceUpdatedAt > waiting.InstanceUpdatedAt);
        Assert.Contains(active.Nodes, node => node.NodeId == 2);
        Assert.Equal(1, active.Flows.Single(flow => flow.FlowId == 501).TraversalCount);

        // Isolated repository-corruption test, not a browser fixture or a
        // manufactured workflow outcome: conflicting retained evidence must fail closed.
        await using var db = Fixture.CreateDbContext();
        var child = await db.NodeExecutions.Where(row => row.InstanceId == instance.Id && row.NodeId == 2)
            .OrderByDescending(row => row.Id).FirstAsync();
        child.EnteredViaFlowId = null;
        await db.SaveChangesAsync();
        var conflict = await HistoryAsync(instance.Id);
        Assert.Equal("partial", conflict.Coverage.Status);
        Assert.Contains("conflictingEvidence", conflict.Coverage.Reasons);
        Assert.DoesNotContain(conflict.Flows, flow => flow.FlowId == 501);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WaitingVisitsRebindButIncomingOrdinaryTraversalStaysOnOldVersion(bool multiInstance)
    {
        var model = Model(multiInstance);
        var workflow = await CreateAsync(model);
        var instance = await StartAsync(workflow.Id);
        model.Name = "Updated diagram version";
        var target = await CreateAsync(model);
        using var previewResponse = await SendAsync(HttpMethod.Post, $"/api/instances/{instance.Id}/version-change/preview", new PreviewInstanceVersionChangeRequest(target.Id));
        var preview = await ReadAsync<InstanceVersionChangePreviewDto>(previewResponse);
        using var change = await SendAsync(HttpMethod.Post, $"/api/instances/{instance.Id}/version-change",
            new ChangeInstanceVersionRequest(target.Id, preview.ExpectedSourceWorkflowId, preview.ExpectedUpdatedAt, "Verify diagram attribution"));
        await ExpectAsync(change, HttpStatusCode.OK);
        var waiting = await HistoryAsync(instance.Id);
        Assert.Equal(target.Id, waiting.WorkflowId);
        Assert.Equal(target.Version, waiting.WorkflowVersion);
        Assert.Equal([2], waiting.Nodes.Select(node => node.NodeId));
        Assert.Empty(waiting.Flows);
        await TakeAsync(instance.Id, 201);
        if (multiInstance) await TakeAsync(instance.Id, 201);
        var complete = await HistoryAsync(instance.Id);
        Assert.Equal([2, 3], complete.Nodes.Select(node => node.NodeId));
        Assert.Equal(new InstanceDiagramHistoryFlowDto(201, 1), Assert.Single(complete.Flows));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ReboundTargetEntryUsesDisplayedSourceCountingRule(bool initialAsyncAfter, bool displayedAsyncAfter)
    {
        var model = Model();
        AddAsyncSource(model);
        model.FlowNodes.Single(node => node.Id == 5).AsyncAfter = initialAsyncAfter;
        var workflow = await CreateAsync(model);
        var instance = await StartAsync(workflow.Id);
        if (initialAsyncAfter) await ProcessAsync(instance.Id, WorkflowJobKinds.AsyncAfter);
        Assert.Equal(1, (await HistoryAsync(instance.Id)).Flows.Single(flow => flow.FlowId == 501).TraversalCount);
        model.Name = "Changed completed source";
        model.FlowNodes.Single(node => node.Id == 5).AsyncAfter = displayedAsyncAfter;
        var target = await CreateAsync(model);
        using var previewResponse = await SendAsync(HttpMethod.Post, $"/api/instances/{instance.Id}/version-change/preview", new PreviewInstanceVersionChangeRequest(target.Id));
        var preview = await ReadAsync<InstanceVersionChangePreviewDto>(previewResponse);
        using var change = await SendAsync(HttpMethod.Post, $"/api/instances/{instance.Id}/version-change",
            new ChangeInstanceVersionRequest(target.Id, preview.ExpectedSourceWorkflowId, preview.ExpectedUpdatedAt, "Verify displayed source counting rule"));
        await ExpectAsync(change, HttpStatusCode.OK);

        var rebound = await HistoryAsync(instance.Id);
        Assert.Equal(target.Id, rebound.WorkflowId);
        Assert.Equal([2], rebound.Nodes.Select(node => node.NodeId));
        if (displayedAsyncAfter)
            Assert.Equal(new InstanceDiagramHistoryFlowDto(501, 1), Assert.Single(rebound.Flows));
        else
            Assert.Empty(rebound.Flows);
        await TakeAsync(instance.Id, 201);
        var completed = await HistoryAsync(instance.Id);
        Assert.Equal(1, completed.Flows.Single(flow => flow.FlowId == 201).TraversalCount);
        Assert.Equal(displayedAsyncAfter, completed.Flows.Any(flow => flow.FlowId == 501));
    }

    [Fact]
    public async Task SummarySeesCommittedSnapshotWithoutWaitingForRuntimeRowLock()
    {
        var instance = await CreateAndStartAsync(Model());
        var original = await HistoryAsync(instance.Id);
        await using var db = Fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.WorkflowInstances.Where(row => row.Id == instance.Id)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.UpdatedAt, DateTimeOffset.UtcNow.AddSeconds(1)));
        await db.NodeExecutions.Where(row => row.InstanceId == instance.Id && row.NodeId == 1)
            .ExecuteUpdateAsync(set => set.SetProperty(row => row.ExitedViaFlowId, 999));
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var request = Request(HttpMethod.Get, $"/api/instances/{instance.Id}/diagram-history");
        using var response = await Fixture.Client.SendAsync(request, timeout.Token);
        var concurrent = await ReadAsync<InstanceDiagramHistoryDto>(response);
        Assert.Equal(original.InstanceUpdatedAt, concurrent.InstanceUpdatedAt);
        Assert.Equal(original.Flows, concurrent.Flows);
        Assert.Equal("complete", concurrent.Coverage.Status);
        await transaction.RollbackAsync();
        Assert.Equal(original.Flows, (await HistoryAsync(instance.Id)).Flows);
    }

    [Fact]
    public async Task EmptyRetainedSummaryIsSuccessWithCoverageAndCancellationPropagates()
    {
        var instance = await CreateAndStartAsync(Model());
        await TakeAsync(instance.Id, 201);
        var prunedAt = DateTimeOffset.UtcNow;
        // Storage-shape check for fully pruned history. Retention's selection,
        // reference protection, and transactional marker have their own tests.
        await using (var db = Fixture.CreateDbContext())
        {
            await using var transaction = await db.Database.BeginTransactionAsync();
            await db.NodeExecutions.Where(row => row.InstanceId == instance.Id).ExecuteDeleteAsync();
            await db.WorkflowInstances.Where(row => row.Id == instance.Id).ExecuteUpdateAsync(set => set
                .SetProperty(row => row.NodeActivityPrunedAt, prunedAt)
                .SetProperty(row => row.HistoryPrunedAt, prunedAt)
                .SetProperty(row => row.UpdatedAt, prunedAt));
            await transaction.CommitAsync();
        }
        var summary = await HistoryAsync(instance.Id);
        Assert.Empty(summary.Nodes);
        Assert.Empty(summary.Flows);
        Assert.Equal("partial", summary.Coverage.Status);
        Assert.Equal(["nodeActivityPruned"], summary.Coverage.Reasons);
        Assert.NotNull(summary.Coverage.NodeActivityPrunedAt);

        await using var scope = Fixture.Factory.Services.CreateAsyncScope();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ServiceProvider
            .GetRequiredService<IInstanceDiagramHistoryQueryService>().GetAsync(instance.Id, cancellation.Token));
    }
}
