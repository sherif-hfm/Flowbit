using System.Text.Json.Nodes;

namespace Flowbit.Tests;

/// <summary>
/// Signal start event definitions and the first problem that both the server's definition validator and the editor's
/// save validator must report for them, with the same wording, so the two cannot drift. A null problem means the
/// definition is valid. Every case changes a copy of <see cref="Base"/>: signal start #1 on 'warehouse.recall-announced'
/// with the required string mapping recallNumber (path recallNumber) and the optional string mapping reason (path
/// reason, default 'Not stated'), user task #2, end #3, and signal end #4, which throws 'warehouse.recall-closed' to
/// the family example-signal-closing. Flows 101 (start to task), 201 (task to end), and 202 (task to signal end) join
/// them. Its process variables are the instance variable amount (number) and the read/write shared alias rate (number).
/// </summary>
internal static class SignalStartFixtures
{
    private const string Start = "Signal start event #1";
    private const string InvalidName =
        " signal.name must be 1 to 100 lowercase letters, digits, '.', '_', or '-', starting with a letter or digit.";

    private const string BaseJson = """
        {
          "id": "signal-start-fixture",
          "name": "Signal start fixture",
          "initialEventId": null,
          "lanes": [],
          "variables": [
            { "id": 1, "name": "amount", "dataType": "number", "isArray": false, "nullable": true,
              "required": false, "defaultValue": null, "validation": null },
            { "id": 2, "name": "rate", "dataType": "number", "isArray": false, "nullable": false,
              "required": false, "defaultValue": null, "validation": null,
              "scope": "shared", "sharedKey": "examples.signals.rate", "access": "readWrite" }
          ],
          "flowNodes": [
            {
              "id": 1,
              "name": "Recall announced",
              "type": "signalStartEvent",
              "signal": {
                "name": "warehouse.recall-announced",
                "outputMappings": [
                  { "variable": "recallNumber", "path": "recallNumber", "dataType": "string", "isArray": false,
                    "required": true, "defaultValue": null, "validation": null },
                  { "variable": "reason", "path": "reason", "dataType": "string", "isArray": false,
                    "required": false, "defaultValue": "Not stated", "validation": null }
                ]
              }
            },
            { "id": 2, "name": "Write the notice", "type": "userTask" },
            { "id": 3, "name": "Done", "type": "endEvent" },
            {
              "id": 4,
              "name": "Recall closed",
              "type": "signalEndEvent",
              "signal": {
                "name": "warehouse.recall-closed",
                "scope": { "kind": "workflow", "workflowKey": "example-signal-closing" }
              }
            }
          ],
          "sequenceFlows": [
            { "id": 101, "sourceRef": 1, "targetRef": 2 },
            { "id": 201, "name": "Done", "sourceRef": 2, "targetRef": 3 },
            { "id": 202, "name": "Close the recall", "sourceRef": 2, "targetRef": 4 }
          ]
        }
        """;

    public static JsonObject Base() => JsonNode.Parse(BaseJson)!.AsObject();

