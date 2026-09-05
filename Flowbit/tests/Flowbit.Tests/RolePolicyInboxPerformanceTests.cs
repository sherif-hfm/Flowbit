using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;
using Xunit.Abstractions;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class RolePolicyInboxPerformanceTests(PostgresApiFixture fixture, ITestOutputHelper output)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false, 240)]
    [InlineData(true, 1000)]
    public async Task InboxPolicyReadsRemainBatchBoundedAtPageSizes50And200(bool multiInstance, int total)
    {
        var model = Model(multiInstance, total);
        using var create = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
        Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        var workflow = (await create.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions))!;
        for (var index = 0; index < (multiInstance ? 1 : total); index++)
        {
            using var start = await SendAsync(HttpMethod.Post, "/api/instances",
                new StartInstanceRequest(workflow.Id, null, null, null));
            Assert.True(start.StatusCode == HttpStatusCode.Created, await start.Content.ReadAsStringAsync());
        }

        // Warm immutable definition/settings caches before comparing request costs.
        await InboxAsync(workflow.Id, 50);
        var readerCounts = new List<int>();
        foreach (var pageSize in new[] { 50, 200 })
        {
            fixture.CommandCounter.Reset(captureReaderCommandTexts: true);
            var page = await InboxAsync(workflow.Id, pageSize);
            readerCounts.Add(fixture.CommandCounter.ReaderCommands);
            var commandTexts = fixture.CommandCounter.ReaderCommandTexts;
            Assert.Equal(total, page.TotalCount);
            Assert.Equal(pageSize, page.Items.Count);
            Assert.All(page.Items, item => Assert.True(item.CanAct));
            // The captured SQL page's exact policy IDs are fetched in one EF
            // command. This fails if a future change loads one policy per task.
            Assert.Single(commandTexts, command => command.Contains(
                "FROM flowbit.user_task_role_policies", StringComparison.OrdinalIgnoreCase));
            output.WriteLine($"multiInstance={multiInstance}; total={total}; pageSize={pageSize}; EF reader commands={readerCounts[^1]}; policy batch commands=1");
        }
        Assert.Equal(readerCounts[0], readerCounts[1]);
        Assert.InRange(readerCounts[1], 1, 12);
    }

    private async Task<PagedResult<InboxItemDto>> InboxAsync(long workflowId, int pageSize)
    {
        using var response = await SendAsync(HttpMethod.Get,
            $"/api/instances/inbox?workflowId={workflowId}&pageSize={pageSize}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PagedResult<InboxItemDto>>(JsonOptions))!;
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        ApiTestAuth.Authorize(request, "role-policy-query-budget", ["admin", "Finance", "Approver"]);
        return fixture.Client.SendAsync(request);
    }

    private static WorkflowModel Model(bool multi, int count)
    {
        var model = new WorkflowModel
        {
            Id = $"role-policy-query-budget-{Guid.NewGuid():N}", Name = "Role policy query budget", InitialEventId = 1,
            Variables =
            [
                new VariableModel { Id = 1, Name = "taskRoles", DataType = "string", IsArray = true,
                    DefaultValue = JsonSerializer.SerializeToElement(new[] { "Finance" }) },
                new VariableModel { Id = 2, Name = "actionRoles", DataType = "string", IsArray = true,
                    DefaultValue = JsonSerializer.SerializeToElement(new[] { "Approver" }) }
            ],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = "startEvent" },
                new FlowNodeModel { Id = 2, Name = "Review", Type = "userTask", RolesVariable = "taskRoles" },
                new FlowNodeModel { Id = 3, Name = "Done", Type = "endEvent" }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3, Name = "Approve", RolesVariable = "actionRoles" }
            ]
        };
        if (!multi) return model;
        model.Variables.Add(new VariableModel { Id = 3, Name = "results", DataType = "json",
            DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>()) });
        model.FlowNodes[1].MultiInstance = new MultiInstanceModel
        {
            Mode = "parallel", Source = "cardinality", CardinalityExpression = count.ToString(), ResultVariable = "results"
        };
        model.SequenceFlows[1].CompletionCondition = $"CountFlow(201) >= {count}";
        model.SequenceFlows[1].CompletionPriority = 1;
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 3, IsDefault = true, IsSelectable = false });
        return model;
    }
}
