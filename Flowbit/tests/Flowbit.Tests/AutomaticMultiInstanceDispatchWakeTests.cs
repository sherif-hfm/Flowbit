using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// Publishing an item keeps a dispatch wake that has not run yet instead of replacing it
/// with an identical one, so parallel items that finish close together share one wake.
/// Found by the release-readiness scale run (2026-11-14): every publication had queued a
/// new wake and cancelled the previous one, and each cancelled wake still waited for the
/// instance lock.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class AutomaticMultiInstanceDispatchWakeTests(PostgresApiFixture fixture) : IAsyncLifetime
{
    private readonly List<long> startedInstances = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        // Other suites lease specific jobs from this shared database.
        await using var db = fixture.CreateDbContext();
        var distant = DateTimeOffset.UtcNow.AddYears(10);
        await db.WorkflowJobs
            .Where(job => job.InstanceId != null && startedInstances.Contains(job.InstanceId.Value)
                && (job.Status == WorkflowJobStatuses.Queued || job.Status == WorkflowJobStatuses.Retry
                    || job.Status == WorkflowJobStatuses.Running || job.Status == WorkflowJobStatuses.ResultReady))
            .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.DueAt, distant)
                .SetProperty(job => job.LeaseExpiresAt, job => job.LeaseExpiresAt == null ? null : distant));
    }

    [Fact]
    public async Task Items_finishing_together_share_one_pending_dispatch_wake_and_the_body_still_completes()
    {
        await using var host = OpenHost();
        var body = new FlowNodeModel
        {
            Id = 2, Name = "Double each amount", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true,
            ScriptFormat = ScriptFormats.NCalc, X = 200, Y = 20,
            MultiInstance = new MultiInstanceModel
            {
                Mode = "parallel", Source = "collection", CollectionVariable = "amounts", CompletionEvaluation = "afterAll",
                MaxConcurrency = 4, ResultVariable = "results",
                OutputVariables = [new() { Name = "doubled", DataType = "number", IsArray = false, Nullable = false }]
            },
            Assignments = [new() { Variable = "doubled", Expression = "[mi.item] * 2" }]
        };
        var key = $"mi-wake-{Guid.NewGuid():N}";
        var published = await PublishAsync(host, new WorkflowModel
        {
            Id = key, Name = key, InitialEventId = 1,
            Variables =
            [
                new() { Id = 1, Name = "amounts", DataType = "number", IsArray = true, DefaultValue = Json(Enumerable.Range(1, 8).ToArray()) },
                new() { Id = 2, Name = "results", DataType = "json", DefaultValue = Json(Array.Empty<object>()) }
            ],
            FlowNodes =
            [
                new() { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent, X = 40, Y = 40 },
                body,
                new() { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent, X = 500, Y = 40 }
            ],
            SequenceFlows = [new() { Id = 101, SourceRef = 1, TargetRef = 2 }, new() { Id = 201, SourceRef = 2, TargetRef = 3 }]
        });
        var instanceId = await StartAsync(host, published.Id);

        // Entry, then the first dispatch admits the parallel window of four items.
        await ProcessAsync(host, await LeaseOneAsync(host));
        await ProcessAsync(host, await LeaseOneAsync(host));
        Assert.Equal(4, await CountJobsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem, null));

        // Four Workers hold the four items at once, as they would under load. A lease call
        // takes at most one job per instance, so lease them one at a time.
        var items = new List<WorkflowJobLeaseRecord>();
        for (var index = 0; index < 4; index++) items.Add(await LeaseOneAsync(host));
        Assert.All(items, lease => Assert.Equal(WorkflowJobKinds.MultiInstanceItem, lease.Job.Kind));

        // The first publication queues a wake. A Worker leases it but has not reached the
        // instance lock when the next items publish: they keep it rather than replace it.
        await ProcessAsync(host, items[0]);
        var wake = await LeaseOneAsync(host);
        Assert.Equal(WorkflowJobKinds.MultiInstanceDispatch, wake.Job.Kind);
        foreach (var item in items.Skip(1)) await ProcessAsync(host, item);

        Assert.Equal(2, await CountJobsAsync(instanceId, WorkflowJobKinds.MultiInstanceDispatch, null));
        Assert.Equal(0, await CountJobsAsync(instanceId, WorkflowJobKinds.MultiInstanceDispatch, WorkflowJobStatuses.Cancelled));

        // The kept wake still admits every free slot, against all four publications.
        await ProcessAsync(host, wake);
        Assert.Equal(8, await CountJobsAsync(instanceId, WorkflowJobKinds.MultiInstanceItem, null));

        await DrainAsync(host, instanceId);
        await using var db = fixture.CreateDbContext();
        Assert.Equal(WorkflowInstanceStatuses.Completed, (await db.WorkflowInstances.SingleAsync(row => row.Id == instanceId)).Status);
        Assert.Equal(0, await CountJobsAsync(instanceId, WorkflowJobKinds.MultiInstanceDispatch, WorkflowJobStatuses.Cancelled));
        var results = (await db.InstanceVariables.Where(row => row.InstanceId == instanceId && row.VariableName == "results")
            .OrderByDescending(row => row.Id).FirstAsync()).ValueJson.RootElement;
        Assert.Equal(Enumerable.Range(1, 8).Select(value => value * 2),
            results.EnumerateArray().Select(row => row.GetProperty("variables").GetProperty("doubled").GetInt32()));
    }

    private WebApplicationFactory<Program> OpenHost() =>
        fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            var open = new AutomaticMultiInstanceOptions { AllowInDevelopment = true };
            open.ResolveForEnvironment(isDevelopment: true);
            services.RemoveAll<AutomaticMultiInstanceOptions>();
            services.AddSingleton(open);
        }));

    private static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);

    private async Task<WorkflowDetailDto> PublishAsync(WebApplicationFactory<Program> host, WorkflowModel definition)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/workflows")
        {
            Content = JsonContent.Create(new CreateWorkflowRequest(definition, true))
        };
        ApiTestAuth.Authorize(request, "wake-admin", "admin");
        using var response = await host.CreateClient().SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        return JsonSerializer.Deserialize<WorkflowDetailDto>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private async Task<long> StartAsync(WebApplicationFactory<Program> host, long workflowId)
    {
        await using var scope = host.Services.CreateAsyncScope();
        var started = await scope.ServiceProvider.GetRequiredService<IWorkflowEngineService>().StartInstanceSlimAsync(
            workflowId, null, new ActorContext("starter", ["User"], new Dictionary<string, string>()), null, null,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase), CancellationToken.None);
        startedInstances.Add(started.Id);
        await using var db = fixture.CreateDbContext();
        // Due a minute ago, so the job is due even when the database clock runs behind this machine's,
        // and ahead of other suites' work in the shared database.
        await db.WorkflowJobs.Where(job => job.InstanceId == started.Id).ExecuteUpdateAsync(setters => setters
            .SetProperty(job => job.Priority, 1_000_000).SetProperty(job => job.DueAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        return started.Id;
    }

    private async Task<int> CountJobsAsync(long instanceId, string kind, string? status)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowJobs.CountAsync(job => job.InstanceId == instanceId && job.Kind == kind
            && (status == null || job.Status == status));
    }

    private async Task<WorkflowJobLeaseRecord> LeaseOneAsync(WebApplicationFactory<Program> host) =>
        Assert.Single(await LeaseAsync(host, 1));

    // Leases this test's due work, control jobs first, as a Worker would.
    private async Task<IReadOnlyList<WorkflowJobLeaseRecord>> LeaseAsync(WebApplicationFactory<Program> host, int count)
    {
        await using (var promote = fixture.CreateDbContext())
        {
            await promote.WorkflowJobs.Where(job => job.InstanceId != null && startedInstances.Contains(job.InstanceId.Value)
                    && (job.Status == WorkflowJobStatuses.Queued || job.Status == WorkflowJobStatuses.Retry))
                .ExecuteUpdateAsync(setters => setters.SetProperty(job => job.Priority, 1_000_000)
                    .SetProperty(job => job.DueAt, DateTimeOffset.UtcNow.AddMinutes(-1)));
        }
        await using var scope = host.Services.CreateAsyncScope();
        var leases = await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().LeaseRunnableAsync(
            new WorkflowJobLeaseRequest($"mi-wake:{Guid.NewGuid():N}", MaxCount: count, MaxActivityCount: count,
                MaxPerInstance: 4, LeaseDuration: TimeSpan.FromMinutes(1)), CancellationToken.None);
        Assert.All(leases, lease => Assert.Contains(lease.Job.InstanceId ?? 0, startedInstances));
        Assert.Equal(count, leases.Count);
        return leases;
    }

    private static async Task ProcessAsync(WebApplicationFactory<Program> host, WorkflowJobLeaseRecord lease)
    {
        await using var scope = host.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(lease, CancellationToken.None);
    }

    // Runs runnable jobs of the instance until none remains, the way Workers eventually would.
    private async Task DrainAsync(WebApplicationFactory<Program> host, long instanceId, int maxJobs = 64)
    {
        for (var processed = 0; processed < maxJobs; processed++)
        {
            await using (var db = fixture.CreateDbContext())
            {
                if (!await db.WorkflowJobs.AnyAsync(job => job.InstanceId == instanceId
                        && (job.Status == WorkflowJobStatuses.Queued || job.Status == WorkflowJobStatuses.Retry)))
                    return;
            }
            await ProcessAsync(host, await LeaseOneAsync(host));
        }
        throw new InvalidOperationException($"Instance #{instanceId} still had runnable jobs after {maxJobs}.");
    }
}