    /// <summary>(case, definition JSON, first problem). A null problem means the definition is valid.</summary>
    public static IEnumerable<(string Case, string Json, string? Problem)> Cases()
    {
        yield return ("a signal start with typed mappings", Json(_ => { }), null);
        yield return ("a signal start without mappings", Json(model => Signal(model, 1)["outputMappings"] = new JsonArray()), null);
        yield return ("a signal start beside a plain start",
            Json(model =>
            {
                model["initialEventId"] = 5;
                Nodes(model).Add(new JsonObject { ["id"] = 5, ["name"] = "Start", ["type"] = "startEvent" });
                Flows(model).Add(new JsonObject { ["id"] = 501, ["sourceRef"] = 5, ["targetRef"] = 2 });
            }), null);
        yield return ("two signal starts on different names",
            Json(model => AddSecondStart(model, "warehouse.recall-reopened")), null);
        yield return ("a business key from a required path mapping",
            Json(model => Node(model, 1)["businessKey"] = new JsonObject { ["variable"] = "recallNumber", ["uniqueness"] = "all" }),
            null);
        yield return ("a throw of the start's name to another family",
            Json(model => Signal(model, 4)["name"] = "warehouse.recall-announced"), null);
        yield return ("a throw of the start's name to this instance's business key",
            Json(model =>
            {
                Node(model, 1)["businessKey"] = new JsonObject { ["variable"] = "recallNumber", ["uniqueness"] = "all" };
                Signal(model, 4)["name"] = "warehouse.recall-announced";
                Signal(model, 4)["scope"] = new JsonObject { ["kind"] = "ownBusinessKey", ["workflowKey"] = "signal-start-fixture" };
            }), null);

        yield return ("a throw of the start's name to one business key in this workflow",
            Json(model =>
            {
                Signal(model, 4)["name"] = "warehouse.recall-announced";
                Signal(model, 4)["scope"] = new JsonObject
                {
                    ["kind"] = "businessKey", ["workflowKey"] = "signal-start-fixture", ["businessKeyVariable"] = "recallNumber"
                };
            }), null);

        // A throw to this instance reaches no start, so its only problem is the missing wait.
        yield return ("a throw of the start's name to this instance",
            Json(model =>
            {
                Signal(model, 4)["name"] = "warehouse.recall-announced";
                Signal(model, 4)["scope"] = new JsonObject { ["kind"] = "thisInstance" };
            }),
            "Signal end event #4 throws 'warehouse.recall-announced' to this instance, but no signal catch or boundary event in this workflow waits for it.");
        yield return ("a missing name", Json(model => Signal(model, 1).Remove("name")), $"{Start} must define signal.name.");
        yield return ("an uppercase name", Json(model => Signal(model, 1)["name"] = "Warehouse.Recall"), Start + InvalidName);
        yield return ("an incoming flow",
            Json(model => Flows(model).Add(new JsonObject { ["id"] = 203, ["name"] = "Restart", ["sourceRef"] = 2, ["targetRef"] = 1 })),
            $"{Start} cannot have incoming sequence flows.");
        yield return ("no outgoing flow",
            Json(model => Flows(model).Remove(Flow(model, 101))),
            $"{Start} must have exactly one outgoing sequence flow.");
        yield return ("a conditional outgoing flow",
            Json(model => Flow(model, 101)["condition"] = "amount > 1"),
            $"The outgoing sequence flow from signal start event #1 must be unconditional and cannot define action or multi-instance metadata.");
        yield return ("a scope on the start",
            Json(model => Signal(model, 1)["scope"] = new JsonObject { ["kind"] = "thisWorkflow" }),
            $"{Start} cannot define signal.scope or signal.payload; only signal throw and end events send a signal.");
        yield return ("the same name on two starts",
            Json(model => AddSecondStart(model, "warehouse.recall-announced")),
            "Signal start event #5 starts on 'warehouse.recall-announced', which signal start event #1 already starts on; a workflow starts once per signal.");
        yield return ("idempotency on the start",
            Json(model => Node(model, 1)["idempotency"] = new JsonObject { ["headerName"] = "Idempotency-Key", ["variable"] = "requestId" }),
            $"{Start} cannot define idempotency; a broadcast starts one instance per workflow, and businessKey rejects a repeated case.");
        yield return ("a mapping that names a process variable",
            Json(model => NumberMapping(model, "amount")),
            "Entry event #1 variable 'amount' collides with a process variable; a signal start's mappings declare instance start variables only.");
        yield return ("a mapping that names a shared alias",
            Json(model => NumberMapping(model, "rate")),
            "Entry event #1 variable 'rate' collides with a process variable; a signal start's mappings declare instance start variables only.");
        yield return ("a throw of the start's name to this workflow",
            Json(model =>
            {
                Signal(model, 4)["name"] = "warehouse.recall-announced";
                Signal(model, 4)["scope"] = new JsonObject { ["kind"] = "thisWorkflow" };
            }),
            "Signal end event #4 throws 'warehouse.recall-announced' to a scope that includes this workflow, which starts on it at signal start event #1; every instance would start another.");
        yield return ("a throw of the start's name to all workflows",
            Json(model =>
            {
                Signal(model, 4)["name"] = "warehouse.recall-announced";
                Signal(model, 4)["scope"] = new JsonObject { ["kind"] = "allWorkflows" };
            }),
            "Signal end event #4 throws 'warehouse.recall-announced' to a scope that includes this workflow, which starts on it at signal start event #1; every instance would start another.");
        yield return ("a throw of the start's name to its own family by key",
            Json(model =>
            {
                Signal(model, 4)["name"] = "warehouse.recall-announced";
                Signal(model, 4)["scope"] = new JsonObject { ["kind"] = "workflow", ["workflowKey"] = "signal-start-fixture" };
            }),
            "Signal end event #4 throws 'warehouse.recall-announced' to a scope that includes this workflow, which starts on it at signal start event #1; every instance would start another.");
        yield return ("an intermediate throw of the start's name to this workflow",
            Json(model =>
            {
                AddThrow(model, "warehouse.recall-announced");
            }),
            "Signal throw event #7 throws 'warehouse.recall-announced' to a scope that includes this workflow, which starts on it at signal start event #1; every instance would start another.");
        yield return ("an intermediate throw of another name to this workflow",
            Json(model => AddThrow(model, "warehouse.recall-reminded")), null);

        // The mapping rules, in the server's order and wording.
        yield return ("a mapping without a type",
            Json(model => Mappings(model, 1)[0]!.AsObject().Remove("dataType")),
            $"{Start} output mapping for 'recallNumber' must declare dataType and isArray.");
        yield return ("a mapping without isArray",
            Json(model => Mappings(model, 1)[0]!.AsObject().Remove("isArray")),
            $"{Start} output mapping for 'recallNumber' must declare dataType and isArray.");
        yield return ("a mapping without a path or a default",
            Json(model => Mappings(model, 1)[0]!["path"] = ""),
            $"{Start} output mapping for 'recallNumber' must have a path unless defaultValue is configured.");
        yield return ("a default of the wrong type",
            Json(model => Mappings(model, 1)[1]!["defaultValue"] = 3),
            $"{Start} output mapping default for 'reason' must be string.");
        yield return ("a mapping without a name",
            Json(model => Mappings(model, 1)[1]!["variable"] = ""),
            "Variable name is required on signal start event #1 output mappings.");
        yield return ("a mapping whose name is null",
            Json(model => Mappings(model, 1)[1]!["variable"] = null),
            "Variable name is required on signal start event #1 output mappings.");
        yield return ("a mapping with a reserved prefix",
            Json(model => Mappings(model, 1)[1]!["variable"] = "sys.reason"),
            "Variable 'sys.reason' on signal start event #1 output mappings uses a reserved context prefix.");
        yield return ("two mappings with one name in different casing",
            Json(model => Mappings(model, 1)[1]!["variable"] = "RecallNumber"),
            "Variable name 'recallNumber' is duplicated on signal start event #1 output mappings; variable names are case-insensitive.");
        yield return ("a mapping of an unsupported type",
            Json(model => Mappings(model, 1)[1]!["dataType"] = "money"),
            $"{Start} output mapping default for 'reason' must be money.");
        yield return ("an unsupported type without a default",
            Json(model =>
            {
                Mappings(model, 1)[1]!["dataType"] = "money";
                Mappings(model, 1)[1]!["defaultValue"] = null;
            }),
            "Variable 'reason' on signal start event #1 output mappings has unsupported type 'money'.");

        // The business key, which the server checks before the start's own rules.
        yield return ("a business key that names no mapping",
            Json(model => Node(model, 1)["businessKey"] = new JsonObject { ["variable"] = "missing", ["uniqueness"] = "all" }),
            "Entry event #1 businessKey variable 'missing' is not a typed output mapping on the signal start event.");
        yield return ("a business key on an optional mapping",
            Json(model => Node(model, 1)["businessKey"] = new JsonObject { ["variable"] = "reason", ["uniqueness"] = "all" }),
            "Entry event #1 businessKey mapping 'reason' must be a required scalar string with an explicit path and no defaultValue.");
        yield return ("a signal start without a key beside a keyed plain start",
            Json(model =>
            {
                Nodes(model).Add(new JsonObject
                {
                    ["id"] = 5, ["name"] = "Start", ["type"] = "startEvent",
                    ["variables"] = new JsonArray(new JsonObject
                    {
                        ["id"] = 51, ["name"] = "key", ["dataType"] = "string", ["isArray"] = false, ["required"] = true
                    }),
                    ["businessKey"] = new JsonObject { ["variable"] = "key", ["uniqueness"] = "all" }
                });
                Flows(model).Add(new JsonObject { ["id"] = 501, ["sourceRef"] = 5, ["targetRef"] = 2 });
                model["initialEventId"] = 5;
            }),
            "Entry event #1 must configure businessKey because business keys are enabled for this workflow.");

        // Two problems on one start: the shared entry rules come first.
        yield return ("no name and an incoming flow",
            Json(model =>
            {
                Signal(model, 1).Remove("name");
                Flows(model).Add(new JsonObject { ["id"] = 203, ["name"] = "Restart", ["sourceRef"] = 2, ["targetRef"] = 1 });
            }),
            $"{Start} cannot have incoming sequence flows.");
        yield return ("no name and a business key that names no mapping",
            Json(model =>
            {
                Signal(model, 1).Remove("name");
                Node(model, 1)["businessKey"] = new JsonObject { ["variable"] = "missing", ["uniqueness"] = "all" };
            }),
            "Entry event #1 businessKey variable 'missing' is not a typed output mapping on the signal start event.");
        yield return ("a scope and a mapping without a type",
            Json(model =>
            {
                Signal(model, 1)["scope"] = new JsonObject { ["kind"] = "thisWorkflow" };
                Mappings(model, 1)[0]!.AsObject().Remove("dataType");
            }),
            $"{Start} cannot define signal.scope or signal.payload; only signal throw and end events send a signal.");

        // Three starts on one name, not in id order: the first earlier start in document order.
        yield return ("three starts on one name out of id order",
            Json(model =>
            {
                var nodes = Nodes(model);
                nodes.Insert(0, new JsonObject
                {
                    ["id"] = 6, ["name"] = "Again", ["type"] = "signalStartEvent",
                    ["signal"] = new JsonObject { ["name"] = "warehouse.recall-announced", ["outputMappings"] = new JsonArray() }
                });
                nodes.Insert(0, new JsonObject
                {
                    ["id"] = 7, ["name"] = "Once more", ["type"] = "signalStartEvent",
                    ["signal"] = new JsonObject { ["name"] = "warehouse.recall-announced", ["outputMappings"] = new JsonArray() }
                });
                Flows(model).Add(new JsonObject { ["id"] = 601, ["sourceRef"] = 6, ["targetRef"] = 2 });
                Flows(model).Add(new JsonObject { ["id"] = 701, ["sourceRef"] = 7, ["targetRef"] = 2 });
            }),
            "Signal start event #7 starts on 'warehouse.recall-announced', which signal start event #6 already starts on; a workflow starts once per signal.");
    }

