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

public abstract class InstanceDiagramHistoryApiTestBase(PostgresApiFixture fixture)
{
    protected static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    protected PostgresApiFixture Fixture { get; } = fixture;

    protected async Task<InstanceDetailDto> CreateAndStartAsync(WorkflowModel model) => await StartAsync((await CreateAsync(model)).Id);

    protected async Task<WorkflowDetailDto> CreateAsync(WorkflowModel model)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
        return await ReadAsync<WorkflowDetailDto>(response, HttpStatusCode.Created);
    }

    protected async Task<InstanceDetailDto> StartAsync(long workflowId)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/instances?detail=full", new StartInstanceRequest(workflowId, null, null, null));
        return await ReadAsync<InstanceDetailDto>(response, HttpStatusCode.Created);
    }

    protected async Task<InstanceDiagramHistoryDto> HistoryAsync(long instanceId)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/instances/{instanceId}/diagram-history");
        return await ReadAsync<InstanceDiagramHistoryDto>(response);
    }

    protected async Task TakeAsync(long instanceId, int flowId)
    {
        await using var db = Fixture.CreateDbContext();
        var id = await db.UserTasks.Where(task => task.InstanceId == instanceId && task.Status == "active")
            .OrderBy(task => task.Id).Select(task => task.Id).FirstAsync();
        using var response = await SendAsync(HttpMethod.Post, $"/api/user-tasks/{id}/flows/{flowId}", new TakeFlowRequest(null));
        await ExpectAsync(response, HttpStatusCode.OK);
    }

    protected async Task ProcessAsync(long instanceId, string kind)
    {
        long jobId;
        await using (var db = Fixture.CreateDbContext())
        {
            var job = await db.WorkflowJobs.SingleAsync(row => row.InstanceId == instanceId && row.Kind == kind);
            jobId = job.Id;
            job.Priority = 1_000_000;
            job.DueAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            await db.SaveChangesAsync();
        }
        await using var scope = Fixture.Factory.Services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>();
        var lease = Assert.Single(await jobs.LeaseRunnableAsync(new WorkflowJobLeaseRequest(
            $"diagram-summary:{Guid.NewGuid():N}", 1, 1, 4, TimeSpan.FromMinutes(1)), CancellationToken.None));
        Assert.Equal(jobId, lease.Job.Id);
        await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(lease, CancellationToken.None);
    }

    protected static HttpRequestMessage Request(HttpMethod method, string path, object? body = null, string? user = "administrator")
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        if (user is not null)
        {
            ApiTestAuth.Authorize(request, user, user == "administrator" ? ["admin"] : ["Reviewer"]);
            request.Headers.Add("X-Test-Suppress-Admin", "true");
        }
        return request;
    }

    protected Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null, string? user = "administrator") =>
        Fixture.Client.SendAsync(Request(method, path, body, user));

    protected static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        await ExpectAsync(response, expected);
        return (await response.Content.ReadFromJsonAsync<T>(JsonOptions))!;
    }

    protected static async Task ExpectAsync(HttpResponseMessage response, HttpStatusCode expected) =>
        Assert.True(response.StatusCode == expected, $"Expected {expected}; got {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");

    protected static void AddAsyncSource(WorkflowModel model)
    {
        model.FlowNodes.Add(new FlowNodeModel { Id = 5, Name = "Deferred source", Type = BpmnFlowNodeTypes.Task, AsyncAfter = true });
        model.SequenceFlows[0].TargetRef = 5;
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 2 });
    }

    protected static WorkflowModel Model(bool multiInstance = false)
    {
        var model = new WorkflowModel
        {
            Id = $"diagram-summary-{Guid.NewGuid():N}", Name = "Diagram summary", InitialEventId = 1,
            FlowNodes =
            [
                new() { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new() { Id = 2, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
                new() { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows = [new() { Id = 101, SourceRef = 1, TargetRef = 2 }, new() { Id = 201, SourceRef = 2, TargetRef = 3, Name = "Complete" }]
        };
        if (!multiInstance) return model;
        model.Variables.Add(new VariableModel { Id = 1, Name = "results", DataType = "json", DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>()) });
        model.FlowNodes[1].MultiInstance = new MultiInstanceModel
        {
            Mode = "sequential", Source = "cardinality", CardinalityExpression = "2", ResultVariable = "results", CompletionEvaluation = "afterAll"
        };
        model.SequenceFlows[1].CompletionCondition = "CountFlow(201) >= 2";
        model.SequenceFlows[1].CompletionPriority = 1;
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 3, IsDefault = true, IsSelectable = false });
        return model;
    }
}
