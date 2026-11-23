using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Jint;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// The rules for signal start events (EVENT-04C): the shared <see cref="SignalStartFixtures"/> through the server's
/// validator and the editor's save validator with the same first message, the settings only a raw request carries,
/// the predicates, normalization, and the listing that the family's subscriptions are built from.
/// </summary>
public sealed class SignalStartValidationTests
{
    public static TheoryData<string> CaseNames() => new(SignalStartFixtures.Cases().Select(item => item.Case));

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Server_ReportsEachFixtureWithItsFirstMessage(string name)
    {
        var (_, json, problem) = SignalStartFixtures.Cases().Single(item => item.Case == name);

        Assert.Equal(problem, SignalThrowValidationTests.ServerError(json));
    }

    [Theory]
    [MemberData(nameof(CaseNames))]
    public void Editor_ReportsEachFixtureWithTheServersFirstMessage(string name)
    {
        var (_, json, problem) = SignalStartFixtures.Cases().Single(item => item.Case == name);

        var errors = EditorErrors(json);

        if (problem is null)
        {
            Assert.Empty(errors);
        }
        else
        {
            Assert.NotEmpty(errors);
            Assert.Equal(problem, errors[0]);
        }
    }

    [Fact]
    public void Fixtures_HaveUniqueNames()
    {
        var names = SignalStartFixtures.Cases().Select(item => item.Case).ToList();
        Assert.Equal(names.Count, names.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData("roles")]
    [InlineData("variables")]
    [InlineData("message")]
    public void SettingsTheMigratorWouldDrop_AreRejectedOnARequest(string member)
    {
        var json = SignalStartFixtures.Json(model =>
        {
            var start = SignalStartFixtures.Node(model, 1);
            start[member] = member switch
            {
                "roles" => new JsonArray("Clerk"),
                "variables" => new JsonArray(new JsonObject
                {
                    ["id"] = 9, ["name"] = "extra", ["dataType"] = "string", ["isArray"] = false, ["required"] = false
                }),
                _ => new JsonObject { ["clientId"] = "x", ["clientSecret"] = "y", ["headerName"] = "X-A", ["headerValue"] = "z" }
            };
        });

        Assert.Equal(
            "Signal start event #1 cannot define task, role, variable, timer, conditional, message, async, or multi-instance settings.",
            SignalThrowValidationTests.ServerError(json));
    }

    [Fact]
    public void MappingRules_FollowTheMessageStartsWithTheirOwnNoun()
    {
        Assert.Equal(
            "Signal start event #1 output mapping for 'recallNumber' must declare dataType and isArray.",
            SignalThrowValidationTests.ServerError(SignalStartFixtures.Json(model =>
                SignalStartFixtures.Mappings(model, 1)[0]!.AsObject().Remove("dataType"))));
        Assert.Equal(
            "Signal start event #1 output mapping for 'recallNumber' must have a path unless defaultValue is configured.",
            SignalThrowValidationTests.ServerError(SignalStartFixtures.Json(model =>
                SignalStartFixtures.Mappings(model, 1)[0]!["path"] = "")));
        Assert.Equal(
            "Signal start event #1 output mapping default for 'reason' must be string.",
            SignalThrowValidationTests.ServerError(SignalStartFixtures.Json(model =>
                SignalStartFixtures.Mappings(model, 1)[1]!["defaultValue"] = 3)));
        Assert.Equal(
            "Signal start event #1 output mappings cannot contain null entries.",
            SignalThrowValidationTests.ServerError(SignalStartFixtures.Json(model =>
                SignalStartFixtures.Mappings(model, 1).Add(null))));
    }

    [Fact]
    public void BusinessKey_IsARequiredPathMappingAndEveryOtherEntryNeedsOne()
    {
        Assert.Equal(
            "Entry event #1 businessKey variable 'missing' is not a typed output mapping on the signal start event.",
            SignalThrowValidationTests.ServerError(SignalStartFixtures.Json(model =>
                SignalStartFixtures.Node(model, 1)["businessKey"] = new JsonObject { ["variable"] = "missing", ["uniqueness"] = "all" })));
        Assert.Equal(
            "Entry event #1 businessKey mapping 'reason' must be a required scalar string with an explicit path and no defaultValue.",
            SignalThrowValidationTests.ServerError(SignalStartFixtures.Json(model =>
                SignalStartFixtures.Node(model, 1)["businessKey"] = new JsonObject { ["variable"] = "reason", ["uniqueness"] = "all" })));
        Assert.Equal(
            "Entry event #1 must configure businessKey because business keys are enabled for this workflow.",
            SignalThrowValidationTests.ServerError(SignalStartFixtures.Json(model =>
            {
                SignalStartFixtures.Nodes(model).Add(new JsonObject
                {
                    ["id"] = 5, ["name"] = "Start", ["type"] = "startEvent",
                    ["variables"] = new JsonArray(new JsonObject
                    {
                        ["id"] = 51, ["name"] = "key", ["dataType"] = "string", ["isArray"] = false, ["required"] = true
                    }),
                    ["businessKey"] = new JsonObject { ["variable"] = "key", ["uniqueness"] = "all" }
                });
                SignalStartFixtures.Flows(model).Add(new JsonObject { ["id"] = 501, ["sourceRef"] = 5, ["targetRef"] = 2 });
                model["initialEventId"] = 5;
            })));
    }

    [Fact]
    public void SharedAliasReads_AreRejected()
    {
        Assert.Equal(
            "Signal start event #1 output mapping 'reason' cannot read shared variable alias 'rate' in its default or validation.",
            SignalThrowValidationTests.ServerError(SignalStartFixtures.Json(model =>
                SignalStartFixtures.Mappings(model, 1)[1]!["validation"] = "rate > 0")));
        var editor = EditorErrors(SignalStartFixtures.Json(model =>
            SignalStartFixtures.Mappings(model, 1)[1]!["validation"] = "rate > 0"));
        Assert.Contains(
            "Signal start event #1 output mapping 'reason' cannot read shared variable alias 'rate' in its default or validation.",
            editor);
    }

    /// <summary>
    /// Normalization leaves the members this change normalizes, a node's signal, business key, idempotency, and start
    /// variables, as every catalog example stores them, Example 05's signal start included, and a second pass changes
    /// nothing. Older canonicalizations of other members, such as a timer date's offset, are not this test's concern.
    /// </summary>
    [Theory]
    [MemberData(nameof(ExampleWorkflowData.All), MemberType = typeof(ExampleWorkflowData))]
    public void Normalization_LeavesTheEntryAndSignalMembersOfEveryExampleAsStored(string path)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var model = JsonSerializer.Deserialize<WorkflowModel>(ExampleWorkflowData.Read(path), options)!;
        string Members(WorkflowModel value) => string.Join("\n", value.FlowNodes.Select(node =>
        {
            var json = JsonSerializer.SerializeToNode(node, options)!.AsObject();
            return $"{node.Id} {json["signal"]?.ToJsonString()} {json["businessKey"]?.ToJsonString()} "
                + $"{json["idempotency"]?.ToJsonString()} {json["variables"]?.ToJsonString()}";
        }));
        var stored = Members(model);

        WorkflowModelMigrator.Normalize(model);
        Assert.Equal(stored, Members(model));
        var once = JsonSerializer.Serialize(model, options);
        WorkflowModelMigrator.Normalize(model);
        Assert.Equal(once, JsonSerializer.Serialize(model, options));
    }

