using System.Text.Json;
using Flowbit.Infrastructure.Scripting;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Services;
using Flowbit.Shared.Authoring;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flowbit.Tests;

public sealed class AutomaticMultiInstanceDefinitionTests
{
    private static WorkflowDefinitionValidator Validator(bool enabled = true)
    {
        var options = new AutomaticMultiInstanceOptions { AllowInDevelopment = enabled };
        options.ResolveForEnvironment(isDevelopment: true);
        return new(new JintScriptEvaluator(new ScriptOptions(), NullLogger<JintScriptEvaluator>.Instance),
            new ServiceTaskOptions(), automaticMultiInstanceOptions: options);
    }

    private static void Validate(WorkflowModel definition, bool enabled = true)
    {
        var validator = Validator(enabled);
        validator.ValidateAuthored(definition);
        WorkflowModelMigrator.Normalize(definition);
        validator.ValidateNormalized(definition);
    }

    private static WorkflowModel Definition(string kind = "scriptTask") => new()
    {
        Id = "automatic-body", Name = "Automatic body", InitialEventId = 1,
        Variables =
        [
            new() { Id = 1, Name = "items", DataType = "json", IsArray = true, DefaultValue = JsonSerializer.SerializeToElement(new object[] { new { id = 1 }, new { id = 1 } }) },
            new() { Id = 2, Name = "results", DataType = "json", DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>()) }
        ],
        FlowNodes =
        [
            new() { Id = 1, Name = "Start", Type = "startEvent" },
            new()
            {
                Id = 2, Name = "Process", Type = kind, AsyncBefore = kind != "callActivity",
                MultiInstance = new() { CollectionVariable = "items", ResultVariable = "results", CompletionEvaluation = "afterAll", OutputVariables = [] },
                Service = kind == "serviceTask" ? new() { Url = "https://example.test/items", Method = "POST" } : null,
                CallActivity = kind == "callActivity" ? new() { WorkflowKey = "child" } : null
            },
            new() { Id = 3, Name = "End", Type = "endEvent" }
        ],
        SequenceFlows = [new() { Id = 101, SourceRef = 1, TargetRef = 2 }, new() { Id = 102, SourceRef = 2, TargetRef = 3 }]
    };

    [Theory]
    [InlineData("serviceTask")]
    [InlineData("scriptTask")]
    [InlineData("callActivity")]
    public void Development_contract_allows_all_automatic_kinds_but_default_gate_rejects_them(string kind)
    {
        Validate(Definition(kind));
        var error = Assert.Throws<WorkflowDomainException>(() => Validate(Definition(kind), enabled: false));
        Assert.Equal("Flow node #2 automatic multiInstance is not enabled.", error.Message);
    }

    [Fact]
    public void Option_binding_alone_and_every_non_development_host_keep_gate_closed()
    {
        var options = new AutomaticMultiInstanceOptions { AllowInDevelopment = true };
        Assert.False(options.Enabled);
        options.ResolveForEnvironment(true);
        Assert.True(options.Enabled);
        options.ResolveForEnvironment(false);
        Assert.False(options.Enabled);
    }

    [Theory]
    [InlineData("string")]
    [InlineData("number")]
    [InlineData("boolean")]
    [InlineData("date")]
    [InlineData("datetime")]
    [InlineData("json")]
    public void Every_typed_collection_source_is_supported_including_empty_arrays(string type)
    {
        var definition = Definition();
        definition.Variables[0].DataType = type;
        definition.Variables[0].DefaultValue = JsonSerializer.SerializeToElement(Array.Empty<object>());
        Validate(definition);
    }

    [Fact]
    public void Locals_are_not_inferred_as_process_producers_or_downstream_call_sources()
    {
        var definition = Definition("serviceTask");
        var node = definition.FlowNodes[1];
        node.MultiInstance!.OutputVariables = [new() { Name = "localStatus", DataType = "number" }, new() { Name = "localResult", DataType = "string" }];
        node.Service!.StatusVariable = "localStatus";
        node.Service.OutputMappings = [new() { Variable = "localResult", DataType = "string", IsArray = false, Path = "value" }];
        Validate(definition);
        var sources = CallActivityRules.SourceDeclarations(definition);
        Assert.False(sources.ContainsKey("localStatus"));
        Assert.False(sources.ContainsKey("localResult"));
        Assert.True(sources.ContainsKey("results"));
    }

    [Fact]
    public void Locals_are_not_stored_variables_for_conditions_timers_or_inbox_visibility()
    {
        var definition = Definition("serviceTask");
        var node = definition.FlowNodes[1];
        node.MultiInstance!.OutputVariables = [new() { Name = "localStatus", DataType = "number" }, new() { Name = "localResult", DataType = "string" }];
        node.Service!.StatusVariable = "localStatus";
        node.Service.OutputMappings = [new() { Variable = "localResult", DataType = "string", IsArray = false, Path = "value" }];
        definition.Variables.Add(new() { Id = 3, Name = "region", DataType = "string", DefaultValue = JsonSerializer.SerializeToElement("north") });
        definition.FlowNodes.Add(new() { Id = 4, Name = "Review", Type = "userTask", InboxVisibilityCondition = "[region] == 'north'" });
        definition.SequenceFlows[1].TargetRef = 4;
        definition.SequenceFlows.Add(new() { Id = 401, Name = "Done", SourceRef = 4, TargetRef = 3 });
        Validate(definition);

        // Conditional events and expression timers share this stored-variable vocabulary.
        var stored = ConditionalEventDefinitionAnalyzer.DeclaredStoredVariableNames(definition);
        Assert.DoesNotContain("localStatus", stored);
        Assert.DoesNotContain("localResult", stored);
        Assert.Contains("results", stored);

        definition.FlowNodes[3].InboxVisibilityCondition = "[localResult] == 'x'";
        Assert.Contains("localResult", Assert.Throws<WorkflowDomainException>(() => Validate(definition)).Message);
    }

    [Fact]
    public void Ncalc_assignments_target_declared_locals_but_cannot_write_parent_instance_inputs()
    {
        var definition = Definition();
        definition.FlowNodes[1].MultiInstance!.OutputVariables = [new() { Name = "answer", DataType = "number" }];
        definition.FlowNodes[1].Assignments = [new() { Variable = "answer", Expression = "[mi.index] + 1" }];
        Validate(definition);
        definition.FlowNodes[1].Assignments[0].Variable = "items";
        Assert.Contains("overlaps", Assert.Throws<WorkflowDomainException>(() => Validate(definition)).Message);
    }

    [Theory]
    [InlineData("omitted-evaluation", "afterAll")]
    [InlineData("null-evaluation", "afterAll")]
    [InlineData("cardinality", "source")]
    [InlineData("cardinality-expression", "cardinalityExpression")]
    [InlineData("one-per-actor", "onePerActor")]
    [InlineData("no-outputs", "outputVariables")]
    [InlineData("invalid-mode", "mode")]
    [InlineData("null-mode", "mode")]
    [InlineData("no-async", "asyncBefore")]
    [InlineData("scalar-source", "typed-array")]
    [InlineData("local-shadow", "collides")]
    [InlineData("local-duplicate", "duplicated")]
    [InlineData("local-reserved", "reserved")]
    [InlineData("local-unsupported-type", "dataType")]
    [InlineData("local-bad-default", "defaultValue")]
    [InlineData("local-bad-validation", "validation expression")]
    [InlineData("concurrency-zero", "maxConcurrency")]
    [InlineData("concurrency-over-limit", "maxConcurrency")]
    [InlineData("sequential-window", "sequential")]
    [InlineData("human-only-outcome", "unconditional")]
    public void Malformed_automatic_contracts_fail_explicitly(string scenario, string expected)
    {
        var definition = Definition();
        var node = definition.FlowNodes[1];
        var multi = node.MultiInstance!;
        switch (scenario)
        {
            case "omitted-evaluation": multi.CompletionEvaluation = new MultiInstanceModel().CompletionEvaluation; break;
            case "null-evaluation": multi.CompletionEvaluation = null!; break;
            case "cardinality": multi.Source = "cardinality"; break;
            case "cardinality-expression": multi.CardinalityExpression = ""; break;
            case "one-per-actor": multi.OnePerActor = true; break;
            case "no-outputs": multi.OutputVariables = null; break;
            case "invalid-mode": multi.Mode = "many"; break;
            case "null-mode": multi.Mode = null!; break;
            case "no-async": node.AsyncBefore = false; break;
            case "scalar-source": definition.Variables[0].IsArray = false; break;
            case "local-shadow": multi.OutputVariables = [new() { Name = "items" }]; break;
            case "local-duplicate": multi.OutputVariables = [new() { Name = "same", DataType = "string" }, new() { Name = "SAME", DataType = "string" }]; break;
            case "local-reserved": multi.OutputVariables = [new() { Name = "mi.index" }]; break;
            case "local-unsupported-type": multi.OutputVariables = [new() { Name = "local", DataType = "tuple" }]; break;
            case "local-bad-default": multi.OutputVariables = [new() { Name = "local", DataType = "number", DefaultValue = JsonSerializer.SerializeToElement(new { bad = true }) }]; break;
            case "local-bad-validation": multi.OutputVariables = [new() { Name = "local", DataType = "string", Validation = "(" }]; break;
            case "concurrency-zero": multi.MaxConcurrency = 0; break;
            case "concurrency-over-limit": multi.MaxConcurrency = 1001; break;
            case "sequential-window": multi.Mode = "sequential"; multi.MaxConcurrency = 2; break;
            case "human-only-outcome": definition.SequenceFlows[1].IsDefault = true; break;
        }
        Assert.Contains(expected, Assert.Throws<WorkflowDomainException>(() => Validate(definition)).Message);
    }

    [Fact]
    public void Normalization_preserves_invalid_automatic_fields_and_canonicalizes_recognized_casing()
    {
        var definition = Definition();
        var multi = definition.FlowNodes[1].MultiInstance!;
        multi.Mode = "PARALLEL";
        multi.Source = "COLLECTION";
        multi.CompletionEvaluation = "AFTERALL";
        multi.CardinalityExpression = "3";
        multi.OnePerActor = true;
        WorkflowModelMigrator.Normalize(definition);
        Assert.Same(multi, definition.FlowNodes[1].MultiInstance);
        Assert.Equal("parallel", multi.Mode);
        Assert.Equal("collection", multi.Source);
        Assert.Equal("afterAll", multi.CompletionEvaluation);
        Assert.Equal("3", multi.CardinalityExpression);
        Assert.True(multi.OnePerActor);
    }

    [Theory]
    [InlineData("{\"maxConcurrency\":1.5}")]
    [InlineData("{\"maxConcurrency\":\"2\"}")]
    [InlineData("{\"outputVariables\":[{\"name\":\"local\",\"scope\":\"shared\"}]}")]
    [InlineData("{\"outputVariables\":[{\"name\":\"local\",\"required\":true}]}")]
    [InlineData("{\"outputVariables\":[{\"name\":\"local\",\"isArray\":\"false\"}]}")]
    public void Json_binding_rejects_wrong_numeric_types_and_nonlocal_fields(string json) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<MultiInstanceModel>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web)));

    [Fact]
    public void Human_mi_rejects_automatic_settings_and_retains_its_default_evaluation()
    {
        var node = new FlowNodeModel { Id = 7, Type = "userTask", MultiInstance = new() { OutputVariables = [] } };
        Assert.Contains("cannot define automatic", AutomaticMultiInstanceRules.FindAuthoredProblem(node, true));
        node.MultiInstance = new();
        Assert.Null(AutomaticMultiInstanceRules.FindAuthoredProblem(node, true));
        Assert.Equal("afterEach", node.MultiInstance.CompletionEvaluation);
    }

    [Fact]
    public void Shared_sources_and_aggregate_targets_require_the_authored_access_contract()
    {
        var definition = Definition();
        foreach (var variable in definition.Variables)
        {
            variable.Scope = "shared";
            variable.SharedKey = variable.Name;
            variable.Access = variable.Name == "items" ? "read" : "readWrite";
            variable.DefaultValue = null;
        }
        Validate(definition);
        definition.Variables[1].Access = "read";
        Assert.Contains("writable", Assert.Throws<WorkflowDomainException>(() => Validate(definition)).Message);
    }

    [Fact]
    public void Development_authoring_does_not_allow_publication_before_adapter_integration()
    {
        var readiness = new WorkflowDefinitionReadinessChecker(durableProcessingOptions: new() { PublicationEnabled = true });
        readiness.EnsureDurablePublicationAllowed(Definition(), publish: false);
        Assert.Contains("integration release gates", Assert.Throws<WorkflowDomainException>(() =>
            readiness.EnsureDurablePublicationAllowed(Definition(), publish: true)).Message);
    }

    [Theory]
    [InlineData("task")]
    [InlineData("endEvent")]
    [InlineData("errorEndEvent")]
    public void Unsupported_node_configuration_survives_normalization_and_is_rejected(string kind)
    {
        var definition = Definition(kind);
        var original = definition.FlowNodes[1].MultiInstance;
        WorkflowModelMigrator.Normalize(definition);
        Assert.Same(original, definition.FlowNodes[1].MultiInstance);
        Assert.Equal("Flow node #2 cannot define multiInstance.",
            Assert.Throws<WorkflowDomainException>(() => Validator().ValidateNormalized(definition)).Message);
    }

    [Fact]
    public void Generated_authoring_schema_includes_the_small_local_declaration_and_concurrency()
    {
        using var schema = JsonDocument.Parse(WorkflowAuthoringJson.CreateSchema());
        var definitions = schema.RootElement.GetProperty("$defs");
        var multi = definitions.GetProperty(nameof(MultiInstanceModel)).GetProperty("properties");
        Assert.True(multi.TryGetProperty("maxConcurrency", out _));
        Assert.True(multi.TryGetProperty("outputVariables", out _));
        var output = definitions.GetProperty(nameof(MultiInstanceOutputVariableModel));
        Assert.False(output.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(new[] { "name", "dataType", "isArray", "nullable", "defaultValue", "validation" }.Order(),
            output.GetProperty("properties").EnumerateObject().Select(property => property.Name).Order());
    }
}
