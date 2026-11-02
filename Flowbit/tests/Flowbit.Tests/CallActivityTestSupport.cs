using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// Definitions and durable-job helpers for call activity tests. Everything goes through the
/// real API and job processor, so transactions and constraints are the real ones.
/// </summary>
public sealed class CallActivityHarness(PostgresApiFixture fixture)
{
    private readonly List<long> startedRoots = [];

    public const int Start = 1;
    public const int Call = 2;
    public const int End = 3;
    public const int Boundary = 4;
    public const int BoundaryEnd = 5;

    public static string Key(string prefix = "call") => $"{prefix}-{Guid.NewGuid():N}";

    public static CallVariableMappingModel Mapping(
        string source,
        string variable,
        string dataType = WorkflowVariableTypes.Number,
        bool required = true,
        bool isArray = false) => new()
    {
        SourceVariable = source,
        Variable = variable,
        DataType = dataType,
        Required = required,
        IsArray = isArray
    };

    public static VariableModel Input(
        string name,
        string dataType = WorkflowVariableTypes.Number,
        bool required = true,
        bool isArray = false) => new()
    {
        Id = name.GetHashCode(StringComparison.Ordinal) & 0xFFFF,
        Name = name,
        DataType = dataType,
        Required = required,
        IsArray = isArray
    };

    public static VariableModel Process(
        string name,
        string dataType = WorkflowVariableTypes.Boolean,
        bool nullable = true,
        string? validation = null,
        object? defaultValue = null) => new()
    {
        Id = name.GetHashCode(StringComparison.Ordinal) & 0xFFFF,
        Name = name,
        DataType = dataType,
        Nullable = nullable,
        Validation = validation,
        DefaultValue = defaultValue is null ? null : JsonSerializer.SerializeToElement(defaultValue)
    };

