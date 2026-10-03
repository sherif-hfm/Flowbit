using System.Text.Json;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Flowbit.Infrastructure.Scripting;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Ai;
using Flowbit.Service.Authoring;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flowbit.Tests;

public sealed class WorkflowAiProtocolTests
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
        { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private const string Key = "protocol-test-provider-key";
    private const string Finish = """{"kind":"finish","message":"Review this workflow."}""";

    [Fact]
    public void RunDeadlineCanBeShortenedButCannotExceedFiveMinutes()
    {
        new WorkflowAiOptions { RunTimeoutSeconds = 1 }.Validate();
        new WorkflowAiOptions { RunTimeoutSeconds = 300 }.Validate();
        var error = Assert.Throws<WorkflowAiException>(() => new WorkflowAiOptions { RunTimeoutSeconds = 301 }.Validate());
        Assert.Equal("provider_configuration", error.Code);
    }

    [Theory]
    [InlineData("مراجعة طلب الشراء")]
    [InlineData("中日文审批流程")]
    [InlineData("<review>&approve</review>")]
    public async Task InitialCheckpointCannotExpandBeyondTheWorkflowLimit(string name)
    {
        var model = Model();
        model.Name = string.Concat(Enumerable.Repeat(name, 20));
        var json = JsonSerializer.Serialize(model, new JsonSerializerOptions(Json) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var original = Parse(json);
        var options = new WorkflowAiOptions { MaxWorkflowCharacters = Encoding.UTF8.GetByteCount(json) + 8 };
        var provider = new Provider(Finish);
        var events = new List<AiRunEventDto>();
        Task Observe(AiRunEventDto frame, CancellationToken _) { events.Add(frame); return Task.CompletedTask; }

        var error = await Assert.ThrowsAsync<WorkflowAiException>(() => Service(provider, options)
            .RunAsync(Request() with { CurrentWorkflow = original }, Key, Observe, CancellationToken.None));

        Assert.Equal("workflow_too_large", error.Code);
        Assert.Equal(413, error.StatusCode);
        Assert.Empty(provider.Calls);
        Assert.Empty(events);
    }

    [Theory]
    [InlineData("Here is the complete command.\n", "\nReady for review.")]
    [InlineData("", "")]
    public async Task OneCompleteCommandWithSurroundingProseReturnsValidatedProposal(string prefix, string suffix)
    {
        var provider = new Provider(prefix + Finish + suffix);
        var result = await Service(provider).TurnAsync(Request(), Key, CancellationToken.None);

        Assert.Equal("proposal", result.Kind);
        Assert.True(result.Validation.IsValid);
        Assert.Equal("protocol-workflow", result.Definition!.Value.GetProperty("id").GetString());
        Assert.Single(provider.Calls);
    }

    [Theory]
    [InlineData("""{"kind":"finish"}{"kind":"edit","baseRevision":0,"batchId":"hidden","operations":[]}""")]
    [InlineData("""{"kind":"finish","operations":[{"op":"set","target":"workflow","path":"/name","value":"Silently ignored change"}]}""")]
    public async Task MultipleCommandsOrInapplicableFieldsAreRejectedWithoutApplyingAnything(string response)
    {
        var provider = new Provider(response);
        var result = await Service(provider, new() { MaxRepairAttempts = 0 }).TurnAsync(Request(), Key, CancellationToken.None);

        Assert.Equal("invalid", result.Kind);
        Assert.Null(result.Definition);
        Assert.NotEmpty(result.Validation.Errors);
        Assert.Equal(0, result.Checkpoint!.Revision);
        Assert.Equal("Protocol workflow", result.Checkpoint.Draft.GetProperty("name").GetString());
        Assert.Single(provider.Calls);
    }

    [Theory]
    [InlineData("set")]
    [InlineData("node")]
    public async Task CredentialLiteralsDoNotRewriteOperationDiscriminators(string credential)
    {
        var model = Model();
        model.TaskDistribution = new() { ClientId = "dispatcher", ClientSecret = credential };
        var provider = new Provider(
            """{"kind":"edit","baseRevision":0,"batchId":"rename","operations":[{"op":"set","target":"node","id":2,"path":"/name","value":"Updated review"}]}""",
            Finish);
        var request = Request() with { CurrentWorkflow = JsonSerializer.SerializeToElement(model, Json) };
        var result = await Service(provider).TurnAsync(request, Key, CancellationToken.None);

        Assert.Equal("proposal", result.Kind);
        Assert.Equal("Updated review", result.Definition!.Value.GetProperty("flowNodes")[1].GetProperty("name").GetString());
        Assert.Equal(credential, result.Definition.Value.GetProperty("taskDistribution").GetProperty("clientSecret").GetString());
        Assert.Equal(2, provider.Calls.Count);
        foreach (var call in provider.Calls)
        {
            using var context = JsonDocument.Parse(call.Last().Content);
            var outboundSecret = context.RootElement.GetProperty("currentWorkflow").GetProperty("taskDistribution").GetProperty("clientSecret").GetString();
            Assert.Contains("FLOWBIT_REDACTED_", outboundSecret);
            Assert.NotEqual(credential, outboundSecret);
            Assert.Contains("{op:\"set\",target:\"node\"", call[0].Content);
        }
    }

    [Theory]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("\"unexpected\"")]
    [InlineData("[]")]
    public async Task NonObjectCurrentWorkflowIsRejectedBeforeProviderCall(string value)
    {
        var provider = new Provider(Finish);
        var request = Request() with { CurrentWorkflow = Parse(value) };
        var error = await Assert.ThrowsAsync<WorkflowAiException>(() => Service(provider).TurnAsync(request, Key, CancellationToken.None));

        Assert.Equal("invalid_current_workflow", error.Code);
        Assert.Empty(provider.Calls);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("true")]
    [InlineData("\"unexpected\"")]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData(null)]
    public async Task MalformedCheckpointDraftFailsWithSanitizedDomainErrorBeforeProviderCall(string? value)
    {
        var request = Request() with { CurrentWorkflow = null };
        var initial = new Provider("""{"kind":"read","reads":[{"kind":"source","resource":"requirements","offset":0,"count":100}]}""");
        var paused = await Service(initial, new() { MaxProviderCalls = 1 }).TurnAsync(request, Key, CancellationToken.None);
        Assert.Equal("paused", paused.Kind);
        Assert.NotNull(paused.Checkpoint);
        var checkpoint = paused.Checkpoint with { Draft = value is null ? default : Parse(value) };
        var continuation = new Provider(Finish);

        var error = await Assert.ThrowsAsync<WorkflowAiException>(() =>
            Service(continuation).TurnAsync(request with { Checkpoint = checkpoint }, Key, CancellationToken.None));

        Assert.Equal("invalid_checkpoint", error.Code);
        Assert.Empty(continuation.Calls);
        Assert.DoesNotContain("unexpected", error.Message);
    }

    [Fact]
    public void OpaqueBusinessJsonWithDuplicateIdsCannotMoveCredentialPlaceholders()
    {
        var original = Parse("""
            {"variables":[{"id":1,"name":"payload","dataType":"json","defaultValue":{"items":[
              {"id":7,"secret":"alpha-private-value"},{"id":7,"secret":"beta-private-value"}
            ]}}]}
            """);
        var redaction = new WorkflowAiRedaction();
        var protectedCopy = redaction.Redact(original);
        var copy = JsonNode.Parse(protectedCopy.GetRawText())!;
        var items = copy["variables"]![0]!["defaultValue"]!["items"]!.AsArray();
        var firstSecret = items[0]!["secret"]!.DeepClone();
        items[0]!["secret"] = items[1]!["secret"]!.DeepClone();
        items[1]!["secret"] = firstSecret;

        Assert.Throws<JsonException>(() => redaction.Restore(JsonSerializer.SerializeToElement(copy)));
        var restoredUnchanged = redaction.Restore(protectedCopy);
        Assert.Equal("alpha-private-value", restoredUnchanged.GetProperty("variables")[0].GetProperty("defaultValue").GetProperty("items")[0].GetProperty("secret").GetString());
    }

    [Fact]
    public void CanonicalEntitiesAndTheirVariableDeclarationsMayReorderWithoutMovingCredentials()
    {
        var original = Parse("""
            {"flowNodes":[
              {"id":10,"name":"First","message":{"clientSecret":"first-private-value"},"variables":[
                {"id":1,"name":"apiSecret","dataType":"string","defaultValue":"variable-private-value"},
                {"id":2,"name":"normal","dataType":"string","defaultValue":"ordinary"}
              ]},
              {"id":20,"name":"Second","message":{"clientSecret":"second-private-value"}}
            ]}
            """);
        var redaction = new WorkflowAiRedaction();
        var copy = JsonNode.Parse(redaction.Redact(original).GetRawText())!;
        var nodes = copy["flowNodes"]!.AsArray();
        var first = nodes[0]!.DeepClone();
        nodes[0] = nodes[1]!.DeepClone();
        nodes[1] = first;
        var variables = nodes[1]!["variables"]!.AsArray();
        var firstVariable = variables[0]!.DeepClone();
        variables[0] = variables[1]!.DeepClone();
        variables[1] = firstVariable;

        var restored = redaction.Restore(JsonSerializer.SerializeToElement(copy));
        var reordered = restored.GetProperty("flowNodes");
        Assert.Equal(20, reordered[0].GetProperty("id").GetInt32());
        Assert.Equal("second-private-value", reordered[0].GetProperty("message").GetProperty("clientSecret").GetString());
        Assert.Equal("first-private-value", reordered[1].GetProperty("message").GetProperty("clientSecret").GetString());
        Assert.Equal("variable-private-value", reordered[1].GetProperty("variables")[1].GetProperty("defaultValue").GetString());
    }

    private static JsonElement Parse(string value) => JsonSerializer.Deserialize<JsonElement>(value);

    private static AiTurnRequestDto Request() => new()
    {
        ConversationId = Guid.NewGuid().ToString("N"), Message = "Keep the review workflow and make the requested changes.",
        SnapshotId = "snapshot-one", CurrentWorkflow = JsonSerializer.SerializeToElement(Model(), Json)
    };

    private static WorkflowModel Model() => new()
    {
        Id = "protocol-workflow", Name = "Protocol workflow", InitialEventId = 1,
        FlowNodes = [new() { Id = 1, Name = "Start", Type = "startEvent" }, new() { Id = 2, Name = "Review", Type = "userTask" }, new() { Id = 3, Name = "End", Type = "endEvent" }],
        SequenceFlows = [new() { Id = 1, SourceRef = 1, TargetRef = 2, Name = "Begin" }, new() { Id = 2, SourceRef = 2, TargetRef = 3, Name = "Finish" }]
    };

    private static WorkflowAiAuthoringService Service(Provider provider, WorkflowAiOptions? configured = null)
    {
        var options = configured ?? new();
        return new([provider], new Knowledge(),
            new WorkflowDefinitionValidator(new JintScriptEvaluator(new ScriptOptions(), NullLogger<JintScriptEvaluator>.Instance), new ServiceTaskOptions()),
            new WorkflowDefinitionReadinessChecker(), options, new WorkflowAiConcurrencyGate(options));
    }

    private sealed class Provider(params string[] responses) : IAiWorkflowProvider
    {
        public AiProviderDto Descriptor => new("opencode-go", "Test", "kimi-k2.7-code", [new("kimi-k2.7-code", "Test")]);
        public List<AiChatMessageDto[]> Calls { get; } = [];
        public Task<AiCompletion> CompleteAsync(string modelId, string conversationId, IReadOnlyList<AiChatMessageDto> messages,
            string apiKey, int maxOutputTokens, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = Calls.Count;
            Calls.Add(messages.ToArray());
            if (index >= responses.Length) throw new InvalidOperationException("Unexpected additional provider request.");
            return Task.FromResult(new AiCompletion(responses[index], "stop", OutputTokens: 1));
        }
    }

    private sealed class Knowledge : IAuthoringKnowledge
    {
        public string ContractHash => "protocol-test-contract";
        public IReadOnlyDictionary<string, string> Resources { get; } = new Dictionary<string, string>();
        public string GetPromptContext(string? query = null) => "";
        public byte[] GetPackageZip() => [];
    }
}
