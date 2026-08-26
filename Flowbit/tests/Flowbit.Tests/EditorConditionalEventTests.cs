using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Jint;
using Xunit;

namespace Flowbit.Tests;

public sealed class EditorConditionalEventTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("atomic")]
    [InlineData("durableAsync")]
    public void Validator_AcceptsConditionalCatchEventDeliveryModes(string? deliveryMode)
    {
        var candidate = ValidCandidate(deliveryMode);

        var errors = Validate(candidate);

        Assert.Empty(errors);
    }

    [Fact]
    public void Validator_RejectsMalformedConditionalCatchEventContract()
    {
        var missingIncoming = ValidCandidate();
        ((JsonArray)missingIncoming["sequenceFlows"]!).RemoveAt(0);
        Assert.Contains(
            "Conditional catch event #2 must have at least one incoming sequence flow.",
            Validate(missingIncoming));

        var multipleOutgoing = ValidCandidate();
        ((JsonArray)multipleOutgoing["sequenceFlows"]!).Add(Flow(103, 2, 3));
        Assert.Contains(
            "Conditional catch event #2 must have exactly one outgoing sequence flow.",
            Validate(multipleOutgoing));

        var conditionalOutgoing = ValidCandidate();
        ((JsonObject)((JsonArray)conditionalOutgoing["sequenceFlows"]!)[1]!)["condition"] = "ready";
        Assert.Contains(
            "Conditional catch event #2 must have one unconditional outgoing sequence flow without user-action or multi-instance metadata.",
            Validate(conditionalOutgoing));

        var blankCondition = ValidCandidate();
        ((JsonObject)((JsonArray)blankCondition["flowNodes"]!)[1]!["conditional"]!)["condition"] = "  ";
        Assert.Contains(
            "Conditional catch event #2 condition must not be blank.",
            Validate(blankCondition));

        var unknownMode = ValidCandidate("eventual");
        Assert.Contains(
            "Conditional catch event #2 deliveryMode must be atomic or durableAsync.",
            Validate(unknownMode));
    }

    [Fact]
    public void Normalizer_OmitsAtomicAndCanonicalizesDurableAsync()
    {
        var engine = CreateConditionalNormalizerEngine();

        var atomic = engine.Evaluate(
            "JSON.stringify(normalizeConditionalDefinition({ condition: 'ready', deliveryMode: 'ATOMIC' }))")
            .AsString();
        var durable = engine.Evaluate(
            "JSON.stringify(normalizeConditionalDefinition({ condition: 'ready', deliveryMode: 'DURABLEASYNC' }))")
            .AsString();

        Assert.Equal("{\"condition\":\"ready\"}", atomic);
        Assert.Equal("{\"condition\":\"ready\",\"deliveryMode\":\"durableAsync\"}", durable);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("atomic")]
    [InlineData("durableAsync")]
    public void Validator_RejectsSharedVariableDependenciesForEveryDeliveryMode(
        string? deliveryMode)
    {
        var candidate = ValidCandidate(deliveryMode);
        AddSharedVariable(candidate, "approvalSignal", "approvals.signal", "read");
        var conditional = (JsonObject)((JsonArray)candidate["flowNodes"]!)[1]!["conditional"]!;
        conditional["condition"] =
            "'[approvalSignal]' == '[approvalSignal]' and approved == true " +
            "and [APPROVALSIGNAL] == true";

        Assert.Contains(
            "Conditional catch event #2 condition cannot reference shared process variable " +
            "'approvalSignal'; copy the value into an instance variable or use a message event.",
            Validate(candidate));
    }

    [Fact]
    public void Validator_DoesNotTreatQuotedSharedAliasTextAsDependency()
    {
        var candidate = ValidCandidate();
        AddSharedVariable(candidate, "approvalSignal", "approvals.signal", "readWrite");
        var conditional = (JsonObject)((JsonArray)candidate["flowNodes"]!)[1]!["conditional"]!;
        conditional["condition"] =
            "approved == true and 'approvalSignal' == 'approvalSignal'";

        Assert.Empty(Validate(candidate));
    }

    [Fact]
    public void Validator_RequiresExplicitSharedAccessAndRejectsNestedBindings()
    {
        var candidate = ValidCandidate("durableAsync");
        var shared = new JsonObject
        {
            ["id"] = 1,
            ["name"] = "signal",
            ["dataType"] = "boolean",
            ["isArray"] = false,
            ["nullable"] = true,
            ["required"] = false,
            ["defaultValue"] = null,
            ["validation"] = null,
            ["scope"] = "shared",
            ["sharedKey"] = "events.signal"
        };
        ((JsonArray)candidate["variables"]!).Add(shared);

        Assert.Contains(
            "Shared process variable 'signal' must explicitly set access to read or readWrite.",
            Validate(candidate));

        shared["access"] = "readWrite";
        var nested = new JsonObject
        {
            ["id"] = 1,
            ["name"] = "nested",
            ["dataType"] = "string",
            ["isArray"] = false,
            ["required"] = false,
            ["defaultValue"] = null,
            ["scope"] = "shared",
            ["sharedKey"] = "invalid.nested",
            ["access"] = "readWrite"
        };
        ((JsonArray)((JsonArray)candidate["flowNodes"]!)[1]!["variables"]!).Add(nested);

        Assert.Contains(Validate(candidate), error =>
            error.Contains("bind shared variables only at workflow level", StringComparison.Ordinal));
    }

    [Fact]
    public void Validator_RejectsReadOnlySharedAssignmentTarget()
    {
        var candidate = ValidCandidate();
        var shared = new JsonObject
        {
            ["id"] = 1,
            ["name"] = "signal",
            ["dataType"] = "boolean",
            ["isArray"] = false,
            ["nullable"] = false,
            ["required"] = false,
            ["defaultValue"] = null,
            ["validation"] = null,
            ["scope"] = "shared",
            ["sharedKey"] = "events.signal",
            ["access"] = "read"
        };
        ((JsonArray)candidate["variables"]!).Add(shared);
        var task = (JsonObject)((JsonArray)candidate["flowNodes"]!)[1]!;
        task["type"] = "scriptTask";
        task.Remove("conditional");
        task["scriptFormat"] = "ncalc";
        task["assignments"] = new JsonArray(new JsonObject
        {
            ["variable"] = "signal",
            ["expression"] = "true"
        });

        Assert.Contains(Validate(candidate), error =>
            error.Contains("Script task #2", StringComparison.Ordinal) &&
            error.Contains("cannot assign read-only shared variable 'signal'", StringComparison.Ordinal));

        shared["access"] = "readWrite";
        Assert.DoesNotContain(Validate(candidate), error =>
            error.Contains("read-only shared variable", StringComparison.Ordinal));
    }

    [Fact]
    public void EditorSource_ContainsInstanceOnlyConditionalGuidanceAndBpmnMarker()
    {
        var html = ReadEditorSource();

        Assert.Contains(
            "CONDITIONAL_CATCH_EVENT: \"intermediateConditionalCatchEvent\"",
            html,
            StringComparison.Ordinal);
        Assert.Contains("id=\"icon-conditional\"", html, StringComparison.Ordinal);
        Assert.Contains(
            "isMessageCatchEventType(node.type) || isTimerCatchEventType(node.type) ||\n          isConditionalCatchEventType(node.type)",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "if (node.type !== \"intermediateConditionalCatchEvent\") delete node.conditional;",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "Conditional events cannot reference shared process variable",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "their conditions may reference instance variables only",
            html,
            StringComparison.Ordinal);
        Assert.DoesNotContain("Insert shared dependency", html, StringComparison.Ordinal);
    }

    private static JsonObject ValidCandidate(string? deliveryMode = null)
    {
        var conditional = new JsonObject { ["condition"] = "approved == true" };
        if (deliveryMode is not null)
        {
            conditional["deliveryMode"] = deliveryMode;
        }

        return new JsonObject
        {
            ["id"] = "conditional-editor-test",
            ["name"] = "Conditional editor test",
            ["initialEventId"] = 1,
            ["variables"] = new JsonArray(),
            ["lanes"] = new JsonArray(),
            ["flowNodes"] = new JsonArray(
                Node(1, "Start", "startEvent"),
                Node(2, "Wait", "intermediateConditionalCatchEvent", conditional),
                Node(3, "Done", "endEvent")),
            ["sequenceFlows"] = new JsonArray(
                Flow(101, 1, 2),
                Flow(102, 2, 3)),
            ["cancelRoles"] = new JsonArray(),
            ["unclaimRoles"] = new JsonArray(),
            ["taskAssignmentRoles"] = new JsonArray()
        };
    }

    private static JsonObject Node(
        int id,
        string name,
        string type,
        JsonObject? conditional = null)
    {
        var node = new JsonObject
        {
            ["id"] = id,
            ["name"] = name,
            ["type"] = type,
            ["attributes"] = new JsonArray(),
            ["roles"] = new JsonArray(),
            ["variables"] = new JsonArray(),
            ["requiresClaim"] = false,
            ["claimMode"] = "fresh",
            ["requiresAssignment"] = false,
            ["assignmentMode"] = "fresh"
        };
        if (conditional is not null)
        {
            node["conditional"] = conditional;
        }
        return node;
    }

    private static JsonObject Flow(int id, int sourceRef, int targetRef) => new()
    {
        ["id"] = id,
        ["name"] = string.Empty,
        ["sourceRef"] = sourceRef,
        ["targetRef"] = targetRef,
        ["attributes"] = new JsonArray(),
        ["roles"] = new JsonArray(),
        ["variables"] = new JsonArray(),
        ["condition"] = null,
        ["conditionPriority"] = null,
        ["isDefault"] = false,
        ["isSelectable"] = true,
        ["canActWithoutClaim"] = false,
        ["canActWithoutClaimRoles"] = new JsonArray(),
        ["completionCondition"] = null,
        ["completionPriority"] = null,
        ["cancelRemainingInstances"] = false
    };

    private static void AddSharedVariable(
        JsonObject candidate,
        string name,
        string sharedKey,
        string access)
    {
        ((JsonArray)candidate["variables"]!).Add(new JsonObject
        {
            ["id"] = 1,
            ["name"] = name,
            ["dataType"] = "boolean",
            ["isArray"] = false,
            ["nullable"] = false,
            ["required"] = false,
            ["defaultValue"] = null,
            ["validation"] = null,
            ["scope"] = "shared",
            ["sharedKey"] = sharedKey,
            ["access"] = access
        });
    }

    private static IReadOnlyList<string> Validate(JsonObject candidate)
    {
        var engine = CreateValidatorEngine();
        engine.SetValue("candidateJson", candidate.ToJsonString());
        var resultJson = engine.Evaluate(
            "JSON.stringify(validateModelForSave(JSON.parse(candidateJson)))").AsString();
        return JsonSerializer.Deserialize<List<string>>(resultJson) ?? [];
    }

    private static Engine CreateValidatorEngine()
    {
        var html = ReadEditorSource();
        var validator = Regex.Match(
            html,
            @"// BEGIN WORKFLOW SAVE VALIDATOR(?<code>[\s\S]*?)// END WORKFLOW SAVE VALIDATOR");
        Assert.True(validator.Success, "The marked workflow save validator was not found.");
        var normalizeRoles = Regex.Match(
            html,
            @"function normalizeRoles\(roles\) \{[\s\S]*?(?=function normalizeAttributesForLoad)");
        Assert.True(normalizeRoles.Success, "The role-normalization helper was not found.");

        var engine = new Engine();
        engine.Execute(normalizeRoles.Value);
        engine.Execute(validator.Groups["code"].Value);
        return engine;
    }

    private static Engine CreateConditionalNormalizerEngine()
    {
        var html = ReadEditorSource();
        var canonicalizer = Regex.Match(
            html,
            @"function canonicalizeKnownValue\(value, supported, fallback\) \{[\s\S]*?\n\}");
        var normalizer = Regex.Match(
            html,
            @"function normalizeConditionalDefinition\(value\) \{[\s\S]*?(?=function normalizeMultiInstanceForLoad)");
        Assert.True(canonicalizer.Success, "The enum canonicalizer was not found.");
        Assert.True(normalizer.Success, "The conditional normalizer was not found.");

        var engine = new Engine();
        engine.Execute("const CONDITIONAL_DELIVERY_MODE = { ATOMIC: 'atomic', DURABLE_ASYNC: 'durableAsync' };");
        engine.Execute(canonicalizer.Value);
        engine.Execute(normalizer.Value);
        return engine;
    }

    private static string ReadEditorSource()
    {
        var editorPath = Path.Combine(
            AppContext.BaseDirectory,
            "Fixtures",
            "flowbit-editor.html");
        return File.ReadAllText(editorPath);
    }
}
