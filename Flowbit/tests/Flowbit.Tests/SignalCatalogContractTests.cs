using System.Text.Json;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// The payload and definition checks against a catalog entry (EVENT-04F): every rule of the
/// plan's tables, the stages they belong to, and messages that never quote a value.
/// </summary>
public sealed class SignalCatalogContractTests
{
    private const string Name = "warehouse.product-recall";

    internal static SignalCatalogEntryRecord Entry(
        IReadOnlyList<SignalCatalogFieldDto> fields,
        string name = Name,
        bool allowWorkflows = true,
        bool archived = false,
        IReadOnlyList<string>? roles = null,
        bool allowClients = true) =>
        new(
            1,
            name,
            null,
            archived ? SignalCatalogStatuses.Archived : SignalCatalogStatuses.Active,
            fields,
            roles ?? [],
            allowClients,
            allowWorkflows,
            DateTimeOffset.UnixEpoch,
            "alice",
            DateTimeOffset.UnixEpoch,
            "alice",
            archived ? DateTimeOffset.UnixEpoch : null);

    private static readonly SignalCatalogFieldDto[] RecallFields =
    [
        new("reason", WorkflowVariableTypes.String, false, true),
        new("skus", WorkflowVariableTypes.String, true, true),
        new("details", WorkflowVariableTypes.Json, false, false)
    ];

    private static string? Payload(SignalCatalogEntryRecord entry, string? json) =>
        SignalCatalogContract.PayloadProblem(
            entry,
            json is null ? null : JsonDocument.Parse(json).RootElement.Clone());

    // ---------------------------------------------------------------- payload of an API throw

    [Fact]
    public void Payload_AcceptsAFittingPayload()
    {
        var entry = Entry(RecallFields);
        Assert.Null(Payload(entry, """{"reason":"mould","skus":["a","b"],"details":{"lot":7}}"""));
        Assert.Null(Payload(entry, """{"reason":"mould","skus":[],"details":{"lot":null}}"""));
        Assert.Null(Payload(entry, """{"skus":["a"],"reason":"mould"}"""));
    }

    [Fact]
    public void Payload_NoFieldMayBeNull()
    {
        // A receiver's mapping refuses null unless its target is nullable, so a null that one
        // receiver accepts would fail another; an optional field is left out instead.
        var entry = Entry([
            new SignalCatalogFieldDto("reason", WorkflowVariableTypes.String, false, true),
            new SignalCatalogFieldDto("note", WorkflowVariableTypes.String, false, false),
            new SignalCatalogFieldDto("count", WorkflowVariableTypes.Number, false, false),
            new SignalCatalogFieldDto("tags", WorkflowVariableTypes.String, true, false),
            new SignalCatalogFieldDto("details", WorkflowVariableTypes.Json, false, false)
        ]);
        foreach (var field in new[] { "note", "count", "tags", "details" })
        {
            Assert.Equal(
                $"Signal 'warehouse.product-recall' payload field '{field}' is null; leave out an optional field instead of sending null.",
                Payload(entry, $$"""{"reason":"x","{{field}}":null}"""));
        }

        // Fields come in contract order, before unknown members.
        Assert.Equal(
            "Signal 'warehouse.product-recall' payload field 'note' is null; leave out an optional field instead of sending null.",
            Payload(entry, """{"other":1,"details":null,"reason":"x","note":null}"""));
        Assert.Null(Payload(entry, """{"reason":"x"}"""));
        // A null inside a json value or an array element is checked by the field's type, not here.
        Assert.Null(Payload(entry, """{"reason":"x","details":{"lot":null},"tags":[]}"""));
        Assert.Equal(
            "Signal 'warehouse.product-recall' payload field 'tags' must be an array of strings.",
            Payload(entry, """{"reason":"x","tags":["a",null]}"""));
    }

    [Fact]
    public void Payload_MissingRequiredFieldsInContractOrder()
    {
        var entry = Entry(RecallFields);
        Assert.Equal("Signal 'warehouse.product-recall' payload is missing the required field 'reason'.",
            Payload(entry, """{"skus":["a"]}"""));
        Assert.Equal("Signal 'warehouse.product-recall' payload is missing the required field 'reason'.",
            Payload(entry, """{"reason":null,"skus":null}"""));
        Assert.Equal("Signal 'warehouse.product-recall' payload is missing the required field 'reason'.",
            Payload(entry, null));
        Assert.Equal("Signal 'warehouse.product-recall' payload is missing the required field 'skus'.",
            Payload(entry, """{"reason":"x"}"""));
    }

