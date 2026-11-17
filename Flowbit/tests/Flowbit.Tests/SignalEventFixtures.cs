using System.Text.Json.Nodes;

namespace Flowbit.Tests;

/// <summary>
/// Signal event definitions and the first problem that both the server's definition validator and the editor's
/// save validator must report for them, with the same wording, so the two cannot drift. A null problem means the
/// definition is valid. The signal boundary cases, named "boundary: ...", change a copy of <see cref="BoundaryBase"/>;
/// every other case changes a copy of <see cref="Base"/>: start #1, user task #2, signal catch #9
/// waiting for <see cref="Name"/> with one typed mapping into the instance variable policyVersion, and end #10, joined
/// by flows 101, 201 (task to catch), and 901 (catch to end). Its process variables are the instance variables
/// policyVersion and note, the read-only shared alias baseRate, and the read/write shared alias rate.
/// </summary>
internal static class SignalEventFixtures
{
    public const string Name = "claims.policy-changed";

    private const string Label = "Signal catch event #9";
    private const string MissingName = Label + " must define signal.name.";
    private const string InvalidName = Label +
        " signal.name must be 1 to 100 lowercase letters, digits, '.', '_', or '-', starting with a letter or digit.";
    private const string MetadataOnOutgoing = Label +
        " must have one unconditional outgoing sequence flow without user-action or multi-instance metadata.";

    private const string BaseJson = """
        {
          "id": "signal-fixture",
          "name": "Signal fixture",
          "initialEventId": 1,
          "lanes": [],
          "variables": [
            { "id": 1, "name": "policyVersion", "dataType": "number", "isArray": false, "nullable": true,
              "required": false, "defaultValue": null, "validation": null },
            { "id": 2, "name": "note", "dataType": "string", "isArray": false, "nullable": true,
              "required": false, "defaultValue": null, "validation": null },
            { "id": 3, "name": "baseRate", "dataType": "number", "isArray": false, "nullable": false,
              "required": false, "defaultValue": null, "validation": null,
              "scope": "shared", "sharedKey": "examples.signals.base-rate", "access": "read" },
            { "id": 4, "name": "rate", "dataType": "number", "isArray": false, "nullable": false,
              "required": false, "defaultValue": null, "validation": null,
              "scope": "shared", "sharedKey": "examples.signals.rate", "access": "readWrite" }
          ],
          "flowNodes": [
            { "id": 1, "name": "Start", "type": "startEvent" },
            { "id": 2, "name": "Review", "type": "userTask" },
            {
              "id": 9,
              "name": "Wait for the new policy",
              "type": "intermediateSignalCatchEvent",
              "signal": {
                "name": "claims.policy-changed",
                "outputMappings": [
                  { "variable": "policyVersion", "path": "version", "dataType": "number", "isArray": false, "required": true }
                ]
              }
            },
            { "id": 10, "name": "Done", "type": "endEvent" }
          ],
          "sequenceFlows": [
            { "id": 101, "sourceRef": 1, "targetRef": 2 },
            { "id": 201, "name": "Wait for the policy", "sourceRef": 2, "targetRef": 9 },
            { "id": 901, "sourceRef": 9, "targetRef": 10 }
          ]
        }
        """;

    public static JsonObject Base() => JsonNode.Parse(BaseJson)!.AsObject();