    /// <summary>Adds signal throw #7 of the given name to this workflow between task #2 and end #3.</summary>
    public static void AddThrow(JsonObject model, string signalName)
    {
        Nodes(model).Add(new JsonObject
        {
            ["id"] = 7,
            ["name"] = "Announce again",
            ["type"] = "intermediateSignalThrowEvent",
            ["signal"] = new JsonObject
            {
                ["name"] = signalName,
                ["scope"] = new JsonObject { ["kind"] = "thisWorkflow" }
            }
        });
        Flows(model).Add(new JsonObject { ["id"] = 203, ["name"] = "Announce", ["sourceRef"] = 2, ["targetRef"] = 7 });
        Flows(model).Add(new JsonObject { ["id"] = 701, ["sourceRef"] = 7, ["targetRef"] = 3 });
    }

    public static string Json(Action<JsonObject> change)
    {
        var model = Base();
        change(model);
        return model.ToJsonString();
    }

    public static JsonArray Nodes(JsonObject model) => model["flowNodes"]!.AsArray();

    public static JsonArray Flows(JsonObject model) => model["sequenceFlows"]!.AsArray();

    public static JsonObject Node(JsonObject model, int id) =>
        Nodes(model).Select(node => node!.AsObject()).Single(node => (int)node["id"]! == id);

