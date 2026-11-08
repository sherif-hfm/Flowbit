extern alias FlowbitWorker;

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;
using TimerStartReconciliationService = FlowbitWorker::Flowbit.Worker.TimerStartReconciliationService;
using WorkerOptions = FlowbitWorker::Flowbit.Worker.WorkerOptions;
using WorkerTelemetry = FlowbitWorker::Flowbit.Worker.WorkerTelemetry;

namespace Flowbit.Tests;

/// <summary>
/// Lifecycle capture runs in deferred PostgreSQL triggers, so every producer path is exercised through the real
/// API and through raw SQL, with no application producer code.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class LifecycleEventCaptureTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly string[] AllTypes = [.. LifecycleEventTypes.Subscribable];
    private const string DistributorClientId = "lifecycle-distributor";
    private const string DistributorClientSecret = "lifecycle-distributor-secret";

    /// <summary>
    /// Whether an incident of each job kind reports its job's node. Version-change and variable-update batch
    /// jobs name the initial start event only to anchor their row, so no authored node applies to them. An
    /// administrative batch job names the user task whose positions its batch acts on. Classify every new kind
    /// here; the kinds that report no node are listed in capture_workflow_incident_lifecycle().
    /// </summary>
    private static readonly IReadOnlyDictionary<string, bool> JobKindReportsNode =
        new Dictionary<string, bool>(StringComparer.Ordinal)
        {
            [WorkflowJobKinds.AsyncBefore] = true,
            [WorkflowJobKinds.AsyncAfter] = true,
            [WorkflowJobKinds.Timer] = true,
            [WorkflowJobKinds.TimerStart] = true,
            [WorkflowJobKinds.TimerBoundary] = true,
            [WorkflowJobKinds.ConditionalWake] = true,
            [WorkflowJobKinds.CallStart] = true,
            [WorkflowJobKinds.CallResume] = true,
            [WorkflowJobKinds.CallCancel] = true,
            [WorkflowJobKinds.AdministrativeBatchPrepare] = true,
            [WorkflowJobKinds.AdministrativeBatchExecute] = true,
            [WorkflowJobKinds.InstanceVersionChangeBatchPrepare] = false,
            [WorkflowJobKinds.InstanceVersionChangeBatchExecute] = false,
            [WorkflowJobKinds.InstanceVariableUpdateBatchPrepare] = false,
            [WorkflowJobKinds.InstanceVariableUpdateBatchExecute] = false
        };

    [Fact]
    public async Task UserTaskFlow_RecordsStartTaskAndCompletionEventsInOrder()
    {
        var model = UserTaskModel(BpmnFlowNodeTypes.EndEvent);
        await WithCaptureAsync(model.Id, AllTypes, async revision =>
        {
            var workflowId = await CreateWorkflowAsync(model);
            var started = await StartAsync(workflowId);
            var task = await SingleActiveTaskAsync(started.Id);
            await TakeTaskFlowAsync(task.Id, 201);

            var events = await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision);
            Assert.Equal(
                [LifecycleEventTypes.InstanceStarted, LifecycleEventTypes.TaskCreated,
                 LifecycleEventTypes.TaskCompleted, LifecycleEventTypes.InstanceCompleted],
                events.Select(item => item.Type));
            Assert.All(events, LifecycleEventBodies.AssertMatchesCatalog);
            Assert.All(events, item => Assert.Equal(started.Id, item.InstanceId));

            var created = LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<TaskLifecycleEventDataDto>>(events[1].Body);
            Assert.Equal(model.Id, created.WorkflowKey);
            Assert.Equal(workflowId, created.WorkflowDefinitionId);
            Assert.Equal(1, created.WorkflowVersion);
            Assert.Equal(task.Id, created.Data.UserTaskId);
            Assert.Equal(2, created.Data.NodeId);
            Assert.Equal("REVIEW", created.Data.NodeExternalId);
            Assert.NotNull(created.Data.NodeExecutionId);
            Assert.Null(created.Data.MultiInstanceExecutionId);
            Assert.Null(created.Data.ItemIndex);
            Assert.Equal("active", created.Data.TaskStatus);
            Assert.Empty(events[1].RecipientUserIds);

            var completedInstance = LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<InstanceLifecycleEventDataDto>>(events[3].Body);
            Assert.Equal("completed", completedInstance.Data.Status);
            // External bodies never carry variables, actors, or business keys.
            Assert.DoesNotContain("tester", events[2].Body, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task ImmediateStartToEnd_RecordsBothEventsFromOneTransaction()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var model = new WorkflowModel
        {
            Id = "webhook-immediate-" + suffix,
            Name = "Immediate " + suffix,
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows = [new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 }]
        };
        await WithCaptureAsync(model.Id, AllTypes, async revision =>
        {
            var started = await StartAsync(await CreateWorkflowAsync(model), expectRunning: false);
            var events = await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision);
            Assert.Equal([LifecycleEventTypes.InstanceStarted, LifecycleEventTypes.InstanceCompleted], events.Select(item => item.Type));
            Assert.All(events, item => Assert.Equal(started.Id, item.InstanceId));
        });
    }

    [Fact]
    public async Task ErrorEndAndCancellation_RecordFaultedCancelledAndTaskCancellation()
    {
        var faultModel = UserTaskModel(BpmnFlowNodeTypes.ErrorEndEvent);
        await WithCaptureAsync(faultModel.Id, AllTypes, async revision =>
        {
            var workflowId = await CreateWorkflowAsync(faultModel);
            var faulted = await StartAsync(workflowId);
            await TakeTaskFlowAsync((await SingleActiveTaskAsync(faulted.Id)).Id, 201);
            var cancelled = await StartAsync(workflowId);
            var task = await SingleActiveTaskAsync(cancelled.Id);
            using (var response = await SendAsync(HttpMethod.Post, $"/api/instances/{cancelled.Id}/cancel"))
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            // Repeated cancellation changes nothing and records nothing.
            using (var again = await SendAsync(HttpMethod.Post, $"/api/instances/{cancelled.Id}/cancel"))
                Assert.NotEqual(HttpStatusCode.InternalServerError, again.StatusCode);

            var events = await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision);
            var faultedEvents = events.Where(item => item.InstanceId == faulted.Id).Select(item => item.Type);
            Assert.Equal(
                [LifecycleEventTypes.InstanceStarted, LifecycleEventTypes.TaskCreated,
                 LifecycleEventTypes.TaskCompleted, LifecycleEventTypes.InstanceFaulted],
                faultedEvents);
            var cancelledEvents = events.Where(item => item.InstanceId == cancelled.Id).ToArray();
            Assert.Equal(
                [LifecycleEventTypes.InstanceStarted, LifecycleEventTypes.TaskCreated,
                 LifecycleEventTypes.TaskCancelled, LifecycleEventTypes.InstanceCancelled],
                cancelledEvents.Select(item => item.Type));
            var taskCancelled = LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<TaskCancelledEventDataDto>>(cancelledEvents[2].Body);
            Assert.Equal(task.Id, taskCancelled.Data.UserTaskId);
            Assert.Equal("cancelled", taskCancelled.Data.TaskStatus);
            Assert.Equal("active", taskCancelled.Data.PreviousTaskStatus);
            Assert.All(events, LifecycleEventBodies.AssertMatchesCatalog);
        });
    }

    [Fact]
    public async Task Reactivation_RecordsReactivatedThenTaskCreatedAndALaterDistinctTerminalEvent()
    {
        var model = UserTaskModel(BpmnFlowNodeTypes.EndEvent);
        await WithCaptureAsync(model.Id, AllTypes, async revision =>
        {
            var started = await StartAsync(await CreateWorkflowAsync(model));
            await TakeTaskFlowAsync((await SingleActiveTaskAsync(started.Id)).Id, 201);
            using (var previewResponse = await SendAsync(HttpMethod.Get, $"/api/instances/{started.Id}/reactivation"))
            {
                var preview = await ReadAsync<InstanceReactivationPreviewDto>(previewResponse);
                using var reactivation = await SendAsync(
                    HttpMethod.Post,
                    $"/api/instances/{started.Id}/reactivation",
                    new ReactivateInstanceRequest(2, preview.WorkflowId, preview.ExpectedUpdatedAt, "Reopen for webhooks"));
                Assert.Equal(HttpStatusCode.OK, reactivation.StatusCode);
            }
            await TakeTaskFlowAsync((await SingleActiveTaskAsync(started.Id)).Id, 201);

            var events = await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision);
            Assert.Equal(
                [LifecycleEventTypes.InstanceStarted, LifecycleEventTypes.TaskCreated, LifecycleEventTypes.TaskCompleted,
                 LifecycleEventTypes.InstanceCompleted, LifecycleEventTypes.InstanceReactivated, LifecycleEventTypes.TaskCreated,
                 LifecycleEventTypes.TaskCompleted, LifecycleEventTypes.InstanceCompleted],
                events.Select(item => item.Type));
            var reactivated = LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<InstanceReactivatedEventDataDto>>(events[4].Body);
            Assert.Equal("running", reactivated.Data.Status);
            Assert.Equal("completed", reactivated.Data.PreviousStatus);
            // Terminal events are not deduplicated: each committed transition is its own event.
            Assert.NotEqual(events[3].Id, events[7].Id);
            Assert.All(events, LifecycleEventBodies.AssertMatchesCatalog);
        });
    }

    [Fact]
    public async Task Assignment_RecordsCreatedThenAssignedAndEveryRealReassignment()
    {
        var model = UserTaskModel(BpmnFlowNodeTypes.EndEvent);
        model.TaskAssignmentRoles = ["Manager"];
        model.FlowNodes.Single(node => node.Id == 2).AssigneeExpression = "'alice'";
        await WithCaptureAsync(model.Id, AllTypes, async revision =>
        {
            var started = await StartAsync(await CreateWorkflowAsync(model));
            var task = await SingleActiveTaskAsync(started.Id, "alice");
            await AssignAsync(task.Id, "bob");
            await AssignAsync(task.Id, "BOB"); // the same actor under case-insensitive identity: no event
            await AssignAsync(task.Id, "alice");
            using (var unassign = await SendAsync(
                       HttpMethod.Post,
                       $"/api/user-tasks/{task.Id}/unassign",
                       new UnassignUserTaskRequest(await TaskUpdatedAtAsync(task.Id), null),
                       roles: "Manager"))
            {
                Assert.Equal(HttpStatusCode.OK, unassign.StatusCode);
            }

            var events = await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision);
            Assert.Equal(
                [LifecycleEventTypes.InstanceStarted, LifecycleEventTypes.TaskCreated, LifecycleEventTypes.TaskAssigned,
                 LifecycleEventTypes.TaskAssigned, LifecycleEventTypes.TaskAssigned],
                events.Select(item => item.Type));
            var initial = LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<TaskAssignedEventDataDto>>(events[2].Body);
            Assert.Equal("assigned", initial.Data.AssignmentOperation);
            Assert.Equal("active", initial.Data.TaskStatus);
            Assert.Equal(["alice"], events[1].RecipientUserIds);
            Assert.Equal(["alice"], events[2].RecipientUserIds);
            Assert.Equal(["bob"], events[3].RecipientUserIds);
            Assert.Equal(["alice"], events[4].RecipientUserIds);
            Assert.All(events.Skip(3), item => Assert.Equal(
                "reassigned",
                item.Data.GetProperty("assignmentOperation").GetString()));
            // Internal recipients never reach the external body.
            Assert.All(events, item => Assert.DoesNotContain("alice", item.Body, StringComparison.Ordinal));
            Assert.All(events, LifecycleEventBodies.AssertMatchesCatalog);
        });
    }

    [Fact]
    public async Task SequentialMultiInstance_AnnouncesPendingAssignmentsAndActivationWithoutRepeatingThem()
    {
        var model = DefinitionValidationTests.LoadModel("votes-sequence-users-list.json");
        model.Id = "webhook-mi-" + Guid.NewGuid().ToString("N");
        model.Name = model.Id;
        await WithCaptureAsync(model.Id, AllTypes, async revision =>
        {
            var started = await StartAsync(await CreateWorkflowAsync(model), expectedNode: 5);
            await TakeTaskFlowAsync((await SingleActiveTaskAsync(started.Id, roles: "Manager")).Id, 204, roles: "Manager");
            var first = await SingleActiveTaskAsync(started.Id, "alice");
            await TakeTaskFlowAsync(first.Id, 201, user: "alice");

            var events = await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision);
            var childEvents = events
                .Where(item => item.Type.StartsWith("task.", StringComparison.Ordinal)
                    && item.Data.GetProperty("multiInstanceExecutionId").ValueKind != JsonValueKind.Null)
                .Select(item => (item.Type, Index: item.Data.GetProperty("itemIndex").GetInt32(),
                    Status: item.Data.GetProperty("taskStatus").GetString()))
                .ToArray();
            Assert.Equal(
                [
                    (LifecycleEventTypes.TaskCreated, 0, "active"),
                    (LifecycleEventTypes.TaskAssigned, 0, "active"),
                    (LifecycleEventTypes.TaskAssigned, 1, "pending"),
                    (LifecycleEventTypes.TaskAssigned, 2, "pending"),
                    (LifecycleEventTypes.TaskCompleted, 0, "completed"),
                    (LifecycleEventTypes.TaskCreated, 1, "active")
                ],
                childEvents);
            var activation = events.Last(item => item.Type == LifecycleEventTypes.TaskCreated);
            Assert.Equal(["alice1"], activation.RecipientUserIds);
            Assert.All(events, LifecycleEventBodies.AssertMatchesCatalog);
        });
    }

    [Fact]
    public async Task RawSqlTransitions_FollowTheCatalogRules()
    {
        var model = UserTaskModel(BpmnFlowNodeTypes.EndEvent);
        await WithCaptureAsync(model.Id, AllTypes, async revision =>
        {
            var started = await StartAsync(await CreateWorkflowAsync(model));
            var task = await SingleActiveTaskAsync(started.Id);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();

            // Rolled-back and savepoint-rolled-back work records nothing.
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await Execute(connection, transaction, "UPDATE flowbit.user_tasks SET \"Assignee\" = 'carol', \"UpdatedAt\" = now() WHERE \"Id\" = @id", task.Id);
                await transaction.RollbackAsync();
            }
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await transaction.SaveAsync("before");
                await Execute(connection, transaction, "UPDATE flowbit.user_tasks SET \"Assignee\" = 'dave', \"UpdatedAt\" = now() WHERE \"Id\" = @id", task.Id);
                await transaction.RollbackAsync("before");
                // An unchanged update fails the trigger's WHEN clause.
                await Execute(connection, transaction, "UPDATE flowbit.user_tasks SET \"Status\" = \"Status\" WHERE \"Id\" = @id", task.Id);
                await transaction.CommitAsync();
            }
            // The transaction-local repair setting suppresses capture.
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await Execute(connection, transaction, "SET LOCAL flowbit.lifecycle_events = 'off'", null);
                await Execute(connection, transaction, "UPDATE flowbit.user_tasks SET \"Assignee\" = 'erin', \"UpdatedAt\" = now() WHERE \"Id\" = @id", task.Id);
                await transaction.CommitAsync();
            }
            Assert.DoesNotContain(
                await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision),
                item => item.Type == LifecycleEventTypes.TaskAssigned);

            // A pending item completed directly emits created then completed; a pending item without an
            // assignee was never announced, so its cancellation emits nothing.
            var pendingIds = new List<long>();
            for (var index = 0; index < 3; index++)
            {
                await using var insert = new NpgsqlCommand("""
                    INSERT INTO flowbit.user_tasks
                        ("InstanceId", "TokenId", "NodeId", "NodeName", "NodeExternalId", "Roles", "RequiresClaim",
                         "Status", "CreatedAt", "UpdatedAt", "Assignee")
                    SELECT "InstanceId", "TokenId", "NodeId", "NodeName", "NodeExternalId", "Roles", FALSE,
                           'pending', now(), now(), CASE WHEN @index = 2 THEN 'frank' END
                    FROM flowbit.user_tasks WHERE "Id" = @id
                    RETURNING "Id"
                    """, connection);
                insert.Parameters.AddWithValue("id", task.Id);
                insert.Parameters.AddWithValue("index", index);
                pendingIds.Add((long)(await insert.ExecuteScalarAsync())!);
            }
            await Execute(connection, null, "UPDATE flowbit.user_tasks SET \"Status\" = 'completed', \"CompletedAt\" = now(), \"UpdatedAt\" = now() WHERE \"Id\" = @id", pendingIds[0]);
            await Execute(connection, null, "UPDATE flowbit.user_tasks SET \"Status\" = 'cancelled', \"CompletedAt\" = now(), \"UpdatedAt\" = now() WHERE \"Id\" = @id", pendingIds[1]);
            await Execute(connection, null, "UPDATE flowbit.user_tasks SET \"Status\" = 'cancelled', \"CompletedAt\" = now(), \"UpdatedAt\" = now() WHERE \"Id\" = @id", pendingIds[2]);

            var events = await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision);
            Assert.Equal(
                [LifecycleEventTypes.TaskCreated, LifecycleEventTypes.TaskCompleted],
                events.Where(item => item.SourceTable == "user_tasks" && item.SourceId == pendingIds[0]).Select(item => item.Type));
            Assert.DoesNotContain(events, item => item.SourceTable == "user_tasks" && item.SourceId == pendingIds[1]);
            var announced = events.Where(item => item.SourceTable == "user_tasks" && item.SourceId == pendingIds[2]).ToArray();
            Assert.Equal([LifecycleEventTypes.TaskAssigned, LifecycleEventTypes.TaskCancelled], announced.Select(item => item.Type));
            Assert.Equal("pending", announced[0].Data.GetProperty("taskStatus").GetString());
            Assert.Equal("pending", announced[1].Data.GetProperty("previousTaskStatus").GetString());
            Assert.All(events, LifecycleEventBodies.AssertMatchesCatalog);
        });
    }

    [Fact]
    public async Task CaptureOffAndUnmatchedSubscriptionsStoreNothing()
    {
        var model = UserTaskModel(BpmnFlowNodeTypes.EndEvent);
        var (subscriptionId, revision) = await WebhookTestDatabase.CreateSubscriptionAsync(
            fixture.DataSource, AllTypes, model.Id);
        var (otherId, otherRevision) = await WebhookTestDatabase.CreateSubscriptionAsync(
            fixture.DataSource, AllTypes, "some-other-family-" + Guid.NewGuid().ToString("N"));
        var (typeFilteredId, typeFilteredRevision) = await WebhookTestDatabase.CreateSubscriptionAsync(
            fixture.DataSource, [LifecycleEventTypes.IncidentOpened], model.Id);
        var (pausedId, pausedRevision) = await WebhookTestDatabase.CreateSubscriptionAsync(
            fixture.DataSource, AllTypes, model.Id, state: "paused");
        try
        {
            await WebhookTestDatabase.SetCaptureAsync(fixture.DataSource, false);
            var workflowId = await CreateWorkflowAsync(model);
            await StartAsync(workflowId);
            Assert.Empty(await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision));

            await WebhookTestDatabase.SetCaptureAsync(fixture.DataSource, true);
            var captured = await StartAsync(workflowId);
            var events = await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision);
            Assert.Equal([LifecycleEventTypes.InstanceStarted, LifecycleEventTypes.TaskCreated], events.Select(item => item.Type));
            Assert.All(events, item => Assert.Equal(captured.Id, item.InstanceId));
            // Each event targets the matching active subscription (and, in the producer sweep, its catch-all) only.
            Assert.All(events, item => Assert.Equal([revision], WebhookTestDatabase.WithoutCatchAll(item.TargetRevisionIds)));
            Assert.Empty(await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, otherRevision));
            Assert.Empty(await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, typeFilteredRevision));
            Assert.Empty(await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, pausedRevision));
        }
        finally
        {
            foreach (var id in new[] { subscriptionId, otherId, typeFilteredId, pausedId })
            {
                await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, id, "archived");
            }
            await WebhookTestDatabase.RestoreCaptureAsync(fixture.DataSource);
        }
    }

    [Fact]
    public async Task SubscriptionEditsAffectOnlyLaterCaptures()
    {
        var model = UserTaskModel(BpmnFlowNodeTypes.EndEvent);
        await WithCaptureAsync(model.Id, AllTypes, async revision =>
        {
            var workflowId = await CreateWorkflowAsync(model);
            var before = await StartAsync(workflowId);
            long newRevision;
            await using (var connection = await fixture.DataSource.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand("""
                WITH next AS (
                    INSERT INTO flowbit.webhook_subscription_revisions
                        ("SubscriptionId", "ConfigurationRevision", "EndpointUrl", "EventTypes", "WorkflowKey", "CreatedAt", "CreatedBy")
                    SELECT "SubscriptionId", 2, 'https://other.example.com/flowbit', ARRAY['instance.started'], "WorkflowKey", now(), 'tests'
                    FROM flowbit.webhook_subscription_revisions WHERE "Id" = @revision
                    RETURNING "Id", "SubscriptionId")
                UPDATE flowbit.webhook_subscriptions s SET "CurrentRevisionId" = next."Id"
                FROM next WHERE s."Id" = next."SubscriptionId"
                RETURNING next."Id"
                """, connection))
            {
                command.Parameters.AddWithValue("revision", revision);
                newRevision = (long)(await command.ExecuteScalarAsync())!;
            }
            var after = await StartAsync(workflowId);

            var original = await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision);
            Assert.All(original, item => Assert.Equal(before.Id, item.InstanceId));
            var edited = await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, newRevision);
            Assert.Equal([LifecycleEventTypes.InstanceStarted], edited.Select(item => item.Type));
            Assert.Equal(after.Id, Assert.Single(edited).InstanceId);
        });
    }

    [Fact]
    public async Task DelegationGrantAndIncidentInsertsRecordGlobalEvents()
    {
        var model = UserTaskModel(BpmnFlowNodeTypes.EndEvent);
        await WithCaptureAsync(model.Id, AllTypes, async revision =>
        {
            var workflowId = await CreateWorkflowAsync(model);
            using (var grant = await SendAsync(
                       HttpMethod.Post,
                       "/api/user-delegations",
                       new CreateUserDelegationRequest(
                           "deputy",
                           [model.Id],
                           DateTimeOffset.UtcNow.AddMinutes(-1),
                           DateTimeOffset.UtcNow.AddDays(1)),
                       user: "manager"))
            {
                Assert.Equal(HttpStatusCode.Created, grant.StatusCode);
            }

            long jobId;
            await using (var connection = await fixture.DataSource.OpenConnectionAsync())
            {
                await using var job = new NpgsqlCommand("""
                    INSERT INTO flowbit.workflow_jobs
                        ("InstanceId", "WorkflowDefinitionId", "WorkflowKey", "ActivationId", "NodeId", "NodeName", "NodeType",
                         "Kind", "QueueClass", "Phase", "Status", "Priority", "AttemptCount", "MaxAttempts", "FailureHandling",
                         "RetryDelays", "DueAt", "LeaseGeneration", "CreatedAt", "UpdatedAt")
                    VALUES (NULL, @definition, @key, gen_random_uuid(), 2, 'Review', 'userTask', 'timerStart', 'control',
                            'execute', 'incident', 0, 3, 3, 'boundaryFirst', '{}', now(), 0, now(), now())
                    RETURNING "Id"
                    """, connection);
                job.Parameters.AddWithValue("definition", workflowId);
                job.Parameters.AddWithValue("key", model.Id);
                jobId = (long)(await job.ExecuteScalarAsync())!;
                const string incident = """
                    INSERT INTO flowbit.workflow_incidents
                        ("JobId", "OriginalJobId", "InstanceId", "WorkflowDefinitionId", "WorkflowKey", "NodeId", "NodeName",
                         "Type", "Status", "Summary", "CreatedAt", "UpdatedAt")
                    VALUES (@job, @job, NULL, @definition, @key, 2, 'Review', 'lease_exhausted', 'open', 'Lease exhausted', now(), now())
                    ON CONFLICT ("JobId", "Status") WHERE "Status" = 'open' DO NOTHING
                    """;
                for (var attempt = 0; attempt < 2; attempt++)
                {
                    await using var insert = new NpgsqlCommand(incident, connection);
                    insert.Parameters.AddWithValue("job", jobId);
                    insert.Parameters.AddWithValue("definition", workflowId);
                    insert.Parameters.AddWithValue("key", model.Id);
                    await insert.ExecuteNonQueryAsync();
                }
            }

            var events = await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision);
            var delegation = Assert.Single(events, item => item.Type == LifecycleEventTypes.DelegationGranted);
            var granted = LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<DelegationGrantedEventDataDto>>(delegation.Body);
            Assert.Null(granted.InstanceId);
            Assert.Null(granted.WorkflowDefinitionId);
            Assert.Null(granted.WorkflowVersion);
            Assert.Equal(model.Id, granted.WorkflowKey);
            Assert.Contains(granted.Data.AcceptanceState, new[] { "pending", "notRequired" });
            Assert.Equal(["deputy"], delegation.RecipientUserIds);

            // The skipped ON CONFLICT DO NOTHING insert records nothing.
            var opened = Assert.Single(events, item => item.Type == LifecycleEventTypes.IncidentOpened);
            var incidentBody = LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<IncidentOpenedEventDataDto>>(opened.Body);
            Assert.Null(incidentBody.InstanceId);
            Assert.Equal(jobId, incidentBody.Data.JobId);
            Assert.Null(incidentBody.Data.ExecutionTokenId);
            Assert.Equal(2, incidentBody.Data.NodeId);
            Assert.Equal("REVIEW", incidentBody.Data.NodeExternalId);
            Assert.Equal("open", incidentBody.Data.Status);
            Assert.All(events, LifecycleEventBodies.AssertMatchesCatalog);
        });
    }

    [Fact]
    public async Task TriggerFailureRollsBackTheSourceCommit()
    {
        var model = UserTaskModel(BpmnFlowNodeTypes.EndEvent);
        await WithCaptureAsync(model.Id, AllTypes, async revision =>
        {
            var workflowId = await CreateWorkflowAsync(model);
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var transaction = await connection.BeginTransactionAsync();
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO flowbit.workflow_instances ("WorkflowDefinitionId", "WorkflowKey", "Status")
                VALUES (@definition, @key, 'running')
                """, connection, transaction))
            {
                insert.Parameters.AddWithValue("definition", workflowId);
                insert.Parameters.AddWithValue("key", model.Id);
                await insert.ExecuteNonQueryAsync();
            }
            // Make the deferred capture fail at commit: the event body check rejects an oversized body.
            await using (var sabotage = new NpgsqlCommand("""
                ALTER TABLE flowbit.lifecycle_events DROP CONSTRAINT "CK_lifecycle_events_body";
                ALTER TABLE flowbit.lifecycle_events ADD CONSTRAINT "CK_lifecycle_events_body"
                    CHECK (octet_length("Body") BETWEEN 2 AND 3) NOT VALID;
                """, connection, transaction))
            {
                await sabotage.ExecuteNonQueryAsync();
            }
            await Assert.ThrowsAsync<PostgresException>(() => transaction.CommitAsync());
            Assert.Equal(0, await WebhookTestDatabase.CountEventsAsync(
                fixture.DataSource,
                "SELECT count(*) FROM flowbit.workflow_instances WHERE \"WorkflowDefinitionId\" = @definition",
                ("definition", workflowId)));
            Assert.Empty(await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision));
        });
    }

    [Fact]
    public async Task ParallelBranches_PartialEndRecordsNoInstanceEvent_TerminateAndScopedInterruptCancelSiblingWork()
    {
        var model = ParallelTasksModel();
        await WithCaptureAsync(model.Id, AllTypes, async revision =>
        {
            var workflowId = await CreateWorkflowAsync(model);

            // One branch reaching its end event leaves the instance running, so no instance event follows.
            var partial = await StartAsync(workflowId, expectRunning: false);
            var (partialFirst, partialSecond) = await BranchTasksAsync(partial.Id);
            await TakeTaskFlowAsync(partialFirst, 301);
            Assert.Equal(
                [LifecycleEventTypes.InstanceStarted],
                InstanceEventTypes(await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision)));
            await TakeTaskFlowAsync(partialSecond, 401);

            // A terminate end and a scoped interrupt each cancel the other branch's work item.
            var terminated = await StartAsync(workflowId, expectRunning: false);
            var (terminateFirst, terminateSecond) = await BranchTasksAsync(terminated.Id);
            await TakeTaskFlowAsync(terminateFirst, 303);
            var interrupted = await StartAsync(workflowId, expectRunning: false);
            var (interruptFirst, interruptSecond) = await BranchTasksAsync(interrupted.Id);
            await TakeTaskFlowAsync(interruptFirst, 302);

            var events = await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision);
            var partialEvents = events.Where(item => item.InstanceId == partial.Id).ToArray();
            Assert.Equal([LifecycleEventTypes.TaskCreated, LifecycleEventTypes.TaskCompleted], TaskEventTypes(partialEvents, partialFirst));
            Assert.Equal([LifecycleEventTypes.TaskCreated, LifecycleEventTypes.TaskCompleted], TaskEventTypes(partialEvents, partialSecond));
            Assert.Equal([LifecycleEventTypes.InstanceStarted, LifecycleEventTypes.InstanceCompleted], InstanceEventTypes(partialEvents));
            AssertSiblingCancelled(events, terminated.Id, terminateFirst, terminateSecond);
            AssertSiblingCancelled(events, interrupted.Id, interruptFirst, interruptSecond);
            Assert.All(events, LifecycleEventBodies.AssertMatchesCatalog);
        });
    }

    [Fact]
    public async Task CancellingJoinCancelsTheLosingBranchTask()
    {
        var model = CancellingJoinModel();
        await WithCaptureAsync(model.Id, AllTypes, async revision =>
        {
            var started = await StartAsync(await CreateWorkflowAsync(model), expectRunning: false);
            var (winner, loser) = await BranchTasksAsync(started.Id);
            await TakeTaskFlowAsync(winner, 301);

            var events = await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision);
            AssertSiblingCancelled(events, started.Id, winner, loser);
            Assert.All(events, LifecycleEventBodies.AssertMatchesCatalog);
        });
    }

    [Fact]
    public async Task ConditionalBoundaryInterruptCancelsItsHostTask()
    {
        var model = ConditionalBoundaryModel();
        await WithCaptureAsync(model.Id, AllTypes, async revision =>
        {
            var started = await StartAsync(await CreateWorkflowAsync(model));
            var host = Assert.Single(await TasksAsync(started.Id));
            using (var response = await SendAsync(
                       HttpMethod.Patch,
                       $"/api/instances/{started.Id}/variables",
                       new UpdateInstanceVariablesRequest(
                           [new InstanceVariableWriteDto("approved", JsonSerializer.SerializeToElement(true))],
                           "Lifecycle capture test",
                           $"webhook-boundary-{Guid.NewGuid():N}"),
                       user: "operator",
                       roles: "admin"))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            var events = await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision);
            Assert.Equal(
                [LifecycleEventTypes.InstanceStarted, LifecycleEventTypes.TaskCreated,
                 LifecycleEventTypes.TaskCancelled, LifecycleEventTypes.InstanceCompleted],
                events.Select(item => item.Type));
            var cancelled = LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<TaskCancelledEventDataDto>>(events[2].Body);
            Assert.Equal(host.Id, cancelled.Data.UserTaskId);
            Assert.Equal("active", cancelled.Data.PreviousTaskStatus);
            Assert.All(events, LifecycleEventBodies.AssertMatchesCatalog);
        });
    }

    [Fact]
    public async Task ParallelMultiInstanceAnnouncesEachChild_AndQuorumAndParentInterruptCancelTheRest()
    {
        var model = VoteModel(MultiInstanceModes.Parallel);
        await WithCaptureAsync(model.Id, AllTypes, async revision =>
        {
            var workflowId = await CreateWorkflowAsync(model);

            // One task.created per child and none for the parent execution.
            var quorum = await StartAsync(workflowId);
            var votes = await TasksAsync(quorum.Id);
            Assert.Equal(3, votes.Count);
            var created = (await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision))
                .Where(item => item.Type == LifecycleEventTypes.TaskCreated)
                .Select(item => LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<TaskLifecycleEventDataDto>>(item.Body).Data)
                .ToArray();
            Assert.Equal(votes.Select(task => task.Id), created.Select(data => data.UserTaskId).Order());
            Assert.Equal([0, 1, 2], created.Select(data => data.ItemIndex!.Value).Order());
            Assert.All(created, data =>
            {
                Assert.Equal(votes[0].MultiInstanceExecutionId, data.MultiInstanceExecutionId);
                Assert.Equal("VOTE", data.NodeExternalId);
            });

            // The first vote meets the quorum; the set-based cancellation of the other children announces each one.
            var first = votes.Single(task => task.ItemIndex == 0);
            await TakeTaskFlowAsync(first.Id, 201);

            // A parent-level interrupt cancels every child.
            var interrupted = await StartAsync(workflowId);
            var children = await TasksAsync(interrupted.Id);
            using (var response = await SendAsync(
                       HttpMethod.Post,
                       $"/api/multi-instance-executions/{children[0].MultiInstanceExecutionId}/flows/203",
                       new TakeFlowRequest(null)))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            var events = await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision);
            var quorumEvents = events.Where(item => item.InstanceId == quorum.Id).ToArray();
            Assert.Equal([LifecycleEventTypes.TaskCreated, LifecycleEventTypes.TaskCompleted], TaskEventTypes(quorumEvents, first.Id));
            Assert.All(votes.Where(task => task.Id != first.Id), task => Assert.Equal(
                [LifecycleEventTypes.TaskCreated, LifecycleEventTypes.TaskCancelled],
                TaskEventTypes(quorumEvents, task.Id)));
            Assert.Equal([LifecycleEventTypes.InstanceStarted, LifecycleEventTypes.InstanceCompleted], InstanceEventTypes(quorumEvents));
            var interruptedEvents = events.Where(item => item.InstanceId == interrupted.Id).ToArray();
            Assert.Equal(3, children.Count);
            Assert.All(children, task => Assert.Equal(
                [LifecycleEventTypes.TaskCreated, LifecycleEventTypes.TaskCancelled],
                TaskEventTypes(interruptedEvents, task.Id)));
            Assert.Equal([LifecycleEventTypes.InstanceStarted, LifecycleEventTypes.InstanceCompleted], InstanceEventTypes(interruptedEvents));
            Assert.All(events.Where(item => item.Type == LifecycleEventTypes.TaskCancelled), item =>
            {
                var data = LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<TaskCancelledEventDataDto>>(item.Body).Data;
                Assert.Equal("active", data.PreviousTaskStatus);
                Assert.NotNull(data.MultiInstanceExecutionId);
                Assert.NotNull(data.ItemIndex);
            });
            Assert.All(events, LifecycleEventBodies.AssertMatchesCatalog);
        });
    }

    [Fact]
    public async Task CancellingASequentialMultiInstanceAnnouncesOnlyItsActiveChild()
    {
        var model = VoteModel(MultiInstanceModes.Sequential);
        await WithCaptureAsync(model.Id, AllTypes, async revision =>
        {
            var started = await StartAsync(await CreateWorkflowAsync(model));
            var children = await TasksAsync(started.Id);
            Assert.Equal(
                [UserTaskStatuses.Active, UserTaskStatuses.Pending, UserTaskStatuses.Pending],
                children.OrderBy(task => task.ItemIndex).Select(task => task.Status));
            Assert.All(children, task => Assert.Null(task.Assignee));
            using (var response = await SendAsync(HttpMethod.Post, $"/api/instances/{started.Id}/cancel"))
            {
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            }
            Assert.All(await TasksAsync(started.Id), task => Assert.Equal(UserTaskStatuses.Cancelled, task.Status));

            // The pending children without an assignee were never announced, so their cancellation emits nothing.
            var events = await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision);
            Assert.Equal(
                [LifecycleEventTypes.InstanceStarted, LifecycleEventTypes.TaskCreated,
                 LifecycleEventTypes.TaskCancelled, LifecycleEventTypes.InstanceCancelled],
                events.Select(item => item.Type));
            var active = children.Single(task => task.ItemIndex == 0);
            Assert.All(events.Where(item => item.SourceTable == "user_tasks"), item => Assert.Equal(active.Id, item.SourceId));
            Assert.All(events, LifecycleEventBodies.AssertMatchesCatalog);
        });
    }

    [Fact]
    public async Task ActivationAndAssignmentInOneUpdateRecordCreatedThenAssigned()
    {
        var model = UserTaskModel(BpmnFlowNodeTypes.EndEvent);
        await WithCaptureAsync(model.Id, AllTypes, async revision =>
        {
            var started = await StartAsync(await CreateWorkflowAsync(model));
            var task = Assert.Single(await TasksAsync(started.Id));

            // Raw SQL stands in for a producer that activates and assigns a pending item in one statement.
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            long pendingId;
            await using (var insert = new NpgsqlCommand("""
                INSERT INTO flowbit.user_tasks
                    ("InstanceId", "TokenId", "NodeId", "NodeName", "NodeExternalId", "Roles", "RequiresClaim",
                     "Status", "CreatedAt", "UpdatedAt")
                SELECT "InstanceId", "TokenId", "NodeId", "NodeName", "NodeExternalId", "Roles", FALSE,
                       'pending', now(), now()
                FROM flowbit.user_tasks WHERE "Id" = @id
                RETURNING "Id"
                """, connection))
            {
                insert.Parameters.AddWithValue("id", task.Id);
                pendingId = (long)(await insert.ExecuteScalarAsync())!;
            }
            await Execute(connection, null, "UPDATE flowbit.user_tasks SET \"Status\" = 'active', \"Assignee\" = 'grace', \"UpdatedAt\" = now() WHERE \"Id\" = @id", pendingId);

            var events = (await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision))
                .Where(item => item.SourceTable == "user_tasks" && item.SourceId == pendingId)
                .ToArray();
            Assert.Equal([LifecycleEventTypes.TaskCreated, LifecycleEventTypes.TaskAssigned], events.Select(item => item.Type));
            var created = LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<TaskLifecycleEventDataDto>>(events[0].Body);
            var assigned = LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<TaskAssignedEventDataDto>>(events[1].Body);
            Assert.Equal("active", created.Data.TaskStatus);
            Assert.Equal("active", assigned.Data.TaskStatus);
            Assert.Equal("assigned", assigned.Data.AssignmentOperation);
            Assert.Equal(events[0].OccurredAt, events[1].OccurredAt);
            Assert.All(events, item => Assert.Equal(["grace"], item.RecipientUserIds));
            Assert.All(events, LifecycleEventBodies.AssertMatchesCatalog);
        });
    }

    [Fact]
    public async Task AssignmentInheritanceAnnouncesTheInheritedAssignee()
    {
        var model = InheritanceModel();
        await WithCaptureAsync(model.Id, AllTypes, async revision =>
        {
            var started = await StartAsync(await CreateWorkflowAsync(model));
            var source = Assert.Single(await TasksAsync(started.Id));
            await TakeTaskFlowAsync(source.Id, 201, user: "casey", roles: "Worker");
            var inherited = (await TasksAsync(started.Id)).Single(task => task.NodeId == 3);
            Assert.Equal("casey", inherited.Assignee);

            var events = await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision);
            Assert.Equal([LifecycleEventTypes.TaskCreated, LifecycleEventTypes.TaskCompleted], TaskEventTypes(events, source.Id));
            Assert.Equal([LifecycleEventTypes.TaskCreated, LifecycleEventTypes.TaskAssigned], TaskEventTypes(events, inherited.Id));
            var assigned = Assert.Single(events, item => item.Type == LifecycleEventTypes.TaskAssigned);
            var data = LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<TaskAssignedEventDataDto>>(assigned.Body).Data;
            Assert.Equal("assigned", data.AssignmentOperation);
            Assert.Equal("active", data.TaskStatus);
            Assert.Equal("FOLLOW_UP", data.NodeExternalId);
            Assert.Equal(["casey"], assigned.RecipientUserIds);
            Assert.All(events, item => Assert.DoesNotContain("casey", item.Body, StringComparison.Ordinal));
            Assert.All(events, LifecycleEventBodies.AssertMatchesCatalog);
        });
    }

    [Fact]
    public async Task TaskDistributionAssignmentsAreAnnounced()
    {
        var model = DistributionModel();
        await WithCaptureAsync(model.Id, AllTypes, async revision =>
        {
            var started = await StartAsync(await CreateWorkflowAsync(model));
            var task = Assert.Single(await TasksAsync(started.Id));
            await DistributeAsync(model.Id, task.Id, "bob");
            await DistributeAsync(model.Id, task.Id, "carol");

            var events = await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision);
            Assert.Equal(
                [LifecycleEventTypes.TaskCreated, LifecycleEventTypes.TaskAssigned, LifecycleEventTypes.TaskAssigned],
                TaskEventTypes(events, task.Id));
            var assignments = events.Where(item => item.Type == LifecycleEventTypes.TaskAssigned).ToArray();
            Assert.Equal(
                ["assigned", "reassigned"],
                assignments.Select(item => LifecycleEventBodies
                    .Deserialize<LifecycleEventEnvelopeDto<TaskAssignedEventDataDto>>(item.Body).Data.AssignmentOperation));
            Assert.Equal(["bob"], assignments[0].RecipientUserIds);
            Assert.Equal(["carol"], assignments[1].RecipientUserIds);
            Assert.All(events, LifecycleEventBodies.AssertMatchesCatalog);
        });
    }

    [Fact]
    public async Task DelegatedCompletionIsAnnouncedAsTaskCompleted()
    {
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var owner = "owner-" + suffix;
        var deputy = "deputy-" + suffix;
        var model = UserTaskModel(BpmnFlowNodeTypes.EndEvent);
        model.FlowNodes.Single(node => node.Id == 2).AssigneeExpression = $"'{owner}'";
        await WithCaptureAsync(model.Id, AllTypes, async revision =>
        {
            var started = await StartAsync(await CreateWorkflowAsync(model));
            var task = Assert.Single(await TasksAsync(started.Id));
            using (var grant = await SendAsync(
                       HttpMethod.Post,
                       "/api/user-delegations",
                       new CreateUserDelegationRequest(
                           deputy,
                           [model.Id],
                           DateTimeOffset.UtcNow.AddMinutes(-10),
                           DateTimeOffset.UtcNow.AddDays(1)),
                       user: owner))
            {
                Assert.Equal(HttpStatusCode.Created, grant.StatusCode);
            }
            await TakeTaskFlowAsync(task.Id, 201, user: deputy);

            var events = await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision);
            Assert.Equal(
                [LifecycleEventTypes.TaskCreated, LifecycleEventTypes.TaskAssigned, LifecycleEventTypes.TaskCompleted],
                TaskEventTypes(events, task.Id));
            var completed = LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<TaskLifecycleEventDataDto>>(
                events.Single(item => item.Type == LifecycleEventTypes.TaskCompleted).Body);
            Assert.Equal(task.Id, completed.Data.UserTaskId);
            Assert.Equal("completed", completed.Data.TaskStatus);
            Assert.Single(events, item => item.Type == LifecycleEventTypes.DelegationGranted);
            Assert.Equal(LifecycleEventTypes.InstanceCompleted, events[^1].Type);
            Assert.All(events, item =>
            {
                Assert.DoesNotContain(owner, item.Body, StringComparison.Ordinal);
                Assert.DoesNotContain(deputy, item.Body, StringComparison.Ordinal);
            });
            Assert.All(events, LifecycleEventBodies.AssertMatchesCatalog);
        });
    }

    [Fact]
    public async Task DelegationBatchAnnouncesOneGrantPerFamily_AndRefusedOrRolledBackBatchesNothing()
    {
        var pendingFamily = UserTaskModel(BpmnFlowNodeTypes.EndEvent);
        var openFamily = UserTaskModel(BpmnFlowNodeTypes.EndEvent);
        var suffix = Guid.NewGuid().ToString("N")[..12];
        var delegator = "lead-" + suffix;
        var delegateUser = "cover-" + suffix;
        await WithCaptureAsync(null, [LifecycleEventTypes.DelegationGranted], async revision =>
        {
            await CreateWorkflowAsync(pendingFamily);
            await CreateWorkflowAsync(openFamily);
            using (var policy = await SendAsync(
                       HttpMethod.Put,
                       $"/api/user-delegation-policies/{pendingFamily.Id}",
                       new UpdateWorkflowDelegationPolicyRequest(true),
                       roles: "admin"))
            {
                Assert.Equal(HttpStatusCode.OK, policy.StatusCode);
            }

            var validFrom = DateTimeOffset.UtcNow.AddMinutes(-10);
            var validUntil = DateTimeOffset.UtcNow.AddDays(1);
            var request = new CreateUserDelegationRequest(
                delegateUser,
                [pendingFamily.Id, openFamily.Id],
                validFrom,
                validUntil);
            List<UserDelegationDto> grants;
            using (var response = await SendAsync(HttpMethod.Post, "/api/user-delegations", request, user: delegator))
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                grants = await ReadAsync<List<UserDelegationDto>>(response);
            }

            // An overlapping batch is refused inside its transaction, and a batch whose grants were inserted and
            // then rolled back was never committed: neither records anything.
            using (var overlap = await SendAsync(HttpMethod.Post, "/api/user-delegations", request, user: delegator))
            {
                Assert.Equal(HttpStatusCode.Conflict, overlap.StatusCode);
            }
            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
                var repository = scope.ServiceProvider.GetRequiredService<IUserDelegationRepository>();
                await using var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);
                var inserted = await repository.AddBatchAsync(
                    [
                        new NewUserDelegationRecord(delegator, "rolled-back-" + suffix, pendingFamily.Id, validFrom,
                            validUntil, true, UserDelegationAcceptanceStates.Pending, delegator, null, DateTimeOffset.UtcNow),
                        new NewUserDelegationRecord(delegator, "rolled-back-" + suffix, openFamily.Id, validFrom,
                            validUntil, false, UserDelegationAcceptanceStates.NotRequired, delegator, null, DateTimeOffset.UtcNow)
                    ],
                    CancellationToken.None);
                Assert.Equal(2, inserted.Count);
                // Disposing the transaction without committing rolls the inserted grants back.
            }

            var events = (await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision))
                .Where(item => item.WorkflowKey == pendingFamily.Id || item.WorkflowKey == openFamily.Id)
                .ToArray();
            Assert.Equal(2, events.Length);
            foreach (var (family, acceptanceState) in new[]
                     {
                         (pendingFamily.Id, UserDelegationAcceptanceStates.Pending),
                         (openFamily.Id, UserDelegationAcceptanceStates.NotRequired)
                     })
            {
                var grant = Assert.Single(grants, item => item.WorkflowKey == family);
                Assert.Equal(acceptanceState, grant.AcceptanceState);
                var captured = Assert.Single(events, item => item.WorkflowKey == family);
                var body = LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<DelegationGrantedEventDataDto>>(captured.Body);
                Assert.Equal(grant.Id, body.Data.DelegationId);
                Assert.Equal(acceptanceState, body.Data.AcceptanceState);
                Assert.Null(body.InstanceId);
                Assert.Null(body.WorkflowDefinitionId);
                Assert.Null(body.WorkflowVersion);
                Assert.Equal([grant.Delegate], captured.RecipientUserIds);
            }
            Assert.All(events, LifecycleEventBodies.AssertMatchesCatalog);
        });
    }

    [Fact]
    public async Task BlockedCallAdmissionIncidentReportsItsCallNodeAndToken()
    {
        var childKey = "webhook-call-child-" + Guid.NewGuid().ToString("N");
        var parent = CallParentModel(childKey);
        await WithCaptureAsync(parent.Id, [LifecycleEventTypes.IncidentOpened], async revision =>
        {
            await CreateWorkflowAsync(CallActivityHarness.WaitingChild(childKey));
            var workflowId = await CreateWorkflowAsync(parent);

            // With one open call allowed per root, the second call's callStart job is enqueued as an incident
            // (WorkflowJobRepository.EnqueueIncidentAsync) in the start transaction.
            await using var host = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<CallActivityOptions>();
                services.AddSingleton(new CallActivityOptions { MaxDepth = 8, MaxOpenInvocationsPerRoot = 1 });
            }));
            long instanceId;
            using (var client = host.CreateClient())
            using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/instances?detail=full")
            {
                Content = JsonContent.Create(new StartInstanceRequest(workflowId, null, null, null), options: JsonOptions)
            })
            {
                ApiTestAuth.Authorize(request, "test-admin", "User");
                using var response = await client.SendAsync(request);
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                instanceId = (await ReadAsync<InstanceDetailDto>(response)).Id;
            }

            try
            {
                await using var db = fixture.CreateDbContext();
                var incident = await db.WorkflowIncidents.AsNoTracking().SingleAsync(item => item.InstanceId == instanceId);
                var job = await db.WorkflowJobs.AsNoTracking().SingleAsync(item => item.Id == incident.JobId);
                Assert.Equal(CallFailureCodes.CapacityExceeded, incident.Type);
                Assert.Equal(WorkflowJobKinds.CallStart, job.Kind);

                var opened = Assert.Single(await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision));
                var body = LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<IncidentOpenedEventDataDto>>(opened.Body);
                Assert.Equal(instanceId, body.InstanceId);
                Assert.Equal(workflowId, body.WorkflowDefinitionId);
                Assert.Equal(incident.Id, body.Data.IncidentId);
                Assert.Equal(job.Id, body.Data.JobId);
                Assert.NotNull(body.Data.ExecutionTokenId);
                Assert.Equal(job.TokenId, body.Data.ExecutionTokenId);
                Assert.Equal(incident.NodeId, body.Data.NodeId);
                Assert.Equal(incident.NodeId == 11 ? "CALL_FIRST" : "CALL_SECOND", body.Data.NodeExternalId);
                LifecycleEventBodies.AssertMatchesCatalog(opened);
            }
            finally
            {
                // Other suites lease one specific job at a time; move this test's admitted call out of their way.
                await WebhookTestDatabase.ExecuteAsync(
                    fixture.DataSource,
                    "UPDATE flowbit.workflow_jobs SET \"DueAt\" = now() + interval '10 years' WHERE \"InstanceId\" = @id AND \"Status\" IN ('queued', 'retry')",
                    ("id", instanceId));
            }
        });
    }

    [Fact]
    public async Task VersionChangeBatchIncidentReportsNoNode()
    {
        var model = UserTaskModel(BpmnFlowNodeTypes.EndEvent);
        await WithCaptureAsync(model.Id, [LifecycleEventTypes.IncidentOpened], async revision =>
        {
            var sourceId = await CreateWorkflowAsync(model);
            var started = await StartAsync(sourceId);
            var targetId = await CreateVersionAsync(sourceId, model);
            long jobId;
            using (var created = await SendAsync(
                       HttpMethod.Post,
                       "/api/instance-version-change-batches",
                       new CreateInstanceVersionChangeBatchRequest(
                           sourceId,
                           targetId,
                           "Lifecycle capture test",
                           new InstanceVersionChangeBatchSelectionDto(
                               InstanceVersionChangeBatchSelectionModes.Explicit,
                               [started.Id],
                               null,
                               null),
                           $"webhook-batch-{Guid.NewGuid():N}"),
                       roles: "admin"))
            {
                Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
                jobId = (await ReadAsync<InstanceVersionChangeBatchDetailDto>(created)).PreparationJobId!.Value;
            }

            // The preparation job's final attempt lost its lease, as when its Worker stops; the next lease opens the
            // set-based lease_exhausted incident.
            await using (var expire = fixture.CreateDbContext())
            {
                Assert.Equal(1, await expire.WorkflowJobs
                    .Where(job => job.Id == jobId && job.Status == WorkflowJobStatuses.Queued)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(job => job.Status, WorkflowJobStatuses.Running)
                        .SetProperty(job => job.AttemptCount, job => job.MaxAttempts)
                        .SetProperty(job => job.WorkerId, "lifecycle-capture-tests")
                        .SetProperty(job => job.LeaseToken, Guid.NewGuid())
                        .SetProperty(job => job.LeaseGeneration, job => job.LeaseGeneration + 1)
                        .SetProperty(job => job.LeaseExpiresAt, LongExpiredLease)
                        .SetProperty(job => job.HeartbeatAt, LongExpiredLease)));
            }
            await OpenExhaustedLeaseIncidentsThroughLeasingAsync();

            await using var db = fixture.CreateDbContext();
            var incident = await db.WorkflowIncidents.AsNoTracking().SingleAsync(item => item.JobId == jobId);
            Assert.Equal("lease_exhausted", incident.Type);
            // The job, and the incident copying it, name the initial start event only as an anchor.
            Assert.Equal(model.InitialEventId, incident.NodeId);

            var opened = Assert.Single(await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision));
            var body = LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<IncidentOpenedEventDataDto>>(opened.Body);
            Assert.Null(body.InstanceId);
            Assert.Equal(sourceId, body.WorkflowDefinitionId);
            Assert.Equal(incident.Id, body.Data.IncidentId);
            Assert.Equal(jobId, body.Data.JobId);
            Assert.Null(body.Data.ExecutionTokenId);
            Assert.Null(body.Data.NodeId);
            Assert.Null(body.Data.NodeExternalId);
            LifecycleEventBodies.AssertMatchesCatalog(opened);
        });
    }

    [Fact]
    public void EveryJobKindIsClassifiedForIncidentNodes()
    {
        var kinds = typeof(WorkflowJobKinds)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Order(StringComparer.Ordinal);
        // A new job kind must be classified in JobKindReportsNode. The incident test that covers every kind
        // then fails until capture_workflow_incident_lifecycle() agrees with that classification.
        Assert.Equal(kinds, JobKindReportsNode.Keys.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task IncidentsReportANodeOnlyForJobKindsWithAnAuthoredNode()
    {
        var model = UserTaskModel(BpmnFlowNodeTypes.EndEvent);
        await WithCaptureAsync(model.Id, [LifecycleEventTypes.IncidentOpened], async revision =>
        {
            var workflowId = await CreateWorkflowAsync(model);
            var kindsByJob = new Dictionary<long, string>();
            foreach (var kind in JobKindReportsNode.Keys)
            {
                kindsByJob[await InsertJobAsync(workflowId, model.Id, kind, nodeId: 2)] = kind;
            }
            await InsertLeaseExhaustedIncidentsAsync(kindsByJob.Keys);

            var events = await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision);
            var actual = events
                .Select(item => LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<IncidentOpenedEventDataDto>>(item.Body).Data)
                .Select(data => (Kind: kindsByJob[data.JobId], data.NodeId, data.NodeExternalId))
                .OrderBy(item => item.Kind, StringComparer.Ordinal);
            var expected = JobKindReportsNode
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(pair => (Kind: pair.Key, NodeId: pair.Value ? 2 : (int?)null, NodeExternalId: pair.Value ? "REVIEW" : null));
            Assert.Equal(expected, actual);
            Assert.All(events, LifecycleEventBodies.AssertMatchesCatalog);
        });
    }

    [Fact]
    public async Task IncidentNodeExternalIdIsReportedUpToTheRuntimeLimitOfThreeHundredCharacters()
    {
        // Each control character escapes to six bytes, so this is the largest external id a body can carry.
        var largest = new string('\u0001', 300);
        var model = UserTaskModel(BpmnFlowNodeTypes.EndEvent);
        model.FlowNodes.Single(node => node.Id == 1).ExternalId = largest;
        model.FlowNodes.Single(node => node.Id == 3).ExternalId = new string('x', 301);
        await WithCaptureAsync(model.Id, [LifecycleEventTypes.IncidentOpened], async revision =>
        {
            var workflowId = await CreateWorkflowAsync(model);
            var kept = await InsertJobAsync(workflowId, model.Id, WorkflowJobKinds.TimerStart, nodeId: 1);
            var dropped = await InsertJobAsync(workflowId, model.Id, WorkflowJobKinds.AsyncBefore, nodeId: 3);
            await InsertLeaseExhaustedIncidentsAsync([kept, dropped]);

            var events = await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision);
            var bodies = events.ToDictionary(
                item => LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<IncidentOpenedEventDataDto>>(item.Body).Data.JobId,
                item => item);
            var keptBody = LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<IncidentOpenedEventDataDto>>(bodies[kept].Body);
            Assert.Equal(1, keptBody.Data.NodeId);
            Assert.Equal(largest, keptBody.Data.NodeExternalId);
            Assert.InRange(Encoding.UTF8.GetByteCount(bodies[kept].Body), 6 * 300, 4 * 1024);
            var droppedBody = LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<IncidentOpenedEventDataDto>>(bodies[dropped].Body);
            Assert.Equal(3, droppedBody.Data.NodeId);
            Assert.Null(droppedBody.Data.NodeExternalId);
            Assert.All(events, LifecycleEventBodies.AssertMatchesCatalog);
        });
    }

    [Fact]
    public async Task IncidentsOfANodeWithAnOverlongExternalIdCommitWithoutIt()
    {
        var suffix = Guid.NewGuid().ToString("N");
        BusinessCalendarDto calendar;
        using (var response = await SendAsync(
                   HttpMethod.Post,
                   "/api/business-calendars",
                   new CreateBusinessCalendarRequest(
                       $"webhook-monday-{suffix}",
                       "Etc/UTC",
                       new BusinessCalendarWeekDto(Monday: BusinessCalendarApiTests.Periods("09:00-10:00"))),
                   roles: "admin"))
        {
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
            calendar = await ReadAsync<BusinessCalendarDto>(response);
        }
        var model = new WorkflowModel
        {
            Id = "webhook-overlong-external-id-" + suffix,
            Name = "Webhook overlong external id " + suffix,
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Scheduled start",
                    // Definitions do not limit external ids, so one can exceed the 16 KiB event body bound.
                    ExternalId = new string('x', 20_000),
                    Type = BpmnFlowNodeTypes.TimerStartEvent,
                    // One working hour a week cannot count 30,000 working days, so the start cannot be scheduled.
                    Timer = new TimerDefinitionModel { TimeDuration = "P30000D", Calendar = calendar.Name }
                },
                new FlowNodeModel { Id = 2, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows = [new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 }]
        };
        await WithCaptureAsync(model.Id, [LifecycleEventTypes.IncidentOpened], async revision =>
        {
            var workflowId = await CreateWorkflowAsync(model);
            try
            {
                // The Worker's reconciliation opens the schedule incident through EnqueueIncidentAsync, in the one
                // transaction that reconciles every timer-start family of its batch.
                await ReconcileTimerStartsAsync();
                // The set-based lease-exhaustion insert runs inside job leasing.
                await InsertJobAsync(workflowId, model.Id, WorkflowJobKinds.TimerStart, nodeId: 1, exhaustedLease: true);
                await OpenExhaustedLeaseIncidentsThroughLeasingAsync();

                await using var db = fixture.CreateDbContext();
                Assert.Equal(
                    [WorkflowIncidentTypes.TimerScheduleFailed, "lease_exhausted"],
                    await db.WorkflowIncidents.AsNoTracking()
                        .Where(item => item.WorkflowDefinitionId == workflowId)
                        .OrderBy(item => item.Id)
                        .Select(item => item.Type)
                        .ToListAsync());
                var events = await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revision);
                Assert.Equal(2, events.Count);
                Assert.All(events, item =>
                {
                    var body = LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<IncidentOpenedEventDataDto>>(item.Body);
                    Assert.Null(body.InstanceId);
                    Assert.Null(body.Data.ExecutionTokenId);
                    Assert.Equal(1, body.Data.NodeId);
                    Assert.Null(body.Data.NodeExternalId);
                    Assert.True(Encoding.UTF8.GetByteCount(item.Body) < 1024, item.Body);
                    LifecycleEventBodies.AssertMatchesCatalog(item);
                });
            }
            finally
            {
                // Another default ends the schedule incident, so later reconciliation passes leave this family alone.
                var plainStart = new WorkflowModel
                {
                    Id = model.Id,
                    Name = model.Name,
                    InitialEventId = 1,
                    FlowNodes =
                    [
                        new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                        new FlowNodeModel { Id = 2, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
                    ],
                    SequenceFlows = [new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 }]
                };
                var laterId = await CreateVersionAsync(workflowId, plainStart);
                using (var setDefault = await SendAsync(HttpMethod.Post, $"/api/workflows/{laterId}/set-default", roles: "admin"))
                {
                    Assert.True(setDefault.IsSuccessStatusCode, await setDefault.Content.ReadAsStringAsync());
                }
                await ReconcileTimerStartsAsync();
            }
        });
    }

    private static readonly DateTimeOffset LongExpiredLease = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private async Task WithCaptureAsync(string? workflowKey, IReadOnlyList<string> types, Func<long, Task> body)
    {
        var (subscriptionId, revisionId) = await WebhookTestDatabase.CreateSubscriptionAsync(fixture.DataSource, types, workflowKey);
        try
        {
            await WebhookTestDatabase.SetCaptureAsync(fixture.DataSource, true);
            await body(revisionId);
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscriptionId, "archived");
            await WebhookTestDatabase.RestoreCaptureAsync(fixture.DataSource);
        }
    }

    private static WorkflowModel UserTaskModel(string terminalType)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "webhook-capture-" + suffix,
            Name = "Webhook capture " + suffix,
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Review", ExternalId = "REVIEW", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Done",
                    Type = terminalType,
                    ErrorCode = terminalType == BpmnFlowNodeTypes.ErrorEndEvent ? "REVIEW_FAILED" : null
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, Name = "Finish", SourceRef = 2, TargetRef = 3 }
            ]
        };
    }

    private async Task<long> CreateWorkflowAsync(WorkflowModel model)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await ReadAsync<WorkflowDetailDto>(response)).Id;
    }

    private async Task<InstanceDetailDto> StartAsync(long workflowId, bool expectRunning = true, int expectedNode = 2)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, null),
            roles: "User,Manager");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var detail = await ReadAsync<InstanceDetailDto>(response);
        if (expectRunning)
        {
            Assert.Equal("running", detail.Status);
            Assert.Equal(expectedNode, detail.CurrentNodeId);
        }
        return detail;
    }

    private async Task<UserTaskDto> SingleActiveTaskAsync(long instanceId, string user = "test-admin", string roles = "User")
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            $"/api/instances/{instanceId}/user-tasks?status=active&page=1&pageSize=20",
            user: user,
            roles: roles);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return Assert.Single((await ReadAsync<PagedResult<UserTaskDto>>(response)).Items);
    }

    private async Task TakeTaskFlowAsync(long taskId, int flowId, string user = "tester", string roles = "User")
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{taskId}/flows/{flowId}",
            new TakeFlowRequest(null),
            user,
            roles);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task AssignAsync(long taskId, string actor)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/user-tasks/{taskId}/assign",
            new AssignUserTaskRequest(actor, await TaskUpdatedAtAsync(taskId), null),
            roles: "Manager");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<DateTimeOffset> TaskUpdatedAtAsync(long taskId)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT \"UpdatedAt\" FROM flowbit.user_tasks WHERE \"Id\" = @id", connection);
        command.Parameters.AddWithValue("id", taskId);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return reader.GetFieldValue<DateTimeOffset>(0);
    }

    private static async Task Execute(NpgsqlConnection connection, NpgsqlTransaction? transaction, string sql, long? id)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        if (id is not null)
        {
            command.Parameters.AddWithValue("id", id.Value);
        }
        await command.ExecuteNonQueryAsync();
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "test-admin",
        string roles = "User")
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, user, roles.Split(','));
        return await fixture.Client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("Response body was empty.");

    private async Task<long> CreateVersionAsync(long workflowId, WorkflowModel model)
    {
        using var response = await SendAsync(HttpMethod.Put, $"/api/workflows/{workflowId}", new UpdateWorkflowRequest(model, true));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await ReadAsync<WorkflowDetailDto>(response)).Id;
    }

    /// <summary>Every work item of an instance in id order, read from the database.</summary>
    private async Task<IReadOnlyList<UserTaskEntity>> TasksAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.UserTasks.AsNoTracking()
            .Where(task => task.InstanceId == instanceId)
            .OrderBy(task => task.Id)
            .ToListAsync();
    }

    /// <summary>The active work items of the two parallel branches: node 3 and node 4.</summary>
    private async Task<(long First, long Second)> BranchTasksAsync(long instanceId)
    {
        var tasks = await TasksAsync(instanceId);
        Assert.Equal(2, tasks.Count);
        Assert.All(tasks, task => Assert.Equal(UserTaskStatuses.Active, task.Status));
        return (tasks.Single(task => task.NodeId == 3).Id, tasks.Single(task => task.NodeId == 4).Id);
    }

    private static string[] TaskEventTypes(IEnumerable<CapturedLifecycleEvent> events, long userTaskId) =>
        [.. events.Where(item => item.SourceTable == "user_tasks" && item.SourceId == userTaskId).Select(item => item.Type)];

    private static string[] InstanceEventTypes(IEnumerable<CapturedLifecycleEvent> events) =>
        [.. events.Where(item => item.SourceTable == "workflow_instances").Select(item => item.Type)];

    /// <summary>The acting branch completed its task, the sibling branch's task was cancelled, and the instance completed.</summary>
    private static void AssertSiblingCancelled(
        IReadOnlyList<CapturedLifecycleEvent> events,
        long instanceId,
        long actingTaskId,
        long siblingTaskId)
    {
        var instanceEvents = events.Where(item => item.InstanceId == instanceId).ToArray();
        Assert.Equal([LifecycleEventTypes.TaskCreated, LifecycleEventTypes.TaskCompleted], TaskEventTypes(instanceEvents, actingTaskId));
        Assert.Equal([LifecycleEventTypes.TaskCreated, LifecycleEventTypes.TaskCancelled], TaskEventTypes(instanceEvents, siblingTaskId));
        Assert.Equal([LifecycleEventTypes.InstanceStarted, LifecycleEventTypes.InstanceCompleted], InstanceEventTypes(instanceEvents));
        Assert.Equal(LifecycleEventTypes.InstanceCompleted, instanceEvents[^1].Type);
        var cancelled = LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<TaskCancelledEventDataDto>>(
            instanceEvents.Single(item => item.Type == LifecycleEventTypes.TaskCancelled).Body);
        Assert.Equal(siblingTaskId, cancelled.Data.UserTaskId);
        Assert.Equal("TASK_B", cancelled.Data.NodeExternalId);
        Assert.Equal("cancelled", cancelled.Data.TaskStatus);
        Assert.Equal("active", cancelled.Data.PreviousTaskStatus);
    }

    private async Task DistributeAsync(string workflowKey, long taskId, string actor)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"/api/task-distribution/workflows/{Uri.EscapeDataString(workflowKey)}/tasks/{taskId}/assign")
        {
            Content = JsonContent.Create(
                new AssignUserTaskRequest(actor, await TaskUpdatedAtAsync(taskId), "Lifecycle capture test"),
                options: JsonOptions)
        };
        request.Headers.Add("X-Client-Id", DistributorClientId);
        request.Headers.Add("X-Client-Secret", DistributorClientSecret);
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    /// <summary>
    /// Inserts a job of the given kind at an authored node. By default it already waits in an incident, as the
    /// set-based statement leaves it; with <paramref name="exhaustedLease"/> it is running on a final attempt
    /// whose lease expired long ago, so the next lease opens its lease_exhausted incident.
    /// </summary>
    private async Task<long> InsertJobAsync(
        long workflowId,
        string workflowKey,
        string kind,
        int nodeId,
        bool exhaustedLease = false)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO flowbit.workflow_jobs
                ("InstanceId", "WorkflowDefinitionId", "WorkflowKey", "ActivationId", "NodeId", "NodeName", "NodeType",
                 "Kind", "QueueClass", "Phase", "Status", "Priority", "AttemptCount", "MaxAttempts", "FailureHandling",
                 "RetryDelays", "DueAt", "WorkerId", "LeaseToken", "LeaseGeneration", "LeaseExpiresAt", "HeartbeatAt",
                 "StartedAt", "CreatedAt", "UpdatedAt")
            VALUES (NULL, @definition, @key, gen_random_uuid(), @node, 'Lifecycle capture node', 'task',
                    @kind, 'control', 'execute', CASE WHEN @exhausted THEN 'running' ELSE 'incident' END, 0, 4, 4,
                    'boundaryFirst', '{}', now(),
                    CASE WHEN @exhausted THEN 'lifecycle-capture-tests' END,
                    CASE WHEN @exhausted THEN gen_random_uuid() END,
                    CASE WHEN @exhausted THEN 1 ELSE 0 END,
                    CASE WHEN @exhausted THEN @expired END,
                    CASE WHEN @exhausted THEN @expired END,
                    CASE WHEN @exhausted THEN now() END,
                    now(), now())
            RETURNING "Id"
            """, connection);
        command.Parameters.AddWithValue("definition", workflowId);
        command.Parameters.AddWithValue("key", workflowKey);
        command.Parameters.AddWithValue("node", nodeId);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("exhausted", exhaustedLease);
        command.Parameters.AddWithValue("expired", LongExpiredLease);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    /// <summary>
    /// Raw SQL with the set-based statement that WorkflowJobRepository.OpenExhaustedLeaseIncidentsAsync runs while
    /// leasing, limited to the given jobs.
    /// </summary>
    private async Task InsertLeaseExhaustedIncidentsAsync(IReadOnlyCollection<long> jobIds)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO flowbit.workflow_incidents
                ("JobId", "OriginalJobId", "InstanceId", "WorkflowDefinitionId", "WorkflowKey",
                 "NodeId", "NodeName", "Type", "Status", "Summary", "Details",
                 "CreatedAt", "UpdatedAt")
            SELECT j."Id", j."Id", j."InstanceId", j."WorkflowDefinitionId", j."WorkflowKey",
                   j."NodeId", j."NodeName", 'lease_exhausted', 'open',
                   'Workflow job exhausted its attempts after a worker lease expired.',
                   'The external operation may have occurred. Review downstream idempotency before retrying.',
                   clock_timestamp(), clock_timestamp()
            FROM flowbit.workflow_jobs AS j
            WHERE j."Id" = ANY(@ids)
            ON CONFLICT ("JobId", "Status") WHERE "Status" = 'open' DO NOTHING
            """, connection);
        command.Parameters.AddWithValue("ids", jobIds.ToArray());
        Assert.Equal(jobIds.Count, await command.ExecuteNonQueryAsync());
    }

    /// <summary>
    /// Leases through the real repository, which first opens lease_exhausted incidents for running jobs whose final
    /// attempt lost its lease. It leases no activity job; a due control job left by another suite may be leased.
    /// </summary>
    private async Task OpenExhaustedLeaseIncidentsThroughLeasingAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().LeaseRunnableAsync(
            new WorkflowJobLeaseRequest(
                $"lifecycle-capture:{Guid.NewGuid():N}",
                MaxCount: 1,
                MaxActivityCount: 0,
                MaxPerInstance: 1,
                LeaseDuration: TimeSpan.FromMinutes(1)),
            CancellationToken.None);
    }

    /// <summary>One pass of the Worker's timer-start reconciliation, as BusinessCalendarRuntimeTests runs it.</summary>
    private async Task ReconcileTimerStartsAsync()
    {
        using var telemetry = new WorkerTelemetry();
        var service = new TimerStartReconciliationService(
            fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new WorkerOptions { TimerStartReconcileBatchSize = 1000 },
            TimeProvider.System,
            telemetry,
            NullLogger<TimerStartReconciliationService>.Instance);
        var reconcile = typeof(TimerStartReconciliationService).GetMethod(
                "ReconcileAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ReconcileAsync was not found.");
        await Assert.IsAssignableFrom<Task>(reconcile.Invoke(service, [CancellationToken.None]));
    }

    /// <summary>
    /// Two parallel branches, each a user task: node 3 (TASK_A) can finish, interrupt the split's scope, or reach a
    /// terminate end; node 4 (TASK_B) can only finish.
    /// </summary>
    private static WorkflowModel ParallelTasksModel()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "webhook-parallel-" + suffix,
            Name = "Webhook parallel " + suffix,
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Fork", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel { Id = 3, Name = "First", ExternalId = "TASK_A", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 4, Name = "Second", ExternalId = "TASK_B", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 5, Name = "First done", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 6, Name = "Second done", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel
                {
                    Id = 7,
                    Name = "Interrupt the review",
                    Type = BpmnFlowNodeTypes.ScopedInterruptEvent,
                    GatewayRef = 2
                },
                new FlowNodeModel { Id = 8, Name = "Interrupted", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 9, Name = "Terminate", Type = BpmnFlowNodeTypes.TerminateEndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, Name = "Begin", SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, Name = "First", SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 202, Name = "Second", SourceRef = 2, TargetRef = 4 },
                new SequenceFlowModel { Id = 301, Name = "Finish", SourceRef = 3, TargetRef = 5 },
                new SequenceFlowModel { Id = 302, Name = "Interrupt", SourceRef = 3, TargetRef = 7 },
                new SequenceFlowModel { Id = 303, Name = "Terminate", SourceRef = 3, TargetRef = 9 },
                new SequenceFlowModel { Id = 401, Name = "Finish", SourceRef = 4, TargetRef = 6 },
                new SequenceFlowModel { Id = 701, Name = "Continue", SourceRef = 7, TargetRef = 8 }
            ]
        };
    }

    /// <summary>Two parallel user tasks meet at an exclusive merge whose first arrival cancels the other branch.</summary>
    private static WorkflowModel CancellingJoinModel()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "webhook-cancelling-join-" + suffix,
            Name = "Webhook cancelling join " + suffix,
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Fork", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel { Id = 3, Name = "First", ExternalId = "TASK_A", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 4, Name = "Second", ExternalId = "TASK_B", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel
                {
                    Id = 5,
                    Name = "First arrival wins",
                    Type = BpmnFlowNodeTypes.ExclusiveGateway,
                    JoinCancellation = new JoinCancellationModel { GatewayRef = 2 }
                },
                new FlowNodeModel { Id = 6, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, Name = "Begin", SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, Name = "First", SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 202, Name = "Second", SourceRef = 2, TargetRef = 4 },
                new SequenceFlowModel { Id = 301, Name = "Finish", SourceRef = 3, TargetRef = 5 },
                new SequenceFlowModel { Id = 401, Name = "Finish", SourceRef = 4, TargetRef = 5 },
                new SequenceFlowModel { Id = 501, Name = "Joined", SourceRef = 5, TargetRef = 6 }
            ]
        };
    }

    /// <summary>A user task with an interrupting atomic conditional boundary on <c>approved == true</c>.</summary>
    private static WorkflowModel ConditionalBoundaryModel()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "webhook-conditional-boundary-" + suffix,
            Name = "Webhook conditional boundary " + suffix,
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "approved",
                    DataType = WorkflowVariableTypes.Boolean,
                    DefaultValue = JsonSerializer.SerializeToElement(false)
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Review", ExternalId = "REVIEW", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 3, Name = "Reviewed", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel
                {
                    Id = 4,
                    Name = "Approved elsewhere",
                    Type = BpmnFlowNodeTypes.ConditionalBoundaryEvent,
                    AttachedToRef = 2,
                    CancelActivity = true,
                    Conditional = new ConditionalDefinitionModel
                    {
                        Condition = "approved == true",
                        DeliveryMode = ConditionalEventDeliveryModes.Atomic
                    }
                },
                new FlowNodeModel { Id = 5, Name = "Interrupted", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, Name = "Finish", SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 5 }
            ]
        };
    }

    /// <summary>
    /// Three votes on node 2 (VOTE): the first approval meets the quorum, the engine-only default is the fallback,
    /// and flow 203 is a parent-level interrupt.
    /// </summary>
    private static WorkflowModel VoteModel(string mode)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "webhook-votes-" + suffix,
            Name = "Webhook votes " + suffix,
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "results",
                    DataType = WorkflowVariableTypes.Json,
                    DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>())
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Vote",
                    ExternalId = "VOTE",
                    Type = BpmnFlowNodeTypes.UserTask,
                    MultiInstance = new MultiInstanceModel
                    {
                        Mode = mode,
                        Source = MultiInstanceSources.Cardinality,
                        CardinalityExpression = "3",
                        CompletionEvaluation = MultiInstanceCompletionEvaluations.AfterEach,
                        ResultVariable = "results"
                    }
                },
                new FlowNodeModel { Id = 3, Name = "Decided", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 4, Name = "Undecided", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel { Id = 5, Name = "Stopped", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, Name = "Begin", SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel
                {
                    Id = 201,
                    Name = "Approve",
                    SourceRef = 2,
                    TargetRef = 3,
                    CompletionCondition = "CountFlow(201) >= 1",
                    CompletionPriority = 1
                },
                new SequenceFlowModel
                {
                    Id = 202,
                    Name = "No result",
                    SourceRef = 2,
                    TargetRef = 4,
                    IsDefault = true,
                    IsSelectable = false
                },
                new SequenceFlowModel
                {
                    Id = 203,
                    Name = "Stop voting",
                    SourceRef = 2,
                    TargetRef = 5,
                    CancelRemainingInstances = true
                }
            ]
        };
    }

    /// <summary>Node 3 (FOLLOW_UP) requires an assignee and inherits the actor who completed node 2.</summary>
    private static WorkflowModel InheritanceModel()
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "webhook-inheritance-" + suffix,
            Name = "Webhook inheritance " + suffix,
            InitialEventId = 1,
            TaskAssignmentRoles = ["Manager"],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Shared review",
                    ExternalId = "SHARED_REVIEW",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["Worker"]
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Follow up",
                    ExternalId = "FOLLOW_UP",
                    Type = BpmnFlowNodeTypes.UserTask,
                    Roles = ["Worker"],
                    RequiresAssignment = true,
                    AssignmentMode = AssignmentModes.Previous
                },
                new FlowNodeModel { Id = 4, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, Name = "Continue", SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 301, Name = "Complete", SourceRef = 3, TargetRef = 4 }
            ]
        };
    }

    /// <summary>One user task in a family whose tasks an external distributor assigns.</summary>
    private static WorkflowModel DistributionModel()
    {
        var model = UserTaskModel(BpmnFlowNodeTypes.EndEvent);
        model.TaskDistribution = new TaskDistributionModel
        {
            ClientId = DistributorClientId,
            ClientSecret = DistributorClientSecret
        };
        return model;
    }

    /// <summary>A parallel split into two calls of the same waiting child (CALL_FIRST and CALL_SECOND).</summary>
    private static WorkflowModel CallParentModel(string childKey)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "webhook-call-parent-" + suffix,
            Name = "Webhook call parent " + suffix,
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 10, Name = "Fork", Type = BpmnFlowNodeTypes.ParallelGateway },
                CallNode(11, "CALL_FIRST", childKey),
                CallNode(12, "CALL_SECOND", childKey),
                new FlowNodeModel { Id = 14, Name = "Join", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel { Id = 15, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 10 },
                new SequenceFlowModel { Id = 1001, SourceRef = 10, TargetRef = 11 },
                new SequenceFlowModel { Id = 1002, SourceRef = 10, TargetRef = 12 },
                new SequenceFlowModel { Id = 1101, SourceRef = 11, TargetRef = 14 },
                new SequenceFlowModel { Id = 1201, SourceRef = 12, TargetRef = 14 },
                new SequenceFlowModel { Id = 1401, SourceRef = 14, TargetRef = 15 }
            ]
        };
    }

    private static FlowNodeModel CallNode(int id, string externalId, string targetKey) => new()
    {
        Id = id,
        Name = $"Call {id}",
        ExternalId = externalId,
        Type = BpmnFlowNodeTypes.CallActivity,
        CallActivity = new CallActivityModel { WorkflowKey = targetKey }
    };
}
