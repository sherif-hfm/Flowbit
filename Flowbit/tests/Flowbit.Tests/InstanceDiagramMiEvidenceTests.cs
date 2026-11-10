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

// Characterizes the committed evidence used by the diagram, independently of its query/UI.
[Collection(PostgresApiCollection.Name)]
public sealed class InstanceDiagramMiEvidenceTests(PostgresApiFixture fixture) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly List<long> ownedInstances = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // A failed characterization must not leave a prioritized runnable job for
        // the next test's global lease query in this shared PostgreSQL fixture.
        await using var db = fixture.CreateDbContext();
        var now = DateTimeOffset.UtcNow;
        await db.WorkflowJobs.Where(job => job.InstanceId.HasValue && ownedInstances.Contains(job.InstanceId.Value)
                && (job.Status == WorkflowJobStatuses.Queued || job.Status == WorkflowJobStatuses.Running
                    || job.Status == WorkflowJobStatuses.Retry || job.Status == WorkflowJobStatuses.ResultReady))
            .ExecuteUpdateAsync(set => set.SetProperty(job => job.Status, WorkflowJobStatuses.Cancelled)
                .SetProperty(job => job.WorkerId, (string?)null)
                .SetProperty(job => job.LeaseToken, (Guid?)null)
                .SetProperty(job => job.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(job => job.CompletedAt, now)
                .SetProperty(job => job.UpdatedAt, now));
    }

    [Fact]
    public async Task StagedServiceActivationChangesVisitStatusWithoutAdvancingInstanceRevision()
    {
        var model = Model(multiInstance: false);
        model.FlowNodes[1].Type = BpmnFlowNodeTypes.ServiceTask;
        model.FlowNodes[1].AsyncBefore = true;
        model.FlowNodes[1].Service = new ServiceTaskModel { Url = "https://tests.local/typed-output-success" };
        var workflow = await CreateAsync(model);
        var instance = await StartAsync(workflow.Id);
        var pending = Assert.Single(await EvidenceAsync(instance.Id), row => row.NodeId == 2);
        Assert.Equal(NodeExecutionStatuses.Pending, pending.Status);
        fixture.ServiceInvocations.Reset();
        var block = fixture.ServiceInvocations.BlockNext("/typed-output-success");
        var processing = ProcessJobAsync(instance.Id, WorkflowJobKinds.AsyncBefore);
        try
        {
            await block.WaitUntilEnteredAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
            var active = Assert.Single(await EvidenceAsync(instance.Id), row => row.NodeId == 2);
            Assert.Equal(pending.Id, active.Id);
            Assert.Equal(NodeExecutionStatuses.Active, active.Status);
            Assert.True(active.UpdatedAt > pending.UpdatedAt);
            // The activation is committed separately while external work is in flight.
            // The broad "every execution change advances UpdatedAt" assumption is false;
            // its node/entry/exit/definition evidence is unchanged, however.
            Assert.Equal(instance.UpdatedAt, await RevisionAsync(instance.Id));
            Assert.Equal(pending.EnteredViaFlowId, active.EnteredViaFlowId);
            Assert.Equal(pending.ExitedViaFlowId, active.ExitedViaFlowId);
            Assert.Equal(pending.WorkflowDefinitionId, active.WorkflowDefinitionId);
        }
        finally
        {
            block.Release();
            await processing;
            fixture.ServiceInvocations.Reset();
        }
        Assert.True(await RevisionAsync(instance.Id) > instance.UpdatedAt);
        Assert.Equal(201, Assert.Single(await EvidenceAsync(instance.Id), row => row.NodeId == 2).ExitedViaFlowId);
        await AssertRevisionCoversEvidenceAsync(instance.Id);
    }

    [Theory]
    [InlineData("parallel")]
    [InlineData("sequential")]
    public async Task MultiInstanceEntryCountsSourceExitOnceAndVotesDoNotAddTraversals(string mode)
    {
        var model = Model(multiInstance: true, mode);
        var workflow = await CreateAsync(model);
        var instance = await StartAsync(workflow.Id);
        var initial = await EvidenceAsync(instance.Id);
        Assert.Single(initial, row => row.NodeId == 1 && row.ExitedViaFlowId == 101);
        var children = initial.Where(row => row.NodeId == 2).ToArray();
        Assert.Equal(2, children.Length);
        Assert.All(children, row =>
        {
            Assert.Equal(NodeExecutionKinds.UserTaskItem, row.ExecutionKind);
            Assert.Equal(101, row.EnteredViaFlowId);
            Assert.Null(row.ExitedViaFlowId);
        });
        Assert.Single(children.Select(row => row.MultiInstanceExecutionId).Distinct());
        Assert.Equal(mode == "sequential" ? 1 : 0, children.Count(row => row.Status == NodeExecutionStatuses.Pending));

        var previousRevision = instance.UpdatedAt;
        for (var item = 0; item < 2; item++)
        {
            var taskId = await ActiveTaskAsync(instance.Id, 2);
            await TakeAsync(taskId, 201);
            var revision = await RevisionAsync(instance.Id);
            Assert.True(revision > previousRevision);
            previousRevision = revision;
        }
        var final = await EvidenceAsync(instance.Id);
        Assert.Equal(2, final.Count(row => row.SelectedFlowId == 201));
        Assert.DoesNotContain(final, row => row.NodeId == 2 && row.ExitedViaFlowId != null);
        Assert.Single(final, row => row.NodeId == 3 && row.EnteredViaFlowId == 201);
        await AssertRevisionCoversEvidenceAsync(instance.Id);
    }

    [Fact]
    public async Task AsyncBeforeMiFanOutAdvancesInstanceRevisionWithNewSummaryEvidenceAndReplayIsUnchanged()
    {
        var model = Model(multiInstance: true);
        model.FlowNodes[1].AsyncBefore = true;
        model.FlowNodes.Add(new FlowNodeModel { Id = 5, Name = "Async source", Type = BpmnFlowNodeTypes.Task, AsyncAfter = true });
        model.SequenceFlows[0].TargetRef = 5;
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 2 });
        var workflow = await CreateAsync(model);
        var instance = await StartAsync(workflow.Id);
        await ProcessJobAsync(instance.Id, WorkflowJobKinds.AsyncAfter);
        var pendingRevision = await RevisionAsync(instance.Id);
        Assert.DoesNotContain(await EvidenceAsync(instance.Id), row => row.NodeId == 2 || row.EnteredViaFlowId == 501 || row.ExitedViaFlowId == 501);
        await ProcessJobAsync(instance.Id, WorkflowJobKinds.AsyncBefore, verifyReplay: true);
        var children = (await EvidenceAsync(instance.Id)).Where(row => row.NodeId == 2).ToArray();
        Assert.Equal(2, children.Length);
        Assert.All(children, row => Assert.Equal(501, row.EnteredViaFlowId));
        Assert.Single(children.Select(row => row.MultiInstanceExecutionId).Distinct());
        // The node and traversal appear for the first time at fan-out, so this
        // commit must invalidate a summary obtained during the async-before wait.
        Assert.True(await RevisionAsync(instance.Id) > pendingRevision);
        await CancelAsync(instance.Id);
    }

    [Fact]
    public async Task AsyncBeforeCancellationRetainsOrdinarySourceExitWithoutChildVisits()
    {
        var model = Model(multiInstance: true);
        model.FlowNodes[1].AsyncBefore = true;
        var workflow = await CreateAsync(model);
        var instance = await StartAsync(workflow.Id);
        var before = await EvidenceAsync(instance.Id);
        Assert.Single(before, row => row.NodeId == 1 && row.ExitedViaFlowId == 101);
        Assert.DoesNotContain(before, row => row.NodeId == 2);
        await CancelAsync(instance.Id);
        var after = await EvidenceAsync(instance.Id);
        Assert.Equal(before.Select(row => row.Id), after.Select(row => row.Id));
        Assert.Single(after, row => row.ExitedViaFlowId == 101);
        Assert.True(await RevisionAsync(instance.Id) > instance.UpdatedAt);
    }

    [Theory]
    [InlineData("atomic")]
    [InlineData("durableAsync")]
    public async Task InitiallyTrueInterruptingBoundaryEndsMiEntryBeforeFanOut(string deliveryMode)
    {
        var model = Model(multiInstance: true);
        model.Variables.Add(new VariableModel { Id = 2, Name = "stop", DataType = "boolean", DefaultValue = JsonSerializer.SerializeToElement(true) });
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 4, Name = "Interrupt", Type = BpmnFlowNodeTypes.ConditionalBoundaryEvent,
            AttachedToRef = 2, CancelActivity = true,
            Conditional = new ConditionalDefinitionModel { Condition = "stop == true", DeliveryMode = deliveryMode }
        });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 3 });
        var workflow = await CreateAsync(model);
        var instance = await StartAsync(workflow.Id);
        var rows = await EvidenceAsync(instance.Id);
        Assert.Single(rows, row => row.NodeId == 1 && row.ExitedViaFlowId == 101);
        Assert.DoesNotContain(rows, row => row.NodeId == 2);
        if (deliveryMode == "durableAsync")
        {
            await ProcessJobAsync(instance.Id, WorkflowJobKinds.ConditionalWake);
            rows = await EvidenceAsync(instance.Id);
        }
        Assert.Single(rows, row => row.NodeId == 4 && row.EnteredViaFlowId == null && row.ExitedViaFlowId == 401);
        Assert.Single(rows, row => row.NodeId == 3 && row.EnteredViaFlowId == 401);
        await AssertRevisionCoversEvidenceAsync(instance.Id);
    }

    [Fact]
    public async Task NonInterruptingConditionalBranchCanTerminateMiBeforeFanOut()
    {
        var model = Model(multiInstance: true);
        model.FlowNodes.Add(new FlowNodeModel { Id = 5, Name = "Async source", Type = BpmnFlowNodeTypes.Task, AsyncAfter = true });
        model.SequenceFlows[0].TargetRef = 5;
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 2 });
        model.Variables.Add(new VariableModel { Id = 2, Name = "stop", DataType = "boolean", DefaultValue = JsonSerializer.SerializeToElement(true) });
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 4, Name = "Non-interrupting branch", Type = BpmnFlowNodeTypes.ConditionalBoundaryEvent,
            AttachedToRef = 2, CancelActivity = false,
            Conditional = new ConditionalDefinitionModel { Condition = "stop == true", DeliveryMode = "atomic" }
        });
        model.FlowNodes.Add(new FlowNodeModel { Id = 6, Name = "Terminate all", Type = BpmnFlowNodeTypes.TerminateEndEvent });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 6 });
        var workflow = await CreateAsync(model);
        var instance = await StartAsync(workflow.Id);
        await ProcessJobAsync(instance.Id, WorkflowJobKinds.AsyncAfter);
        var rows = await EvidenceAsync(instance.Id);
        Assert.DoesNotContain(rows, row => row.NodeId == 2 || row.EnteredViaFlowId == 501 || row.ExitedViaFlowId == 501);
        Assert.Single(rows, row => row.NodeId == 6 && row.CompletionReason == NodeExecutionCompletionReasons.TerminateEnd);
        await AssertRevisionCoversEvidenceAsync(instance.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AsyncAfterSourceUsesTargetEvidenceAndHasKnownGapBeforeMiFanOut(bool asyncBeforeTarget)
    {
        var model = Model(multiInstance: true);
        model.FlowNodes.Add(new FlowNodeModel { Id = 5, Name = "Async source", Type = BpmnFlowNodeTypes.Task, AsyncAfter = true });
        model.SequenceFlows[0].TargetRef = 5;
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 2 });
        model.FlowNodes[1].AsyncBefore = asyncBeforeTarget;
        var workflow = await CreateAsync(model);
        var instance = await StartAsync(workflow.Id);
        var before = await EvidenceAsync(instance.Id);
        Assert.Null(Assert.Single(before, row => row.NodeId == 5).ExitedViaFlowId);
        await ProcessJobAsync(instance.Id, WorkflowJobKinds.AsyncAfter);
        var rows = await EvidenceAsync(instance.Id);
        Assert.DoesNotContain(rows, row => row.ExitedViaFlowId == 501);
        var children = rows.Where(row => row.NodeId == 2).ToArray();
        if (asyncBeforeTarget)
        {
            Assert.Empty(children);
            await CancelAsync(instance.Id);
            Assert.DoesNotContain(await EvidenceAsync(instance.Id), row => row.EnteredViaFlowId == 501 || row.ExitedViaFlowId == 501);
        }
        else
        {
            Assert.Equal(2, children.Length);
            Assert.All(children, row => Assert.Equal(501, row.EnteredViaFlowId));
            Assert.Single(children.Select(row => row.MultiInstanceExecutionId).Distinct());
            await CancelAsync(instance.Id);
        }
        Assert.True(await RevisionAsync(instance.Id) > instance.UpdatedAt);
        await AssertRevisionCoversEvidenceAsync(instance.Id);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MiSourceUsesOneDistinctTargetActivationAndHasKnownGapBeforeFanOut(bool asyncBeforeTarget)
    {
        var model = Model(multiInstance: true);
        model.Variables.Add(new VariableModel { Id = 2, Name = "nextResults", DataType = "json", DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>()) });
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 5, Name = "Next MI", Type = BpmnFlowNodeTypes.UserTask, AsyncBefore = asyncBeforeTarget,
            MultiInstance = new MultiInstanceModel { Mode = "sequential", Source = "cardinality", CardinalityExpression = "2", ResultVariable = "nextResults", CompletionEvaluation = "afterAll" }
        });
        model.SequenceFlows[1].TargetRef = 5;
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 3, Name = "Complete next", CompletionCondition = "CountFlow(501) >= 2", CompletionPriority = 1 });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 502, SourceRef = 5, TargetRef = 3, IsDefault = true, IsSelectable = false });
        var workflow = await CreateAsync(model);
        var instance = await StartAsync(workflow.Id);
        await TakeAsync(await ActiveTaskAsync(instance.Id, 2), 201);
        await TakeAsync(await ActiveTaskAsync(instance.Id, 2), 201);
        var rows = await EvidenceAsync(instance.Id);
        Assert.DoesNotContain(rows, row => row.ExitedViaFlowId == 201);
        var target = rows.Where(row => row.NodeId == 5).ToArray();
        Assert.Equal(asyncBeforeTarget ? 0 : 2, target.Length);
        if (!asyncBeforeTarget)
        {
            Assert.Single(target.Select(row => row.MultiInstanceExecutionId).Distinct());
            Assert.All(target, row => Assert.Equal(201, row.EnteredViaFlowId));
            Assert.Single(target, row => row.Status == NodeExecutionStatuses.Pending);
        }
        await CancelAsync(instance.Id);
        await AssertRevisionCoversEvidenceAsync(instance.Id);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task SwitchRebindsOnlyOpenVisitsAndKeepsPriorExitOnPriorVersion(bool multiInstance, bool completeOneChild)
    {
        var model = Model(multiInstance);
        var source = await CreateAsync(model);
        var instance = await StartAsync(source.Id);
        await using (var db = fixture.CreateDbContext())
        {
            Assert.True((await db.WorkflowInstances.SingleAsync(row => row.Id == instance.Id)).NodeActivityFromCreation);
            // Exercise both classified legacy and new-instance values through the switch.
            if (completeOneChild)
                await db.WorkflowInstances.Where(row => row.Id == instance.Id)
                    .ExecuteUpdateAsync(set => set.SetProperty(row => row.NodeActivityFromCreation, false));
        }
        if (completeOneChild)
            await TakeAsync(await ActiveTaskAsync(instance.Id, 2), 201);
        model.Name += " v2";
        var target = await CreateAsync(model);
        using var previewResponse = await SendAsync(HttpMethod.Post, $"/api/instances/{instance.Id}/version-change/preview", new PreviewInstanceVersionChangeRequest(target.Id));
        await ExpectAsync(previewResponse, HttpStatusCode.OK);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<InstanceVersionChangePreviewDto>(JsonOptions))!;
        Assert.True(preview.Compatible, JsonSerializer.Serialize(preview.Blockers));
        using var change = await SendAsync(HttpMethod.Post, $"/api/instances/{instance.Id}/version-change",
            new ChangeInstanceVersionRequest(target.Id, preview.ExpectedSourceWorkflowId, preview.ExpectedUpdatedAt, "Characterize diagram version evidence"));
        await ExpectAsync(change, HttpStatusCode.OK);
        var rows = await EvidenceAsync(instance.Id);
        Assert.Equal(source.Id, Assert.Single(rows, row => row.NodeId == 1 && row.ExitedViaFlowId == 101).WorkflowDefinitionId);
        Assert.All(rows.Where(row => row.Status is NodeExecutionStatuses.Active or NodeExecutionStatuses.Pending), row =>
        {
            Assert.Equal(target.Id, row.WorkflowDefinitionId);
            Assert.Equal(101, row.EnteredViaFlowId);
        });
        if (completeOneChild)
            Assert.Equal(source.Id, Assert.Single(rows, row => row.NodeId == 2 && row.Status == NodeExecutionStatuses.Completed).WorkflowDefinitionId);
        var remaining = multiInstance && !completeOneChild ? 2 : 1;
        for (var index = 0; index < remaining; index++)
            await TakeAsync(await ActiveTaskAsync(instance.Id, 2), 201);
        rows = await EvidenceAsync(instance.Id);
        var end = Assert.Single(rows, row => row.NodeId == 3);
        Assert.Equal(target.Id, end.WorkflowDefinitionId);
        Assert.Equal(201, end.EnteredViaFlowId);
        if (!multiInstance)
            Assert.Equal(target.Id, Assert.Single(rows, row => row.ExitedViaFlowId == 201).WorkflowDefinitionId);
        Assert.True(await RevisionAsync(instance.Id) > instance.UpdatedAt);
        await AssertRevisionCoversEvidenceAsync(instance.Id);
        await using var finalDb = fixture.CreateDbContext();
        var finalInstance = await finalDb.WorkflowInstances.SingleAsync(row => row.Id == instance.Id);
        Assert.Equal(!completeOneChild, finalInstance.NodeActivityFromCreation);
        Assert.Null(finalInstance.NodeActivityPrunedAt);
    }

    private async Task ProcessJobAsync(long instanceId, string kind, bool verifyReplay = false)
    {
        long jobId;
        await using (var db = fixture.CreateDbContext())
        {
            var job = await db.WorkflowJobs.SingleAsync(row => row.InstanceId == instanceId && row.Kind == kind);
            jobId = job.Id;
            // Only scheduling is accelerated; the real durable processor records the traversal.
            job.Priority = 1_000_000;
            job.DueAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>();
        var lease = Assert.Single(await jobs.LeaseRunnableAsync(new WorkflowJobLeaseRequest(
            $"diagram-evidence:{Guid.NewGuid():N}", 1, 1, 4, TimeSpan.FromMinutes(1)), CancellationToken.None));
        Assert.Equal(jobId, lease.Job.Id);
        var processor = scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>();
        await processor.ProcessAsync(lease, CancellationToken.None);
        if (verifyReplay)
        {
            var revision = await RevisionAsync(instanceId);
            var ids = (await EvidenceAsync(instanceId)).Select(row => row.Id).ToArray();
            await processor.ProcessAsync(lease, CancellationToken.None);
            Assert.Equal(revision, await RevisionAsync(instanceId));
            Assert.Equal(ids, (await EvidenceAsync(instanceId)).Select(row => row.Id));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReactivationPreservesClassifiedCoverage(bool fromCreation)
    {
        var workflow = await CreateAsync(Model(multiInstance: false));
        var instance = await StartAsync(workflow.Id);
        await CancelAsync(instance.Id);
        await using (var db = fixture.CreateDbContext())
            await db.WorkflowInstances.Where(row => row.Id == instance.Id)
                .ExecuteUpdateAsync(set => set.SetProperty(row => row.NodeActivityFromCreation, fromCreation));
        using var previewResponse = await SendAsync(HttpMethod.Get, $"/api/instances/{instance.Id}/reactivation");
        await ExpectAsync(previewResponse, HttpStatusCode.OK);
        var preview = (await previewResponse.Content.ReadFromJsonAsync<InstanceReactivationPreviewDto>(JsonOptions))!;
        Assert.True(preview.CanReactivate);
        using var response = await SendAsync(HttpMethod.Post, $"/api/instances/{instance.Id}/reactivation",
            new ReactivateInstanceRequest(2, preview.WorkflowId, preview.ExpectedUpdatedAt, "Preserve coverage across runs"));
        await ExpectAsync(response, HttpStatusCode.OK);
        await using var verify = fixture.CreateDbContext();
        var persisted = await verify.WorkflowInstances.SingleAsync(row => row.Id == instance.Id);
        Assert.Equal(fromCreation, persisted.NodeActivityFromCreation);
        Assert.Null(persisted.NodeActivityPrunedAt);
        await CancelAsync(instance.Id);
    }

    private async Task<long> ActiveTaskAsync(long instanceId, int nodeId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.UserTasks.Where(row => row.InstanceId == instanceId && row.NodeId == nodeId && row.Status == UserTaskStatuses.Active).OrderBy(row => row.Id).Select(row => row.Id).FirstAsync();
    }

    private async Task<NodeExecutionEntity[]> EvidenceAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.NodeExecutions.AsNoTracking().Where(row => row.InstanceId == instanceId).OrderBy(row => row.Id).ToArrayAsync();
    }

    private async Task<DateTimeOffset> RevisionAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowInstances.Where(row => row.Id == instanceId).Select(row => row.UpdatedAt).SingleAsync();
    }

    private async Task AssertRevisionCoversEvidenceAsync(long instanceId)
    {
        var revision = await RevisionAsync(instanceId);
        Assert.All(await EvidenceAsync(instanceId), row => Assert.True(revision >= row.UpdatedAt, $"Instance revision {revision:O} precedes execution {row.Id} revision {row.UpdatedAt:O}"));
    }

    private async Task TakeAsync(long taskId, int flowId)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/api/user-tasks/{taskId}/flows/{flowId}", new TakeFlowRequest(null));
        await ExpectAsync(response, HttpStatusCode.OK);
    }

    private async Task CancelAsync(long instanceId)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/api/instances/{instanceId}/cancel");
        await ExpectAsync(response, HttpStatusCode.NoContent);
    }

    private async Task<WorkflowDetailDto> CreateAsync(WorkflowModel model)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
        await ExpectAsync(response, HttpStatusCode.Created);
        return (await response.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions))!;
    }

    private async Task<InstanceDetailDto> StartAsync(long workflowId)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/instances?detail=full", new StartInstanceRequest(workflowId, null, null, null));
        await ExpectAsync(response, HttpStatusCode.Created);
        var instance = (await response.Content.ReadFromJsonAsync<InstanceDetailDto>(JsonOptions))!;
        ownedInstances.Add(instance.Id);
        return instance;
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        ApiTestAuth.Authorize(request, "diagram-evidence", ["admin"]);
        return fixture.Client.SendAsync(request);
    }

    private static async Task ExpectAsync(HttpResponseMessage response, HttpStatusCode expected) =>
        Assert.True(response.StatusCode == expected, $"Expected {expected}, got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

    private static WorkflowModel Model(bool multiInstance, string mode = "sequential")
    {
        var model = new WorkflowModel
        {
            Id = $"diagram-mi-evidence-{Guid.NewGuid():N}", Name = "Diagram MI evidence", InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3, Name = "Complete" }
            ]
        };
        if (!multiInstance) return model;
        model.Variables.Add(new VariableModel { Id = 1, Name = "results", DataType = "json", DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>()) });
        model.FlowNodes[1].MultiInstance = new MultiInstanceModel
        {
            Mode = mode, Source = "cardinality", CardinalityExpression = "2", ResultVariable = "results", CompletionEvaluation = "afterAll"
        };
        model.SequenceFlows[1].CompletionCondition = "CountFlow(201) >= 2";
        model.SequenceFlows[1].CompletionPriority = 1;
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 3, IsDefault = true, IsSelectable = false });
        return model;
    }
}