    /// <summary>(case, definition JSON, first problem). A null problem means the definition is valid.</summary>
    public static IEnumerable<(string Case, string Json, string? Problem)> Cases()
    {
        yield return ("a named catch with a typed mapping", Json(_ => { }), null);
        yield return ("a name at the length limit", Json(model => Signal(model)["name"] = new string('a', 100)), null);
        yield return ("a name with every allowed kind of character", Json(model => Signal(model)["name"] = "0a._-z9"), null);
        yield return ("a catch without mappings", Json(model => Signal(model)["outputMappings"] = new JsonArray()), null);
        yield return ("exactly 100 mappings", Json(model => Signal(model)["outputMappings"] = Mappings(100)), null);

        yield return ("no signal configuration", Json(model => Node(model, 9).Remove("signal")), MissingName);
        yield return ("an empty name", Json(model => Signal(model)["name"] = ""), MissingName);
        yield return ("a null name", Json(model => Signal(model)["name"] = null), MissingName);
        yield return ("no name property", Json(model => Signal(model).Remove("name")), MissingName);

        yield return ("an uppercase letter", Json(model => Signal(model)["name"] = "claims.Policy-changed"), InvalidName);
        yield return ("a leading dot", Json(model => Signal(model)["name"] = ".policy-changed"), InvalidName);
        yield return ("a leading hyphen", Json(model => Signal(model)["name"] = "-policy-changed"), InvalidName);
        yield return ("101 characters", Json(model => Signal(model)["name"] = new string('a', 101)), InvalidName);
        yield return ("a trailing line feed", Json(model => Signal(model)["name"] = Name + "\n"), InvalidName);
        yield return ("a space", Json(model => Signal(model)["name"] = "claims policy-changed"), InvalidName);
        yield return ("a letter outside ASCII", Json(model => Signal(model)["name"] = "claims.pólicy-changed"), InvalidName);

        yield return ("more than 100 mappings", Json(model => Signal(model)["outputMappings"] = Mappings(101)),
            Label + " defines more than 100 output mappings.");

        yield return ("no incoming flow", Json(model => Flow(model, 201)["targetRef"] = 10),
            Label + " must have at least one incoming sequence flow.");
        yield return ("no outgoing flow", Json(model =>
            {
                Flows(model).Remove(Flow(model, 901));
                Flows(model).Add(new JsonObject { ["id"] = 202, ["name"] = "Finish", ["sourceRef"] = 2, ["targetRef"] = 10 });
            }),
            Label + " must have exactly one outgoing sequence flow.");
        yield return ("two outgoing flows", Json(model =>
            {
                Nodes(model).Add(new JsonObject { ["id"] = 11, ["name"] = "Also done", ["type"] = "endEvent" });
                Flows(model).Add(new JsonObject { ["id"] = 902, ["sourceRef"] = 9, ["targetRef"] = 11 });
            }),
            Label + " must have exactly one outgoing sequence flow.");
        yield return ("a conditional outgoing flow", Json(model => Flow(model, 901)["condition"] = "policyVersion > 0"),
            MetadataOnOutgoing);
        yield return ("an outgoing flow with action roles", Json(model => Flow(model, 901)["roles"] = new JsonArray("Clerk")),
            MetadataOnOutgoing);

        yield return ("a mapping writes a read/write shared alias",
            Json(model => Signal(model)["outputMappings"]!.AsArray().Add(Mapping("rate", "number"))),
            Label + " output mapping 'rate' cannot write shared variable alias 'rate'; signal mappings write instance variables only.");
        yield return ("a mapping writes a read-only shared alias in another casing",
            Json(model => Signal(model)["outputMappings"]!.AsArray().Add(Mapping("BASERATE", "number"))),
            Label + " output mapping 'baseRate' cannot write shared variable alias 'baseRate'; signal mappings write instance variables only.");
        yield return ("a mapping default reads a shared alias",
            Json(model =>
            {
                var note = Mapping("note", "string");
                note["defaultValue"] = "Rate ${baseRate}";
                Signal(model)["outputMappings"]!.AsArray().Add(note);
            }),
            Label + " output mapping 'note' cannot read shared variable alias 'baseRate' in its default or validation.");
        yield return ("an array default reads a shared alias",
            Json(model =>
            {
                var labels = Mapping("labels", "string");
                labels["isArray"] = true;
                labels["defaultValue"] = new JsonArray("fixed", "${rate}");
                Signal(model)["outputMappings"]!.AsArray().Add(labels);
            }),
            Label + " output mapping 'labels' cannot read shared variable alias 'rate' in its default or validation.");
        yield return ("a mapping validation reads a shared alias",
            Json(model => FirstMapping(model)["validation"] = "policyVersion >= baseRate"),
            Label + " output mapping 'policyVersion' cannot read shared variable alias 'baseRate' in its default or validation.");
        yield return ("a mapping in another casing reads a shared alias",
            Json(model =>
            {
                FirstMapping(model)["variable"] = "POLICYVERSION";
                FirstMapping(model)["validation"] = "[baseRate] > 0";
            }),
            Label + " output mapping 'policyVersion' cannot read shared variable alias 'baseRate' in its default or validation.");
        yield return ("the target's validation reads a shared alias",
            Json(model => Variable(model, "policyVersion")["validation"] = "policyVersion >= [rate]"),
            Label + " output mapping 'policyVersion' cannot read shared variable alias 'rate' in its default or validation.");
        yield return ("a validation reads two shared aliases, the later declared one first",
            Json(model => FirstMapping(model)["validation"] = "policyVersion >= rate + baseRate"),
            Label + " output mapping 'policyVersion' cannot read shared variable alias 'rate' in its default or validation.");
        yield return ("quoted alias text is not a read",
            Json(model => FirstMapping(model)["validation"] = "policyVersion >= 0 or Len('baseRate') > 0"), null);
        yield return ("a dotted context name is not a read of the alias",
            Json(model =>
            {
                var note = Mapping("note", "string");
                note["defaultValue"] = "Rate ${setting.baseRate}";
                Signal(model)["outputMappings"]!.AsArray().Add(note);
            }),
            null);

        yield return ("a signal on a user task",
            Json(model => Node(model, 2)["signal"] = new JsonObject { ["name"] = Name, ["outputMappings"] = new JsonArray() }),
            "Flow node #2 defines signal but is not a signal event.");
        yield return ("a null signal on a user task", Json(model => Node(model, 2)["signal"] = null), null);

        foreach (var boundaryCase in BoundaryCases())
        {
            yield return boundaryCase;
        }
    }

