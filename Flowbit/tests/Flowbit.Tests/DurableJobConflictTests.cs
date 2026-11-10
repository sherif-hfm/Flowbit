using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Scripting;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// A conflict ends a job quietly only when the job has lost its work. Otherwise nothing else can move
/// that work, so the job has to stay visible as an incident an operator can retry.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class DurableJobConflictTests(PostgresApiFixture fixture)
{
    private const int Host = 10;
    private const int NormalEnd = 2;
    private const string CancellingJoinFailure = "could not resolve an active activation";

    // The definition is inserted directly, so no catalog binding row refuses the archive.
    // A_validated_definition_reaches_the_same_state_through_reactivation covers the API route.
    [Fact]
    public async Task A_key_archived_while_a_script_runs_opens_an_incident_that_retries_after_reactivation()
    {
        var key = Key();
        var sharedKey = SharedKey();
        try
        {
            await CreateSharedVariableAsync(sharedKey);
            var gate = new ScriptGate();
            await using var gatedHost = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IScriptEvaluator>();
                services.AddSingleton<IScriptEvaluator>(_ => new GatedScriptEvaluator(
                    new JintScriptEvaluator(new ScriptOptions(), NullLogger<JintScriptEvaluator>.Instance),
                    gate));
            }));
            var instanceId = await InsertAndStartAsync(key, ScriptDefinition(key, sharedKey));
            var jobId = await SingleJobIdAsync(instanceId, WorkflowJobKinds.AsyncBefore);

            await using (var scope = gatedHost.Services.CreateAsyncScope())
            {
                var lease = await LeaseAsync(jobId);
                var processing = scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>()
                    .ProcessAsync(lease, CancellationToken.None);
                Assert.True(gate.Entered.Wait(TimeSpan.FromSeconds(20)));
                await ArchiveAsync(sharedKey);
                gate.Release.Set();
                await processing;
            }

            var incident = await ConflictIncidentAsync(jobId);
            Assert.Contains(sharedKey, incident.Details);
            Assert.Contains("archived", incident.Details);
            await AssertTokenStillWaitsAsync(instanceId, jobId);
            Assert.Equal("-1", await SharedValueAsync(sharedKey));

            // Retrying too early reports the same cause again and strands nothing.
            await RetryAsync(incident.Id);
            await ProcessAsync(jobId);
            var second = await ConflictIncidentAsync(jobId);
            Assert.NotEqual(incident.Id, second.Id);
            await AssertTokenStillWaitsAsync(instanceId, jobId);

            await ReactivateAsync(sharedKey);
            await RetryAsync(second.Id);
            await ProcessAsync(jobId);

            await AssertCompletedThroughAsync(instanceId, NormalEnd);
            Assert.Equal("5", await SharedValueAsync(sharedKey));
            await AssertNoOpenIncidentAsync(jobId);
        }
        finally
        {
            await DeleteJobsAsync(key);
        }
    }

    [Theory]
    [InlineData("typed-output-success", NormalEnd, "12")]
    [InlineData("status/409", 130, "409")]
    public async Task A_key_archived_during_a_service_call_opens_an_incident_that_retries_after_reactivation(
        string path,
        int expectedEnd,
        string expectedValue)
    {
        var key = Key();
        var sharedKey = SharedKey();
        try
        {
            await CreateSharedVariableAsync(sharedKey);
            var instanceId = await InsertAndStartAsync(key, ServiceDefinition(key, sharedKey, path));
            var jobId = await SingleJobIdAsync(instanceId, WorkflowJobKinds.AsyncBefore);

            var block = fixture.ServiceInvocations.BlockNext("/" + path);
            var processing = ProcessAsync(jobId);
            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                var entered = block.WaitUntilEnteredAsync(timeout.Token);
                if (await Task.WhenAny(entered, processing) == processing)
                {
                    // A lease assertion can fail before the service is invoked. Observe
                    // that failure instead of waiting forever for its entry signal.
                    await processing;
                    Assert.Fail("Job processing finished before the blocked service was invoked.");
                }
                await entered;
                await ArchiveAsync(sharedKey);
            }
            finally
            {
                block.Release();
                try
                {
                    await processing.WaitAsync(TimeSpan.FromSeconds(30));
                }
                finally
                {
                    fixture.ServiceInvocations.Reset();
                }
            }

            var incident = await ConflictIncidentAsync(jobId);
            Assert.Contains("archived", incident.Details);
            await AssertTokenStillWaitsAsync(instanceId, jobId);
            await using (var db = fixture.CreateDbContext())
            {
                Assert.False(await db.NodeExecutions.AnyAsync(item => item.InstanceId == instanceId && item.NodeType == BpmnFlowNodeTypes.ErrorBoundaryEvent));
            }

            await ReactivateAsync(sharedKey);
            await RetryAsync(incident.Id);
            await ProcessAsync(jobId);

            await AssertCompletedThroughAsync(instanceId, expectedEnd);
            Assert.Equal(expectedValue, await SharedValueAsync(sharedKey));
            await AssertNoOpenIncidentAsync(jobId);
        }
        finally
        {
            await DeleteJobsAsync(key);
        }
    }

    [Fact]
    public async Task A_validated_definition_reaches_the_same_state_through_reactivation()
    {
        var key = Key();
        var sharedKey = SharedKey();
        try
        {
            await CreateSharedVariableAsync(sharedKey);
            var definition = ScriptDefinition(key, sharedKey);
            definition.FlowNodes.Add(new FlowNodeModel { Id = 5, Name = "Review", Type = BpmnFlowNodeTypes.UserTask });
            definition.SequenceFlows.Single(flow => flow.SourceRef == 1).TargetRef = 5;
            definition.SequenceFlows.Add(new SequenceFlowModel { Id = 501, Name = "Done", SourceRef = 5, TargetRef = Host });
            var workflow = await CreateWorkflowAsync(definition);

            var instanceId = await StartAsync(workflow.Id);
            await CompleteReviewAsync(instanceId);
            await ProcessAsync(await SingleJobIdAsync(instanceId, WorkflowJobKinds.AsyncBefore));
            await AssertCompletedThroughAsync(instanceId, NormalEnd);

            // Running instances and published definitions block an archive, so a key goes
            // away by retiring it: a new default without it, then the old version unpublished.
            using (var refused = await SendAsync(
                       HttpMethod.Post,
                       $"/api/shared-variables/{sharedKey}/archive",
                       new ArchiveSharedVariableRequest(await SharedRevisionAsync(sharedKey))))
            {
                Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            }
            var replacement = await CreateWorkflowAsync(new WorkflowModel
            {
                Id = key,
                Name = key,
                InitialEventId = 1,
                Variables = [],
                FlowNodes =
                [
                    new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                    new FlowNodeModel { Id = NormalEnd, Name = "Normal end", Type = BpmnFlowNodeTypes.EndEvent }
                ],
                SequenceFlows = [new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = NormalEnd }]
            });
            using (var madeDefault = await SendAsync(HttpMethod.Post, $"/api/workflows/{replacement.Id}/set-default", null))
            {
                Assert.Equal(HttpStatusCode.NoContent, madeDefault.StatusCode);
            }
            using (var unpublished = await SendAsync(HttpMethod.Post, $"/api/workflows/{workflow.Id}/unpublish", null))
            {
                Assert.Equal(HttpStatusCode.NoContent, unpublished.StatusCode);
            }
            await ArchiveAsync(sharedKey);

            InstanceReactivationPreviewDto preview;
            using (var previewed = await SendAsync(HttpMethod.Get, $"/api/instances/{instanceId}/reactivation", null))
            {
                preview = (await previewed.Content.ReadFromJsonAsync<InstanceReactivationPreviewDto>())!;
            }
            using (var reactivated = await SendAsync(
                       HttpMethod.Post,
                       $"/api/instances/{instanceId}/reactivation",
                       new ReactivateInstanceRequest(5, preview.WorkflowId, preview.ExpectedUpdatedAt, "Review again")))
            {
                Assert.Equal(HttpStatusCode.OK, reactivated.StatusCode);
            }
            await CompleteReviewAsync(instanceId);
            long jobId;
            await using (var db = fixture.CreateDbContext())
            {
                jobId = await db.WorkflowJobs
                    .Where(job => job.InstanceId == instanceId
                        && job.Kind == WorkflowJobKinds.AsyncBefore
                        && job.Status != WorkflowJobStatuses.Completed)
                    .Select(job => job.Id)
                    .SingleAsync();
            }

            await ProcessAsync(jobId);

            var incident = await ConflictIncidentAsync(jobId);
            Assert.Contains("archived", incident.Details);
            await AssertTokenStillWaitsAsync(instanceId, jobId);
        }
        finally
        {
            await DeleteJobsAsync(key);
        }
    }

    [Fact]
    public async Task A_conflict_on_the_path_after_an_async_task_opens_an_incident()
    {
        var key = Key();
        try
        {
            var host = new FlowNodeModel { Id = Host, Name = "Host", Type = BpmnFlowNodeTypes.Task, AsyncBefore = true };
            var instanceId = await InsertAndStartAsync(key, ConflictingContinuation(key, host, Host));
            var jobId = await SingleJobIdAsync(instanceId, WorkflowJobKinds.AsyncBefore);

            await ProcessAsync(jobId);

            var incident = await ConflictIncidentAsync(jobId);
            Assert.Contains(CancellingJoinFailure, incident.Details);
            await AssertTokenStillWaitsAsync(instanceId, jobId);
        }
        finally
        {
            await DeleteJobsAsync(key);
        }
    }

    [Fact]
    public async Task A_conflict_on_the_path_after_a_timer_catch_opens_an_incident()
    {
        var key = Key();
        try
        {
            var host = new FlowNodeModel
            {
                Id = Host,
                Name = "Wait",
                Type = BpmnFlowNodeTypes.IntermediateTimerCatchEvent,
                Timer = new TimerDefinitionModel { TimeDuration = "PT1H" }
            };
            var instanceId = await InsertAndStartAsync(key, ConflictingContinuation(key, host, Host));
            var jobId = await SingleJobIdAsync(instanceId, WorkflowJobKinds.Timer);

            await ProcessAsync(jobId);

            var incident = await ConflictIncidentAsync(jobId);
            Assert.Contains(CancellingJoinFailure, incident.Details);
            await AssertTokenStillWaitsAsync(instanceId, jobId);
        }
        finally
        {
            await DeleteJobsAsync(key);
        }
    }

    // A timer boundary does not park its host, but a job completed here would still
    // lose the occurrence: the subscription stays active with nothing left to fire it.
    [Fact]
    public async Task A_conflict_on_a_timer_boundary_path_opens_an_incident_and_keeps_the_host()
    {
        var key = Key();
        try
        {
            var definition = ConflictingContinuation(
                key,
                new FlowNodeModel { Id = Host, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
                40);
            definition.FlowNodes.Add(new FlowNodeModel
            {
                Id = 40,
                Name = "Deadline",
                Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                AttachedToRef = Host,
                CancelActivity = true,
                Timer = new TimerDefinitionModel { TimeDuration = "PT1H" }
            });
            definition.SequenceFlows.Add(new SequenceFlowModel { Id = 1001, Name = "Done", SourceRef = Host, TargetRef = NormalEnd });
            var instanceId = await InsertAndStartAsync(key, definition);
            var jobId = await SingleJobIdAsync(instanceId, WorkflowJobKinds.TimerBoundary);

            await ProcessAsync(jobId);

            var incident = await ConflictIncidentAsync(jobId);
            Assert.Contains(CancellingJoinFailure, incident.Details);
            await using (var db = fixture.CreateDbContext())
            {
                Assert.Equal(
                    TimerSubscriptionStatuses.Active,
                    await db.TimerSubscriptions.Where(item => item.InstanceId == instanceId).Select(item => item.Status).SingleAsync());
                Assert.True(await db.UserTasks.AnyAsync(item => item.InstanceId == instanceId && item.Status == "active"));
            }

            // The same occurrence can be retried, and fails the same way until corrected.
            await RetryAsync(incident.Id);
            await ProcessAsync(jobId);
            Assert.NotEqual(incident.Id, (await ConflictIncidentAsync(jobId)).Id);
        }
        finally
        {
            await DeleteJobsAsync(key);
        }
    }

    [Fact]
    public async Task A_job_whose_instance_was_cancelled_still_ends_without_an_incident()
    {
        var key = Key();
        try
        {
            var host = new FlowNodeModel { Id = Host, Name = "Host", Type = BpmnFlowNodeTypes.Task, AsyncBefore = true };
            var instanceId = await InsertAndStartAsync(key, ConflictingContinuation(key, host, Host));
            var jobId = await SingleJobIdAsync(instanceId, WorkflowJobKinds.AsyncBefore);
            var lease = await LeaseAsync(jobId);

            using (var cancelled = await SendAsync(HttpMethod.Post, $"/api/instances/{instanceId}/cancel", null))
            {
                Assert.Equal(HttpStatusCode.NoContent, cancelled.StatusCode);
            }
            await ProcessAsync(lease);

            await using var db = fixture.CreateDbContext();
            Assert.False(await db.WorkflowIncidents.AnyAsync(item => item.JobId == jobId));
            Assert.NotEqual(WorkflowJobStatuses.Incident, await JobStatusAsync(db, jobId));
        }
        finally
        {
            await DeleteJobsAsync(key);
        }
    }

    // The boundary's host finished while its timer job was leased, so the occurrence has nothing left to do.
    [Fact]
    public async Task A_timer_boundary_job_whose_host_finished_still_ends_without_an_incident()
    {
        var key = Key();
        try
        {
            var definition = ConflictingContinuation(
                key,
                new FlowNodeModel { Id = Host, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
                40);
            definition.FlowNodes.Add(new FlowNodeModel
            {
                Id = 40,
                Name = "Deadline",
                Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                AttachedToRef = Host,
                CancelActivity = true,
                Timer = new TimerDefinitionModel { TimeDuration = "PT1H" }
            });
            definition.SequenceFlows.Add(new SequenceFlowModel { Id = 1001, Name = "Done", SourceRef = Host, TargetRef = NormalEnd });
            var instanceId = await InsertAndStartAsync(key, definition);
            var jobId = await SingleJobIdAsync(instanceId, WorkflowJobKinds.TimerBoundary);
            var lease = await LeaseAsync(jobId);

            await CompleteReviewAsync(instanceId);
            await ProcessAsync(lease);

            await AssertCompletedThroughAsync(instanceId, NormalEnd);
            await using var db = fixture.CreateDbContext();
            Assert.False(await db.WorkflowIncidents.AnyAsync(item => item.JobId == jobId));
        }
        finally
        {
            await DeleteJobsAsync(key);
        }
    }

    // Either the script itself fails, or its binding has no rule and only the catalog's
    // rule refuses the value when finalization writes it.
    [Theory]
    [InlineData("throw new Error('refused');")]
    [InlineData("execution.setVariable('sharedNumber', 5);")]
    public async Task A_failed_script_leaves_a_concurrent_error_variable_change_in_place(string script)
    {
        var key = Key();
        var sharedKey = SharedKey();
        try
        {
            await CreateSharedVariableAsync(sharedKey, "value < 0");
            var gate = new ScriptGate();
            await using var gatedHost = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IScriptEvaluator>();
                services.AddSingleton<IScriptEvaluator>(_ => new GatedScriptEvaluator(
                    new JintScriptEvaluator(new ScriptOptions(), NullLogger<JintScriptEvaluator>.Instance),
                    gate));
            }));
            var definition = ScriptDefinition(key, sharedKey);
            definition.FlowNodes.Single(node => node.Id == Host).Script = script;
            definition.Variables.Add(new VariableModel
            {
                Id = 4,
                Name = "failure",
                DataType = WorkflowVariableTypes.String,
                DefaultValue = JsonSerializer.SerializeToElement("none")
            });
            definition.FlowNodes.Add(new FlowNodeModel
            {
                Id = 30,
                Name = "Caught",
                Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
                AttachedToRef = Host,
                ErrorVariable = "failure"
            });
            definition.FlowNodes.Add(new FlowNodeModel { Id = 130, Name = "After caught", Type = BpmnFlowNodeTypes.EndEvent });
            definition.SequenceFlows.Add(new SequenceFlowModel { Id = 3001, SourceRef = 30, TargetRef = 130 });
            var instanceId = await InsertAndStartAsync(key, definition);
            var jobId = await SingleJobIdAsync(instanceId, WorkflowJobKinds.AsyncBefore);

            await using (var scope = gatedHost.Services.CreateAsyncScope())
            {
                var lease = await LeaseAsync(jobId);
                var processing = scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>()
                    .ProcessAsync(lease, CancellationToken.None);
                Assert.True(gate.Entered.Wait(TimeSpan.FromSeconds(20)));
                await using (var write = fixture.CreateDbContext())
                {
                    write.InstanceVariables.Add(new InstanceVariableEntity
                    {
                        InstanceId = instanceId,
                        VariableName = "failure",
                        SourceActionId = 99,
                        ValueJson = JsonDocument.Parse("\"concurrent\""),
                        SetBy = "parallel-branch",
                        SetAt = DateTimeOffset.UtcNow
                    });
                    await write.SaveChangesAsync();
                }
                gate.Release.Set();
                await processing;
            }

            long incidentId;
            await using (var db = fixture.CreateDbContext())
            {
                Assert.Equal(WorkflowJobStatuses.Incident, await JobStatusAsync(db, jobId));
                var incident = await db.WorkflowIncidents.SingleAsync(item =>
                    item.JobId == jobId && item.Status == WorkflowIncidentStatuses.Open);
                Assert.Equal("output_version_conflict", incident.Type);
                incidentId = incident.Id;
                Assert.Equal(["none", "concurrent"], await FailureHistoryAsync(db, instanceId));
                Assert.False(await db.NodeExecutions.AnyAsync(item => item.InstanceId == instanceId && item.NodeId == 30));
            }
            await AssertTokenStillWaitsAsync(instanceId, jobId);
            Assert.Equal("-1", await SharedValueAsync(sharedKey));

            // A retry works from a new snapshot, so the same failure now reaches the boundary.
            await RetryAsync(incidentId);
            await ProcessAsync(jobId);

            await AssertCompletedThroughAsync(instanceId, 130);
            await AssertNoOpenIncidentAsync(jobId);
            await using var routed = fixture.CreateDbContext();
            var history = await FailureHistoryAsync(routed, instanceId);
            Assert.Equal(3, history.Count);
            Assert.Contains($"Script task #{Host} failed", history[^1]);
        }
        finally
        {
            await DeleteJobsAsync(key);
        }
    }

    private static async Task<List<string>> FailureHistoryAsync(AppDbContext db, long instanceId)
    {
        var values = await db.InstanceVariables
            .Where(item => item.InstanceId == instanceId && item.VariableName == "failure")
            .OrderBy(item => item.Id)
            .Select(item => item.ValueJson)
            .ToListAsync();
        return values.Select(value => value.RootElement.GetString()!).ToList();
    }

    private static string Key() => $"job-conflict-{Guid.NewGuid():N}";

    private static string SharedKey() => $"tests.job-conflict.{Guid.NewGuid():N}";

    private static VariableModel SharedNumber(string sharedKey) => new()
    {
        Id = 3,
        Name = "sharedNumber",
        Scope = VariableScopes.Shared,
        SharedKey = sharedKey,
        Access = SharedVariableAccessModes.ReadWrite,
        DataType = WorkflowVariableTypes.Number
    };

    private static WorkflowModel ScriptDefinition(string key, string sharedKey) => new()
    {
        Id = key,
        Name = key,
        InitialEventId = 1,
        Variables = [SharedNumber(sharedKey)],
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel { Id = NormalEnd, Name = "Normal end", Type = BpmnFlowNodeTypes.EndEvent },
            new FlowNodeModel
            {
                Id = Host,
                Name = "Script",
                Type = BpmnFlowNodeTypes.ScriptTask,
                AsyncBefore = true,
                Job = new JobPolicyModel { FailureHandling = JobFailureHandling.BoundaryFirst, RetryDelays = [] },
                ScriptFormat = ScriptFormats.JavaScript,
                UsesFlowInfo = false,
                Script = "execution.setVariable('sharedNumber', 5);"
            }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = Host },
            new SequenceFlowModel { Id = 1001, SourceRef = Host, TargetRef = NormalEnd }
        ]
    };

    // On success the mapped output goes to the shared alias, and on a 409 its status does, then a boundary catches it.
    private static WorkflowModel ServiceDefinition(string key, string sharedKey, string path)
    {
        var service = new ServiceTaskModel { Url = "https://tests.local/" + path, Method = "GET", TimeoutSeconds = 5 };
        if (path == "typed-output-success")
        {
            service.OutputMappings =
            [
                new ServiceOutputMappingModel
                {
                    Variable = "sharedNumber",
                    Path = "result.score",
                    Required = true,
                    DataType = WorkflowVariableTypes.Number,
                    IsArray = false
                }
            ];
        }
        else
        {
            service.StatusVariable = "sharedNumber";
        }
        return new WorkflowModel
        {
            Id = key,
            Name = key,
            InitialEventId = 1,
            Variables = [SharedNumber(sharedKey)],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = NormalEnd, Name = "Normal end", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel
                {
                    Id = Host,
                    Name = "Call",
                    Type = BpmnFlowNodeTypes.ServiceTask,
                    AsyncBefore = true,
                    Job = new JobPolicyModel { FailureHandling = JobFailureHandling.BoundaryFirst, RetryDelays = [] },
                    Service = service
                },
                new FlowNodeModel
                {
                    Id = 30,
                    Name = "Conflict",
                    Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
                    AttachedToRef = Host,
                    ErrorMatch = new ErrorBoundaryMatchModel { HttpStatusCodes = [409] }
                },
                new FlowNodeModel { Id = 130, Name = "After conflict", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = Host },
                new SequenceFlowModel { Id = 1001, SourceRef = Host, TargetRef = NormalEnd },
                new SequenceFlowModel { Id = 3001, SourceRef = 30, TargetRef = 130 }
            ]
        };
    }

    // The join refers to a gateway no token came through, which the validator would refuse
    // at save. Reaching it raises a conflict in the job's own transaction.
    private static WorkflowModel ConflictingContinuation(string key, FlowNodeModel host, int joinSource) => new()
    {
        Id = key,
        Name = key,
        InitialEventId = 1,
        Variables = [],
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel { Id = NormalEnd, Name = "Normal end", Type = BpmnFlowNodeTypes.EndEvent },
            host,
            new FlowNodeModel
            {
                Id = 20,
                Name = "Cancelling join",
                Type = BpmnFlowNodeTypes.ExclusiveGateway,
                JoinCancellation = new JoinCancellationModel { GatewayRef = 99 }
            },
            new FlowNodeModel { Id = 21, Name = "Other input", Type = BpmnFlowNodeTypes.Task },
            new FlowNodeModel { Id = 3, Name = "After join", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = Host },
            new SequenceFlowModel { Id = joinSource * 100 + 2, SourceRef = joinSource, TargetRef = 20 },
            new SequenceFlowModel { Id = 2101, SourceRef = 21, TargetRef = 20 },
            new SequenceFlowModel { Id = 2001, SourceRef = 20, TargetRef = 3 }
        ]
    };

    private async Task<WorkflowIncidentEntity> ConflictIncidentAsync(long jobId)
    {
        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowJobStatuses.Incident, await JobStatusAsync(db, jobId));
        var incident = await db.WorkflowIncidents.SingleAsync(item =>
            item.JobId == jobId && item.Status == WorkflowIncidentStatuses.Open);
        Assert.Equal(WorkflowIncidentTypes.JobConflict, incident.Type);
        return incident;
    }

    private async Task AssertTokenStillWaitsAsync(long instanceId, long jobId)
    {
        await using var db = fixture.CreateDbContext();
        Assert.Equal(
            WorkflowInstanceStatuses.Running,
            await db.WorkflowInstances.Where(item => item.Id == instanceId).Select(item => item.Status).SingleAsync());
        Assert.True(await db.ExecutionTokens.AnyAsync(token =>
            token.InstanceId == instanceId
            && token.Status == ExecutionTokenStatuses.Active
            && token.WaitingJobId == jobId));
    }

    private async Task AssertCompletedThroughAsync(long instanceId, int endNodeId)
    {
        await using var db = fixture.CreateDbContext();
        Assert.Equal(
            WorkflowInstanceStatuses.Completed,
            await db.WorkflowInstances.Where(item => item.Id == instanceId).Select(item => item.Status).SingleAsync());
        Assert.True(await db.NodeExecutions.AnyAsync(item => item.InstanceId == instanceId && item.NodeId == endNodeId));
    }

    private async Task AssertNoOpenIncidentAsync(long jobId)
    {
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.WorkflowIncidents.AnyAsync(item =>
            item.JobId == jobId && item.Status == WorkflowIncidentStatuses.Open));
    }

    private static Task<string> JobStatusAsync(AppDbContext db, long jobId) =>
        db.WorkflowJobs.Where(job => job.Id == jobId).Select(job => job.Status).SingleAsync();

    private async Task<string> SharedValueAsync(string sharedKey)
    {
        await using var db = fixture.CreateDbContext();
        var variable = await db.SharedVariables.SingleAsync(item => item.Key == sharedKey);
        var current = await db.SharedVariableCurrentValues.SingleAsync(item => item.SharedVariableId == variable.Id);
        return current.ValueJson!.RootElement.GetRawText();
    }

    private async Task CreateSharedVariableAsync(string sharedKey, string? validation = null)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/shared-variables",
            new CreateSharedVariableRequest(
                sharedKey,
                WorkflowVariableTypes.Number,
                IsArray: false,
                Nullable: false,
                HasValue: true,
                JsonSerializer.SerializeToElement(-1),
                validation));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<long> SharedRevisionAsync(string sharedKey)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/shared-variables/{sharedKey}", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("revision").GetInt64();
    }

    private async Task ArchiveAsync(string sharedKey)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/shared-variables/{sharedKey}/archive",
            new ArchiveSharedVariableRequest(await SharedRevisionAsync(sharedKey)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task ReactivateAsync(string sharedKey)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/shared-variables/{sharedKey}/reactivate",
            new ReactivateSharedVariableRequest(await SharedRevisionAsync(sharedKey)));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task RetryAsync(long incidentId)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/api/incidents/{incidentId}/retry", null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task CompleteReviewAsync(long instanceId)
    {
        long taskId;
        int flowId;
        await using (var db = fixture.CreateDbContext())
        {
            var task = await db.UserTasks.SingleAsync(item => item.InstanceId == instanceId && item.Status == "active");
            taskId = task.Id;
            flowId = task.NodeId == Host ? 1001 : 501;
        }
        using var response = await SendAsync(HttpMethod.Post, $"/api/user-tasks/{taskId}/flows/{flowId}", new TakeFlowRequest(null));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    private async Task<WorkflowDetailDto> CreateWorkflowAsync(WorkflowModel definition)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(definition, true));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<WorkflowDetailDto>())!;
    }

    private async Task<long> InsertAndStartAsync(string key, WorkflowModel definition)
    {
        long workflowId;
        await using (var setup = fixture.CreateDbContext())
        {
            var entity = new WorkflowDefinitionEntity
            {
                Name = key,
                WorkflowKey = key,
                Version = 1,
                IsPublished = true,
                IsDefault = true,
                DefaultActivationId = Guid.NewGuid(),
                DefaultActivatedAt = DateTimeOffset.UtcNow,
                Definition = definition
            };
            setup.WorkflowDefinitions.Add(entity);
            await setup.SaveChangesAsync();
            workflowId = entity.Id;
        }

        return await StartAsync(workflowId);
    }

    private async Task<long> StartAsync(long workflowId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var started = await scope.ServiceProvider.GetRequiredService<IWorkflowEngineService>().StartInstanceSlimAsync(
            workflowId,
            null,
            new ActorContext("starter", ["User", "admin"], new Dictionary<string, string>()),
            null,
            null,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);
        return started.Id;
    }

    private async Task<long> SingleJobIdAsync(long instanceId, string kind)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowJobs
            .Where(job => job.InstanceId == instanceId && job.Kind == kind)
            .Select(job => job.Id)
            .SingleAsync();
    }

    private async Task ProcessAsync(long jobId) => await ProcessAsync(await LeaseAsync(jobId));

    private async Task ProcessAsync(WorkflowJobLeaseRecord lease)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>()
            .ProcessAsync(lease, CancellationToken.None);
    }

    private async Task<WorkflowJobLeaseRecord> LeaseAsync(long jobId)
    {
        await using (var promote = fixture.CreateDbContext())
        {
            // Due a minute ago, so the job is due even when the database clock runs behind this machine's.
            await promote.WorkflowJobs
                .Where(job => job.Id == jobId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Priority, 1_000_000)
                    .SetProperty(job => job.DueAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var leases = await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().LeaseRunnableAsync(
            new WorkflowJobLeaseRequest(
                $"job-conflict-test:{Guid.NewGuid():N}",
                MaxCount: 1,
                MaxActivityCount: 1,
                MaxPerInstance: 4,
                LeaseDuration: TimeSpan.FromMinutes(1)),
            CancellationToken.None);
        var lease = Assert.Single(leases);
        Assert.Equal(jobId, lease.Job.Id);
        return lease;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }
        ApiTestAuth.Authorize(request, "job-conflict-admin", "admin");
        return await fixture.Client.SendAsync(request);
    }

    // Shared-variable audit rows keep their instances, so only runnable work is removed.
    private async Task DeleteJobsAsync(string workflowKey)
    {
        await using var cleanup = fixture.CreateDbContext();
        await cleanup.WorkflowIncidents.Where(item => item.WorkflowKey == workflowKey).ExecuteDeleteAsync();
        await cleanup.WorkflowJobs.Where(item => item.WorkflowKey == workflowKey).ExecuteDeleteAsync();
    }

    private sealed class ScriptGate
    {
        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
    }

    // Runs the real script, then holds until the test has archived the key.
    private sealed class GatedScriptEvaluator(IScriptEvaluator inner, ScriptGate gate) : IScriptEvaluator
    {
        public ScriptResult Evaluate(string script, IScriptContext context, CancellationToken cancellationToken)
        {
            var result = inner.Evaluate(script, context, cancellationToken);
            gate.Entered.Set();
            gate.Release.Wait(TimeSpan.FromSeconds(30), cancellationToken);
            return result;
        }

        public bool IsValid(string script, out string? error) => inner.IsValid(script, out error);
    }
}