    [Fact]
    public void CallOnly_IsRejectedOnASignalStart()
    {
        Assert.Equal(
            "Flow node #1 sets callOnly but is not a start event.",
            SignalThrowValidationTests.ServerError(SignalStartFixtures.Json(model =>
                SignalStartFixtures.Node(model, 1)["callOnly"] = true)));
    }

    /// <summary>
    /// Once the signal start configures a business key, a message start beside it must configure one too, and a timer
    /// start, which has no start values, need not.
    /// </summary>
    [Fact]
    public void BusinessKey_BesideAMessageStartAndATimerStart()
    {
        static void Keyed(JsonObject model) =>
            SignalStartFixtures.Node(model, 1)["businessKey"] = new JsonObject { ["variable"] = "recallNumber", ["uniqueness"] = "all" };
        static JsonObject MessageStart(bool keyed)
        {
            var node = new JsonObject
            {
                ["id"] = 6, ["name"] = "By webhook", ["type"] = "messageStartEvent",
                ["message"] = new JsonObject
                {
                    ["clientId"] = "svc", ["clientSecret"] = "secret", ["headerName"] = "X-Token", ["headerValue"] = "token",
                    ["outputMappings"] = new JsonArray(new JsonObject
                    {
                        ["variable"] = "recallNumber", ["path"] = "number", ["dataType"] = "string", ["isArray"] = false, ["required"] = true
                    })
                }
            };
            if (keyed) node["businessKey"] = new JsonObject { ["variable"] = "recallNumber", ["uniqueness"] = "all" };
            return node;
        }

        Assert.Equal(
            "Entry event #6 must configure businessKey because business keys are enabled for this workflow.",
            SignalThrowValidationTests.ServerError(SignalStartFixtures.Json(model =>
            {
                Keyed(model);
                SignalStartFixtures.Nodes(model).Add(MessageStart(keyed: false));
                SignalStartFixtures.Flows(model).Add(new JsonObject { ["id"] = 601, ["sourceRef"] = 6, ["targetRef"] = 2 });
            })));
        Assert.Null(SignalThrowValidationTests.ServerError(SignalStartFixtures.Json(model =>
        {
            Keyed(model);
            SignalStartFixtures.Nodes(model).Add(MessageStart(keyed: true));
            SignalStartFixtures.Flows(model).Add(new JsonObject { ["id"] = 601, ["sourceRef"] = 6, ["targetRef"] = 2 });
        })));
        Assert.Null(SignalThrowValidationTests.ServerError(SignalStartFixtures.Json(model =>
        {
            Keyed(model);
            SignalStartFixtures.Nodes(model).Add(new JsonObject
            {
                ["id"] = 7, ["name"] = "Every morning", ["type"] = "timerStartEvent",
                ["timer"] = new JsonObject { ["timeCycle"] = "R/P1D" }
            });
            SignalStartFixtures.Flows(model).Add(new JsonObject { ["id"] = 701, ["sourceRef"] = 7, ["targetRef"] = 2 });
        })));
    }

