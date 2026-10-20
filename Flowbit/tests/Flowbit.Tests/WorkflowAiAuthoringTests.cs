using System.Net;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Flowbit.Infrastructure.Ai;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Ai;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Flowbit.Tests;

/// <summary>The authoring service around the agent: request bounds, settings, catalog context, validation, redaction and the provider client.</summary>
public sealed class WorkflowAiAuthoringTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    [Fact]
    public async Task Turn_ReturnsAValidatedProposalWithoutPersistence()
    {
        var handler = new ScriptedHandler(Completions.Save("workflow.json", Json(Model())), Completions.Final("Review this proposal."));
        var result = await Service(handler).TurnAsync(Request(), CancellationToken.None);
        Assert.Equal(("proposal", "Review this proposal.", "snapshot-1", "test-contract"), (result.Kind, result.Message, result.SnapshotId, result.ContractHash));
        Assert.True(result.Validation.IsValid);
        Assert.True(result.Validation.CanSave);
        Assert.NotNull(result.Definition);
    }

    [Fact]
    public async Task Requests_AreBoundedAndCheckedBeforeAnyProviderCall()
    {
        var handler = new ScriptedHandler();
        async Task<string> Refused(AiTurnRequestDto request, WorkflowAiOptions? options = null, IAiModelStore? models = null) =>
            (await Assert.ThrowsAsync<WorkflowAiException>(() => Service(handler, options, models: models).TurnAsync(request, CancellationToken.None))).Code;

        Assert.Equal("input_too_large", await Refused(Request(), AgentHarness.Options(options => options.MaxInputCharacters = 5)));
        Assert.Equal("invalid_request", await Refused(Request() with { Message = " " }));
        Assert.Equal("invalid_request", await Refused(Request() with { Continue = true }));
        Assert.Equal("invalid_request", await Refused(Request() with { ConversationId = "not-a-guid" }));
        // Only a model configured on the AI models page can be selected.
        Assert.Equal("unsupported_model", await Refused(Request() with { ModelId = "kimi-k2.7-code" }));
        Assert.Equal("unsupported_model", await Refused(Request() with { ModelId = "" }));
        Assert.Equal("unsupported_model", await Refused(Request(), models: new FixedAiModelStore()));
        foreach (var value in new[] { "42", "true", "\"unexpected\"", "[]" })
            Assert.Equal("invalid_current_workflow", await Refused(Request() with { CurrentWorkflow = JsonDocument.Parse(value).RootElement }));
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public void Settings_AllowLongRunsButNotBeyondAnHourAndRejectRemovedEngineSelections()
    {
        new WorkflowAiOptions { RunTimeoutSeconds = 1 }.Validate();
        new WorkflowAiOptions { RunTimeoutSeconds = 3600 }.Validate();
        Assert.Equal("provider_configuration", Assert.Throws<WorkflowAiException>(() => new WorkflowAiOptions { RunTimeoutSeconds = 3601 }.Validate()).Code);

        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WorkflowAi:ExecutionVariant"] = "current", ["WorkflowAi:RequirementsReviewEnabled"] = "false",
            ["WorkflowAi:MaxRepairAttempts"] = "1", ["WorkflowAi:MaxProviderCalls"] = "12"
        }).Build();
        var options = WorkflowAiServiceCollectionExtensions.ReadWorkflowAiOptions(configuration);
        Assert.Equal(12, options.MaxProviderCalls);
        Assert.Equal(["ExecutionVariant"], options.UnsupportedSettings);
        Assert.Equal(["MaxRepairAttempts", "RequirementsReviewEnabled"], options.IgnoredSettings);
        var error = Assert.Throws<WorkflowAiException>(options.Validate);
        Assert.Equal(("provider_configuration", 503), (error.Code, error.StatusCode));
        Assert.Contains("WorkflowAi:ExecutionVariant", error.Message);

        var review = WorkflowAiServiceCollectionExtensions.ReadWorkflowAiOptions(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["WorkflowAi:RequirementsReviewEnabled"] = "true" }).Build());
        Assert.Equal(["RequirementsReviewEnabled"], review.UnsupportedSettings);
    }

    [Fact]
    public void Settings_TheMovedProviderSettingsOnlyWarn()
    {
        var options = WorkflowAiServiceCollectionExtensions.ReadWorkflowAiOptions(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WorkflowAi:OpenCodeBaseUrl"] = "https://opencode.ai/zen/v1/", ["WorkflowAi:OpenCodeModels:0"] = "glm-5.3-flash",
                ["WorkflowAi:OpenCodeReasoningEfforts:glm-5.3-flash"] = "max"
            }).Build());

        Assert.Equal(["OpenCodeBaseUrl", "OpenCodeModels", "OpenCodeReasoningEfforts"], options.ConfiguredRelocatedSettings);
        Assert.Empty(options.UnsupportedSettings);
        Assert.Empty(options.IgnoredSettings);
        options.Validate();
    }

    [Fact]
    public async Task Authoring_RejectsExcessConcurrencyAndReleasesTheSlotAfterAFailure()
    {
        var options = AgentHarness.Options(configure => configure.MaxConcurrentRequests = 1);
        using var gate = new WorkflowAiConcurrencyGate(options);
        var handler = new ScriptedHandler(Completions.Final("x")) { Status = HttpStatusCode.BadGateway };
        var service = Service(handler, options, gate);
        Assert.True(await gate.Semaphore.WaitAsync(0));
        Assert.Equal("authoring_busy", (await Assert.ThrowsAsync<WorkflowAiException>(() => service.TurnAsync(Request(), CancellationToken.None))).Code);
        Assert.Empty(handler.Bodies);
        gate.Semaphore.Release();
        Assert.Equal("paused", (await service.TurnAsync(Request(), CancellationToken.None)).Kind);
        Assert.Equal(1, gate.Semaphore.CurrentCount);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.TurnAsync(Request(), cancelled.Token));
        Assert.Equal(1, gate.Semaphore.CurrentCount);
    }

    [Fact]
    public async Task CatalogContext_GivesOnlySelectedMetadataWithoutCurrentValues()
    {
        var repository = DispatchProxy.Create<ISharedVariableRepository, SelectedCatalogProxy>();
        var proxy = (SelectedCatalogProxy)(object)repository;
        proxy.Records = new Dictionary<string, SharedVariableRecord>
        {
            ["chosen.threshold"] = TestSharedVariableCatalog.Active(1, "chosen.threshold", "number") with
                { HasValue = true, Value = JsonSerializer.SerializeToElement("never-send-current-value"), Description = "Review threshold" },
            ["unselected.key"] = TestSharedVariableCatalog.Active(2, "unselected.key", "string")
        };
        var handler = new ScriptedHandler(Completions.Save("workflow.json", Json(Model())), Completions.Final("Done."));
        var result = await Service(handler, repository: repository).TurnAsync(Request() with { SharedVariableKeys = ["chosen.threshold"] }, CancellationToken.None);

        Assert.Equal("proposal", result.Kind);
        Assert.Equal(["chosen.threshold"], proxy.RequestedKeys);
        var message = LastUserText(handler.Bodies[0]);
        var marker = "\n\n--- Selected shared variables ---\n";
        Assert.StartsWith("Build a review workflow" + marker, message);
        using var metadata = JsonDocument.Parse(message[(message.IndexOf(marker, StringComparison.Ordinal) + marker.Length)..]);
        var item = Assert.Single(metadata.RootElement.EnumerateArray());
        Assert.Equal(("chosen.threshold", "number", "Review threshold"), (item.GetProperty("key").GetString(), item.GetProperty("dataType").GetString(), item.GetProperty("description").GetString()));
        Assert.False(item.TryGetProperty("value", out _));
        Assert.All(handler.Bodies, body => Assert.DoesNotContain("never-send-current-value", body));
        Assert.All(handler.Bodies, body => Assert.DoesNotContain("unselected.key", body));
    }

    [Fact]
    public async Task CatalogContext_RejectsUnavailableSelectionsBeforeCallingTheProvider()
    {
        var repository = DispatchProxy.Create<ISharedVariableRepository, SelectedCatalogProxy>();
        var handler = new ScriptedHandler();
        var error = await Assert.ThrowsAsync<WorkflowAiException>(() => Service(handler, repository: repository)
            .TurnAsync(Request() with { SharedVariableKeys = ["no-longer-active"] }, CancellationToken.None));
        Assert.Equal("catalog_selection_changed", error.Code);
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task SourcePages_AreSentOnceAsLabelledBlocksWithoutCredentials()
    {
        var current = Model();
        current.Variables = [new() { Id = 1, Name = "apiSecret", DataType = "number", DefaultValue = JsonSerializer.SerializeToElement(123456) }];
        var sources = new[] { new AiSourcePageDto("spec.pdf", 1, "Managers approve requests."), new AiSourcePageDto("spec.pdf", 2, "The code is 123456.") };
        var first = new ScriptedHandler(Completions.Final("Read it."));
        var answer = await Service(first).TurnAsync(Request() with
        {
            CurrentWorkflow = JsonSerializer.SerializeToElement(current, JsonOptions), Sources = sources, Message = "Keep the numeric secret. Earlier note: 123456"
        }, CancellationToken.None);
        Assert.Equal("answer", answer.Kind);
        Assert.Equal("Keep the numeric secret. Earlier note: [credential removed]\n\n--- spec.pdf, page 1 ---\nManagers approve requests.\n\n--- spec.pdf, page 2 ---\nThe code is [credential removed].",
            LastUserText(first.Bodies[0]));
        Assert.All(first.Bodies, body => Assert.DoesNotContain("123456", body));

        // A later message repeats no page that was already sent.
        var second = new ScriptedHandler(Completions.Final("Noted."));
        await Service(second).TurnAsync(Request() with
        {
            CurrentWorkflow = JsonSerializer.SerializeToElement(current, JsonOptions),
            Sources = [.. sources, new AiSourcePageDto("spec.pdf", 3, "Escalate after five days.")], Message = "One more page.", State = answer.State
        }, CancellationToken.None);
        Assert.Equal("One more page.\n\n--- spec.pdf, page 3 ---\nEscalate after five days.", LastUserText(second.Bodies[0]));
    }

    [Fact]
    public async Task Validation_SeparatesSaveFromDurablePublicationReadiness()
    {
        var model = Model();
        model.FlowNodes[1].AsyncBefore = true;
        var validation = await Service(new ScriptedHandler()).ValidateAsync(JsonSerializer.SerializeToElement(model, JsonOptions), CancellationToken.None);
        Assert.True(validation.IsValid);
        Assert.True(validation.CanSave);
        Assert.False(validation.CanPublish);
        Assert.Single(validation.PublicationBlockers);
    }

    [Fact]
    public async Task Validation_RejectsUnknownPropertiesAndDuplicateJsonMembers()
    {
        var service = Service(new ScriptedHandler());
        using var unknown = JsonDocument.Parse("{\"id\":\"x\",\"name\":\"x\",\"flowNodes\":[],\"sequenceFlows\":[],\"lanes\":[],\"extra\":true}");
        using var duplicate = JsonDocument.Parse("{\"id\":\"x\",\"id\":\"y\",\"name\":\"x\",\"flowNodes\":[],\"sequenceFlows\":[],\"lanes\":[]}");
        Assert.False((await service.ValidateAsync(unknown.RootElement, CancellationToken.None)).IsValid);
        Assert.False((await service.ValidateAsync(duplicate.RootElement, CancellationToken.None)).IsValid);
    }

    [Fact]
    public async Task Models_ListOnlyTheIdsAndNamesOfTheConfiguredModels()
    {
        var models = new FixedAiModelStore(AgentHarness.Connection,
            new AiModelConnection("second", "Second model", "https://other.example/v1/", "other-model", null, "second-provider-key-0123456789"));

        var listed = await Service(new ScriptedHandler(), models: models).GetModelsAsync(CancellationToken.None);

        Assert.Equal([new AiModelDto("test-model", "GLM-5.3-Flash"), new AiModelDto("second", "Second model")], listed);
        Assert.DoesNotContain(AgentHarness.Key, JsonSerializer.Serialize(listed));
        Assert.Empty(await Service(new ScriptedHandler(), AgentHarness.Options(options => options.Enabled = false), models: models)
            .GetModelsAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("http://opencode.example/zen/v1/")]
    [InlineData("https://user:secret@opencode.example/zen/v1/")]
    [InlineData("https://opencode.example/zen/v1/?debug=1")]
    [InlineData("not a url")]
    public async Task ProviderClient_RejectsUnsafeEndpointsBeforeTransport(string baseUrl)
    {
        var handler = new ScriptedHandler();
        var error = await Assert.ThrowsAsync<WorkflowAiException>(() => Service(handler, models: new FixedAiModelStore(AgentHarness.Connection with { Endpoint = baseUrl }))
            .TurnAsync(Request(), CancellationToken.None));
        Assert.Equal(("provider_configuration", 503), (error.Code, error.StatusCode));
        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task ProviderClient_UsesEachModelsEndpointKeyNameAndReasoning()
    {
        var models = new FixedAiModelStore(
            AgentHarness.Connection with { Endpoint = "http://127.0.0.1:5999/zen/v1", ApiKey = "first-provider-key-0123456789" },
            new AiModelConnection("other", "Other model", "https://other.example/v1/", "other-model", null, "second-provider-key-0123456789"));
        var first = new ScriptedHandler(Completions.Final("a"), Completions.Final("b"), Completions.Final("c"));
        await Service(first, models: models).TurnAsync(Request(), CancellationToken.None);
        var second = new ScriptedHandler(Completions.Final("a"), Completions.Final("b"), Completions.Final("c"));
        await Service(second, models: models).TurnAsync(Request() with { ModelId = "other" }, CancellationToken.None);

        Assert.Equal("http://127.0.0.1:5999/zen/v1/chat/completions", first.Requests[0].RequestUri!.AbsoluteUri);
        Assert.Equal("https://other.example/v1/chat/completions", second.Requests[0].RequestUri!.AbsoluteUri);
        Assert.Equal("Bearer first-provider-key-0123456789", first.Requests[0].Headers.Authorization!.ToString());
        Assert.Equal("Bearer second-provider-key-0123456789", second.Requests[0].Headers.Authorization!.ToString());
        using var withEffort = JsonDocument.Parse(first.Bodies[0]);
        using var withoutEffort = JsonDocument.Parse(second.Bodies[0]);
        Assert.Equal(("glm-5.3-flash", "high"), (withEffort.RootElement.GetProperty("model").GetString(),
            withEffort.RootElement.GetProperty("reasoning_effort").GetString()));
        Assert.Equal("other-model", withoutEffort.RootElement.GetProperty("model").GetString());
        Assert.False(withoutEffort.RootElement.TryGetProperty("reasoning_effort", out _));
    }

    [Fact]
    public async Task ProviderErrors_NeverExposeTheResponseBody()
    {
        var handler = new ScriptedHandler("{\"error\":{\"message\":\"internal detail sk-leak-me\"}}") { Status = HttpStatusCode.InternalServerError };
        var result = await Service(handler).TurnAsync(Request(), CancellationToken.None);
        Assert.Equal(("paused", "provider_unavailable"), (result.Kind, result.Code));
        Assert.DoesNotContain("sk-leak-me", JsonSerializer.Serialize(result));
    }

    [Fact]
    public void Redaction_PreservesTrustedReferencesAndRestoresOnlyOriginalLocations()
    {
        using var document = JsonDocument.Parse("{\"flowNodes\":[{\"id\":7,\"message\":{\"clientSecret\":\"literal-secret\"}},{\"id\":8,\"message\":{\"clientSecret\":\"${config.apiSecret}\"}}],\"taskDistribution\":{\"clientSecret\":\"distribution-secret\"}}");
        var redactor = new WorkflowAiRedaction();
        var redacted = redactor.Redact(document.RootElement);
        Assert.DoesNotContain("literal-secret", redacted.GetRawText());
        Assert.DoesNotContain("distribution-secret", redacted.GetRawText());
        Assert.Contains("${config.apiSecret}", redacted.GetRawText());
        Assert.Equal("literal-secret", redactor.Restore(redacted).GetProperty("flowNodes")[0].GetProperty("message").GetProperty("clientSecret").GetString());
        var moved = redacted.GetRawText().Replace("\"id\":7", "\"id\":99", StringComparison.Ordinal);
        using var changed = JsonDocument.Parse(moved);
        Assert.Equal("/flowNodes/0/message/clientSecret", Assert.Throws<WorkflowAiPlaceholderException>(() => redactor.Restore(changed.RootElement)).Pointer);
    }

    [Theory]
    [InlineData("{\"roles\":[\"${config.FLOWBIT_REDACTED_unknown}\"]}")]
    [InlineData("{\"clientSecret\":\"prefix ${config.FLOWBIT_REDACTED_unknown}\"}")]
    [InlineData("{\"clientSecret\":\"${config.FLOWBIT_REDACTED_unknown}\"}")]
    public void Redaction_RejectsUnknownEmbeddedAndMovedArrayPlaceholders(string json)
    {
        var redactor = new WorkflowAiRedaction();
        using var document = JsonDocument.Parse(json);
        Assert.Throws<WorkflowAiPlaceholderException>(() => redactor.Restore(document.RootElement));
    }

    [Fact]
    public void Redaction_RedactsNestedSecretDefaultsAndHeaderValues()
    {
        using var document = JsonDocument.Parse("{\"variables\":[{\"id\":1,\"name\":\"apiSecret\",\"defaultValue\":{\"nested\":[\"a-secret\"]}}],\"service\":{\"headers\":[{\"name\":\"X-Custom\",\"value\":\"custom-token\"}]}}");
        var redactor = new WorkflowAiRedaction();
        var result = redactor.Redact(document.RootElement);
        Assert.DoesNotContain("a-secret", result.GetRawText());
        Assert.DoesNotContain("custom-token", result.GetRawText());
        Assert.Equal("[credential removed]", redactor.Sanitize("a-secret"));
        Assert.Equal(document.RootElement.GetRawText(), redactor.Restore(result).GetRawText());
    }

    [Fact]
    public void Redaction_SanitizingContextDoesNotCorruptProtectedPlaceholders()
    {
        using var document = JsonDocument.Parse("{\"clientSecret\":\"config\"}");
        var redactor = new WorkflowAiRedaction();
        var redacted = redactor.Redact(document.RootElement);
        var sanitized = redactor.Sanitize(redacted.GetRawText());
        Assert.Equal(redacted.GetRawText(), sanitized);
        using var reparsed = JsonDocument.Parse(sanitized);
        Assert.Equal("config", redactor.Restore(reparsed.RootElement).GetProperty("clientSecret").GetString());
        Assert.Equal("[credential removed]", redactor.Sanitize("config"));
    }

    [Theory]
    [InlineData("123456")]
    [InlineData("true")]
    [InlineData("false")]
    public void Redaction_ProtectsScalarSecretDefaultsAndRestoresTheirJsonTypes(string literal)
    {
        using var document = JsonDocument.Parse("{\"variables\":[{\"id\":1,\"name\":\"apiSecret\",\"defaultValue\":" + literal + "}]}");
        var redactor = new WorkflowAiRedaction();
        var redacted = redactor.Redact(document.RootElement);
        var placeholder = redacted.GetProperty("variables")[0].GetProperty("defaultValue");
        Assert.Equal(JsonValueKind.String, placeholder.ValueKind);
        Assert.Contains("FLOWBIT_REDACTED_", placeholder.GetString());
        Assert.Equal("[credential removed]", redactor.Sanitize(literal));
        Assert.Equal(document.RootElement.GetRawText(), redactor.Restore(redacted).GetRawText());
    }

    [Fact]
    public void Redaction_ProtectsNonStringCredentialFieldsWithinBusinessJsonDefaults()
    {
        using var document = JsonDocument.Parse("{\"variables\":[{\"id\":1,\"name\":\"integration\",\"defaultValue\":{\"password\":123456,\"apiKey\":[\"first-secret\",\"second-secret\"]}}]}");
        var redactor = new WorkflowAiRedaction();
        var redacted = redactor.Redact(document.RootElement);
        var defaults = redacted.GetProperty("variables")[0].GetProperty("defaultValue");
        Assert.Equal(JsonValueKind.String, defaults.GetProperty("password").ValueKind);
        Assert.Equal(JsonValueKind.String, defaults.GetProperty("apiKey").ValueKind);
        Assert.DoesNotContain("first-secret", redacted.GetRawText());
        Assert.DoesNotContain("second-secret", redacted.GetRawText());
        Assert.Equal(document.RootElement.GetRawText(), redactor.Restore(redacted).GetRawText());
    }

    [Fact]
    public void Redaction_EntitiesAndTheirVariableDeclarationsMayReorderWithoutMovingCredentials()
    {
        using var document = JsonDocument.Parse("{\"flowNodes\":[{\"id\":1,\"variables\":[{\"id\":1,\"name\":\"apiSecret\",\"defaultValue\":\"one\"},{\"id\":2,\"name\":\"note\"}]},{\"id\":2}]}");
        var redactor = new WorkflowAiRedaction();
        var redacted = JsonSerializer.SerializeToNode(redactor.Redact(document.RootElement))!;
        var nodes = redacted["flowNodes"]!.AsArray();
        var first = nodes[0]!.DeepClone();
        nodes[0] = nodes[1]!.DeepClone();
        nodes[1] = first;
        var variables = nodes[1]!["variables"]!.AsArray();
        var secret = variables[0]!.DeepClone();
        variables[0] = variables[1]!.DeepClone();
        variables[1] = secret;
        Assert.Equal("one", redactor.Restore(JsonSerializer.SerializeToElement(redacted)).GetProperty("flowNodes")[1].GetProperty("variables")[1].GetProperty("defaultValue").GetString());
    }

    private static AiTurnRequestDto Request() => new()
    {
        ConversationId = ConversationId, Message = "Build a review workflow", SnapshotId = "snapshot-1", ModelId = AgentHarness.Connection.Id
    };
    private static readonly string ConversationId = Guid.NewGuid().ToString();
    private static string Json(WorkflowModel model) => JsonSerializer.Serialize(model, JsonOptions);
    private static WorkflowModel Model() => new()
    {
        Id = "ai-test", Name = "AI test", InitialEventId = 1,
        FlowNodes = [new() { Id = 1, Name = "Start", Type = "startEvent" }, new() { Id = 2, Name = "Review", Type = "userTask" }, new() { Id = 3, Name = "End", Type = "endEvent" }],
        SequenceFlows = [new() { Id = 1, SourceRef = 1, TargetRef = 2, Name = "Begin" }, new() { Id = 2, SourceRef = 2, TargetRef = 3, Name = "Finish" }]
    };

    private static WorkflowAiAuthoringService Service(HttpMessageHandler handler, WorkflowAiOptions? configured = null,
        WorkflowAiConcurrencyGate? gate = null, ISharedVariableRepository? repository = null, IAiModelStore? models = null)
    {
        var options = configured ?? AgentHarness.Options();
        return new(new OpenAiCompatibleChatClientFactory(() => handler), models ?? new FixedAiModelStore(AgentHarness.Connection),
            ResourceKnowledge.Small, new WorkflowAiDocs(ResourceKnowledge.Small),
            AgentHarness.Validator(), new WorkflowDefinitionReadinessChecker(), options, gate ?? new WorkflowAiConcurrencyGate(options), repository);
    }

    private static string LastUserText(string body)
    {
        using var document = JsonDocument.Parse(body);
        var content = document.RootElement.GetProperty("messages").EnumerateArray().Last(item => item.GetProperty("role").GetString() == "user").GetProperty("content");
        return content.ValueKind == JsonValueKind.String ? content.GetString()! : string.Concat(content.EnumerateArray().Select(part => part.GetProperty("text").GetString()));
    }

    public class SelectedCatalogProxy : DispatchProxy
    {
        public IReadOnlyDictionary<string, SharedVariableRecord> Records { get; set; } = new Dictionary<string, SharedVariableRecord>();
        public IReadOnlyCollection<string> RequestedKeys { get; private set; } = [];
        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            if (targetMethod?.Name != nameof(ISharedVariableRepository.GetManyByKeyAsync))
                throw new InvalidOperationException("Authoring attempted an unexpected catalog operation: " + targetMethod?.Name);
            RequestedKeys = (IReadOnlyCollection<string>)args![0]!;
            return Task.FromResult(Records);
        }
    }
}