    private const string BoundaryName = "claims.recall";

    private const string BoundaryLabel = "Signal boundary event #14";

    /// <summary>
    /// Signal boundary cases. Each changes a copy of <see cref="BoundaryBase"/>: <see cref="Base"/> plus the
    /// non-interrupting signal boundary #14 on user task #2, waiting for <see cref="BoundaryName"/> with one typed
    /// mapping into the instance variable note, and its flow 1401 to end #15.
    /// </summary>
    private static IEnumerable<(string Case, string Json, string? Problem)> BoundaryCases()
    {
        yield return ("boundary: a non-interrupting signal boundary on a user task", BoundaryJson(_ => { }), null);
        yield return ("boundary: an interrupting signal boundary",
            BoundaryJson(model => Node(model, 14)["cancelActivity"] = true), null);
        yield return ("boundary: no cancelActivity, which means interrupting",
            BoundaryJson(model => Node(model, 14).Remove("cancelActivity")), null);
        yield return ("boundary: without mappings",
            BoundaryJson(model => BoundarySignal(model).Remove("outputMappings")), null);
        yield return ("boundary: on the signal catch with another name",
            BoundaryJson(model => Node(model, 14)["attachedToRef"] = 9), null);
        yield return ("boundary: on an automatic task with asyncBefore",
            BoundaryJson(model =>
            {
                Node(model, 2)["type"] = "task";
                Node(model, 2)["asyncBefore"] = true;
            }),
            null);
        yield return ("boundary: two on one host with different names",
            BoundaryJson(model => AddBoundary(model, 16, 2, "claims.withdrawn")), null);
        yield return ("boundary: one name on two hosts",
            BoundaryJson(model => AddBoundary(model, 16, 9, BoundaryName)), null);
        yield return ("boundary: the name of a catch that is not its host",
            BoundaryJson(model => BoundarySignal(model)["name"] = Name), null);
        yield return ("boundary: eight timer, conditional, and signal boundaries on one host",
            BoundaryJson(model => AddWaitBoundaries(model, 7)), null);

        yield return ("boundary: no signal configuration",
            BoundaryJson(model => Node(model, 14).Remove("signal")), BoundaryLabel + " must define signal.name.");
        yield return ("boundary: an empty name",
            BoundaryJson(model => BoundarySignal(model)["name"] = ""), BoundaryLabel + " must define signal.name.");
        yield return ("boundary: an uppercase letter",
            BoundaryJson(model => BoundarySignal(model)["name"] = "claims.Recall"),
            BoundaryLabel + " signal.name must be 1 to 100 lowercase letters, digits, '.', '_', or '-', starting with a letter or digit.");
        yield return ("boundary: more than 100 mappings",
            BoundaryJson(model => BoundarySignal(model)["outputMappings"] = Mappings(101)),
            BoundaryLabel + " defines more than 100 output mappings.");

        yield return ("boundary: no attachedToRef",
            BoundaryJson(model => Node(model, 14).Remove("attachedToRef")),
            BoundaryLabel + " must reference a host via attachedToRef.");
        yield return ("boundary: a host that does not exist",
            BoundaryJson(model => Node(model, 14)["attachedToRef"] = 99),
            BoundaryLabel + " attachedToRef #99 does not reference an existing flow node.");
        yield return ("boundary: an end event as host",
            BoundaryJson(model => Node(model, 14)["attachedToRef"] = 10),
            BoundaryLabel + " host #10 must be a durable wait or an automatic task with asyncBefore enabled.");
        yield return ("boundary: an automatic task without asyncBefore",
            BoundaryJson(model => Node(model, 2)["type"] = "task"),
            BoundaryLabel + " host #2 must be a durable wait or an automatic task with asyncBefore enabled.");
        yield return ("boundary: a conditional catch as host",
            BoundaryJson(model =>
            {
                var host = Node(model, 9);
                host["type"] = "intermediateConditionalCatchEvent";
                host.Remove("signal");
                host["conditional"] = new JsonObject { ["condition"] = "policyVersion > 1" };
                Node(model, 14)["attachedToRef"] = 9;
            }),
            BoundaryLabel + " host #9 must be a durable wait or an automatic task with asyncBefore enabled.");
        yield return ("boundary: an ordinary call activity as host",
            BoundaryJson(model =>
            {
                var host = Node(model, 2);
                host["type"] = "callActivity";
                host["callActivity"] = new JsonObject
                {
                    ["workflowKey"] = "child", ["inputMappings"] = new JsonArray(), ["outputMappings"] = new JsonArray()
                };
            }),
            BoundaryLabel + " host #2 must be a durable wait or an automatic task with asyncBefore enabled.");
        yield return ("boundary: a multi-instance script task as host",
            BoundaryJson(model =>
            {
                model["variables"]!.AsArray().Add(JsonNode.Parse("""
                    { "id": 5, "name": "amounts", "dataType": "number", "isArray": true, "defaultValue": [10, 20] }
                    """));
                model["variables"]!.AsArray().Add(JsonNode.Parse("""
                    { "id": 6, "name": "results", "dataType": "json", "isArray": false, "defaultValue": [] }
                    """));
                var host = Node(model, 2);
                host["type"] = "scriptTask";
                host["asyncBefore"] = true;
                host["scriptFormat"] = "ncalc";
                host["multiInstance"] = JsonNode.Parse("""
                    {
                      "mode": "sequential", "source": "collection", "collectionVariable": "amounts",
                      "completionEvaluation": "afterAll", "maxConcurrency": 1, "resultVariable": "results",
                      "outputVariables": [ { "name": "scaled", "dataType": "number", "nullable": false } ]
                    }
                    """);
                host["assignments"] = JsonNode.Parse("""[ { "variable": "scaled", "expression": "[mi.item] * 2" } ]""");
                Flow(model, 201).Remove("name");
            }),
            BoundaryLabel + " host #2 is a multi-instance service, script, or call activity, which signal boundaries do not support.");

        yield return ("boundary: an incoming flow",
            BoundaryJson(model =>
            {
                Nodes(model).Add(new JsonObject { ["id"] = 17, ["name"] = "Recall by hand", ["type"] = "userTask" });
                Flows(model).Add(new JsonObject { ["id"] = 102, ["name"] = "Recall", ["sourceRef"] = 2, ["targetRef"] = 17 });
                Flows(model).Add(new JsonObject { ["id"] = 1701, ["name"] = "Recall", ["sourceRef"] = 17, ["targetRef"] = 14 });
            }),
            BoundaryLabel + " cannot have incoming sequence flows.");
        yield return ("boundary: no outgoing flow",
            BoundaryJson(model =>
            {
                Flows(model).Remove(Flow(model, 1401));
                Nodes(model).Remove(Node(model, 15));
            }),
            BoundaryLabel + " must have exactly one outgoing sequence flow.");
        yield return ("boundary: two outgoing flows",
            BoundaryJson(model => Flows(model).Add(new JsonObject { ["id"] = 1402, ["sourceRef"] = 14, ["targetRef"] = 10 })),
            BoundaryLabel + " must have exactly one outgoing sequence flow.");
        yield return ("boundary: a conditional outgoing flow",
            BoundaryJson(model => Flow(model, 1401)["condition"] = "policyVersion > 0"),
            BoundaryLabel + " must have one unconditional outgoing sequence flow without user-action or multi-instance metadata.");

        yield return ("boundary: a name another signal boundary on its host uses",
            BoundaryJson(model => AddBoundary(model, 16, 2, BoundaryName)),
            "Signal boundary event #16 uses signal 'claims.recall', which signal boundary event #14 on host #2 already uses.");
        yield return ("boundary: a repeated name listed before the first",
            BoundaryJson(model =>
            {
                AddBoundary(model, 16, 2, BoundaryName);
                var later = Node(model, 16);
                Nodes(model).Remove(later);
                Nodes(model).Insert(0, later);
            }),
            "Signal boundary event #16 uses signal 'claims.recall', which signal boundary event #14 on host #2 already uses.");
        yield return ("boundary: the name its host catch waits for",
            BoundaryJson(model =>
            {
                Node(model, 14)["attachedToRef"] = 9;
                BoundarySignal(model)["name"] = Name;
            }),
            BoundaryLabel + " uses signal 'claims.policy-changed', the signal its host signal catch event #9 waits for.");
        yield return ("boundary: nine timer, conditional, and signal boundaries on one host",
            BoundaryJson(model => AddWaitBoundaries(model, 8)),
            "Host node #2 has 9 timer, conditional, and signal boundary events; at most 8 are allowed.");
        yield return ("boundary: nine on one host, the first of them with an incoming flow",
            BoundaryJson(model =>
            {
                AddWaitBoundaries(model, 8);
                Nodes(model).Add(new JsonObject { ["id"] = 17, ["name"] = "Recall by hand", ["type"] = "userTask" });
                Flows(model).Add(new JsonObject { ["id"] = 102, ["name"] = "Recall", ["sourceRef"] = 2, ["targetRef"] = 17 });
                Flows(model).Add(new JsonObject { ["id"] = 1701, ["name"] = "Recall", ["sourceRef"] = 17, ["targetRef"] = 14 });
            }),
            BoundaryLabel + " cannot have incoming sequence flows.");
        yield return ("boundary: nine on one host, a timer boundary listed first",
            BoundaryJson(model =>
            {
                AddWaitBoundaries(model, 8);
                var first = Node(model, 20);
                Nodes(model).Remove(first);
                Nodes(model).Insert(0, first);
            }),
            "Host node #2 has 9 timer, conditional, and signal boundary events; at most 8 are allowed.");

        yield return ("boundary: a mapping writes a shared alias",
            BoundaryJson(model => BoundarySignal(model)["outputMappings"]!.AsArray().Add(Mapping("rate", "number"))),
            BoundaryLabel + " output mapping 'rate' cannot write shared variable alias 'rate'; signal mappings write instance variables only.");
        yield return ("boundary: a mapping validation reads a shared alias",
            BoundaryJson(model => BoundarySignal(model)["outputMappings"]![0]!["validation"] = "Len(note) < baseRate"),
            BoundaryLabel + " output mapping 'note' cannot read shared variable alias 'baseRate' in its default or validation.");

        yield return ("boundary: cancelActivity on a user task",
            BoundaryJson(model => Node(model, 2)["cancelActivity"] = false),
            "Flow node #2 defines cancelActivity but is not a timer, conditional, or signal boundary event.");
    }