    [Fact]
    public void InitialEventId_CannotNameASignalStart()
    {
        Assert.Equal(
            "Workflow initialEventId must reference a start event.",
            SignalThrowValidationTests.ServerError(SignalStartFixtures.Json(model => model["initialEventId"] = 1)));
    }

    [Fact]
    public void NodeTypes_ClassifyTheStart()
    {
        var type = BpmnFlowNodeTypes.SignalStartEvent;
        Assert.True(BpmnFlowNodeTypes.IsSupported(type));
        Assert.True(BpmnFlowNodeTypes.IsEntry(type));
        Assert.True(BpmnFlowNodeTypes.IsPassThrough(type));
        Assert.True(BpmnFlowNodeTypes.IsSignalEvent(type));
        Assert.True(BpmnFlowNodeTypes.IsSignalStart(type));
        Assert.False(BpmnFlowNodeTypes.IsStart(type));
        Assert.False(BpmnFlowNodeTypes.IsSignalWait(type));
        Assert.False(BpmnFlowNodeTypes.ThrowsSignal(type));
        Assert.Equal("Signal start event", EntryEventRules.Kind(type));

        var startOnly = SignalThrowValidationTests.Deserialize(SignalStartFixtures.Json(model =>
        {
            SignalStartFixtures.Nodes(model).Remove(SignalStartFixtures.Node(model, 4));
            SignalStartFixtures.Flows(model).Remove(SignalStartFixtures.Flow(model, 202));
        }));
        Assert.False(SignalEventRules.HasSignalWaits(startOnly));
        Assert.True(SignalEventRules.HasSignalStarts(startOnly));
        Assert.True(WorkflowDefinitionReadinessChecker.RequiresDurableProcessing(startOnly));
        Assert.Equal(new[] { "recallNumber", "reason" }, EntryEventRules.DeclaredNames(startOnly.FlowNodes[0]));
    }