    [Fact]
    public void Payload_FieldsComeBeforeUnknownMembers()
    {
        var entry = Entry(RecallFields);
        Assert.Equal("Signal 'warehouse.product-recall' payload field 'skus' must be an array of strings.",
            Payload(entry, """{"reson":"x","reason":"x","skus":"a"}"""));
        Assert.Equal(
            "Signal 'warehouse.product-recall' payload member 'reson' is not a field of its catalog entry. Its fields: reason (string), skus (string[]), details (json).",
            Payload(entry, """{"reason":"x","skus":["a"],"reson":"x","other":1}"""));
        Assert.Equal(
            "Signal 'warehouse.product-recall' payload member 'Reason' is not a field of its catalog entry. Its fields: reason (string), skus (string[]), details (json).",
            Payload(entry, """{"reason":"x","skus":["a"],"Reason":"x"}"""));
    }

    [Fact]
    public void Payload_AnEntryWithoutFieldsAcceptsOnlyAnEmptyPayload()
    {
        var entry = Entry([]);
        Assert.Null(Payload(entry, null));
        Assert.Null(Payload(entry, "{}"));
        Assert.Equal(
            "Signal 'warehouse.product-recall' payload member 'x' is not a field of its catalog entry. Its fields: none.",
            Payload(entry, """{"x":1}"""));
    }

    [Theory]
    [InlineData("string", false, "\"text\"", "1", "a string")]
    [InlineData("number", false, "1.5", "\"1\"", "a number")]
    [InlineData("boolean", false, "true", "\"true\"", "true or false")]
    [InlineData("date", false, "\"2026-11-24\"", "\"2026-11-24T10:00:00Z\"", "a date (YYYY-MM-DD)")]
    [InlineData("datetime", false, "\"2026-11-24T10:00:00+01:00\"", "\"2026-11-24\"", "a date and time (ISO-8601)")]
    [InlineData("string", true, "[\"a\"]", "[\"a\",1]", "an array of strings")]
    [InlineData("number", true, "[1,2]", "1", "an array of numbers")]
    [InlineData("boolean", true, "[false]", "[\"no\"]", "an array of true or false values")]
    [InlineData("date", true, "[\"2026-01-31\"]", "[\"2026-02-30\"]", "an array of dates (YYYY-MM-DD)")]
    [InlineData("datetime", true, "[\"2026-01-31T00:00:00Z\"]", "[\"yesterday\"]", "an array of dates and times (ISO-8601)")]
    [InlineData("json", true, "[{\"a\":1},2]", "{\"a\":1}", "an array of JSON values")]
    public void Payload_EveryTypeAcceptedAndRefused(string type, bool isArray, string good, string bad, string words)
    {
        var entry = Entry([new SignalCatalogFieldDto("value", type, isArray, true)]);
        Assert.Null(Payload(entry, $$"""{"value":{{good}}}"""));
        var problem = Payload(entry, $$"""{"value":{{bad}}}""");
        Assert.Equal($"Signal 'warehouse.product-recall' payload field 'value' must be {words}.", problem);
    }

    [Fact]
    public void Payload_AJsonFieldAcceptsAnyValue()
    {
        var entry = Entry([new SignalCatalogFieldDto("details", WorkflowVariableTypes.Json, false, true)]);
        foreach (var value in new[] { "1", "\"x\"", "[1,\"a\"]", "{\"a\":{}}", "false" })
        {
            Assert.Null(Payload(entry, $$"""{"details":{{value}}}"""));
        }
    }

    [Fact]
    public void Payload_MessagesNeverQuoteAValue()
    {
        var entry = Entry(RecallFields);
        const string secret = "patient-4711-secret";
        foreach (var json in new[]
                 {
                     $$"""{"reason":1,"skus":["{{secret}}"]}""",
                     $$"""{"reason":"x","skus":"{{secret}}"}""",
                     $$"""{"reason":"x","skus":["a"],"{{"note"}}":"{{secret}}"}"""
                 })
        {
            var problem = Payload(entry, json);
            Assert.NotNull(problem);
            Assert.DoesNotContain(secret, problem, StringComparison.Ordinal);
        }
    }

    // ---------------------------------------------------------------- type compatibility

