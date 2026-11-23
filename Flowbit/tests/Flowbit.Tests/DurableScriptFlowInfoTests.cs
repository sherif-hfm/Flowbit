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

/// <summary>
/// A script task with asyncBefore stages the instance's FlowInfo summaries in its job snapshot and reads them back when
/// the job runs. Both script formats see the evidence of the flow that reached them, and the job opens no incident.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class DurableScriptFlowInfoTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(ScriptFormats.NCalc)]
    [InlineData(ScriptFormats.JavaScript)]
    public async Task ADurableScriptReadsTheFlowInfoOfTheFlowThatReachedIt(string format)
    {
        var script = new FlowNodeModel { Id = 2, Name = "Record the route", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true };
        if (format == ScriptFormats.NCalc)
        {
            script.Assignments =
            [
                new AssignmentModel { Variable = "arrivedBy", Expression = "FlowInfo(101, 'traversals.last.kind')" },
                new AssignmentModel { Variable = "arrivals", Expression = "FlowInfo(101, 'traversals.count')" }
            ];
        }
        else
        {
            script.ScriptFormat = ScriptFormats.JavaScript;
            script.UsesFlowInfo = true;
            script.Script = """
                var info = execution.getFlowInfo(101);
                execution.setVariable('arrivedBy', info.traversals.last.kind);
                execution.setVariable('arrivals', info.traversals.count);
                """;
        }
        var model = new WorkflowModel
        {
            Id = $"durable-flowinfo-{format}-{Guid.NewGuid():N}",
            Name = "Durable FlowInfo",
            InitialEventId = 1,
            Variables =
            [
                new VariableModel { Id = 1, Name = "arrivedBy", DataType = WorkflowVariableTypes.String, DefaultValue = JsonSerializer.SerializeToElement("unknown") },
                new VariableModel { Id = 2, Name = "arrivals", DataType = WorkflowVariableTypes.Number, DefaultValue = JsonSerializer.SerializeToElement(0) }
            ],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                script,
                new FlowNodeModel { Id = 3, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 4, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4, Name = "Done" }
            ]
        };

        long workflowId;
        using (var create = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true)))
        {
            Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
            workflowId = (await create.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions))!.Id;
        }
        long instanceId;
        using (var start = await SendAsync(HttpMethod.Post, "/api/instances", new StartInstanceRequest(workflowId, null, null, null)))
        {
            Assert.True(start.StatusCode == HttpStatusCode.Created, await start.Content.ReadAsStringAsync());
            instanceId = (await start.Content.ReadFromJsonAsync<InstanceDetailDto>(JsonOptions))!.Id;
        }

        long jobId;
        await using (var db = fixture.CreateDbContext())
        {
            jobId = await db.WorkflowJobs.AsNoTracking()
                .Where(job => job.InstanceId == instanceId && job.NodeId == 2)
                .Select(job => job.Id)
                .SingleAsync();
        }
        for (var round = 0; round < 10 && !await TaskIsActiveAsync(instanceId); round++)
        {
            await using (var db = fixture.CreateDbContext())
            {
                var job = await db.WorkflowJobs.SingleAsync(item => item.Id == jobId);
                if (job.Status is not (WorkflowJobStatuses.Queued or WorkflowJobStatuses.Retry)) break;
                job.Priority = 1_000_000;
                job.DueAt = DateTimeOffset.UtcNow.AddMinutes(-1);
                await db.SaveChangesAsync();
            }
            await LeaseAndProcessOneAsync();
        }

        await using (var db = fixture.CreateDbContext())
        {
            var incidents = await db.WorkflowIncidents.AsNoTracking()
                .Where(item => item.InstanceId == instanceId)
                .Select(item => item.Summary)
                .ToListAsync();
            Assert.Empty(incidents);
            Assert.True(await TaskIsActiveAsync(instanceId), "The script job did not move the instance to the task.");
            var values = await db.InstanceVariableCurrentValues.AsNoTracking()
                .Where(item => item.InstanceId == instanceId)
                .ToDictionaryAsync(item => item.VariableName, item => item.ValueJson.RootElement.Clone());
            Assert.Equal("start", values["arrivedBy"].GetString());
            Assert.Equal(1, values["arrivals"].GetDecimal());
        }
    }

    /// <summary>The stored shape, as both durable writers serialize a snapshot, reads back through one method.</summary>
    [Fact]
    public void FromStoredJson_ReadsWhatTheSerializerWrites()
    {
        var summary = new SequenceFlowRuntimeSummary(
            101,
            SequenceFlowRuntimeView.Empty,
            new SequenceFlowRuntimeView(2, new SequenceFlowLastOccurrence("alice", ["clerk"], DateTimeOffset.UnixEpoch, "start", null)));
        var snapshot = new SequenceFlowInfoSnapshot([101, 201], [summary]);

        var stored = JsonSerializer.SerializeToElement(snapshot);
        var read = SequenceFlowInfoSnapshot.FromStoredJson(stored);

        Assert.Equal([101, 201], read.KnownFlowIds.Order());
        Assert.Equal(2, read.GetSummary(101).Traversals.Count);
        Assert.Equal("start", read.GetSummary(101).Traversals.Last!.Kind);
        Assert.Equal(0, read.GetSummary(201).Traversals.Count);
        Assert.Equal(stored.GetRawText(), JsonSerializer.SerializeToElement(read).GetRawText());
        Assert.Throws<JsonException>(() => SequenceFlowInfoSnapshot.FromStoredJson(JsonSerializer.SerializeToElement(new { KnownFlowIds = new[] { 1 } })));
    }

    private async Task<bool> TaskIsActiveAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.UserTasks.AnyAsync(task => task.InstanceId == instanceId && task.NodeId == 3 && task.Status == UserTaskStatuses.Active);
    }

    private async Task LeaseAndProcessOneAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var leases = await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().LeaseRunnableAsync(
            new WorkflowJobLeaseRequest($"durable-flowinfo-test-{Guid.NewGuid():N}", 1, 1, 1, TimeSpan.FromMinutes(2)),
            CancellationToken.None);
        foreach (var lease in leases)
        {
            await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>().ProcessAsync(lease, CancellationToken.None);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, "durable-flowinfo", ["admin"]);
        return await fixture.Client.SendAsync(request);
    }
}
