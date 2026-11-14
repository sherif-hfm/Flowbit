using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// Drives automatic control jobs through the registered job processor and the real
/// engine continuation, against PostgreSQL. Item execution belongs to the TASK-05B/C/D
/// adapters, so these tests stand in for an adapter only where it would call the
/// common coordinator.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class AutomaticMultiInstanceProcessorTests(PostgresApiFixture fixture) : IAsyncLifetime
{
    private readonly List<long> ownedInstanceIds = [];
    private static readonly ActorContext Actor = new("causal", ["User"], new Dictionary<string, string>());
    private WebApplicationFactory<Program>? releasedHost;

    // Dispatch and completion jobs run the body, so only a host with the release switch open
    // processes them; the ordinary test host refuses them.
    private WebApplicationFactory<Program> ReleasedHost => releasedHost ??=
        fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var options = new AutomaticMultiInstanceOptions { AllowInDevelopment = true };
            options.ResolveForEnvironment(isDevelopment: true);
            services.RemoveAll<AutomaticMultiInstanceOptions>();
            services.AddSingleton(options);
        }));

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (releasedHost is not null) await releasedHost.DisposeAsync();
        // The shared database also hosts worker lease tests.
        await using var db = fixture.CreateDbContext();
        var jobs = new WorkflowJobRepository(db, fixture.DataSource);
        foreach (var instanceId in ownedInstanceIds)
            await jobs.CancelByInstanceAsync(instanceId, "testCleanup", default);
    }

    [Fact]
    public async Task Empty_body_completion_runs_through_the_processor_and_completes_the_instance()
    {
        var seed = await SeedAsync(count: 0, boundary: false);
        var body = await InitializeAsync(seed);
        Assert.Equal(0, body.TotalCount);

        await ProcessAsync(await ControlJobAsync(body, WorkflowJobKinds.MultiInstanceComplete), WorkflowJobClasses.Control);

        await using var db = fixture.CreateDbContext();
        Assert.Equal("completed", (await db.WorkflowInstances.SingleAsync(row => row.Id == seed.InstanceId)).Status);
        Assert.Equal("completed", (await db.MultiInstanceExecutions.SingleAsync(row => row.Id == body.Id)).Status);
        Assert.Equal("[]", (await LatestAsync(db, seed.InstanceId, "results")).GetRawText());
        Assert.Equal(2, await db.InstanceHistory.CountAsync(row => row.InstanceId == seed.InstanceId
            && row.Note == InstanceHistoryNotes.AutomaticMultiInstanceCompleted));
    }

    [Fact]
    public async Task Refused_item_result_without_a_boundary_faults_the_instance_through_the_processor()
    {
        var seed = await SeedAsync(count: 2, boundary: false);
        var body = await InitializeAsync(seed);
        var itemJob = await DispatchAndLeaseFirstItemAsync(body);

        await RefuseAsync(seed, body, itemJob);
        await ProcessAsync(await ControlJobAsync(body, WorkflowJobKinds.MultiInstanceComplete), WorkflowJobClasses.Control);

        await using var db = fixture.CreateDbContext();
        Assert.Equal("faulted", (await db.WorkflowInstances.SingleAsync(row => row.Id == seed.InstanceId)).Status);
        var token = await db.ExecutionTokens.SingleAsync(row => row.Id == seed.TokenId);
        Assert.Equal(ExecutionTokenTerminationReasons.MultiInstanceFailed, token.TerminationReason);
        Assert.Equal(AutomaticMultiInstanceValues.ResultTooLarge, token.FaultCode);
        Assert.Equal("interrupted", (await db.MultiInstanceExecutions.SingleAsync(row => row.Id == body.Id)).Status);
        // Nothing is published: the aggregate stays at its initialization reset.
        Assert.Equal("[]", (await LatestAsync(db, seed.InstanceId, "results")).GetRawText());
        var items = await db.MultiInstanceActivityItems.Where(row => row.MultiInstanceExecutionId == body.Id)
            .OrderBy(row => row.ItemIndex).ToArrayAsync();
        Assert.Equal(new[] { "failed", "cancelled" }, items.Select(item => item.Status));
        Assert.Equal("multiInstanceInterrupt", (await db.NodeExecutions.SingleAsync(row =>
            row.AutomaticMultiInstanceItemId == items[0].Id)).CompletionReason);
    }

    [Fact]
    public async Task Refused_item_result_takes_the_catch_all_boundary_without_publishing()
    {
        var seed = await SeedAsync(count: 2, boundary: true);
        var body = await InitializeAsync(seed);
        var itemJob = await DispatchAndLeaseFirstItemAsync(body);

        await RefuseAsync(seed, body, itemJob);
        await ProcessAsync(await ControlJobAsync(body, WorkflowJobKinds.MultiInstanceComplete), WorkflowJobClasses.Control);

        await using var db = fixture.CreateDbContext();
        Assert.Equal("completed", (await db.WorkflowInstances.SingleAsync(row => row.Id == seed.InstanceId)).Status);
        Assert.Equal(5, (await db.ExecutionTokens.SingleAsync(row => row.Id == seed.TokenId)).NodeId);
        Assert.Equal(AutomaticMultiInstanceValues.ResultTooLarge, (await LatestAsync(db, seed.InstanceId, "failure")).GetString());
        Assert.Equal("[]", (await LatestAsync(db, seed.InstanceId, "results")).GetRawText());
    }

    // Only script and service items run as multiInstanceItem jobs (TASK-05C, TASK-05B). A call
    // item runs through its invocation's callStart and callResume (TASK-05D), so an item job
    // naming any other kind fails closed at item processing whatever reached the database: it
    // never runs as another kind's item.
    [Fact]
    public async Task An_item_job_of_a_kind_without_an_adapter_stays_refused_at_item_processing()
    {
        // The release switch is open, so only the missing adapter refuses this job.
        await using var open = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var options = new AutomaticMultiInstanceOptions { AllowInDevelopment = true };
            options.ResolveForEnvironment(isDevelopment: true);
            services.RemoveAll<AutomaticMultiInstanceOptions>();
            services.AddSingleton(options);
        }));
        var seed = await SeedAsync(count: 2, boundary: false);
        var body = await InitializeAsync(seed);
        var itemFence = await DispatchAndLeaseFirstItemAsync(body);
        await using (var rename = fixture.CreateDbContext())
            await rename.WorkflowJobs.Where(job => job.Id == itemFence.JobId).ExecuteUpdateAsync(setters =>
                setters.SetProperty(job => job.NodeType, BpmnFlowNodeTypes.CallActivity));
        await using (var scope = open.Services.CreateAsyncScope())
        {
            var job = (await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().GetForUpdateAsync(itemFence.JobId, default))!;
            await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(
                new WorkflowJobLeaseRecord(job, itemFence.LeaseToken, itemFence.LeaseGeneration, job.AttemptCount), CancellationToken.None);
        }
        await using var after = fixture.CreateDbContext();
        var incident = await after.WorkflowIncidents.SingleAsync(row => row.JobId == itemFence.JobId && row.Status == "open");
        Assert.Equal("job_invariant_violation", incident.Type);
        Assert.Contains("item adapter is not installed", incident.Details);
    }

    private sealed record Seed(long InstanceId, long TokenId, long DefinitionId, Guid ActivationId, string Key,
        WorkflowModel Definition, long EntryJobId);

    private async Task<Seed> SeedAsync(int count, bool boundary)
    {
        await using var db = fixture.CreateDbContext();
        var key = "processor-mi-" + Guid.NewGuid().ToString("N");
        var definition = new WorkflowModel
        {
            Id = key, Name = key,
            Variables =
            [
                new() { Id = 1, Name = "items", DataType = "number", IsArray = true, DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<int>()) },
                new() { Id = 2, Name = "results", DataType = "json", DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<int>()) },
                new() { Id = 3, Name = "failure", DataType = "string", Nullable = true }
            ],
            FlowNodes =
            [
                new() { Id = 2, Name = "Body", Type = BpmnFlowNodeTypes.ServiceTask, AsyncBefore = true,
                    Service = new() { Method = "POST", Url = "https://example.test/items", TimeoutSeconds = 30 },
                    MultiInstance = new() { Mode = "parallel", CollectionVariable = "items", ResultVariable = "results",
                        CompletionEvaluation = "afterAll", OutputVariables = [] } },
                new() { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows = [new() { Id = 201, SourceRef = 2, TargetRef = 3 }]
        };
        if (boundary)
        {
            definition.FlowNodes.Add(new() { Id = 4, Name = "Caught", Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
                AttachedToRef = 2, ErrorVariable = "failure" });
            definition.FlowNodes.Add(new() { Id = 5, Name = "Handled", Type = BpmnFlowNodeTypes.EndEvent });
            definition.SequenceFlows.Add(new() { Id = 401, SourceRef = 4, TargetRef = 5 });
        }
        var stored = new WorkflowDefinitionEntity { Name = key, WorkflowKey = key, Version = 1, Definition = definition };
        db.WorkflowDefinitions.Add(stored);
        await db.SaveChangesAsync();
        var instance = new WorkflowInstanceEntity { WorkflowDefinitionId = stored.Id, WorkflowKey = key,
            Status = "running", StartedBy = "starter" };
        db.WorkflowInstances.Add(instance);
        await db.SaveChangesAsync();
        ownedInstanceIds.Add(instance.Id);
        var token = new ExecutionTokenEntity { InstanceId = instance.Id, NodeId = 2, NodeName = "Body",
            NodeType = BpmnFlowNodeTypes.ServiceTask };
        db.ExecutionTokens.Add(token);
        await db.SaveChangesAsync();
        var jobs = new WorkflowJobRepository(db, fixture.DataSource);
        var entry = await jobs.EnqueueAsync(Template(instance.Id, token.Id, stored.Id, token.ActivationId, key) with
        {
            Kind = WorkflowJobKinds.AsyncBefore, Phase = WorkflowJobKinds.AsyncBefore, QueueClass = WorkflowJobClasses.Activity
        }, default);
        token.WaitState = ExecutionTokenWaitStates.AsyncBefore;
        token.WaitingJobId = entry.Id;
        await db.SaveChangesAsync();
        var runtime = new WorkflowRuntimeRepository(db);
        await runtime.AddVariableAsync(instance.Id, "items", null, "starter",
            JsonSerializer.SerializeToElement(Enumerable.Range(1, count).ToArray()), default);
        await db.SaveChangesAsync();
        return new(instance.Id, token.Id, stored.Id, token.ActivationId, key, definition, entry.Id);
    }

    private static WorkflowJobCreateRecord Template(long instanceId, long tokenId, long definitionId, Guid activationId, string key) => new()
    {
        InstanceId = instanceId, TokenId = tokenId, WorkflowDefinitionId = definitionId, WorkflowKey = key,
        ActivationId = activationId, NodeId = 2, NodeName = "Body", NodeType = BpmnFlowNodeTypes.ServiceTask,
        Kind = WorkflowJobKinds.MultiInstanceDispatch, Phase = WorkflowJobKinds.MultiInstanceDispatch,
        QueueClass = WorkflowJobClasses.Control, DueAt = DateTimeOffset.UtcNow, MaxAttempts = 4
    };

    private static WorkflowJobCreateRecord Template(Seed seed) =>
        Template(seed.InstanceId, seed.TokenId, seed.DefinitionId, seed.ActivationId, seed.Key);

    // The service adapter would initialize the body from its async-before entry job.
    private async Task<AutomaticMultiInstanceRecord> InitializeAsync(Seed seed)
    {
        var entryFence = await LeaseAsync(seed.EntryJobId);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        var engine = services.GetRequiredService<WorkflowEngineService>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(default);
        var instance = (await services.GetRequiredService<IWorkflowRuntimeRepository>().GetInstanceAsync(seed.InstanceId, default))!;
        var body = await services.GetRequiredService<AutomaticMultiInstanceCoordinator>().InitializeBodyAsync(new()
        {
            InstanceId = seed.InstanceId, TokenId = seed.TokenId, WorkflowDefinitionId = seed.DefinitionId,
            ActivationId = seed.ActivationId, NodeId = 2, NodeName = "Body", ActivityKind = BpmnFlowNodeTypes.ServiceTask,
            Mode = "parallel", ResultVariable = "results", AggregateScope = "instance", AggregateValueVersion = 0,
            MaxInstances = 1000, EffectiveConcurrency = 0, TotalCount = 0, ResultBytes = 2,
            InputSnapshot = JsonSerializer.SerializeToElement(new { collection = Array.Empty<int>() })
        }, seed.Definition, Actor, entryFence, Template(seed),
            engine.CreateAutomaticMultiInstanceLifecycle(instance, seed.Definition, Actor), default);
        await unitOfWork.SaveChangesAsync(default);
        await transaction.CommitAsync(default);
        return body!;
    }

    private async Task<WorkflowJobFence> DispatchAndLeaseFirstItemAsync(AutomaticMultiInstanceRecord body)
    {
        await ProcessAsync(await ControlJobAsync(body, WorkflowJobKinds.MultiInstanceDispatch), WorkflowJobClasses.Control);
        await using var db = fixture.CreateDbContext();
        var itemJobs = await db.WorkflowJobs.Where(job => job.MultiInstanceExecutionId == body.Id
            && job.Kind == WorkflowJobKinds.MultiInstanceItem).OrderBy(job => job.Id).ToArrayAsync();
        Assert.Equal(2, itemJobs.Length);
        return await LeaseAsync(itemJobs[0].Id);
    }

    // The adapter refuses a result that publication found over the body's limit.
    private async Task RefuseAsync(Seed seed, AutomaticMultiInstanceRecord body, WorkflowJobFence itemFence)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var services = scope.ServiceProvider;
        var unitOfWork = services.GetRequiredService<IUnitOfWork>();
        var engine = services.GetRequiredService<WorkflowEngineService>();
        await using var transaction = await unitOfWork.BeginTransactionAsync(default);
        var instance = (await services.GetRequiredService<IWorkflowRuntimeRepository>().GetInstanceAsync(seed.InstanceId, default))!;
        var itemId = (await services.GetRequiredService<IWorkflowJobRepository>().GetForUpdateAsync(itemFence.JobId, default))!
            .AutomaticMultiInstanceItemId!.Value;
        Assert.True(await services.GetRequiredService<AutomaticMultiInstanceCoordinator>().RefuseItemResultAsync(
            body.Id, itemId, body.ActivationId, itemFence, AutomaticMultiInstanceValues.ResultTooLarge, Actor, Template(seed),
            engine.CreateAutomaticMultiInstanceLifecycle(instance, seed.Definition, Actor), default));
        await unitOfWork.SaveChangesAsync(default);
        await transaction.CommitAsync(default);
    }

    private async Task<long> ControlJobAsync(AutomaticMultiInstanceRecord body, string kind)
    {
        await using var db = fixture.CreateDbContext();
        return (await db.WorkflowJobs.SingleAsync(job => job.MultiInstanceExecutionId == body.Id && job.Kind == kind
            && (job.Status == "queued" || job.Status == "retry"))).Id;
    }

    private async Task<WorkflowJobFence> LeaseAsync(long jobId)
    {
        await using var db = fixture.CreateDbContext();
        var lease = Guid.NewGuid();
        await db.WorkflowJobs.Where(job => job.Id == jobId).ExecuteUpdateAsync(update => update
            .SetProperty(job => job.Status, "running").SetProperty(job => job.WorkerId, "processor-test")
            .SetProperty(job => job.LeaseToken, lease).SetProperty(job => job.LeaseGeneration, 1)
            .SetProperty(job => job.LeaseExpiresAt, DateTimeOffset.UtcNow.AddMinutes(10)));
        return new(jobId, "processor-test", lease, 1);
    }

    private async Task ProcessAsync(long jobId, string queueClass)
    {
        await using (var promote = fixture.CreateDbContext())
        {
            // Due a minute ago, so the job is due even when the database clock runs behind this machine's.
            Assert.Equal(1, await promote.WorkflowJobs.Where(job => job.Id == jobId).ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Priority, 1_000_000)
                .SetProperty(job => job.DueAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        }
        await using var scope = ReleasedHost.Services.CreateAsyncScope();
        var leases = await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().LeaseRunnableAsync(
            new WorkflowJobLeaseRequest($"processor-test:{Guid.NewGuid():N}", MaxCount: 1,
                MaxActivityCount: queueClass == WorkflowJobClasses.Activity ? 1 : 0, MaxPerInstance: 4,
                LeaseDuration: TimeSpan.FromMinutes(1)), CancellationToken.None);
        var lease = Assert.Single(leases);
        Assert.Equal(jobId, lease.Job.Id);
        await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(lease, CancellationToken.None);
        await using var db = fixture.CreateDbContext();
        var processed = await db.WorkflowJobs.SingleAsync(job => job.Id == jobId);
        Assert.Equal("completed", processed.Status);
    }

    private static async Task<JsonElement> LatestAsync(Flowbit.Infrastructure.Data.AppDbContext db, long instanceId, string name) =>
        (await db.InstanceVariables.Where(row => row.InstanceId == instanceId && row.VariableName == name)
            .OrderByDescending(row => row.Id).FirstAsync()).ValueJson.RootElement.Clone();
}
