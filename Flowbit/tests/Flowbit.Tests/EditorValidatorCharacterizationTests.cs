using System.Text.Json;
using System.Text.RegularExpressions;
using Jint;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// Characterization tests for the editor save validator extraction. They pin the
/// complete ordered error arrays of multi-phase models, per-call context
/// isolation, and input non-mutation. Expected values were recorded from the
/// implementation; they are not intended as authored rule documentation.
/// </summary>
public sealed class EditorValidatorCharacterizationTests
{
    private const string MultiPhaseErrorModelJson = """
{
  "id": "",
  "name": "Multi-error ordering probe",
  "initialEventId": 99,
  "taskAssignmentRoles": null,
  "taskDistribution": { "clientId": "", "clientSecret": "" },
  "variables": [
    { "id": 1, "name": "Approval", "dataType": "number", "isArray": false, "nullable": true, "required": false, "defaultValue": 5, "validation": null },
    { "id": 2, "name": "approval", "dataType": "string", "isArray": false, "nullable": false, "defaultValue": null, "validation": null },
    { "id": 3, "name": "", "dataType": "number", "isArray": false, "nullable": false, "defaultValue": null, "validation": null },
    { "id": 4, "name": "scopeVar", "scope": "instance", "sharedKey": "k", "access": "read", "dataType": "string", "isArray": false, "nullable": false, "defaultValue": null, "validation": null },
    { "id": 5, "name": "sharedVar", "scope": "shared", "sharedKey": "gateway.x", "access": "maybe", "required": true, "defaultValue": "x", "dataType": "string", "isArray": false, "nullable": false, "validation": null }
  ],
  "lanes": [],
  "flowNodes": [
    { "id": 1, "name": "Start", "type": "startEvent", "laneId": null, "x": 0, "y": 0, "roles": [], "attributes": [{ "key": "k", "value": "v" }, { "key": "K ", "value": "v2" }, { "key": "k", "value": "v3" }, { "key": "", "value": 5 }], "variables": [{ "id": 1, "name": "dup", "dataType": "number", "isArray": false, "required": false }, { "id": 2, "name": "DUP", "dataType": "number", "isArray": false, "required": false }] },
    { "id": 2, "name": "Task", "type": "task", "laneId": null, "x": 0, "y": 0, "roles": [], "asyncBefore": "yes", "job": { "failureHandling": "wrong", "retryDelays": [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10] }, "timer": { "timeDuration": "PT1H" }, "cancelActivity": true },
    { "id": 3, "name": "Xor", "type": "exclusiveGateway", "laneId": null, "x": 0, "y": 0, "roles": [], "assignee": "someone", "message": {} },
    { "id": 4, "name": "End", "type": "endEvent", "laneId": null, "x": 0, "y": 0, "roles": [] },
    { "id": 5, "name": "End", "type": "endEvent", "laneId": null, "x": 0, "y": 0, "roles": [] }
  ],
  "sequenceFlows": [
    { "id": 101, "name": "", "sourceRef": 1, "targetRef": 3, "roles": ["Manager"], "variables": [{ "id": 1, "name": "sharedVar", "scope": "shared", "dataType": "number", "isArray": false, "required": false }], "attributes": [] },
    { "id": 202, "name": "A", "sourceRef": 3, "targetRef": 4, "roles": [], "variables": [], "attributes": [], "condition": "sharedVar > 1", "conditionPriority": 1 },
    { "id": 203, "name": "B", "sourceRef": 3, "targetRef": 4, "roles": [], "variables": [], "attributes": [], "condition": "", "conditionPriority": 1 }
  ]
}
""";

    private const string GatewaySharedRoleMultiInstanceModelJson = """
{
  "id": "probe-2",
  "name": "Gateway/shared/role/MI probe",
  "initialEventId": 1,
  "variables": [
    { "id": 1, "name": "votes", "dataType": "json", "isArray": false, "nullable": false, "defaultValue": { "a": 1 }, "validation": null },
    { "id": 2, "name": "roList", "scope": "shared", "sharedKey": "cat.ro", "access": "read", "dataType": "string", "isArray": true, "nullable": false, "validation": null },
    { "id": 3, "name": "rwList", "scope": "shared", "sharedKey": "cat.rw", "access": "readWrite", "dataType": "number", "isArray": true, "nullable": false, "validation": null }
  ],
  "flowNodes": [
    { "id": 1, "name": "Start", "type": "startEvent", "variables": [{ "id": 1, "name": "roList", "dataType": "string", "isArray": true, "required": true }] },
    { "id": 2, "name": "Parallel", "type": "parallelGateway" },
    { "id": 3, "name": "Roles task", "type": "userTask", "roles": ["Clerk"], "rolesVariable": "roList" },
    { "id": 4, "name": "MI", "type": "userTask", "multiInstance": { "mode": "parallel", "source": "collection", "collectionVariable": "rwList", "resultVariable": "votes", "completionEvaluation": "later" } },
    { "id": 9, "name": "End", "type": "endEvent" }
  ],
  "sequenceFlows": [
    { "id": 101, "sourceRef": 1, "targetRef": 2 },
    { "id": 201, "sourceRef": 2, "targetRef": 3, "condition": "true", "conditionPriority": 1 },
    { "id": 202, "sourceRef": 2, "targetRef": 4 },
    { "id": 401, "sourceRef": 4, "targetRef": 9, "name": "Yes", "isDefault": true, "roles": ["X"], "condition": "a > 0", "completionCondition": "CountFlow(401) > 0", "completionPriority": 1 },
    { "id": 402, "sourceRef": 4, "targetRef": 9, "name": "No", "completionPriority": 2, "completionCondition": "CountFlow(999) > 0" }
  ]
}
""";