    /// <summary>A parent that starts, calls the target, and ends, optionally with a catch-all boundary.</summary>
    public static WorkflowModel Parent(
        string key,
        string targetKey,
        bool withBoundary = false,
        string? errorVariable = null,
        IReadOnlyList<VariableModel>? startInputs = null,
        IReadOnlyList<VariableModel>? variables = null,
        IReadOnlyList<CallVariableMappingModel>? inputs = null,
        IReadOnlyList<CallVariableMappingModel>? outputs = null)
    {
        var model = new WorkflowModel
        {
            Id = key,
            Name = key,
            InitialEventId = Start,
            Variables = variables?.ToList() ?? [],
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = Start,
                    Name = "Start",
                    Type = BpmnFlowNodeTypes.StartEvent,
                    Variables = startInputs?.ToList() ?? []
                },
                new FlowNodeModel
                {
                    Id = Call,
                    Name = "Call child",
                    Type = BpmnFlowNodeTypes.CallActivity,
                    CallActivity = new CallActivityModel
                    {
                        WorkflowKey = targetKey,
                        InputMappings = inputs?.ToList() ?? [],
                        OutputMappings = outputs?.ToList() ?? []
                    }
                },
                new FlowNodeModel { Id = End, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = Start, TargetRef = Call },
                new SequenceFlowModel { Id = 201, SourceRef = Call, TargetRef = End }
            ]
        };

        if (withBoundary)
        {
            model.FlowNodes.Add(new FlowNodeModel
            {
                Id = Boundary,
                Name = "Call failed",
                Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
                AttachedToRef = Call,
                ErrorVariable = errorVariable
            });
            model.FlowNodes.Add(new FlowNodeModel
            {
                Id = BoundaryEnd,
                Name = "Handled",
                Type = BpmnFlowNodeTypes.EndEvent
            });
            model.SequenceFlows.Add(new SequenceFlowModel { Id = 401, SourceRef = Boundary, TargetRef = BoundaryEnd });
        }

        return model;
    }

    /// <summary>A child that ends at once.</summary>
    public static WorkflowModel ImmediateChild(
        string key,
        IReadOnlyList<VariableModel>? startInputs = null,
        IReadOnlyList<VariableModel>? variables = null) => new()
    {
        Id = key,
        Name = key,
        InitialEventId = Start,
        Variables = variables?.ToList() ?? [],
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = Start,
                Name = "Start",
                Type = BpmnFlowNodeTypes.StartEvent,
                Variables = startInputs?.ToList() ?? []
            },
            new FlowNodeModel { Id = End, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows = [new SequenceFlowModel { Id = 101, SourceRef = Start, TargetRef = End }]
    };

    public const int ChildTask = 2;
    public const int ChildFlow = 201;

    /// <summary>A child that rests on a user task until its one action is taken.</summary>
    public static WorkflowModel WaitingChild(
        string key,
        IReadOnlyList<VariableModel>? startInputs = null,
        IReadOnlyList<VariableModel>? variables = null,
        IReadOnlyList<VariableModel>? actionVariables = null) => new()
    {
        Id = key,
        Name = key,
        InitialEventId = Start,
        Variables = variables?.ToList() ?? [],
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = Start,
                Name = "Start",
                Type = BpmnFlowNodeTypes.StartEvent,
                Variables = startInputs?.ToList() ?? []
            },
            new FlowNodeModel { Id = ChildTask, Name = "Approve", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel { Id = End, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = Start, TargetRef = ChildTask },
            new SequenceFlowModel
            {
                Id = ChildFlow,
                Name = "Approve",
                SourceRef = ChildTask,
                TargetRef = End,
                Variables = actionVariables?.ToList() ?? []
            }
        ]
    };

    /// <summary>A child that ends in an error end event at once.</summary>
    public static WorkflowModel FaultingChild(string key, string errorCode = "child.rejected") => new()
    {
        Id = key,
        Name = key,
        InitialEventId = Start,
        FlowNodes =
        [
            new FlowNodeModel { Id = Start, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel
            {
                Id = End,
                Name = "Rejected",
                Type = BpmnFlowNodeTypes.ErrorEndEvent,
                ErrorCode = errorCode,
                ErrorDescription = "The child rejected the request."
            }
        ],
        SequenceFlows = [new SequenceFlowModel { Id = 101, SourceRef = Start, TargetRef = End }]
    };

    public async Task<WorkflowDetailDto> PublishAsync(WorkflowModel definition)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(definition, true));
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, body);
        return JsonSerializer.Deserialize<WorkflowDetailDto>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    public async Task<long> StartAsync(
        long workflowId,
        Dictionary<string, JsonElement>? values = null,
        string user = "starter",
        params string[] roles)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var started = await scope.ServiceProvider.GetRequiredService<IWorkflowEngineService>().StartInstanceSlimAsync(
            workflowId,
            null,
            new ActorContext(user, roles, new Dictionary<string, string>()),
            null,
            values,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            CancellationToken.None);
        startedRoots.Add(started.Id);
        return started.Id;
    }

    /// <summary>
    /// Pushes the jobs a test left open far into the future, because other suites sharing
    /// this database lease one specific job and would otherwise receive them first.
    /// </summary>
    public async Task RetireLeftoverJobsAsync()
    {
        if (startedRoots.Count == 0)
        {
            return;
        }

        await using var db = fixture.CreateDbContext();
        var instanceIds = await db.WorkflowInstances.AsNoTracking()
            .Where(instance => startedRoots.Contains(instance.Id)
                || (instance.CallRootInstanceId != null && startedRoots.Contains(instance.CallRootInstanceId.Value)))
            .Select(instance => instance.Id)
            .ToListAsync();
        var distant = DateTimeOffset.UtcNow.AddYears(10);
        await db.WorkflowJobs
            .Where(job => job.InstanceId != null
                && instanceIds.Contains(job.InstanceId.Value)
                && (job.Status == WorkflowJobStatuses.Queued
                    || job.Status == WorkflowJobStatuses.Retry
                    || job.Status == WorkflowJobStatuses.Running
                    || job.Status == WorkflowJobStatuses.ResultReady))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.DueAt, distant)
                .SetProperty(job => job.LeaseExpiresAt, job => job.LeaseExpiresAt == null ? null : distant));
    }

    public static Dictionary<string, JsonElement> Values(params (string Name, object? Value)[] values) =>
        values.ToDictionary(
            pair => pair.Name,
            pair => JsonSerializer.SerializeToElement(pair.Value),
            StringComparer.OrdinalIgnoreCase);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        ApiTestAuth.Authorize(request, "call-admin", "admin");
        return await fixture.Client.SendAsync(request);
    }

    public async Task<InstanceDetailDto> DetailAsync(long instanceId)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/instances/{instanceId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<InstanceDetailDto>())!;
    }

    public async Task<PagedResult<CallActivityExecutionDto>> CallsAsync(long instanceId, int page = 1, int pageSize = 50)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/instances/{instanceId}/calls?page={page}&pageSize={pageSize}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PagedResult<CallActivityExecutionDto>>())!;
    }

    /// <summary>The invocation row as stored.</summary>
    public async Task<Flowbit.Infrastructure.Entities.CallActivityExecutionEntity> InvocationAsync(long parentInstanceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.CallActivityExecutions.AsNoTracking()
            .Where(call => call.ParentInstanceId == parentInstanceId)
            .OrderByDescending(call => call.Id)
            .FirstAsync();
    }

    public async Task<long> SingleJobIdAsync(long invocationId, string kind)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowJobs.AsNoTracking()
            .Where(job => job.CallInvocationId == invocationId && job.Kind == kind)
            .Select(job => job.Id)
            .SingleAsync();
    }

    public async Task<string> JobStatusAsync(long jobId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowJobs.AsNoTracking()
            .Where(job => job.Id == jobId)
            .Select(job => job.Status)
            .SingleAsync();
    }

    public async Task<WorkflowJobLeaseRecord> LeaseAsync(long jobId)
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
                $"call-test:{Guid.NewGuid():N}",
                MaxCount: 1,
                MaxActivityCount: 1,
                MaxPerInstance: 4,
                LeaseDuration: TimeSpan.FromMinutes(1)),
            CancellationToken.None);
        var lease = Assert.Single(leases);
        Assert.Equal(jobId, lease.Job.Id);
        return lease;
    }

    public async Task ProcessAsync(long jobId, IServiceProvider? services = null)
    {
        var lease = await LeaseAsync(jobId);
        await using var scope = (services ?? fixture.Factory.Services).CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>()
            .ProcessAsync(lease, CancellationToken.None);
    }

    public async Task ProcessLeaseAsync(WorkflowJobLeaseRecord lease, IServiceProvider? services = null)
    {
        await using var scope = (services ?? fixture.Factory.Services).CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>()
            .ProcessAsync(lease, CancellationToken.None);
    }

    /// <summary>Takes the family's default away, as if it had no published default.</summary>
    public async Task RemoveDefaultAsync(string workflowKey)
    {
        await using var db = fixture.CreateDbContext();
        await db.WorkflowDefinitions
            .Where(definition => definition.WorkflowKey == workflowKey && definition.IsDefault)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(definition => definition.IsDefault, false)
                .SetProperty(definition => definition.DefaultActivationId, (Guid?)null)
                .SetProperty(definition => definition.DefaultActivatedAt, (DateTimeOffset?)null));
    }

    public async Task RestoreDefaultAsync(long workflowId)
    {
        await using var db = fixture.CreateDbContext();
        await db.WorkflowDefinitions
            .Where(definition => definition.Id == workflowId)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(definition => definition.IsDefault, true)
                .SetProperty(definition => definition.DefaultActivationId, (Guid?)Guid.NewGuid())
                .SetProperty(definition => definition.DefaultActivatedAt, (DateTimeOffset?)DateTimeOffset.UtcNow));
    }

    /// <summary>Runs one call job of the invocation.</summary>
    public async Task ProcessCallJobAsync(long invocationId, string kind) =>
        await ProcessAsync(await SingleJobIdAsync(invocationId, kind));

    /// <summary>
    /// Runs the open call jobs of a tree until none is runnable, the way Workers eventually would.
    /// </summary>
    public async Task DrainAsync(long rootInstanceId, int maxJobs = 64, IServiceProvider? services = null)
    {
        for (var processed = 0; processed < maxJobs; processed++)
        {
            await using var db = fixture.CreateDbContext();
            var instanceIds = await db.WorkflowInstances.AsNoTracking()
                .Where(instance => instance.Id == rootInstanceId || instance.CallRootInstanceId == rootInstanceId)
                .Select(instance => instance.Id)
                .ToListAsync();
            var next = await db.WorkflowJobs.AsNoTracking()
                .Where(job =>
                    job.InstanceId != null
                    && instanceIds.Contains(job.InstanceId.Value)
                    && (job.Status == WorkflowJobStatuses.Queued || job.Status == WorkflowJobStatuses.Retry))
                .OrderBy(job => job.Id)
                .Select(job => (long?)job.Id)
                .FirstOrDefaultAsync();
            if (next is not long jobId)
            {
                return;
            }

            await ProcessAsync(jobId, services);
        }

        throw new InvalidOperationException($"Root instance #{rootInstanceId} still had runnable jobs after {maxJobs}.");
    }

    public async Task<Flowbit.Infrastructure.Entities.WorkflowIncidentEntity?> OpenIncidentAsync(long jobId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowIncidents.AsNoTracking()
            .Where(incident => incident.JobId == jobId && incident.Status == WorkflowIncidentStatuses.Open)
            .SingleOrDefaultAsync();
    }

    public async Task RetryIncidentAsync(long incidentId)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/api/incidents/{incidentId}/retry");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    /// <summary>Takes the child's one action as a real user.</summary>
    public async Task<HttpResponseMessage> TakeChildActionAsync(
        long childInstanceId,
        string user = "approver",
        object? body = null)
    {
        await using var db = fixture.CreateDbContext();
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/instances/{childInstanceId}/flows/{ChildFlow}")
        {
            Content = JsonContent.Create(body ?? new { })
        };
        ApiTestAuth.Authorize(request, user, "approver");
        return await fixture.Client.SendAsync(request);
    }

    /// <summary>Asks for a batch version change of one instance and returns its prepared item.</summary>
    public async Task<InstanceVersionChangeBatchItemDto> PrepareVersionBatchItemAsync(long sourceId, long targetId, long instanceId)
    {
        using var created = await SendAsync(
            HttpMethod.Post,
            "/api/instance-version-change-batches",
            new CreateInstanceVersionChangeBatchRequest(
                sourceId,
                targetId,
                "Move a waiting call",
                new InstanceVersionChangeBatchSelectionDto(
                    InstanceVersionChangeBatchSelectionModes.Explicit,
                    [instanceId],
                    null,
                    null),
                $"call-batch-{Guid.NewGuid():N}"));
        Assert.True(created.StatusCode == HttpStatusCode.Accepted, await created.Content.ReadAsStringAsync());
        var batch = await created.Content.ReadFromJsonAsync<InstanceVersionChangeBatchDetailDto>();
        await ProcessAsync(batch!.PreparationJobId!.Value);

        using var items = await SendAsync(
            HttpMethod.Get,
            $"/api/instance-version-change-batches/{batch.Summary.Id}/items?page=1&pageSize=200");
        Assert.Equal(HttpStatusCode.OK, items.StatusCode);
        return Assert.Single((await items.Content.ReadFromJsonAsync<PagedResult<InstanceVersionChangeBatchItemDto>>())!.Items);
    }

    public async Task<string?> VariableAsync(long instanceId, string name)
    {
        var detail = await DetailAsync(instanceId);
        return detail.Variables
            .Where(variable => string.Equals(variable.VariableName, name, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(variable => variable.Id)
            .Select(variable => variable.Value.GetRawText())
            .FirstOrDefault();
    }
}
