using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Flowbit.Service.Abstractions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class AuditClaimsApiTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task RealJwtSnapshotsFollowEachActionAndBothLifecycleActors()
    {
        await using var factory = new AuditApiFactory(fixture.ConnectionString, true);
        using var client = factory.CreateClient();
        Assert.Equal(["department", "sub", "email"],
            factory.Services.GetRequiredService<WorkflowAuditOptions>().AllowedClaims);
        var workflow = await CreateAsync(client);
        var started = await ReadAsync<StartInstanceResultDto>(await SendAsync(client, HttpMethod.Post,
            "/api/instances", "starter", ["Finance", "<script>example</script>", "Finance"],
            new StartInstanceRequest(workflow.Id, null, null, null)), HttpStatusCode.Created);
        await using var db = fixture.CreateDbContext();
        var task = await db.UserTasks.SingleAsync(row => row.InstanceId == started.Id);
        var visit = await db.NodeExecutions.SingleAsync(row => row.UserTaskId == task.Id);
        var originalStartedAt = visit.StartedAt;

        await AssertOkAsync(await SendAsync(client, HttpMethod.Post,
            $"/api/user-tasks/{task.Id}/claim", "alice", ["Operations"]));
        await AssertOkAsync(await SendAsync(client, HttpMethod.Post,
            $"/api/user-tasks/{task.Id}/claim", "ALICE", ["Changed token"]));
        var duringClaim = await ReadAsync<NodeExecutionDetailDto>(await SendAsync(client, HttpMethod.Get,
            $"/api/node-executions/{visit.Id}", "reader", []));
        Assert.Equal(["Finance", "<script>example</script>", "Finance"], duringClaim.StartedByClaims!["department"]);
        Assert.Null(duringClaim.CompletedByClaims);
        Assert.Equal(originalStartedAt, duringClaim.StartedAt);

        await AssertOkAsync(await SendAsync(client, HttpMethod.Post,
            $"/api/instances/{started.Id}/unclaim", "alice", ["Release team"]));
        await AssertOkAsync(await SendAsync(client, HttpMethod.Post,
            $"/api/user-tasks/{task.Id}/claim", "bob", ["Review"]));
        await AssertOkAsync(await SendAsync(client, HttpMethod.Post,
            $"/api/user-tasks/{task.Id}/flows/201", "bob", ["Approval", "Audit"], new TakeFlowRequest(null)));

        var detail = await ReadAsync<InstanceDetailDto>(await SendAsync(client, HttpMethod.Get,
            $"/api/instances/{started.Id}", "reader", []));
        var claims = detail.History.Where(row => row.Note == "taskClaim").ToArray();
        Assert.Equal(3, claims.Length);
        Assert.Equal(["Operations"], claims[0].ActorClaims!["department"]);
        Assert.Equal(["Release team"], claims[1].ActorClaims!["department"]);
        Assert.Equal(["Review"], claims[2].ActorClaims!["department"]);
        Assert.Equal(["alice"], claims[0].ActorClaims!["sub"]);
        Assert.Equal(["alice@example.test"], claims[0].ActorClaims!["email"]);
        Assert.All(detail.History, row => Assert.DoesNotContain("notSelected", row.ActorClaims!.Keys));
        var selected = Assert.Single(detail.History, row => row.SequenceFlowId == 201);
        Assert.Equal(["Approval", "Audit"], selected.ActorClaims!["department"]);
        Assert.DoesNotContain("department", selected.Payload!.Keys);

        var completed = await ReadAsync<NodeExecutionDetailDto>(await SendAsync(client, HttpMethod.Get,
            $"/api/node-executions/{visit.Id}", "reader", []));
        Assert.Equal("starter", completed.StartedBy);
        Assert.Equal("bob", completed.CompletedBy);
        Assert.Equal(["Finance", "<script>example</script>", "Finance"], completed.StartedByClaims!["department"]);
        Assert.Equal(["Approval", "Audit"], completed.CompletedByClaims!["department"]);
        Assert.Equal(originalStartedAt, completed.StartedAt);

        using var listResponse = await SendAsync(client, HttpMethod.Get,
            $"/api/node-executions?instanceId={started.Id}", "reader", []);
        using var list = JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync());
        Assert.All(list.RootElement.GetProperty("items").EnumerateArray(), item =>
        {
            Assert.False(item.TryGetProperty("startedByClaims", out _));
            Assert.False(item.TryGetProperty("completedByClaims", out _));
        });
        using var anonymous = await client.GetAsync($"/api/node-executions/{visit.Id}");
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DisabledAndMissingClaimsRemainDistinct(bool enabled)
    {
        await using var factory = new AuditApiFactory(fixture.ConnectionString, enabled, onlyAbsentClaim: true);
        using var client = factory.CreateClient();
        var workflow = await CreateAsync(client);
        var started = await ReadAsync<StartInstanceResultDto>(await SendAsync(client, HttpMethod.Post,
            "/api/instances", "starter", [], new StartInstanceRequest(workflow.Id, null, null, null)),
            HttpStatusCode.Created);
        var detail = await ReadAsync<InstanceDetailDto>(await SendAsync(client, HttpMethod.Get,
            $"/api/instances/{started.Id}", "reader", []));
        Assert.All(detail.History, row =>
        {
            if (enabled) Assert.Empty(Assert.IsAssignableFrom<IReadOnlyDictionary<string, string[]>>(row.ActorClaims));
            else Assert.Null(row.ActorClaims);
        });
        await using var db = fixture.CreateDbContext();
        var visits = await db.NodeExecutions.Where(row => row.InstanceId == started.Id).ToListAsync();
        Assert.All(visits, row =>
        {
            if (enabled) Assert.Empty(row.TriggeredByClaimsJson!.RootElement.EnumerateObject());
            else Assert.Null(row.TriggeredByClaimsJson);
        });
    }

    [Fact]
    public async Task DownstreamFailureRollsBackActionHistoryAndCompletionSnapshot()
    {
        await using var factory = new AuditApiFactory(fixture.ConnectionString, true);
        using var client = factory.CreateClient();
        var workflow = await CreateAsync(client, failingScript: true);
        var started = await ReadAsync<StartInstanceResultDto>(await SendAsync(client, HttpMethod.Post,
            "/api/instances", "starter", ["Start"], new StartInstanceRequest(workflow.Id, null, null, null)),
            HttpStatusCode.Created);
        await using var db = fixture.CreateDbContext();
        var task = await db.UserTasks.SingleAsync(row => row.InstanceId == started.Id);
        await AssertOkAsync(await SendAsync(client, HttpMethod.Post,
            $"/api/user-tasks/{task.Id}/claim", "alice", ["Claim"]));
        var before = await db.InstanceHistory.CountAsync(row => row.InstanceId == started.Id);
        using var failed = await SendAsync(client, HttpMethod.Post,
            $"/api/user-tasks/{task.Id}/flows/201", "alice", ["Failed action"], new TakeFlowRequest(null));
        Assert.Equal(HttpStatusCode.BadRequest, failed.StatusCode);
        Assert.Equal(before, await db.InstanceHistory.CountAsync(row => row.InstanceId == started.Id));
        var visit = await db.NodeExecutions.AsNoTracking().SingleAsync(row => row.UserTaskId == task.Id);
        Assert.Equal("active", visit.Status);
        Assert.Null(visit.CompletedByClaimsJson);
        Assert.Equal(["Start"], visit.TriggeredByClaimsJson!.RootElement.GetProperty("department")
            .EnumerateArray().Select(value => value.GetString()!).ToArray());
    }

    private static async Task<WorkflowDetailDto> CreateAsync(HttpClient client, bool failingScript = false)
    {
        var model = new WorkflowModel
        {
            Id = "jwt-audit-" + Guid.NewGuid().ToString("N"), Name = "JWT audit", InitialEventId = 1,
            TaskAssignmentRoles = ["admin"],
            Variables = [new VariableModel { Id = 1, Name = "number", DataType = WorkflowVariableTypes.Number,
                DefaultValue = JsonSerializer.SerializeToElement(0) }],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Review", Type = BpmnFlowNodeTypes.UserTask, RequiresClaim = true },
                new FlowNodeModel { Id = 4, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = failingScript ? 3 : 4 }
            ]
        };
        if (failingScript)
        {
            model.FlowNodes.Add(new FlowNodeModel { Id = 3, Name = "Fail", Type = BpmnFlowNodeTypes.ScriptTask,
                ScriptFormat = ScriptFormats.NCalc,
                Assignments = [new AssignmentModel { Variable = "number", Expression = "'not a number'" }] });
            model.SequenceFlows.Add(new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 });
        }
        return await ReadAsync<WorkflowDetailDto>(await SendAsync(client, HttpMethod.Post,
            "/api/workflows", "author", [], new CreateWorkflowRequest(model, true)), HttpStatusCode.Created);
    }

    private static Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path,
        string user, string[] departments, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body);
        var claims = departments.Select(value => new Claim("department", value)).ToList();
        claims.Add(new Claim("email", user + "@example.test"));
        claims.Add(new Claim("notSelected", "not persisted"));
        ApiTestAuth.AuthorizeWithClaims(request, user, claims, "admin");
        return client.SendAsync(request);
    }

    private static async Task AssertOkAsync(HttpResponseMessage response)
    {
        using (response)
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using (response)
        {
            Assert.True(response.StatusCode == expected,
                $"Expected {expected}, received {response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
            return (await response.Content.ReadFromJsonAsync<T>())!;
        }
    }

    private sealed class AuditApiFactory(string connectionString, bool enabled, bool onlyAbsentClaim = false)
        : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Flowbit"] = connectionString,
                    ["Jwt:Issuer"] = ApiTestAuth.Issuer, ["Jwt:Audience"] = ApiTestAuth.Audience,
                    ["Jwt:Key"] = ApiTestAuth.Key,
                    ["WorkflowAudit:AllowedClaims:0"] = enabled ? onlyAbsentClaim ? "absent" : "department" : "",
                    ["WorkflowAudit:AllowedClaims:1"] = enabled && !onlyAbsentClaim ? "sub" : "",
                    ["WorkflowAudit:AllowedClaims:2"] = enabled && !onlyAbsentClaim ? "email" : "",
                    ["Serilog:WriteTo:0:Name"] = "Console"
                }));
        }
    }
}