    private const string CleanModelJson = """
{
  "id": "reuse-a",
  "name": "First",
  "initialEventId": 1,
  "flowNodes": [{ "id": 1, "name": "Start", "type": "startEvent" }, { "id": 2, "name": "End", "type": "endEvent" }],
  "sequenceFlows": [{ "id": 101, "sourceRef": 1, "targetRef": 2 }]
}
""";

    private const string EmptyModelJson = "{}";

    [Fact]
    public void Validator_PreservesCompleteOrderedErrorArrayAcrossPhases()
    {
        var errors = ValidateJson(MultiPhaseErrorModelJson);

        var expected = new[]
        {
            "Flow node #1 has duplicate attribute key 'K'; keys are compared case-insensitively after trimming.",
            "Flow node #1 has duplicate attribute key 'k'; keys are compared case-insensitively after trimming.",
            "Flow node #1 attribute #4 key must not be blank.",
            "Flow node #1 attribute #4 value must be a non-null string.",
            "Workflow id is required and is used as the stable workflow key.",
            "Workflow taskDistribution must have a clientId when enabled.",
            "Workflow taskDistribution must have a clientSecret when enabled.",
            "Variable name 'approval' is duplicated on process variables; names are case-insensitive.",
            "Variable name is required on process variables.",
            "Variable name 'DUP' is duplicated on flow node #1; names are case-insensitive.",
            "Instance process variable 'scopeVar' cannot define sharedKey or access.",
            "Shared process variable 'sharedVar' sharedKey 'gateway.x' uses a reserved context prefix.",
            "Shared process variable 'sharedVar' must explicitly set access to read or readWrite.",
            "Shared process variable 'sharedVar' cannot be required.",
            "Shared process variable 'sharedVar' cannot define defaultValue.",
            "Variable 'sharedVar' on sequence flow #101 cannot define scope, sharedKey, or access; bind shared variables only at workflow level.",
            "Process variable 'approval' must have a defaultValue.",
            "Process variable 'scopeVar' must have a defaultValue.",
            "Shared process variable 'sharedVar' cannot define a defaultValue.",
            "Sequence flow #101 variable 'sharedVar' cannot target a read-only shared variable.",
            "Flow node #2 asyncBefore must be a boolean.",
            "Flow node #2 job requires asyncBefore or asyncAfter.",
            "Flow node #2 job failureHandling must be boundaryFirst or retryFirst.",
            "Flow node #2 job retryDelays may contain at most 10 entries.",
            "Flow node #2 job retryDelays[0] must be a positive fixed-unit ISO-8601 duration.",
            "Flow node #2 job retryDelays[1] must be a positive fixed-unit ISO-8601 duration.",
            "Flow node #2 job retryDelays[2] must be a positive fixed-unit ISO-8601 duration.",
            "Flow node #2 job retryDelays[3] must be a positive fixed-unit ISO-8601 duration.",
            "Flow node #2 job retryDelays[4] must be a positive fixed-unit ISO-8601 duration.",
            "Flow node #2 job retryDelays[5] must be a positive fixed-unit ISO-8601 duration.",
            "Flow node #2 job retryDelays[6] must be a positive fixed-unit ISO-8601 duration.",
            "Flow node #2 job retryDelays[7] must be a positive fixed-unit ISO-8601 duration.",
            "Flow node #2 job retryDelays[8] must be a positive fixed-unit ISO-8601 duration.",
            "Flow node #2 job retryDelays[9] must be a positive fixed-unit ISO-8601 duration.",
            "Flow node #2 job retryDelays[10] must be a positive fixed-unit ISO-8601 duration.",
            "Flow node #2 defines a timer but is not a timer event.",
            "Flow node #2 defines cancelActivity but is not a timer or conditional boundary event.",
            "Workflow initialEventId must reference an existing startEvent.",
            "The outgoing sequence flow from start event #1 must be unconditional and cannot define action or multi-instance metadata.",
            "End event #5 must have at least one incoming sequence flow.",
            "Exclusive gateway #3 can define only gateway configuration and normal node identity/layout fields.",
            "Exclusive gateway #3 split must have exactly one default outgoing sequence flow.",
            "Sequence flow #203 leaving exclusive gateway #3 must define a condition.",
            "Exclusive gateway #3 has duplicate conditionPriority 1."
        };
        Assert.Equal(expected, errors);
    }