    public static JsonObject Flow(JsonObject model, int id) =>
        Flows(model).Select(flow => flow!.AsObject()).Single(flow => (int)flow["id"]! == id);

    public static JsonObject Signal(JsonObject model, int id) => Node(model, id)["signal"]!.AsObject();

    public static JsonArray Mappings(JsonObject model, int id) => Signal(model, id)["outputMappings"]!.AsArray();

    /// <summary>Makes the optional mapping reason a number mapping into the given variable.</summary>
    public static void NumberMapping(JsonObject model, string variable)
    {
        var mapping = Mappings(model, 1)[1]!.AsObject();
        mapping["variable"] = variable;
        mapping["dataType"] = "number";
        mapping["defaultValue"] = 1;
    }

    /// <summary>Adds signal start #5 on the given name, leading to task #2 through flow 501.</summary>
    public static void AddSecondStart(JsonObject model, string signalName)
    {
        Nodes(model).Add(new JsonObject
        {
            ["id"] = 5,
            ["name"] = "Recall reopened",
            ["type"] = "signalStartEvent",
            ["signal"] = new JsonObject { ["name"] = signalName, ["outputMappings"] = new JsonArray() }
        });
        Flows(model).Add(new JsonObject { ["id"] = 501, ["sourceRef"] = 5, ["targetRef"] = 2 });
    }
}
