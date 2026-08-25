using System.Net;
using System.Net.Http.Json;
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
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class SharedVariableRuntimeTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AsyncServiceSnapshotPersistsFrozenSharedInputRevisionSeparatelyFromOutputFence()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var sharedKey = $"tests.fx-rate.{suffix}";
        using (var create = await SendAsync(
                   HttpMethod.Post,
                   "/api/shared-variables",
                   new CreateSharedVariableRequest(
                       sharedKey,
                       WorkflowVariableTypes.Number,
                       IsArray: false,
                       Nullable: false,
                       HasValue: true,
                       JsonSerializer.SerializeToElement(3.75m))))
        {
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        }

        long sharedRevision;
        await using (var catalog = fixture.CreateDbContext())
        {
            sharedRevision = (await catalog.SharedVariables.SingleAsync(variable =>
                variable.Key == sharedKey)).CurrentRevision;
        }

        var workflowId = await CreateWorkflowAsync(
            CreateAsyncServiceReader($"shared-async-service-{suffix}", sharedKey));
        var instance = await StartAsync(workflowId);

        long jobId;
        await using (var before = fixture.CreateDbContext())
        {
            jobId = (await before.WorkflowJobs.SingleAsync(job =>
                job.InstanceId == instance.Id
                && job.Kind == WorkflowJobKinds.AsyncBefore)).Id;
        }

        await ProcessWorkflowJobAsync(jobId, maxActivityCount: 1);

        long snapshotId;
        await using (var completed = fixture.CreateDbContext())
        {
            var job = await completed.WorkflowJobs.SingleAsync(item => item.Id == jobId);
            Assert.Equal(WorkflowJobStatuses.Completed, job.Status);
            snapshotId = Assert.IsType<long>(job.SnapshotId);
            Assert.NotNull((await completed.WorkflowJobSnapshots.SingleAsync(snapshot =>
                snapshot.Id == snapshotId)).SharedVariableRevisionsJson);
        }

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>();
        var snapshotRecord = await jobs.GetSnapshotAsync(
            snapshotId,
            CancellationToken.None);
        Assert.NotNull(snapshotRecord);
        var sharedRevisions = snapshotRecord!.SharedVariableRevisions;
        Assert.NotNull(sharedRevisions);
        Assert.Equal(sharedRevision, Assert.Single(sharedRevisions!).Value);
        Assert.Equal(sharedRevision, sharedRevisions["fxRate"]);
        Assert.Equal(3.75m, snapshotRecord.Variables["fxRate"].GetDecimal());
        Assert.Contains("decision", snapshotRecord.OutputVariableVersions.Keys);
        Assert.DoesNotContain("fxRate", snapshotRecord.OutputVariableVersions.Keys);
    }

    [Fact]
    public async Task CrossWorkflowWriteDurablyWakesConditionalWaitAndNoOpSuppressesExpansion()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var sharedKey = $"tests.release.{suffix}";
        var waiterKey = $"shared-waiter-{suffix}";
        var writerKey = $"shared-writer-{suffix}";

        using (var create = await SendAsync(
                   HttpMethod.Post,
                   "/api/shared-variables",
                   new CreateSharedVariableRequest(
                       sharedKey,
                       WorkflowVariableTypes.Boolean,
                       IsArray: false,
                       Nullable: false,
                       HasValue: true,
                       JsonSerializer.SerializeToElement(false))))
        {
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        }

        // Drain the creation revision before definitions bind the key. This
        // isolates the assertion to the workflow-originated value change.
        await ProcessSharedWakeOutboxAsync();

        long initialWakeCount;
        await using (var initial = fixture.CreateDbContext())
        {
            var variable = await initial.SharedVariables.SingleAsync(item => item.Key == sharedKey);
            initialWakeCount = await initial.SharedVariableWakes.LongCountAsync(item =>
                item.SharedVariableId == variable.Id);
        }

        var waiterWorkflowId = await CreateWorkflowAsync(
            CreateWaiter(waiterKey, sharedKey));
        var writerWorkflowId = await CreateWorkflowAsync(
            CreateWriter(writerKey, sharedKey));
        var waiter = await StartAsync(waiterWorkflowId);
        var writer = await StartAsync(writerWorkflowId);

        await using (var before = fixture.CreateDbContext())
        {
            Assert.Equal(
                WorkflowInstanceStatuses.Running,
                (await before.WorkflowInstances.SingleAsync(item => item.Id == waiter.Id)).Status);
            var token = await before.ExecutionTokens.SingleAsync(item =>
                item.InstanceId == waiter.Id
                && item.Status == ExecutionTokenStatuses.Active);
            Assert.Equal(2, token.NodeId);
            Assert.Null(token.WaitState);
            Assert.Null(token.WaitingJobId);
        }

        long writerTaskId;
        await using (var db = fixture.CreateDbContext())
        {
            writerTaskId = (await db.UserTasks.SingleAsync(task =>
                task.InstanceId == writer.Id
                && task.Status == UserTaskStatuses.Active)).Id;
        }
        using (var write = await SendAsync(
                   HttpMethod.Post,
                   $"/api/user-tasks/{writerTaskId}/flows/201",
                   new TakeFlowRequest(new Dictionary<string, JsonElement>
                   {
                       ["release"] = JsonSerializer.SerializeToElement(true),
                       ["localNote"] = JsonSerializer.SerializeToElement("retained locally")
                   }),
                   user: "writer"))
        {
            Assert.Equal(HttpStatusCode.OK, write.StatusCode);
        }

        long wakeCountAfterChange;
        await using (var changed = fixture.CreateDbContext())
        {
            var variable = await changed.SharedVariables.SingleAsync(item => item.Key == sharedKey);
            wakeCountAfterChange = await changed.SharedVariableWakes.LongCountAsync(item =>
                item.SharedVariableId == variable.Id);
            Assert.Equal(initialWakeCount + 1, wakeCountAfterChange);
            Assert.True((await changed.SharedVariableCurrentValues.SingleAsync(item =>
                item.SharedVariableId == variable.Id)).ValueJson!.RootElement.GetBoolean());
            Assert.Contains(await changed.SharedVariableRevisions
                    .Where(item => item.SharedVariableId == variable.Id)
                    .ToListAsync(),
                revision => revision.InstanceId == writer.Id
                    && revision.NodeExecutionId is not null
                    && revision.ValueChanged);
            var completedTask = await changed.UserTasks.SingleAsync(task =>
                task.Id == writerTaskId);
            Assert.Equal(
                "retained locally",
                completedTask.ResultJson!.RootElement.GetProperty("localNote").GetString());
            Assert.False(completedTask.ResultJson.RootElement.TryGetProperty("release", out _));

            var actionHistory = await changed.InstanceHistory.SingleAsync(item =>
                item.InstanceId == writer.Id
                && item.UserTaskId == writerTaskId
                && item.ActionId == 201);
            Assert.Equal(
                "retained locally",
                actionHistory.Payload!.RootElement.GetProperty("localNote").GetString());
            Assert.False(actionHistory.Payload.RootElement.TryGetProperty("release", out _));
            var historyCorrelation = Assert.Single(
                actionHistory.SharedVariableWritesJson!.RootElement.EnumerateArray());
            Assert.Equal("release", historyCorrelation.GetProperty("alias").GetString());
            Assert.Equal(sharedKey, historyCorrelation.GetProperty("key").GetString());
            Assert.True(historyCorrelation.GetProperty("valueChanged").GetBoolean());

            var occurrence = await changed.SequenceFlowOccurrences.SingleAsync(item =>
                item.InstanceId == writer.Id && item.SequenceFlowId == 201);
            Assert.Equal(
                "retained locally",
                occurrence.ValuesJson!.RootElement.GetProperty("localNote").GetString());
            Assert.False(occurrence.ValuesJson.RootElement.TryGetProperty("release", out _));
            var occurrenceCorrelation = Assert.Single(
                occurrence.SharedVariableWritesJson!.RootElement.EnumerateArray());
            Assert.Equal("release", occurrenceCorrelation.GetProperty("alias").GetString());
            Assert.True(occurrenceCorrelation.GetProperty("revision").GetInt64() > 0);
            Assert.False(await changed.WorkflowJobs.AnyAsync(job =>
                job.InstanceId == waiter.Id
                && job.Kind == WorkflowJobKinds.ConditionalWake));
        }

        await ProcessSharedWakeOutboxAsync();

        long conditionalJobId;
        await using (var latched = fixture.CreateDbContext())
        {
            var token = await latched.ExecutionTokens.SingleAsync(item =>
                item.InstanceId == waiter.Id
                && item.Status == ExecutionTokenStatuses.Active);
            Assert.Equal(ExecutionTokenWaitStates.ConditionalWake, token.WaitState);
            conditionalJobId = Assert.IsType<long>(token.WaitingJobId);
        }
        await ProcessWorkflowJobAsync(conditionalJobId);

        await using (var completed = fixture.CreateDbContext())
        {
            Assert.Equal(
                WorkflowInstanceStatuses.Completed,
                (await completed.WorkflowInstances.SingleAsync(item => item.Id == waiter.Id)).Status);
            Assert.Equal(
                NodeExecutionCompletionReasons.ConditionalTriggered,
                (await completed.NodeExecutions.SingleAsync(item =>
                    item.InstanceId == waiter.Id
                    && item.NodeId == 2)).CompletionReason);
        }

        SharedVariableValueDto current;
        using (var get = await SendAsync(
                   HttpMethod.Get,
                   $"/api/shared-variables/{Uri.EscapeDataString(sharedKey)}/value"))
        {
            Assert.Equal(HttpStatusCode.OK, get.StatusCode);
            current = await ReadAsync<SharedVariableValueDto>(get);
        }
        using (var noOp = await SendAsync(
                   HttpMethod.Put,
                   $"/api/shared-variables/{Uri.EscapeDataString(sharedKey)}/value",
                   new UpdateSharedVariableValueRequest(
                       JsonSerializer.SerializeToElement(true),
                       current.Revision,
                       $"no-op-{suffix}",
                       "audit identical value")))
        {
            Assert.Equal(HttpStatusCode.OK, noOp.StatusCode);
        }

        await using (var afterNoOp = fixture.CreateDbContext())
        {
            var variable = await afterNoOp.SharedVariables.SingleAsync(item => item.Key == sharedKey);
            Assert.Equal(
                wakeCountAfterChange,
                await afterNoOp.SharedVariableWakes.LongCountAsync(item =>
                    item.SharedVariableId == variable.Id));
            var latest = await afterNoOp.SharedVariableRevisions
                .Where(item => item.SharedVariableId == variable.Id)
                .OrderByDescending(item => item.Revision)
                .FirstAsync();
            Assert.False(latest.ValueChanged);
        }
    }

    private async Task ProcessSharedWakeOutboxAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
        var engine = scope.ServiceProvider.GetRequiredService<WorkflowEngineService>();
        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        var request = new SharedVariableWakeLeaseRequest(
            $"shared-runtime-test:{Guid.NewGuid():N}",
            MaxCount: 1000,
            TimeSpan.FromMinutes(1),
            now);
        foreach (var wake in await repository.LeaseWakeExpansionsAsync(
                     request,
                     CancellationToken.None))
        {
            var fence = new SharedVariableWakeFence(
                wake.Id,
                wake.LeaseToken,
                wake.LeaseGeneration);
            await repository.ExpandWakeAsync(fence, CancellationToken.None);
            await repository.CompleteWakeExpansionAsync(fence, null, CancellationToken.None);
        }

        foreach (var delivery in await repository.LeaseWakeDeliveriesAsync(
                     request with { WorkerId = $"shared-delivery-test:{Guid.NewGuid():N}" },
                     CancellationToken.None))
        {
            var fence = new SharedVariableWakeFence(
                delivery.Id,
                delivery.LeaseToken,
                delivery.LeaseGeneration);
            await engine.ProcessSharedVariableWakeDeliveryAsync(
                delivery,
                CancellationToken.None);
            await repository.CompleteWakeDeliveryAsync(fence, null, CancellationToken.None);
        }
    }

    private async Task ProcessWorkflowJobAsync(long jobId, int maxActivityCount = 0)
    {
        await using (var db = fixture.CreateDbContext())
        {
            await db.WorkflowJobs
                .Where(job => job.Id == jobId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Priority, int.MaxValue)
                    .SetProperty(job => job.DueAt, DateTimeOffset.UtcNow));
        }
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>();
        var processor = scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>();
        var lease = Assert.Single(await jobs.LeaseRunnableAsync(
            new WorkflowJobLeaseRequest(
                $"shared-runtime-job:{Guid.NewGuid():N}",
                MaxCount: 1,
                MaxActivityCount: maxActivityCount,
                MaxPerInstance: 4,
                LeaseDuration: TimeSpan.FromMinutes(1)),
            CancellationToken.None));
        Assert.Equal(jobId, lease.Job.Id);
        await processor.ProcessAsync(lease, CancellationToken.None);
    }

    private async Task<long> CreateWorkflowAsync(WorkflowModel definition)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(definition, true));
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"Expected workflow creation to return 201 but received {(int)response.StatusCode}: "
            + await response.Content.ReadAsStringAsync());
        return (await ReadAsync<WorkflowDetailDto>(response)).Id;
    }

    private async Task<InstanceDetailDto> StartAsync(long workflowId)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(
                workflowId,
                null,
                null,
                new Dictionary<string, JsonElement>()));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "shared-runtime-admin")
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, user, "admin");
        return fixture.Client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("Response body was empty.");

    private static WorkflowModel CreateWaiter(string workflowKey, string sharedKey) => new()
    {
        Id = workflowKey,
        Name = workflowKey,
        InitialEventId = 1,
        Variables =
        [
            new VariableModel
            {
                Id = 1,
                Name = "releaseFlag",
                Scope = VariableScopes.Shared,
                SharedKey = sharedKey,
                Access = SharedVariableAccessModes.Read,
                DataType = WorkflowVariableTypes.Boolean,
                Nullable = false
            }
        ],
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Wait for release",
                Type = BpmnFlowNodeTypes.IntermediateConditionalCatchEvent,
                Conditional = new ConditionalDefinitionModel
                {
                    Condition = "releaseFlag == true",
                    DeliveryMode = ConditionalEventDeliveryModes.DurableAsync
                }
            },
            new FlowNodeModel { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, Name = "Wait", SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, Name = "Continue", SourceRef = 2, TargetRef = 3 }
        ]
    };

    private static WorkflowModel CreateWriter(string workflowKey, string sharedKey) => new()
    {
        Id = workflowKey,
        Name = workflowKey,
        InitialEventId = 1,
        Variables =
        [
            new VariableModel
            {
                Id = 1,
                Name = "release",
                Scope = VariableScopes.Shared,
                SharedKey = sharedKey,
                Access = SharedVariableAccessModes.ReadWrite,
                DataType = WorkflowVariableTypes.Boolean,
                Nullable = false
            },
            new VariableModel
            {
                Id = 2,
                Name = "localNote",
                DataType = WorkflowVariableTypes.String,
                Nullable = true
            },
            new VariableModel
            {
                Id = 3,
                Name = "actionCount",
                DataType = WorkflowVariableTypes.Number,
                Nullable = true
            }
        ],
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel { Id = 2, Name = "Release", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel
            {
                Id = 3,
                Name = "Record evidence",
                Type = BpmnFlowNodeTypes.ScriptTask,
                ScriptFormat = ScriptFormats.NCalc,
                Assignments =
                [
                    new AssignmentModel
                    {
                        Variable = "actionCount",
                        Expression = "FlowInfo(201, 'actions.count')"
                    }
                ]
            },
            new FlowNodeModel { Id = 4, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, Name = "Begin", SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel
            {
                Id = 201,
                Name = "Release",
                SourceRef = 2,
                TargetRef = 3,
                Variables =
                [
                    new VariableModel
                    {
                        Id = 2,
                        Name = "release",
                        DataType = WorkflowVariableTypes.Boolean,
                        Required = true
                    },
                    new VariableModel
                    {
                        Id = 3,
                        Name = "localNote",
                        DataType = WorkflowVariableTypes.String,
                        Required = true
                    }
                ]
            },
            new SequenceFlowModel { Id = 301, Name = "Finish", SourceRef = 3, TargetRef = 4 }
        ]
    };

    private static WorkflowModel CreateAsyncServiceReader(
        string workflowKey,
        string sharedKey) => new()
    {
        Id = workflowKey,
        Name = workflowKey,
        InitialEventId = 1,
        Variables =
        [
            new VariableModel
            {
                Id = 1,
                Name = "fxRate",
                Scope = VariableScopes.Shared,
                SharedKey = sharedKey,
                Access = SharedVariableAccessModes.Read,
                DataType = WorkflowVariableTypes.Number,
                Nullable = false
            },
            new VariableModel
            {
                Id = 2,
                Name = "decision",
                DataType = WorkflowVariableTypes.String,
                DefaultValue = JsonSerializer.SerializeToElement("initial")
            }
        ],
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Read rate",
                Type = BpmnFlowNodeTypes.ServiceTask,
                AsyncBefore = true,
                Service = new ServiceTaskModel
                {
                    Url = "https://tests.local/typed-output-success",
                    Method = "GET",
                    Headers =
                    [
                        new ServiceHeaderModel { Name = "X-Fx-Rate", Value = "${fxRate}" }
                    ],
                    OutputMappings =
                    [
                        new ServiceOutputMappingModel
                        {
                            Variable = "decision",
                            Path = "result.decision",
                            Required = true,
                            DataType = WorkflowVariableTypes.String
                        }
                    ]
                }
            },
            new FlowNodeModel { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, Name = "Fetch", SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, Name = "Done", SourceRef = 2, TargetRef = 3 }
        ]
    };
}