    [Theory]
    // Field, mapping, accepted
    [InlineData("string", false, "string", false, true)]
    [InlineData("string", false, "json", false, true)]
    [InlineData("string", false, "json", true, false)]
    [InlineData("string", true, "json", true, true)]
    [InlineData("string", true, "json", false, true)]
    [InlineData("string", false, "date", false, false)]
    [InlineData("date", false, "string", false, true)]
    [InlineData("datetime", true, "string", true, true)]
    [InlineData("datetime", true, "string", false, false)]
    [InlineData("date", false, "datetime", false, false)]
    [InlineData("number", false, "number", true, false)]
    [InlineData("number", false, "string", false, false)]
    [InlineData("json", false, "json", false, true)]
    [InlineData("json", false, "string", false, false)]
    [InlineData("json", true, "json", true, true)]
    [InlineData("boolean", false, "boolean", false, true)]
    public void ReceiverTypeTable(string field, bool fieldArray, string mapping, bool mappingArray, bool accepted) =>
        Assert.Equal(accepted, SignalCatalogContract.ReceiverAccepts(field, fieldArray, mapping, mappingArray));

    [Theory]
    // Field, variable, accepted
    [InlineData("string", false, "string", false, true)]
    [InlineData("string", false, "date", false, true)]
    [InlineData("string", true, "datetime", true, true)]
    [InlineData("string", true, "datetime", false, false)]
    [InlineData("date", false, "string", false, false)]
    [InlineData("date", false, "date", false, true)]
    [InlineData("json", false, "number", true, true)]
    [InlineData("json", true, "boolean", false, true)]
    [InlineData("number", false, "json", false, false)]
    [InlineData("number", true, "number", false, false)]
    public void ThrowTypeTable(string field, bool fieldArray, string variable, bool variableArray, bool accepted) =>
        Assert.Equal(accepted, SignalCatalogContract.ThrowAccepts(field, fieldArray, variable, variableArray));

    // ---------------------------------------------------------------- definitions

    private static IReadOnlyDictionary<string, SignalCatalogEntryRecord> Entries(params SignalCatalogEntryRecord[] entries) =>
        entries.ToDictionary(entry => entry.Name, StringComparer.Ordinal);

    private static IReadOnlyList<string> Problems(
        WorkflowModel model,
        SignalCatalogStage stage,
        string mode = SignalCatalogModes.Optional,
        params SignalCatalogEntryRecord[] entries) =>
        SignalCatalogContract.FindDefinitionProblems(model, Entries(entries), mode, stage)
            .Select(problem => problem.Message)
            .ToList();

    internal static MessageOutputMappingModel Mapping(
        string variable,
        string path,
        string type,
        bool isArray = false,
        bool required = false,
        JsonElement? defaultValue = null) =>
        new()
        {
            Variable = variable,
            Path = path,
            DataType = type,
            IsArray = isArray,
            Required = required,
            DefaultValue = defaultValue
        };

    internal static WorkflowModel ReceiverModel(string type, params MessageOutputMappingModel[] mappings)
    {
        var model = new WorkflowModel
        {
            Id = "catalog-contract",
            Name = "Catalog contract",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 3, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 9, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ]
        };
        var receiver = new FlowNodeModel
        {
            Id = 4,
            Name = "Receive",
            Type = type,
            Signal = new SignalEventModel { Name = Name, OutputMappings = [.. mappings] }
        };
        if (type == BpmnFlowNodeTypes.SignalBoundaryEvent)
        {
            receiver.AttachedToRef = 3;
        }

