using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class InstanceAdministrativeActionLifecycleTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(AdministrativeActionMultiInstanceModes.CompleteAllChildren)]
    [InlineData(AdministrativeActionMultiInstanceModes.ForceParent)]
    public async Task SequentialPendingChildVisitIsStartedOnlyWhenAdministrativelyCompleted(string mode)
    {
        var model = new WorkflowModel
        {
            Id = $"administrative-sequential-ledger-{Guid.NewGuid():N}",
            Name = "Administrative sequential ledger",
            InitialEventId = 1,
            Variables = [new VariableModel { Id = 1, Name = "results", DataType = WorkflowVariableTypes.Json, DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>()) }],
            FlowNodes =
            [
                new() { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new()
                {
                    Id = 2, Name = "Review", Type = BpmnFlowNodeTypes.UserTask, Roles = ["Reviewer"],
                    MultiInstance = new MultiInstanceModel
                    {
                        Mode = MultiInstanceModes.Sequential, Source = MultiInstanceSources.Cardinality,
                        CardinalityExpression = "3", ResultVariable = "results"
                    }
                },
                new() { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new() { Id = 101, SourceRef = 1, TargetRef = 2 },
                new()
                {
                    Id = 201, Name = "Approve", SourceRef = 2, TargetRef = 3,
                    CompletionCondition = "CountFlow(201) >= 3", CompletionPriority = 1
                },
                new() { Id = 202, SourceRef = 2, TargetRef = 3, IsDefault = true, IsSelectable = false }
            ]
        };
        var instance = await StartAsync(model);
        await using var db = fixture.CreateDbContext();
        var before = await db.NodeExecutions.AsNoTracking()
            .Where(visit => visit.InstanceId == instance.Id && visit.UserTaskId != null)
            .OrderBy(visit => visit.ItemIndex).ToListAsync();
        Assert.Equal(3, before.Count);
        var active = Assert.Single(before, visit => visit.Status == "active");
        var pendingIds = before.Where(visit => visit.Status == "pending").Select(visit => visit.Id).ToArray();
        Assert.Equal(2, pendingIds.Length);
        Assert.All(before.Where(visit => pendingIds.Contains(visit.Id)), visit => Assert.Null(visit.StartedAt));

        var result = await ExecuteAsync(instance.Id, mode: mode);
        Assert.Equal("completed", result.Instance.Status);
        var visits = await db.NodeExecutions.AsNoTracking()
            .Where(visit => visit.InstanceId == instance.Id && visit.UserTaskId != null).ToListAsync();
        Assert.Equal(active.StartedAt, visits.Single(visit => visit.Id == active.Id).StartedAt);
        foreach (var visit in visits.Where(visit => pendingIds.Contains(visit.Id)))
        {
            Assert.Equal(mode == AdministrativeActionMultiInstanceModes.CompleteAllChildren
                ? "administrativeAction" : "multiInstanceInterrupt", visit.CompletionReason);
            Assert.Equal("administrator", visit.CompletedBy);
            Assert.NotNull(visit.CompletedAt);
            if (mode == AdministrativeActionMultiInstanceModes.CompleteAllChildren)
            {
                Assert.Equal("completed", visit.Status);
                Assert.Equal(visit.CompletedAt, visit.StartedAt);
                Assert.Equal("administrator", visit.TriggeredBy);
                Assert.Contains("admin", visit.TriggeredByRolesJson!.RootElement.EnumerateArray().Select(role => role.GetString()));
            }
            else
            {
                Assert.Equal("cancelled", visit.Status);
                Assert.Null(visit.StartedAt);
            }
        }
        Assert.Equal("completed", (await db.AdministrativeActionBatches.SingleAsync(batch => batch.Id == result.AdministrativeActionBatchId)).Status);
    }

    [Fact]
    public async Task ConditionalBoundaryEarlyReturnCommitsOverrideAndAuditInOneOuterTransaction()
    {
        var model = JsonSerializer.Deserialize<WorkflowModel>(
            ExampleWorkflowData.Read("examples/basics/08-interrupting-conditional-boundary.json"), JsonOptions)!;
        model.Id = $"administrative-conditional-boundary-{Guid.NewGuid():N}";
        model.FlowNodes.Single(node => node.Id == 2).Roles = ["Reviewer"];
        model.SequenceFlows.Single(flow => flow.Id == 201).Variables =
        [new VariableModel { Id = 10, Name = "riskScore", DataType = WorkflowVariableTypes.Number, Required = true }];
        var instance = await StartAsync(model);
        var result = await ExecuteAsync(instance.Id, variables: new()
        {
            ["riskScore"] = JsonSerializer.SerializeToElement(90)
        });
        Assert.Equal("running", result.Instance.Status);
        await using var db = fixture.CreateDbContext();
        var work = await db.UserTasks.AsNoTracking().Where(task => task.InstanceId == instance.Id).ToListAsync();
        Assert.Equal("cancelled", work.Single(task => task.NodeId == 2).Status);
        Assert.Equal("active", work.Single(task => task.NodeId == 5).Status);
        var action = Assert.Single(await db.SequenceFlowOccurrences.AsNoTracking()
            .Where(occurrence => occurrence.InstanceId == instance.Id && occurrence.SequenceFlowId == 201).ToListAsync());
        Assert.True(action.IsAction);
        Assert.False(action.IsTraversal);
        Assert.Equal("completed", (await db.AdministrativeActionBatches.SingleAsync(batch => batch.Id == result.AdministrativeActionBatchId)).Status);
        Assert.Equal("succeeded", (await db.AdministrativeActionBatchItems.SingleAsync(item => item.BatchId == result.AdministrativeActionBatchId)).Status);
        Assert.False(await db.WorkflowJobs.AnyAsync(job => job.InstanceId == instance.Id));
        var savedValue = await db.InstanceVariableCurrentValues.SingleAsync(value => value.InstanceId == instance.Id && value.VariableName == "riskScore");
        Assert.Equal(90, savedValue.ValueJson.RootElement.GetInt32());
    }

    private async Task<InstanceDetailDto> StartAsync(WorkflowModel model)
    {
        using var definitionResponse = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
        Assert.True(definitionResponse.StatusCode == HttpStatusCode.Created, await definitionResponse.Content.ReadAsStringAsync());
        var workflow = (await definitionResponse.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions))!;
        using var instanceResponse = await SendAsync(HttpMethod.Post, "/api/instances?detail=full", new StartInstanceRequest(workflow.Id, null, null, null));
        Assert.True(instanceResponse.StatusCode == HttpStatusCode.Created, await instanceResponse.Content.ReadAsStringAsync());
        return (await instanceResponse.Content.ReadFromJsonAsync<InstanceDetailDto>(JsonOptions))!;
    }

    private async Task<AdministrativeActionResultDto> ExecuteAsync(
        long instanceId,
        string? mode = null,
        Dictionary<string, JsonElement>? variables = null)
    {
        var path = $"/api/instances/{instanceId}/administrative-actions";
        using var discovery = await SendAsync(HttpMethod.Get, path);
        Assert.Equal(HttpStatusCode.OK, discovery.StatusCode);
        var position = Assert.Single((await discovery.Content.ReadFromJsonAsync<PagedResult<InstanceAdministrativeActionPositionDto>>(JsonOptions))!.Items).Position;
        using var response = await SendAsync(HttpMethod.Post, path, new ExecuteInstanceAdministrativeActionRequest
        {
            ExpectedWorkflowDefinitionId = position.WorkflowDefinitionId,
            SourceNodeId = position.NodeId,
            PositionKind = position.PositionKind,
            PositionId = position.PositionId,
            FlowId = 201,
            ExpectedTokenId = position.TokenId,
            ExpectedTokenActivationId = position.TokenActivationId,
            ExpectedPositionUpdatedAt = position.PositionUpdatedAt,
            ExpectedAffectedTaskCount = position.AffectedTaskCount,
            MultiInstanceMode = mode,
            Variables = variables
        });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<AdministrativeActionResultDto>(JsonOptions))!;
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        ApiTestAuth.Authorize(request, "administrator", ["admin"]);
        request.Headers.TryAddWithoutValidation("X-Test-Suppress-Admin", "true");
        return await fixture.Client.SendAsync(request);
    }
}
