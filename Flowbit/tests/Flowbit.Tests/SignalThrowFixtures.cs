using System.Text.Json.Nodes;

namespace Flowbit.Tests;

/// <summary>
/// Signal throw and end event definitions and the first problem that both the server's definition validator and the
/// editor's save validator must report for them, with the same wording, so the two cannot drift. A null problem means
/// the definition is valid. Every case changes a copy of <see cref="Base"/>: start #1 with the optional string input
/// orderNumber, user task #2, signal throw #7 throwing 'orders.cancelled' to this instance with the required payload
/// entry reason from cancelReason, end #8, signal catch #9 waiting for 'orders.cancelled', end #10, and signal end #12
/// throwing 'warehouse.product-recall' to the workflow family example-signal-boundaries-recall. Flows 101, 201 (task to
/// throw), 202 (task to catch), 203 (task to signal end), 701 (throw to end), and 901 (catch to end) join them. Its
/// process variables are the instance variables cancelReason (string) and amount (number), the read/write shared alias
/// rate (number), and the read-only shared alias region (string).
/// </summary>
internal static class SignalThrowFixtures
{
    private const string Throw = "Signal throw event #7";
    private const string End = "Signal end event #12";
    private const string InvalidName =
        " signal.name must be 1 to 100 lowercase letters, digits, '.', '_', or '-', starting with a letter or digit.";
    private const string KindProblem =
        " signal.scope.kind must be 'thisInstance', 'thisWorkflow', 'workflow', 'businessKey', 'ownBusinessKey', or 'allWorkflows'.";
    private const string OwnKeyProblem =
        " uses this instance's business key, but this workflow's entry events do not configure businessKey.";
    private const string PayloadNameProblem =
        " name must be 1 to 100 letters, digits, '_', or '-', starting with a letter or '_'.";

    private const string BaseJson = """
        {
          "id": "signal-throw-fixture",
          "name": "Signal throw fixture",
          "initialEventId": 1,
          "lanes": [],
          "variables": [
            { "id": 1, "name": "cancelReason", "dataType": "string", "isArray": false, "nullable": true,
              "required": false, "defaultValue": null, "validation": null },
            { "id": 2, "name": "amount", "dataType": "number", "isArray": false, "nullable": true,
              "required": false, "defaultValue": null, "validation": null },
            { "id": 3, "name": "rate", "dataType": "number", "isArray": false, "nullable": false,
              "required": false, "defaultValue": null, "validation": null,
              "scope": "shared", "sharedKey": "examples.signals.rate", "access": "readWrite" },
            { "id": 4, "name": "region", "dataType": "string", "isArray": false, "nullable": false,
              "required": false, "defaultValue": null, "validation": null,
              "scope": "shared", "sharedKey": "examples.signals.region", "access": "read" }
          ],
          "flowNodes": [
            { "id": 1, "name": "Start", "type": "startEvent",
              "variables": [ { "id": 11, "name": "orderNumber", "dataType": "string", "isArray": false, "required": false } ] },
            { "id": 2, "name": "Review", "type": "userTask" },
            {
              "id": 7,
              "name": "Announce the cancellation",
              "type": "intermediateSignalThrowEvent",
              "signal": {
                "name": "orders.cancelled",
                "scope": { "kind": "thisInstance" },
                "payload": [ { "name": "reason", "variable": "cancelReason", "required": true } ]
              }
            },
            { "id": 8, "name": "Cancelled", "type": "endEvent" },
            { "id": 9, "name": "Wait for the cancellation", "type": "intermediateSignalCatchEvent",
              "signal": { "name": "orders.cancelled", "outputMappings": [] } },
            { "id": 10, "name": "Stopped", "type": "endEvent" },
            {
              "id": 12,
              "name": "Recall announced",
              "type": "signalEndEvent",
              "signal": {
                "name": "warehouse.product-recall",
                "scope": { "kind": "workflow", "workflowKey": "example-signal-boundaries-recall" }
              }
            }
          ],
          "sequenceFlows": [
            { "id": 101, "sourceRef": 1, "targetRef": 2 },
            { "id": 201, "name": "Cancel", "sourceRef": 2, "targetRef": 7 },
            { "id": 202, "name": "Wait", "sourceRef": 2, "targetRef": 9 },
            { "id": 203, "name": "Recall", "sourceRef": 2, "targetRef": 12 },
            { "id": 701, "sourceRef": 7, "targetRef": 8 },
            { "id": 901, "sourceRef": 9, "targetRef": 10 }
          ]
        }
        """;

    public static JsonObject Base() => JsonNode.Parse(BaseJson)!.AsObject();