        model.FlowNodes.Add(receiver);
        return model;
    }

    internal static WorkflowModel ThrowModel(
        string type,
        IReadOnlyList<VariableModel> variables,
        params SignalPayloadEntryModel[] payload) =>
        new()
        {
            Id = "catalog-contract-throw",
            Name = "Catalog contract throw",
            InitialEventId = 1,
            Variables = [.. variables],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel
                {
                    Id = 7,
                    Name = "Throw",
                    Type = type,
                    Signal = new SignalEventModel
                    {
                        Name = Name,
                        Scope = new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows },
                        Payload = [.. payload]
                    }
                }
            ]
        };

    private static VariableModel Variable(string name, string type, bool isArray = false, bool nullable = true) =>
        new() { Id = name.Length, Name = name, DataType = type, IsArray = isArray, Nullable = nullable };

    public static TheoryData<string, string> SignalEventTypes => new()
    {
        { BpmnFlowNodeTypes.IntermediateSignalCatchEvent, "Signal catch event #4" },
        { BpmnFlowNodeTypes.SignalBoundaryEvent, "Signal boundary event #4" },
        { BpmnFlowNodeTypes.SignalStartEvent, "Signal start event #4" },
        { BpmnFlowNodeTypes.IntermediateSignalThrowEvent, "Signal throw event #7" },
        { BpmnFlowNodeTypes.SignalEndEvent, "Signal end event #7" }
    };

    /// <summary>A definition with one signal event of the type and nothing else to check.</summary>
    private static WorkflowModel SignalEventModel(string type) =>
        BpmnFlowNodeTypes.ThrowsSignal(type) ? ThrowModel(type, []) : ReceiverModel(type);

    private static SignalPayloadEntryModel Send(string name, string? variable = null, string? source = null, bool required = false) =>
        new() { Name = name, Variable = variable, Source = source, Required = required };

    [Theory]
    [InlineData(BpmnFlowNodeTypes.IntermediateSignalCatchEvent, "Signal catch event #4")]
    [InlineData(BpmnFlowNodeTypes.SignalBoundaryEvent, "Signal boundary event #4")]
    [InlineData(BpmnFlowNodeTypes.SignalStartEvent, "Signal start event #4")]
    public void Receiver_SaveRulesAtEveryStage(string type, string label)
    {
        var entry = Entry([
            new SignalCatalogFieldDto("version", WorkflowVariableTypes.Number, false, true),
            new SignalCatalogFieldDto("note", WorkflowVariableTypes.String, false, false)
        ], name: Name);
        var model = ReceiverModel(type,
            Mapping("policyVersion", "verison", WorkflowVariableTypes.Number),
            Mapping("major", "version.major", WorkflowVariableTypes.Number),
            Mapping("label", "version", WorkflowVariableTypes.String));
        var expected = new[]
        {
            $"{label} mapping to 'policyVersion' reads 'verison', which is not a field of the catalog entry of '{Name}'. Its fields: version (number), note (string).",
            $"{label} mapping to 'major' reads 'version.major' below field 'version', which is a number; only a json field or an array element has parts.",
            $"{label} mapping to 'label' is string, but the catalog entry of '{Name}' defines 'version' as number."
        };
        foreach (var stage in Enum.GetValues<SignalCatalogStage>())
        {
            Assert.Equal(expected, Problems(model, stage, SignalCatalogModes.Optional, entry));
        }

        Assert.All(SignalCatalogContract.FindDefinitionProblems(model, Entries(entry), SignalCatalogModes.Optional, SignalCatalogStage.Draft),
            problem => Assert.True(problem.IsSaveProblem));
    }

    [Fact]
    public void Receiver_PathsWithAnIndexBelowJsonAndNestedFirstParts()
    {
        var entry = Entry([
            new SignalCatalogFieldDto("skus", WorkflowVariableTypes.String, true, true),
            new SignalCatalogFieldDto("details", WorkflowVariableTypes.Json, false, false),
            new SignalCatalogFieldDto("items", WorkflowVariableTypes.Json, true, false)
        ]);
        var fitting = ReceiverModel(BpmnFlowNodeTypes.IntermediateSignalCatchEvent,
            Mapping("first", "skus.0", WorkflowVariableTypes.String),
            Mapping("all", "skus", WorkflowVariableTypes.String, isArray: true),
            Mapping("anything", "skus", WorkflowVariableTypes.Json),
            Mapping("lot", "details.lot.number", WorkflowVariableTypes.Number),
            Mapping("item", "items.0.id", WorkflowVariableTypes.String),
            Mapping("onlyDefault", "", WorkflowVariableTypes.String));
        Assert.Empty(Problems(fitting, SignalCatalogStage.Publication, SignalCatalogModes.Optional, entry));

        var wrong = ReceiverModel(BpmnFlowNodeTypes.IntermediateSignalCatchEvent,
            Mapping("first", "skus.0", WorkflowVariableTypes.Number),
            Mapping("deep", "skus.0.x", WorkflowVariableTypes.String),
            Mapping("nested", "detail.lot", WorkflowVariableTypes.String));
        Assert.Equal(
            [
                $"Signal catch event #4 mapping to 'first' is number, but the catalog entry of '{Name}' defines the items of 'skus' as string.",
                "Signal catch event #4 mapping to 'deep' reads 'skus.0.x' below field 'skus', which is an array of strings; only a json field or an array element has parts.",
                $"Signal catch event #4 mapping to 'nested' reads 'detail.lot', whose first part 'detail' is not a field of the catalog entry of '{Name}'. Its fields: skus (string[]), details (json), items (json[])."
            ],
            Problems(wrong, SignalCatalogStage.Draft, SignalCatalogModes.Optional, entry));
    }

    [Theory]
    [InlineData(BpmnFlowNodeTypes.IntermediateSignalCatchEvent, "Signal catch event #4")]
    [InlineData(BpmnFlowNodeTypes.SignalBoundaryEvent, "Signal boundary event #4")]
    [InlineData(BpmnFlowNodeTypes.SignalStartEvent, "Signal start event #4")]
    public void Receiver_RequiredWithoutDefaultOnAnOptionalFieldIsAPublicationRule(string type, string label)
    {
        var entry = Entry([
            new SignalCatalogFieldDto("version", WorkflowVariableTypes.Number, false, true),
            new SignalCatalogFieldDto("note", WorkflowVariableTypes.String, false, false)
        ]);
        var model = ReceiverModel(type,
            Mapping("policyVersion", "version", WorkflowVariableTypes.Number, required: true),
            Mapping("note", "note", WorkflowVariableTypes.String, required: true),
            Mapping("noteWithDefault", "note", WorkflowVariableTypes.String, required: true,
                defaultValue: JsonSerializer.SerializeToElement("none")),
            Mapping("noteWithNullDefault", "note", WorkflowVariableTypes.String, required: true,
                defaultValue: JsonSerializer.SerializeToElement<string?>(null)));
        Assert.Empty(Problems(model, SignalCatalogStage.Draft, SignalCatalogModes.Optional, entry));
        var expected = new[]
        {
            $"{label} mapping to 'note' is required without a defaultValue, but 'note' is optional in the catalog entry of '{Name}'.",
            $"{label} mapping to 'noteWithNullDefault' is required without a defaultValue, but 'note' is optional in the catalog entry of '{Name}'."
        };
        Assert.Equal(expected, Problems(model, SignalCatalogStage.Publication, SignalCatalogModes.Optional, entry));
        Assert.Equal(expected, Problems(model, SignalCatalogStage.Reactivation, SignalCatalogModes.Optional, entry));
        Assert.All(SignalCatalogContract.FindDefinitionProblems(model, Entries(entry), SignalCatalogModes.Optional, SignalCatalogStage.Publication),
            problem => Assert.False(problem.IsSaveProblem));
    }

    [Theory]
    [InlineData(BpmnFlowNodeTypes.IntermediateSignalThrowEvent, "Signal throw event #7")]
    [InlineData(BpmnFlowNodeTypes.SignalEndEvent, "Signal end event #7")]
    public void Throw_SaveRules(string type, string label)
    {
        var entry = Entry(RecallFields);
        var model = ThrowModel(type,
            [Variable("skuList", WorkflowVariableTypes.String), Variable("why", WorkflowVariableTypes.String)],
            Send("reason", "why", required: true),
            Send("reson", "why"),
            Send("skus", "skuList", required: true));
        var expected = new[]
        {
            $"{label} payload sends 'reson', which is not a field of the catalog entry of '{Name}'. Its fields: reason (string), skus (string[]), details (json).",
            $"{label} payload entry 'skus' reads 'skuList', declared as string, but the catalog entry of '{Name}' defines it as string[]."
        };
        Assert.Equal(expected, Problems(model, SignalCatalogStage.Draft, SignalCatalogModes.Optional, entry));
        Assert.Equal(expected, Problems(model, SignalCatalogStage.Publication, SignalCatalogModes.Optional, entry));
    }

    [Theory]
    [InlineData(BpmnFlowNodeTypes.IntermediateSignalThrowEvent, "Signal throw event #7")]
    [InlineData(BpmnFlowNodeTypes.SignalEndEvent, "Signal end event #7")]
    public void Throw_PublicationRules(string type, string label)
    {
        var entry = Entry(RecallFields, allowWorkflows: false);
        var model = ThrowModel(type,
            [Variable("why", WorkflowVariableTypes.String)],
            Send("reason", "why"));
        Assert.Empty(Problems(model, SignalCatalogStage.Draft, SignalCatalogModes.Optional, entry));
        var expected = new[]
        {
            $"{label} throws '{Name}', which its catalog entry does not let workflows throw.",
            $"{label} payload does not send 'skus', which the catalog entry of '{Name}' requires.",
            $"{label} payload entry 'reason' must set required, because the catalog entry of '{Name}' requires it."
        };
        Assert.Equal(expected, Problems(model, SignalCatalogStage.Publication, SignalCatalogModes.Optional, entry));
        Assert.Equal(expected, Problems(model, SignalCatalogStage.Reactivation, SignalCatalogModes.Optional, entry));
    }

    [Fact]
    public void Payload_AnUnknownMemberListsAtMostTwentyFields()
    {
        var entry = Entry(Enumerable.Range(1, 25).Select(index => new SignalCatalogFieldDto($"f{index}", WorkflowVariableTypes.Number)).ToArray());
        var problem = Payload(entry, """{"other":1}""");
        Assert.StartsWith("Signal 'warehouse.product-recall' payload member 'other' is not a field of its catalog entry. Its fields: f1 (number), ", problem);
        Assert.EndsWith("f20 (number), and 5 more.", problem);
        Assert.DoesNotContain("f21", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void Throw_SourcesAndCompatibleVariableTypes()
    {
        var entry = Entry([
            new SignalCatalogFieldDto("orderInstance", WorkflowVariableTypes.Number, false, true),
            new SignalCatalogFieldDto("caseKey", WorkflowVariableTypes.String, false, true),
            new SignalCatalogFieldDto("due", WorkflowVariableTypes.String, true, false),
            new SignalCatalogFieldDto("anything", WorkflowVariableTypes.Json, false, false)
        ]);
        var fits = ThrowModel(BpmnFlowNodeTypes.SignalEndEvent,
            [
                Variable("dates", WorkflowVariableTypes.Date, isArray: true, nullable: false),
                Variable("flags", WorkflowVariableTypes.Boolean, isArray: true, nullable: false)
            ],
            Send("orderInstance", source: SignalPayloadSources.InstanceId),
            Send("caseKey", source: SignalPayloadSources.BusinessKey, required: true),
            Send("due", "dates"),
            Send("anything", "flags"));
        // A business key is left out for an instance without one, so it needs required; an instance ID never does.
        Assert.Empty(Problems(fits, SignalCatalogStage.Publication, SignalCatalogModes.Optional, entry));

        var optionalKey = ThrowModel(BpmnFlowNodeTypes.SignalEndEvent, [],
            Send("orderInstance", source: SignalPayloadSources.BusinessKey, required: true),
            Send("caseKey", source: SignalPayloadSources.BusinessKey));
        Assert.Equal(
            [
                $"Signal end event #7 payload entry 'orderInstance' sends source 'businessKey', which is string, but the catalog entry of '{Name}' defines it as number.",
                $"Signal end event #7 payload entry 'caseKey' must set required, because the catalog entry of '{Name}' requires it."
            ],
            Problems(optionalKey, SignalCatalogStage.Publication, SignalCatalogModes.Optional, entry));
    }

    [Fact]
    public void Throw_EveryDeclarationOfAVariableMustFit()
    {
        var entry = Entry([new SignalCatalogFieldDto("count", WorkflowVariableTypes.Number, false, false)]);
        var model = ThrowModel(BpmnFlowNodeTypes.IntermediateSignalThrowEvent,
            [Variable("total", WorkflowVariableTypes.Number)],
            Send("count", "total"));
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 2,
            Name = "Ask",
            Type = BpmnFlowNodeTypes.StartEvent,
            Variables = [new VariableModel { Id = 9, Name = "total", DataType = WorkflowVariableTypes.String }]
        });
        Assert.Equal(
            [$"Signal throw event #7 payload entry 'count' reads 'total', declared as string, but the catalog entry of '{Name}' defines it as number."],
            Problems(model, SignalCatalogStage.Draft, SignalCatalogModes.Optional, entry));
    }

    [Theory]
    [InlineData(BpmnFlowNodeTypes.IntermediateSignalThrowEvent, "Signal throw event #7")]
    [InlineData(BpmnFlowNodeTypes.SignalEndEvent, "Signal end event #7")]
    public void Throw_AVariableThatCanBeNullMustBeSentAsRequired(string type, string label)
    {
        var entry = Entry([
            new SignalCatalogFieldDto("reason", WorkflowVariableTypes.String, false, true),
            new SignalCatalogFieldDto("note", WorkflowVariableTypes.String, false, false),
            new SignalCatalogFieldDto("details", WorkflowVariableTypes.Json, false, false),
            new SignalCatalogFieldDto("count", WorkflowVariableTypes.Number, false, false)
        ]);
        WorkflowModel Model(bool required)
        {
            var model = ThrowModel(type,
                [
                    Variable("why", WorkflowVariableTypes.String),
                    Variable("remark", WorkflowVariableTypes.String),
                    Variable("total", WorkflowVariableTypes.Number, nullable: false)
                ],
                Send("reason", "why"),
                Send("note", "remark", required: required),
                Send("details", "extra", required: required),
                Send("count", "total"));
            // An optional scalar json input can hold null too.
            model.FlowNodes.Single(node => node.Id == 1).Variables =
                [new VariableModel { Id = 20, Name = "extra", DataType = WorkflowVariableTypes.Json }];
            return model;
        }

        var optional = Model(required: false);
        Assert.Empty(Problems(optional, SignalCatalogStage.Draft, SignalCatalogModes.Optional, entry));
        // A required field already needs required, so its entry gets that one message only.
        var expected = new[]
        {
            $"{label} payload entry 'reason' must set required, because the catalog entry of '{Name}' requires it.",
            $"{label} payload entry 'note' must set required, because variable 'remark' can be null and the catalog entry of '{Name}' accepts no null field.",
            $"{label} payload entry 'details' must set required, because variable 'extra' can be null and the catalog entry of '{Name}' accepts no null field."
        };
        Assert.Equal(expected, Problems(optional, SignalCatalogStage.Publication, SignalCatalogModes.Optional, entry));
        Assert.Equal(expected, Problems(optional, SignalCatalogStage.Reactivation, SignalCatalogModes.Optional, entry));
        Assert.All(SignalCatalogContract.FindDefinitionProblems(optional, Entries(entry), SignalCatalogModes.Optional, SignalCatalogStage.Publication),
            problem => Assert.False(problem.IsSaveProblem));

        Assert.Equal([expected[0]], Problems(Model(required: true), SignalCatalogStage.Publication, SignalCatalogModes.Optional, entry));
    }

    [Theory]
    [MemberData(nameof(SignalEventTypes))]
    public void ArchivedEntry_IsASaveProblemForEverySignalEventAtEveryStage(string type, string label)
    {
        var entry = Entry(RecallFields, archived: true);
        var model = SignalEventModel(type);
        foreach (var stage in Enum.GetValues<SignalCatalogStage>())
        {
            Assert.Equal(
                [$"{label} uses signal '{Name}', whose catalog entry is archived."],
                Problems(model, stage, SignalCatalogModes.Optional, entry));
        }

        Assert.True(Assert.Single(SignalCatalogContract.FindDefinitionProblems(
            model, Entries(entry), SignalCatalogModes.Required, SignalCatalogStage.Draft)).IsSaveProblem);
    }

    [Theory]
    [MemberData(nameof(SignalEventTypes))]
    public void RequiredMode_AppliesAtPublicationOnly(string type, string label)
    {
        var model = SignalEventModel(type);
        var problem = Assert.Single(SignalCatalogContract.FindDefinitionProblems(
            model, Entries(), SignalCatalogModes.Required, SignalCatalogStage.Publication));
        Assert.Equal(
            $"{label} uses signal '{Name}', which has no entry in the signal catalog; this deployment requires one before publication.",
            problem.Message);
        Assert.False(problem.IsSaveProblem);
        Assert.Empty(Problems(model, SignalCatalogStage.Draft, SignalCatalogModes.Required));
        Assert.Empty(Problems(model, SignalCatalogStage.Reactivation, SignalCatalogModes.Required));
        Assert.Empty(Problems(model, SignalCatalogStage.Publication, SignalCatalogModes.Optional));
        Assert.Empty(Problems(model, SignalCatalogStage.Publication, SignalCatalogModes.Required, Entry([])));
    }

    [Fact]
    public void Problems_FollowNodeOrderAndOnlyNameLimitsTheCheck()
    {
        var model = ReceiverModel(BpmnFlowNodeTypes.IntermediateSignalCatchEvent,
            Mapping("x", "missing", WorkflowVariableTypes.String));
        model.FlowNodes.Insert(0, new FlowNodeModel
        {
            Id = 12,
            Name = "Other",
            Type = BpmnFlowNodeTypes.IntermediateSignalCatchEvent,
            Signal = new SignalEventModel { Name = "other.signal", OutputMappings = [Mapping("y", "nope", WorkflowVariableTypes.String)] }
        });
        model.FlowNodes.Insert(0, new FlowNodeModel
        {
            Id = 2,
            Name = "Invalid name",
            Type = BpmnFlowNodeTypes.IntermediateSignalCatchEvent,
            Signal = new SignalEventModel { Name = "Not Valid" }
        });
        var entries = Entries(Entry([]), Entry([], name: "other.signal"));
        var all = SignalCatalogContract.FindDefinitionProblems(model, entries, SignalCatalogModes.Optional, SignalCatalogStage.Draft);
        Assert.Equal([4, 12], all.Select(problem => problem.NodeId));
        var one = SignalCatalogContract.FindDefinitionProblems(model, entries, SignalCatalogModes.Optional, SignalCatalogStage.Draft, "other.signal");
        Assert.Equal(12, Assert.Single(one).NodeId);
        Assert.Equal(["other.signal", Name], SignalCatalogContract.SignalNames(model));
    }

    [Fact]
    public void NoSignalEvents_NoNamesAndNoProblems()
    {
        var model = new WorkflowModel
        {
            Id = "plain",
            Name = "Plain",
            FlowNodes = [new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent }]
        };
        Assert.Empty(SignalCatalogContract.SignalNames(model));
        Assert.Empty(Problems(model, SignalCatalogStage.Publication, SignalCatalogModes.Required));
    }

    [Fact]
    public void UsageCounts_AnyOneCountBlocksArchiving()
    {
        // Waits and family starts never occur alone through the API, so each count is checked here.
        Assert.True(new SignalCatalogUsageCounts(0, 0, 0, 0, 0).IsUnused);
        for (var index = 0; index < 5; index++)
        {
            var counts = new long[5];
            counts[index] = 1;
            Assert.False(new SignalCatalogUsageCounts(counts[0], counts[1], counts[2], counts[3], counts[4]).IsUnused);
        }
    }

    // ---------------------------------------------------------------- access

    [Fact]
    public void Access_RolesNarrowUsersAndAllowClientsGatesClients()
    {
        var entry = Entry([], roles: ["recall-manager", "auditor"], allowClients: false);
        var user = new Flowbit.Service.Abstractions.SignalCaller(SignalThrowerKinds.User, "bob", ["admin"], null);
        var manager = user with { Roles = ["admin", "Recall-Manager"] };
        var client = new Flowbit.Service.Abstractions.SignalCaller(SignalThrowerKinds.Client, "erp", [], null);
        Assert.Equal(
            $"Throwing signal '{Name}' needs one of the roles its catalog entry lists: recall-manager, auditor.",
            SignalCatalogAccess.Refusal(entry, user, SignalCatalogAccess.Throwing));
        Assert.Equal(
            $"Cancelling signal '{Name}' needs one of the roles its catalog entry lists: recall-manager, auditor.",
            SignalCatalogAccess.Refusal(entry, user, SignalCatalogAccess.Cancelling));
        Assert.Null(SignalCatalogAccess.Refusal(entry, manager, SignalCatalogAccess.Retrying));
        Assert.Equal(
            $"Signal '{Name}' cannot be thrown by an API client; its catalog entry does not allow clients.",
            SignalCatalogAccess.Refusal(entry, client, SignalCatalogAccess.Throwing));
        Assert.Null(SignalCatalogAccess.Refusal(entry with { AllowClients = true }, client, SignalCatalogAccess.Throwing));
        Assert.Null(SignalCatalogAccess.Refusal(Entry([]), user, SignalCatalogAccess.Throwing));
    }

    [Fact]
    public void CanThrow_ForEachCallerKind()
    {
        var open = Entry([]);
        var narrowed = Entry([], roles: ["recall-manager"], allowClients: false);
        string[] signalRoles = ["admin", "signals"];
        var signalUser = new Flowbit.Service.Abstractions.SignalCatalogCaller(false, "u", ["signals"], false);
        var outsider = signalUser with { Roles = ["workflow-admin"] };
        var manager = signalUser with { Roles = ["signals", "recall-manager"] };
        var managerOnly = signalUser with { Roles = ["recall-manager"] };
        var client = new Flowbit.Service.Abstractions.SignalCatalogCaller(true, "erp", [], true);
        var readOnlyClient = client with { HasThrowScope = false };

        Assert.True(SignalCatalogAccess.CanThrow(open, signalUser, signalRoles));
        Assert.False(SignalCatalogAccess.CanThrow(open, outsider, signalRoles));
        Assert.False(SignalCatalogAccess.CanThrow(narrowed, signalUser, signalRoles));
        Assert.True(SignalCatalogAccess.CanThrow(narrowed, manager, signalRoles));
        Assert.False(SignalCatalogAccess.CanThrow(narrowed, managerOnly, signalRoles));
        Assert.True(SignalCatalogAccess.CanThrow(open, client, signalRoles));
        Assert.False(SignalCatalogAccess.CanThrow(open, readOnlyClient, signalRoles));
        Assert.False(SignalCatalogAccess.CanThrow(narrowed, client, signalRoles));
        Assert.False(SignalCatalogAccess.CanThrow(open with { Status = SignalCatalogStatuses.Archived }, signalUser, signalRoles));
    }
}
