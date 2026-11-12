using System.Text.Json;
using Flowbit.Infrastructure.Scripting;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// TASK-05C script adapter contract without a database: the item evaluator, its
/// target resolution, the release boundary by activity kind, and the access plan
/// for JavaScript's conservative shared scope.
/// </summary>
public sealed class AutomaticMultiInstanceScriptAdapterTests
{
    private static readonly IScriptEvaluator Jint =
        new JintScriptEvaluator(new ScriptOptions(), NullLogger<JintScriptEvaluator>.Instance);

    private static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);

    private static WorkflowModel Definition(FlowNodeModel body, params VariableModel[] variables)
    {
        var all = new List<VariableModel>
        {
            new() { Id = 1, Name = "items", DataType = "json", IsArray = true, DefaultValue = Json(Array.Empty<object>()) },
            new() { Id = 2, Name = "results", DataType = "json", DefaultValue = Json(Array.Empty<object>()) }
        };
        all.AddRange(variables);
        return new WorkflowModel
        {
            Id = "script-body", Name = "Script body", InitialEventId = 1, Variables = all,
            FlowNodes =
            [
                new() { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                body,
                new() { Id = 3, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows = [new() { Id = 101, SourceRef = 1, TargetRef = 2 }, new() { Id = 102, SourceRef = 2, TargetRef = 3 }]
        };
    }

    private static FlowNodeModel NCalcBody(IEnumerable<MultiInstanceOutputVariableModel> outputs,
        params (string Variable, string Expression)[] assignments) => new()
    {
        Id = 2, Name = "Body", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true, ScriptFormat = ScriptFormats.NCalc,
        MultiInstance = new()
        {
            Mode = "sequential", Source = "collection", CollectionVariable = "items",
            CompletionEvaluation = "afterAll", OutputVariables = outputs.ToList(), ResultVariable = "results"
        },
        Assignments = assignments.Select(pair => new AssignmentModel { Variable = pair.Variable, Expression = pair.Expression }).ToList()
    };

    private static FlowNodeModel JavaScriptBody(IEnumerable<MultiInstanceOutputVariableModel> outputs, string script) => new()
    {
        Id = 2, Name = "Body", Type = BpmnFlowNodeTypes.ScriptTask, AsyncBefore = true, ScriptFormat = ScriptFormats.JavaScript,
        MultiInstance = new()
        {
            Mode = "parallel", Source = "collection", CollectionVariable = "items",
            CompletionEvaluation = "afterAll", MaxConcurrency = 4, OutputVariables = outputs.ToList(), ResultVariable = "results"
        },
        Script = script
    };

    private static MultiInstanceOutputVariableModel Local(string name, string type = "number", bool nullable = false,
        bool isArray = false, string? validation = null, JsonElement? defaultValue = null) => new()
    {
        Name = name, DataType = type, Nullable = nullable, IsArray = isArray, Validation = validation, DefaultValue = defaultValue
    };

    // Builds an item context as the coordinator does: frozen inputs and collection,
    // staged shared/context values, mi.*, then fresh local defaults.
    private static AutomaticMultiInstanceItemContext Context(FlowNodeModel node, object[] collection, int index,
        Dictionary<string, object?>? instanceInputs = null, Dictionary<string, object?>? staged = null,
        Dictionary<string, long>? versions = null)
    {
        var snapshot = Json(new
        {
            collection,
            instanceInputs = instanceInputs ?? new Dictionary<string, object?>()
        });
        var values = AutomaticMultiInstanceValues.CreateItemContext(snapshot, index,
            (staged ?? new Dictionary<string, object?>()).ToDictionary(pair => pair.Key, pair => Json(pair.Value),
                StringComparer.OrdinalIgnoreCase),
            node.MultiInstance!.CollectionVariable!);
        values["sys.jobId"] = Json(77);
        values["sys.jobAttempt"] = Json(1);
        var locals = WorkflowEngineService.CreateAutomaticLocalBuffer(node.MultiInstance.OutputVariables!, values);
        foreach (var local in locals) values[local.Key] = local.Value;
        return new AutomaticMultiInstanceItemContext(values, locals,
            versions ?? new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase), DateTimeOffset.UtcNow);
    }

    private static AutomaticMultiInstanceCandidate Evaluate(FlowNodeModel node, WorkflowModel definition,
        AutomaticMultiInstanceItemContext context) =>
        WorkflowEngineService.EvaluateAutomaticScriptItem(Jint, node, definition, context, CancellationToken.None);

    private static WorkflowEngineService.AutomaticScriptItemException Fails(FlowNodeModel node, WorkflowModel definition,
        AutomaticMultiInstanceItemContext context) =>
        Assert.Throws<WorkflowEngineService.AutomaticScriptItemException>(() => Evaluate(node, definition, context));

    [Fact]
    public void NCalc_example_reads_the_item_and_an_earlier_write_but_never_another_item()
    {
        var node = NCalcBody([Local("scaledAmount"), Local("adjustedAmount")],
            ("scaledAmount", "[mi.item] * multiplier"), ("adjustedAmount", "scaledAmount + [mi.index]"));
        var definition = Definition(node, new VariableModel { Id = 3, Name = "multiplier", DataType = "number", DefaultValue = Json(2) });
        var inputs = new Dictionary<string, object?> { ["multiplier"] = 2 };

        var first = Evaluate(node, definition, Context(node, [10, 20], 0, inputs));
        var second = Evaluate(node, definition, Context(node, [10, 20], 1, inputs));

        Assert.Equal(20, first.LocalVariables["scaledAmount"].GetDouble());
        Assert.Equal(20, first.LocalVariables["adjustedAmount"].GetDouble());
        Assert.Equal(40, second.LocalVariables["scaledAmount"].GetDouble());
        Assert.Equal(41, second.LocalVariables["adjustedAmount"].GetDouble());
        Assert.Empty(first.SharedWrites);
        Assert.Empty(first.SharedOutputValueVersions);
    }

    [Fact]
    public void JavaScript_example_builds_object_locals_and_cannot_mutate_its_input_through_a_returned_copy()
    {
        var node = JavaScriptBody([Local("lineAmount"), Local("lineSummary", "json")],
            "const item = execution.getVariable('mi.item');\n" +
            "const index = execution.getVariable('mi.index');\n" +
            "const multiplier = execution.getVariable('multiplier');\n" +
            "item.sku = 'changed';\n" +
            "execution.setVariable('lineAmount', item.quantity * item.unitPrice * multiplier);\n" +
            "execution.setVariable('lineSummary', { index: index, sku: execution.getVariable('mi.item').sku, amount: execution.getVariable('lineAmount') });");
        var definition = Definition(node, new VariableModel { Id = 3, Name = "multiplier", DataType = "number", DefaultValue = Json(3) });

        var candidate = Evaluate(node, definition, Context(node, [new { sku = "A", quantity = 2, unitPrice = 5 }], 0,
            new Dictionary<string, object?> { ["multiplier"] = 3 }));

        Assert.Equal(30, candidate.LocalVariables["lineAmount"].GetDouble());
        var summary = candidate.LocalVariables["lineSummary"];
        Assert.Equal(0, summary.GetProperty("index").GetInt32());
        Assert.Equal("A", summary.GetProperty("sku").GetString());
        Assert.Equal(30, summary.GetProperty("amount").GetDouble());
    }

    // NCalc keeps an ordinary script's conversion of each JSON kind: a json object
    // item arrives as its JSON text and a json array as a list.
    [Theory]
    [InlineData("string", "\"Ada\"", "string", "Ada")]
    [InlineData("number", "4.5", "number", "4.5")]
    [InlineData("boolean", "true", "boolean", "true")]
    [InlineData("date", "\"2026-11-12\"", "date", "2026-11-12")]
    [InlineData("datetime", "\"2026-11-12T10:00:00Z\"", "datetime", "2026-11-12T10:00:00Z")]
    [InlineData("json", "{\"sku\":\"A\"}", "string", "{\"sku\":\"A\"}")]
    public void NCalc_item_reads_each_element_type_as_an_ordinary_script_reads_that_type(
        string itemType, string itemJson, string localType, string expected)
    {
        var node = NCalcBody([Local("copy", localType)], ("copy", "[mi.item]"));
        var definition = Definition(node);
        definition.Variables[0].DataType = itemType;
        var item = JsonDocument.Parse(itemJson).RootElement.Clone();

        var candidate = Evaluate(node, definition, Context(node, [item], 0));

        var value = candidate.LocalVariables["copy"];
        Assert.Equal(expected, value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText());
    }

    [Fact]
    public void NCalc_json_array_item_arrives_as_a_list()
    {
        var node = NCalcBody([Local("hasTwo", "boolean")], ("hasTwo", "2 in [mi.item]"));
        var candidate = Evaluate(node, Definition(node), Context(node, [new[] { 1, 2, 3 }], 0));
        Assert.True(candidate.LocalVariables["hasTwo"].GetBoolean());
    }

    [Fact]
    public void Item_writes_resolve_only_to_locals_and_writable_shared_aliases()
    {
        var shared = new VariableModel { Id = 4, Name = "counter", DataType = "number", Scope = "shared", SharedKey = "tests.counter", Access = "readWrite" };
        var readOnly = new VariableModel { Id = 5, Name = "rate", DataType = "number", Scope = "shared", SharedKey = "tests.rate", Access = "read" };
        var instance = new VariableModel { Id = 6, Name = "total", DataType = "number", DefaultValue = Json(0) };
        var errorTarget = new VariableModel { Id = 7, Name = "failure", DataType = "string", Nullable = true };
        string Message(string target)
        {
            var node = JavaScriptBody([Local("value")], $"execution.setVariable('{target}', 1);");
            var definition = Definition(node, shared, readOnly, instance, errorTarget);
            definition.FlowNodes.Add(new() { Id = 4, Name = "Caught", Type = BpmnFlowNodeTypes.ErrorBoundaryEvent, AttachedToRef = 2, ErrorVariable = "failure" });
            var failure = Fails(node, definition, Context(node, [1], 0,
                staged: new Dictionary<string, object?> { ["counter"] = 0, ["rate"] = 1 },
                versions: new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["counter"] = 3 }));
            Assert.Equal(WorkflowEngineService.AutomaticScriptFailureCode, failure.Code);
            return failure.Message;
        }

        Assert.Contains("cannot write instance variable 'total'", Message("total"));
        Assert.Contains("cannot write read-only shared variable 'rate'", Message("rate"));
        Assert.Contains("cannot write context value 'mi.index'", Message("mi.index"));
        Assert.Contains("'results'; it is this body's collection, aggregate, or boundary error target", Message("results"));
        Assert.Contains("'items'; it is this body's collection, aggregate, or boundary error target", Message("items"));
        Assert.Contains("'failure'; it is this body's collection, aggregate, or boundary error target", Message("failure"));
        Assert.Contains("writes 'undeclared', which is not a declared local output or readWrite shared alias", Message("undeclared"));
    }

    [Fact]
    public void A_computed_shared_target_is_checked_at_runtime_and_its_fence_is_narrowed_to_the_written_alias()
    {
        var counter = new VariableModel { Id = 4, Name = "counter", DataType = "number", Scope = "shared", SharedKey = "tests.counter", Access = "readWrite" };
        var other = new VariableModel { Id = 5, Name = "other", DataType = "number", Scope = "shared", SharedKey = "tests.other", Access = "readWrite" };
        var node = JavaScriptBody([], "const name = 'coun' + 'ter';\nexecution.setVariable(name, execution.getVariable('counter') + 1);\n" +
            "if (execution.getVariable('counter') !== 6) throw new Error('read-after-write');");
        var definition = Definition(node, counter, other);

        var candidate = Evaluate(node, definition, Context(node, [1], 0,
            staged: new Dictionary<string, object?> { ["counter"] = 5, ["other"] = 1 },
            versions: new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["counter"] = 9, ["other"] = 4 }));

        Assert.Equal(6, candidate.SharedWrites["counter"].GetDouble());
        Assert.Equal(new Dictionary<string, long> { ["counter"] = 9 }, candidate.SharedOutputValueVersions);
        Assert.Empty(candidate.LocalVariables);
    }

    [Fact]
    public void One_shared_key_written_through_two_aliases_fails_at_the_second_write()
    {
        var first = new VariableModel { Id = 4, Name = "first", DataType = "number", Scope = "shared", SharedKey = "tests.same", Access = "readWrite" };
        var second = new VariableModel { Id = 5, Name = "second", DataType = "number", Scope = "shared", SharedKey = "tests.same", Access = "readWrite" };
        var node = JavaScriptBody([], "execution.setVariable('first', 1);\nexecution.setVariable('second', 2);");

        var failure = Fails(node, Definition(node, first, second), Context(node, [1], 0,
            versions: new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["first"] = 1, ["second"] = 1 }));

        Assert.Contains("writes shared key 'tests.same' through both 'first' and 'second'", failure.Message);
    }

    [Fact]
    public void Output_contract_failures_use_the_result_invalid_code()
    {
        var node = NCalcBody([Local("required"), Local("checked", validation: "checked > 10")], ("checked", "5"));
        var missing = Fails(node, Definition(node), Context(node, [1], 0));
        Assert.Equal(AutomaticMultiInstanceValues.ResultInvalid, missing.Code);

        node = NCalcBody([Local("checked", validation: "checked > 10")], ("checked", "5"));
        var invalid = Fails(node, Definition(node), Context(node, [1], 0));
        Assert.Equal(AutomaticMultiInstanceValues.ResultInvalid, invalid.Code);

        var shared = new VariableModel { Id = 4, Name = "limited", DataType = "number", Scope = "shared", SharedKey = "tests.limited", Access = "readWrite", Validation = "value < 3" };
        node = NCalcBody([], ("limited", "5"));
        var sharedFailure = Fails(node, Definition(node, shared), Context(node, [1], 0,
            versions: new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase) { ["limited"] = 1 }));
        Assert.Equal(AutomaticMultiInstanceValues.ResultInvalid, sharedFailure.Code);
    }

    [Fact]
    public void Nullable_unset_locals_and_defaults_satisfy_their_contract_while_script_errors_use_the_script_code()
    {
        var node = NCalcBody([Local("note", "string", nullable: true), Local("fallback", defaultValue: Json(7))]);
        var candidate = Evaluate(node, Definition(node), Context(node, [1], 0));
        Assert.Equal(7, candidate.LocalVariables["fallback"].GetDouble());
        Assert.True(!candidate.LocalVariables.TryGetValue("note", out var note) || note.ValueKind == JsonValueKind.Null);

        node = JavaScriptBody([Local("value")], "throw new Error('item refused');");
        var failure = Fails(node, Definition(node), Context(node, [1], 0));
        Assert.Equal(WorkflowEngineService.AutomaticScriptFailureCode, failure.Code);
        Assert.Contains("item refused", failure.Message);
    }

    [Theory]
    [InlineData("[mi.item] % 0")]
    [InlineData("[mi.item] / 0")]
    [InlineData("0.0 / 0")]
    public void Division_by_zero_is_the_items_script_failure_not_an_unexpected_exception(string expression)
    {
        var node = NCalcBody([Local("value", nullable: true)], ("value", expression));

        var failure = Fails(node, Definition(node), Context(node, [1], 0));

        Assert.Equal(WorkflowEngineService.AutomaticScriptFailureCode, failure.Code);
    }

    [Fact]
    public void A_JavaScript_item_writing_infinity_fails_as_a_script_error()
    {
        var node = JavaScriptBody([Local("value")], "execution.setVariable('value', 1 / 0);");

        var failure = Fails(node, Definition(node), Context(node, [1], 0));

        Assert.Equal(WorkflowEngineService.AutomaticScriptFailureCode, failure.Code);
        Assert.Contains("Infinity", failure.Message);
    }

    [Fact]
    public void Reads_cover_frozen_inputs_context_and_own_writes_but_not_the_aggregate_or_an_unset_local()
    {
        var node = JavaScriptBody([Local("seen", "json"), Local("later")],
            "const before = execution.hasVariable('later');\n" +
            "const all = execution.getVariables();\n" +
            "execution.setVariable('later', 1);\n" +
            "execution.setVariable('seen', { before: before, hasResults: execution.hasVariable('results'), " +
            "items: execution.getVariable('items').length, item: all['mi.item'], total: all['mi.total'], " +
            "frozen: all['frozenInput'], job: all['sys.jobId'], after: execution.getVariables()['later'] });");
        var candidate = Evaluate(node, Definition(node), Context(node, ["a", "b"], 1,
            new Dictionary<string, object?> { ["frozenInput"] = "kept", ["results"] = "stale" }));

        var seen = candidate.LocalVariables["seen"];
        Assert.False(seen.GetProperty("before").GetBoolean());
        Assert.False(seen.GetProperty("hasResults").GetBoolean());
        Assert.Equal(2, seen.GetProperty("items").GetInt32());
        Assert.Equal("b", seen.GetProperty("item").GetString());
        Assert.Equal(2, seen.GetProperty("total").GetInt32());
        Assert.Equal("kept", seen.GetProperty("frozen").GetString());
        Assert.Equal(77, seen.GetProperty("job").GetInt32());
        Assert.Equal(1, seen.GetProperty("after").GetInt32());

        // An NCalc reference to a local without a default or write fails like a missing variable.
        var ncalc = NCalcBody([Local("first"), Local("second")], ("second", "first + 1"), ("first", "1"));
        Assert.Equal(WorkflowEngineService.AutomaticScriptFailureCode, Fails(ncalc, Definition(ncalc), Context(ncalc, [1], 0)).Code);
    }

    [Fact]
    public void Local_validation_context_holds_only_the_names_validations_reference()
    {
        var node = NCalcBody([Local("value", validation: "value <= limit and [mi.index] >= 0")], ("value", "[mi.item]"));
        var definition = Definition(node, new VariableModel { Id = 3, Name = "limit", DataType = "number", DefaultValue = Json(10) });

        var candidate = Evaluate(node, definition, Context(node, [3], 0,
            new Dictionary<string, object?> { ["limit"] = 10, ["unrelated"] = "large" },
            new Dictionary<string, object?> { ["config.apiKey"] = "secret" }));

        Assert.Equal(new[] { "limit", "mi.index" }.Order(StringComparer.OrdinalIgnoreCase),
            candidate.ValidationContext.Keys.Order(StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void Instance_inputs_are_referenced_names_for_NCalc_and_every_stored_name_for_JavaScript_but_never_the_aggregate_or_collection()
    {
        var ncalc = NCalcBody([Local("value", validation: "value < cap", defaultValue: Json("${prefix}"))],
            ("value", "[mi.item] * multiplier + [sys.jobAttempt] + Count(Items)"));
        Assert.Equal(new[] { "cap", "multiplier", "prefix" },
            WorkflowEngineService.AutomaticScriptInstanceInputNames(ncalc, Definition(ncalc), []).Order());

        // The snapshot holds the collection once; the item context reads it from there.
        var script = JavaScriptBody([], "execution.setVariable('x', 1);");
        Assert.Equal(new[] { "other" },
            WorkflowEngineService.AutomaticScriptInstanceInputNames(script, Definition(script), ["items", "results", "other"]).Order());
    }

    [Fact]
    public void A_script_bodys_locals_are_not_process_variables_a_downstream_call_can_read()
    {
        var node = NCalcBody([Local("scaledAmount")], ("scaledAmount", "[mi.item]"));
        var definition = Definition(node);

        var sources = CallActivityRules.SourceDeclarations(definition);

        Assert.DoesNotContain("scaledAmount", sources.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("items", sources.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void Publication_opens_only_installed_kinds_on_a_development_host_with_the_switch()
    {
        var open = new AutomaticMultiInstanceOptions { AllowInDevelopment = true };
        open.ResolveForEnvironment(isDevelopment: true);
        var production = new AutomaticMultiInstanceOptions { AllowInDevelopment = true };
        production.ResolveForEnvironment(isDevelopment: false);
        WorkflowDefinitionReadinessChecker Readiness(AutomaticMultiInstanceOptions options) =>
            new(durableProcessingOptions: new() { PublicationEnabled = true }, automaticMultiInstanceOptions: options);
        WorkflowModel Body(string type) => Definition(new FlowNodeModel
        {
            Id = 2, Name = "Body", Type = type, AsyncBefore = type != BpmnFlowNodeTypes.CallActivity,
            MultiInstance = new() { CollectionVariable = "items", ResultVariable = "results", CompletionEvaluation = "afterAll", OutputVariables = [] }
        });

        Readiness(open).EnsureDurablePublicationAllowed(Body(BpmnFlowNodeTypes.ScriptTask), publish: true);
        Assert.Equal("Automatic multi-instance serviceTask #2 cannot be published or run until its activity adapter is installed.",
            Assert.Throws<WorkflowDomainException>(() =>
                Readiness(open).EnsureDurablePublicationAllowed(Body(BpmnFlowNodeTypes.ServiceTask), publish: true)).Message);
        Assert.Equal("Automatic multi-instance callActivity #2 cannot be published or run until its activity adapter is installed.",
            Assert.Throws<WorkflowDomainException>(() =>
                Readiness(open).EnsureDurablePublicationAllowed(Body(BpmnFlowNodeTypes.CallActivity), publish: true)).Message);
        Assert.Contains("integration release gates", Assert.Throws<WorkflowDomainException>(() =>
            Readiness(production).EnsureDurablePublicationAllowed(Body(BpmnFlowNodeTypes.ScriptTask), publish: true)).Message);
        Readiness(production).EnsureDurablePublicationAllowed(Body(BpmnFlowNodeTypes.ScriptTask), publish: false);
    }

    [Fact]
    public void Runtime_entry_proceeds_only_for_an_installed_kind_on_a_released_host()
    {
        FlowNodeModel Body(string type) => new()
        {
            Id = 2, Type = type, MultiInstance = new() { CollectionVariable = "items", ResultVariable = "results" }
        };

        WorkflowEngineService.EnsureAutomaticMultiInstanceRuntimeAvailable(Body(BpmnFlowNodeTypes.ScriptTask), released: true);
        Assert.Throws<WorkflowDomainException>(() =>
            WorkflowEngineService.EnsureAutomaticMultiInstanceRuntimeAvailable(Body(BpmnFlowNodeTypes.ServiceTask), released: true));
        Assert.Throws<WorkflowDomainException>(() =>
            WorkflowEngineService.EnsureAutomaticMultiInstanceRuntimeAvailable(Body(BpmnFlowNodeTypes.CallActivity), released: true));
        Assert.Equal(new[] { BpmnFlowNodeTypes.ScriptTask }, AutomaticMultiInstanceRules.InstalledAdapterKinds);

        // A host outside Development, or with the switch closed, refuses even the installed kind.
        Assert.Contains("runs only on a Development host with the release switch open",
            Assert.Throws<WorkflowDomainException>(() => WorkflowEngineService.EnsureAutomaticMultiInstanceRuntimeAvailable(
                Body(BpmnFlowNodeTypes.ScriptTask), released: false)).Message);
        // An ordinary activity never depends on the switch.
        WorkflowEngineService.EnsureAutomaticMultiInstanceRuntimeAvailable(
            new FlowNodeModel { Id = 2, Type = BpmnFlowNodeTypes.ScriptTask }, released: false);
    }

    [Fact]
    public void JavaScript_conservative_reads_are_optional_while_statically_named_dependencies_stay_required()
    {
        var limit = new VariableModel { Id = 4, Name = "limit", DataType = "number", Scope = "shared", SharedKey = "tests.limit", Access = "read" };
        var unrelated = new VariableModel { Id = 5, Name = "unrelated", DataType = "number", Scope = "shared", SharedKey = "tests.unrelated", Access = "read" };
        var target = new VariableModel { Id = 6, Name = "target", DataType = "number", Scope = "shared", SharedKey = "tests.target", Access = "readWrite" };
        var node = JavaScriptBody([Local("value", validation: "value < limit")], "execution.setVariable('value', 1);");
        var definition = Definition(node, limit, unrelated, target);

        var stage = SharedVariableAccessPlanner.Build(definition, new ConditionalEventDefinitionAnalyzer())
            .SelectAutomaticPhase(2, AutomaticMultiInstanceAccessPhases.StageItem).AutomaticPhase!;

        Assert.Contains("limit", stage.ReadAliases);
        Assert.DoesNotContain("limit", stage.OptionalReadAliases);
        Assert.Contains("unrelated", stage.OptionalReadAliases);
        Assert.Contains("target", stage.OptionalReadAliases);
        Assert.Contains("target", stage.ProducerAliases);

        var ncalc = NCalcBody([Local("value", validation: "value < limit")], ("value", "1"));
        var exact = SharedVariableAccessPlanner.Build(Definition(ncalc, limit, unrelated, target), new ConditionalEventDefinitionAnalyzer())
            .SelectAutomaticPhase(2, AutomaticMultiInstanceAccessPhases.StageItem).AutomaticPhase!;
        Assert.Empty(exact.OptionalReadAliases);
        Assert.DoesNotContain("unrelated", exact.ReadAliases);
    }
}
