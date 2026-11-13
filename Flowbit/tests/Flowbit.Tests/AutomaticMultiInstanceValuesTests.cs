using System.Text.Json;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

public sealed class AutomaticMultiInstanceValuesTests
{
    [Theory]
    [InlineData(null, 4)]
    [InlineData("", 4)]
    [InlineData(" ", 4)]
    [InlineData("0", 4)]
    [InlineData("-1", 4)]
    [InlineData("1.5", 4)]
    [InlineData("1e2", 4)]
    [InlineData("2147483648", 4)]
    [InlineData("2147483647", 1000)]
    [InlineData("1001", 1000)]
    [InlineData(" +7 ", 7)]
    [InlineData("1", 1)]
    public void Concurrency_settings_parse_fallback_and_cap(string? value, int expected) =>
        Assert.Equal(expected, AutomaticMultiInstanceLimits.ParseConcurrency(value, 4));

    [Fact]
    public void Effective_window_is_bounded_by_mode_count_authoring_and_operator_cap()
    {
        var multi = new MultiInstanceModel();
        Assert.Equal(2, AutomaticMultiInstanceLimits.ResolveConcurrency(multi, 50, "9", "2"));
        multi.MaxConcurrency = 20;
        Assert.Equal(3, AutomaticMultiInstanceLimits.ResolveConcurrency(multi, 3, "9", "10"));
        multi.Mode = MultiInstanceModes.Sequential;
        multi.MaxConcurrency = 1;
        Assert.Equal(1, AutomaticMultiInstanceLimits.ResolveConcurrency(multi, 50, "9", "10"));
        Assert.Equal(0, AutomaticMultiInstanceLimits.ResolveConcurrency(multi, 0, "9", "10"));
    }