    /// <summary><see cref="Base"/> with the non-interrupting signal boundary #14 on user task #2.</summary>
    public static JsonObject BoundaryBase()
    {
        var model = Base();
        Nodes(model).Add(JsonNode.Parse("""
            {
              "id": 14,
              "name": "Recall announced",
              "type": "signalBoundaryEvent",
              "attachedToRef": 2,
              "cancelActivity": false,
              "signal": {
                "name": "claims.recall",
                "outputMappings": [
                  { "variable": "note", "path": "reason", "dataType": "string", "isArray": false, "required": false }
                ]
              }
            }
            """));
        Nodes(model).Add(new JsonObject { ["id"] = 15, ["name"] = "Recalled", ["type"] = "endEvent" });
        Flows(model).Add(new JsonObject { ["id"] = 1401, ["sourceRef"] = 14, ["targetRef"] = 15 });
        return model;
    }

    private static string BoundaryJson(Action<JsonObject> change)
    {
        var model = BoundaryBase();
        change(model);
        return model.ToJsonString();
    }

    private static JsonObject BoundarySignal(JsonObject model) => Node(model, 14)["signal"]!.AsObject();

    private static void AddBoundary(JsonObject model, int id, int hostId, string name)
    {
        Nodes(model).Add(new JsonObject
        {
            ["id"] = id,
            ["name"] = "Boundary " + id,
            ["type"] = "signalBoundaryEvent",
            ["attachedToRef"] = hostId,
            ["signal"] = new JsonObject { ["name"] = name, ["outputMappings"] = new JsonArray() }
        });
        Flows(model).Add(new JsonObject { ["id"] = id * 100 + 1, ["sourceRef"] = id, ["targetRef"] = 15 });
    }

