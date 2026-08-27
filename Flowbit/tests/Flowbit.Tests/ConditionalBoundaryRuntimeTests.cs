using System.Net;
using System.Net.Http.Json;
using System.Diagnostics.Metrics;
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
public sealed class ConditionalBoundaryRuntimeTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task Initial_true_interrupting_boundary_captures_before_host_waits()
    {
        var workflowKey = $"conditional-boundary-initial-{Guid.NewGuid():N}";
        try
        {
            var workflowId = await CreateWorkflowAsync(CreateWorkflow(
                workflowKey,
                ConditionalEventDeliveryModes.Atomic,
                cancelActivity: true,
                approvedDefault: true));

            var started = await StartAsync(workflowId, approved: true);

            Assert.Equal(WorkflowInstanceStatuses.Completed, started.Status);
            Assert.Equal(5, started.CurrentNodeId);
            await using var db = fixture.CreateDbContext();
            var subscription = await db.ConditionalBoundarySubscriptions.SingleAsync(item =>
                item.InstanceId == started.Id);
            Assert.Equal(ConditionalBoundarySubscriptionStatuses.Completed, subscription.Status);
            Assert.True(subscription.IsConditionTrue);
            Assert.Equal(1, subscription.Occurrence);
            Assert.Single(await db.UserTasks.Where(task =>
                task.InstanceId == started.Id
                && task.Status == UserTaskStatuses.Cancelled).ToListAsync());
            Assert.Single(await db.InstanceHistory.Where(item =>
                item.InstanceId == started.Id
                && item.Note == InstanceHistoryNotes.ConditionalTriggered).ToListAsync());
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task Non_interrupting_atomic_boundary_rearms_only_after_false()
    {
        var workflowKey = $"conditional-boundary-rearm-{Guid.NewGuid():N}";
        try
        {
            var workflowId = await CreateWorkflowAsync(CreateWorkflow(
                workflowKey,
                ConditionalEventDeliveryModes.Atomic,
                cancelActivity: false));
            var started = await StartAsync(workflowId, approved: false);

            _ = await PatchVariableAsync(started.Id, "approved", true);
            _ = await PatchVariableAsync(started.Id, "approved", true);
            await AssertOccurrenceAsync(started.Id, 1, conditionTrue: true);

            _ = await PatchVariableAsync(started.Id, "approved", false);
            await AssertOccurrenceAsync(started.Id, 1, conditionTrue: false);
            _ = await PatchVariableAsync(started.Id, "approved", true);
            await AssertOccurrenceAsync(started.Id, 2, conditionTrue: true);

            await using var db = fixture.CreateDbContext();
            Assert.Equal(WorkflowInstanceStatuses.Running,
                (await db.WorkflowInstances.SingleAsync(item => item.Id == started.Id)).Status);
            Assert.Equal(2, await db.InstanceHistory.CountAsync(item =>
                item.InstanceId == started.Id
                && item.Note == InstanceHistoryNotes.ConditionalTriggered));
            Assert.Single(await db.UserTasks.Where(task =>
                task.InstanceId == started.Id
                && task.Status == UserTaskStatuses.Active).ToListAsync());
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task Durable_non_interrupting_boundary_captures_multiple_edges_while_jobs_are_queued()
    {
        var workflowKey = $"conditional-boundary-durable-edges-{Guid.NewGuid():N}";
        try
        {
            var workflowId = await CreateWorkflowAsync(CreateWorkflow(
                workflowKey,
                ConditionalEventDeliveryModes.DurableAsync,
                cancelActivity: false));
            var started = await StartAsync(workflowId, approved: false);

            _ = await PatchVariableAsync(started.Id, "approved", true);
            _ = await PatchVariableAsync(started.Id, "approved", false);
            _ = await PatchVariableAsync(started.Id, "approved", true);

            await using var db = fixture.CreateDbContext();
            var subscription = await db.ConditionalBoundarySubscriptions.SingleAsync(item =>
                item.InstanceId == started.Id);
            Assert.Equal(2, subscription.Occurrence);
            Assert.True(subscription.IsConditionTrue);
            var jobs = await db.WorkflowJobs.Where(job =>
                    job.InstanceId == started.Id
                    && job.Kind == WorkflowJobKinds.ConditionalWake)
                .OrderBy(job => job.ConditionalBoundaryOccurrence)
                .ToListAsync();
            Assert.Equal(2, jobs.Count);
            Assert.Equal(new long?[] { 1, 2 }, jobs.Select(job =>
                job.ConditionalBoundaryOccurrence).ToArray());
            Assert.All(jobs, job =>
                Assert.Equal(subscription.Id, job.ConditionalBoundarySubscriptionId));
            Assert.Equal(2, await db.ExecutionTokens.CountAsync(token =>
                token.InstanceId == started.Id
                && token.NodeId == 4
                && token.WaitState == ExecutionTokenWaitStates.ConditionalWake));
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task Duplicate_durable_worker_delivery_cannot_repeat_boundary_effects()
    {
        var workflowKey = $"conditional-boundary-worker-fence-{Guid.NewGuid():N}";
        try
        {
            var workflowId = await CreateWorkflowAsync(CreateWorkflow(
                workflowKey,
                ConditionalEventDeliveryModes.DurableAsync,
                cancelActivity: false));
            var started = await StartAsync(workflowId, approved: false);
            _ = await PatchVariableAsync(started.Id, "approved", true);

            long jobId;
            await using (var before = fixture.CreateDbContext())
            {
                jobId = (await before.WorkflowJobs.SingleAsync(job =>
                    job.InstanceId == started.Id
                    && job.Kind == WorkflowJobKinds.ConditionalWake)).Id;
            }
            await PromoteJobAsync(jobId);

            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var repository = scope.ServiceProvider
                    .GetRequiredService<IWorkflowJobRepository>();
                var lease = Assert.Single(await repository.LeaseRunnableAsync(
                    new WorkflowJobLeaseRequest(
                        $"conditional-boundary-duplicate:{Guid.NewGuid():N}",
                        MaxCount: 1,
                        MaxActivityCount: 0,
                        MaxPerInstance: 4,
                        LeaseDuration: TimeSpan.FromMinutes(1)),
                    CancellationToken.None));
                Assert.Equal(jobId, lease.Job.Id);
                var processor = scope.ServiceProvider
                    .GetRequiredService<IWorkflowJobProcessor>();
                await processor.ProcessAsync(lease, CancellationToken.None);
                await processor.ProcessAsync(lease, CancellationToken.None);
            }

            await using var after = fixture.CreateDbContext();
            Assert.Equal(1, await after.InstanceHistory.CountAsync(item =>
                item.InstanceId == started.Id
                && item.Note == InstanceHistoryNotes.ConditionalTriggered));
            Assert.Equal(1, await after.ExecutionTokens.CountAsync(token =>
                token.InstanceId == started.Id
                && token.NodeId == 5
                && token.Status == ExecutionTokenStatuses.Completed));
            Assert.Equal(
                WorkflowJobStatuses.Completed,
                (await after.WorkflowJobs.SingleAsync(job => job.Id == jobId)).Status);
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task Version_change_blocks_changed_boundary_contract_and_rebinds_exact_durable_capture()
    {
        var workflowKey = $"conditional-boundary-version-{Guid.NewGuid():N}";
        try
        {
            var sourceModel = CreateWorkflow(
                workflowKey,
                ConditionalEventDeliveryModes.DurableAsync,
                cancelActivity: true);
            var sourceId = await CreateWorkflowAsync(sourceModel);

            var incompatible = Clone(sourceModel);
            incompatible.Name += " incompatible";
            incompatible.FlowNodes.Single(node => node.Id == 4)
                .Conditional!.Condition = "approved == false";
            var incompatibleId = await CreateWorkflowAsync(incompatible);

            var compatible = Clone(sourceModel);
            compatible.Name += " compatible";
            compatible.FlowNodes.Single(node => node.Id == 4).Name =
                "Approved boundary v3";
            var compatibleId = await CreateWorkflowAsync(compatible);

            var started = await StartAsync(sourceId, approved: false);
            _ = await PatchVariableAsync(started.Id, "approved", true);

            using (var incompatiblePreviewResponse = await SendAsync(
                       HttpMethod.Post,
                       $"/api/instances/{started.Id}/version-change/preview",
                       new PreviewInstanceVersionChangeRequest(incompatibleId)))
            {
                Assert.Equal(HttpStatusCode.OK, incompatiblePreviewResponse.StatusCode);
                var preview = await ReadAsync<InstanceVersionChangePreviewDto>(
                    incompatiblePreviewResponse);
                Assert.False(preview.Compatible);
                Assert.Contains(preview.Blockers, blocker =>
                    blocker.Code.Contains("conditional_boundary", StringComparison.OrdinalIgnoreCase));
            }

            InstanceVersionChangePreviewDto compatiblePreview;
            using (var compatiblePreviewResponse = await SendAsync(
                       HttpMethod.Post,
                       $"/api/instances/{started.Id}/version-change/preview",
                       new PreviewInstanceVersionChangeRequest(compatibleId)))
            {
                Assert.Equal(HttpStatusCode.OK, compatiblePreviewResponse.StatusCode);
                compatiblePreview = await ReadAsync<InstanceVersionChangePreviewDto>(
                    compatiblePreviewResponse);
                Assert.True(compatiblePreview.Compatible);
            }
            using (var changeResponse = await SendAsync(
                       HttpMethod.Post,
                       $"/api/instances/{started.Id}/version-change",
                       new ChangeInstanceVersionRequest(
                           compatibleId,
                           compatiblePreview.ExpectedSourceWorkflowId,
                           compatiblePreview.ExpectedUpdatedAt,
                           "verify conditional boundary rebind")))
            {
                Assert.Equal(HttpStatusCode.OK, changeResponse.StatusCode);
            }

            await using var after = fixture.CreateDbContext();
            var subscription = await after.ConditionalBoundarySubscriptions.SingleAsync(item =>
                item.InstanceId == started.Id);
            Assert.Equal(ConditionalBoundarySubscriptionStatuses.Completed, subscription.Status);
            Assert.Equal(compatibleId, subscription.WorkflowDefinitionId);
            Assert.Equal("Approved boundary v3", subscription.BoundaryNodeName);
            var job = await after.WorkflowJobs.SingleAsync(item =>
                item.InstanceId == started.Id
                && item.Kind == WorkflowJobKinds.ConditionalWake);
            Assert.Equal(compatibleId, job.WorkflowDefinitionId);
            Assert.Equal(subscription.Id, job.ConditionalBoundarySubscriptionId);
            Assert.Equal(1, job.ConditionalBoundaryOccurrence);
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task Interrupting_boundary_wins_over_successful_user_action_output()
    {
        var workflowKey = $"conditional-boundary-user-output-{Guid.NewGuid():N}";
        try
        {
            var workflow = CreateWorkflow(
                workflowKey,
                ConditionalEventDeliveryModes.Atomic,
                cancelActivity: true);
            workflow.SequenceFlows.Single(flow => flow.Id == 20).Variables =
            [
                new VariableModel
                {
                    Id = 20,
                    Name = "approved",
                    DataType = WorkflowVariableTypes.Boolean,
                    Required = true
                }
            ];
            var workflowId = await CreateWorkflowAsync(workflow);
            var started = await StartAsync(workflowId, approved: false);

            long taskId;
            await using (var before = fixture.CreateDbContext())
            {
                taskId = (await before.UserTasks.SingleAsync(task =>
                    task.InstanceId == started.Id
                    && task.Status == UserTaskStatuses.Active)).Id;
            }
            using var response = await SendAsync(
                HttpMethod.Post,
                $"/api/user-tasks/{taskId}/flows/20",
                new TakeFlowRequest(new Dictionary<string, JsonElement>
                {
                    ["approved"] = JsonSerializer.SerializeToElement(true)
                }));

            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            await using var after = fixture.CreateDbContext();
            var instance = await after.WorkflowInstances.SingleAsync(item =>
                item.Id == started.Id);
            Assert.Equal(WorkflowInstanceStatuses.Completed, instance.Status);
            Assert.DoesNotContain(await after.ExecutionTokens.Where(token =>
                    token.InstanceId == started.Id
                    && token.NodeId == 3)
                .ToListAsync(), token => token.Status == ExecutionTokenStatuses.Completed);
            Assert.Single(await after.ExecutionTokens.Where(token =>
                token.InstanceId == started.Id
                && token.NodeId == 5
                && token.Status == ExecutionTokenStatuses.Completed).ToListAsync());
            Assert.Equal(UserTaskStatuses.Cancelled,
                (await after.UserTasks.SingleAsync(task => task.Id == taskId)).Status);
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task Interrupting_boundary_wins_over_successful_async_script_output()
    {
        var workflowKey = $"conditional-boundary-script-output-{Guid.NewGuid():N}";
        try
        {
            var workflow = CreateWorkflow(
                workflowKey,
                ConditionalEventDeliveryModes.Atomic,
                cancelActivity: true);
            var host = workflow.FlowNodes.Single(node => node.Id == 2);
            host.Type = BpmnFlowNodeTypes.ScriptTask;
            host.AsyncBefore = true;
            host.ScriptFormat = ScriptFormats.NCalc;
            host.Assignments =
            [
                new AssignmentModel
                {
                    Variable = "approved",
                    Expression = "true"
                }
            ];
            var workflowId = await CreateWorkflowAsync(workflow);
            var started = await StartAsync(workflowId, approved: false);

            long jobId;
            await using (var before = fixture.CreateDbContext())
            {
                jobId = (await before.WorkflowJobs.SingleAsync(job =>
                    job.InstanceId == started.Id
                    && job.Kind == WorkflowJobKinds.AsyncBefore)).Id;
            }
            await PromoteAndProcessAsync(jobId, WorkflowJobClasses.Activity);

            await using var after = fixture.CreateDbContext();
            Assert.Equal(
                WorkflowInstanceStatuses.Completed,
                (await after.WorkflowInstances.SingleAsync(item => item.Id == started.Id)).Status);
            Assert.DoesNotContain(await after.ExecutionTokens.Where(token =>
                    token.InstanceId == started.Id
                    && token.NodeId == 3)
                .ToListAsync(), token => token.Status == ExecutionTokenStatuses.Completed);
            Assert.Single(await after.ExecutionTokens.Where(token =>
                token.InstanceId == started.Id
                && token.NodeId == 5
                && token.Status == ExecutionTokenStatuses.Completed).ToListAsync());
            Assert.Equal(
                ConditionalBoundarySubscriptionStatuses.Completed,
                (await after.ConditionalBoundarySubscriptions.SingleAsync(item =>
                    item.InstanceId == started.Id)).Status);
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task Interrupting_boundary_wins_over_successful_async_service_output()
    {
        var workflowKey = $"conditional-boundary-service-output-{Guid.NewGuid():N}";
        fixture.ServiceInvocations.Reset();
        try
        {
            var workflow = CreateWorkflow(
                workflowKey,
                ConditionalEventDeliveryModes.Atomic,
                cancelActivity: true);
            var host = workflow.FlowNodes.Single(node => node.Id == 2);
            host.Type = BpmnFlowNodeTypes.ServiceTask;
            host.AsyncBefore = true;
            host.Service = new ServiceTaskModel
            {
                Url = "https://tests.local/typed-output-success",
                Method = "GET",
                OutputMappings =
                [
                    new ServiceOutputMappingModel
                    {
                        Variable = "approved",
                        Path = "approved",
                        Required = true,
                        DataType = WorkflowVariableTypes.Boolean
                    }
                ]
            };
            var workflowId = await CreateWorkflowAsync(workflow);
            var started = await StartAsync(workflowId, approved: false);

            long jobId;
            await using (var before = fixture.CreateDbContext())
            {
                jobId = (await before.WorkflowJobs.SingleAsync(job =>
                    job.InstanceId == started.Id
                    && job.Kind == WorkflowJobKinds.AsyncBefore)).Id;
            }
            await PromoteAndProcessAsync(jobId, WorkflowJobClasses.Activity);

            await using var after = fixture.CreateDbContext();
            Assert.Equal(
                WorkflowInstanceStatuses.Completed,
                (await after.WorkflowInstances.SingleAsync(item => item.Id == started.Id)).Status);
            Assert.DoesNotContain(await after.ExecutionTokens.Where(token =>
                    token.InstanceId == started.Id
                    && token.NodeId == 3)
                .ToListAsync(), token => token.Status == ExecutionTokenStatuses.Completed);
            Assert.Single(await after.ExecutionTokens.Where(token =>
                token.InstanceId == started.Id
                && token.NodeId == 5
                && token.Status == ExecutionTokenStatuses.Completed).ToListAsync());
            Assert.Equal(
                ConditionalBoundarySubscriptionStatuses.Completed,
                (await after.ConditionalBoundarySubscriptions.SingleAsync(item =>
                    item.InstanceId == started.Id)).Status);
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task Interrupting_boundary_wins_over_successful_message_output()
    {
        var workflowKey = $"conditional-boundary-message-output-{Guid.NewGuid():N}";
        try
        {
            var workflow = CreateWorkflow(
                workflowKey,
                ConditionalEventDeliveryModes.Atomic,
                cancelActivity: true);
            var host = workflow.FlowNodes.Single(node => node.Id == 2);
            host.Type = BpmnFlowNodeTypes.IntermediateMessageCatchEvent;
            host.Message = new MessageCatchModel
            {
                ClientId = "tests-client",
                ClientSecret = "tests-secret",
                HeaderName = "X-Correlation",
                HeaderValue = "accepted",
                OutputMappings =
                [
                    new MessageOutputMappingModel
                    {
                        Variable = "approved",
                        Path = "approved",
                        Required = true,
                        DataType = WorkflowVariableTypes.Boolean
                    }
                ]
            };
            var workflowId = await CreateWorkflowAsync(workflow);
            var started = await StartAsync(workflowId, approved: false);

            using var request = new HttpRequestMessage(
                HttpMethod.Post,
                $"/api/instances/{started.Id}/message")
            {
                Content = JsonContent.Create(new { approved = true }, options: JsonOptions)
            };
            request.Headers.Add("X-Client-Id", "tests-client");
            request.Headers.Add("X-Client-Secret", "tests-secret");
            request.Headers.Add("X-Correlation", "accepted");
            using var response = await fixture.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            await using var after = fixture.CreateDbContext();
            Assert.Equal(
                WorkflowInstanceStatuses.Completed,
                (await after.WorkflowInstances.SingleAsync(item => item.Id == started.Id)).Status);
            Assert.DoesNotContain(await after.ExecutionTokens.Where(token =>
                    token.InstanceId == started.Id
                    && token.NodeId == 3)
                .ToListAsync(), token => token.Status == ExecutionTokenStatuses.Completed);
            Assert.Single(await after.ExecutionTokens.Where(token =>
                token.InstanceId == started.Id
                && token.NodeId == 5
                && token.Status == ExecutionTokenStatuses.Completed).ToListAsync());
            Assert.Equal(
                ConditionalBoundarySubscriptionStatuses.Completed,
                (await after.ConditionalBoundarySubscriptions.SingleAsync(item =>
                    item.InstanceId == started.Id)).Status);
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task Interrupting_boundary_cancels_active_multi_instance_parent_and_children()
    {
        var workflowKey = $"conditional-boundary-multi-instance-{Guid.NewGuid():N}";
        try
        {
            var workflow = CreateWorkflow(
                workflowKey,
                ConditionalEventDeliveryModes.Atomic,
                cancelActivity: true);
            workflow.Variables.Add(new VariableModel
            {
                Id = 3,
                Name = "results",
                DataType = WorkflowVariableTypes.Json,
                Required = true,
                DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>())
            });
            var host = workflow.FlowNodes.Single(node => node.Id == 2);
            host.MultiInstance = new MultiInstanceModel
            {
                Mode = MultiInstanceModes.Parallel,
                Source = MultiInstanceSources.Cardinality,
                CardinalityExpression = "2",
                CompletionEvaluation = MultiInstanceCompletionEvaluations.AfterAll,
                ResultVariable = "results"
            };
            var outcome = workflow.SequenceFlows.Single(flow => flow.Id == 20);
            outcome.CompletionCondition = "CountFlow(20) >= 2";
            outcome.CompletionPriority = 1;
            workflow.SequenceFlows.Add(new SequenceFlowModel
            {
                Id = 21,
                Name = "Default",
                SourceRef = 2,
                TargetRef = 3,
                IsDefault = true,
                IsSelectable = false
            });
            var workflowId = await CreateWorkflowAsync(workflow);
            var started = await StartAsync(workflowId, approved: false);

            await using (var before = fixture.CreateDbContext())
            {
                Assert.Equal(2, await before.UserTasks.CountAsync(task =>
                    task.InstanceId == started.Id
                    && task.Status == UserTaskStatuses.Active));
                Assert.Equal(
                    MultiInstanceExecutionStatuses.Active,
                    (await before.MultiInstanceExecutions.SingleAsync(execution =>
                        execution.InstanceId == started.Id)).Status);
            }

            _ = await PatchVariableAsync(started.Id, "approved", true);

            await using var after = fixture.CreateDbContext();
            Assert.Equal(
                WorkflowInstanceStatuses.Completed,
                (await after.WorkflowInstances.SingleAsync(item => item.Id == started.Id)).Status);
            Assert.All(await after.UserTasks.Where(task => task.InstanceId == started.Id).ToListAsync(),
                task => Assert.Equal(UserTaskStatuses.Cancelled, task.Status));
            Assert.Equal(
                MultiInstanceExecutionStatuses.Cancelled,
                (await after.MultiInstanceExecutions.SingleAsync(execution =>
                    execution.InstanceId == started.Id)).Status);
            Assert.Single(await after.ExecutionTokens.Where(token =>
                token.InstanceId == started.Id
                && token.NodeId == 5
                && token.Status == ExecutionTokenStatuses.Completed).ToListAsync());
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task Concurrent_true_writers_capture_one_non_interrupting_edge()
    {
        var workflowKey = $"conditional-boundary-writer-race-{Guid.NewGuid():N}";
        try
        {
            var workflowId = await CreateWorkflowAsync(CreateWorkflow(
                workflowKey,
                ConditionalEventDeliveryModes.Atomic,
                cancelActivity: false));
            var started = await StartAsync(workflowId, approved: false);

            var writes = await Task.WhenAll(
                PatchVariableResponseAsync(started.Id, "approved", true),
                PatchVariableResponseAsync(started.Id, "approved", true));
            try
            {
                Assert.All(writes, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
            }
            finally
            {
                foreach (var response in writes) response.Dispose();
            }

            await AssertOccurrenceAsync(started.Id, 1, conditionTrue: true);
            await using var db = fixture.CreateDbContext();
            Assert.Equal(1, await db.InstanceHistory.CountAsync(item =>
                item.InstanceId == started.Id
                && item.Note == InstanceHistoryNotes.ConditionalTriggered));
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task Host_completion_and_interrupting_capture_are_serialized_by_instance_lock()
    {
        var workflowKey = $"conditional-boundary-completion-race-{Guid.NewGuid():N}";
        try
        {
            var workflowId = await CreateWorkflowAsync(CreateWorkflow(
                workflowKey,
                ConditionalEventDeliveryModes.Atomic,
                cancelActivity: true));
            var started = await StartAsync(workflowId, approved: false);
            long taskId;
            await using (var before = fixture.CreateDbContext())
            {
                taskId = (await before.UserTasks.SingleAsync(task =>
                    task.InstanceId == started.Id
                    && task.Status == UserTaskStatuses.Active)).Id;
            }

            var operations = await Task.WhenAll(
                PatchVariableResponseAsync(started.Id, "approved", true),
                SendAsync(
                    HttpMethod.Post,
                    $"/api/user-tasks/{taskId}/flows/20",
                    new TakeFlowRequest(null)));
            try
            {
                Assert.Single(operations, response => response.IsSuccessStatusCode);
                Assert.Single(operations, response => !response.IsSuccessStatusCode);
            }
            finally
            {
                foreach (var response in operations) response.Dispose();
            }

            await using var after = fixture.CreateDbContext();
            Assert.Equal(WorkflowInstanceStatuses.Completed,
                (await after.WorkflowInstances.SingleAsync(item => item.Id == started.Id)).Status);
            var terminalNodeIds = await after.ExecutionTokens
                .Where(token => token.InstanceId == started.Id
                    && token.Status == ExecutionTokenStatuses.Completed)
                .Select(token => token.NodeId)
                .ToListAsync();
            Assert.Single(terminalNodeIds);
            Assert.Contains(terminalNodeIds[0], new[] { 3, 5 });
            var subscription = await after.ConditionalBoundarySubscriptions.SingleAsync(item =>
                item.InstanceId == started.Id);
            Assert.Contains(
                subscription.Status,
                new[]
                {
                    ConditionalBoundarySubscriptionStatuses.Cancelled,
                    ConditionalBoundarySubscriptionStatuses.Completed
                });
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task Simultaneous_matches_capture_non_interrupting_boundaries_then_lowest_interrupt()
    {
        var workflowKey = $"conditional-boundary-simultaneous-{Guid.NewGuid():N}";
        try
        {
            var workflow = CreateWorkflow(
                workflowKey,
                ConditionalEventDeliveryModes.Atomic,
                cancelActivity: false);
            AddBoundary(workflow, 6, 7, cancelActivity: false);
            AddBoundary(workflow, 8, 9, cancelActivity: true);
            AddBoundary(workflow, 10, 11, cancelActivity: true);
            var workflowId = await CreateWorkflowAsync(workflow);
            var started = await StartAsync(workflowId, approved: false);

            _ = await PatchVariableAsync(started.Id, "approved", true);

            await using var db = fixture.CreateDbContext();
            var subscriptions = await db.ConditionalBoundarySubscriptions
                .Where(item => item.InstanceId == started.Id)
                .OrderBy(item => item.BoundaryNodeId)
                .ToListAsync();
            Assert.Equal(new[] { 4, 6, 8, 10 }, subscriptions.Select(item =>
                item.BoundaryNodeId).ToArray());
            Assert.Equal(new long[] { 1, 1, 1, 0 }, subscriptions.Select(item =>
                item.Occurrence).ToArray());
            Assert.Equal(
                ConditionalBoundarySubscriptionStatuses.Completed,
                subscriptions.Single(item => item.BoundaryNodeId == 8).Status);
            Assert.Equal(
                ConditionalBoundarySubscriptionStatuses.Cancelled,
                subscriptions.Single(item => item.BoundaryNodeId == 10).Status);
            var triggeredBoundaries = await db.InstanceHistory
                .Where(item => item.InstanceId == started.Id
                    && item.Note == InstanceHistoryNotes.ConditionalTriggered)
                .Select(item => item.FromStepId)
                .OrderBy(id => id)
                .ToListAsync();
            Assert.Equal(new[] { 4, 6, 8 }, triggeredBoundaries);
            Assert.DoesNotContain(10, triggeredBoundaries);
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task Unrelated_variable_write_does_not_query_conditional_subscriptions()
    {
        var workflowKey = $"conditional-boundary-query-budget-{Guid.NewGuid():N}";
        try
        {
            var workflowId = await CreateWorkflowAsync(CreateWorkflow(
                workflowKey,
                ConditionalEventDeliveryModes.DurableAsync,
                cancelActivity: false));
            var started = await StartAsync(workflowId, approved: false);

            fixture.CommandCounter.Reset(captureReaderCommandTexts: true);
            _ = await PatchVariableAsync(started.Id, "noise", true);

            Assert.DoesNotContain(
                fixture.CommandCounter.ReaderCommandTexts,
                command => command.Contains(
                    "conditional_boundary_subscriptions",
                    StringComparison.OrdinalIgnoreCase));
            await AssertOccurrenceAsync(started.Id, 0, conditionTrue: false);
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task Instance_cancellation_cancels_subscription_captured_token_and_durable_job()
    {
        var workflowKey = $"conditional-boundary-instance-cancel-{Guid.NewGuid():N}";
        try
        {
            var workflowId = await CreateWorkflowAsync(CreateWorkflow(
                workflowKey,
                ConditionalEventDeliveryModes.DurableAsync,
                cancelActivity: false));
            var started = await StartAsync(workflowId, approved: false);
            _ = await PatchVariableAsync(started.Id, "approved", true);

            using (var response = await SendAsync(
                       HttpMethod.Post,
                       $"/api/instances/{started.Id}/cancel",
                       user: "operator"))
            {
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            }

            await using var db = fixture.CreateDbContext();
            Assert.Equal(
                WorkflowInstanceStatuses.Cancelled,
                (await db.WorkflowInstances.SingleAsync(item => item.Id == started.Id)).Status);
            Assert.Equal(
                ConditionalBoundarySubscriptionStatuses.Cancelled,
                (await db.ConditionalBoundarySubscriptions.SingleAsync(item =>
                    item.InstanceId == started.Id)).Status);
            Assert.All(
                await db.WorkflowJobs.Where(job => job.InstanceId == started.Id).ToListAsync(),
                job => Assert.Equal(WorkflowJobStatuses.Cancelled, job.Status));
            Assert.DoesNotContain(
                await db.ExecutionTokens.Where(token => token.InstanceId == started.Id).ToListAsync(),
                token => token.Status == ExecutionTokenStatuses.Active);
            Assert.All(
                await db.UserTasks.Where(task => task.InstanceId == started.Id).ToListAsync(),
                task => Assert.Equal(UserTaskStatuses.Cancelled, task.Status));
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task Scoped_interruption_cancels_sibling_conditional_boundary_subscription()
    {
        var workflowKey = $"conditional-boundary-scoped-interrupt-{Guid.NewGuid():N}";
        try
        {
            var workflowId = await CreateWorkflowAsync(
                CreateParallelCleanupWorkflow(workflowKey, terminate: false));
            var started = await StartAsync(workflowId, approved: false);
            long triggerTaskId;
            long hostTaskId;
            await using (var before = fixture.CreateDbContext())
            {
                triggerTaskId = (await before.UserTasks.SingleAsync(task =>
                    task.InstanceId == started.Id && task.NodeId == 7)).Id;
                hostTaskId = (await before.UserTasks.SingleAsync(task =>
                    task.InstanceId == started.Id && task.NodeId == 2)).Id;
            }

            using (var response = await SendAsync(
                       HttpMethod.Post,
                       $"/api/user-tasks/{triggerTaskId}/flows/70",
                       new TakeFlowRequest(null)))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            await using var db = fixture.CreateDbContext();
            Assert.Equal(
                WorkflowInstanceStatuses.Completed,
                (await db.WorkflowInstances.SingleAsync(item => item.Id == started.Id)).Status);
            Assert.Equal(
                ConditionalBoundarySubscriptionStatuses.Cancelled,
                (await db.ConditionalBoundarySubscriptions.SingleAsync(item =>
                    item.InstanceId == started.Id)).Status);
            Assert.Equal(
                UserTaskStatuses.Cancelled,
                (await db.UserTasks.SingleAsync(task => task.Id == hostTaskId)).Status);
            Assert.Single(await db.InstanceHistory.Where(item =>
                item.InstanceId == started.Id && item.Note == "scopedInterrupt").ToListAsync());
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task Terminate_end_cancels_sibling_conditional_boundary_subscription()
    {
        var workflowKey = $"conditional-boundary-terminate-{Guid.NewGuid():N}";
        try
        {
            var workflowId = await CreateWorkflowAsync(
                CreateParallelCleanupWorkflow(workflowKey, terminate: true));
            var started = await StartAsync(workflowId, approved: false);
            long triggerTaskId;
            long hostTaskId;
            await using (var before = fixture.CreateDbContext())
            {
                triggerTaskId = (await before.UserTasks.SingleAsync(task =>
                    task.InstanceId == started.Id && task.NodeId == 7)).Id;
                hostTaskId = (await before.UserTasks.SingleAsync(task =>
                    task.InstanceId == started.Id && task.NodeId == 2)).Id;
            }

            using (var response = await SendAsync(
                       HttpMethod.Post,
                       $"/api/user-tasks/{triggerTaskId}/flows/70",
                       new TakeFlowRequest(null)))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            await using var db = fixture.CreateDbContext();
            Assert.Equal(
                WorkflowInstanceStatuses.Completed,
                (await db.WorkflowInstances.SingleAsync(item => item.Id == started.Id)).Status);
            Assert.Equal(
                ConditionalBoundarySubscriptionStatuses.Cancelled,
                (await db.ConditionalBoundarySubscriptions.SingleAsync(item =>
                    item.InstanceId == started.Id)).Status);
            Assert.Equal(
                UserTaskStatuses.Cancelled,
                (await db.UserTasks.SingleAsync(task => task.Id == hostTaskId)).Status);
            Assert.Contains(
                await db.ExecutionTokens.Where(token => token.InstanceId == started.Id).ToListAsync(),
                token => token.NodeId == 2
                    && token.Status == ExecutionTokenStatuses.Cancelled
                    && token.TerminationReason == ExecutionTokenTerminationReasons.TerminateEnd);
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task Conditional_and_timer_interruptions_have_one_serialized_winner()
    {
        var workflowKey = $"conditional-boundary-timer-race-{Guid.NewGuid():N}";
        try
        {
            var workflow = CreateWorkflow(
                workflowKey,
                ConditionalEventDeliveryModes.Atomic,
                cancelActivity: true);
            workflow.FlowNodes.AddRange(
            [
                new FlowNodeModel
                {
                    Id = 6,
                    Name = "Deadline",
                    Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                    AttachedToRef = 2,
                    CancelActivity = true,
                    Timer = new TimerDefinitionModel { TimeDuration = "PT1H" }
                },
                new FlowNodeModel
                {
                    Id = 7,
                    Name = "Deadline end",
                    Type = BpmnFlowNodeTypes.EndEvent
                }
            ]);
            workflow.SequenceFlows.Add(new SequenceFlowModel
            {
                Id = 60,
                SourceRef = 6,
                TargetRef = 7
            });
            var workflowId = await CreateWorkflowAsync(workflow);
            var started = await StartAsync(workflowId, approved: false);

            long timerJobId;
            await using (var before = fixture.CreateDbContext())
            {
                timerJobId = (await before.WorkflowJobs.SingleAsync(job =>
                    job.InstanceId == started.Id
                    && job.Kind == WorkflowJobKinds.TimerBoundary)).Id;
            }
            await PromoteJobAsync(timerJobId);

            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>();
            var lease = Assert.Single(await repository.LeaseRunnableAsync(
                new WorkflowJobLeaseRequest(
                    $"conditional-boundary-timer-race:{Guid.NewGuid():N}",
                    MaxCount: 1,
                    MaxActivityCount: 0,
                    MaxPerInstance: 4,
                    LeaseDuration: TimeSpan.FromMinutes(1)),
                CancellationToken.None));
            Assert.Equal(timerJobId, lease.Job.Id);
            var processor = scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>();
            var process = processor.ProcessAsync(lease, CancellationToken.None);
            var patch = PatchVariableResponseAsync(started.Id, "approved", true);
            await Task.WhenAll(process, patch);
            using (var patchResponse = await patch)
            {
                Assert.Contains(
                    patchResponse.StatusCode,
                    new[] { HttpStatusCode.OK, HttpStatusCode.Conflict });
            }

            await using var after = fixture.CreateDbContext();
            Assert.Equal(
                WorkflowInstanceStatuses.Completed,
                (await after.WorkflowInstances.SingleAsync(item => item.Id == started.Id)).Status);
            var conditional = await after.ConditionalBoundarySubscriptions.SingleAsync(item =>
                item.InstanceId == started.Id);
            var timer = await after.TimerSubscriptions.SingleAsync(item =>
                item.InstanceId == started.Id && item.TimerNodeId == 6);
            var conditionalWon = conditional.Status
                == ConditionalBoundarySubscriptionStatuses.Completed;
            Assert.Equal(
                conditionalWon
                    ? ConditionalBoundarySubscriptionStatuses.Completed
                    : ConditionalBoundarySubscriptionStatuses.Cancelled,
                conditional.Status);
            Assert.Equal(
                conditionalWon
                    ? TimerSubscriptionStatuses.Cancelled
                    : TimerSubscriptionStatuses.Completed,
                timer.Status);
            Assert.Equal(
                conditionalWon ? 1 : 0,
                await after.InstanceHistory.CountAsync(item =>
                    item.InstanceId == started.Id
                    && item.Note == InstanceHistoryNotes.ConditionalTriggered));
            var completedTerminalNodes = await after.ExecutionTokens
                .Where(token => token.InstanceId == started.Id
                    && token.Status == ExecutionTokenStatuses.Completed
                    && (token.NodeId == 5 || token.NodeId == 7))
                .Select(token => token.NodeId)
                .ToListAsync();
            Assert.Single(completedTerminalNodes);
            Assert.Equal(conditionalWon ? 5 : 7, completedTerminalNodes[0]);
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task Durable_batch_capacity_failure_rolls_back_variable_write_and_all_captures()
    {
        var workflowKey = $"conditional-boundary-capacity-{Guid.NewGuid():N}";
        string? originalLimit = null;
        try
        {
            await using (var settings = fixture.CreateDbContext())
            {
                var setting = await settings.EngineSettings.SingleAsync(item =>
                    item.Namespace == "Workflow.MultiInstance"
                    && item.Key == "MaxInstances");
                originalLimit = setting.Value;
                setting.Value = "1";
                setting.UpdatedAt = DateTimeOffset.UtcNow;
                await settings.SaveChangesAsync();
            }

            var workflow = CreateWorkflow(
                workflowKey,
                ConditionalEventDeliveryModes.DurableAsync,
                cancelActivity: false);
            AddBoundary(
                workflow,
                boundaryNodeId: 6,
                targetNodeId: 7,
                cancelActivity: false,
                deliveryMode: ConditionalEventDeliveryModes.DurableAsync);
            AddBoundary(
                workflow,
                boundaryNodeId: 8,
                targetNodeId: 9,
                cancelActivity: false,
                deliveryMode: ConditionalEventDeliveryModes.DurableAsync);
            var workflowId = await CreateWorkflowAsync(workflow);
            var started = await StartAsync(workflowId, approved: false);

            using (var response = await PatchVariableResponseAsync(
                       started.Id,
                       "approved",
                       true))
            {
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            }

            await using var db = fixture.CreateDbContext();
            Assert.False((await db.InstanceVariableCurrentValues.SingleAsync(item =>
                item.InstanceId == started.Id
                && item.VariableName == "approved")).ValueJson.RootElement.GetBoolean());
            Assert.Empty(await db.WorkflowJobs.Where(job =>
                job.InstanceId == started.Id
                && job.Kind == WorkflowJobKinds.ConditionalWake).ToListAsync());
            Assert.All(
                await db.ConditionalBoundarySubscriptions
                    .Where(item => item.InstanceId == started.Id)
                    .ToListAsync(),
                subscription =>
                {
                    Assert.Equal(0, subscription.Occurrence);
                    Assert.False(subscription.IsConditionTrue);
                    Assert.Equal(
                        ConditionalBoundarySubscriptionStatuses.Active,
                        subscription.Status);
                });
            Assert.DoesNotContain(
                await db.ExecutionTokens.Where(token => token.InstanceId == started.Id).ToListAsync(),
                token => token.NodeId is 4 or 6 or 8);
        }
        finally
        {
            if (originalLimit is not null)
            {
                await using var settings = fixture.CreateDbContext();
                var setting = await settings.EngineSettings.SingleAsync(item =>
                    item.Namespace == "Workflow.MultiInstance"
                    && item.Key == "MaxInstances");
                setting.Value = originalLimit;
                setting.UpdatedAt = DateTimeOffset.UtcNow;
                await settings.SaveChangesAsync();
            }
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    [Fact]
    public async Task Observable_write_has_constant_query_budget_and_batches_durable_boundaries()
    {
        const int unrelatedInstanceCount = 6;
        const int historyRowsPerInstance = 20;
        var workflowKey = $"conditional-boundary-batch-budget-{Guid.NewGuid():N}";
        try
        {
            var workflow = CreateWorkflow(
                workflowKey,
                ConditionalEventDeliveryModes.DurableAsync,
                cancelActivity: false);
            AddBoundary(
                workflow,
                boundaryNodeId: 6,
                targetNodeId: 7,
                cancelActivity: false,
                deliveryMode: ConditionalEventDeliveryModes.DurableAsync);
            var workflowId = await CreateWorkflowAsync(workflow);
            var target = await StartAsync(workflowId, approved: false);

            _ = await PatchVariableAsync(target.Id, "approved", false);
            int baselineCommands;
            using (var evaluations = new ConditionalEvaluationProbe())
            {
                fixture.CommandCounter.Reset(captureReaderCommandTexts: true);
                _ = await PatchVariableAsync(target.Id, "approved", true);
                baselineCommands = fixture.CommandCounter.ReaderCommands;
                Assert.Equal(2, evaluations.Count);
                Assert.Single(fixture.CommandCounter.ReaderCommandTexts, command =>
                    command.Contains(
                        "INSERT INTO flowbit.workflow_jobs",
                        StringComparison.OrdinalIgnoreCase));
                Assert.Single(fixture.CommandCounter.ReaderCommandTexts, command =>
                    command.Contains(
                        "INSERT INTO flowbit.execution_tokens",
                        StringComparison.OrdinalIgnoreCase));
                Assert.Single(fixture.CommandCounter.ReaderCommandTexts, command =>
                    command.Contains(
                        "UPDATE flowbit.conditional_boundary_subscriptions",
                        StringComparison.OrdinalIgnoreCase));
            }
            _ = await PatchVariableAsync(target.Id, "approved", false);

            var unrelatedIds = new long[unrelatedInstanceCount];
            for (var index = 0; index < unrelatedInstanceCount; index++)
            {
                unrelatedIds[index] = (await StartAsync(workflowId, approved: false)).Id;
                _ = await PatchVariableAsync(unrelatedIds[index], "approved", true);
            }
            await using (var seed = fixture.CreateDbContext())
            {
                var now = DateTimeOffset.UtcNow;
                seed.InstanceHistory.AddRange(
                    unrelatedIds.SelectMany((instanceId, instanceIndex) =>
                        Enumerable.Range(0, historyRowsPerInstance).Select(row =>
                            new InstanceHistoryEntity
                            {
                                InstanceId = instanceId,
                                WorkflowDefinitionId = workflowId,
                                FromStepId = 1,
                                ToStepId = 2,
                                Note = "conditional-boundary-budget-seed",
                                PerformedAt = now.AddTicks(
                                    instanceIndex * historyRowsPerInstance + row)
                            })));
                await seed.SaveChangesAsync();
                Assert.True(await seed.ExecutionTokens.CountAsync(token =>
                    unrelatedIds.Contains(token.InstanceId)
                    && token.Status == ExecutionTokenStatuses.Active) >= unrelatedInstanceCount * 3);
            }

            using (var evaluations = new ConditionalEvaluationProbe())
            {
                fixture.CommandCounter.Reset(captureReaderCommandTexts: true);
                _ = await PatchVariableAsync(target.Id, "approved", true);
                Assert.Equal(2, evaluations.Count);
                Assert.Equal(baselineCommands, fixture.CommandCounter.ReaderCommands);
                Assert.Single(fixture.CommandCounter.ReaderCommandTexts, command =>
                    command.Contains(
                        "INSERT INTO flowbit.workflow_jobs",
                        StringComparison.OrdinalIgnoreCase));
                Assert.Single(fixture.CommandCounter.ReaderCommandTexts, command =>
                    command.Contains(
                        "INSERT INTO flowbit.execution_tokens",
                        StringComparison.OrdinalIgnoreCase));
                Assert.Single(fixture.CommandCounter.ReaderCommandTexts, command =>
                    command.Contains(
                        "UPDATE flowbit.conditional_boundary_subscriptions",
                        StringComparison.OrdinalIgnoreCase));
            }

            await using var after = fixture.CreateDbContext();
            Assert.Equal(4, await after.WorkflowJobs.CountAsync(job =>
                job.InstanceId == target.Id
                && job.Kind == WorkflowJobKinds.ConditionalWake));
            Assert.All(
                await after.ConditionalBoundarySubscriptions
                    .Where(item => item.InstanceId == target.Id)
                    .ToListAsync(),
                subscription => Assert.Equal(2, subscription.Occurrence));
        }
        finally
        {
            await DeleteWorkflowAsync(workflowKey);
        }
    }

    private async Task<long> CreateWorkflowAsync(WorkflowModel definition)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(definition, true));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<WorkflowDetailDto>(response)).Id;
    }

    private async Task<InstanceDetailDto> StartAsync(long workflowId, bool approved)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(
                workflowId,
                null,
                null,
                new Dictionary<string, JsonElement>
                {
                    ["approved"] = JsonSerializer.SerializeToElement(approved),
                    ["noise"] = JsonSerializer.SerializeToElement(false)
                }));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private async Task<UpdateInstanceVariablesResultDto> PatchVariableAsync(
        long instanceId,
        string name,
        bool value)
    {
        using var response = await PatchVariableResponseAsync(instanceId, name, value);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<UpdateInstanceVariablesResultDto>(response);
    }

    private Task<HttpResponseMessage> PatchVariableResponseAsync(
        long instanceId,
        string name,
        bool value) =>
        SendAsync(
            HttpMethod.Patch,
            $"/api/instances/{instanceId}/variables",
            new UpdateInstanceVariablesRequest(
                [new InstanceVariableWriteDto(
                    name,
                    JsonSerializer.SerializeToElement(value))],
                "conditional boundary runtime test",
                $"conditional-boundary-update-{Guid.NewGuid():N}"),
            user: "operator");

    private async Task AssertOccurrenceAsync(
        long instanceId,
        long occurrence,
        bool conditionTrue)
    {
        await using var db = fixture.CreateDbContext();
        var subscription = await db.ConditionalBoundarySubscriptions.SingleAsync(item =>
            item.InstanceId == instanceId);
        Assert.Equal(occurrence, subscription.Occurrence);
        Assert.Equal(conditionTrue, subscription.IsConditionTrue);
        Assert.Equal(ConditionalBoundarySubscriptionStatuses.Active, subscription.Status);
    }

    private async Task PromoteAndProcessAsync(long jobId, string queueClass)
    {
        await PromoteJobAsync(jobId);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>();
        var leases = await repository.LeaseRunnableAsync(
            new WorkflowJobLeaseRequest(
                $"conditional-boundary-test:{Guid.NewGuid():N}",
                MaxCount: 1,
                MaxActivityCount: queueClass == WorkflowJobClasses.Activity ? 1 : 0,
                MaxPerInstance: 4,
                LeaseDuration: TimeSpan.FromMinutes(1)),
            CancellationToken.None);
        var lease = Assert.Single(leases);
        Assert.Equal(jobId, lease.Job.Id);
        Assert.Equal(queueClass, lease.Job.QueueClass);

        var processor = scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>();
        await processor.ProcessAsync(lease, CancellationToken.None);
    }

    private async Task PromoteJobAsync(long jobId)
    {
        await using var promote = fixture.CreateDbContext();
        var changed = await promote.WorkflowJobs
            .Where(job => job.Id == jobId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Priority, int.MaxValue)
                .SetProperty(job => job.DueAt, DateTimeOffset.UtcNow));
        Assert.Equal(1, changed);
    }

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "test-admin")
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, user, ["admin"]);
        return fixture.Client.SendAsync(request);
    }

    private async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("The API returned no JSON body.");

    private async Task DeleteWorkflowAsync(string workflowKey)
    {
        await using var cleanup = fixture.CreateDbContext();
        await cleanup.WorkflowInstances
            .Where(instance => instance.WorkflowKey == workflowKey)
            .ExecuteDeleteAsync();
        await cleanup.WorkflowDefinitions
            .Where(definition => definition.WorkflowKey == workflowKey)
            .ExecuteDeleteAsync();
    }

    private static WorkflowModel CreateWorkflow(
        string workflowKey,
        string deliveryMode,
        bool cancelActivity,
        bool approvedDefault = false) =>
        new()
        {
            Id = workflowKey,
            Name = workflowKey,
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "approved",
                    DataType = WorkflowVariableTypes.Boolean,
                    Required = true,
                    DefaultValue = JsonSerializer.SerializeToElement(approvedDefault)
                },
                new VariableModel
                {
                    Id = 2,
                    Name = "noise",
                    DataType = WorkflowVariableTypes.Boolean,
                    Required = true,
                    DefaultValue = JsonSerializer.SerializeToElement(false)
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Start",
                    Type = BpmnFlowNodeTypes.StartEvent
                },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Review",
                    Type = BpmnFlowNodeTypes.UserTask
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Normal end",
                    Type = BpmnFlowNodeTypes.EndEvent
                },
                new FlowNodeModel
                {
                    Id = 4,
                    Name = "Approved boundary",
                    Type = BpmnFlowNodeTypes.ConditionalBoundaryEvent,
                    AttachedToRef = 2,
                    CancelActivity = cancelActivity,
                    Conditional = new ConditionalDefinitionModel
                    {
                        Condition = "approved == true",
                        DeliveryMode = deliveryMode
                    }
                },
                new FlowNodeModel
                {
                    Id = 5,
                    Name = "Boundary end",
                    Type = BpmnFlowNodeTypes.EndEvent
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 10, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 20, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 40, SourceRef = 4, TargetRef = 5 }
            ]
        };

    private static void AddBoundary(
        WorkflowModel workflow,
        int boundaryNodeId,
        int targetNodeId,
        bool cancelActivity,
        string deliveryMode = ConditionalEventDeliveryModes.Atomic)
    {
        workflow.FlowNodes.Add(new FlowNodeModel
        {
            Id = boundaryNodeId,
            Name = $"Boundary {boundaryNodeId}",
            Type = BpmnFlowNodeTypes.ConditionalBoundaryEvent,
            AttachedToRef = 2,
            CancelActivity = cancelActivity,
            Conditional = new ConditionalDefinitionModel
            {
                Condition = "approved == true",
                DeliveryMode = deliveryMode
            }
        });
        workflow.FlowNodes.Add(new FlowNodeModel
        {
            Id = targetNodeId,
            Name = $"Boundary end {targetNodeId}",
            Type = BpmnFlowNodeTypes.EndEvent
        });
        workflow.SequenceFlows.Add(new SequenceFlowModel
        {
            Id = 100 + boundaryNodeId,
            SourceRef = boundaryNodeId,
            TargetRef = targetNodeId
        });
    }

    private static WorkflowModel Clone(WorkflowModel model) =>
        JsonSerializer.Deserialize<WorkflowModel>(
            JsonSerializer.Serialize(model, JsonOptions),
            JsonOptions)
        ?? throw new InvalidOperationException("Failed to clone workflow model.");

    private static WorkflowModel CreateParallelCleanupWorkflow(
        string workflowKey,
        bool terminate)
    {
        var workflow = CreateWorkflow(
            workflowKey,
            ConditionalEventDeliveryModes.Atomic,
            cancelActivity: false);
        workflow.SequenceFlows.Single(flow => flow.Id == 10).TargetRef = 6;
        workflow.FlowNodes.AddRange(
        [
            new FlowNodeModel
            {
                Id = 6,
                Name = "Parallel work",
                Type = BpmnFlowNodeTypes.ParallelGateway
            },
            new FlowNodeModel
            {
                Id = 7,
                Name = terminate ? "Terminate instance" : "Interrupt scope",
                Type = BpmnFlowNodeTypes.UserTask
            },
            new FlowNodeModel
            {
                Id = 8,
                Name = terminate ? "Terminate" : "Scoped interrupt",
                Type = terminate
                    ? BpmnFlowNodeTypes.TerminateEndEvent
                    : BpmnFlowNodeTypes.ScopedInterruptEvent,
                GatewayRef = terminate ? null : 6
            }
        ]);
        workflow.SequenceFlows.AddRange(
        [
            new SequenceFlowModel { Id = 11, SourceRef = 6, TargetRef = 2 },
            new SequenceFlowModel { Id = 12, SourceRef = 6, TargetRef = 7 },
            new SequenceFlowModel { Id = 70, SourceRef = 7, TargetRef = 8 }
        ]);
        if (!terminate)
        {
            workflow.FlowNodes.Add(new FlowNodeModel
            {
                Id = 9,
                Name = "Interrupted end",
                Type = BpmnFlowNodeTypes.EndEvent
            });
            workflow.SequenceFlows.Add(new SequenceFlowModel
            {
                Id = 80,
                SourceRef = 8,
                TargetRef = 9
            });
        }
        return workflow;
    }

    private static bool MentionsTable(string commandText, string tableName) =>
        commandText.Contains(tableName, StringComparison.OrdinalIgnoreCase);

    private sealed class ConditionalEvaluationProbe : IDisposable
    {
        private readonly MeterListener listener = new();
        private long count;

        public ConditionalEvaluationProbe()
        {
            listener.InstrumentPublished = (instrument, currentListener) =>
            {
                if (instrument.Meter.Name == "Flowbit.Runtime.ConditionalEvents"
                    && instrument.Name == "flowbit.conditional.evaluations")
                {
                    currentListener.EnableMeasurementEvents(instrument);
                }
            };
            listener.SetMeasurementEventCallback<long>((_, value, _, _) =>
                Interlocked.Add(ref count, value));
            listener.Start();
        }

        public long Count => Interlocked.Read(ref count);

        public void Dispose() => listener.Dispose();
    }
}