    [Theory]
    [InlineData("string", "[\" alice \",\"ALICE\",\" alice \"]")]
    [InlineData("number", "[1,2,1]")]
    [InlineData("boolean", "[true,false,true]")]
    [InlineData("date", "[\"2026-11-11\"]")]
    [InlineData("datetime", "[\"2026-11-11T12:00:00Z\"]")]
    [InlineData("json", "[{\"a\":1},null,[1],{\"a\":1}]")]
    public void Typed_collection_preserves_each_element_and_duplicates(string type, string json)
    {
        using var input = JsonDocument.Parse(json);
        var copied = AutomaticMultiInstanceValues.CopyCollection(input.RootElement,
            new VariableModel { DataType = type, IsArray = true }, 1000);
        Assert.Equal(input.RootElement.EnumerateArray().Select(item => item.GetRawText()), copied.Select(item => item.GetRawText()));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[1]")]
    public void Source_null_scalar_or_invalid_elements_fail_even_for_nullable_variable(string json)
    {
        using var input = JsonDocument.Parse(json);
        var error = Assert.Throws<AutomaticMultiInstanceValueException>(() =>
            AutomaticMultiInstanceValues.CopyCollection(input.RootElement,
                new VariableModel { DataType = "string", IsArray = true, Nullable = true }, 3));
        Assert.Equal(AutomaticMultiInstanceValues.SourceInvalid, error.Code);
    }

    [Fact]
    public void Empty_input_is_valid_and_limit_plus_one_is_refused()
    {
        var declaration = new VariableModel { DataType = "number", IsArray = true };
        Assert.Empty(AutomaticMultiInstanceValues.CopyCollection(Json(Array.Empty<int>()), declaration, 1));
        Assert.Throws<AutomaticMultiInstanceValueException>(() => AutomaticMultiInstanceValues.CopyCollection(Json(new[] { 1, 2 }), declaration, 1));
        var snapshot = AutomaticMultiInstanceValues.CreateInputSnapshot([], new Dictionary<string, JsonElement>(), DateTimeOffset.UtcNow);
        Assert.Equal(0, snapshot.GetProperty("collection").GetArrayLength());
        Assert.Equal("[]", AutomaticMultiInstanceValues.BuildAggregate([], []).GetRawText());
    }

    [Fact]
    public void Frozen_collection_instance_and_item_values_override_later_staging()
    {
        var snapshot = AutomaticMultiInstanceValues.CreateInputSnapshot([Json("original")],
            new Dictionary<string, JsonElement> { ["amount"] = Json(5) }, DateTimeOffset.UtcNow);
        var context = AutomaticMultiInstanceValues.CreateItemContext(snapshot, 0,
            new Dictionary<string, JsonElement> { ["items"] = Json(new[] { "changed" }), ["amount"] = Json(99), ["shared"] = Json(7) }, "items");
        Assert.Equal("original", context["mi.item"].GetString());
        Assert.Equal("original", context["items"][0].GetString());
        Assert.Equal(5, context["amount"].GetInt32());
        Assert.Equal(7, context["shared"].GetInt32());
        Assert.Equal(0, context["mi.index"].GetInt32());
        Assert.Equal(1, context["mi.total"].GetInt32());
    }

    [Fact]
    public void Out_of_order_results_are_ordered_and_partial_failures_have_no_values_or_actor()
    {
        var result = AutomaticMultiInstanceValues.BuildAggregate([Json("a"), Json("b"), Json("c")],
        [new(2, "cancelled", Json(new { secret = "discard" })), new(0, "completed", Json(new { output = 10 })),
            new(1, "failed", Json(new { error = "discard" }))], partial: true);
        Assert.Equal([0, 1, 2], result.EnumerateArray().Select(item => item.GetProperty("index").GetInt32()));
        Assert.Equal(10, result[0].GetProperty("variables").GetProperty("output").GetInt32());
        Assert.Equal("{}", result[1].GetProperty("variables").GetRawText());
        Assert.Equal("{}", result[2].GetProperty("variables").GetRawText());
        Assert.DoesNotContain("discard", result.GetRawText());
        Assert.DoesNotContain("user", result.GetRawText());
    }

    [Fact]
    public void Snapshot_refuses_an_input_whose_minimum_result_cannot_fit()
    {
        // This input fits the snapshot with one collection value; its result needs
        // an item envelope too. Use many small items to expose envelope overhead.
        var collection = Enumerable.Range(0, 1000).Select(_ => Json(new string('x', 1000))).ToArray();
        var error = Assert.Throws<AutomaticMultiInstanceValueException>(() =>
            AutomaticMultiInstanceValues.CreateInputSnapshot(collection, new Dictionary<string, JsonElement>(), DateTimeOffset.UtcNow));
        Assert.Equal(AutomaticMultiInstanceValues.ResultTooLarge, error.Code);
    }

    [Fact]
    public void Required_local_must_be_set_nullable_unset_becomes_null_and_unknown_target_is_rejected()
    {
        MultiInstanceOutputVariableModel[] outputs = [new() { Name = "required", DataType = "number" }, new() { Name = "optional", Nullable = true }];
        var context = new Dictionary<string, JsonElement>();
        Assert.Throws<AutomaticMultiInstanceValueException>(() => WorkflowEngineService.ValidateAutomaticLocalResult(outputs, context, context));
        var result = WorkflowEngineService.ValidateAutomaticLocalResult(outputs,
            new Dictionary<string, JsonElement> { ["required"] = Json(7) }, context);
        Assert.Equal(JsonValueKind.Null, result.GetProperty("optional").ValueKind);
        Assert.Throws<AutomaticMultiInstanceValueException>(() => WorkflowEngineService.ValidateAutomaticLocalResult(outputs,
            new Dictionary<string, JsonElement> { ["required"] = Json(7), ["undeclared"] = Json(8) }, context));
    }

    [Fact]
    public void Fresh_attempt_defaults_use_existing_template_coercion_and_validation()
    {
        MultiInstanceOutputVariableModel[] outputs = [new() { Name = "output", DataType = "number", DefaultValue = Json("${mi.index}"), Validation = "output >= 0" }];
        var context = new Dictionary<string, JsonElement> { ["mi.index"] = Json(3) };
        var first = WorkflowEngineService.CreateAutomaticLocalBuffer(outputs, context);
        first["output"] = Json(99);
        var second = WorkflowEngineService.CreateAutomaticLocalBuffer(outputs, context);
        Assert.Equal(3, second["output"].GetInt32());
        Assert.Equal(3, WorkflowEngineService.ValidateAutomaticLocalResult(outputs, second, context).GetProperty("output").GetInt32());
    }

    // A kind without an installed adapter must never run as one ordinary activity.
    // TASK-05C and TASK-05B installed the script and service adapters, so only call
    // bodies stay refused.
    [Theory]
    [InlineData("callActivity")]
    public void Development_authoring_does_not_execute_a_body_as_an_ordinary_activity(string type)
    {
        Assert.Throws<WorkflowDomainException>(() => WorkflowEngineService.EnsureAutomaticMultiInstanceRuntimeAvailable(
            new FlowNodeModel { Id = 2, Type = type, MultiInstance = new() }, released: true));
        WorkflowEngineService.EnsureAutomaticMultiInstanceRuntimeAvailable(new FlowNodeModel { Id = 2, Type = type }, released: true);
    }

    [Theory]
    [InlineData("scriptTask")]
    [InlineData("serviceTask")]
    public void An_installed_adapter_lets_its_bodies_enter_the_runtime_on_a_released_host(string type)
    {
        WorkflowEngineService.EnsureAutomaticMultiInstanceRuntimeAvailable(
            new FlowNodeModel { Id = 2, Type = type, MultiInstance = new() }, released: true);
    }

    private static JsonElement Json<T>(T value) => JsonSerializer.SerializeToElement(value);
}
