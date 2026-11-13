using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// TASK-05B REST adapter contract without a database: mapping a response into an item
/// candidate, the status target, every authored output failure, and the exact names a
/// body freezes and an item stages.
/// </summary>
public sealed class AutomaticMultiInstanceServiceAdapterTests
{
    private static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value);

    private static WorkflowModel Definition(FlowNodeModel body, params VariableModel[] variables)
    {
        var all = new List<VariableModel>
        {
            new() { Id = 1, Name = "supplierIds", DataType = "string", IsArray = true, DefaultValue = Json(Array.Empty<string>()) },
            new() { Id = 2, Name = "notificationResults", DataType = "json", DefaultValue = Json(Array.Empty<object>()) }
        };
        all.AddRange(variables);
        return new WorkflowModel
        {
            Id = "service-body", Name = "Service body", InitialEventId = 1, Variables = all,
            FlowNodes =
            [
                new() { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                body,
                new() { Id = 3, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows = [new() { Id = 101, SourceRef = 1, TargetRef = 20 }, new() { Id = 201, SourceRef = 20, TargetRef = 3 }]
        };
    }

    private static MultiInstanceOutputVariableModel Local(string name, string type = "string", bool nullable = false,
        string? validation = null, JsonElement? defaultValue = null) => new()
    {
        Name = name, DataType = type, Nullable = nullable, Validation = validation, DefaultValue = defaultValue
    };

    private static ServiceOutputMappingModel Mapping(string variable, string path, string type = "string",
        bool required = true, JsonElement? defaultValue = null, string? validation = null) => new()
    {
        Variable = variable, Path = path, DataType = type, IsArray = false, Required = required,
        DefaultValue = defaultValue, Validation = validation
    };

    // The plan's example body: one request per supplier, a local id and a local status.
    private static FlowNodeModel Body(IEnumerable<MultiInstanceOutputVariableModel>? outputs = null,
        IEnumerable<ServiceOutputMappingModel>? mappings = null, string? statusVariable = "responseStatus") => new()
    {
        Id = 20, Name = "Notify suppliers", Type = BpmnFlowNodeTypes.ServiceTask, AsyncBefore = true,
        MultiInstance = new()
        {
            Mode = "parallel", Source = "collection", CollectionVariable = "supplierIds", CompletionEvaluation = "afterAll",
            MaxConcurrency = 4, ResultVariable = "notificationResults",
            OutputVariables = (outputs ?? [Local("notificationId"), Local("responseStatus", "number")]).ToList()
        },
        Service = new()
        {
            Type = "rest", Method = "POST", Url = "https://notifications.example.test/send", TimeoutSeconds = 30,
            Headers =
            [
                new() { Name = "Content-Type", Value = "application/json" },
                new() { Name = "Idempotency-Key", Value = "flowbit-supplier-${sys.jobId}" },
                new() { Name = "Authorization", Value = "Bearer ${config.notificationToken}" }
            ],
            Body = "{\"supplier\":${mi.item},\"index\":${mi.index},\"region\":\"${region}\"}",
            StatusVariable = statusVariable,
            OutputMappings = (mappings ?? [Mapping("notificationId", "id")]).ToList()
        }
    };

    // An item context as the coordinator builds it: frozen inputs and collection,
    // staged shared/context values, mi.*, job identity, then fresh local defaults.
    private static AutomaticMultiInstanceItemContext Context(FlowNodeModel node, object[] collection, int index,
        Dictionary<string, object?>? instanceInputs = null, Dictionary<string, object?>? staged = null,
        Dictionary<string, long>? versions = null)
    {
        var snapshot = Json(new { collection, instanceInputs = instanceInputs ?? new Dictionary<string, object?>() });
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

    private static ServiceTaskResult Ok(object body, int status = 201) =>
        new(true, status, JsonSerializer.Serialize(body), null);

    private static AutomaticMultiInstanceCandidate Map(FlowNodeModel node, WorkflowModel definition,
        AutomaticMultiInstanceItemContext context, ServiceTaskResult result) =>
        WorkflowEngineService.MapAutomaticServiceItem(node, definition, context, result);

    private static string Fails(FlowNodeModel node, WorkflowModel definition, AutomaticMultiInstanceItemContext context,
        ServiceTaskResult result) =>
        Assert.Throws<WorkflowEngineService.AutomaticServiceItemException>(() => Map(node, definition, context, result)).Message;

    [Fact]
    public void A_2xx_response_maps_into_item_locals_with_its_status_and_no_shared_write()
    {
        var node = Body();
        var definition = Definition(node, new VariableModel { Id = 3, Name = "region", DataType = "string", DefaultValue = Json("eu") });
        var context = Context(node, ["supplier-a", "supplier-b"], 1, new() { ["region"] = "eu" });

        var candidate = Map(node, definition, context, Ok(new { id = "notice-002", ignored = true }));

        Assert.Equal("notice-002", candidate.LocalVariables["notificationId"].GetString());
        Assert.Equal(201, candidate.LocalVariables["responseStatus"].GetInt32());
        Assert.Empty(candidate.SharedWrites);
        Assert.Empty(candidate.SharedOutputValueVersions);
        // The validation context carries only names an output or mapping validation reads.
        Assert.Empty(candidate.ValidationContext);
        // The status target is not a mapping, so publication never repeats a rule for it.
        Assert.Equal(new[] { "notificationId" }, candidate.MappedTargets);
    }

    [Fact]
    public void An_optional_mapping_the_response_left_empty_is_not_a_mapped_target()
    {
        var node = Body(
            outputs: [Local("notificationId"), Local("optionalRef", defaultValue: Json("R-1"))],
            mappings: [Mapping("notificationId", "id"), Mapping("optionalRef", "ref", required: false, validation: "Len(optionalRef) > 2")],
            statusVariable: null);

        var candidate = Map(node, Definition(node), Context(node, ["a"], 0), Ok(new { id = "n-1" }));

        // The local keeps its output default, which no mapping filled.
        Assert.Equal("R-1", candidate.LocalVariables["optionalRef"].GetString());
        Assert.Equal(new[] { "notificationId" }, candidate.MappedTargets);

        // A mapping default fills its target, so its rule is repeated at publication.
        var defaulted = Body(
            outputs: [Local("notificationId"), Local("optionalRef")],
            mappings: [Mapping("notificationId", "id"), Mapping("optionalRef", "ref", required: false, defaultValue: Json("R-${mi.index}"))],
            statusVariable: null);
        candidate = Map(defaulted, Definition(defaulted), Context(defaulted, ["a"], 0), Ok(new { id = "n-1" }));
        Assert.Equal("R-0", candidate.LocalVariables["optionalRef"].GetString());
        Assert.Equal(new[] { "notificationId", "optionalRef" }, candidate.MappedTargets!.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_shared_target_writes_through_its_staged_fence_and_the_status_is_visible_to_validations()
    {
        var counter = new VariableModel
        {
            Id = 4, Name = "lastNotice", DataType = "string", Scope = VariableScopes.Shared,
            SharedKey = "tests.notice", Access = SharedVariableAccessModes.ReadWrite
        };
        var node = Body(
            outputs: [Local("responseStatus", "number", validation: "responseStatus == 201")],
            mappings: [Mapping("lastNotice", "id", validation: "responseStatus == 201 and Len(lastNotice) > 3")]);
        var definition = Definition(node, counter);
        var context = Context(node, ["supplier-a"], 0, versions: new() { ["lastNotice"] = 9 });

        var candidate = Map(node, definition, context, Ok(new { id = "notice-001" }));

        Assert.Equal("notice-001", candidate.SharedWrites["lastNotice"].GetString());
        Assert.Equal(9, candidate.SharedOutputValueVersions["lastNotice"]);
        Assert.Equal(201, candidate.LocalVariables["responseStatus"].GetInt32());
        // A shared write is never copied into the item's locals.
        Assert.False(candidate.LocalVariables.ContainsKey("lastNotice"));
    }

    [Fact]
    public void A_mapping_default_resolves_against_the_item_context_when_the_path_is_absent()
    {
        var node = Body(mappings: [Mapping("notificationId", "missing", required: true, defaultValue: Json("fallback-${mi.index}"))]);
        var definition = Definition(node);

        var candidate = Map(node, definition, Context(node, ["a", "b", "c"], 2), Ok(new { id = "ignored" }));

        Assert.Equal("fallback-2", candidate.LocalVariables["notificationId"].GetString());
    }

    [Theory]
    [InlineData("1e9999")]
    [InlineData("-1e9999")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    public void A_numeric_default_whose_text_is_not_a_finite_number_is_an_authored_output_failure(string item)
    {
        // A mapping default the response left to render.
        var mapped = Body(
            outputs: [Local("notificationId"), Local("quantity", "number")],
            mappings: [Mapping("notificationId", "id"), Mapping("quantity", "quantity", "number", required: false, defaultValue: Json("${mi.item}"))],
            statusVariable: null);
        Assert.Contains("'quantity'", Fails(mapped, Definition(mapped), Context(mapped, [item], 0), Ok(new { id = "n-1" })));

        // A local output default no mapping replaces.
        var local = Body(outputs: [Local("notificationId"), Local("quantity", "number", defaultValue: Json("${mi.item}"))],
            statusVariable: null);
        Assert.Contains("'quantity'", Fails(local, Definition(local), Context(local, [item], 0), Ok(new { id = "n-1" })));
    }

    [Fact]
    public void Every_authored_output_failure_is_one_exception_and_saves_nothing()
    {
        var node = Body();
        var definition = Definition(node);
        var context = Context(node, ["supplier-a"], 0);

        Assert.Contains("'notificationId'",
            Fails(node, definition, context, Ok(new { other = 1 })));
        Assert.Equal("Service task #20 response body was not valid JSON.",
            Fails(node, definition, context, new ServiceTaskResult(true, 200, "{not json", null)));
        Assert.Contains("must be",
            Fails(node, definition, context, Ok(new { id = 42 })));

        // A non-nullable local that no mapping fills is incomplete.
        var incomplete = Body(outputs: [Local("notificationId"), Local("unfilled")], statusVariable: null);
        Assert.Equal("Service task #20 item left a non-nullable local output without a value.",
            Fails(incomplete, Definition(incomplete), Context(incomplete, ["a"], 0), Ok(new { id = "x" })));

        // A local's own validation is part of its contract.
        var validated = Body(outputs: [Local("notificationId", validation: "StartsWith(notificationId, 'N-')"), Local("responseStatus", "number")]);
        Assert.Contains("failed validation",
            Fails(validated, Definition(validated), Context(validated, ["a"], 0), Ok(new { id = "x-1" })));
    }

    [Fact]
    public void A_target_that_is_not_an_item_target_or_a_shared_value_breaking_its_contract_fails_the_item()
    {
        var instance = new VariableModel { Id = 5, Name = "processNotice", DataType = "string", DefaultValue = Json("") };
        var node = Body(mappings: [Mapping("processNotice", "id")]);
        Assert.Equal("Service task #20 item cannot write 'processNotice'; an item writes only its declared local outputs or readWrite shared aliases.",
            Fails(node, Definition(node, instance), Context(node, ["a"], 0), Ok(new { id = "x" })));

        var shared = new VariableModel
        {
            Id = 6, Name = "code", DataType = "string", Scope = VariableScopes.Shared, SharedKey = "tests.code",
            Access = SharedVariableAccessModes.ReadWrite, Validation = "Len(code) == 3"
        };
        var sharedNode = Body(mappings: [Mapping("code", "id")]);
        Assert.Contains("failed validation",
            Fails(sharedNode, Definition(sharedNode, shared), Context(sharedNode, ["a"], 0, versions: new() { ["code"] = 1 }),
                Ok(new { id = "too-long" })));
    }

    [Fact]
    public void A_body_freezes_exactly_the_instance_names_its_templates_and_expressions_reference()
    {
        var node = Body(
            outputs: [Local("notificationId", validation: "Len(notificationId) <= maxLength"), Local("responseStatus", "number")],
            mappings: [Mapping("notificationId", "id", defaultValue: Json("${fallbackPrefix}-x"), validation: "notificationId != blocked")]);

        var names = WorkflowEngineService.AutomaticServiceInstanceInputNames(node, Definition(node),
            ["region", "maxLength", "blocked", "fallbackPrefix", "unrelatedLargeVariable", "supplierIds", "notificationResults"]);

        Assert.Equal(new[] { "blocked", "fallbackPrefix", "maxLength", "region" }, names.Order(StringComparer.Ordinal));
        // Context names are staged per item, never frozen with the body.
        var referenced = WorkflowEngineService.AutomaticServiceReferencedNames(node);
        Assert.Contains("sys.jobId", referenced);
        Assert.Contains("config.notificationToken", referenced);
        Assert.Contains("mi.item", referenced);
    }
}
