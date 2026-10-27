using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class VariablePresentationApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task EveryDeclarationSurfaceReturnsPresentationAndOlderDeclarationsGainNoKeys()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var model = BuildModel(suffix);

        using var createResponse = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(model, true));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var created = await ReadJsonAsync(createResponse);
        AssertDefinition(created["definition"]!);
        var workflowId = created["id"]!.GetValue<long>();
        AssertDefinition((await GetJsonAsync($"/api/workflows/{workflowId}", "definition-reader", "admin"))["definition"]!);

        using var startResponse = await SendAsync(
            HttpMethod.Post,
            "/api/instances",
            new StartInstanceRequest(workflowId, null, null, new Dictionary<string, JsonElement>
            {
                ["title"] = JsonSerializer.SerializeToElement("Laptop"),
                ["category"] = JsonSerializer.SerializeToElement("hardware")
            }),
            "starter",
            "Requester");
        Assert.Equal(HttpStatusCode.Created, startResponse.StatusCode);
        var instanceId = (await ReadJsonAsync(startResponse))["id"]!.GetValue<long>();
        AssertDefinition((await GetJsonAsync($"/api/instances/{instanceId}", "instance-reader", "Requester"))["workflow"]!["definition"]!);

        AssertActionInput(Single(await GetJsonAsync($"/api/instances/{instanceId}/flows", "reviewer", "Reviewer"))["variables"]![0]!);
        var inbox = await GetJsonAsync($"/api/instances/inbox?instanceId={instanceId}", "reviewer", "Reviewer");
        var taskId = Single(inbox["items"]!)["userTaskId"]!.GetValue<long>();
        AssertActionInput(Single(await GetJsonAsync($"/api/user-tasks/{taskId}/flows", "reviewer", "Reviewer"))["variables"]![0]!);

        var positions = await GetJsonAsync($"/api/instances/{instanceId}/administrative-actions", "operator", "admin");
        var action = Single(Single(positions["items"]!)["actions"]!);
        AssertActionInput(action["variables"]![0]!);
    }

    [Fact]
    public async Task AdministrativeBatchDetailKeepsPresentationOfTheExecutedAction()
    {
        var model = BuildModel(Guid.NewGuid().ToString("N"));
        using var createResponse = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var workflowId = (await ReadJsonAsync(createResponse))["id"]!.GetValue<long>();
        using var startResponse = await SendAsync(
            HttpMethod.Post,
            "/api/instances",
            new StartInstanceRequest(workflowId, null, null, new Dictionary<string, JsonElement>
            {
                ["title"] = JsonSerializer.SerializeToElement("Monitor")
            }),
            "starter",
            "Requester");
        Assert.Equal(HttpStatusCode.Created, startResponse.StatusCode);
        var instanceId = (await ReadJsonAsync(startResponse))["id"]!.GetValue<long>();
        var discovered = Single((await GetJsonAsync(
            $"/api/instances/{instanceId}/administrative-actions", "operator", "admin"))["items"]!);
        var position = discovered["position"]!;

        using var executeResponse = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{instanceId}/administrative-actions",
            new ExecuteInstanceAdministrativeActionRequest
            {
                ExpectedWorkflowDefinitionId = position["workflowDefinitionId"]!.GetValue<long>(),
                SourceNodeId = position["nodeId"]!.GetValue<int>(),
                PositionKind = position["positionKind"]!.GetValue<string>(),
                PositionId = position["positionId"]!.GetValue<long>(),
                FlowId = Single(discovered["actions"]!)["flowId"]!.GetValue<int>(),
                ExpectedTokenId = position["tokenId"]!.GetValue<long>(),
                ExpectedTokenActivationId = position["tokenActivationId"]!.GetValue<Guid>(),
                ExpectedPositionUpdatedAt = position["positionUpdatedAt"]!.GetValue<DateTimeOffset>(),
                ExpectedAffectedTaskCount = position["affectedTaskCount"]!.GetValue<int>(),
                Variables = new Dictionary<string, JsonElement> { ["score"] = JsonSerializer.SerializeToElement(2.50m) }
            },
            "operator",
            "admin");
        Assert.Equal(HttpStatusCode.OK, executeResponse.StatusCode);
        var batchId = (await ReadJsonAsync(executeResponse))["administrativeActionBatchId"]!.GetValue<long>();

        var batch = await GetJsonAsync($"/api/administrative-action-batches/{batchId}", "operator", "admin");

        AssertActionInput(batch["action"]!["variables"]![0]!);
    }

    [Fact]
    public async Task MultiInstanceInterruptFlowsReturnPresentation()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var model = DefinitionValidationTests.LoadModel("votes-users-list.json");
        model.Id = $"mi-presentation-api-{suffix}";
        model.Name = $"MI presentation API {suffix}";
        model.SequenceFlows.Single(flow => flow.Id == 203).Variables =
        [
            new VariableModel
            {
                Id = 1,
                Name = "cancelReason",
                DataType = WorkflowVariableTypes.String,
                Label = "Cancellation reason",
                Options = [new VariableOptionModel { Value = JsonSerializer.SerializeToElement("duplicate"), Label = "Duplicate" }]
            }
        ];
        using var createResponse = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
        Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        var workflowId = (await ReadJsonAsync(createResponse))["id"]!.GetValue<long>();
        using var startResponse = await SendAsync(
            HttpMethod.Post,
            "/api/instances",
            new StartInstanceRequest(workflowId, null, null, null),
            "starter",
            "Requester");
        Assert.Equal(HttpStatusCode.Created, startResponse.StatusCode);
        var instanceId = (await ReadJsonAsync(startResponse))["id"]!.GetValue<long>();
        using var enterResponse = await SendAsync(
            HttpMethod.Post,
            $"/api/instances/{instanceId}/flows/204",
            new TakeFlowRequest(new Dictionary<string, JsonElement>
            {
                ["voters"] = JsonSerializer.SerializeToElement(new[] { "alice", "bob" })
            }),
            "manager",
            "Manager");
        Assert.Equal(HttpStatusCode.OK, enterResponse.StatusCode);
        var executionId = (await ReadJsonAsync(enterResponse))["multiInstance"]!["executionId"]!.GetValue<long>();

        var interrupt = Single(await GetJsonAsync($"/api/multi-instance-executions/{executionId}/flows", "manager", "Manager"));

        var variable = interrupt["variables"]![0]!;
        Assert.Equal("Cancellation reason", variable["label"]!.GetValue<string>());
        Assert.Equal("duplicate", variable["options"]![0]!["value"]!.GetValue<string>());
        Assert.Equal("Duplicate", variable["options"]![0]!["label"]!.GetValue<string>());
    }

    [Fact]
    public async Task StrictValidationAcceptsPresentation()
    {
        var model = BuildModel(Guid.NewGuid().ToString("N"));

        using var response = await SendAsync(HttpMethod.Post, "/api/workflows/validate", model);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = await response.Content.ReadFromJsonAsync<AiValidationResultDto>(JsonOptions);
        Assert.True(result!.IsValid, string.Join(Environment.NewLine, result.Errors));
    }

    private static void AssertDefinition(JsonNode definition)
    {
        var start = definition["flowNodes"]!.AsArray().Single(node => node!["id"]!.GetValue<int>() == 1)!;
        var title = start["variables"]![0]!;
        Assert.Equal("Request title", title["label"]!.GetValue<string>());
        Assert.Equal("Name the item.\nKeep it short.", title["helpText"]!.GetValue<string>());
        var category = start["variables"]![1]!;
        Assert.Collection(
            category["options"]!.AsArray(),
            option =>
            {
                Assert.Equal("hardware", option!["value"]!.GetValue<string>());
                Assert.Equal("Hardware", option["label"]!.GetValue<string>());
            },
            option =>
            {
                Assert.Equal("software", option!["value"]!.GetValue<string>());
                Assert.False(option.AsObject().ContainsKey("label"));
            });
        var note = start["variables"]![2]!.AsObject();
        Assert.False(note.ContainsKey("label"));
        Assert.False(note.ContainsKey("helpText"));
        Assert.False(note.ContainsKey("options"));
        AssertActionInput(definition["sequenceFlows"]!.AsArray()
            .Single(flow => flow!["id"]!.GetValue<int>() == 201)!["variables"]![0]!);
    }

    private static void AssertActionInput(JsonNode variable)
    {
        Assert.Equal("Score", variable["label"]!.GetValue<string>());
        Assert.Equal("1", variable["options"]![0]!["value"]!.ToJsonString());
        Assert.Equal("2.50", variable["options"]![1]!["value"]!.ToJsonString());
        Assert.Equal("High", variable["options"]![1]!["label"]!.GetValue<string>());
    }

    private static JsonNode Single(JsonNode array) => Assert.Single(array.AsArray())!;

    private static WorkflowModel BuildModel(string suffix) => new()
    {
        Id = $"variable-presentation-api-{suffix}",
        Name = $"Variable presentation API {suffix}",
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 1,
                Name = "Start",
                Type = BpmnFlowNodeTypes.StartEvent,
                Roles = ["Requester"],
                Variables =
                [
                    new VariableModel
                    {
                        Id = 1,
                        Name = "title",
                        DataType = WorkflowVariableTypes.String,
                        Required = true,
                        Label = "Request title",
                        HelpText = "Name the item.\nKeep it short."
                    },
                    new VariableModel
                    {
                        Id = 2,
                        Name = "category",
                        DataType = WorkflowVariableTypes.String,
                        Options =
                        [
                            new VariableOptionModel { Value = JsonSerializer.SerializeToElement("hardware"), Label = "Hardware" },
                            new VariableOptionModel { Value = JsonSerializer.SerializeToElement("software") }
                        ]
                    },
                    new VariableModel { Id = 3, Name = "note", DataType = WorkflowVariableTypes.String }
                ]
            },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Review",
                Type = BpmnFlowNodeTypes.UserTask,
                Roles = ["Reviewer"]
            },
            new FlowNodeModel { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, Name = "Open review", SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel
            {
                Id = 201,
                Name = "Score",
                SourceRef = 2,
                TargetRef = 3,
                Roles = ["Reviewer"],
                Variables =
                [
                    new VariableModel
                    {
                        Id = 1,
                        Name = "score",
                        DataType = WorkflowVariableTypes.Number,
                        Label = "Score",
                        Options =
                        [
                            new VariableOptionModel { Value = JsonSerializer.SerializeToElement(1) },
                            new VariableOptionModel { Value = JsonSerializer.SerializeToElement(2.50m), Label = "High" }
                        ]
                    }
                ]
            }
        ]
    };

    private async Task<JsonNode> GetJsonAsync(string path, string user, params string[] roles)
    {
        using var response = await SendAsync(HttpMethod.Get, path, null, user, roles);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadJsonAsync(response);
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "test-admin",
        params string[] roles)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, user, roles);
        return await fixture.Client.SendAsync(request);
    }

    private static async Task<JsonNode> ReadJsonAsync(HttpResponseMessage response) =>
        JsonNode.Parse(await response.Content.ReadAsStringAsync())
        ?? throw new InvalidOperationException("Response body was empty.");
}
