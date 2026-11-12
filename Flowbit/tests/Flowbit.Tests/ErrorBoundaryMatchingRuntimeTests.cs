using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class ErrorBoundaryMatchingRuntimeTests(PostgresApiFixture fixture)
{
    private const int Conflict = 30;
    private const int ClientErrors = 31;
    private const int ServerErrors = 32;
    private const int CatchAll = 33;
    private const int StatusWatch = 41;

    [Theory]
    [InlineData("status/409", Conflict)]
    [InlineData("status/422", ClientErrors)]
    [InlineData("status/503", ServerErrors)]
    [InlineData("lost-body/503", ServerErrors)]
    [InlineData("no-response", CatchAll)]
    [InlineData("status/600", CatchAll)]
    [InlineData("status/302", CatchAll)]
    public async Task Synchronous_RoutesOnlyTheSelectedBoundary(string path, int expectedBoundary)
    {
        var key = Key();
        try
        {
            var instanceId = await InsertAndStartAsync(
                key,
                Definition(key, path, [Exact(Conflict, 409), Range(ClientErrors, 400, 499), Range(ServerErrors, 500, 599), Unfiltered(CatchAll)]));

            await AssertRoutedAsync(instanceId, expectedBoundary);
            await using var db = fixture.CreateDbContext();
            var error = await LatestVariableAsync(db, instanceId, "failure");
            Assert.Contains("REST call failed", error);
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Synchronous_IgnoresAStaleStoredStatusAndStillWritesTheRealOne()
    {
        var key = Key();
        try
        {
            var definition = Definition(key, "status/503", [Exact(Conflict, 409), Range(ServerErrors, 500, 599)]);
            definition.Variables.Add(new VariableModel
            {
                Id = 2,
                Name = "httpStatus",
                DataType = WorkflowVariableTypes.Number,
                DefaultValue = JsonSerializer.SerializeToElement(409)
            });
            definition.FlowNodes.Single(node => node.Id == 10).Service!.StatusVariable = "httpStatus";

            var instanceId = await InsertAndStartAsync(key, definition);

            await AssertRoutedAsync(instanceId, ServerErrors);
            await using var db = fixture.CreateDbContext();
            Assert.Equal("503", await LatestVariableAsync(db, instanceId, "httpStatus"));
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Synchronous_FailedMappingAfterA200CanMatchA200Boundary()
    {
        var key = Key();
        try
        {
            var definition = Definition(key, "typed-output-invalid", [Exact(Conflict, 200), Unfiltered(CatchAll)]);
            definition.FlowNodes.Single(node => node.Id == 10).Service!.OutputMappings =
            [
                new ServiceOutputMappingModel
                {
                    Variable = "score",
                    Path = "result.score",
                    Required = true,
                    DataType = WorkflowVariableTypes.Number,
                    IsArray = false
                }
            ];

            var instanceId = await InsertAndStartAsync(key, definition);

            await AssertRoutedAsync(instanceId, Conflict);
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Synchronous_SuccessNeverTakesAMatchingBoundary()
    {
        var key = Key();
        try
        {
            var instanceId = await InsertAndStartAsync(
                key,
                Definition(key, "typed-output-success", [Exact(Conflict, 200), Unfiltered(CatchAll)]));

            await using var db = fixture.CreateDbContext();
            Assert.Equal(
                WorkflowInstanceStatuses.Completed,
                await db.WorkflowInstances.Where(item => item.Id == instanceId).Select(item => item.Status).SingleAsync());
            Assert.False(await db.NodeExecutions.AnyAsync(item =>
                item.InstanceId == instanceId
                && item.NodeType == BpmnFlowNodeTypes.ErrorBoundaryEvent));
            Assert.True(await db.NodeExecutions.AnyAsync(item => item.InstanceId == instanceId && item.NodeId == 2));
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Theory]
    [InlineData("status/500")]
    [InlineData("no-response")]
    public async Task Synchronous_NoMatchRollsBackLikeNoBoundary(string path)
    {
        var key = Key();
        try
        {
            var error = await Assert.ThrowsAsync<WorkflowDomainException>(() => InsertAndStartAsync(
                key,
                Definition(key, path, [Exact(Conflict, 409)])));

            Assert.Contains("REST call failed", error.Message);
            await using var db = fixture.CreateDbContext();
            Assert.False(await db.WorkflowInstances.AnyAsync(item => item.WorkflowKey == key));
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Synchronous_AmbiguousStoredDataRollsBackInsteadOfChoosing()
    {
        var key = Key();
        try
        {
            var error = await Assert.ThrowsAsync<WorkflowDomainException>(() => InsertAndStartAsync(
                key,
                Definition(key, "status/409", [Exact(Conflict, 409), Exact(ClientErrors, 409)])));

            Assert.Contains("#30", error.Message);
            Assert.Contains("#31", error.Message);
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Durable_BoundaryFirstRoutesAMatchBeforeAnyRetry()
    {
        var key = Key();
        try
        {
            var instanceId = await InsertAndStartAsync(key, Durable(key, $"status-sequence/{key}/409,200", JobFailureHandling.BoundaryFirst));
            var jobId = await SingleJobIdAsync(instanceId);

            await PromoteAndProcessAsync(jobId);

            await AssertRoutedAsync(instanceId, Conflict);
            await using var db = fixture.CreateDbContext();
            Assert.Equal(WorkflowJobStatuses.Completed, await JobStatusAsync(db, jobId));
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Durable_BoundaryFirstRetriesAnUnmatchedStatusThenRoutesTheLaterMatch()
    {
        var key = Key();
        try
        {
            var path = $"status-sequence/{key}/500,409";
            var instanceId = await InsertAndStartAsync(key, Durable(key, path, JobFailureHandling.BoundaryFirst));
            var jobId = await SingleJobIdAsync(instanceId);

            await PromoteAndProcessAsync(jobId);
            await using (var db = fixture.CreateDbContext())
            {
                Assert.Equal(WorkflowJobStatuses.Retry, await JobStatusAsync(db, jobId));
                Assert.Equal(WorkflowInstanceStatuses.Running, await db.WorkflowInstances.Where(item => item.Id == instanceId).Select(item => item.Status).SingleAsync());
                Assert.False(await db.NodeExecutions.AnyAsync(item => item.InstanceId == instanceId && item.NodeType == BpmnFlowNodeTypes.ErrorBoundaryEvent));
            }

            await PromoteAndProcessAsync(jobId);

            await AssertRoutedAsync(instanceId, Conflict);
            Assert.Equal(2, fixture.ServiceInvocations.AttemptCount("https://tests.local/" + path));
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Durable_BoundaryFirstOpensAnIncidentWhenUnmatchedRetriesRunOut()
    {
        var key = Key();
        try
        {
            var instanceId = await InsertAndStartAsync(key, Durable(key, "status/500", JobFailureHandling.BoundaryFirst, retryDelays: []));
            var jobId = await SingleJobIdAsync(instanceId);

            await PromoteAndProcessAsync(jobId);

            await using var db = fixture.CreateDbContext();
            var incident = await db.WorkflowIncidents.SingleAsync(item => item.JobId == jobId);
            Assert.Equal(WorkflowIncidentStatuses.Open, incident.Status);
            Assert.False(await db.NodeExecutions.AnyAsync(item => item.InstanceId == instanceId && item.NodeType == BpmnFlowNodeTypes.ErrorBoundaryEvent));
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Durable_RetryFirstRetriesAMatchAndRoutesOnlyTheExhaustedAttempt()
    {
        var key = Key();
        try
        {
            var path = $"status-sequence/{key}/409,503";
            var instanceId = await InsertAndStartAsync(
                key,
                Durable(key, path, JobFailureHandling.RetryFirst, retryDelays: ["PT1S"]));
            var jobId = await SingleJobIdAsync(instanceId);

            await PromoteAndProcessAsync(jobId);
            await using (var db = fixture.CreateDbContext())
            {
                Assert.Equal(WorkflowJobStatuses.Retry, await JobStatusAsync(db, jobId));
                Assert.False(await db.NodeExecutions.AnyAsync(item => item.InstanceId == instanceId && item.NodeType == BpmnFlowNodeTypes.ErrorBoundaryEvent));
            }

            await PromoteAndProcessAsync(jobId);

            // The final attempt decides: 503 is the range, not the first attempt's 409.
            await AssertRoutedAsync(instanceId, ServerErrors);
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Durable_RetryFirstWithNoRetriesConsidersTheFirstFailure()
    {
        var key = Key();
        try
        {
            var instanceId = await InsertAndStartAsync(key, Durable(key, "status/409", JobFailureHandling.RetryFirst, retryDelays: []));

            await PromoteAndProcessAsync(await SingleJobIdAsync(instanceId));

            await AssertRoutedAsync(instanceId, Conflict);
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Durable_ASuccessfulRetryFollowsTheNormalFlow()
    {
        var key = Key();
        try
        {
            var instanceId = await InsertAndStartAsync(
                key,
                Durable(key, $"status-sequence/{key}/503,200", JobFailureHandling.RetryFirst, retryDelays: ["PT1S"]));
            var jobId = await SingleJobIdAsync(instanceId);

            await PromoteAndProcessAsync(jobId);
            await PromoteAndProcessAsync(jobId);

            await using var db = fixture.CreateDbContext();
            Assert.Equal(WorkflowInstanceStatuses.Completed, await db.WorkflowInstances.Where(item => item.Id == instanceId).Select(item => item.Status).SingleAsync());
            Assert.False(await db.NodeExecutions.AnyAsync(item => item.InstanceId == instanceId && item.NodeType == BpmnFlowNodeTypes.ErrorBoundaryEvent));
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Theory]
    [InlineData(JobFailureHandling.BoundaryFirst)]
    [InlineData(JobFailureHandling.RetryFirst)]
    public async Task Durable_CorruptMatcherOpensAnInvariantIncidentWithoutARetry(string policy)
    {
        var key = Key();
        try
        {
            var definition = Durable(key, $"status-sequence/{key}/409", policy);
            definition.FlowNodes.Single(node => node.Id == ClientErrors).ErrorMatch =
                new ErrorBoundaryMatchModel { HttpStatusCodes = [409] };
            var instanceId = await InsertAndStartAsync(key, definition);
            var jobId = await SingleJobIdAsync(instanceId);

            await PromoteAndProcessAsync(jobId);

            await using var db = fixture.CreateDbContext();
            var incident = await db.WorkflowIncidents.SingleAsync(item => item.JobId == jobId);
            Assert.Equal("job_invariant_violation", incident.Type);
            Assert.Equal(1, fixture.ServiceInvocations.AttemptCount($"https://tests.local/status-sequence/{key}/409"));
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Durable_SnapshotsEveryPotentialErrorOutputAndWritesOnlyTheSelectedOne()
    {
        var key = Key();
        try
        {
            var definition = Durable(key, "status/409", JobFailureHandling.BoundaryFirst);
            definition.FlowNodes.Single(node => node.Id == ServerErrors).ErrorVariable = "serverFailure";
            definition.Variables.Add(new VariableModel
            {
                Id = 3,
                Name = "serverFailure",
                DataType = WorkflowVariableTypes.String,
                DefaultValue = JsonSerializer.SerializeToElement("untouched")
            });
            var instanceId = await InsertAndStartAsync(key, definition);
            var jobId = await SingleJobIdAsync(instanceId);

            await PromoteAndProcessAsync(jobId);

            await AssertRoutedAsync(instanceId, Conflict);
            await using var db = fixture.CreateDbContext();
            Assert.Contains("REST call failed", await LatestVariableAsync(db, instanceId, "failure"));
            Assert.Equal("\"untouched\"", await LatestVariableJsonAsync(db, instanceId, "serverFailure"));
            var snapshot = await db.WorkflowJobSnapshots.SingleAsync(item =>
                db.WorkflowJobs.Any(job => job.Id == jobId && job.SnapshotId == item.Id));
            var versions = snapshot.OutputVariableVersionsJson.RootElement;
            Assert.True(versions.TryGetProperty("failure", out _));
            Assert.True(versions.TryGetProperty("serverFailure", out _));
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Durable_AnUnselectedBoundarysOutputChangeProducesTheConservativeConflict()
    {
        var key = Key();
        try
        {
            var definition = Durable(key, "status/409", JobFailureHandling.BoundaryFirst);
            definition.FlowNodes.Single(node => node.Id == ServerErrors).ErrorVariable = "serverFailure";
            definition.Variables.Add(new VariableModel
            {
                Id = 3,
                Name = "serverFailure",
                DataType = WorkflowVariableTypes.String,
                DefaultValue = JsonSerializer.SerializeToElement("untouched")
            });
            var instanceId = await InsertAndStartAsync(key, definition);
            var jobId = await SingleJobIdAsync(instanceId);

            var block = fixture.ServiceInvocations.BlockNext("/status/409");
            var processing = PromoteAndProcessAsync(jobId);
            await block.WaitUntilEnteredAsync(CancellationToken.None);
            try
            {
                await using var write = fixture.CreateDbContext();
                write.InstanceVariables.Add(new InstanceVariableEntity
                {
                    InstanceId = instanceId,
                    VariableName = "serverFailure",
                    SourceActionId = 99,
                    ValueJson = JsonDocument.Parse("\"concurrent\""),
                    SetBy = "parallel-branch",
                    SetAt = DateTimeOffset.UtcNow
                });
                await write.SaveChangesAsync();
            }
            finally
            {
                block.Release();
            }
            await processing;

            await using var db = fixture.CreateDbContext();
            var incident = await db.WorkflowIncidents.SingleAsync(item => item.JobId == jobId);
            Assert.Equal("output_version_conflict", incident.Type);
            Assert.False(await db.NodeExecutions.AnyAsync(item => item.InstanceId == instanceId && item.NodeType == BpmnFlowNodeTypes.ErrorBoundaryEvent));
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Durable_AFailedHandlerPathCallsTheServiceAgainAndSelectsFromTheNewResult()
    {
        var key = Key();
        try
        {
            var path = $"status-sequence/{key}/409,503";
            var definition = Durable(key, path, JobFailureHandling.BoundaryFirst, retryDelays: ["PT1S"]);
            // A stored gateway with no default and no true condition fails in the
            // same transaction as the boundary that led to it.
            definition.FlowNodes.Add(new FlowNodeModel
            {
                Id = 50,
                Name = "Broken handler",
                Type = BpmnFlowNodeTypes.ExclusiveGateway
            });
            definition.SequenceFlows.Single(flow => flow.SourceRef == Conflict).TargetRef = 50;
            definition.SequenceFlows.Add(new SequenceFlowModel { Id = 5001, SourceRef = 50, TargetRef = 100 + Conflict, Condition = "1 == 2", ConditionPriority = 1 });
            definition.SequenceFlows.Add(new SequenceFlowModel { Id = 5002, SourceRef = 50, TargetRef = 100 + Conflict, Condition = "1 == 3", ConditionPriority = 2 });
            var instanceId = await InsertAndStartAsync(key, definition);
            var jobId = await SingleJobIdAsync(instanceId);

            await PromoteAndProcessAsync(jobId);
            await using (var db = fixture.CreateDbContext())
            {
                Assert.Equal(WorkflowJobStatuses.Retry, await JobStatusAsync(db, jobId));
                Assert.False(await db.NodeExecutions.AnyAsync(item => item.InstanceId == instanceId && item.NodeId == Conflict));
                Assert.Equal("none", await LatestVariableAsync(db, instanceId, "failure"));
            }

            await PromoteAndProcessAsync(jobId);

            await AssertRoutedAsync(instanceId, ServerErrors);
            Assert.Equal(2, fixture.ServiceInvocations.AttemptCount("https://tests.local/" + path));
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Durable_AScriptOnTheHandlerPathIsItsOwnJobAndDoesNotRepeatTheCall()
    {
        var key = Key();
        try
        {
            var path = $"status-sequence/{key}/409,503";
            var definition = Durable(key, path, JobFailureHandling.BoundaryFirst, retryDelays: ["PT1S"]);
            definition.FlowNodes.Add(new FlowNodeModel
            {
                Id = 50,
                Name = "Broken handler",
                Type = BpmnFlowNodeTypes.ScriptTask,
                ScriptFormat = ScriptFormats.JavaScript,
                UsesFlowInfo = false,
                Script = "throw new Error('handler failure');"
            });
            definition.SequenceFlows.Single(flow => flow.SourceRef == Conflict).TargetRef = 50;
            definition.SequenceFlows.Add(new SequenceFlowModel { Id = 5001, SourceRef = 50, TargetRef = 100 + Conflict });
            var instanceId = await InsertAndStartAsync(key, definition);
            var jobId = await SingleJobIdAsync(instanceId);

            await PromoteAndProcessAsync(jobId);

            await using var db = fixture.CreateDbContext();
            Assert.Equal(WorkflowJobStatuses.Completed, await JobStatusAsync(db, jobId));
            Assert.True(await db.NodeExecutions.AnyAsync(item => item.InstanceId == instanceId && item.NodeId == Conflict));
            Assert.True(await db.WorkflowJobs.AnyAsync(job => job.InstanceId == instanceId && job.NodeId == 50 && job.Kind == WorkflowJobKinds.AsyncBefore));
            Assert.Equal(1, fixture.ServiceInvocations.AttemptCount("https://tests.local/" + path));
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Durable_AsyncAfterDoesNotRerunAHostThatAlreadySucceeded()
    {
        var key = Key();
        try
        {
            var path = $"status-sequence/{key}/200,409";
            var definition = Durable(key, path, JobFailureHandling.BoundaryFirst);
            definition.FlowNodes.Single(node => node.Id == 10).AsyncAfter = true;
            var instanceId = await InsertAndStartAsync(key, definition);

            await PromoteAndProcessAsync(await SingleJobIdAsync(instanceId));
            await using (var waiting = fixture.CreateDbContext())
            {
                var afterJob = await waiting.WorkflowJobs
                    .Where(job => job.InstanceId == instanceId && job.Kind == WorkflowJobKinds.AsyncAfter)
                    .Select(job => job.Id)
                    .SingleAsync();
                await PromoteAndProcessAsync(afterJob);
            }

            await using var db = fixture.CreateDbContext();
            Assert.Equal(WorkflowInstanceStatuses.Completed, await db.WorkflowInstances.Where(item => item.Id == instanceId).Select(item => item.Status).SingleAsync());
            Assert.True(await db.NodeExecutions.AnyAsync(item => item.InstanceId == instanceId && item.NodeId == 2));
            Assert.False(await db.NodeExecutions.AnyAsync(item => item.InstanceId == instanceId && item.NodeType == BpmnFlowNodeTypes.ErrorBoundaryEvent));
            Assert.Equal(1, fixture.ServiceInvocations.AttemptCount("https://tests.local/" + path));
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Durable_AnInterruptingTimerWinsOverALateMatchingResult()
    {
        var key = Key();
        try
        {
            var definition = Durable(key, "status/409", JobFailureHandling.BoundaryFirst);
            definition.FlowNodes.Add(new FlowNodeModel
            {
                Id = 40,
                Name = "Deadline",
                Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
                AttachedToRef = 10,
                CancelActivity = true,
                Timer = new TimerDefinitionModel { TimeDuration = "PT1H" }
            });
            definition.FlowNodes.Add(new FlowNodeModel { Id = 140, Name = "Timed out", Type = BpmnFlowNodeTypes.EndEvent });
            definition.SequenceFlows.Add(new SequenceFlowModel { Id = 4001, SourceRef = 40, TargetRef = 140 });
            var instanceId = await InsertAndStartAsync(key, definition);
            var jobId = await SingleJobIdAsync(instanceId);

            var block = fixture.ServiceInvocations.BlockNext("/status/409");
            var processing = PromoteAndProcessAsync(jobId);
            await block.WaitUntilEnteredAsync(CancellationToken.None);
            try
            {
                long timerJobId;
                await using (var db = fixture.CreateDbContext())
                {
                    timerJobId = await db.WorkflowJobs
                        .Where(job => job.InstanceId == instanceId && job.Kind == WorkflowJobKinds.TimerBoundary)
                        .Select(job => job.Id)
                        .SingleAsync();
                }
                await PromoteAndProcessAsync(timerJobId, WorkflowJobClasses.Control);
            }
            finally
            {
                block.Release();
            }
            await processing;

            await using var result = fixture.CreateDbContext();
            Assert.Equal(WorkflowInstanceStatuses.Completed, await result.WorkflowInstances.Where(item => item.Id == instanceId).Select(item => item.Status).SingleAsync());
            var visited = await result.NodeExecutions.Where(item => item.InstanceId == instanceId).Select(item => item.NodeId).ToListAsync();
            Assert.Contains(140, visited);
            Assert.DoesNotContain(Conflict, visited);
            Assert.Equal("none", await LatestVariableAsync(result, instanceId, "failure"));
            Assert.Empty(await result.WorkflowIncidents.Where(item => item.JobId == jobId).ToListAsync());
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Durable_AWorkerThatLostItsLeaseCannotRouteASecondTime()
    {
        var key = Key();
        try
        {
            var path = $"status-sequence/{key}/409";
            var instanceId = await InsertAndStartAsync(key, Durable(key, path, JobFailureHandling.BoundaryFirst, retryDelays: ["PT1S"]));
            var jobId = await SingleJobIdAsync(instanceId);

            var lost = await LeaseAsync(jobId);
            await using (var expire = fixture.CreateDbContext())
            {
                await expire.WorkflowJobs
                    .Where(job => job.Id == jobId)
                    .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-5)));
            }
            var current = await LeaseAsync(jobId);
            Assert.NotEqual(lost.LeaseGeneration, current.LeaseGeneration);

            await Task.WhenAll(ProcessAsync(lost), ProcessAsync(current));

            await AssertRoutedAsync(instanceId, Conflict);
            await using var db = fixture.CreateDbContext();
            Assert.Equal(1, await db.NodeExecutions.CountAsync(item => item.InstanceId == instanceId && item.NodeId == Conflict));
            Assert.Equal(WorkflowJobStatuses.Completed, await JobStatusAsync(db, jobId));
            Assert.False(await db.WorkflowIncidents.AnyAsync(item => item.JobId == jobId));
            Assert.Equal(1, fixture.ServiceInvocations.AttemptCount("https://tests.local/" + path));
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Durable_AStagedResultRoutesAfterARestartWithoutCallingAgain()
    {
        var key = Key();
        try
        {
            var path = $"status-sequence/{key}/409";
            var instanceId = await InsertAndStartAsync(key, Durable(key, path, JobFailureHandling.BoundaryFirst));
            var jobId = await SingleJobIdAsync(instanceId);

            // A concurrent write stops finalization after the result is stored, which
            // leaves the same state as a Worker that died before it could finalize.
            var block = fixture.ServiceInvocations.BlockNext($"/{path}");
            var processing = PromoteAndProcessAsync(jobId);
            await block.WaitUntilEnteredAsync(CancellationToken.None);
            try
            {
                await using var write = fixture.CreateDbContext();
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
            finally
            {
                block.Release();
            }
            await processing;

            await using (var reset = fixture.CreateDbContext())
            {
                var stored = await reset.WorkflowJobs.Where(job => job.Id == jobId).Select(job => job.ResultJson).SingleAsync();
                Assert.Equal(409, stored!.RootElement.GetProperty("StatusCode").GetInt32());
                var snapshot = await reset.WorkflowJobSnapshots.SingleAsync(item =>
                    reset.WorkflowJobs.Any(job => job.Id == jobId && job.SnapshotId == item.Id));
                var latest = await reset.InstanceVariables
                    .Where(item => item.InstanceId == instanceId && item.VariableName == "failure")
                    .MaxAsync(item => item.Id);
                snapshot.OutputVariableVersionsJson = JsonDocument.Parse($$"""{ "failure": {{latest}} }""");
                await reset.SaveChangesAsync();
                await reset.WorkflowIncidents.Where(item => item.JobId == jobId).ExecuteDeleteAsync();
                await reset.WorkflowJobs
                    .Where(job => job.Id == jobId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(job => job.Status, WorkflowJobStatuses.ResultReady)
                        .SetProperty(job => job.WorkerId, "gone")
                        .SetProperty(job => job.LeaseToken, Guid.NewGuid())
                        .SetProperty(job => job.LeaseExpiresAt, DateTimeOffset.UtcNow.AddHours(-1)));
            }

            await PromoteAndProcessAsync(jobId);

            await using (var inspect = fixture.CreateDbContext())
            {
                Assert.Empty(await inspect.WorkflowIncidents
                    .Where(item => item.JobId == jobId)
                    .Select(item => item.Type + ": " + item.Details)
                    .ToListAsync());
            }
            await AssertRoutedAsync(instanceId, Conflict);
            Assert.Equal(1, fixture.ServiceInvocations.AttemptCount("https://tests.local/" + path));
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Durable_OldScriptCatchAllStillRoutesAfterRetriesRunOut()
    {
        var key = Key();
        try
        {
            var definition = Definition(key, "unused", [Unfiltered(CatchAll)]);
            var host = definition.FlowNodes.Single(node => node.Id == 10);
            host.Type = BpmnFlowNodeTypes.ScriptTask;
            host.Service = null;
            host.AsyncBefore = true;
            host.Job = new JobPolicyModel { FailureHandling = JobFailureHandling.RetryFirst, RetryDelays = [] };
            host.ScriptFormat = ScriptFormats.JavaScript;
            host.UsesFlowInfo = false;
            host.Script = "throw new Error('script failure');";
            var instanceId = await InsertAndStartAsync(key, definition);

            await PromoteAndProcessAsync(await SingleJobIdAsync(instanceId));

            await AssertRoutedAsync(instanceId, CatchAll);
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    // An integer division by zero throws and a floating one gives infinity, which no
    // variable can hold. Both are the script's own failure, so the catch-all routes them
    // in either execution mode instead of an unexpected exception escaping.
    [Theory]
    [InlineData(false, "1 % 0")]
    [InlineData(false, "1 / 0")]
    [InlineData(true, "1 % 0")]
    [InlineData(true, "0.0 / 0")]
    public async Task AnNCalcArithmeticFailureTakesTheScriptsCatchAll(bool asyncBefore, string expression)
    {
        var key = Key();
        try
        {
            var definition = Definition(key, "unused", [Unfiltered(CatchAll)]);
            definition.Variables.Add(new VariableModel
            {
                Id = 2,
                Name = "score",
                DataType = WorkflowVariableTypes.Number,
                DefaultValue = JsonSerializer.SerializeToElement(0)
            });
            var host = definition.FlowNodes.Single(node => node.Id == 10);
            host.Type = BpmnFlowNodeTypes.ScriptTask;
            host.Service = null;
            host.AsyncBefore = asyncBefore;
            host.Job = asyncBefore
                ? new JobPolicyModel { FailureHandling = JobFailureHandling.BoundaryFirst, RetryDelays = [] }
                : null;
            host.ScriptFormat = ScriptFormats.NCalc;
            host.Assignments = [new AssignmentModel { Variable = "score", Expression = expression }];
            var instanceId = await InsertAndStartAsync(key, definition);
            if (asyncBefore)
            {
                await PromoteAndProcessAsync(await SingleJobIdAsync(instanceId));
            }

            await AssertRoutedAsync(instanceId, CatchAll);
            await using var db = fixture.CreateDbContext();
            var failure = await LatestVariableAsync(db, instanceId, "failure");
            Assert.True(failure!.Contains("could not be evaluated", StringComparison.Ordinal)
                || failure.Contains("not a finite number", StringComparison.Ordinal), failure);
            Assert.Equal("0", await LatestVariableAsync(db, instanceId, "score"));
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Synchronous_AFailedHandlerPathRollsBackTheWholeTransition()
    {
        var key = Key();
        try
        {
            var definition = Definition(key, "status/409", [Exact(Conflict, 409)]);
            // A stored gateway with no default and no true condition fails in the
            // same transaction as the boundary that led to it.
            definition.FlowNodes.Add(new FlowNodeModel { Id = 50, Name = "Broken handler", Type = BpmnFlowNodeTypes.ExclusiveGateway });
            definition.SequenceFlows.Single(flow => flow.SourceRef == Conflict).TargetRef = 50;
            definition.SequenceFlows.Add(new SequenceFlowModel { Id = 5001, SourceRef = 50, TargetRef = 100 + Conflict, Condition = "1 == 2", ConditionPriority = 1 });
            definition.SequenceFlows.Add(new SequenceFlowModel { Id = 5002, SourceRef = 50, TargetRef = 100 + Conflict, Condition = "1 == 3", ConditionPriority = 2 });

            await Assert.ThrowsAsync<WorkflowDomainException>(() => InsertAndStartAsync(key, definition));

            await using var db = fixture.CreateDbContext();
            Assert.False(await db.WorkflowInstances.AnyAsync(item => item.WorkflowKey == key));
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    public async Task Durable_RetryFirstOpensAnIncidentWhenTheFinalAttemptMatchesNothing(int retries)
    {
        var key = Key();
        try
        {
            var path = $"status-sequence/{key}/500";
            var delays = retries == 0 ? new List<string>() : ["PT1S"];
            var instanceId = await InsertAndStartAsync(key, Durable(key, path, JobFailureHandling.RetryFirst, delays));
            var jobId = await SingleJobIdAsync(instanceId);

            for (var attempt = 0; attempt <= retries; attempt++)
            {
                await PromoteAndProcessAsync(jobId);
            }

            await using var db = fixture.CreateDbContext();
            var incident = await db.WorkflowIncidents.SingleAsync(item => item.JobId == jobId);
            Assert.Equal(WorkflowIncidentStatuses.Open, incident.Status);
            Assert.False(await db.NodeExecutions.AnyAsync(item => item.InstanceId == instanceId && item.NodeType == BpmnFlowNodeTypes.ErrorBoundaryEvent));
            Assert.Equal(retries + 1, fixture.ServiceInvocations.AttemptCount("https://tests.local/" + path));
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Theory]
    [InlineData("status/409", Conflict, true, null)]
    [InlineData("status/503", ServerErrors, true, null)]
    [InlineData("status/409", Conflict, false, null)]
    [InlineData("status/409", Conflict, true, ConditionalEventDeliveryModes.DurableAsync)]
    [InlineData("status/409", Conflict, false, ConditionalEventDeliveryModes.DurableAsync)]
    public async Task Durable_AMatchingBoundaryBeatsAConditionOnTheFailedCallsStatus(
        string path,
        int expectedBoundary,
        bool interrupting,
        string? deliveryMode)
    {
        var key = Key();
        try
        {
            var instanceId = await InsertAndStartAsync(
                key,
                WatchStatus(Durable(key, path, JobFailureHandling.BoundaryFirst), interrupting, deliveryMode));

            await PromoteAndProcessAsync(await SingleJobIdAsync(instanceId));

            await AssertRoutedAsync(instanceId, expectedBoundary);
            await using var db = fixture.CreateDbContext();
            await AssertStatusWatchDidNotFireAsync(db, instanceId);
            Assert.False(await db.WorkflowJobs.AnyAsync(job => job.InstanceId == instanceId && job.Kind == WorkflowJobKinds.ConditionalWake));
            Assert.Equal(
                ConditionalBoundarySubscriptionStatuses.Cancelled,
                await db.ConditionalBoundarySubscriptions.Where(item => item.InstanceId == instanceId).Select(item => item.Status).SingleAsync());
            // The real status is stored, and the error path can read it.
            Assert.Equal(path["status/".Length..], await LatestVariableJsonAsync(db, instanceId, "httpStatus"));
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Durable_AMatchingBoundaryBeatsAConditionOnItsOwnErrorVariable()
    {
        var key = Key();
        try
        {
            var instanceId = await InsertAndStartAsync(
                key,
                WatchStatus(Durable(key, "status/409", JobFailureHandling.BoundaryFirst), condition: "failure != 'none'"));

            await PromoteAndProcessAsync(await SingleJobIdAsync(instanceId));

            await AssertRoutedAsync(instanceId, Conflict);
            await using var db = fixture.CreateDbContext();
            await AssertStatusWatchDidNotFireAsync(db, instanceId);
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Theory]
    [InlineData(JobFailureHandling.BoundaryFirst)]
    [InlineData(JobFailureHandling.RetryFirst)]
    public async Task Durable_AnUnmatchedExhaustedFailureOpensAnIncidentInsteadOfTakingACondition(string policy)
    {
        var key = Key();
        try
        {
            var instanceId = await InsertAndStartAsync(key, WatchStatus(Durable(key, "status/500", policy, retryDelays: [])));
            var jobId = await SingleJobIdAsync(instanceId);

            await PromoteAndProcessAsync(jobId);

            await using var db = fixture.CreateDbContext();
            var incident = await db.WorkflowIncidents.SingleAsync(item => item.JobId == jobId);
            Assert.Equal("job_execution_failed", incident.Type);
            Assert.Equal(WorkflowIncidentStatuses.Open, incident.Status);
            Assert.Equal(
                WorkflowInstanceStatuses.Running,
                await db.WorkflowInstances.Where(item => item.Id == instanceId).Select(item => item.Status).SingleAsync());
            await AssertStatusWatchDidNotFireAsync(db, instanceId);
            // The status write rolled back with the attempt, so the condition is still armed.
            Assert.Equal("0", await LatestVariableJsonAsync(db, instanceId, "httpStatus"));
            Assert.Equal(
                ConditionalBoundarySubscriptionStatuses.Active,
                await db.ConditionalBoundarySubscriptions.Where(item => item.InstanceId == instanceId).Select(item => item.Status).SingleAsync());
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Durable_ARetriedFailureLeavesAConditionArmedForTheSuccessThatFollows()
    {
        var key = Key();
        try
        {
            var path = $"status-sequence/{key}/500,200";
            var instanceId = await InsertAndStartAsync(
                key,
                WatchStatus(Durable(key, path, JobFailureHandling.BoundaryFirst, retryDelays: ["PT1S"])));
            var jobId = await SingleJobIdAsync(instanceId);

            await PromoteAndProcessAsync(jobId);
            await using (var db = fixture.CreateDbContext())
            {
                Assert.Equal(WorkflowJobStatuses.Retry, await JobStatusAsync(db, jobId));
                await AssertStatusWatchDidNotFireAsync(db, instanceId);
                Assert.Equal("0", await LatestVariableJsonAsync(db, instanceId, "httpStatus"));
            }

            await PromoteAndProcessAsync(jobId);

            // Error capture only takes precedence over a failure: a successful call's
            // outputs are still observed before the host's normal exit.
            await using var result = fixture.CreateDbContext();
            Assert.Equal(WorkflowInstanceStatuses.Completed, await result.WorkflowInstances.Where(item => item.Id == instanceId).Select(item => item.Status).SingleAsync());
            var visited = await result.NodeExecutions.Where(item => item.InstanceId == instanceId).Select(item => item.NodeId).ToListAsync();
            Assert.Contains(100 + StatusWatch, visited);
            Assert.DoesNotContain(2, visited);
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Durable_ASiblingConditionalCatchStillObservesTheFailedCallsStatus()
    {
        var key = Key();
        try
        {
            var definition = WatchStatus(Durable(key, "status/409", JobFailureHandling.BoundaryFirst));
            definition.FlowNodes.Add(new FlowNodeModel { Id = 5, Name = "Fork", Type = BpmnFlowNodeTypes.ParallelGateway });
            definition.FlowNodes.Add(new FlowNodeModel
            {
                Id = 60,
                Name = "Waits for the status",
                Type = BpmnFlowNodeTypes.IntermediateConditionalCatchEvent,
                Conditional = new ConditionalDefinitionModel { Condition = "httpStatus == 409" }
            });
            definition.FlowNodes.Add(new FlowNodeModel { Id = 160, Name = "Caught", Type = BpmnFlowNodeTypes.EndEvent });
            definition.SequenceFlows.Single(flow => flow.Id == 101).TargetRef = 5;
            definition.SequenceFlows.Add(new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 10 });
            definition.SequenceFlows.Add(new SequenceFlowModel { Id = 502, SourceRef = 5, TargetRef = 60 });
            definition.SequenceFlows.Add(new SequenceFlowModel { Id = 6001, SourceRef = 60, TargetRef = 160 });
            var instanceId = await InsertAndStartAsync(key, definition);

            await PromoteAndProcessAsync(await SingleJobIdAsync(instanceId));

            await AssertRoutedAsync(instanceId, Conflict);
            await using var db = fixture.CreateDbContext();
            await AssertStatusWatchDidNotFireAsync(db, instanceId);
            Assert.True(await db.NodeExecutions.AnyAsync(item => item.InstanceId == instanceId && item.NodeId == 160));
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Theory]
    [InlineData(JobFailureHandling.BoundaryFirst, 1)]
    [InlineData(JobFailureHandling.RetryFirst, 2)]
    public async Task Durable_AScriptWriteThatFailsAtFinalizationFollowsTheHostsPolicy(string policy, int attempts)
    {
        var key = Key();
        var sharedKey = $"tests.error-match-script.{Guid.NewGuid():N}";
        try
        {
            // The binding has no rule, so the script passes its own stage. Only the
            // catalog's rule rejects the value, when finalization writes it.
            await CreateSharedVariableAsync(sharedKey, -1, "value < 0");
            var definition = Definition(key, "unused", [Unfiltered(CatchAll)]);
            definition.Variables.Add(SharedNumber("sharedNumber", sharedKey));
            var host = definition.FlowNodes.Single(node => node.Id == 10);
            host.Type = BpmnFlowNodeTypes.ScriptTask;
            host.Service = null;
            host.AsyncBefore = true;
            host.Job = new JobPolicyModel { FailureHandling = policy, RetryDelays = ["PT1S"] };
            host.ScriptFormat = ScriptFormats.JavaScript;
            host.UsesFlowInfo = false;
            host.Script = "execution.setVariable('sharedNumber', 5);";
            var instanceId = await InsertAndStartAsync(key, definition);
            var jobId = await SingleJobIdAsync(instanceId);

            for (var attempt = 1; attempt < attempts; attempt++)
            {
                await PromoteAndProcessAsync(jobId);
                await using var waiting = fixture.CreateDbContext();
                Assert.Equal(WorkflowJobStatuses.Retry, await JobStatusAsync(waiting, jobId));
            }
            await PromoteAndProcessAsync(jobId);

            await AssertRoutedAsync(instanceId, CatchAll);
            await using var db = fixture.CreateDbContext();
            Assert.Contains("Script task #10 failed", await LatestVariableAsync(db, instanceId, "failure"));
            var lines = fixture.ErrorBoundaryLogs.ForInstance(instanceId);
            Assert.Equal(attempts, lines.Count);
            Assert.Equal(
                $"Failed task #10 on instance {instanceId} with HTTP status (null) selected error boundary #{CatchAll} by CatchAll.",
                lines[^1].Message);
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    // A caught failure commits the transaction, so nothing from the refused batch may
    // come with it. The bindings have no rules, so only the catalog can refuse a value,
    // and the accepted key sorts first, so its write would already have happened.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AScriptBatchTheCatalogRefusesWritesNothing(bool asyncBefore)
    {
        var key = Key();
        var acceptedKey = $"tests.error-match-a.{Guid.NewGuid():N}";
        var refusedKey = $"tests.error-match-z.{Guid.NewGuid():N}";
        try
        {
            await CreateSharedVariableAsync(acceptedKey, -1, "value < 100");
            await CreateSharedVariableAsync(refusedKey, -1, "value < 0");
            var definition = RefusedBatchDefinition(key, "unused", acceptedKey, refusedKey);
            var host = definition.FlowNodes.Single(node => node.Id == 10);
            host.Type = BpmnFlowNodeTypes.ScriptTask;
            host.Service = null;
            host.AsyncBefore = asyncBefore;
            host.Job = asyncBefore
                ? new JobPolicyModel { FailureHandling = JobFailureHandling.BoundaryFirst, RetryDelays = [] }
                : null;
            host.ScriptFormat = ScriptFormats.JavaScript;
            host.UsesFlowInfo = false;
            host.Script = """
                execution.setVariable('score', 7);
                execution.setVariable('accepted', 5);
                execution.setVariable('refused', 5);
                """;
            var instanceId = await InsertAndStartAsync(key, definition);
            if (asyncBefore)
            {
                await PromoteAndProcessAsync(await SingleJobIdAsync(instanceId));
            }

            await AssertRefusedBatchWroteNothingAsync(instanceId, acceptedKey, refusedKey);
            await using var db = fixture.CreateDbContext();
            Assert.Contains("Script task #10 failed", await LatestVariableAsync(db, instanceId, "failure"));
        }
        finally
        {
            await DeleteRunnableWorkAsync(key);
        }
    }

    [Fact]
    public async Task Durable_AServiceOutputBatchTheCatalogRefusesWritesNothing()
    {
        var key = Key();
        var acceptedKey = $"tests.error-match-a.{Guid.NewGuid():N}";
        var refusedKey = $"tests.error-match-z.{Guid.NewGuid():N}";
        try
        {
            await CreateSharedVariableAsync(acceptedKey, -1, "value < 100");
            await CreateSharedVariableAsync(refusedKey, -1, "value < 0");
            var definition = RefusedBatchDefinition(key, "typed-output-success", acceptedKey, refusedKey);
            var host = definition.FlowNodes.Single(node => node.Id == 10);
            host.AsyncBefore = true;
            host.Job = new JobPolicyModel { FailureHandling = JobFailureHandling.BoundaryFirst, RetryDelays = [] };
            host.Service!.OutputMappings = new[] { "score", "accepted", "refused" }
                .Select(variable => new ServiceOutputMappingModel
                {
                    Variable = variable,
                    Path = "result.score",
                    Required = true,
                    DataType = WorkflowVariableTypes.Number,
                    IsArray = false
                })
                .ToList();
            var instanceId = await InsertAndStartAsync(key, definition);

            await PromoteAndProcessAsync(await SingleJobIdAsync(instanceId));

            await AssertRefusedBatchWroteNothingAsync(instanceId, acceptedKey, refusedKey);
        }
        finally
        {
            await DeleteRunnableWorkAsync(key);
        }
    }

    // If a batch does write, its shared-variable audit rows keep their instance. Leaving the
    // instance lets the assertion's message through instead of a failed cleanup's.
    private async Task DeleteRunnableWorkAsync(string workflowKey)
    {
        await using var cleanup = fixture.CreateDbContext();
        await cleanup.WorkflowIncidents.Where(item => item.WorkflowKey == workflowKey).ExecuteDeleteAsync();
        await cleanup.WorkflowJobs.Where(item => item.WorkflowKey == workflowKey).ExecuteDeleteAsync();
    }

    private static WorkflowModel RefusedBatchDefinition(string key, string path, string acceptedKey, string refusedKey)
    {
        var definition = Definition(key, path, [Unfiltered(CatchAll)]);
        definition.Variables.Add(new VariableModel
        {
            Id = 2,
            Name = "score",
            DataType = WorkflowVariableTypes.Number,
            DefaultValue = JsonSerializer.SerializeToElement(0)
        });
        definition.Variables.Add(SharedNumber("accepted", acceptedKey));
        var refused = SharedNumber("refused", refusedKey);
        refused.Id = 4;
        definition.Variables.Add(refused);
        return definition;
    }

    private async Task AssertRefusedBatchWroteNothingAsync(long instanceId, string acceptedKey, string refusedKey)
    {
        await AssertRoutedAsync(instanceId, CatchAll);
        await using var db = fixture.CreateDbContext();
        Assert.Equal("0", await LatestVariableJsonAsync(db, instanceId, "score"));
        var shared = await db.SharedVariables
            .Include(item => item.CurrentValue)
            .Where(item => item.Key == acceptedKey || item.Key == refusedKey)
            .ToListAsync();
        Assert.Equal(2, shared.Count);
        Assert.All(shared, item => Assert.Equal("-1", item.CurrentValue!.ValueJson!.RootElement.GetRawText()));
    }

    [Fact]
    public async Task Durable_AFailedStatusWriteStillSelectsByTheReceivedStatus()
    {
        var key = Key();
        var sharedKey = $"tests.error-match-status.{Guid.NewGuid():N}";
        try
        {
            await CreateSharedVariableAsync(sharedKey, -1, "value < 0");
            var definition = Durable(key, "status/409", JobFailureHandling.BoundaryFirst);
            definition.Variables.Add(SharedNumber("sharedStatus", sharedKey));
            definition.FlowNodes.Single(node => node.Id == 10).Service!.StatusVariable = "sharedStatus";
            var instanceId = await InsertAndStartAsync(key, definition);

            await PromoteAndProcessAsync(await SingleJobIdAsync(instanceId));

            await AssertRoutedAsync(instanceId, Conflict);
            await using var db = fixture.CreateDbContext();
            Assert.DoesNotContain("REST call failed", await LatestVariableAsync(db, instanceId, "failure"));
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Logs_ASelectionWithOnlyItsIdentifiersStatusAndTier()
    {
        var key = Key();
        try
        {
            var instanceId = await InsertAndStartAsync(
                key,
                Definition(key, "status/409", [Exact(Conflict, 409), Range(ClientErrors, 400, 499), Unfiltered(CatchAll)]));

            var line = Assert.Single(fixture.ErrorBoundaryLogs.ForInstance(instanceId));

            Assert.Equal(LogLevel.Information, line.Level);
            Assert.Equal($"Failed task #10 on instance {instanceId} with HTTP status 409 selected error boundary #{Conflict} by ExactStatus.", line.Message);
            Assert.Equal(
                ["BoundaryId", "HttpStatus", "InstanceId", "MatchTier", "NodeId", "{OriginalFormat}"],
                line.Fields.Keys.Order(StringComparer.Ordinal));
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Logs_AFailureWithNoResponseStatusReachingTheCatchAll()
    {
        var key = Key();
        try
        {
            var instanceId = await InsertAndStartAsync(key, Definition(key, "no-response", [Exact(Conflict, 409), Unfiltered(CatchAll)]));

            var line = Assert.Single(fixture.ErrorBoundaryLogs.ForInstance(instanceId));

            Assert.Equal($"Failed task #10 on instance {instanceId} with HTTP status (null) selected error boundary #{CatchAll} by CatchAll.", line.Message);
            Assert.DoesNotContain("timed out", line.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Logs_OneLinePerAttemptAndSaysWhenARetryComesFirst()
    {
        var key = Key();
        try
        {
            var instanceId = await InsertAndStartAsync(
                key,
                Durable(key, $"status-sequence/{key}/409,503", JobFailureHandling.RetryFirst, retryDelays: ["PT1S"]));
            var jobId = await SingleJobIdAsync(instanceId);

            await PromoteAndProcessAsync(jobId);
            await PromoteAndProcessAsync(jobId);

            var lines = fixture.ErrorBoundaryLogs.ForInstance(instanceId);
            Assert.Equal(2, lines.Count);
            Assert.All(lines, line => Assert.Equal(LogLevel.Information, line.Level));
            Assert.Equal($"Failed task #10 on instance {instanceId} with HTTP status 409 matches error boundary #{Conflict} by ExactStatus, but will retry first.", lines[0].Message);
            Assert.Equal($"Failed task #10 on instance {instanceId} with HTTP status 503 selected error boundary #{ServerErrors} by StatusRange.", lines[1].Message);
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Logs_AnUnmatchedFailureAsAWarningWithoutABoundary()
    {
        var key = Key();
        try
        {
            var instanceId = await InsertAndStartAsync(key, Durable(key, "status/500", JobFailureHandling.BoundaryFirst, retryDelays: []));

            await PromoteAndProcessAsync(await SingleJobIdAsync(instanceId));

            var line = Assert.Single(fixture.ErrorBoundaryLogs.ForInstance(instanceId));
            Assert.Equal(LogLevel.Warning, line.Level);
            Assert.Equal($"Failed task #10 on instance {instanceId} with HTTP status 500 matched no error boundary; it has 3 attached.", line.Message);
            Assert.Equal(
                ["BoundaryCount", "HttpStatus", "InstanceId", "NodeId", "{OriginalFormat}"],
                line.Fields.Keys.Order(StringComparer.Ordinal));
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task VersionChange_PreviewApplyAndBatchAgreeOnMatcherContracts()
    {
        var key = Key();
        try
        {
            await SetWorkflowRequiredRoleAsync("admin");
            var source = await CreateAsync(Durable(key, "status/409", JobFailureHandling.BoundaryFirst));
            var instanceId = await StartAsync(source.Id);

            var changed = Durable(key, "status/409", JobFailureHandling.BoundaryFirst);
            changed.FlowNodes.Single(node => node.Id == Conflict).ErrorMatch = new ErrorBoundaryMatchModel { HttpStatusCodes = [410] };
            var changedTarget = await CreateAsync(changed);

            // The same selectors in another shape: an empty array that means nothing.
            var equivalent = Durable(key, "status/409", JobFailureHandling.BoundaryFirst);
            equivalent.FlowNodes.Single(node => node.Id == ClientErrors).ErrorMatch = new ErrorBoundaryMatchModel
            {
                HttpStatusCodes = [],
                HttpStatusRanges = [new HttpStatusRangeModel { From = 400, To = 408 }]
            };
            var equivalentTarget = await CreateAsync(equivalent);

            var blocked = await PreviewAsync(instanceId, changedTarget.Id);
            Assert.False(blocked.Compatible);
            Assert.Contains(blocked.Blockers, item => item.Code == WorkflowVersionCompatibilityCodes.OpenJobContractChanged);
            using (var refused = await SendAsync(
                       HttpMethod.Post,
                       $"/api/instances/{instanceId}/version-change",
                       new ChangeInstanceVersionRequest(changedTarget.Id, blocked.ExpectedSourceWorkflowId, blocked.ExpectedUpdatedAt, "matcher changed")))
            {
                Assert.False(refused.IsSuccessStatusCode);
            }

            var blockedItem = await PrepareBatchItemAsync(source.Id, changedTarget.Id, instanceId);
            Assert.Equal(InstanceVersionChangeBatchItemStatuses.Ineligible, blockedItem.Status);
            Assert.Contains(blockedItem.Blockers, item => item.Code == WorkflowVersionCompatibilityCodes.OpenJobContractChanged);

            var eligibleItem = await PrepareBatchItemAsync(source.Id, equivalentTarget.Id, instanceId);
            Assert.Equal(InstanceVersionChangeBatchItemStatuses.Eligible, eligibleItem.Status);

            // The batch path applies the same contract to another open activation.
            var batchedId = await StartAsync(source.Id);
            var batchedItem = await PrepareBatchItemAsync(source.Id, equivalentTarget.Id, batchedId);
            Assert.Equal(InstanceVersionChangeBatchItemStatuses.Eligible, batchedItem.Status);
            InstanceVersionChangeBatchDetailDto batch;
            using (var read = await SendAsync(HttpMethod.Get, $"/api/instance-version-change-batches/{batchedItem.BatchId}", null))
            {
                batch = (await read.Content.ReadFromJsonAsync<InstanceVersionChangeBatchDetailDto>())!;
            }
            using (var confirmed = await SendAsync(
                       HttpMethod.Post,
                       $"/api/instance-version-change-batches/{batchedItem.BatchId}/confirm",
                       new ConfirmInstanceVersionChangeBatchRequest(
                           batch.Summary.EligibleItemCount,
                           batch.Summary.IneligibleItemCount,
                           batch.Summary.WarningItemCount,
                           batch.Summary.UpdatedAt)))
            {
                Assert.Equal(HttpStatusCode.OK, confirmed.StatusCode);
                batch = (await confirmed.Content.ReadFromJsonAsync<InstanceVersionChangeBatchDetailDto>())!;
            }
            await PromoteAndProcessAsync(batch.ExecutionJobId!.Value);
            await using (var db = fixture.CreateDbContext())
            {
                Assert.Equal(
                    equivalentTarget.Id,
                    await db.WorkflowInstances.Where(item => item.Id == batchedId).Select(item => item.WorkflowDefinitionId).SingleAsync());
            }

            var allowed = await PreviewAsync(instanceId, equivalentTarget.Id);
            Assert.True(allowed.Compatible);
            using var applied = await SendAsync(
                HttpMethod.Post,
                $"/api/instances/{instanceId}/version-change",
                new ChangeInstanceVersionRequest(equivalentTarget.Id, allowed.ExpectedSourceWorkflowId, allowed.ExpectedUpdatedAt, "same contract"));
            Assert.Equal(HttpStatusCode.OK, applied.StatusCode);
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Fact]
    public async Task Api_RoundTripsAMatcherInCanonicalOrder()
    {
        var key = Key();
        try
        {
            var model = Definition(key, "status/409", [
                new FlowNodeModel
                {
                    Id = Conflict,
                    Name = "Listed out of order",
                    Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
                    AttachedToRef = 10,
                    ErrorMatch = new ErrorBoundaryMatchModel { HttpStatusCodes = [503, 409] }
                }]);
            using var created = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            var detail = await created.Content.ReadFromJsonAsync<WorkflowDetailDto>();

            using var read = await SendAsync(HttpMethod.Get, $"/api/workflows/{detail!.Id}", null);
            var saved = (await read.Content.ReadFromJsonAsync<WorkflowDetailDto>())!
                .Definition.FlowNodes.Single(node => node.Id == Conflict);

            Assert.Equal([409, 503], saved.ErrorMatch!.HttpStatusCodes);
        }
        finally
        {
            await DeleteWorkflowAsync(key);
        }
    }

    [Theory]
    [InlineData("""{ "httpStatusCodes": ["409"] }""", "The value has the wrong JSON type. Path: $.Definition.flowNodes[3].errorMatch.httpStatusCodes[0]")]
    [InlineData("""{ "httpStatusRangez": [{ "from": 400, "to": 499 }] }""", "Unknown property 'httpStatusRangez'. Path: $.Definition.flowNodes[3].errorMatch.httpStatusRangez")]
    [InlineData("""{ "httpStatusCodes": [null] }""", "The value has the wrong JSON type. Path: $.Definition.flowNodes[3].errorMatch.httpStatusCodes[0]")]
    public async Task Api_BindingRejectsWhatWouldChangeRoutingNamesTheValueAndWritesNothing(string matcher, string expectedError)
    {
        var key = Key();
        var body = JsonSerializer.Serialize(new CreateWorkflowRequest(
            Definition(key, "status/409", [Unfiltered(Conflict)]), false));
        var withMatcher = body.Replace(
            "\"attachedToRef\":10",
            "\"attachedToRef\":10,\"errorMatch\":" + matcher,
            StringComparison.Ordinal);
        Assert.NotEqual(body, withMatcher);

        foreach (var (method, path) in new[] { (HttpMethod.Post, "/api/workflows"), (HttpMethod.Put, "/api/workflows/1") })
        {
            using var request = new HttpRequestMessage(method, path)
            {
                Content = new StringContent(withMatcher, Encoding.UTF8, "application/json")
            };
            request.Headers.Add("X-Test-User", "designer");
            using var response = await fixture.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            var error = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(expectedError, error.GetProperty("error").GetString());
        }

        await using var db = fixture.CreateDbContext();
        Assert.False(await db.WorkflowDefinitions.AnyAsync(item => item.WorkflowKey == key));
    }

    [Fact]
    public async Task Api_ABodyThatIsNotJsonReportsTheProblemOnAnyRoute()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/instances")
        {
            Content = new StringContent("{ not json", Encoding.UTF8, "application/json")
        };
        request.Headers.Add("X-Test-User", "designer");

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("Path: $", error.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Api_AQueryValueThatCannotBeBoundReportsTheProblem()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/instances/inbox?page=not-a-number");
        request.Headers.Add("X-Test-User", "designer");

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var error = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Contains("not-a-number", error.GetProperty("error").GetString());
    }

    [Fact]
    public async Task Api_AnUnsupportedMediaTypeKeepsItsStatus()
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/workflows")
        {
            Content = new StringContent("{}", Encoding.UTF8, "text/plain")
        };
        request.Headers.Add("X-Test-User", "designer");

        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
    }

    [Fact]
    public async Task Api_SemanticErrorNamesTheBoundaryAndWritesNothing()
    {
        var key = Key();
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(Definition(key, "status/409", [Exact(Conflict, 409), Exact(ClientErrors, 409)]), false));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        Assert.Contains("#30", text);
        Assert.Contains("#31", text);
        await using var db = fixture.CreateDbContext();
        Assert.False(await db.WorkflowDefinitions.AnyAsync(item => item.WorkflowKey == key));
    }

    private static string Key() => $"error-match-{Guid.NewGuid():N}";

    private static FlowNodeModel Exact(int id, params int[] codes) => Boundary(id, new ErrorBoundaryMatchModel { HttpStatusCodes = codes.ToList() });

    private static FlowNodeModel Range(int id, int from, int to) => Boundary(id, new ErrorBoundaryMatchModel
    {
        HttpStatusRanges = [new HttpStatusRangeModel { From = from, To = to }]
    });

    private static FlowNodeModel Unfiltered(int id) => Boundary(id, null);

    private static FlowNodeModel Boundary(int id, ErrorBoundaryMatchModel? match) => new()
    {
        Id = id,
        Name = "Boundary " + id,
        Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
        AttachedToRef = 10,
        ErrorVariable = "failure",
        ErrorMatch = match
    };

    // Declares httpStatus as the host's status variable, and attaches a conditional
    // boundary that a stored status would trigger.
    private static WorkflowModel WatchStatus(
        WorkflowModel definition,
        bool interrupting = true,
        string? deliveryMode = null,
        string condition = "httpStatus > 0")
    {
        definition.Variables.Add(new VariableModel
        {
            Id = 2,
            Name = "httpStatus",
            DataType = WorkflowVariableTypes.Number,
            DefaultValue = JsonSerializer.SerializeToElement(0)
        });
        definition.FlowNodes.Single(node => node.Id == 10).Service!.StatusVariable = "httpStatus";
        definition.FlowNodes.Add(new FlowNodeModel
        {
            Id = StatusWatch,
            Name = "Status watch",
            Type = BpmnFlowNodeTypes.ConditionalBoundaryEvent,
            AttachedToRef = 10,
            CancelActivity = interrupting,
            Conditional = new ConditionalDefinitionModel { Condition = condition, DeliveryMode = deliveryMode }
        });
        definition.FlowNodes.Add(new FlowNodeModel { Id = 100 + StatusWatch, Name = "Conditional end", Type = BpmnFlowNodeTypes.EndEvent });
        definition.SequenceFlows.Add(new SequenceFlowModel { Id = StatusWatch * 100 + 1, SourceRef = StatusWatch, TargetRef = 100 + StatusWatch });
        return definition;
    }

    private static async Task AssertStatusWatchDidNotFireAsync(AppDbContext db, long instanceId)
    {
        var visited = await db.NodeExecutions
            .Where(item => item.InstanceId == instanceId)
            .Select(item => item.NodeId)
            .ToListAsync();
        Assert.DoesNotContain(StatusWatch, visited);
        Assert.DoesNotContain(100 + StatusWatch, visited);
    }

    private static VariableModel SharedNumber(string name, string sharedKey) => new()
    {
        Id = 3,
        Name = name,
        Scope = VariableScopes.Shared,
        SharedKey = sharedKey,
        Access = SharedVariableAccessModes.ReadWrite,
        DataType = WorkflowVariableTypes.Number
    };

    private async Task CreateSharedVariableAsync(string sharedKey, int value, string validation)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/shared-variables")
        {
            Content = JsonContent.Create(new CreateSharedVariableRequest(
                sharedKey,
                WorkflowVariableTypes.Number,
                IsArray: false,
                Nullable: false,
                HasValue: true,
                JsonSerializer.SerializeToElement(value),
                validation))
        };
        ApiTestAuth.Authorize(request, "error-match-admin", "admin");
        using var response = await fixture.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task<WorkflowDetailDto> CreateAsync(WorkflowModel model)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<WorkflowDetailDto>())!;
    }

    private async Task<InstanceVersionChangePreviewDto> PreviewAsync(long instanceId, long targetId)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{instanceId}/version-change/preview",
            new PreviewInstanceVersionChangeRequest(targetId));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<InstanceVersionChangePreviewDto>())!;
    }

    private async Task<InstanceVersionChangeBatchItemDto> PrepareBatchItemAsync(long sourceId, long targetId, long instanceId)
    {
        InstanceVersionChangeBatchDetailDto batch;
        using (var created = await SendAsync(
                   HttpMethod.Post,
                   "/api/instance-version-change-batches",
                   new CreateInstanceVersionChangeBatchRequest(
                       sourceId,
                       targetId,
                       "matcher contract",
                       new InstanceVersionChangeBatchSelectionDto(
                           InstanceVersionChangeBatchSelectionModes.Explicit,
                           [instanceId],
                           null,
                           null),
                       $"error-match-{Guid.NewGuid():N}")))
        {
            Assert.Equal(HttpStatusCode.Accepted, created.StatusCode);
            batch = (await created.Content.ReadFromJsonAsync<InstanceVersionChangeBatchDetailDto>())!;
        }

        await PromoteAndProcessAsync(batch.PreparationJobId!.Value);

        using var items = await SendAsync(
            HttpMethod.Get,
            $"/api/instance-version-change-batches/{batch.Summary.Id}/items?page=1&pageSize=10",
            null);
        Assert.Equal(HttpStatusCode.OK, items.StatusCode);
        return Assert.Single((await items.Content.ReadFromJsonAsync<PagedResult<InstanceVersionChangeBatchItemDto>>())!.Items);
    }

    private async Task SetWorkflowRequiredRoleAsync(string value)
    {
        await using var db = fixture.CreateDbContext();
        var setting = await db.EngineSettings.SingleOrDefaultAsync(item =>
            item.Namespace == "Workflow" && item.Key == "RequiredRole");
        if (setting is null)
        {
            db.EngineSettings.Add(new EngineSettingEntity { Namespace = "Workflow", Key = "RequiredRole", Value = value });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync();
    }

    private static WorkflowModel Durable(string key, string path, string policy, List<string>? retryDelays = null)
    {
        var definition = Definition(
            key,
            path,
            [Exact(Conflict, 409), Range(ClientErrors, 400, 408), Range(ServerErrors, 501, 599)]);
        var host = definition.FlowNodes.Single(node => node.Id == 10);
        host.AsyncBefore = true;
        host.Job = new JobPolicyModel { FailureHandling = policy, RetryDelays = retryDelays };
        return definition;
    }

    private static WorkflowModel Definition(string key, string path, IReadOnlyList<FlowNodeModel> boundaries)
    {
        var endNodes = boundaries.Select(boundary => new FlowNodeModel
        {
            Id = 100 + boundary.Id,
            Name = "After " + boundary.Id,
            Type = BpmnFlowNodeTypes.EndEvent
        });
        return new WorkflowModel
        {
            Id = key,
            Name = key,
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "failure",
                    DataType = WorkflowVariableTypes.String,
                    DefaultValue = JsonSerializer.SerializeToElement("none")
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Normal end", Type = BpmnFlowNodeTypes.EndEvent },
                new FlowNodeModel
                {
                    Id = 10,
                    Name = "Call",
                    Type = BpmnFlowNodeTypes.ServiceTask,
                    Service = new ServiceTaskModel { Url = "https://tests.local/" + path, Method = "GET", TimeoutSeconds = 5 }
                },
                .. boundaries,
                .. endNodes
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 10 },
                new SequenceFlowModel { Id = 1001, SourceRef = 10, TargetRef = 2 },
                .. boundaries.Select(boundary => new SequenceFlowModel
                {
                    Id = boundary.Id * 100 + 1,
                    SourceRef = boundary.Id,
                    TargetRef = 100 + boundary.Id
                })
            ]
        };
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
        var engine = scope.ServiceProvider.GetRequiredService<IWorkflowEngineService>();
        var started = await engine.StartInstanceSlimAsync(
            workflowId,
            null,
            new ActorContext("starter", ["User", "admin"], new Dictionary<string, string>()),
            null,
            null,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);
        return started.Id;
    }

    private async Task AssertRoutedAsync(long instanceId, int expectedBoundary)
    {
        await using var db = fixture.CreateDbContext();
        Assert.Equal(
            WorkflowInstanceStatuses.Completed,
            await db.WorkflowInstances.Where(item => item.Id == instanceId).Select(item => item.Status).SingleAsync());
        var visited = await db.NodeExecutions
            .Where(item => item.InstanceId == instanceId)
            .Select(item => item.NodeId)
            .ToListAsync();
        Assert.Contains(expectedBoundary, visited);
        Assert.Contains(100 + expectedBoundary, visited);
        Assert.DoesNotContain(2, visited);
        Assert.DoesNotContain(visited, id => id is >= Conflict and <= CatchAll && id != expectedBoundary);
    }

    private static async Task<string?> LatestVariableAsync(AppDbContext db, long instanceId, string name)
    {
        var raw = await LatestVariableJsonAsync(db, instanceId, name);
        return raw is null ? null : JsonDocument.Parse(raw).RootElement.ToString();
    }

    private static async Task<string?> LatestVariableJsonAsync(AppDbContext db, long instanceId, string name)
    {
        var values = await db.InstanceVariables
            .Where(item => item.InstanceId == instanceId && item.VariableName == name)
            .OrderByDescending(item => item.Id)
            .Select(item => item.ValueJson)
            .Take(1)
            .ToListAsync();
        return values.Count == 0 ? null : values[0].RootElement.GetRawText();
    }

    private static Task<string> JobStatusAsync(AppDbContext db, long jobId) =>
        db.WorkflowJobs.Where(job => job.Id == jobId).Select(job => job.Status).SingleAsync();

    private async Task<long> SingleJobIdAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowJobs
            .Where(job => job.InstanceId == instanceId && job.Kind == WorkflowJobKinds.AsyncBefore)
            .Select(job => job.Id)
            .SingleAsync();
    }

    private async Task PromoteAndProcessAsync(long jobId, string queueClass = WorkflowJobClasses.Activity) =>
        await ProcessAsync(await LeaseAsync(jobId, queueClass));

    private async Task ProcessAsync(WorkflowJobLeaseRecord lease)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>()
            .ProcessAsync(lease, CancellationToken.None);
    }

    private async Task<WorkflowJobLeaseRecord> LeaseAsync(long jobId, string queueClass = WorkflowJobClasses.Activity)
    {
        await using (var promote = fixture.CreateDbContext())
        {
            // Due a minute ago, so the job is due even when the database clock runs behind this machine's.
            var changed = await promote.WorkflowJobs
                .Where(job => job.Id == jobId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Priority, 1_000_000)
                    .SetProperty(job => job.DueAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
            Assert.Equal(1, changed);
        }

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>();
        var leases = await repository.LeaseRunnableAsync(
            new WorkflowJobLeaseRequest(
                $"error-match-test:{Guid.NewGuid():N}",
                MaxCount: 1,
                MaxActivityCount: queueClass == WorkflowJobClasses.Activity ? 1 : 0,
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
        request.Headers.Add("X-Test-User", "designer");
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }
        return await fixture.Client.SendAsync(request);
    }

    private async Task DeleteWorkflowAsync(string workflowKey)
    {
        await using var cleanup = fixture.CreateDbContext();
        await cleanup.WorkflowInstanceVersionChanges
            .Where(item => cleanup.WorkflowInstances.Any(instance => instance.Id == item.InstanceId && instance.WorkflowKey == workflowKey))
            .ExecuteDeleteAsync();
        await cleanup.WorkflowInstanceVersionChangeBatchItems.Where(item => item.Batch!.WorkflowKey == workflowKey).ExecuteDeleteAsync();
        await cleanup.WorkflowInstanceVersionChangeBatches.Where(item => item.WorkflowKey == workflowKey).ExecuteDeleteAsync();
        await cleanup.WorkflowIncidents.Where(item => item.WorkflowKey == workflowKey).ExecuteDeleteAsync();
        await cleanup.WorkflowJobs.Where(item => item.WorkflowKey == workflowKey).ExecuteDeleteAsync();
        await cleanup.WorkflowInstances.Where(item => item.WorkflowKey == workflowKey).ExecuteDeleteAsync();
        await cleanup.WorkflowDefinitions.Where(item => item.WorkflowKey == workflowKey).ExecuteDeleteAsync();
    }
}