    /// <summary>(case, definition JSON, first problem). A null problem means the definition is valid.</summary>
    public static IEnumerable<(string Case, string Json, string? Problem)> Cases()
    {
        yield return ("a throw to this instance and an end event to a family", Json(_ => { }), null);
        yield return ("a throw to this workflow", Json(model => Scope(model, 7, new JsonObject { ["kind"] = "thisWorkflow" })), null);
        yield return ("a throw to the definition's own family",
            Json(model => Scope(model, 7, new JsonObject { ["kind"] = "workflow", ["workflowKey"] = "signal-throw-fixture" })), null);
        yield return ("a throw to a business key from a variable",
            Json(model => Scope(model, 7, BusinessKeyScope("orderNumber"))), null);
        yield return ("a throw to this instance's business key",
            Json(model =>
            {
                EnableBusinessKeys(model);
                Scope(model, 7, new JsonObject { ["kind"] = "ownBusinessKey", ["workflowKey"] = "example-signal-boundaries-recall" });
            }), null);
        yield return ("an end event to all workflows",
            Json(model => Scope(model, 12, new JsonObject { ["kind"] = "allWorkflows" })), null);
        yield return ("a payload with the instance ID and the business key",
            Json(model =>
            {
                EnableBusinessKeys(model);
                Payload(model, 7).Add(new JsonObject { ["name"] = "orderInstance", ["source"] = "instanceId" });
                Payload(model, 7).Add(new JsonObject { ["name"] = "order_key", ["source"] = "businessKey", ["required"] = true });
            }), null);
        yield return ("a payload of any declared variable",
            Json(model => Payload(model, 7).Add(new JsonObject { ["name"] = "orderNumber", ["variable"] = "orderNumber" })), null);
        yield return ("an empty payload", Json(model => Signal(model, 7)["payload"] = new JsonArray()), null);
        yield return ("no payload", Json(model => Signal(model, 7).Remove("payload")), null);
        yield return ("a null payload", Json(model => Signal(model, 7)["payload"] = null), null);
        yield return ("exactly 100 payload entries", Json(model => Signal(model, 7)["payload"] = Entries(100)), null);
        yield return ("empty output mappings on a throw", Json(model => Signal(model, 7)["outputMappings"] = new JsonArray()), null);

        yield return ("no signal on a throw", Json(model => Node(model, 7).Remove("signal")), Throw + " must define signal.name.");
        yield return ("an empty name on an end event", Json(model => Signal(model, 12)["name"] = ""), End + " must define signal.name.");
        yield return ("an uppercase name", Json(model => Signal(model, 7)["name"] = "Orders.cancelled"), Throw + InvalidName);
        yield return ("an invalid name on an end event", Json(model => Signal(model, 12)["name"] = "-recall"), End + InvalidName);

        yield return ("a throw without an incoming flow", Json(model => Flow(model, 201)["targetRef"] = 8),
            Throw + " must have at least one incoming sequence flow.");
        yield return ("a throw with two outgoing flows",
            Json(model => Flows(model).Add(new JsonObject { ["id"] = 702, ["sourceRef"] = 7, ["targetRef"] = 10 })),
            Throw + " must have exactly one outgoing sequence flow.");
        yield return ("a throw without an outgoing flow",
            Json(model => Flows(model).Remove(Flow(model, 701))),
            Throw + " must have exactly one outgoing sequence flow.");
        yield return ("a conditional outgoing flow", Json(model => Flow(model, 701)["condition"] = "amount > 1"),
            Throw + " must have one unconditional outgoing sequence flow without user-action or multi-instance metadata.");
        yield return ("an end event with an outgoing flow",
            Json(model => Flows(model).Add(new JsonObject { ["id"] = 1201, ["sourceRef"] = 12, ["targetRef"] = 10 })),
            "End event #12 cannot have outgoing sequence flows.");
        yield return ("an end event without an incoming flow", Json(model => Flows(model).Remove(Flow(model, 203))),
            "End event #12 must have at least one incoming sequence flow.");

        yield return ("output mappings on a throw",
            Json(model => Signal(model, 7)["outputMappings"] = new JsonArray(new JsonObject
            {
                ["variable"] = "amount", ["path"] = "amount", ["dataType"] = "number", ["isArray"] = false, ["required"] = false
            })),
            Throw + " cannot define output mappings; it sends signal.payload.");

        yield return ("no scope", Json(model => Signal(model, 7).Remove("scope")), Throw + KindProblem);
        yield return ("a null scope", Json(model => Signal(model, 12)["scope"] = null), End + KindProblem);
        yield return ("a scope without a kind", Json(model => Scope(model, 7, new JsonObject())), Throw + KindProblem);
        yield return ("an unknown kind", Json(model => Scope(model, 7, new JsonObject { ["kind"] = "instance" })), Throw + KindProblem);
        yield return ("a kind in another casing", Json(model => Scope(model, 7, new JsonObject { ["kind"] = "ThisInstance" })),
            Throw + KindProblem);

        yield return ("a workflow scope without a key", Json(model => Scope(model, 12, new JsonObject { ["kind"] = "workflow" })),
            End + " signal.scope needs a workflowKey for kind 'workflow'.");
        yield return ("a blank workflow key",
            Json(model => Scope(model, 12, new JsonObject { ["kind"] = "workflow", ["workflowKey"] = "  " })),
            End + " signal.scope needs a workflowKey for kind 'workflow'.");
        yield return ("an own business key scope without a key",
            Json(model =>
            {
                EnableBusinessKeys(model);
                Scope(model, 7, new JsonObject { ["kind"] = "ownBusinessKey" });
            }),
            Throw + " signal.scope needs a workflowKey for kind 'ownBusinessKey'.");
        yield return ("a workflow key with surrounding white space",
            Json(model => Scope(model, 12, new JsonObject { ["kind"] = "workflow", ["workflowKey"] = " shipping" })),
            End + " signal.scope.workflowKey cannot have surrounding whitespace.");
        yield return ("a workflow key of 301 characters",
            Json(model => Scope(model, 12, new JsonObject { ["kind"] = "workflow", ["workflowKey"] = new string('k', 301) })),
            End + " signal.scope.workflowKey must contain at most 300 characters.");
        yield return ("a workflow key of 300 characters",
            Json(model => Scope(model, 12, new JsonObject { ["kind"] = "workflow", ["workflowKey"] = new string('k', 300) })),
            null);
        yield return ("a templated workflow key",
            Json(model => Scope(model, 12, new JsonObject { ["kind"] = "workflow", ["workflowKey"] = "${targetKey}" })),
            End + " signal.scope.workflowKey is a literal key; ${...} is not replaced.");
        yield return ("a workflow key on this instance",
            Json(model => Scope(model, 7, new JsonObject { ["kind"] = "thisInstance", ["workflowKey"] = "shipping" })),
            Throw + " signal.scope cannot define workflowKey for kind 'thisInstance'.");
        yield return ("an empty workflow key on all workflows",
            Json(model => Scope(model, 12, new JsonObject { ["kind"] = "allWorkflows", ["workflowKey"] = "" })),
            End + " signal.scope cannot define workflowKey for kind 'allWorkflows'.");

        yield return ("a business key scope without a variable",
            Json(model => Scope(model, 7, new JsonObject { ["kind"] = "businessKey", ["workflowKey"] = "shipping" })),
            Throw + " signal.scope needs a businessKeyVariable for kind 'businessKey'; use kind 'ownBusinessKey' for this instance's own business key.");
        yield return ("a key variable on a family scope",
            Json(model => Scope(model, 12, new JsonObject
            {
                ["kind"] = "workflow", ["workflowKey"] = "shipping", ["businessKeyVariable"] = "orderNumber"
            })),
            End + " signal.scope cannot define businessKeyVariable for kind 'workflow'.");
        yield return ("a key variable on an own business key scope",
            Json(model =>
            {
                EnableBusinessKeys(model);
                Scope(model, 7, new JsonObject
                {
                    ["kind"] = "ownBusinessKey", ["workflowKey"] = "shipping", ["businessKeyVariable"] = "orderNumber"
                });
            }),
            Throw + " signal.scope cannot define businessKeyVariable for kind 'ownBusinessKey'.");
        yield return ("an undeclared key variable", Json(model => Scope(model, 7, BusinessKeyScope("orderNo"))),
            Throw + " signal.scope.businessKeyVariable 'orderNo' is never declared or produced by this workflow.");
        yield return ("a number key variable", Json(model => Scope(model, 7, BusinessKeyScope("amount"))),
            Throw + " signal.scope.businessKeyVariable 'amount' is not declared as string.");
        yield return ("a shared key variable", Json(model => Scope(model, 7, BusinessKeyScope("region"))),
            Throw + " signal.scope.businessKeyVariable 'region' is a shared-variable alias, which a signal scope cannot read.");
        yield return ("a key variable in another casing", Json(model => Scope(model, 7, BusinessKeyScope("ORDERNUMBER"))), null);
        yield return ("an own business key without business keys",
            Json(model => Scope(model, 7, new JsonObject { ["kind"] = "ownBusinessKey", ["workflowKey"] = "shipping" })),
            Throw + OwnKeyProblem);

        yield return ("101 payload entries", Json(model => Signal(model, 7)["payload"] = Entries(101)),
            Throw + " defines more than 100 payload entries.");
        yield return ("a null payload entry", Json(model => Payload(model, 7).Add(null)),
            Throw + " payload cannot contain null entries.");
        yield return ("a payload name with a dot",
            Json(model => Payload(model, 7).Insert(0, new JsonObject { ["name"] = "order.reason", ["variable"] = "cancelReason" })),
            Throw + " payload entry 1" + PayloadNameProblem);
        yield return ("a payload name starting with a digit",
            Json(model => Payload(model, 7).Add(new JsonObject { ["name"] = "1reason", ["variable"] = "cancelReason" })),
            Throw + " payload entry 2" + PayloadNameProblem);
        yield return ("a payload entry without a name",
            Json(model => Payload(model, 7).Add(new JsonObject { ["variable"] = "cancelReason" })),
            Throw + " payload entry 2" + PayloadNameProblem);
        yield return ("a payload name of 101 characters",
            Json(model => Payload(model, 7).Add(new JsonObject { ["name"] = "r" + new string('e', 100), ["variable"] = "amount" })),
            Throw + " payload entry 2" + PayloadNameProblem);
        yield return ("a payload name used twice ignoring case",
            Json(model => Payload(model, 7).Add(new JsonObject { ["name"] = "Reason", ["variable"] = "amount" })),
            Throw + " payload sends 'Reason' more than once.");
        yield return ("a payload entry without a value",
            Json(model => Payload(model, 7).Add(new JsonObject { ["name"] = "note" })),
            Throw + " payload entry 'note' must define either variable or source.");
        yield return ("a payload entry with both values",
            Json(model => Payload(model, 7).Add(new JsonObject { ["name"] = "note", ["variable"] = "amount", ["source"] = "instanceId" })),
            Throw + " payload entry 'note' must define either variable or source.");
        yield return ("an unknown payload source",
            Json(model => Payload(model, 7).Add(new JsonObject { ["name"] = "sender", ["source"] = "user" })),
            Throw + " payload entry 'sender' source must be 'instanceId' or 'businessKey'.");
        yield return ("a business key source without business keys",
            Json(model => Payload(model, 7).Add(new JsonObject { ["name"] = "orderKey", ["source"] = "businessKey" })),
            Throw + OwnKeyProblem);
        yield return ("an undeclared payload variable",
            Json(model => Payload(model, 7).Add(new JsonObject { ["name"] = "note", ["variable"] = "cancelNote" })),
            Throw + " payload entry 'note' reads 'cancelNote', which is never declared or produced by this workflow.");
        yield return ("a shared payload variable",
            Json(model => Payload(model, 7).Add(new JsonObject { ["name"] = "rate", ["variable"] = "rate" })),
            Throw + " payload entry 'rate' reads shared-variable alias 'rate', which a signal payload cannot read.");
        yield return ("a read-only shared payload variable on an end event",
            Json(model => Signal(model, 12)["payload"] = new JsonArray(new JsonObject { ["name"] = "region", ["variable"] = "REGION" })),
            End + " payload entry 'region' reads shared-variable alias 'REGION', which a signal payload cannot read.");

        yield return ("this instance without a wait for the name",
            Json(model => Signal(model, 9)["name"] = "orders.closed"),
            Throw + " throws 'orders.cancelled' to this instance, but no signal catch or boundary event in this workflow waits for it.");
        yield return ("this instance where only another throw has the name",
            Json(model =>
            {
                Signal(model, 9)["name"] = "orders.closed";
                Signal(model, 12)["name"] = "orders.cancelled";
            }),
            Throw + " throws 'orders.cancelled' to this instance, but no signal catch or boundary event in this workflow waits for it.");

        yield return ("a scope on a catch",
            Json(model => Signal(model, 9)["scope"] = new JsonObject { ["kind"] = "thisInstance" }),
            "Signal catch event #9 cannot define signal.scope or signal.payload; only signal throw and end events send a signal.");
        yield return ("a payload on a catch",
            Json(model => Signal(model, 9)["payload"] = new JsonArray()),
            "Signal catch event #9 cannot define signal.scope or signal.payload; only signal throw and end events send a signal.");
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

    public static JsonArray Payload(JsonObject model, int id) => Signal(model, id)["payload"]!.AsArray();

    public static void Scope(JsonObject model, int id, JsonObject scope) => Signal(model, id)["scope"] = scope;

    /// <summary>Makes orderNumber a required start input and the start's active business key.</summary>
    public static void EnableBusinessKeys(JsonObject model)
    {
        var start = Node(model, 1);
        start["variables"]![0]!["required"] = true;
        start["businessKey"] = new JsonObject { ["variable"] = "orderNumber", ["uniqueness"] = "active" };
    }

    private static JsonObject BusinessKeyScope(string variable) => new()
    {
        ["kind"] = "businessKey", ["workflowKey"] = "example-signal-boundaries-recall", ["businessKeyVariable"] = variable
    };

    private static JsonArray Entries(int count)
    {
        var entries = new JsonArray();
        for (var index = 0; index < count; index++)
        {
            entries.Add(new JsonObject { ["name"] = "value" + index, ["variable"] = "cancelReason" });
        }

        return entries;
    }
}