    // Adds timer and conditional boundaries, alternately, to user task #2.
    private static void AddWaitBoundaries(JsonObject model, int count)
    {
        for (var index = 0; index < count; index++)
        {
            var id = 20 + index;
            var boundary = new JsonObject
            {
                ["id"] = id,
                ["name"] = "Wait " + id,
                ["attachedToRef"] = 2,
                ["cancelActivity"] = false
            };
            if (index % 2 == 0)
            {
                boundary["type"] = "timerBoundaryEvent";
                boundary["timer"] = new JsonObject { ["timeDuration"] = "P2D" };
            }
            else
            {
                boundary["type"] = "conditionalBoundaryEvent";
                boundary["conditional"] = new JsonObject { ["condition"] = "policyVersion > " + index };
            }

            Nodes(model).Add(boundary);
            Flows(model).Add(new JsonObject { ["id"] = id * 100 + 1, ["sourceRef"] = id, ["targetRef"] = 15 });
        }
    }

    private static string Json(Action<JsonObject> change)
    {
        var model = Base();
        change(model);
        return model.ToJsonString();
    }

    private static JsonArray Nodes(JsonObject model) => model["flowNodes"]!.AsArray();

    private static JsonArray Flows(JsonObject model) => model["sequenceFlows"]!.AsArray();

    private static JsonObject Node(JsonObject model, int id) =>
        Nodes(model).Select(node => node!.AsObject()).Single(node => (int)node["id"]! == id);

    private static JsonObject Flow(JsonObject model, int id) =>
        Flows(model).Select(flow => flow!.AsObject()).Single(flow => (int)flow["id"]! == id);

    private static JsonObject Variable(JsonObject model, string name) =>
        model["variables"]!.AsArray().Select(variable => variable!.AsObject())
            .Single(variable => (string)variable["name"]! == name);

    private static JsonObject Signal(JsonObject model) => Node(model, 9)["signal"]!.AsObject();

    private static JsonObject FirstMapping(JsonObject model) => Signal(model)["outputMappings"]![0]!.AsObject();

    private static JsonObject Mapping(string variable, string dataType) => new()
    {
        ["variable"] = variable,
        ["path"] = variable,
        ["dataType"] = dataType,
        ["isArray"] = false,
        ["required"] = false
    };

    private static JsonArray Mappings(int count)
    {
        var mappings = new JsonArray();
        for (var index = 0; index < count; index++)
        {
            mappings.Add(Mapping("value" + index, "string"));
        }

        return mappings;
    }
}