    [Fact]
    public void Validator_PreservesGatewaySharedRoleSourceAndMultiInstanceErrorOrder()
    {
        var errors = ValidateJson(GatewaySharedRoleMultiInstanceModelJson);

        var expected = new[]
        {
            "Flow node #3 cannot combine rolesVariable with nonempty literal roles.",
            "Flow node #1 variable 'roList' cannot target a read-only shared variable.",
            "Entry event #1 cannot write read-only shared variable 'roList'.",
            "Sequence flow #201 leaving parallel gateway #2 must be unconditional and cannot define default or condition metadata.",
            "Sequence flow #201 can define conditionPriority only when leaving an Exclusive gateway split.",
            "User task #4 (MI) has unsupported completionEvaluation 'later'.",
            "User task #4 (MI) resultVariable must reference a writable scalar JSON process variable whose defaultValue is a JSON array for instance scope.",
            "User task #4 (MI) collectionVariable must reference a declared string[] process variable.",
            "User task #4 (MI) collection source must use requiresClaim=false and claimMode='fresh'.",
            "Sequence flow #401 must be a pure engine-only default with no actor or completion settings.",
            "Sequence flow #402 completionCondition references a non-selectable outcome flow."
        };
        Assert.Equal(expected, errors);
    }

    [Fact]
    public void Validator_LeavesTheSuppliedCandidateUnchanged()
    {
        var result = ValidateAndReportMutation(MultiPhaseErrorModelJson);
        Assert.True(result.Unchanged, "validateModelForSave mutated its input.");

        result = ValidateAndReportMutation(GatewaySharedRoleMultiInstanceModelJson);
        Assert.True(result.Unchanged, "validateModelForSave mutated its input.");

        result = ValidateAndReportMutation("{}");
        Assert.True(result.Unchanged, "validateModelForSave mutated its input.");
    }

    [Fact]
    public void Validator_RepeatedCallsForDifferentModelsDoNotLeakContext()
    {
        Assert.Empty(ValidateJson(CleanModelJson));

        var dirty = ValidateJson(MultiPhaseErrorModelJson);
        Assert.NotEmpty(dirty);

        var afterDirty = ValidateJson(CleanModelJson);
        Assert.Empty(afterDirty);

        var emptyAfterDirty = ValidateJson(EmptyModelJson);
        Assert.Equal(
            new[]
            {
                "Workflow id is required and is used as the stable workflow key.",
                "Workflow must have at least one entry event (startEvent, messageStartEvent, or timerStartEvent)."
            },
            emptyAfterDirty);
    }

    private static ValidationRun ValidateAndReportMutation(string json)
    {
        var engine = CreateValidatorEngine();
        engine.SetValue("candidateJson", json);
        var resultJson = engine.Evaluate(
            """
            (() => {
              const candidate = JSON.parse(candidateJson);
              const before = JSON.stringify(candidate);
              const errors = validateModelForSave(candidate);
              const after = JSON.stringify(candidate);
              return JSON.stringify({ unchanged: before === after, errors });
            })()
            """).AsString();
        using var document = JsonDocument.Parse(resultJson);
        return new ValidationRun(
            document.RootElement.GetProperty("unchanged").GetBoolean(),
            document.RootElement.GetProperty("errors").EnumerateArray()
                .Select(value => value.GetString() ?? "")
                .ToArray());
    }

    private static IReadOnlyList<string> ValidateJson(string json)
    {
        var engine = CreateValidatorEngine();
        engine.SetValue("candidateJson", json);
        var resultJson = engine.Evaluate(
            "JSON.stringify(validateModelForSave(JSON.parse(candidateJson)))").AsString();
        return JsonSerializer.Deserialize<List<string>>(resultJson) ?? [];
    }

    private static Engine CreateValidatorEngine()
    {
        var html = ReadEditorSource();
        var match = Regex.Match(
            html,
            @"// BEGIN WORKFLOW SAVE VALIDATOR(?<code>[\s\S]*?)// END WORKFLOW SAVE VALIDATOR");
        Assert.True(match.Success, "The marked workflow save validator was not found.");
        var normalizeRoles = Regex.Match(
            html,
            @"function normalizeRoles\(roles\) \{[\s\S]*?(?=function normalizeAttributesForLoad)");
        Assert.True(normalizeRoles.Success, "The role-normalization helper was not found.");

        var engine = new Engine();
        engine.Execute(normalizeRoles.Value);
        engine.Execute(match.Groups["code"].Value);
        return engine;
    }

    private static string ReadEditorSource()
    {
        var editorPath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "flowbit-editor.html");
        return File.ReadAllText(editorPath);
    }

    private sealed record ValidationRun(bool Unchanged, IReadOnlyList<string> Errors);
}