    [Fact]
    public void Normalization_KeepsTheSignalAndBusinessKeyAndDropsIdempotencyAndInputs()
    {
        var model = SignalThrowValidationTests.Deserialize(SignalStartFixtures.Json(model =>
        {
            var start = SignalStartFixtures.Node(model, 1);
            start["businessKey"] = new JsonObject { ["variable"] = "recallNumber", ["uniqueness"] = "ALL" };
            start["idempotency"] = new JsonObject { ["headerName"] = "Idempotency-Key", ["variable"] = "requestId" };
            start["roles"] = new JsonArray("Clerk");
        }));

        WorkflowModelMigrator.Normalize(model);

        var start = model.FlowNodes.Single(node => node.Id == 1);
        Assert.Equal("warehouse.recall-announced", start.Signal!.Name);
        Assert.Equal(2, start.Signal.OutputMappings.Count);
        Assert.Equal(BusinessKeyUniqueness.All, start.BusinessKey!.Uniqueness);
        Assert.Null(start.Idempotency);
        Assert.Empty(start.Roles);
        Assert.Empty(start.Variables);
        var serialized = JsonSerializer.Serialize(model, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("\"variables\":[]", JsonNode.Parse(serialized)!["flowNodes"]![0]!.ToJsonString(), StringComparison.Ordinal);
    }

    [Fact]
    public void SignalStarts_ListsTheFirstStartPerValidName()
    {
        var model = SignalThrowValidationTests.Deserialize(SignalStartFixtures.Json(model =>
        {
            SignalStartFixtures.AddSecondStart(model, "warehouse.recall-announced");
            SignalStartFixtures.Nodes(model).Add(new JsonObject
            {
                ["id"] = 6, ["name"] = "Invalid", ["type"] = "signalStartEvent",
                ["signal"] = new JsonObject { ["name"] = "Not.Valid", ["outputMappings"] = new JsonArray() }
            });
            SignalStartFixtures.Nodes(model).Add(new JsonObject
            {
                ["id"] = 7, ["name"] = "Other", ["type"] = "signalStartEvent",
                ["signal"] = new JsonObject { ["name"] = "warehouse.other", ["outputMappings"] = new JsonArray() }
            });
        }));

        var starts = SignalEventRules.SignalStarts(model);

        Assert.Equal(
            new[] { ("warehouse.recall-announced", 1, "Recall announced"), ("warehouse.other", 7, "Other") },
            starts.Select(start => (start.SignalName, start.NodeId, start.NodeName)).ToArray());
    }

    internal static IReadOnlyList<string> EditorErrors(string json)
    {
        var engine = CreateValidatorEngine();
        engine.SetValue("candidateJson", json);
        return JsonSerializer.Deserialize<string[]>(engine.Evaluate(
            "JSON.stringify(validateModelForSave(JSON.parse(candidateJson)))").AsString())!;
    }

    private static Engine CreateValidatorEngine()
    {
        var html = EditorSource.Read();
        var match = Regex.Match(html, @"// BEGIN WORKFLOW SAVE VALIDATOR(?<code>[\s\S]*?)// END WORKFLOW SAVE VALIDATOR");
        Assert.True(match.Success, "The marked workflow save validator was not found.");
        var normalizeRoles = Regex.Match(html, @"function normalizeRoles\(roles\) \{[\s\S]*?(?=function normalizeAttributesForLoad)");
        Assert.True(normalizeRoles.Success, "The role-normalization helper was not found.");
        var engine = new Engine();
        engine.Execute(normalizeRoles.Value);
        engine.Execute(match.Groups["code"].Value);
        return engine;
    }
}
