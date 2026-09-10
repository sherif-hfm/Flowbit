# Workflow nodes and properties

[Documentation home](index.md) · [BPMN behavior](bpmn-support.md) · [Developer guide](developer-guide.md) · [Example catalog](../examples/README.md)

Use this reference when choosing a task, event, or gateway and authoring its Flowbit JSON properties. It covers all **21 supported node types**, their shared and nested configuration, and the sequence flows that supply actions and routing. For execution semantics across a whole process, read [BPMN support](bpmn-support.md); for HTTP requests, read the [API reference](api-guide.md).

The tables describe current authoring contracts and effective defaults. **Required** means needed for that configuration to work and pass validation; **conditional** means required only in the stated situation. Omit optional properties that do not apply to the selected type. The model uses one shared node class: a property existing on that class does not make it meaningful on every node. Some irrelevant legacy fields are cleared during normalization, while explicitly invalid combinations are rejected. Do not rely on normalization to repair newly authored definitions.

JSON blocks below are **fragments**, not standalone workflows, unless stated otherwise. Node fragments belong in `flowNodes`; flow fragments belong in `sequenceFlows`. Referenced nodes, flows, variables, credentials, and settings must exist in the containing workflow or deployment. Each type links to a complete example where available.

## Contents

- [Node catalog](#node-catalog)
- [Common node properties](#common-node-properties)
- [Tasks](#tasks)
- [Events](#events)
- [Gateways](#gateways)
- [Message configuration](#message-configuration)
- [Business key configuration](#business-key-configuration)
- [Start idempotency configuration](#start-idempotency-configuration)
- [Timer configuration](#timer-configuration)
- [Conditional configuration](#conditional-configuration)
- [Boundary host rules](#boundary-host-rules)
- [Durable job policy](#durable-job-policy)
- [Sequence flows](#sequence-flows)
- [Variables and inputs](#variables-and-inputs)
- [Output mappings](#output-mappings)
- [Authoring and compatibility](#authoring-and-compatibility)

## Node catalog

| Category | JSON `type` | Purpose |
| --- | --- | --- |
| Task | [task](#task) | Automatic pass-through step. |
| Task | [userTask](#usertask) | Human work, including parallel or sequential multi-instance work. |
| Task | [serviceTask](#servicetask) | Outbound REST request and typed response mappings. |
| Task | [scriptTask](#scripttask) | NCalc assignments or sandboxed JavaScript. |
| Start | [startEvent](#startevent) | Authenticated user starts with roles and inputs. |
| Start | [messageStartEvent](#messagestartevent) | Machine-authenticated inbound message starts. |
| Start | [timerStartEvent](#timerstartevent) | Scheduled instance creation. |
| Catch | [intermediateMessageCatchEvent](#intermediatemessagecatchevent) | Wait for an inbound message. |
| Catch | [intermediateTimerCatchEvent](#intermediatetimercatchevent) | Wait for a scheduled trigger. |
| Catch | [intermediateConditionalCatchEvent](#intermediateconditionalcatchevent) | Wait for a persisted-variable condition. |
| Boundary | [errorBoundaryEvent](#errorboundaryevent) | Handle an attached service/script failure. |
| Boundary | [timerBoundaryEvent](#timerboundaryevent) | Deadline or reminder while a host is active. |
| Boundary | [conditionalBoundaryEvent](#conditionalboundaryevent) | Interrupt or branch when a host's observed condition becomes true. |
| Interrupt | [scopedInterruptEvent](#scopedinterruptevent) | Interrupt a particular active gateway scope. |
| End | [endEvent](#endevent) | Complete this token. |
| End | [terminateEndEvent](#terminateendevent) | Complete the instance and cancel its remaining work. |
| End | [errorEndEvent](#errorendevent) | Fault the instance and cancel its remaining work. |
| Gateway | [exclusiveGateway](#exclusivegateway) | Choose one branch or merge arrivals without synchronization. |
| Gateway | [parallelGateway](#parallelgateway) | Start all branches or synchronize all static inputs. |
| Gateway | [inclusiveGateway](#inclusivegateway) | Start matching branches or merge using reachability. |
| Gateway | [complexGateway](#complexgateway) | Stateful activation and reset conditions. |

Timers and conditional `durableAsync` delivery require a running Worker. Task async flags also require a Worker. The [BPMN catalog](bpmn-support.md#supported-node-types) gives the prerequisites for each type; [deployment](deployment.md#worker-operation) explains setup.

## Common node properties

These properties apply to every `flowNodes` element. Type-specific tables below add to this table.

| Property | JSON type | Requirement / omitted behavior | Meaning and constraints |
| --- | --- | --- | --- |
| `id` | integer | Author explicitly; CLR default is `0`. | Authored node identifier, unique within `flowNodes`. Flows and attachments reference this value. It is not a runtime task, token, or execution ID. Positive IDs are a useful authoring convention, not a blanket server validation rule. |
| `name` | string | Required; blank is rejected. | Display name of the node. |
| `type` | string | Author explicitly; deserialization defaults to `userTask`. | One of the exact canonical names in the [catalog](#node-catalog). Unknown names are rejected. |
| `externalId` | string | Optional; absent/null. | Application-facing identifier. Message starts have additional uniqueness and selection rules described below. This does not replace `id`. |
| `attributes` | array of objects | Optional; `[]`. | Ordered application metadata, such as a form identifier; does not change authorization or execution. See the entry schema below. |
| `laneId` | integer or null | Optional; null. | Editor lane association. Lanes organize layout and grant no runtime permissions or cancellation scope. |
| `x` | integer | Optional; `0`. | Horizontal editor coordinate. |
| `y` | integer | Optional; `0`. | Vertical editor coordinate. |

An `attributes` entry has the following complete shape. At most **100 entries** are allowed on each node or sequence flow.

| Property | JSON type | Requirement | Meaning and constraints |
| --- | --- | --- | --- |
| `key` | string | Required, nonblank. | Trimmed by normalization, unique case-insensitively within the owning element, maximum 300 Unicode scalar values. |
| `value` | string | Required; empty string is allowed. | Opaque text, maximum 4,000 Unicode scalar values; not an executable expression. |

```json
{
  "id": 20,
  "name": "Review request",
  "type": "userTask",
  "externalId": "review-request",
  "laneId": 1,
  "x": 420,
  "y": 180,
  "attributes": [{ "key": "form", "value": "purchase-review" }]
}
```

The containing workflow still needs a start, routes, and an end. Geometry does not determine execution order; `sequenceFlows` does.

## Tasks

All four task types support the [common node properties](#common-node-properties) and optional `asyncBefore`, `asyncAfter`, and `job` settings described under [durable job policy](#durable-job-policy). Omitted async flags keep execution synchronous. A task does not require the Worker unless its configuration introduces a durable boundary or wait.

### task

An automatic pass-through activity. Entering it records the visit and follows its single outgoing sequence flow; it does not create inbox work, invoke a connector, or evaluate a script. Use it for an explicit process milestone that needs no authored operation.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `type` | string | Set to `"task"` | Automatic activity. |
| `asyncBefore`, `asyncAfter`, `job` | Boolean / object | Optional; flags `false` | Shared [durable job policy](#durable-job-policy). |

There are no additional task-specific settings. This type has no role, ownership, or input-form behavior. Omit user-task, service, and script settings; normalization clears some irrelevant fields and validation rejects unsupported combinations. Give it exactly one unconditional outgoing flow.

```json
{
  "id": 2,
  "type": "task",
  "name": "Record processing milestone",
  "asyncAfter": true
}
```

### userTask

Creates waiting human work. A normal task has one work item; a multi-instance task has multiple child items under one parent execution. The user selects an eligible outgoing [sequence flow](#sequence-flows) to complete work. At least one outgoing flow is required. A normal user task cannot have a default flow.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `type` | string | Set to `"userTask"` | Waiting human activity. |
| `roles` | string[] | Optional; `[]` | The actor must hold at least one listed role, compared case-insensitively. Blank entries are removed and roles trimmed/deduplicated. Empty literal lists add no role restriction within the authenticated endpoint scope. Cannot be nonempty with `rolesVariable`. |
| `rolesVariable` | string | Optional; absent | Name of a declared process `string[]` variable, including a shared alias. Resolved once when work is created, together with outgoing action role sources. See captured roles below. |
| `requiresClaim` | Boolean | Optional; `false` | Shared-pool actions require claim ownership when true, except an authorized flow-level claim bypass. Direct assignment is a separate restriction. |
| `claimMode` | string | Optional; `"fresh"` | `fresh` starts unclaimed; `previous` inherits the latest eligible human action's actor; `fromNode` searches eligible actions at the referenced user task. Non-fresh modes require `requiresClaim: true`. |
| `inheritClaimFromNodeId` | integer | Required for `claimMode: "fromNode"`; otherwise cleared | Existing user-task node ID. No matching prior actor leaves work unclaimed; automatic gateway/service/script history does not supply one. Delegated actions use the represented owner. |
| `assignee` | string | Optional; absent | NCalc expression evaluated once on entry to a normal task, for example `"requestOwner"` or `"'alice'"`. A trimmed, nonempty string of at most 300 UTF-16 code units becomes the direct assignee. Missing/failed/non-string/oversized results leave it unassigned. Forbidden with multi-instance work or inherited assignment modes. |
| `requiresAssignment` | Boolean | Optional; `false` | Hides unassigned work from personal inbox/task access until a manager or distributor assigns it. Requires workflow `taskAssignmentRoles` or complete `taskDistribution` credentials, `requiresClaim: false`, `claimMode: "fresh"`, and no multi-instance configuration. |
| `assignmentMode` | string | Optional; `"fresh"` | `fresh` uses the optional assignee expression or leaves work unassigned. `previous` uses the latest completed work item; `fromNode` uses the latest completed item at a specified user task. Non-fresh modes require `requiresAssignment: true` and no `assignee` expression. |
| `inheritAssignmentFromNodeId` | integer | Required for `assignmentMode: "fromNode"`; otherwise cleared | Existing user-task node ID. Inheritance prefers the source assignee, then its represented completing owner, then its completing actor. No eligible source leaves work in the assignment queue. |
| `inboxVisibilityCondition` | string | Optional; absent/blank means no extra restriction | Bounded database expression deciding personal inbox membership and access. See the visibility contract below. |
| `variables` | variable[] | Optional; `[]` | Retained node-scope declarations, using [variable properties](#variables-and-inputs). Current user-action endpoints validate and resolve inputs from the selected outgoing **flow's** `variables`; author action forms there. This node list does not create a second submission contract. |
| `multiInstance` | object | Optional; absent means one normal work item | Repeated human work; see all properties below. |
| `asyncBefore`, `asyncAfter`, `job` | Boolean / object | Optional; flags `false` | Shared [durable job policy](#durable-job-policy). For multi-instance work, boundaries apply once to the parent, not once per child. |

Task roles and action roles are separate checks: passing one does not bypass the other. Claim bypass (`canActWithoutClaim` and `canActWithoutClaimRoles`) belongs on the selected outgoing flow and never overrides direct assignment, roles, or visibility. Workflow `taskAssignmentRoles`, `taskRoleManagementRoles`, and `taskDistribution` are workflow-level settings, not node properties; see [ownership and role policies](developer-guide.md#apply-ownership-and-role-policies).

A `rolesVariable` value must resolve to a nonempty string array with at most 100 entries, each trimmed role at most 300 Unicode scalar values. Missing values, wrong types, empty normalized lists, and oversized lists fail closed. Waiting work retains its captured task/action roles when variables change, pending children activate, or ownership changes. Authorized role managers can replace the saved policy through the roles API; this preserves existing ownership and completed children. Explicit empty literal/manual role lists remain unrestricted.

`inboxVisibilityCondition` supports Boolean composition, scalar comparisons, arithmetic, variable-to-variable operands, and `Number(expr)`. It accepts declared scalar instance producers with consistent types (`string`, `number`, `boolean`, `date`, `datetime`), configured `config.*`/`setting.*` values, allowlisted `sys.claim.*` claims, and scalar `sys.user`, `sys.actingFor`, `sys.workflowName`, `sys.nodeName`, `sys.now`, `sys.today`, `sys.instanceId`, `sys.workflowId`, and `sys.nodeId` context. This is a bounded language, not unrestricted NCalc. Only exact true makes a task visible; missing/null/wrongly typed operands and failed arithmetic fail closed. It runs before inbox representative selection, count, order, and paging, and personal detail/actions recheck it (hidden work returns `404`). Management, distribution, and administrative surfaces keep their own privileged scope; configured recovery roles can bypass it for unclaim only.

Visibility limits are 4,096 UTF-8 bytes, expression depth 8, 64 compiled instructions, 16 comparisons, 8 distinct instance-variable references, 16 external references, and 16 distinct literals. String literals allow 512 UTF-8 bytes; numeric literals allow 128 characters and exponent magnitude at most 100. For example, `"[amount] > Number([config.ReviewLimit]) and [approved] == false"` is valid when those variables/context values exist with appropriate types.

Example: a pooled reviewer task; give its outgoing actions their own names, optional roles, conditions, and input declarations.

```json
{
  "id": 2,
  "type": "userTask",
  "name": "Review request",
  "roles": ["Reviewer"],
  "requiresClaim": true,
  "claimMode": "fresh"
}
```

Examples: [roles, claims, and bypass](../examples/user-tasks/01-roles-claim-and-bypass.json), [claim inheritance](../examples/user-tasks/02-claim-inheritance.json), and [required assignment and distribution](../examples/user-tasks/04-required-assignment-and-distribution.json).

#### Multi-instance properties

These are properties of the `userTask.multiInstance` object; multi-instance behavior is supported only for user tasks.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `mode` | string | Optional; `"parallel"` | `parallel` activates all children; `sequential` activates one at a time, leaving the rest pending. |
| `source` | string | Optional; `"collection"` | `collection` snapshots a username array; `cardinality` evaluates a count. |
| `collectionVariable` | string | Required for `collection`; otherwise cleared | Declared process `string[]` variable. Each entry creates one directly assigned child. Runtime entries must be nonempty trimmed usernames of at most 300 UTF-16 code units. Requires `requiresClaim: false`, `claimMode: "fresh"`. |
| `cardinalityExpression` | string | Required for `cardinality`; otherwise cleared | NCalc count evaluated on entry. Must produce an integer from 1 to the configured maximum. Children use the normal task role/claim pool. Requires `claimMode: "fresh"`; `requiresClaim` may be true. |
| `onePerActor` | Boolean | Optional; `false`; normalized to false for `collection` | Cardinality only: a username may complete at most one child in this execution, compared case-insensitively. The inbox returns one representative eligible item for that actor; simultaneous actors can see the same item, so stale competing actions return `409`. |
| `completionEvaluation` | string | Optional; `"afterEach"` | `afterEach` evaluates aggregate outcome conditions after each completed child and allows early completion. `afterAll` evaluates only when every child finishes. Interrupting flows remain immediate in both modes. |
| `resultVariable` | string | Required | Declared writable process variable with `dataType: "json"`, `isArray: false`. An instance-scoped variable must have a JSON array default (author `[]`); a shared alias uses its catalog binding. Stores the JSON result collection. Must differ from `collectionVariable`. |

Recognized enum casing is canonicalized; explicit null/unknown/misspelled `mode`, `source`, or `completionEvaluation` is invalid. Both collection size and cardinality are bounded by `Workflow.MultiInstance.MaxInstances` (default 1,000); an empty collection is invalid. Do not combine multi-instance work with node `assignee` or `requiresAssignment`.

Every multi-instance task requires at least one selectable non-default outcome and exactly one engine-only default fallback (`isDefault: true`, `isSelectable: false`). Each non-default outcome requires a `completionCondition` and unique positive `completionPriority`; lowest priority number wins. Conditions can use `CountFlow(flowId)`, `PercentFlow(flowId)`, `mi.total`, `mi.completed`, and `mi.remaining`. `CountFlow`/`PercentFlow` references must identify selectable outcomes of this task. The fallback has no condition/priority. Interrupting selectable flows set `cancelRemainingInstances: true` and cannot be default or define aggregate completion rules. Engine-only flows cannot carry user-action inputs, roles, visibility conditions, or claim bypass. See [sequence-flow properties](#sequence-flows).

When a condition wins or an interrupt occurs, the parent advances once and unfinished children are cancelled atomically. If all children finish with no match, the required default wins. `resultVariable` is reset to `[]` on entry and receives index-ordered result rows with child outcomes, submitted values, actor, normalized action-time roles (`userRoles`), and timestamps. A direct parent interrupt appends a final `kind: "parentInterrupt"` row after `kind: "item"` rows. Cancelled/non-action rows use null roles; an actor with no roles uses `[]`.

Example: define `panelSize` as a process `number` and `reviewResults` as a process `json` with `defaultValue: []`; connect flow 202 to an end node 3.

```json
{
  "flowNodes": [
    {
      "id": 2,
      "type": "userTask",
      "name": "Panel review",
      "roles": ["Reviewer"],
      "multiInstance": {
        "mode": "parallel",
        "source": "cardinality",
        "cardinalityExpression": "panelSize",
        "onePerActor": true,
        "completionEvaluation": "afterEach",
        "resultVariable": "reviewResults"
      }
    }
  ],
  "sequenceFlows": [
    {
      "id": 201,
      "sourceRef": 2,
      "targetRef": 3,
      "name": "Approve",
      "completionCondition": "CountFlow(201) >= 2",
      "completionPriority": 1
    },
    {
      "id": 202,
      "sourceRef": 2,
      "targetRef": 3,
      "name": "Fallback",
      "isDefault": true,
      "isSelectable": false
    }
  ]
}
```

For a collection variant, replace the object with `{"mode":"sequential","source":"collection","collectionVariable":"reviewers","resultVariable":"reviewResults"}` and declare `reviewers` as a `string[]`. See [complete multi-instance examples](../examples/multi-instance/README.md) for full definitions and prerequisites.

### serviceTask

Calls an external HTTP endpoint, then continues down one unconditional outgoing sequence flow. The only current connector is REST. A 2xx response is success; transport/configuration errors, non-2xx status, invalid mapped response data, or output validation failure fail the activity. An attached error boundary can catch the failure; otherwise synchronous execution rolls back and returns a domain error. Durable execution follows its [job policy](#durable-job-policy).

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `type` | string | Set to `"serviceTask"` | Automatic connector activity. |
| `service` | object | Required usable configuration | REST properties below. A missing object normalizes to an empty configuration and fails URL validation. |
| `asyncBefore`, `asyncAfter`, `job` | Boolean / object | Optional; flags `false` | Shared [durable job policy](#durable-job-policy). REST tasks accessing shared-variable bindings require `asyncBefore: true`. |

Properties of `service`:

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `type` | string | Optional; `"rest"` | Only `rest` is supported. Recognized casing normalizes; explicit null or unknown connector types are invalid. |
| `method` | string | Optional; `"GET"` | `GET`, `POST`, `PUT`, `PATCH`, or `DELETE` (validated case-insensitively). |
| `url` | string | Required | Absolute HTTP(S) destination after templating, without embedded credentials or a fragment. Supports `${variable}` substitution; missing URL placeholders fail before invocation. |
| `headers` | header[] | Optional; `[]` | Each entry has `name` and `value`, below. |
| `body` | string or null | Optional; absent/null/empty means no body | Request body template. Quoted placeholders insert JSON-escaped text; bare placeholders insert typed JSON. The resolved body is validated as JSON for default/JSON content types. |
| `timeoutSeconds` | integer | Optional; `30` | From 1 through `WorkflowServiceTasks:MaxTimeoutSeconds` (deployment default 300). |
| `statusVariable` | string | Optional; absent/blank | Writes HTTP status; 0 means no status was received, such as a preflight/transport failure. A declared process target must be scalar `number`; an undeclared instance target is supported. Must not overlap an output mapping, use a reserved context prefix, or exceed 300 Unicode scalar values. |
| `outputMappings` | mapping[] | Optional; `[]` | Typed extraction/default/validation rules applied to successful responses. See [all mapping properties](#output-mappings). |

Properties of each `headers` entry:

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `name` | string | Required | Static HTTP field name, trimmed, at most 300 characters, containing only valid HTTP token characters. `Host`, `Content-Length`, `Transfer-Encoding`, `Connection`, `TE`, `Trailer`, and `Upgrade` are forbidden. |
| `value` | string | Optional; `""` | Supports `${variable}` substitution; missing placeholders fail before invocation. Resolved values cannot contain CR/LF. `Content-Type`, if supplied for a body, must parse as a media type; default is `application/json; charset=utf-8`. |

Templates can use available variables and `sys.*`, `config.*`, and `setting.*` context. URL/header replacement uses scalar text without URL encoding. The body template `"{\"amount\":${amount},\"note\":\"By ${sys.user}\"}"` preserves a numeric amount while escaping the text. Missing body placeholders become empty text inside strings or `null` in bare positions; this differs from strict URL/header substitution.

Mappings are staged and validated before writes; a failed mapping does not leave partial mapped outputs. A caught error may retain the separately written status so its error path can branch on it. Without a catching boundary the synchronous transaction rolls back those writes. HTTP effects at the remote endpoint cannot be rolled back; durable retries therefore require an idempotent remote operation. The deployment response-size default is 1,048,576 bytes, configured with `WorkflowServiceTasks:MaxResponseBodyBytes`; see [deployment configuration](deployment.md).

```json
{
  "id": 2,
  "type": "serviceTask",
  "name": "Create external order",
  "service": {
    "type": "rest",
    "method": "POST",
    "url": "https://api.example.com/orders",
    "headers": [
      { "name": "Authorization", "value": "Bearer ${config.OrderApiToken}" }
    ],
    "body": "{\"amount\":${amount}}",
    "timeoutSeconds": 10,
    "statusVariable": "httpStatus",
    "outputMappings": [
      {
        "variable": "orderId",
        "path": "data.order.id",
        "dataType": "string",
        "isArray": false,
        "required": true
      }
    ]
  }
}
```

This fragment needs an available `amount` value and configured `config.OrderApiToken`; the example URL is a placeholder. See [REST examples](../examples/service-tasks/README.md) for runnable prerequisites, typed mappings, shared outputs, and error handling.

### scriptTask

Executes authored logic and follows exactly one unconditional outgoing flow without action or multi-instance metadata. Scripts write declared process variables; they do not create input forms. Writes are staged together, coerced to each target's type, validated, and committed only when the whole script succeeds. An attached error boundary can catch a failure without retaining partial script outputs.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `type` | string | Set to `"scriptTask"` | Automatic script activity. |
| `scriptFormat` | string | Optional; `"ncalc"` | `ncalc` or `javascript`. Recognized casing is canonicalized; explicit blank/null/unknown values are invalid when saving. |
| `assignments` | assignment[] | Optional; `[]` in NCalc mode | Ordered NCalc assignments; empty means no-op. Must not contain null entries. JavaScript mode cannot carry a nonempty list. |
| `script` | string | Required nonblank in JavaScript mode; otherwise absent | JavaScript body, parsed at definition save. NCalc mode cannot include a nonblank body. |
| `usesFlowInfo` | Boolean | Optional; see compatibility note | JavaScript opt-in for `execution.getFlowInfo`. Author explicit `true` when using it and `false` otherwise. NCalc cannot set it true. For older JavaScript definitions, omission infers a direct `execution.getFlowInfo(...)` call; normalization stores the Boolean. Aliased/computed access requires explicit true. |
| `asyncBefore`, `asyncAfter`, `job` | Boolean / object | Optional; flags `false` | Shared [durable job policy](#durable-job-policy). |

Each NCalc `assignments` entry has:

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `variable` | string | Required | Declared process target name, matched case-insensitively. A shared target must have write access. |
| `expression` | string | Required nonblank | Valid NCalc expression over current values and expression context. Later assignments can read earlier assignments' staged values. Supports instance-wide `FlowInfo` where used with its literal arguments. |

Scalar results assigned to an array target are wrapped as one-element arrays; existing arrays have their elements coerced. Numeric/Boolean conversions and final target validation are shared by both script formats. Example: declare `subtotal`, `tax`, and `total` as process numbers before using this fragment.

```json
{
  "id": 2,
  "type": "scriptTask",
  "name": "Calculate total",
  "scriptFormat": "ncalc",
  "assignments": [
    { "variable": "tax", "expression": "subtotal * 0.15" },
    { "variable": "total", "expression": "subtotal + tax" }
  ]
}
```

JavaScript uses the Jint engine with the `execution` API:

| Method | Behavior |
| --- | --- |
| `execution.getVariable(name)` | Returns the current typed value, or `null` when absent. |
| `execution.hasVariable(name)` | Checks whether the current context contains a value under that name. |
| `execution.getVariables()` | Returns the current variable/context map. |
| `execution.setVariable(name, value)` | Stages a write to a declared process variable; validates/coerces the target contract. Updating an object returned by a getter does not persist it until it is passed to this method. |
| `execution.getFlowInfo(flowId)` | Returns instance-wide action/traversal counts and last evidence for an existing flow when `usesFlowInfo: true`; an unknown flow or disabled capability fails. |

Scripts have no CLR, filesystem, or network access. `eval`/string compilation is disabled. Values crossing the API must be JSON-compatible: reject `undefined`, nonfinite numbers, `BigInt`, cyclic objects, and unsupported values. Null is accepted only by a compatible nullable target contract. Deployment limits bound time, statements, memory, recursion, stack, regex, arrays, and data crossing the API; see `WorkflowScript:*` in [deployment configuration](deployment.md). These limits are in-process safeguards for trusted workflow authors.

```json
{
  "id": 3,
  "type": "scriptTask",
  "name": "Normalize request owner",
  "scriptFormat": "javascript",
  "usesFlowInfo": false,
  "script": "const owner = execution.getVariable('requestOwner');\nexecution.setVariable('normalizedOwner', String(owner).trim().toLowerCase());"
}
```

Declare `normalizedOwner` as a process string and provide `requestOwner` in the current context. See [script examples](../examples/scripts/README.md) for NCalc helpers, JavaScript type conversions, flow evidence, and caught errors.

## Events

Every event also has the [common node properties](#common-node-properties). Set `type` to the exact name in its heading. Event properties listed here add to those common properties; task ownership, assignment, multi-instance configuration, `asyncBefore`, `asyncAfter`, and node `job` policies do not apply to events. Timers and durable conditionals use Worker jobs through their own event contracts.

The JSON examples below are **node fragments**, not complete importable workflows. Add the referenced nodes, declared variables, and sequence flows in a workflow definition. An unconditional event continuation has no condition, default flag, role/input/action configuration, or multi-instance metadata.

### startEvent

A manual entry point. An authorized user starts it through `POST /api/instances`; the runtime validates its inputs, initializes the instance, and follows its continuation. A workflow can have several starts. Workflow `initialEventId` selects the default manual start when the request omits `startEventId`.

**Topology:** no incoming flows; exactly one unconditional outgoing flow. **Worker:** not required by the start itself.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `roles` | string array | Optional; `[]` | Roles allowed to start through this entry. At least one caller role must match when the list is nonempty; matching is case-insensitive. An empty list adds no role restriction, but the normal start API still requires authentication. |
| `variables` | [input variable array](#variables-and-inputs) | Optional; `[]` | Inputs accepted from the start request. Required inputs must be supplied; optional defaults are applied. These are node-local input declarations, not shared-variable bindings. |
| `businessKey` | [business-key object](#business-key-configuration) | Optional; absent | Domain identity and uniqueness across the workflow family. If any manual/message entry defines it, all manual/message entries must define it. |
| `idempotency` | [start-idempotency object](#start-idempotency-configuration) | Optional; absent | Reserves a header-sourced transport retry key and writes its implicit string variable. |

```json
{
  "id": 1,
  "name": "Submit request",
  "type": "startEvent",
  "roles": ["Requester"],
  "variables": [
    { "id": 1, "name": "caseId", "dataType": "string", "isArray": false, "required": true }
  ],
  "businessKey": { "variable": "caseId", "uniqueness": "active" },
  "idempotency": { "headerName": "Idempotency-Key", "variable": "requestKey" }
}
```

See the [start-event examples](../examples/start-events/README.md).

### messageStartEvent

Starts an instance from an inbound JSON message through `POST /api/workflows/{workflowKey}/message-start`. The endpoint selects the published default workflow version and authenticates the machine using the event's message contract. Its typed output mappings are the complete start-input declarations; canonical JSON has no separate node `variables` section.

**Topology:** no incoming flows; exactly one unconditional outgoing flow. **Worker:** not required by this event.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `externalId` | string | Required when there are multiple message starts | The request's `startEvent` selector. With multiple message starts, every one needs a nonblank, case-sensitive unique external ID. It is otherwise an optional common property. |
| `message` | [message configuration](#message-configuration) | Required | Machine credentials, required correlation header, and typed payload mappings. Catch-only delivery-idempotency fields are rejected here. |
| `businessKey` | [business-key object](#business-key-configuration) | Optional; absent | References a required scalar-string message output mapping with an explicit path and no default. |
| `idempotency` | [start-idempotency object](#start-idempotency-configuration) | Optional; absent | Uses a request header and an implicit string variable, separately from payload mappings. |

```json
{
  "id": 2,
  "name": "Order received",
  "type": "messageStartEvent",
  "externalId": "order-received",
  "message": {
    "clientId": "order-provider",
    "clientSecret": "${config.orderProviderSecret}",
    "headerName": "X-Source",
    "headerValue": "orders",
    "outputMappings": [
      { "variable": "caseId", "path": "order.id", "dataType": "string", "isArray": false, "required": true }
    ]
  },
  "businessKey": { "variable": "caseId", "uniqueness": "all" },
  "idempotency": { "variable": "requestKey" }
}
```

Configure `config.orderProviderSecret` before using this fragment. Start authentication resolves server-controlled configuration/settings and available shared bindings before reading payload mappings; the request body cannot supply the expected credentials. There is no instance-variable snapshot yet. See [message integration](developer-guide.md#integrate-inbound-messages) and the [message examples](../examples/messages/README.md).

### timerStartEvent

Creates instances on a persisted schedule belonging to the published default workflow definition. Publishing/default selection activates the schedule through reconciliation; unpublishing or changing the default affects it. Scheduler occurrences use internal keys. This event cannot be selected as a manual start and does not use authored business keys or start transport idempotency.

**Topology:** no incoming flows; exactly one unconditional outgoing flow. **Worker:** required.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `timer` | [timer configuration](#timer-configuration) | Required | Exactly one absolute date, relative duration, or recurrence expression. Relative timing is based on schedule activation. |

There are no event-specific input variables or roles. Use top-level process-variable defaults to initialize scheduler-created instances; `variables` is omitted from canonical timer-start JSON. `businessKey` and `idempotency` are rejected.

```json
{
  "id": 3,
  "name": "Daily check",
  "type": "timerStartEvent",
  "timer": { "timeCycle": "R/P1D" }
}
```

See the [example catalog](../examples/README.md) for timer workflows.

### intermediateMessageCatchEvent

Persists a wait in an existing instance. `POST /api/instances/{instanceId}/message` authenticates the sender, verifies the required header, maps the raw JSON payload into variables, and follows the waiting token's continuation. It is an instance-scoped HTTP integration point, not a cross-instance message bus.

**Topology:** reached through the workflow; exactly one unconditional outgoing flow. **Worker:** not required by this event.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `externalId` | string | Optional | Exact, case-sensitive `catchEvent` request selector when multiple waiting catches need disambiguation. Give independently addressable waits distinct external IDs. |
| `message` | [message configuration](#message-configuration) | Required | Credentials, correlation contract, typed output mappings, and optional catch-delivery idempotency. |

The event carries no node `variables` input form. Credentials and correlation templates resolve against stored values and server-controlled context, before applying inbound outputs. All output mappings commit atomically. Timer/conditional boundaries may attach to this wait.

```json
{
  "id": 20,
  "name": "Wait for receipt",
  "type": "intermediateMessageCatchEvent",
  "externalId": "receipt",
  "message": {
    "clientId": "receipt-provider",
    "clientSecret": "${config.receiptProviderSecret}",
    "headerName": "X-Case-Id",
    "headerValue": "${caseId}",
    "deliveryIdempotency": true,
    "deliveryIdempotencyHeaderName": "X-Delivery-Key",
    "outputMappings": [
      { "variable": "accepted", "path": "receipt.accepted", "dataType": "boolean", "isArray": false, "required": true }
    ]
  }
}
```

Declare `caseId` and `accepted` with compatible types and configure the secret before use. See [inbound-message integration](developer-guide.md#integrate-inbound-messages).

### intermediateTimerCatchEvent

Persists a timed wait. The Worker resumes the token when its scheduled occurrence is due. A cyclic catch consumes a trigger and then leaves the node; it does not produce recurring sibling branches after departure.

**Topology:** at least one incoming flow; exactly one unconditional outgoing flow. **Worker:** required.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `timer` | [timer configuration](#timer-configuration) | Required | Exactly one timer expression. Relative durations start when the wait is activated. |

```json
{
  "id": 21,
  "name": "Wait thirty seconds",
  "type": "intermediateTimerCatchEvent",
  "timer": { "timeDuration": "PT30S" }
}
```

Timer and conditional boundaries may attach to this event. A Worker outage delays delivery; a finite cyclic catch with no eligible occurrence remaining opens an incident. See [timer semantics](bpmn-support.md#timers).

### intermediateConditionalCatchEvent

Waits for a Boolean condition over persisted instance variables. The condition is checked on entry, then on relevant transactional variable-write batches. An already-true condition can continue immediately. This observes stored variable changes; it does not poll an external service or wall-clock expression.

**Topology:** at least one incoming flow; exactly one unconditional outgoing flow. **Worker:** required only for `durableAsync` delivery.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `conditional` | [conditional configuration](#conditional-configuration) | Required | Observable condition plus `atomic` or `durableAsync` delivery. |

```json
{
  "id": 22,
  "name": "Wait for approval",
  "type": "intermediateConditionalCatchEvent",
  "conditional": { "condition": "approved == true", "deliveryMode": "durableAsync" }
}
```

Declare persisted instance variable `approved`. Timer/conditional boundaries cannot attach to a conditional catch. See the [conditional runtime reference](../Flowbit/README.md#intermediate-conditional-catch-events).

### errorBoundaryEvent

Catches a failure of its attached service or script task and routes through an error path. The failed host visit is recorded as faulted and the boundary path continues instead of the normal host continuation. It handles host failures rather than filtering authored `errorEndEvent` codes.

**Topology:** no incoming flows; exactly one unconditional outgoing flow. **Worker:** follows the host's execution mode.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `attachedToRef` | integer | Required | ID of an existing `serviceTask` or `scriptTask`. A host may have at most one error boundary. |
| `errorVariable` | string | Optional; absent | Captures the failure reason. Must be a nonblank name of at most 300 Unicode scalar values when configured, without `sys.`, `config.`, `setting.`, or `mi.` prefix. It can be an implicit runtime output; if a same-name process variable exists, it must be scalar `string`. It must not collide case-insensitively with the host service's status/output targets. |

```json
{
  "id": 30,
  "name": "Handle integration failure",
  "type": "errorBoundaryEvent",
  "attachedToRef": 10,
  "errorVariable": "integrationFailure"
}
```

The example assumes node `10` is a service/script task. Error boundaries always replace the failed host's continuation; `cancelActivity` is not supported. For async hosts, the host's [durable-job policy](#durable-job-policy) determines whether the boundary or retries are attempted first. See the [script boundary example](../examples/scripts/04-javascript-error-boundary.json).

### timerBoundaryEvent

Attaches a deadline or reminder to an active host. An interrupting timer cancels that host's active work and takes the boundary route. A non-interrupting timer creates a sibling token while the host remains active; a recurring timer can create repeated reminders.

**Topology:** no incoming flows; exactly one unconditional outgoing flow. **Worker:** required.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `attachedToRef` | integer | Required | Existing eligible host ID; see [boundary host rules](#boundary-host-rules). |
| `cancelActivity` | Boolean | Optional; `true` | `true` interrupts the host; `false` creates sibling work without completing the host. |
| `timer` | [timer configuration](#timer-configuration) | Required | Exactly one timer expression, activated with the host. |

```json
{
  "id": 31,
  "name": "Send reminders",
  "type": "timerBoundaryEvent",
  "attachedToRef": 11,
  "cancelActivity": false,
  "timer": { "timeCycle": "R3/PT10M" }
}
```

The example assumes node `11` is an eligible host. Host completion removes future attached timer work. See the [reminder/deadline example](../examples/timers/05-user-task-reminder-and-deadline.json).

### conditionalBoundaryEvent

Observes a variable condition while its host activation is active. Its initial truth is false, so an already-true condition captures immediately. An interrupting capture cancels the host's work and takes the boundary route. A non-interrupting capture creates sibling work, rearms after false, and captures each later false-to-true change; repeated true-to-true writes produce no new branch.

**Topology:** no incoming flows; exactly one unconditional outgoing flow. **Worker:** required for `durableAsync` delivery, and independently for an async host.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `attachedToRef` | integer | Required | Existing eligible host ID; see [boundary host rules](#boundary-host-rules). |
| `cancelActivity` | Boolean | Optional; `true` | Interrupt the host when true; otherwise create a rearmable sibling branch. |
| `conditional` | [conditional configuration](#conditional-configuration) | Required | Observable predicate and delivery mode. |

```json
{
  "id": 32,
  "name": "Escalate high risk",
  "type": "conditionalBoundaryEvent",
  "attachedToRef": 11,
  "cancelActivity": false,
  "conditional": { "condition": "riskScore >= 80", "deliveryMode": "durableAsync" }
}
```

Declare persisted instance variable `riskScore`. In one matching batch, non-interrupting boundaries are captured in node-ID order, then the lowest-ID interrupting boundary wins. Successful host outputs are evaluated while the host is active, so an interrupting match can take precedence over normal exit; service/script failure handling retains error-boundary precedence. Several durable rearmed occurrences can remain queued independently. See the [conditional-boundary runtime reference](../Flowbit/README.md#conditional-boundary-events).

### scopedInterruptEvent

A Flowbit control-flow extension. On entry, it finds the nearest active activation of the referenced split in the token's branch ancestry, cancels sibling/nested work in that activation, promotes the triggering token to the scope's parent, and follows its continuation. Unrelated scopes continue. If the referenced activation is no longer active, it records `scopedInterruptSkipped` and continues.

**Topology:** exactly one incoming flow and one unconditional outgoing flow. **Worker:** not required by this event.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `gatewayRef` | integer | Required | Existing `parallelGateway`, `inclusiveGateway`, or `complexGateway` split ID. That gateway must have exactly one incoming and at least two outgoing flows, and the interrupt must be structurally reachable from it. Exclusive splits and merge gateways are invalid references. |

```json
{
  "id": 40,
  "name": "Stop the review branches",
  "type": "scopedInterruptEvent",
  "gatewayRef": 4
}
```

The continuation cannot target the same interrupt, an entry event, a boundary event, or a Parallel/Complex merge directly. Cancellation covers nested gateway scopes, tasks/MI items, message/timer waits, conditional subscriptions, and durable jobs in the interrupted activation. Strict BPMN normally models this scope with an interruptible subprocess. See the [scoped-interrupt example](../examples/gateways/06-scoped-interrupt-parallel-branch.json).

### endEvent

Completes the current execution token normally. Other branches keep running; the instance completes after its remaining work finishes. Use this for a branch that can finish independently.

**Topology:** at least one incoming flow; no outgoing flows. **Worker:** not required. **Additional properties:** none beyond the [common node properties](#common-node-properties).

```json
{ "id": 90, "name": "Branch complete", "type": "endEvent" }
```

### terminateEndEvent

Completes the triggering token, cancels all other active tokens and gateway scopes in the instance, and completes the instance. Cancellation includes open tasks/MI items, message/timer/conditional waits, and durable jobs. Use it when one route is allowed to finish the whole workflow immediately.

**Topology:** at least one incoming flow; no outgoing flows. **Worker:** not required. **Additional properties:** none beyond the [common node properties](#common-node-properties).

```json
{ "id": 91, "name": "Finish all work", "type": "terminateEndEvent" }
```

See the [quorum example with terminate end](../examples/gateways/04-complex-two-of-three-merge.json).

### errorEndEvent

Faults the instance with a stable authored code and optional public description, cancelling sibling work. This is an intentional terminal business failure rather than a caught task exception. The committed instance exposes its fault metadata through runtime APIs.

**Topology:** at least one incoming flow; no outgoing flows. **Worker:** not required.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `errorCode` | string | Required | Trimmed, nonblank, at most 300 characters. Starts with an ASCII letter or digit and contains only ASCII letters/digits, `.`, `_`, or `-`: `^[A-Za-z0-9][A-Za-z0-9._-]*$`. |
| `errorDescription` | string | Optional; node name used when absent/blank | Trimmed public fault description, at most 1,000 Unicode scalar values. It is authored text, not a template/expression. |

```json
{
  "id": 92,
  "name": "Order rejected",
  "type": "errorEndEvent",
  "errorCode": "ORDER.REJECTED",
  "errorDescription": "The supplier declined the order."
}
```

See the [REST error-boundary/error-end example](../examples/service-tasks/02-rest-error-boundary-and-error-end.json).

## Message configuration

The `message` object is required on `messageStartEvent` and `intermediateMessageCatchEvent` only. These endpoints use configured machine credentials through `X-Client-Id` and `X-Client-Secret`, plus a required custom header; they do not use node role lists or user-task claims.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `clientId` | string/template | Required; no valid empty default | Expected client ID. Literal/resolved client IDs are bounded to 300 Unicode scalar values. Exact comparison with the incoming credential header. |
| `clientSecret` | string/template | Required; no valid empty default | Expected secret. Prefer a server-controlled `${config.name}` or `${setting.name}` binding instead of embedding a secret in workflow JSON. |
| `headerName` | string/template | Required | Required correlation-header name. After resolution it must be a valid HTTP field name, at most 300 characters, and must differ from `X-Client-Id`, `X-Client-Secret`, and the configured idempotency header/standard alias. |
| `headerValue` | string/template | Required | Expected value of that header, compared exactly. Templated missing/blank values fail delivery configuration validation; header validation is an additional check, not a substitute for equality. |
| `headerValidation` | NCalc expression string | Optional; absent | Additional rule with incoming value bound as `header`. It must evaluate truthy. Start rules use pre-payload authentication context; catch rules can also inspect stored instance context. `FlowInfo` is not supported. |
| `outputMappings` | [output-mapping array](#output-mappings) | Optional; `[]` | Typed writes extracted from the raw JSON message payload. Message-start mappings declare start variables; catch mappings describe operation-specific writes. See the shared mapping table for every property and compatibility behavior. |
| `deliveryIdempotency` | Boolean | Catch only; `false` | Requires a transport delivery key and reserves it after successful commit for the whole instance. The key is independent of correlation and is not written as an implicit variable. A duplicate returns `409`. |
| `deliveryIdempotencyHeaderName` | string | Catch only; `Idempotency-Key` when enabled | Header carrying the catch-delivery key. Fixed valid HTTP field name of at most 300 characters, not a template; reserved names are listed below. Must differ from the correlation header. Ignored/removed when catch-delivery idempotency is disabled. |
| `idempotencyVariable` | string | Legacy import only | Old message-start configuration migrated to node-level `idempotency`. Do not author it in new definitions or combine it with the canonical node-level contract. |

Credential/correlation templates support scalar `${...}` substitution from the context available to that event. Catch authentication uses persisted values and server-controlled `sys.*`, `config.*`, and `setting.*` context; it does not trust newly submitted payload fields as expected credentials. Header-name lookup follows HTTP case-insensitivity, while credential/correlation **values** compare exactly. Missing, conflicting, or malformed headers reject delivery.

The catch-delivery key is trimmed, nonblank, case-sensitive, and at most 300 Unicode scalar values. The default standard header also recognizes `X-Idempotency-Key`; conflicting values are rejected. Successful delivery is atomic with its receipt, mapped values, and token movement. See [message API contracts](api-guide.md) for body limits, selectors, authentication failures, and duplicate responses.

## Business key configuration

`businessKey` is optional on manual/message starts. Once enabled on any manual/message entry in a definition, it must be configured on every manual/message entry; timer starts are excluded. It identifies a domain case across the stable workflow family, independently of a transport retry key.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `variable` | string | Required | Exact name of a required scalar `string` start input, or required scalar-string message-start mapping. It must have no `defaultValue`; a message-start mapping must have an explicit payload `path`. It must differ from the idempotency variable. |
| `uniqueness` | string enum | Required; no implicit default | `active` reserves the case key while its instance is active; completion, cancellation, or fault releases it. `all` reserves it permanently. Recognized casing is canonicalized. |

Input keys are trimmed, nonblank, case-sensitive, and limited to 300 Unicode scalar values. The reservation is workflow-family scoped, across versions and external entry points. Duplicate business identity returns a conflict. Enabling a family's policy also constrains starts through older unkeyed versions; see [business identity and retries](developer-guide.md#separate-retries-from-business-identity).

## Start idempotency configuration

Node-level `idempotency` is optional on manual/message starts. It claims a transport request key permanently across the stable workflow family after a successful commit, including after the instance becomes terminal. A duplicate returns `409` with the existing instance reference; it is not a replayed success response.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `headerName` | string | Optional; `Idempotency-Key` | Fixed valid HTTP field name, at most 300 characters. It cannot use the reserved names below or collide with the message-start correlation header. Explicit blank/null is invalid. |
| `variable` | string | Required | Name of the implicit required scalar-string variable populated only from that header. Follow [variable naming rules](#variables-and-inputs). It must not duplicate an entry variable/message mapping or the business-key variable; the request body must not submit it. |

The key is trimmed, nonblank, case-sensitive, and bounded to 300 Unicode scalar values. For the standard header, the legacy `X-Idempotency-Key` alias is accepted; conflicting values reject the request. Arbitrary headers on unconfigured starts do not enable this behavior.

Both start-idempotency and catch-delivery header names reject these reserved names case-insensitively: `Authorization`, `Proxy-Authorization`, `Cookie`, `Host`, `Content-Length`, `Content-Type`, `Content-Encoding`, `Transfer-Encoding`, `Connection`, `Keep-Alive`, `TE`, `Trailer`, `Upgrade`, `Expect`, `X-Client-Id`, and `X-Client-Secret`.

## Timer configuration

The required `timer` object belongs only to `timerStartEvent`, `intermediateTimerCatchEvent`, and `timerBoundaryEvent`. Set **exactly one** nonblank property; every expression is limited to 128 characters after trimming.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `timeDate` | ISO-8601 timestamp string | One of the three required | Absolute date/time with `Z` or an explicit numeric UTC offset, for example `2030-01-01T09:00:00Z`. Normalized to UTC. A local timestamp without offset is invalid. |
| `timeDuration` | ISO-8601 duration string | One of the three required | Positive fixed-unit delay, for example `PT30S`, `P2D`, or `P1W`. Supports weeks or day/hour/minute/second components and decimal components, within the runtime duration range. |
| `timeCycle` | recurrence string | One of the three required | `R/Duration` for unbounded authored recurrence, or `R<n>/Duration` with a positive 32-bit integer occurrence count. For example `R5/PT30S`. The interval must be at least one second. |

No cron, calendar month/year duration, timezone calendar, or expression/template schedule is supported. Relative waits are measured from activation; absolute dates do not move with activation. Timer jobs persist across restarts. Firing can be late when the Worker is unavailable; recurring occurrences more than one minute late skip to a later eligible occurrence. This is a durable schedule, not a hard real-time guarantee. See [timer execution semantics](bpmn-support.md#timers).

## Conditional configuration

The required `conditional` object belongs only to `intermediateConditionalCatchEvent` and `conditionalBoundaryEvent`.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `condition` | NCalc expression string | Required | Nonblank after trimming/optional legacy `${...}` wrapper removal; at most 4,000 Unicode scalar values. Must reference 1–64 distinct declared, persisted instance variables. Unknown variables/functions, shared aliases, `sys.*`, `config.*`, `setting.*`, `mi.*`, `gateway.*`, `FlowInfo`, and gateway/MI helper functions are rejected. |
| `deliveryMode` | string enum | Optional; `atomic` | `atomic` evaluates and advances in the writer's transaction. `durableAsync` latches a true occurrence and creates a persisted Worker job in that transaction; later false values cannot retract that captured occurrence. Omitted/null/blank means `atomic`; recognized casing is canonicalized and unknown values are invalid. |

Conditions support NCalc built-ins plus the pure string helpers `Length`, `Len`, `IsNullOrEmpty`, `IsNullOrWhiteSpace`, `Contains`, `StartsWith`, `EndsWith`, `Lower`, `Upper`, `Trim`, and `IsMatch`. The engine extracts dependencies from the parsed expression and reevaluates affected waits against the complete post-write-batch current-value snapshot. This includes supported variable producers such as user actions, messages, scripts/service outputs, and administrative variable updates. Shared aliases are rejected in both delivery modes. See the [conditional runtime reference](../Flowbit/README.md#intermediate-conditional-catch-events) for persistence and version-switch restrictions.

## Boundary host rules

Timer/conditional boundaries attach to a normal or multi-instance `userTask`, an `intermediateMessageCatchEvent`, an `intermediateTimerCatchEvent`, or a `task`/`serviceTask`/`scriptTask` with `asyncBefore: true`. A conditional catch, gateway, start, end, or another boundary is not an eligible host. A host may have at most **eight timer and conditional boundaries combined**, plus its separate one-error-boundary allowance when it is a service/script task.

An interrupting boundary cancels the host's open work, waits, attached subscriptions, and durable jobs before continuing on the boundary route. A non-interrupting boundary creates sibling work and retains the host. The engine fences cancellation against late jobs/results. It cannot undo an external REST side effect that already occurred. See [boundary semantics](bpmn-support.md#conditional-boundaries) and [Worker operations](deployment.md).

## Gateways

All four gateway types use the [common node properties](#common-node-properties).
Their direction comes from their connections, not a JSON property:

| Shape | Required incoming flows | Required outgoing flows |
| --- | --- | --- |
| Split | Exactly 1 | At least 2 |
| Merge | At least 2 | Exactly 1 |

A one-in/one-out or many-in/many-out gateway is invalid; use separate merge and
split nodes. A split and its later merge may use different gateway types.
Gateway nodes do not accept authored asynchronous task boundaries. Runtime
fan-out is bounded by `Workflow.Gateway.MaxActiveTokens` (default `1000`).

Gateway outgoing [sequence flows](#sequence-flows) cannot carry user-action
roles, input variables, claim bypass, or multi-instance outcome metadata. Leave
`isSelectable` at its default `true`; it does not turn a gateway route into a
user action. Incoming flows retain the rules of their source node.

### exclusiveGateway

An Exclusive split evaluates non-default flows by ascending
`conditionPriority` and traverses only the first true condition. If none match,
it takes its required default. An ordinary Exclusive merge forwards each
arrival independently; it does not synchronize parallel branches. An optional
cancelling join makes its first arrival cancel unfinished work in the referenced
split activation.

| Node property | Requirement / default | Meaning |
| --- | --- | --- |
| `type` | Required: `"exclusiveGateway"` | Exclusive split or merge. |
| `joinCancellation` | Optional; omitted means no cancellation | Merge only; see [cancelling joins](#joincancellation). |

For a split, exactly one outgoing flow must have `isDefault: true`, no
`condition`, and no `conditionPriority`. Every other outgoing flow requires a
valid NCalc `condition` and a unique positive integer `conditionPriority`.
Older definitions whose non-default routes all omit priorities are normalized
using their order in `sequenceFlows` (`1`, `2`, ...); partially specified
priorities are rejected. An Exclusive merge's sole outgoing flow must be
unconditional and cannot be a default.

This fragment shows the gateway and its outgoing routes; the surrounding
workflow supplies an incoming flow to node `2`, target nodes, and `amount`:

```json
{
  "flowNodes": [
    { "id": 2, "name": "Approval route", "type": "exclusiveGateway" }
  ],
  "sequenceFlows": [
    { "id": 201, "sourceRef": 2, "targetRef": 3, "condition": "amount >= 10000", "conditionPriority": 1 },
    { "id": 202, "sourceRef": 2, "targetRef": 4, "isDefault": true }
  ]
}
```

Complete example: [Exclusive priority and default](../examples/gateways/01-exclusive-priority-and-default.json).

### parallelGateway

A Parallel split creates one execution token per outgoing flow. A merge waits
for one token from **every static incoming flow**, then emits one continuation.
If a branch never reaches that merge, it keeps waiting; it does not infer which
branches were selected upstream. Surplus arrivals remain available for later
merge batches.

| Node property | Requirement / default | Meaning |
| --- | --- | --- |
| `type` | Required: `"parallelGateway"` | Parallel split or merge. |
| `joinCancellation` | Optional; omitted means no cancellation | Merge only; cancellation happens after every required input arrives. |

All outgoing flows, on both splits and merges, must be unconditional:
`condition` and `conditionPriority` are absent, and `isDefault` is `false` or
omitted. There is no priority, default, or branch-selection expression.

```json
{ "id": 2, "name": "Run all reviews", "type": "parallelGateway" }
```

Give this split exactly one incoming flow and at least two unconditional
outgoing flows. Complete example:
[Parallel fork and join](../examples/gateways/02-parallel-fork-and-join.json).

### inclusiveGateway

An Inclusive split traverses **every** non-default flow whose condition is
true. It uses its required default only when none match. An Inclusive merge
waits while an active token elsewhere can still reach a currently empty
incoming flow without passing through the merge. When no such arrival remains
possible, it consumes one token from each populated input and continues. This
reachability rule works without an explicitly paired split and retains surplus
arrivals for later batches.

| Node property | Requirement / default | Meaning |
| --- | --- | --- |
| `type` | Required: `"inclusiveGateway"` | Inclusive split or merge. |
| `joinCancellation` | Optional; omitted means no cancellation | Merge only; preserves the reachability-based enabling rule. |

A split requires exactly one unconditional default and a valid NCalc
`condition` on every non-default outgoing flow. `conditionPriority` is invalid
on all Inclusive outgoing flows. A merge's sole outgoing flow must be
unconditional and cannot be a default.

The surrounding workflow supplies one incoming flow to node `2`, the targets,
and the two Boolean variables in this fragment:

```json
{
  "flowNodes": [
    { "id": 2, "name": "Choose required reviews", "type": "inclusiveGateway" }
  ],
  "sequenceFlows": [
    { "id": 201, "sourceRef": 2, "targetRef": 3, "condition": "needsLegal == true" },
    { "id": 202, "sourceRef": 2, "targetRef": 4, "condition": "needsSecurity == true" },
    { "id": 203, "sourceRef": 2, "targetRef": 5, "isDefault": true }
  ]
}
```

Complete example:
[Inclusive conditional split and merge](../examples/gateways/03-inclusive-conditional-split-and-merge.json).

### complexGateway

A Complex gateway stores its current phase, arrival counts, and cycle number.
In the start phase it waits until `activationCondition` evaluates true, consumes
one waiting token per populated input, and routes through every matching
non-default outgoing flow. It then enters a reset phase. Reset waits until no
active token can still reach an empty relevant input, evaluates the outgoing
flows with reset context, and starts the next cycle. Reset may produce no output;
a start activation with no matching output and no default fails.

| Node property | Requirement / default | Meaning |
| --- | --- | --- |
| `type` | Required: `"complexGateway"` | Complex split or merge. |
| `activationCondition` | Required nonblank valid NCalc expression; no default | Determines when start-phase arrivals enable a firing. Whitespace is trimmed. Reset completion uses graph reachability rather than re-evaluating this expression. |
| `joinCancellation` | Optional; omitted means ordinary start/reset behavior | Merge only. A cancelling firing closes the activation and advances the cycle without emitting reset output. |

The following expression context is available:

| Expression | Where allowed | Meaning |
| --- | --- | --- |
| `IncomingCount(301)` | `activationCondition` and outgoing `condition` | Count of waiting tokens on the specified incoming sequence flow. The argument must be a literal ID belonging to this gateway's incoming flows. |
| `TotalIncomingCount()` | `activationCondition` and outgoing `condition` | Total waiting input tokens in the current phase. |
| `[gateway.waitingForStart]` | Outgoing `condition` only | `true` during start routing and `false` during reset routing. It is invalid in `activationCondition`. |

A Complex gateway may have zero or one default outgoing flow. A default must
be unconditional; **every non-default outgoing flow requires a condition even
on a merge with only one outgoing flow**. Priorities are unsupported. For an
ordinary merge that should emit only start-phase output, use
`"condition": "[gateway.waitingForStart]"` on its sole non-default continuation.
Non-default outgoing conditions of Exclusive, Inclusive, and Complex gateways
may inspect `FlowInfo(...)`; a Complex `activationCondition` cannot.

This node fragment assumes split `2` owns all incoming branches and the merge
has at least two incoming flows and one outgoing flow with `condition: "true"`:

```json
{
  "id": 6,
  "name": "Continue after any two reviews",
  "type": "complexGateway",
  "activationCondition": "TotalIncomingCount() >= 2",
  "joinCancellation": { "gatewayRef": 2 }
}
```

Complete examples: [Two-of-three cancelling merge](../examples/gateways/04-complex-two-of-three-merge.json)
and [Complex start/reset cycle](../examples/gateways/05-complex-start-reset-cycle.json).

### joinCancellation

`joinCancellation` is an optional Flowbit extension on any valid Exclusive,
Parallel, Inclusive, or Complex **merge**. It changes the cleanup performed
when the gateway fires, not its enabling rule.

| Property | Requirement / default | Meaning |
| --- | --- | --- |
| `joinCancellation` | Optional object; absent means disabled | Cancel unfinished descendants of the referenced split activation after this merge fires. |
| `joinCancellation.gatewayRef` | Required positive integer when the object exists | ID of an existing, structurally upstream Parallel, Inclusive, or Complex split. It cannot name an Exclusive split or the merge itself. Every merge input must be structurally downstream of that split before crossing this merge. |

The contributing arrivals must share the nearest active activation of that
referenced split. Flowbit retains the lowest-ID contributing token, promotes it
to the split activation's parent scope, and atomically cancels the activation's
other unfinished descendants, including nested branches, user and
multi-instance tasks, event waits, and durable jobs. Work outside that activation
continues. If the arrivals lack a common active referenced activation, the
transition returns `409 Conflict` and rolls back.

Adding, removing, or changing this policy prevents an in-place workflow-version
switch while gateway or branch state is active. See the
[gateway runtime reference](../Flowbit/README.md#gateways-and-scoped-interruption)
for lifecycle records and cancellation details.

## Durable job policy

Only `task`, `userTask`, `serviceTask`, and `scriptTask` support these node properties. Events and gateways do not accept authored `asyncBefore`, `asyncAfter`, or `job`; their built-in durable behavior uses their own configuration.

| Property | JSON type | Requirement / default | Meaning and constraints |
| --- | --- | --- | --- |
| `asyncBefore` | boolean | Optional; `false`. | Persist a job and commit before activating the task. A REST transition touching shared-variable bindings requires `true`. |
| `asyncAfter` | boolean | Optional; `false`. | Persist a job after completion and before traversing the chosen outgoing flow. |
| `job` | object | Optional; engine policy. | Overrides failure handling/retries for authored task jobs. Requires at least one async flag to be true. |
| `job.failureHandling` | string | Optional; `"boundaryFirst"`. | `boundaryFirst` uses a matching error boundary before retries; `retryFirst` retries first, then considers the error boundary after exhaustion. An unresolved failure becomes an incident. |
| `job.retryDelays` | array of strings | Optional/null; `["PT10S", "PT1M", "PT5M"]`. | Delay before each retry, up to 10 positive fixed-unit ISO-8601 durations, each at most 128 characters. `[]` explicitly disables automatic retries. The initial attempt is additional to the retries. Calendar months/years and nonpositive delays are invalid. |

For multi-instance tasks the async flags apply to the parent execution, not independently to each child. Workers must be deployed for jobs to advance. A durable REST call may be repeated after a failure; remote side effects need their own idempotency contract. See [durable execution](bpmn-support.md#synchronous-and-durable-execution).

```json
{
  "id": 30,
  "name": "Continue asynchronously",
  "type": "task",
  "asyncBefore": true,
  "job": { "failureHandling": "retryFirst", "retryDelays": ["PT10S", "PT1M"] }
}
```

## Sequence flows

Sequence flows live in the workflow's `sequenceFlows` array. They are first-class objects with their own IDs. On user tasks they define action buttons, input forms, and multi-instance outcomes; on gateways they define routing. This table is the complete current `SequenceFlowModel` JSON property set.

| Property | JSON type | Requirement / default | Meaning and constraints |
| --- | --- | --- | --- |
| `id` | integer | Author explicitly; CLR default `0`. | Unique within `sequenceFlows`. Used for action selection, gateway incoming-count functions, and flow evidence. |
| `name` | string | Optional; `""`. | Display/action label. Give selectable user actions a meaningful name. |
| `externalId` | string | Optional/null. | Application metadata identifying the flow. |
| `attributes` | array of objects | Optional; `[]`. | Same `key`/`value` schema and limits as [node attributes](#common-node-properties). |
| `sourceRef` | integer | Required reference. | ID of an existing source node. |
| `targetRef` | integer | Required reference. | ID of an existing target node. |
| `roles` | array of strings | Optional; `[]`. | Literal role check on a user action, additional to node roles. Empty means unrestricted for this check only. Trimmed and deduplicated case-insensitively. |
| `rolesVariable` | string | Optional/null. | Alternative to nonempty `roles`; declared process `string[]`, instance or shared. Only selectable, non-default user-task flows. Captured with task roles on entry; see [userTask](#usertask). |
| `variables` | array of objects | Optional; `[]`. | Inputs collected for this action, using [VariableModel](#variables-and-inputs). Supported only when the source is a user task; engine-only MI flows cannot have inputs. |
| `condition` | string | Optional/null except routing requirements. | NCalc expression: stored-state visibility/action guard on selectable user actions, or a gateway routing condition. It is evaluated before applying submitted action values. Not a default-flow or automatic pass-through condition. |
| `conditionPriority` | integer | Required positive, unique among non-default Exclusive split outputs; otherwise omit. | Lower values run first. Not MI completion priority. For older Exclusive splits with all priorities absent, normalization materializes array order. |
| `isDefault` | boolean | Optional; `false`. | Fallback route. Required exactly once on Exclusive/Inclusive splits and MI tasks; optional at most once on Complex gateways. Unsupported on normal user tasks. See type-specific routing rules. |
| `isSelectable` | boolean | Optional; `true`. | `false` marks an engine-only MI outcome/fallback and is supported only on MI user-task outputs. |
| `canActWithoutClaim` | boolean | Optional; `false`. | Allows an otherwise-authorized actor to use this action without owning the claim. Only meaningful on a claim-required user task. Does not bypass roles, assignment, or action conditions. |
| `canActWithoutClaimRoles` | array of strings | Optional; `[]`. | Additional roles restricting claim bypass. With bypass enabled, empty permits any otherwise-authorized actor. Cleared when bypass is disabled or the task needs no claim. |
| `completionCondition` | string | Required on every non-default, non-interrupting MI outcome; otherwise omit. | Aggregate condition evaluated according to `multiInstance.completionEvaluation`. May use `CountFlow(id)`, `PercentFlow(id)`, `mi.total`, `mi.completed`, `mi.remaining`, and `FlowInfo`. |
| `completionPriority` | integer | Required positive, unique among non-default, non-interrupting MI outcomes; otherwise omit. | Lowest matching priority wins. Default and interrupting flows cannot set this. |
| `cancelRemainingInstances` | boolean | Optional; `false`. | MI user-task interrupt: selecting it immediately cancels unfinished children and continues the parent. Requires a selectable, non-default flow with no completion condition/priority. |

Literal/manual/dynamic task and action role policies are normalized according to the [waiting-task role rules](../Flowbit/README.md#waiting-task-roles); dynamic or managed lists are bounded to 100 entries and 300 Unicode scalars per trimmed role. An invalid dynamic value fails closed rather than becoming an unrestricted empty policy.

For **normal user tasks**, offer one or more selectable, non-default flows; do not set MI completion fields. For **MI user tasks**, provide at least one selectable outcome and exactly one engine-only fallback. Every non-default, non-interrupting outcome, including an engine-only outcome, needs a completion condition and a unique positive completion priority. `CountFlow`/`PercentFlow` IDs must name selectable outcomes of that MI node. Engine-only outputs cannot have role policies, action inputs, visibility conditions, or claim bypass. Interrupts must be selectable and cannot have aggregate completion rules. The fallback has no condition, completion rule, or priority.

A normal action fragment, for a claim-required review node `20` and an end node `90`:

```json
{
  "id": 201,
  "name": "Approve",
  "sourceRef": 20,
  "targetRef": 90,
  "roles": ["Reviewer"],
  "condition": "amount <= 10000",
  "variables": [
    { "id": 1, "name": "reviewNote", "dataType": "string", "required": true,
      "validation": "Len(Trim(reviewNote)) >= 3" }
  ]
}
```

Declare and initialize `amount` in the workflow. The condition checks its stored value; `reviewNote` is collected only when this action is selected. See the [user-task examples](../examples/user-tasks/README.md) and [MI examples](../examples/multi-instance/README.md) for complete routing.

## Variables and inputs

The same variable declaration shape is used for workflow-level variables, manual start inputs, and user-action inputs, but their contracts differ. **Process variables** are initialized or bound by the engine; **start/action inputs** are supplied by a caller. Message mappings use a separate schema in [output mappings](#output-mappings).

| Property | JSON type | Requirement / default | Meaning and constraints |
| --- | --- | --- | --- |
| `id` | integer | Authoring identifier; default `0`. | Identifies the declaration in the editor. Runtime values are addressed by `name`. |
| `name` | string | Required, nonblank. | Maximum 300 Unicode scalar values. Names are unique case-insensitively within each declaration collection. `sys.`, `config.`, `setting.`, and `mi.` prefixes are reserved. |
| `dataType` | string | Optional; `"string"`. | Exact lowercase values: `string`, `number`, `boolean`, `date`, `datetime`, or `json`. |
| `isArray` | boolean | Optional; `false`. | Array of the selected type. `string` + `true` means a JSON string array, not comma-separated text. |
| `nullable` | boolean | Optional; `false`. | Top-level process variables only. Allows null and permits omission of an instance variable's default. Not an input optionality flag. |
| `required` | boolean | Optional; `false`. | Start/action inputs require an explicit non-null value; whitespace-only strings are missing. A required input cannot have a non-null default. Shared bindings cannot set this to true. Top-level instance declarations are initialized by the engine, not requested from a caller. |
| `defaultValue` | JSON value | Conditional. | Instance process variables require a default unless nullable; nullable omission initializes null. Optional inputs use their default when omitted. Shared bindings cannot supply defaults. Must resolve to the type/array contract; authored numeric/Boolean strings and supported string templates are coerced. |
| `validation` | string | Optional/null. | NCalc rule parsed at authoring and evaluated against final values. Blank means no rule. Nullable process values skip validation when null; optional input rules still run when the input is omitted. Shared rules use only `value` and supported pure functions and must match the catalog contract. |
| `scope` | string | Top-level only; omitted means `"instance"`. | `instance` stores per-instance data; `shared` binds to a deployment catalog entry. Start/action inputs cannot set scope metadata. |
| `sharedKey` | string | Required for `scope: "shared"`; otherwise omit. | Existing catalog key, maximum 300 Unicode scalar values. Only one local alias for a given catalog key per workflow. |
| `access` | string | Required explicitly for `scope: "shared"`; otherwise omit. | `read` or `readWrite`. Producers need write access; binding contract and deployment permissions must match. |

| `dataType` | Scalar JSON example | Notes |
| --- | --- | --- |
| `string` | `"Request A"` | Use a JSON string. |
| `number` | `125.5` | Numeric strings are not valid typed external input. |
| `boolean` | `true` | Boolean strings are not valid typed external input. |
| `date` | `"2026-09-10"` | Valid `yyyy-MM-dd` string. |
| `datetime` | `"2026-09-10T09:00:00Z"` | Parseable ISO datetime containing `T`; include a UTC offset for interoperability. |
| `json` | `{"priority":2}` | JSON values; no JSON Schema enforcement. Null is still subject to the surrounding required/nullability contract. |

Top-level variable fragments:

```json
[
  { "id": 1, "name": "amount", "dataType": "number", "defaultValue": 0 },
  { "id": 2, "name": "approved", "dataType": "boolean", "defaultValue": false },
  { "id": 3, "name": "reviewers", "dataType": "string", "isArray": true,
    "defaultValue": ["alice", "bob"] },
  { "id": 4, "name": "reviewResults", "dataType": "json", "defaultValue": [] },
  { "id": 5, "name": "optionalReference", "dataType": "string", "nullable": true }
]
```

An MI `resultVariable` uses scalar `dataType: "json"` with `isArray: false`; its JSON value is the result array. Do not confuse that with a declaration of `json[]`.

Start/action defaults can refer to earlier resolved values through `${name}` and available `${sys.*}`, `${config.*}`, or `${setting.*}` context. NCalc uses bracketed dotted names such as `[sys.user]`. Available functions and context depend on the owning property: conditional predicates accept persisted instance variables and reject external context; `FlowInfo` is supported only in the documented routing, MI completion, and script contexts.

Entry input names cannot collide with top-level process variables, including implicit business/idempotency keys. Service and message-catch outputs may produce instance names without a top-level declaration; if a process declaration exists, its contract must match. Script assignment targets, dynamic role sources, and MI collection/result variables must be declared process variables. Refer to each producer's rules instead of assuming all producers share one target policy.

See [input collection](developer-guide.md#collect-and-validate-variables) for null/default handling and [shared variables](../Flowbit/README.md#shared-variables) for catalog authorization and transaction restrictions.

## Output mappings

Service responses and message payloads use the same JSON mapping fields. For a `messageStartEvent`, mappings also declare the entry variables; for `serviceTask` and `intermediateMessageCatchEvent`, they describe operation-specific writes. They are not the node's `variables` list.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `variable` | string | Required | Output name, unique case-insensitively within the mapping list. Subject to [variable naming rules](#variables-and-inputs). For service/catch mappings, a matching process declaration supplies its canonical spelling and must have the same type/array contract; undeclared instance targets are also supported. Message-start mappings declare entry variables and cannot collide with top-level process variable names. |
| `path` | string | Required unless a non-null `defaultValue` is configured | Dotted JSON property path, for example `data.order.id` or `items.0.code`. Property lookup is case-sensitive; numeric segments index arrays from zero. This is not JSONPath: no `$`, wildcards, or bracket-index syntax. Blank path with a default creates a default-only mapping. |
| `dataType` | string | Required in canonical JSON | `string`, `number`, `boolean`, `date`, `datetime`, or `json`; element type when `isArray: true`. Legacy service/catch mappings inherit a matching process type, otherwise normalize to `json`. Message-start legacy declarations are migrated into typed mappings. |
| `isArray` | Boolean | Required in canonical JSON | Whether the mapped value is an array of `dataType`. Legacy service/catch omissions inherit a process declaration, otherwise normalize to `false`. |
| `required` | Boolean | Optional; `false` | Requires a resolved non-null value after extraction/defaulting; whitespace-only strings also fail. A valid default can satisfy requiredness when the path is missing. Empty arrays remain valid. |
| `defaultValue` | JSON value | Optional; absent/null means no fallback | Used only when the path is absent/unresolvable (or blank), never to replace an explicitly present invalid value. Supports the normal typed default/template rules. Defaults are resolved in mapping order and may refer to earlier resolved outputs. |
| `validation` | string | Optional; absent/blank | NCalc rule checked against the final output overlay plus existing context. Matching process-variable validation is checked too. `FlowInfo` is unavailable in output validation. |

Incoming extracted values are type-checked strictly: `number` requires a JSON number and `boolean` a JSON Boolean; strings such as `"12"` are not accepted as numeric response values. `date` requires `YYYY-MM-DD`; `datetime` requires a parseable timestamp containing `T`. Arrays require compatible elements. A declared process target's `nullable` contract determines whether explicit JSON null is accepted. Undeclared `json` outputs retain JSON-null compatibility; mappings have no independent `nullable` property.

If an optional path is absent and there is no default, that mapping produces no write and preserves an existing value. Its authored validation still runs against the final context, so optional does not bypass a validation rule. A present wrong-type value fails even when the mapping is optional or has a fallback. All mappings are resolved and validated before writes. A service mapping failure fails the activity and may be caught by an error boundary; a message-catch failure rejects delivery and leaves the wait in place. Message-start failure creates no instance.

```json
{
  "variable": "labels",
  "path": "data.labels",
  "dataType": "string",
  "isArray": true,
  "required": false,
  "defaultValue": [],
  "validation": "Length(labels) <= 10"
}
```

## Authoring and compatibility

1. Start with a complete [catalog example](../examples/README.md), then select types and properties from this reference. Fragments here intentionally omit the surrounding graph.
2. Give every node a nonblank name and an explicit canonical type and ID. Keep node/flow IDs unique within their own collections, and resolve every reference.
3. Declare the process variables required by the selected node. Put manual start inputs on `startEvent.variables` and user inputs on the selected outgoing flow's `variables`.
4. Apply the node's topology rules. Entry events have no incoming flow and one unconditional output; boundaries attach by `attachedToRef` and have no incoming sequence flow; ends have incoming flow and no output; gateway shape determines split versus merge.
5. Save the definition through the [HTTP definition API](api-guide.md). Server validation, deployment settings, shared catalog contracts, and worker publication gates remain authoritative. Editor export alone does not publish it.

Legacy documents may normalize differently from new authoring: old step/action vocabulary migrates to nodes/flows; Exclusive splits with all priorities omitted gain ordered priorities; old selectable MI defaults become a selectable outcome plus a hidden fallback. Write the current vocabulary explicitly. Changing properties on active tasks, waits, gateway scopes, or durable jobs can block in-place version switching; see [version selection and changes](developer-guide.md#select-and-change-workflow-versions).

This reference follows the [shared JSON model](../Flowbit/src/Flowbit.Shared/Models/WorkflowModel.cs), [normalization](../Flowbit/src/Flowbit.Shared/Models/WorkflowModelMigrator.cs), and [definition validation](../Flowbit/src/Flowbit.Service/Services/WorkflowDefinitionService.cs), together with the expression analyzers and runtime behavior. For implementation details and limits beyond authoring, use the [runtime reference](../Flowbit/README.md).

[Documentation home](index.md) · [BPMN behavior](bpmn-support.md) · [Developer guide](developer-guide.md)
