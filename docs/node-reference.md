# Workflow nodes and properties

[Documentation home](index.md) · [BPMN behavior](bpmn-support.md) · [Developer guide](developer-guide.md) · [Example catalog](../examples/README.md)

Use this reference when choosing a task, event, or gateway and authoring its Flowbit JSON properties. It covers all **28 supported node types**, their shared and nested configuration, and the sequence flows that supply actions and routing. For execution semantics across a whole process, read [BPMN support](bpmn-support.md); for HTTP requests, read the [API reference](api-guide.md).

The tables describe current authoring contracts and effective defaults. **Required** means needed for that configuration to work and pass validation; **conditional** means required only in the stated situation. Omit optional properties that do not apply to the selected type. The model uses one shared node class: a property existing on that class does not make it meaningful on every node. Some irrelevant legacy fields are cleared during normalization, while explicitly invalid combinations are rejected. Do not rely on normalization to repair newly authored definitions.

JSON blocks below are **fragments**, not standalone workflows, unless stated otherwise. Node fragments belong in `flowNodes`; flow fragments belong in `sequenceFlows`. Referenced nodes, flows, variables, credentials, and settings must exist in the containing workflow or deployment. Each type links to a complete example where available.

## Contents

- [Node catalog](#node-catalog)
- [Common node properties](#common-node-properties)
- [Tasks](#tasks)
  - [Automatic multi-instance properties](#automatic-multi-instance-properties)
- [Events](#events)
- [Gateways](#gateways)
- [Message configuration](#message-configuration)
- [Business key configuration](#business-key-configuration)
- [Start idempotency configuration](#start-idempotency-configuration)
- [Timer configuration](#timer-configuration)
- [Working-time durations](#working-time-durations)
- [Conditional configuration](#conditional-configuration)
- [Signal configuration](#signal-configuration)
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
| Task | [callActivity](#callactivity) | Start a child instance of another workflow family and wait for it. |
| Start | [startEvent](#startevent) | Authenticated user starts with roles and inputs. |
| Start | [messageStartEvent](#messagestartevent) | Machine-authenticated inbound message starts. |
| Start | [timerStartEvent](#timerstartevent) | Scheduled instance creation. |
| Catch | [intermediateMessageCatchEvent](#intermediatemessagecatchevent) | Wait for an inbound message. |
| Catch | [intermediateTimerCatchEvent](#intermediatetimercatchevent) | Wait for a scheduled trigger. |
| Catch | [intermediateConditionalCatchEvent](#intermediateconditionalcatchevent) | Wait for a persisted-variable condition. |
| Catch | [intermediateSignalCatchEvent](#intermediatesignalcatchevent) | Wait for a signal broadcast with its name. |
| Throw | [intermediateSignalThrowEvent](#intermediatesignalthrowevent) | Broadcast a signal to a stated scope and continue. |
| Link | [intermediateLinkThrowEvent](#intermediatelinkthrowevent) | "Go to" the link catch event with the same name. |
| Link | [intermediateLinkCatchEvent](#intermediatelinkcatchevent) | Continue here from every link throw event with the same name. |
| Boundary | [errorBoundaryEvent](#errorboundaryevent) | Handle an attached service, script, or call failure. |
| Boundary | [timerBoundaryEvent](#timerboundaryevent) | Deadline or reminder while a host is active. |
| Boundary | [conditionalBoundaryEvent](#conditionalboundaryevent) | Interrupt or branch when a host's observed condition becomes true. |
| Boundary | [signalBoundaryEvent](#signalboundaryevent) | Interrupt or branch when a signal with its name is delivered while a host is active. |
| Interrupt | [scopedInterruptEvent](#scopedinterruptevent) | Interrupt a particular active gateway scope. |
| End | [endEvent](#endevent) | Complete this token. |
| End | [terminateEndEvent](#terminateendevent) | Complete the instance and cancel its remaining work. |
| End | [errorEndEvent](#errorendevent) | Fault the instance and cancel its remaining work. |
| End | [signalEndEvent](#signalendevent) | Complete this token and broadcast a signal. |
| Gateway | [exclusiveGateway](#exclusivegateway) | Choose one branch or merge arrivals without synchronization. |
| Gateway | [parallelGateway](#parallelgateway) | Start all branches or synchronize all static inputs. |
| Gateway | [inclusiveGateway](#inclusivegateway) | Start matching branches or merge using reachability. |
| Gateway | [complexGateway](#complexgateway) | Stateful activation and reset conditions. |

Timers, conditional `durableAsync` delivery, signal events, call activities, call-only starts, and multi-instance service, script, and call activities require a running Worker. Task async flags also require a Worker. The [BPMN catalog](bpmn-support.md#supported-node-types) gives the prerequisites for each type; [deployment](deployment.md#worker-operation) explains setup.

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
| `dueDate` | string | Optional; absent | Due time resolved once when the work item is created: a fixed ISO-8601 duration such as `"P2D"` counted from creation, a fixed date-time with an offset, or an NCalc expression giving either as text. Allowed on multi-instance tasks. See [due date and priority](#due-date-and-priority). |
| `dueDateCalendar` | string | Optional; absent | Name of a [business calendar](#working-time-durations) whose working time a `dueDate` duration counts, such as `"examples.weekdays"`. Requires `dueDate`; rejected when `dueDate` is a fixed date-time. The calendar must exist, written with its exact casing. |
| `priority` | string | Optional; absent | NCalc expression resolved once when the work item is created, giving a whole number from -2,147,483,648 to 2,147,483,647; higher is more urgent. Write it as a JSON string, such as `"50"`. See [due date and priority](#due-date-and-priority). |
| `variables` | variable[] | Optional; `[]` | Retained node-scope declarations, using [variable properties](#variables-and-inputs). Current user-action endpoints validate and resolve inputs from the selected outgoing **flow's** `variables`; author action forms there. This node list does not create a second submission contract. |
| `multiInstance` | object | Optional; absent means one normal work item | Repeated human work; see all properties below. |
| `asyncBefore`, `asyncAfter`, `job` | Boolean / object | Optional; flags `false` | Shared [durable job policy](#durable-job-policy). For multi-instance work, boundaries apply once to the parent, not once per child. |

Task roles and action roles are separate checks: passing one does not bypass the other. Claim bypass (`canActWithoutClaim` and `canActWithoutClaimRoles`) belongs on the selected outgoing flow and never overrides direct assignment, roles, or visibility. Workflow `taskAssignmentRoles`, `taskRoleManagementRoles`, and `taskDistribution` are workflow-level settings, not node properties; see [ownership and role policies](developer-guide.md#apply-ownership-and-role-policies).

A `rolesVariable` value must resolve to a nonempty string array with at most 100 entries, each trimmed role at most 300 Unicode scalar values. Missing values, wrong types, empty normalized lists, and oversized lists fail closed. Waiting work retains its captured task/action roles when variables change, pending children activate, or ownership changes. Authorized role managers can replace the saved policy through the roles API; this preserves existing ownership and completed children. Explicit empty literal/manual role lists remain unrestricted.

The [multi-instance role-management walkthrough](../examples/multi-instance/README.md#role-management-across-active-and-pending-items)
shows that replacement across active/pending children and stale-edit handling.

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

Examples: [roles, claims, and bypass](../examples/user-tasks/01-roles-claim-and-bypass.json), [claim inheritance](../examples/user-tasks/02-claim-inheritance.json), [required assignment and distribution](../examples/user-tasks/04-required-assignment-and-distribution.json), and [due date and priority](../examples/user-tasks/06-due-date-and-priority.json).

#### Due date and priority

`dueDate` and `priority` are resolved once when each work item is created and saved on it. Later variable changes, claims, assignment, role changes, delegation, administrative actions, and version changes keep the saved values; a reactivated task is a new work item and resolves them again. A multi-instance task resolves them once when its execution starts, and every child gets the same values, so a sequential child's duration counts from the start of the execution. A normal task with `asyncBefore: true` creates its pending work item when the token enters the task, so its duration counts from entry, not from when the Worker activates it; activation keeps the saved values. A multi-instance task with `asyncBefore: true` starts its execution, and resolves the values, after its job runs.

| `dueDate` value | Saved due time |
| --- | --- |
| A fixed duration with the grammar of a timer's [`timeDuration`](#timer-configuration), such as `P2D`, `PT4H`, or `P1W` | The work item's creation time plus the duration. |
| A fixed date-time with `Z` or an offset, with the grammar of a timer's `timeDate`, such as `2030-01-31T17:00:00+02:00` | That instant, in UTC. A time already past makes the task overdue at once. |
| Anything else: an NCalc expression, such as `if(amount >= 10000, 'PT4H', 'P2D')` or `contractEnd` | The expression's result, which must be text in one of the two forms above. |

Expressions see the same stored variables and `sys.*`, `config.*`, and `setting.*` context as the assignee expression; `FlowInfo` and gateway helper functions are not available. A `datetime` value works only if it carries an offset. For a `date` value, append a time and an offset, for example `dueDay + 'T17:00:00Z'`.

With `dueDateCalendar`, a duration counts the named calendar's working time from the work item's creation, as described in [working-time durations](#working-time-durations): `"P2D"` is due two working days later, at the same time of day. The duration must use whole days and no weeks, such as `P5D` or `P1DT4H`, and be at most 36,525 days long. A date-time result ignores the calendar. A duration result with weeks or fractional days or longer than 36,525 days, or a calendar that no longer exists, leaves the work item without a due date and logs a warning. Changing `dueDateCalendar` never blocks an in-place workflow-version change.

A `priority` result must be a finite number without a fractional part, so `2.0` is accepted and `2.5` or the text `'5'` is not.

A null result leaves the work item without that value. Any other result that cannot be used, such as a date without a time, a date-time without an offset, a number where a due date is expected, or a failed evaluation, also leaves it without the value and logs a warning that names the node, the instance, and the reason; the transition does not fail. Definition validation rejects both properties on other node types, NCalc syntax errors, a quoted or evidently intended fixed due date that does not parse (such as `'P2X'`, `p2d`, `P1M`, `P1Y`, or `2026-11-01`; months and years are not fixed durations), and a whole-number priority outside the range. An unquoted value such as `P2X` is a valid NCalc variable name, so validation accepts it, and a missing variable is reported only by the runtime warning.

The APIs return the saved values as `dueAt` and `priority`, with a `dueStatus` of `overdue`, `dueSoon`, or `upcoming` for open work items; see [build an inbox](developer-guide.md#build-an-inbox-and-action-form). Changing only these properties never blocks an in-place workflow-version change.

```json
{
  "id": 2,
  "type": "userTask",
  "name": "Review purchase",
  "roles": ["Reviewer"],
  "dueDate": "if(amount >= 10000, 'PT4H', 'P2D')",
  "priority": "if(amount >= 10000, 80, 40)"
}
```

#### Multi-instance properties

These are properties of the `userTask.multiInstance` object, which creates human work items. A `serviceTask`, `scriptTask`, or `callActivity` uses the different automatic contract in [automatic multi-instance properties](#automatic-multi-instance-properties), and a user task refuses its `maxConcurrency` and `outputVariables`. Saving refuses `multiInstance` on every other node type; earlier Flowbit versions silently dropped it there, so remove it from such imported definitions.

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
| `asyncBefore`, `asyncAfter`, `job` | Boolean / object | Optional; flags `false` | Shared [durable job policy](#durable-job-policy). REST tasks accessing shared-variable bindings, and multi-instance REST tasks, require `asyncBefore: true`. |
| `multiInstance` | object | Optional; absent | Calls the endpoint once per collection item; see [automatic multi-instance properties](#automatic-multi-instance-properties) and [multi-instance REST](#multi-instance-rest). |

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

#### Multi-instance REST

With `multiInstance`, the task sends one request per collection item. Templates read `${mi.item}`, `${mi.index}`, and `${mi.total}` as well as the body's frozen instance inputs; in a body, `${mi.item}` in a bare position inserts the whole element as JSON, which is the way to send an object item. There is no `${mi.item.field}` path syntax. Each item reads the `config.*`, `setting.*`, and `sys.*` values its templates and expressions name when it first runs, and renders its request again from those saved values on every attempt, so `sys.jobId` stays the same for every retry of that item.

- `outputMappings[].variable` and `statusVariable` must name the body's local outputs or `readWrite` shared aliases; an ordinary instance variable, a read-only alias, or the body's collection, result, or error target is refused. A status target must be a scalar `number`.
- A successful response is mapped and validated in memory, then saved as the item's result before it is published, so a retry after that point never sends the request again. A failed item writes no status and no outputs.
- A template that cannot render, a non-2xx response, a timeout, a transport failure, or a response over the size limit fails with code `service_task_failed`, and status-matched boundaries select on the response's status. A mapping or output-contract failure of a 2xx response fails with `multi_instance_result_invalid`, selected by that 2xx status.
- An entry failure, a result too large for the item or the body, and a local validation that fails only when the result is published carry no HTTP status, so only a catch-all boundary catches them.
- A shared alias the request reads that has never been set stops the item in an incident rather than taking a boundary.

REST effects are at least once: a request can reach the receiver again after a lost lease or a Worker restart before its result was saved. Send an idempotency header the receiver deduplicates on, such as `{ "name": "Idempotency-Key", "value": "flowbit-${sys.jobId}" }`. See the [parallel](../examples/multi-instance/08-rest-parallel-notifications.json) and [sequential](../examples/multi-instance/09-rest-sequential-reservations.json) REST examples.

### scriptTask

Executes authored logic and follows exactly one unconditional outgoing flow without action or multi-instance metadata. Scripts write declared process variables; they do not create input forms. Writes are staged together, coerced to each target's type, validated, and committed only when the whole script succeeds. An attached error boundary can catch a failure without retaining partial script outputs.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `type` | string | Set to `"scriptTask"` | Automatic script activity. |
| `scriptFormat` | string | Optional; `"ncalc"` | `ncalc` or `javascript`. Recognized casing is canonicalized; explicit blank/null/unknown values are invalid when saving. |
| `assignments` | assignment[] | Optional; `[]` in NCalc mode | Ordered NCalc assignments; empty means no-op. Must not contain null entries. JavaScript mode cannot carry a nonempty list. |
| `script` | string | Required nonblank in JavaScript mode; otherwise absent | JavaScript body, parsed at definition save. NCalc mode cannot include a nonblank body. |
| `usesFlowInfo` | Boolean | Optional; see compatibility note | JavaScript opt-in for `execution.getFlowInfo`. Author explicit `true` when using it and `false` otherwise. NCalc cannot set it true. For older JavaScript definitions, omission infers a direct `execution.getFlowInfo(...)` call; normalization stores the Boolean. Aliased/computed access requires explicit true. |
| `asyncBefore`, `asyncAfter`, `job` | Boolean / object | Optional; flags `false` | Shared [durable job policy](#durable-job-policy). A multi-instance script task requires `asyncBefore: true`. |
| `multiInstance` | object | Optional; absent | Runs the script once per collection item; see [automatic multi-instance properties](#automatic-multi-instance-properties) and [multi-instance scripts](#multi-instance-scripts). |

Each NCalc `assignments` entry has:

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `variable` | string | Required | Declared process target name, matched case-insensitively. A shared target must have write access. |
| `expression` | string | Required nonblank | Valid NCalc expression over current values and expression context. Later assignments can read earlier assignments' staged values. Supports instance-wide `FlowInfo` where used with its literal arguments. |

Scalar results assigned to an array target are wrapped as one-element arrays; existing arrays have their elements coerced. Numeric/Boolean conversions and final target validation are shared by both script formats. An assignment that divides by zero, or whose result is a number that is not finite (`1 % 0`, `1 / 0`), fails the script like any other assignment error, so an attached error boundary catches it. Example: declare `subtotal`, `tax`, and `total` as process numbers before using this fragment.

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

#### Multi-instance scripts

With `multiInstance`, the script runs once per collection item, each with its own buffer. Both formats write only the body's local outputs or `readWrite` shared aliases; a write to an ordinary instance variable, a read-only alias, an `mi.*` name, or the body's collection, result, or error target fails. NCalc targets are checked when saving; JavaScript targets are checked when `setVariable` runs, and writing one shared key through two aliases fails at the second write.

- **Reading the item.** NCalc reads `[mi.item]`, `[mi.index]`, and `[mi.total]`; JavaScript uses `execution.getVariable('mi.item')`. In NCalc, `string`, `number`, and `boolean` items arrive as their values, `date` and `datetime` items as their text, a `json` object as its JSON text, and a `json` array as a list, so read object properties with JavaScript. Getters return copies: changing a returned object changes neither the snapshot nor another item.
- **Ordered writes.** Later assignments and statements read the item's own earlier writes, never another item's locals.
- **What an item sees.** Frozen instance inputs, its saved `config.*`, `setting.*`, and `sys.*` values, shared values read when it first ran, and FlowInfo captured once for the body. A shared binding that a JavaScript body reads only because its reads cannot be predicted, and that has never been set, is absent (`hasVariable` returns false).
- **Failures.** A script error, sandbox limit, NCalc evaluation error, or rejected write fails with `script_task_failed`; an unset non-nullable local, a false local validation, or a shared write failing its catalog contract fails with `multi_instance_result_invalid`. Both follow the node's `job` policy. Inputs are frozen, so a retry usually fails the same way; attach an error boundary to a script body.
- **Shared locks.** A JavaScript item locks every shared binding of the definition except the body's collection while it stages, because its reads and writes are dynamic. Declare only the bindings the workflow needs, or use NCalc, whose access is exact.

See the [NCalc](../examples/multi-instance/10-ncalc-script-per-item.json) and [JavaScript](../examples/multi-instance/11-javascript-script-per-item.json) examples.

### callActivity

Starts a child instance of another workflow family and rests until that child finishes. The child is an ordinary instance with its own variables, history, tasks, version, and operations. Inputs are copies taken when the token reaches the call, and results are copies taken when the child ends; nothing else is shared, and later changes on either side do not affect those copies.

**Topology:** at least one incoming flow; exactly one unconditional outgoing flow without submitted variables, action roles, or gateway flags. **Worker:** always required, even when both workflows are otherwise synchronous, because a Worker creates the child and delivers its result. Durability is built in, so `asyncBefore`, `asyncAfter`, and `job` are invalid on a call. With [`multiInstance`](#multi-instance-calls), a call starts one child per collection item.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `type` | string | Set to `"callActivity"` | Resting call of another workflow family. |
| `callActivity.workflowKey` | string | Required | Stable key of the family to call, written exactly. At most 300 Unicode scalar values and no surrounding whitespace. It cannot be this definition's own `id`, and `${...}` is not replaced. |
| `callActivity.versionBinding` | string | Optional; omitted | Only `publishedDefault` is accepted, and the editor omits it. A resolved version belongs to a visit, so it is never saved in the workflow. |
| `callActivity.inputMappings` | mapping array | Optional; `[]` | At most 100. Copies this workflow's variables into declared inputs of the target's default plain start. `null` is invalid; omit the property instead. |
| `callActivity.outputMappings` | mapping array | Optional; `[]` | At most 100. Copies child variables into this workflow's declared process variables after the child completes, or, in a multi-instance call, into item locals or `readWrite` shared aliases. `null` is invalid; omit the property instead. |
| `multiInstance` | object | Optional; absent | Starts one child per collection item; see [multi-instance calls](#multi-instance-calls). |

Each mapping copies one whole variable, so it has no path, expression, default, or validation of its own:

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `sourceVariable` | string | Required | Variable stored on the source instance: a process variable, entry variable, user-task flow variable, or REST or message output target. It must be declared or produced by the source workflow, and cannot be a shared alias or use a reserved prefix (`sys.`, `config.`, `setting.`, `mi.`, `gateway.`). An input mapping of a multi-instance call may also read a readable shared alias and exactly `mi.item`, `mi.index`, or `mi.total`, but never the body's own `resultVariable`. Saved in its declared spelling. |
| `variable` | string | Required | Destination: a declared input of the target's start, or an instance-scoped process variable of this workflow; in a multi-instance call, an item local or a `readWrite` shared alias instead. Unique within its list ignoring case. Other shared aliases, reserved prefixes, and the target start's idempotency variable are rejected. |
| `dataType` | string | Required | `string`, `number`, `boolean`, `date`, `datetime`, or `json`. It must agree with the destination and with a declaration of the source; nothing is converted. |
| `isArray` | Boolean | Optional; `false` | Whether the value is an array. |
| `required` | Boolean | Optional; `false` | When `true`, a missing source or JSON null fails the call. When `false`, a missing source omits that input or leaves that output target unchanged, and a present null is passed on. It never weakens a child's required input. |

```json
{
  "id": 12,
  "name": "Manager approval",
  "type": "callActivity",
  "callActivity": {
    "workflowKey": "manager-approval",
    "inputMappings": [
      { "sourceVariable": "requestedAmount", "variable": "amount", "dataType": "number", "required": true }
    ],
    "outputMappings": [
      { "sourceVariable": "approved", "variable": "managerApproved", "dataType": "boolean", "required": true }
    ]
  }
}
```

**Target contract.** A call always starts the target family's default plain start, the `startEvent` selected by `initialEventId`. That start must have no `roles` and no `idempotency`, every required input needs a mapping with `required: true`, and an unmapped optional input uses its default. A draft can be saved before its target exists; publishing or making a version the default checks the target's current published default, and creating each child checks again. See [what happens when a target changes](developer-guide.md#call-another-workflow).

**Version.** The Worker selects the family's published default when it creates the child, and the visit keeps that version. A child that already exists keeps its version when the default changes. A default that no longer fits fails that visit; it never selects another version or drops a mapping.

**Values.** Input and output snapshots are each limited to 1 MiB. The child's start validation runs over the complete input overlay, and this workflow's process-variable validation runs over the complete output overlay. That validation can read a shared alias, which it sees at its current catalog value when the result is delivered, so retrying a refused result reads it again. Outputs are all written or none are, and they cannot include the attached boundary's `errorVariable`.

**Identity.** The child starts as the fixed system actor `callActivity`, with no roles, claims, or delegation, and this workflow continues as the same actor after delivery. In the child, `sys.user` is therefore `callActivity`, so an `assignee` expression or `${sys.user}` default that relies on the starter produces a value no real user owns. Pass a requester or approver as an ordinary mapped input instead. The child's own tasks keep their roles, assignment, and action checks.

**Boundaries and failures.** A call may have one catch-all [errorBoundaryEvent](#errorboundaryevent). `errorMatch` is invalid on a call, and so are signal boundaries, and timer and conditional boundaries unless the call is multi-instance. The boundary's `errorVariable` receives `code: description` on a single call and the code alone on a multi-instance call.

| What happened | With a boundary | Without a boundary |
| --- | --- | --- |
| The child faulted (`call_child_faulted`) or was cancelled on its own (`call_child_cancelled`) | Takes the boundary. | This workflow faults at the call node. |
| The call could not start: `call_target_unavailable`, `call_target_incompatible`, `call_start_failed`, `call_input_invalid`, `call_business_key_conflict`, `call_depth_exceeded`, `call_capacity_exceeded` | Takes the boundary. | Opens a retryable incident; this workflow keeps waiting. No child exists. |
| The result is invalid (`call_output_invalid`) | Takes the boundary. | Opens a retryable incident; this workflow keeps waiting. The child stays completed. |
| A job inside the child has an incident | The call keeps waiting: this is not a fault. | The call keeps waiting: this is not a fault. |

A `terminateEndEvent` in the child is a successful completion of that child only. Calls may appear in loops, in parallel branches, and inside other calls. Each visit gets its own child, each call entry counts against `Workflow.Async.MaxConsecutiveAutomaticActivations`, including one that its error boundary catches before a child exists, and nesting is limited by [`WorkflowCallActivities`](deployment.md#configuration). External effects a child has already performed are not undone when a call is cancelled.

#### Multi-instance calls

With [`multiInstance`](#automatic-multi-instance-properties), the call starts one child per collection item, each with its own invocation. Input mappings can read `mi.item` (typed like the collection's elements), `mi.index`, `mi.total`, frozen instance inputs, and readable shared aliases; output mappings write the body's local outputs or `readWrite` shared aliases. Each item reads its shared inputs once, when its `callStart` first stages, and every retry of that item reuses them.

- **Version pin.** When the body initializes, a Worker resolves the family's published default and its default plain start once, checks the mappings against it, and pins it. Every item and retry starts that version, even after the family's default changes. The pinned version cannot be unpublished or deleted until the body settles or is cancelled. An empty collection completes with `[]` and needs no usable target.
- **Entry failures.** An invalid or oversized collection, a result that cannot fit, `call_target_unavailable`, `call_target_incompatible`, or `call_depth_exceeded` takes the catch-all boundary at once with no partial result; without one, the body's `multiInstanceStart` job retries after 10 seconds, 1 minute, and 5 minutes, then opens an incident. The depth limit is checked once, here.
- **Open-call limit.** Items count against `WorkflowCallActivities:MaxOpenInvocationsPerRoot`. A full root is back-pressure: items stay pending, take no boundary, and the dispatch job retries, opening a `call_capacity_exceeded` incident only when the body has nothing in flight and its retry delay has reached its maximum. Calls made inside the children count against the same root and are refused as ordinary calls are, so keep `maxConcurrency` times the calls each child holds open below the limit.
- **Slots.** An item keeps its slot until its child's result is delivered, including while the child waits for people, messages, timers, or its own incidents.

| What happened to an item | With the catch-all boundary | Without it |
| --- | --- | --- |
| Its child faulted (`call_child_faulted`) or was cancelled on its own (`call_child_cancelled`) | Stops the body, cancels unfinished children, publishes the partial result, and takes the boundary. | Stops the body and faults this workflow with that code. |
| Its start failed (`call_start_failed`, `call_business_key_conflict`) or its mapped inputs were invalid (`call_input_invalid`) | Takes the boundary. | Opens an incident on its `callStart`; the retry reuses the saved inputs and the pin. |
| Its result is invalid (`call_output_invalid`) | Takes the boundary. | Opens an incident on the child's `callResume`; the child stays completed and never runs again. |
| Its result does not fit the item or the body result | Takes the boundary with `multi_instance_result_too_large`. | Faults this workflow; this is never an incident. |
| A shared output changed since the item staged | Not a business failure: opens `output_version_conflict` on the `callResume`. | The same. |
| A shared binding is missing, archived, or denied while the item stages | Not a business failure: opens an incident on its `callStart`. | The same. |

A pinned version that a newer family version made unstartable, for example by enabling business keys, fails every remaining item with `call_start_failed`. See the [multi-instance call example](../examples/call-activities/04-multi-instance-approval-call.json).

See the [call activity examples](../examples/call-activities/README.md).

### Automatic multi-instance properties

A `multiInstance` object on a `serviceTask`, `scriptTask`, or `callActivity` makes the node a **body** that runs its activity once per element of a typed collection and publishes an ordered result. Items are durable work owned by one parent token, never human tasks. See [BPMN semantics](bpmn-support.md#multi-instance-service-script-and-call-activities) for the lifecycle and the [user-task properties](#multi-instance-properties) for human multi-instance work.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `mode` | string | Optional; `"parallel"` | `parallel` admits up to the effective concurrency at once; `sequential` admits one item and starts the next only after the previous one has committed. Recognized casing is canonicalized; null or unknown values are invalid. |
| `source` | string | Optional; `"collection"` | Only `collection`. `cardinalityExpression` and `onePerActor: true` are refused. |
| `collectionVariable` | string | Required | Declared process variable with `isArray: true` and `dataType` `string`, `number`, `boolean`, `date`, `datetime`, or `json`: an instance variable, or a shared alias with `read` or `readWrite` access. A scalar `json` holding an array is not a collection. Elements keep their order, duplicates, casing, and whitespace; they are not usernames. |
| `completionEvaluation` | string | Required; `"afterAll"` | Write `afterAll` explicitly. An omitted value or `afterEach` is refused, because an omitted value reads as `afterEach`. |
| `maxConcurrency` | integer | Optional | 1 to 1,000. In parallel mode an omitted value uses the `Workflow.MultiInstance.DefaultConcurrency` setting; the effective window is the smallest of this value, the `Workflow.MultiInstance.MaxConcurrency` setting, and the item count. Sequential mode accepts only `1` or omission. Explicit `null`, fractions, zero, and negative values are refused. |
| `outputVariables` | array | Required; may be `[]` | Up to 100 local output declarations, below. An empty list suits an activity run only for its effect. |
| `resultVariable` | string | Required | Declared scalar `json` process variable (`isArray: false`): an instance variable with an array `defaultValue` (author `[]`), or a `readWrite` shared alias whose catalog contract accepts `[]`. It must differ from the collection, every item output and status target, the body's error targets, and every other multi-instance node's result, compared by name and by shared key. |

Each `outputVariables` entry declares an item-local value. Other fields are refused.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `name` | string | Required | 1 to 300 Unicode scalar values, without surrounding whitespace or a reserved prefix (`sys.`, `config.`, `setting.`, `mi.`, `gateway.`). Unique ignoring case within the body, and must not collide with a process variable, an entry, task, or action input, or a REST, message, status, or error target that writes the instance. |
| `dataType` | string | Required | `string`, `number`, `boolean`, `date`, `datetime`, or `json`. |
| `isArray` | Boolean | Optional; `false` | Whether the local holds an array. |
| `nullable` | Boolean | Optional; `false` | A nullable local left unset becomes JSON `null`. A non-nullable local must have a value when the item succeeds. |
| `defaultValue` | JSON | Optional | Applied to a fresh buffer on every attempt and type-checked when saving. A string default can contain `${...}` placeholders over the item context. |
| `validation` | string | Optional | NCalc rule checked against the item's completed locals. It cannot use `FlowInfo`, gateway helpers, `CountFlow`, `PercentFlow`, or an `mi.*` name other than the three below. |

**Item context.** Each item reads these read-only names; they are never variables and cannot be written:

| Name | Value | How to read it |
| --- | --- | --- |
| `mi.item` | The copied element, with its declared type and JSON value. | `[mi.item]` in NCalc, `${mi.item}` in a REST template, `execution.getVariable('mi.item')` in JavaScript, `"sourceVariable": "mi.item"` in a call input mapping. |
| `mi.index` | Zero-based position, from 0 to the item count minus one. | The same forms with `mi.index`. |
| `mi.total` | The frozen item count. | The same forms with `mi.total`. |

Progress names such as `mi.completed` and `mi.remaining` are refused in item expressions, because they depend on completion order. Items never read the body's own `resultVariable`: saving refuses a REST template, NCalc assignment, mapping or local-output default or validation, or call input mapping that names it. A JavaScript body is not checked; the result is simply absent from its items.

**Result.** The body resets `resultVariable` to `[]` when it initializes. Once every item has succeeded, it publishes one index-ordered row per item and continues on its outgoing flow; an empty collection publishes `[]`.

```json
[
  { "kind": "item", "index": 0, "item": "supplier-a", "status": "completed", "variables": { "receiptId": "notice-001", "httpStatus": 201 } },
  { "kind": "item", "index": 1, "item": "supplier-b", "status": "completed", "variables": { "receiptId": "notice-002", "httpStatus": 201 } }
]
```

`variables` holds the item's local outputs only, or `{}` with no declarations; shared writes are never copied into it. When an error boundary catches an item failure, the partial result also has `failed` rows for the failure and any already failed items and `cancelled` rows for unfinished and unstarted items, each with `"variables": {}`. A timer or conditional interruption and cancellation publish no result.

**Topology and durability.** A body has exactly one ordinary unconditional outgoing flow, without a condition, default, `isSelectable: false`, or completion settings. A service or script body requires `asyncBefore: true`; its `job` policy applies to its entry and to each item, and an optional `asyncAfter` runs once, after the result is published. A call body refuses `asyncBefore`, `asyncAfter`, and `job`. Error boundaries keep their host's rules; timer and conditional boundaries attach to all three kinds, and signal boundaries to none, as [boundary host rules](#boundary-host-rules) describe. A boundary's `errorVariable` receives a stable failure code, never request or response data; declare it as a process variable, an instance `string` or a `readWrite` shared alias.

**Limits.** At most `Workflow.MultiInstance.MaxInstances` items (default 1,000; an empty collection is valid), a 1 MiB input snapshot, a 1 MiB result, and 1 MiB per item for its frozen inputs and saved result together. Sizes count the text PostgreSQL stores for the JSON, which can be larger than compact JSON. A result whose rows cannot fit even before any output is refused at entry, before any item runs.

```json
{
  "id": 2,
  "name": "Notify each supplier",
  "type": "serviceTask",
  "asyncBefore": true,
  "multiInstance": {
    "mode": "parallel",
    "source": "collection",
    "collectionVariable": "supplierIds",
    "completionEvaluation": "afterAll",
    "maxConcurrency": 4,
    "outputVariables": [
      { "name": "receiptId", "dataType": "string", "isArray": false, "nullable": false }
    ],
    "resultVariable": "notificationResults"
  },
  "service": {
    "type": "rest",
    "method": "POST",
    "url": "${config.exampleApiBaseUrl}/suppliers/${mi.item}/notifications",
    "headers": [{ "name": "Idempotency-Key", "value": "flowbit-${sys.jobId}" }],
    "body": "{\"supplierId\":${mi.item},\"index\":${mi.index}}",
    "outputMappings": [
      { "variable": "receiptId", "path": "id", "dataType": "string", "isArray": false, "required": true }
    ]
  }
}
```

Declare `supplierIds` as a process `string[]` and `notificationResults` as a process `json` with `defaultValue: []`, and connect one outgoing flow. See the [automatic multi-instance examples](../examples/multi-instance/README.md#automatic-multi-instance-activities).

## Events

Every event also has the [common node properties](#common-node-properties). Set `type` to the exact name in its heading. Event properties listed here add to those common properties; task ownership, assignment, multi-instance configuration, `asyncBefore`, `asyncAfter`, and node `job` policies do not apply to events. Timers, durable conditionals, and signal deliveries use Worker jobs through their own event contracts.

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
| `callOnly` | Boolean | Optional; `false` | Only a [call activity](#callactivity) can start from this event: public start routes refuse it and the start page hides it. Valid only on the start selected by `initialEventId`, with no `roles` and no `idempotency`. It is omitted on export when false. It does not limit which workflows can call this one. |

Changing `initialEventId` while the previous start keeps `callOnly: true` is invalid; correct the old start's flag explicitly. Use the flag for a fragment that relies on checks made by its parent, such as a payment or KYC step, because an unrestricted start is otherwise open to every authenticated user. A definition with `callOnly` follows the same publication gate as other durable work.

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
| `timer` | [timer configuration](#timer-configuration) | Required | Exactly one fixed absolute date, relative duration, recurrence, or [cron schedule](#cron-schedules). A timer start cannot use an [expression schedule](#expression-schedules). Relative timing is based on schedule activation. |

There are no event-specific input variables or roles. Use top-level process-variable defaults to initialize scheduler-created instances; `variables` is omitted from canonical timer-start JSON. `businessKey` and `idempotency` are rejected.

```json
{
  "id": 3,
  "name": "Daily check",
  "type": "timerStartEvent",
  "timer": { "timeCycle": "R/P1D" }
}
```

A cron schedule follows the local time of a named zone, such as every weekday at 08:00 in London:

```json
{
  "id": 4,
  "name": "Weekday morning review",
  "type": "timerStartEvent",
  "timer": { "cron": "0 8 * * 1-5", "timeZone": "Europe/London" }
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

The event carries no node `variables` input form. Credentials and correlation templates resolve against stored values and server-controlled context, before applying inbound outputs. All output mappings commit atomically. Timer, conditional, and signal boundaries may attach to this wait.

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

Use the [receipt-versus-timeout example](../examples/messages/README.md#example-03-receipt-versus-timeout)
to compare successful delivery with an interrupting timer boundary and rejected
late delivery.

### intermediateTimerCatchEvent

Persists a timed wait. The Worker resumes the token when its scheduled occurrence is due. A cyclic catch consumes a trigger and then leaves the node; it does not produce recurring sibling branches after departure.

**Topology:** at least one incoming flow; exactly one unconditional outgoing flow. **Worker:** required.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `timer` | [timer configuration](#timer-configuration) | Required | Exactly one schedule. `timeDate` and `timeDuration` may be [expressions](#expression-schedules). Relative durations start when the wait is activated. |

```json
{
  "id": 21,
  "name": "Wait thirty seconds",
  "type": "intermediateTimerCatchEvent",
  "timer": { "timeDuration": "PT30S" }
}
```

Timer, conditional, and signal boundaries may attach to this event. A Worker outage delays delivery; a finite cyclic catch with no eligible occurrence remaining opens an incident. See [timer semantics](bpmn-support.md#timers).

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

Declare persisted instance variable `approved`. Timer, conditional, and signal boundaries cannot attach to a conditional catch. See the [conditional runtime reference](../Flowbit/README.md#intermediate-conditional-catch-events).

### intermediateSignalCatchEvent

Persists a wait for a named signal. A signal is thrown through `POST /api/signal-broadcasts`, or by a workflow's [signal throw](#intermediatesignalthrowevent) or [signal end event](#signalendevent), and reaches every signal catch that is waiting for its name, in one workflow family or in all of them, when it is thrown. The Worker then delivers it to each waiting token: the catch maps the broadcast's JSON payload into variables and continues through its outgoing flow. Use it when many instances must react to one announcement, such as a changed policy; use an [intermediate message catch](#intermediatemessagecatchevent) for something that must not be missed. A throw can still be limited to the instance with one business key, or to one instance ([signal scope](developer-guide.md#limit-a-signal-to-one-case)).

**Topology:** at least one incoming flow; exactly one unconditional outgoing flow. **Worker:** required, and the definition publishes only when durable publication is enabled.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `signal` | [signal configuration](#signal-configuration) | Required | The literal signal name and optional typed payload mappings. |

```json
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
}
```

The example assumes `policyVersion` is undeclared or a declared scalar `number` instance variable.

- **When it waits.** A token that arrives on the catch creates a signal subscription for that visit and rests there. The subscription ends in the same transaction as the wait: when a signal is delivered, when an attached boundary interrupts the catch, or when the instance or the token's branch is cancelled. A loop that returns to the catch subscribes again for the new visit.
- **What it receives.** A throw freezes its recipients when it runs, so a catch reached after the throw does not receive it, and a signal is not stored for later waits. One broadcast advances one wait: when a catch is followed by another catch of the same name, the second subscribes after the first was delivered, and the same broadcast does not reach it.
- **Delivery.** The delivery runs as the fixed system actor `signal`, with no roles or claims. It writes the mapped values on the catch's visit, lets conditional events observe them, and then follows the outgoing flow if the token is still waiting. History records a `signalReceived` row whose payload names the broadcast, the signal, and its thrower, with `thrownByInstanceId` and `thrownByNodeId` when a workflow threw it; the catch's visit completes with reason `signalReceived`, and the outgoing flow's traversal evidence has kind `signalCatch`. Routing after the catch runs in the Worker, so a later service or script task becomes a durable job of its own.
- **Failures.** A payload that fails a mapping fails only that delivery with `signal_payload_invalid`, writes nothing, and leaves the catch waiting and subscribed.
- **Boundaries.** Timer, conditional, and [signal](#signalboundaryevent) boundaries may attach, for example to stop waiting after seven days; see [boundary host rules](#boundary-host-rules). A signal boundary on the catch must use another signal name than the catch. A conditional boundary that the mapped values make true can take the token before the catch continues.
- **Other settings.** Only the [common node properties](#common-node-properties) and `signal` are accepted. Task, role, variable, timer, conditional, message, async, job, and multi-instance settings are rejected, as is `signal` on any other node type.

See [signals](bpmn-support.md#signals), [broadcasting signals](developer-guide.md#broadcast-signals), and the [signal catch example](../examples/signals/01-signal-catch-policy-change.json).

### intermediateSignalThrowEvent

Throws a signal from the workflow and continues at once. When a token reaches the event, it creates the same broadcast that `POST /api/signal-broadcasts` creates, with its `signal.name`, the recipients its `signal.scope` names, and an optional payload copied from the instance, and then follows its one outgoing flow without waiting for the deliveries. A running Worker delivers the signal to every waiting [signal catch](#intermediatesignalcatchevent) and every [signal boundary](#signalboundaryevent) with that name on an active host in the scope, including other waits of the throwing instance. Use it when one part of a process announces something that other work must react to, such as a cancelled order that stops the shipment branch of the same order, or a decision that stops the work of another workflow. To throw as a branch ends, use a [signal end event](#signalendevent).

**Topology:** at least one incoming flow; exactly one unconditional outgoing flow, with no condition, default flag, priority, roles, inputs, claim bypass, or multi-instance outcome settings. **Worker:** required for the deliveries, and the definition publishes only when durable publication is enabled.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `signal` | [signal configuration](#signal-configuration) | Required | The literal signal name, the required [`scope`](#signal-scope), and an optional [`payload`](#signal-payload). `outputMappings` must be missing or empty. |

```json
{
  "id": 9,
  "name": "Announce the cancellation",
  "type": "intermediateSignalThrowEvent",
  "signal": {
    "name": "orders.cancelled",
    "scope": { "kind": "thisInstance" },
    "payload": [
      { "name": "reason", "variable": "cancelReason", "required": true },
      { "name": "orderInstance", "source": "instanceId" }
    ]
  }
}
```

The example assumes the workflow declares or produces `cancelReason`, and that one of its signal catch or boundary events waits for `orders.cancelled`, which the scope `thisInstance` requires.

- **When it throws.** In its own routing step, inside the transaction that moved the token there. The throw locks no other instance and waits for none: it freezes the matching signal waits that are active at that moment and queues their delivery jobs, then the token continues. The broadcast commits or rolls back with the step, so a step that fails later leaves no broadcast, and a retried job throws once. [Which waits receive it](#which-waits-a-thrown-signal-reaches) explains the waits of the throwing instance.
- **After the throw.** The token never waits for a delivery, and no delivery failure reaches the throwing instance. The broadcast outlives its thrower: cancelling, ending, or faulting the instance afterwards does not cancel it. It is listed, cancelled, and retried on the **Signals** page and the [broadcast routes](api-guide.md#signals) like one thrown through the API, with this instance and node as its thrower.
- **Records.** History records the advance from the throw to the next node with note `signalThrown`; its payload holds `broadcastId`, `signalName`, `eventNodeId`, `eventKind` `throw`, `scopeKind`, `workflowKey` (the target family, null for all workflows), and `recipientCount`, never a business key or a payload value. The visit completes with reason `normal`, and the outgoing flow's traversal evidence has kind `signalThrown`, with no action. A receiving instance's `signalReceived` row names the throwing instance and node as `thrownByInstanceId` and `thrownByNodeId`, and the receiver continues as the fixed system actor `signal`.
- **Refusals.** A throw that cannot be built, or that a limit stops, fails its step; it is never dropped. See [refused throws](#refused-signal-throws).
- **Versions and reactivation.** No token rests on the event, so a version change that alters its name, scope, or payload adds no blocker and applies to the next token that enters it. A token that passes the event again, for example after a reactivation or in a loop, throws again.
- **Other settings.** Only the [common node properties](#common-node-properties) and `signal` are accepted. Task, role, variable, timer, conditional, message, async, job, and multi-instance settings are rejected, and no boundary can attach to the event.

See [signals](bpmn-support.md#signals), [throwing a signal from a workflow](developer-guide.md#throw-a-signal-from-a-workflow), and the [order cancellation example](../examples/signals/03-signal-throw-order-cancellation.json).

### intermediateLinkThrowEvent

A "go to" connector inside one workflow. A token that enters a link throw event continues at the [link catch event](#intermediatelinkcatchevent) with the same `name`, as if a sequence flow joined the two events. Use a link pair for a long return or crossing flow, or for a section that several routes enter, such as a shared closing step. Keep short flows as they are. A link cannot leave its workflow; use a [call activity](#callactivity) to reach another one.

**Topology:** at least one incoming flow; no outgoing flows. **Worker:** not required. **Additional properties:** none beyond the [common node properties](#common-node-properties); `name` is the link name.

```json
{ "id": 7, "name": "Fulfilment", "type": "intermediateLinkThrowEvent" }
```

**Pairing.** A throw and a catch pair by their `name`:

- The name is trimmed as .NET `string.Trim()` trims it: Unicode white space and U+0085, but not the U+FEFF byte-order mark. The trimmed names are then compared exactly, including letter case, with no Unicode normalization.
- Every throw needs a link catch event of its name. Several throws may share one catch. A throw whose name matches a catch only ignoring letter case is rejected, and the message names both spellings.
- A blank name is rejected as for any node. Other node types may use the same name as a link.

Only `id`, `name`, `externalId`, `attributes`, `laneId`, `x`, and `y` are accepted. Task, role, variable, event, async, and multi-instance settings are rejected, and a boundary event cannot attach to a link event.

**Routing.** When a token enters the throw, the engine moves it to the catch in the same transaction and on the same gateway branch; the Worker is not involved. The throw's node visit completes as `normal` with no exit flow, and history records a `linkThrow` row from the throw to the catch with no sequence flow or action. The catch then takes its own routing step. The hop between the two events has no sequence flow, so it records no flow evidence and `FlowInfo` cannot refer to it; the flow into the throw and the flow out of the catch record traversals as usual.

Each link event is one routing step and counts against the per-token hop limit, as an automatic `task` does. Link events create no durable work, so they never count against the automatic-activation guard. A [scoped interrupt](#scopedinterruptevent) reached after a link therefore takes effect two routing steps later than one reached directly from the fork, as it would after two automatic tasks.

**Loops.** A catch may come before its throws in the flow, which forms a loop. A loop that passes a user task or another resting node is valid. A loop of only link events and automatic nodes saves, but fails at run time with the `Pass-through routing cycle detected.` error, as any automatic cycle does.

See the [link events example](../examples/basics/11-link-events.json).

### intermediateLinkCatchEvent

Where a link continues. A token that enters any [link throw event](#intermediatelinkthrowevent) with the same `name` continues here, then follows this event's one outgoing flow. An instance cannot start at a link catch event, and it is never a reactivation target.

**Topology:** no incoming flows; exactly one unconditional outgoing flow. **Worker:** not required. **Additional properties:** none beyond the [common node properties](#common-node-properties); `name` is the link name.

```json
{ "id": 12, "name": "Fulfilment", "type": "intermediateLinkCatchEvent" }
```

- Link catch names must be unique among link catch events, ignoring letter case, so two links cannot differ only in case.
- At least one link throw event must use the catch's exact name; an unused catch is rejected.
- The outgoing flow has no condition, default flag, priority, roles, inputs, claim bypass, or multi-instance outcome settings. Task, role, variable, event, async, and multi-instance settings are rejected on the catch, as on a throw.

The token arrives with no entry flow. The catch's visit completes as `normal` through its outgoing flow; history records a `linkCatch` row, and that flow's traversal evidence has kind `linkCatch`. Graph checks follow the link from each throw to its catch as they follow a sequence flow: inclusive and complex merge synchronization, `joinCancellation` and `scopedInterruptEvent` reachability, reactivation safety, and the shared-variable lock-order check. A version change that keeps a throw's ID but pairs it with a different catch is blocked while branch or gateway state is active; see [version changes](developer-guide.md#select-and-change-workflow-versions).

### errorBoundaryEvent

Catches a failure of its attached service task, script task, or [call activity](#callactivity) and routes through an error path. The failed host visit is recorded as faulted and the boundary path continues instead of the normal host continuation. It handles host failures rather than filtering authored `errorEndEvent` codes.

**Topology:** no incoming flows; exactly one unconditional outgoing flow. **Worker:** follows the host's execution mode.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `attachedToRef` | integer | Required | ID of an existing `serviceTask`, `scriptTask`, or `callActivity`. A call has one catch-all. A REST service task may have up to eight error boundaries, including at most one catch-all. A script task has at most one, which is always a catch-all. The same rules apply to [multi-instance bodies](#automatic-multi-instance-properties), where the boundary catches a failure of the whole body. |
| `errorVariable` | string | Optional; absent | Captures the failure reason. Must be a nonblank name of at most 300 Unicode scalar values when configured, without `sys.`, `config.`, `setting.`, or `mi.` prefix. It can be an implicit runtime output; if a same-name process variable exists, it must be scalar `string`. It must not collide case-insensitively with the host service's status/output targets. Several boundaries on one host may share a variable; only the selected boundary writes it. On a multi-instance body it receives the stable failure code, such as `service_task_failed` or `call_child_faulted`; declare it there as a process variable, because the body's error route writes only declared targets. |
| `errorMatch` | object | Optional; absent | REST service host only. The HTTP statuses this boundary catches. Absent or `null` means catch-all. An empty matcher is invalid; it never becomes a catch-all. |
| `errorMatch.httpStatusCodes` | integer array | Optional | Exact statuses from `100` through `599`. `null` means omitted. |
| `errorMatch.httpStatusRanges` | object array | Optional | Inclusive `{ "from", "to" }` ranges, both required integers from `100` through `599` with `from` below `to`. `null` means omitted. Use an exact status for a single value. |

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

#### HTTP status matching

These node fragments assume REST host `10` exists. Each boundary also needs its own outgoing flow:

```json
[
  {
    "id": 30,
    "name": "Duplicate request",
    "type": "errorBoundaryEvent",
    "attachedToRef": 10,
    "errorMatch": { "httpStatusCodes": [409] },
    "errorVariable": "integrationFailure"
  },
  {
    "id": 31,
    "name": "Correct request data",
    "type": "errorBoundaryEvent",
    "attachedToRef": 10,
    "errorMatch": { "httpStatusRanges": [{ "from": 400, "to": 499 }] },
    "errorVariable": "integrationFailure"
  },
  {
    "id": 32,
    "name": "Integration unavailable",
    "type": "errorBoundaryEvent",
    "attachedToRef": 10,
    "errorMatch": { "httpStatusRanges": [{ "from": 500, "to": 599 }] },
    "errorVariable": "integrationFailure"
  },
  {
    "id": 33,
    "name": "Other integration failure",
    "type": "errorBoundaryEvent",
    "attachedToRef": 10,
    "errorVariable": "integrationFailure"
  }
]
```

Selection runs once a task has failed, and uses the status of that invocation: an exact status first, then a range, then the catch-all. Definition order and node IDs never decide the winner. It does not read `statusVariable`, the response body, the error text, or any variable.

| Rule | Detail |
| --- | --- |
| Limits | At most 32 selectors in one matcher, counted across both arrays before any deduplication. |
| Within one matcher | Rejects duplicate statuses, overlapping ranges, and a status inside its own range. |
| Across one host | Rejects a status on two boundaries and ranges that intersect, so `400-450` and `450-499` conflict while `400-449` and `450-499` do not. A status inside another boundary's range is valid, and the status wins. The same status on different hosts is valid. |
| JSON types | Numeric strings, fractions, booleans, null elements, and unknown matcher members are rejected. A misspelled selector cannot widen routing. |
| Other nodes | `errorMatch` is rejected on a script host or any other node type. It is not silently discarded. |

What each failure can match:

| Failure | Selection |
| --- | --- |
| Completed HTTP status such as `409` or `503` | Exact, then range, then catch-all. |
| Body over the size limit, or connection lost while reading the body | The status already received, even though the call did not complete. |
| HTTP `200` whose output mapping or status write fails | `200` may match a boundary, since the task did fail. A successful task never takes an error boundary. |
| Timeout, including one during the body, preflight failure, or status `0` | Catch-all only. `0` is not a valid selector. |
| Completed status outside `100..599`, such as `600` | Catch-all only. `statusVariable` still receives the actual status. |
| Script failure | Catch-all only. |

Caller cancellation, stale activations, output-version conflicts, and database failures are not task failures, and no boundary handles them.

The service-task HTTP client follows redirects, so selection sees the final response. A `3xx` selector matches only a response the client does not follow, such as `304` or a redirect with no `Location`.

With no match, the failure behaves as if no boundary were attached: a synchronous transition rolls back with `400`, and durable work follows its retries and incident handling. **A host with matchers and no catch-all therefore does not catch timeouts, network failures, or unmatched statuses.**

Only the selected boundary's `errorVariable` and continuation are committed. An error boundary takes precedence over a conditional boundary on the same host, even one whose condition reads `statusVariable`: the host's conditional boundaries are cancelled before anything observes the failure's status or error variable. A conditional catch on another token still sees them. A failure with no match rolls its status write back with the attempt, so it triggers no condition. See the [HTTP status routing example](../examples/service-tasks/04-rest-http-status-routing.json) and the [durable example](../examples/durable-jobs/04-rest-status-routing-and-retries.json).

**Retries are per host.** With an async host, [`job.failureHandling`](#durable-job-policy) applies to every status:

- With the default `boundaryFirst`, a matching failure routes on its first occurrence. Attaching a `5xx` boundary therefore stops automatic retries for those statuses. Leave `5xx` unmatched to retry it, which ends in an incident when retries run out.
- With `retryFirst`, every failure is retried first, so a `409` is sent again for each configured retry before it routes.
- One host cannot route `409` immediately, retry `503`, and then route the exhausted `503`.

### timerBoundaryEvent

Attaches a deadline or reminder to an active host. An interrupting timer cancels that host's active work and takes the boundary route. A non-interrupting timer creates a sibling token while the host remains active; a recurring timer can create repeated reminders.

**Topology:** no incoming flows; exactly one unconditional outgoing flow. **Worker:** required.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `attachedToRef` | integer | Required | Existing eligible host ID; see [boundary host rules](#boundary-host-rules). |
| `cancelActivity` | Boolean | Optional; `true` | `true` interrupts the host; `false` creates sibling work without completing the host. |
| `timer` | [timer configuration](#timer-configuration) | Required | Exactly one schedule, activated with the host. `timeDate` and `timeDuration` may be [expressions](#expression-schedules). |

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

### signalBoundaryEvent

Listens for a named signal while its host is active. A signal thrown through `POST /api/signal-broadcasts`, or by a [signal throw](#intermediatesignalthrowevent) or [signal end event](#signalendevent), reaches every signal boundary with its name whose host is active in the chosen scope when it is thrown, as it reaches waiting [signal catches](#intermediatesignalcatchevent). An interrupting boundary ends the host and takes the boundary route; a non-interrupting one starts a new branch for each signal while the host carries on. Use it when an announcement must reach work in progress, such as a product recall that stops a shipment, or a price update that adds a step beside it.

**Topology:** no incoming flows; exactly one unconditional outgoing flow. **Worker:** required, and the definition publishes only when durable publication is enabled.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `attachedToRef` | integer | Required | Existing eligible host ID; see [boundary host rules](#boundary-host-rules). |
| `cancelActivity` | Boolean | Optional; `true` | `true` interrupts the host when a signal is delivered; `false` starts a new branch for each signal and keeps the host and the boundary active. |
| `signal` | [signal configuration](#signal-configuration) | Required | The literal signal name and optional typed payload mappings. |

```json
{
  "id": 4,
  "name": "Price updated",
  "type": "signalBoundaryEvent",
  "attachedToRef": 2,
  "cancelActivity": false,
  "signal": {
    "name": "warehouse.price-update",
    "outputMappings": [
      { "variable": "unitPrice", "path": "unitPrice", "dataType": "number", "isArray": false, "required": true }
    ]
  }
}
```

The example assumes node `2` is an eligible host and `unitPrice` is undeclared or a declared scalar `number` instance variable.

- **When it listens.** When the host's token enters the host, it subscribes once for each signal boundary attached there, in the same transaction and before an `asyncBefore` job runs. Every way the host ends cancels those subscriptions: its normal exit, including the start of an `asyncAfter` wait, another boundary interrupting it, a caught failure, or the cancellation of the instance or the token's branch. A loop that enters the host again subscribes again for the new visit. A delivery that arrives after the host has ended is skipped.
- **Interrupting** (`cancelActivity` missing or `true`). The delivery ends the subscription and cancels the host's work as an interrupting conditional boundary does: open user tasks and multi-instance work items, an automatic body, the host's other timer, conditional, and signal subscriptions, its jobs, and its calls. The host's visit is cancelled with reason `signalReceived`, and the token moves to the boundary. The mapped values are written on the boundary's visit, conditional events observe them, and the token continues through the boundary's flow. The delivery's result code is `interrupted`.
- **Non-interrupting** (`cancelActivity: false`). The host and the subscription stay active, and the subscription's occurrence count increases. The delivery adds a token at the boundary, in the host's gateway branch, writes the mapped values on that token's visit, lets conditional events observe them, and continues it through the boundary's flow; the result code is `branched`. Each later signal adds another branch. A delivery that would raise the instance's active tokens beyond `Workflow.Gateway.MaxActiveTokens` (default 1,000) fails with `signal_token_limit`, adds nothing, and leaves the boundary listening.
- **Records.** History records a `signalReceived` row from the boundary to the next node; its payload has `eventKind` `boundary`, `cancelActivity`, and the host's token as `hostTokenId`. The boundary's visit completes with reason `signalReceived`, and the outgoing flow's traversal evidence has kind `signalBoundary`. The delivery runs as the fixed system actor `signal`, and routing after the boundary runs in the Worker.
- **Names.** Two signal boundaries on one host cannot use the same signal name, and a boundary on a signal catch cannot use the name the catch waits for. Boundaries on different hosts may share a name; one broadcast then reaches each of them whose host is active.
- **Failures.** A payload that fails a mapping fails only that delivery with `signal_payload_invalid`, writes nothing, and leaves the host active and the boundary listening.
- **Version changes.** While a token is at the host, a target version that adds, removes, or changes a signal boundary on it (its ID, type, name, interrupting flag, or outgoing flow) blocks the change with `attached_signal_boundary_contract_changed`. Changing only the mappings gives the `signal_mapping_contract_changed` warning, unless the host has an open job, which then blocks with `open_job_contract_changed` because a job's contract includes its host's boundaries. See [signals and version changes](developer-guide.md#signals-and-version-changes).
- **Other settings.** Only the [common node properties](#common-node-properties), `attachedToRef`, `cancelActivity`, and `signal` are accepted. Task, role, variable, timer, conditional, message, async, job, and multi-instance settings are rejected.

See [signals](bpmn-support.md#signals), [broadcasting signals](developer-guide.md#broadcast-signals), and the [signal boundaries example](../examples/signals/02-signal-boundaries-recall.json).

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

The continuation cannot target the same interrupt, an entry event, a boundary event, or a Parallel/Complex merge directly. Cancellation covers nested gateway scopes, tasks/MI items, message/timer waits, conditional subscriptions, and durable jobs in the interrupted activation. Strict BPMN normally models this scope with an interruptible subprocess. See the [scoped-interrupt example](../examples/gateways/06-scoped-interrupt-parallel-branch.json) and [nested-scope walkthrough](../examples/gateways/README.md#nested-scope-cancellation).

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

Compare both end types in the [terminate-with-live-siblings walkthrough](../examples/gateways/README.md#normal-end-versus-terminate-with-live-siblings).

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

### signalEndEvent

Completes its token as an [endEvent](#endevent) does and throws a signal as it ends. The broadcast is the one an [intermediateSignalThrowEvent](#intermediatesignalthrowevent) creates, with the same `signal.name`, [`scope`](#signal-scope), and [`payload`](#signal-payload). Other branches keep running, and the instance completes after its remaining work finishes. Use it when the end of a route is the announcement, such as a recall decision that stops the shipment of one order in another workflow.

**Topology:** at least one incoming flow; no outgoing flows, as for every end event. **Worker:** required for the deliveries, and the definition publishes only when durable publication is enabled.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `signal` | [signal configuration](#signal-configuration) | Required | The literal signal name, the required `scope`, and an optional `payload`. `outputMappings` must be missing or empty. |

```json
{
  "id": 3,
  "name": "Recall announced",
  "type": "signalEndEvent",
  "signal": {
    "name": "warehouse.product-recall",
    "scope": {
      "kind": "businessKey",
      "workflowKey": "example-signal-boundaries-recall",
      "businessKeyVariable": "orderNumber"
    },
    "payload": [ { "name": "reason", "variable": "recallReason" } ]
  }
}
```

The example assumes the workflow declares `orderNumber` as a single `string` and declares or produces `recallReason`.

- **When it throws.** When its token enters it, in the step that moves the token there: after the token's update and before the instance's completion is decided, so the instance is still running when its other waits are frozen. That is earlier than an intermediate throw, which takes a routing step of its own. An end event that a fork's flow leads straight into therefore throws in the fork's own step, before any other branch of that fork has reached its wait, and reaches none of them whatever the order of the flows; see [which waits receive it](#which-waits-a-thrown-signal-reaches).
- **Records.** History records the row that moves the token onto the end event, then an additional row from the end event to itself with note `signalThrown` and the payload described for the [throw event](#intermediatesignalthrowevent), with `eventKind` `end`. The visit completes with reason `normalEnd`. An instance that ends here is completed and reported as one that ends at a plain end event; a call activity's child that ends here returns its result to the parent as usual.
- **Refusals, versions, and other settings.** As for the [throw event](#intermediatesignalthrowevent): a refused throw fails the step that moved the token there ([refused throws](#refused-signal-throws)), a version change adds no blocker, and only the common node properties and `signal` are accepted.

See [signals](bpmn-support.md#signals), [throwing a signal from a workflow](developer-guide.md#throw-a-signal-from-a-workflow), and the [recall decision example](../examples/signals/04-signal-end-recall-decision.json).

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

Input keys are trimmed, nonblank, case-sensitive, and limited to 300 Unicode scalar values. The reservation is workflow-family scoped, across versions and external entry points. Duplicate business identity returns a conflict. Enabling a family's policy also constrains starts through older unkeyed versions; see [business identity and retries](developer-guide.md#separate-retries-from-business-identity). A signal throw can be limited to the family's instance with one business key ([signal scope](developer-guide.md#limit-a-signal-to-one-case)); instances started before the family enabled business keys have none and are never reached that way.

## Start idempotency configuration

Node-level `idempotency` is optional on manual/message starts. It claims a transport request key permanently across the stable workflow family after a successful commit, including after the instance becomes terminal. A duplicate returns `409` with the existing instance reference; it is not a replayed success response.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `headerName` | string | Optional; `Idempotency-Key` | Fixed valid HTTP field name, at most 300 characters. It cannot use the reserved names below or collide with the message-start correlation header. Explicit blank/null is invalid. |
| `variable` | string | Required | Name of the implicit required scalar-string variable populated only from that header. Follow [variable naming rules](#variables-and-inputs). It must not duplicate an entry variable/message mapping or the business-key variable; the request body must not submit it. |

The key is trimmed, nonblank, case-sensitive, and bounded to 300 Unicode scalar values. For the standard header, the legacy `X-Idempotency-Key` alias is accepted; conflicting values reject the request. Arbitrary headers on unconfigured starts do not enable this behavior.

Both start-idempotency and catch-delivery header names reject these reserved names case-insensitively: `Authorization`, `Proxy-Authorization`, `Cookie`, `Host`, `Content-Length`, `Content-Type`, `Content-Encoding`, `Transfer-Encoding`, `Connection`, `Keep-Alive`, `TE`, `Trailer`, `Upgrade`, `Expect`, `X-Client-Id`, and `X-Client-Secret`.

## Timer configuration

The required `timer` object belongs only to `timerStartEvent`, `intermediateTimerCatchEvent`, and `timerBoundaryEvent`. Set **exactly one** nonblank property, its *schedule value*: `timeDate`, `timeDuration`, or `timeCycle`, or, on a timer start only, `cron`. Every schedule value is limited to 128 UTF-16 code units after trimming. On a timer catch or boundary, `timeDate` and `timeDuration` may be fixed values or [NCalc expressions](#expression-schedules).

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `timeDate` | ISO-8601 timestamp string or expression | One schedule value required | Absolute date/time with `Z` or an explicit numeric UTC offset, for example `2030-01-01T09:00:00Z`. Normalized to UTC. A local timestamp without offset is invalid. |
| `timeDuration` | ISO-8601 duration string or expression | One schedule value required | Positive fixed-unit delay, for example `PT30S`, `P2D`, or `P1W`. Supports weeks or day/hour/minute/second components and decimal components, within the runtime duration range. |
| `timeCycle` | recurrence string | One schedule value required | `R/Duration` for unbounded authored recurrence, or `R<n>/Duration` with a positive 32-bit integer occurrence count. For example `R5/PT30S`. The interval must be at least one second. Always a fixed value. |
| `cron` | five-field cron string | One schedule value required; timer start only | A Flowbit extension: a [cron schedule](#cron-schedules) such as `0 8 * * 1-5`, in `timeZone`. Always a fixed value. A timer catch or boundary that names `cron` is invalid. |
| `timeZone` | IANA time-zone id | Required with `cron`; otherwise absent | The zone a cron schedule runs in, such as `Europe/London`, written with its exact casing; `Etc/UTC` means UTC. At most 100 characters after trimming. There is no default zone. Allowed only beside `cron`. |
| `calendar` | string | Optional; absent | Name of a [business calendar](#working-time-durations) whose working time a `timeDuration` counts, such as `"examples.weekdays"`. Only allowed with `timeDuration`, which must then use whole days and no weeks and be at most 36,525 days long, so it is rejected with `cron` even when the calendar uses the same zone. The calendar must exist, written with its exact casing, and is never an expression. |

Calendar month/year durations are unsupported, and a `timeCycle`, a `cron` schedule, or a timer start cannot use an expression. Relative waits are measured from activation; absolute dates do not move with activation. Timer jobs persist across restarts. Firing can be late when the Worker is unavailable; recurring occurrences more than one minute late skip to a later eligible occurrence. This is a durable schedule, not a hard real-time guarantee. See [timer execution semantics](bpmn-support.md#timers).

```json
{
  "id": 31,
  "name": "Escalate after three working days",
  "type": "timerBoundaryEvent",
  "attachedToRef": 11,
  "timer": { "timeDuration": "P3D", "calendar": "examples.weekdays" }
}
```

A timer that names a calendar computes its due time once, when it activates, and saves it; the timer subscription also records the calendar name. If the calendar no longer exists, the server does not know its time zone, or the calendar cannot count the duration, a timer catch or boundary fails its activation like other timer validation: the request fails, or a Worker job retries and then opens an incident. A timer start in that state is not scheduled. Its subscription stays paused with a `timer_schedule_failed` incident, and the Worker logs the reason once. Retrying the incident after fixing the cause schedules the timer start again, or opens a new incident; a version that replaces the default resolves it. A timer's calendar belongs to its schedule contract, so changing it blocks an in-place version change of an open timer, as changing its expression does.

### Expression schedules

On an `intermediateTimerCatchEvent` or `timerBoundaryEvent`, a `timeDate` or `timeDuration` that is not a fixed value is an NCalc expression, optionally wrapped in one whole-value `${...}`. The engine evaluates it once, when the timer activates (when a token reaches the catch, or when the boundary's host activates), and saves the resulting UTC due time.

```json
{
  "id": 4,
  "name": "Review deadline",
  "type": "timerBoundaryEvent",
  "attachedToRef": 2,
  "timer": { "timeDuration": "if(urgent, 'PT4H', 'P2D')", "calendar": "examples.weekdays" }
}
```

- **Inputs.** An expression reads only declared, persisted instance variables: process variables, start, node, and action inputs, and service/message output, status, error, and idempotency targets. Shared aliases, `sys.*`, `config.*`, `setting.*`, `mi.*`, `gateway.*`, `FlowInfo`, and gateway and multi-instance helpers are rejected; copy such a value into an instance variable before the timer activates. NCalc's built-in functions and Flowbit's string helpers are available; there are no date functions. NCalc resolves each built-in only in its own spelling, `Max`, `Round`, and the other maths functions capitalized and `if`, `in`, and `ifs` in lowercase, so saving rejects another casing such as `If` or `max` and names the one to use; the string helpers accept any casing.
- **Result.** The expression must give text that is a fixed value for its property: a date-time with `Z` or a `±hh:mm` offset for `timeDate`, or a positive fixed-unit duration for `timeDuration`, which with a `calendar` must also be countable (whole days, no weeks, at most 36,525 days). A number, Boolean, list, object, no value, or blank text fails.
- **`datetime` inputs need an explicit offset.** The `datetime` variable type also accepts text the timer grammar rejects: no offset (`2026-12-01T09:30:00`, `2026-12-01T09:30`), more than seven fractional digits, or an hour-only offset such as `+02`. Such a value fails at activation. Flowbit.Ui's date-time controls always send an offset, but HTTP callers, mapped outputs, and scripts may not, so give the input a `validation` rule that requires one, as the [contract end example](../examples/timers/07-contract-end-wait.json) does.
- **Building text.** Two strings join, so `endDate + 'T17:00:00Z'` turns a `date` input into a valid `timeDate`. A string and a number do not (`'P' + days + 'D'` fails), so build a duration from a number in a JavaScript script task first, as the [working-day SLA example](../examples/timers/09-working-day-sla-from-number.json) does.
- **`${...}` is a wrapper, not a template.** Unlike service-task URLs and variable defaults, a schedule never inserts values into text; `P${days}D` is rejected.
- **Text that looks fixed stays fixed.** Text evidently meant as a fixed value is never read as an expression: `P1Y`, `p2d`, `PT0S`, `-P1D`, `2026-11-01`, an offset-free timestamp, recurrence text such as `R/P1D`, and a valid value for the other property (a duration in `timeDate`) are all rejected. Reference a variable whose name looks like a value in brackets, such as `[PT1H]`. A quoted literal, such as `'PT30S'`, must itself be a valid value.
- **Validation.** Saving through the API checks the expression's syntax, functions, references, and context, and evaluates an expression that reads no variables. It also rejects a bracketed name with spaces inside the brackets, such as `[ slaDuration ]`, because NCalc keeps the spaces in the name and the expression could never read the value. The editor checks fixed forms, length, shared and bracketed context references, such padded names, the casing of built-in functions, and calendar combinations; only the API checks NCalc syntax and variable names.
- **Activation and failures.** The expression reads the instance's stored values at activation, including values written earlier in the same transition, but not the output of the activity a boundary guards. A variable with no value, an unusable result, or a calendar that cannot count the result fails the activation: the request returns `400` and changes nothing, or a Worker job retries and then opens an incident. The message names the node, property, and cause but never a value, for example `Timer boundary event #4 timer.timeDuration cannot be scheduled: variable 'slaDuration' has no value.` Each failure also writes one warning log without values.
- **The saved schedule stands.** The timer subscription keeps the authored text and the resolved due time. Changing a variable or calendar later, restarting the Worker, retrying a fired timer, or a compatible version change never recomputes it; a host that is entered again gets a new activation and a new evaluation.
- **Already-due timers.** A date at or before activation is saved as is and is due at once, but the host still starts. An interrupting boundary on a user task creates the work item and interrupts it when the Worker runs. A staged service or script task commits its boundaries before its body runs, so a service task's HTTP call can run while the timer interrupts the task; the late result is discarded, but the external effect has happened.
- **Version changes.** An open expression timer is compatible only with the same authored text, wrapper included: `slaDuration` and `${slaDuration}` differ, and a changed expression blocks the change even if it would give the same due time.

The Flowbit.Ui inspector offers a **Fixed value** or **Expression** choice for these properties. See [correcting a schedule](developer-guide.md#expression-timers) for recovery and the [example catalog](../examples/README.md#durable-jobs-and-timers) for runnable workflows.

### Cron schedules

A `timerStartEvent` may set `cron` and `timeZone` instead of an ISO schedule. Cron is a Flowbit extension, not BPMN serialization, and is not available on timer catches or boundaries.

```json
{
  "id": 1,
  "name": "Month-end close",
  "type": "timerStartEvent",
  "timer": { "cron": "0 17 L * *", "timeZone": "America/New_York" }
}
```

**Dialect.** The five fields are **minute, hour, day of month, month, and day of week**, separated by one or more spaces or tabs.

| Part | Accepted |
| --- | --- |
| Numbers | One or two ASCII digits for every value, range end, and step: `05` means 5, and `005` is rejected. |
| Ranges of values | Minute `0-59`, hour `0-23`, day of month `1-31`, month `1-12`, day of week `0-6`, where `0` is Sunday and `1` is Monday. `7` is rejected, so Sunday has one spelling. |
| A field | `*`, `*/n`, or a comma-separated list of `a`, `a-b`, `a/n`, and `a-b/n` items, with ascending ranges. `a/n` runs from `a` to the field's largest value. Day of week takes no open-ended `a/n`; write `*/n` or a range such as `1-5/2`. |
| Steps | A positive number up to 59, 23, 31, 12, or 6 for the five fields. A step selects calendar-field values; it is not an elapsed interval. |
| Last day | `L` alone as the whole day-of-month field: the month's last calendar day. No `L` lists, offsets, or weekday forms. |
| Rejected | Aliases such as `@daily`, month and weekday names, seconds and year fields, reversed ranges, `?`, `W`, `#`, `H`, other separators, signs, decimals, other scripts' digits, and NCalc or `${...}` templates. An expression whose fields match no calendar date, such as `0 8 30 2 *`, is rejected too. |

| Example | Runs in `timeZone` |
| --- | --- |
| `0 8 * * 1-5` | Weekdays at 08:00. |
| `*/15 9-17 * * 1-5` | Each quarter hour from 09:00 through 17:45 on weekdays. |
| `0 9 1 * *` | The first day of each month at 09:00. |
| `0 17 L * *` | The last calendar day of each month at 17:00, holidays included. |
| `0 17 L * 5` | 17:00 on the last day of a month, only when that day is a Friday. |
| `0 8 29 2 *` | Leap day at 08:00. |

**Both day fields must match.** When both the day-of-month and the day-of-week fields are restricted, a date must match both: `0 8 13 * 5` runs only on Friday the 13th, and `0 17 L * 5` only in months that end on a Friday, not on each month's last Friday. Crontab instead runs when either field matches, so a pasted crontab line that restricts both fields changes meaning; the inspector notes the rule when it applies. Flowbit does not claim compatibility with every Unix or Quartz cron string.

**Daylight saving.** Local times follow `timeZone` as its offset changes:

- A local time the clock skips runs when the clock jumps forward: in `Europe/London`, `30 1 * * *` runs at 02:00 `+01:00` (01:00 UTC) on 29 March 2026. Occurrences that fall in the same skipped hour become one.
- In a repeated hour, an expression whose minute and hour fields are fixed values or lists runs once, at the first: `30 1 * * *` runs at 01:30 `+01:00` (00:30 UTC) on 25 October 2026 and not again at 01:30 `+00:00`. A wildcard, range, or step in the minute or hour field runs in both offsets, so `*/30 * * * *` runs at 01:00 and 01:30 `+01:00` and again at 01:00 and 01:30 `+00:00`. A range and an equivalent list, `0 1-2 * * *` and `0 1,2 * * *`, therefore differ that night; Flowbit keeps the expression exactly as authored.

**Scheduling.**

- The schedule belongs to the published default version and its activation. The first run is the first occurrence strictly after the version became the default; an occurrence at that exact instant is excluded. A first occurrence found more than one minute late, for example because the Worker was stopped, is skipped, and the schedule moves to its first occurrence after the Worker's current time.
- Each run starts a new instance even when earlier instances are still open; there is no overlap lock.
- After a run, the next occurrence is the first one after the run's scheduled time that is at most one minute in the past. An occurrence more than one minute late starts nothing, and the schedule moves to its first occurrence after the Worker's current time; exactly one minute late still runs. A downtime backlog is never replayed.
- The scheduled time is computed once, in UTC, and saved. Restarts, retries, reconciliation, and later time-zone data updates never move a saved time; the next occurrence uses the Worker's time-zone data when it is calculated.
- A cron schedule ends only when another version becomes the default or the definition is unpublished or deleted. The database refuses to complete one.
- If the Worker cannot resolve the zone or calculate an occurrence within 36,525 days, the timer start waits in a `timer_schedule_failed` incident, or the firing rolls back and retries; see [deployment](deployment.md#cron-timer-starts).

**Started instances.** A cron-started instance runs as the system actor `timer`. `sys.now` and `sys.today` are UTC, not the schedule's time zone, so a summer 00:30 run in `Europe/London`, or any 08:00 run in `Asia/Tokyo`, sees the previous date in `sys.today`. The instance does not receive its scheduled occurrence; do not derive a business date from `sys.today`.

**Validation.** Saving through the API checks the dialect, that some calendar date matches, that `timeZone` is present, and that the API's host knows the exact IANA zone; it reports the node and `timer.cron` or `timer.timeZone` with the cause, such as `Timer start event #1 timer.cron day-of-week value 7 is not allowed; write Sunday as 0.` The editor applies the same dialect rules with the same messages in both Flowbit.Ui and the standalone file, but checks only that a zone is named: browsers name some zones differently, so only the server can certify one. In Flowbit.Ui the inspector also [previews the next five runs](ui-guide.md#cron-schedules); the standalone editor cannot.

## Working-time durations

A business calendar defines working time: up to ten working periods for each weekday, holidays, and an IANA time zone. Settings administrators manage calendars on the Flowbit.Ui **Business calendars** page and through the [business calendars API](api-guide.md#business-calendars). A timer `timeDuration` that names a calendar in `calendar`, and a user-task `dueDate` duration that names one in `dueDateCalendar`, count that calendar's working time instead of wall-clock time:

- **Days** (`D`) count working days and keep the time of day. A working day is a date with working time. A start outside working time first moves to the next working time. If that time of day is not working time on the last day, or is exactly the start of a period, the due time is the end of the last period before it, or the end of the previous working day.
- **Hours, minutes, and seconds** count only working time. The due time is the moment the last of that time elapses, so it can be the end of a period.
- **Both**, such as `P1DT4H`, apply the days first and then the time.
- **Weeks and fractional days** are rejected with a calendar, because their working length is ambiguous; write `P5D` or `P1DT4H` instead. Months and years are unsupported everywhere.
- **Length.** A duration can be at most 36,525 days long, about 100 years, counting its days and its time together, because the calculation searches no further. Validation rejects a longer one.

For a calendar with working hours Monday to Friday, 09:00 to 17:00:

| Start | Duration | Due | Why |
| --- | --- | --- | --- |
| Mon 14:00 | `P3D` | Thu 14:00 | Three working days later, at the same time. |
| Fri 14:00 | `P3D` | Wed 14:00 | Saturday and Sunday are not working days. |
| Sat 10:00 | `P1D` | Mon 17:00 | The start counts from Mon 09:00, and one working day ends at Mon 17:00. |
| Mon 08:00 | `P3D` | Wed 17:00 | Counts from Mon 09:00: Monday, Tuesday, and Wednesday. |
| Mon 16:00 | `PT2H` | Tue 10:00 | One working hour on Monday and one on Tuesday. |
| Thu 15:00 | `P1D` | Fri 13:00 | Friday is a holiday with hours 09:00 to 13:00, which end before 15:00. |
| Mon 14:00 | `P3D` | Fri 14:00 | Wednesday is a holiday without hours. |

Working periods are wall-clock times in the calendar's time zone, from a start up to an end; an end of `24:00` is midnight. A holiday's own hours replace the weekday's; a holiday without hours is closed. Across a daylight-saving change:

- A period boundary at a local time that the clock skips falls at the moment the clock jumps, and a boundary at a repeated local time falls at its first occurrence. A period therefore lasts the real time between its boundaries, so one that contains a skipped hour is an hour shorter.
- Days keep their wall-clock time of day, so `P1D` from Saturday 12:00 is Sunday 12:00 in a calendar that works every hour.

The calculation searches up to 36,525 days, about 100 years, after the start; a calendar without enough working time in that window gives no due time. Each API and Worker host uses its own time-zone data, so keep them on the same operating-system image. The due time is computed once and saved: editing a calendar never moves a saved due time, and timers and work items that start later use the calendar as it is then. A calendar that a published definition, a running instance, or an open job uses cannot be deleted, and a definition can be saved and published only when its calendars exist.

## Conditional configuration

The required `conditional` object belongs only to `intermediateConditionalCatchEvent` and `conditionalBoundaryEvent`.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `condition` | NCalc expression string | Required | Nonblank after trimming/optional legacy `${...}` wrapper removal; at most 4,000 Unicode scalar values. Must reference 1–64 distinct declared, persisted instance variables. Unknown variables/functions, shared aliases, `sys.*`, `config.*`, `setting.*`, `mi.*`, `gateway.*`, `FlowInfo`, and gateway/MI helper functions are rejected. Write a bracketed name without spaces inside the brackets: saving rejects `[ approved ]`, because NCalc keeps the spaces in the name and the condition could never see the value. Saving also rejects a built-in function in a casing NCalc does not resolve, such as `If` for `if` or `max` for `Max`. |
| `deliveryMode` | string enum | Optional; `atomic` | `atomic` evaluates and advances in the writer's transaction. `durableAsync` latches a true occurrence and creates a persisted Worker job in that transaction; later false values cannot retract that captured occurrence. Omitted/null/blank means `atomic`; recognized casing is canonicalized and unknown values are invalid. |

Conditions support NCalc built-ins plus the pure string helpers `Length`, `Len`, `IsNullOrEmpty`, `IsNullOrWhiteSpace`, `Contains`, `StartsWith`, `EndsWith`, `Lower`, `Upper`, `Trim`, and `IsMatch`. NCalc resolves each built-in only in its own spelling: the maths functions capitalized, such as `Max`, `Round`, and `Abs`, and `if`, `in`, and `ifs` in lowercase. The string helpers accept any casing. A condition saved before this check with another casing keeps running, but never becomes true. The engine extracts dependencies from the parsed expression and reevaluates affected waits against the complete post-write-batch current-value snapshot. This includes supported variable producers such as user actions, messages, scripts/service outputs, and administrative variable updates. Shared aliases are rejected in both delivery modes. See the [conditional runtime reference](../Flowbit/README.md#intermediate-conditional-catch-events) for persistence and version-switch restrictions.

## Signal configuration

The required `signal` object belongs only to the four signal events. On `intermediateSignalCatchEvent` and `signalBoundaryEvent` it names the signal the event waits for and maps its payload; on `intermediateSignalThrowEvent` and `signalEndEvent` it names the signal the event throws, its scope, and its payload.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `name` | string | Required | Literal signal name: 1 to 100 lowercase ASCII letters, digits, `.`, `_`, or `-`, starting with a letter or digit (`^[a-z0-9][a-z0-9._-]{0,99}$`). It is not a template and is not trimmed; throws match it exactly. An uppercase letter is rejected. Prefer a dotted prefix, such as `claims.policy-changed`, so that unrelated workflows do not share a name by accident. |
| `outputMappings` | [output-mapping array](#output-mappings) | Optional; `[]` | Catch and boundary only: at most 100 typed mappings, in the shape message mappings use. `path` reads the broadcast's JSON payload. `dataType` and `isArray` are required: signals have no untyped legacy form. A throw or end event must leave it missing or empty. |
| `scope` | [scope object](#signal-scope) | Required on a throw or end event | Which waits the thrown signal reaches. Rejected on a catch or boundary. |
| `payload` | array of [payload entries](#signal-payload) | Optional on a throw or end event | At most 100 entries, each one member of the payload object. A missing, null, or empty list sends no payload. Rejected on a catch or boundary. |

A mapping writes an instance variable: a declared instance-scoped process variable, whose type, array shape, nullability, and validation then apply, or a new instance variable that the mapping declares. It cannot write a shared-variable alias, and neither its `defaultValue` template, its `validation`, nor the validation of a targeted process variable may read one, because one broadcast would otherwise touch the same shared key from every recipient. Reserved `sys.`, `config.`, `setting.`, and `mi.` names are rejected as for other mappings.

A broadcast without a payload leaves every path missing, so each mapping's default or required check applies. All mappings resolve and validate before anything is written; a failure fails that delivery with `signal_payload_invalid` and leaves the catch waiting, or the boundary listening.

Validation messages start with `Signal catch event #<id>`, `Signal boundary event #<id>`, `Signal throw event #<id>`, or `Signal end event #<id>`, for example `Signal catch event #9 signal.name must be 1 to 100 lowercase letters, digits, '.', '_', or '-', starting with a letter or digit.` A `signal` object on another node type returns `Flow node #<id> defines signal but is not a signal event.` The boundary name rules return `Signal boundary event #15 uses signal 'warehouse.product-recall', which signal boundary event #14 on host #3 already uses.` and `Signal boundary event #14 uses signal 'claims.policy-changed', the signal its host signal catch event #9 waits for.` A catch or boundary with `scope` or `payload` returns `Signal catch event #9 cannot define signal.scope or signal.payload; only signal throw and end events send a signal.`

### Signal scope

A throw or end event states one of six scope kinds in `signal.scope.kind`, written from the throwing instance's point of view. Every member a kind lists is required, and no other member is allowed, so a forgotten or mistyped member is an error and never chooses other recipients.

| `kind` | Members | Recipients |
| --- | --- | --- |
| `thisInstance` | None | The waits with the name in the throwing instance. Saving requires a signal catch or boundary event with the same name in the definition. |
| `thisWorkflow` | None | The waits with the name in every instance of the throwing instance's workflow family, in every version. |
| `workflow` | `workflowKey` | The waits with the name in every instance of that family. |
| `businessKey` | `workflowKey`, `businessKeyVariable` | The waits with the name in the instance of that family whose [business key](#business-key-configuration) is the variable's value. |
| `ownBusinessKey` | `workflowKey` | The waits with the name in the instance of that family whose business key is the throwing instance's own. |
| `allWorkflows` | None | Every wait with the name, in every family. |

- **`kind`.** Compared exactly; a missing scope, a missing kind, or another value, such as `ThisInstance`, returns `Signal throw event #7 signal.scope.kind must be 'thisInstance', 'thisWorkflow', 'workflow', 'businessKey', 'ownBusinessKey', or 'allWorkflows'.` A member the model does not know, such as a misspelled `businesKeyVariable`, fails binding with its JSON path in the `{"error": ...}` body; the editor names the member before the request is sent.
- **`workflowKey`.** A literal family key: no surrounding white space, at most 300 characters, and no `${...}`, which is not replaced. It may be the definition's own key, but prefer `thisWorkflow` for the own family: a definition copied or imported under another key would otherwise still throw to the family it was copied from. Publishing a version, creating or updating one with `publish: true`, and setting a default require every other family named here to have a definition: `Signal end event #3 throws to workflow 'shipping', which has no definition.` A draft saves without it, so two workflows that throw to each other can both be saved and then published. At run time nothing is looked up: a family without running instances, or one deleted since, simply has no waits.
- **`businessKeyVariable`.** A variable the definition declares or produces as a single `string`, matched ignoring letter case, and not a shared-variable alias. When the event throws, its stored value is trimmed as a start trims a business key; a value that is missing, not text, blank, or longer than 300 characters refuses the throw.
- **`ownBusinessKey`.** The definition's entry events must configure `businessKey`, or saving returns `Signal throw event #7 uses this instance's business key, but this workflow's entry events do not configure businessKey.` An instance without a key, such as one started before business keys were enabled, refuses the throw.
- **No match.** A scope that names a family without enabled business keys, a key that no running instance has, or a family where nothing waits completes the broadcast with no deliveries; its `recipientCount` of 0 appears in the history row and on the broadcast.

### Signal payload

Each entry of `signal.payload` sends one member of the payload object. A receiving catch or boundary reads it through its mappings, with the entry's name as `path`.

| Property | Type | Required / default | Meaning and constraints |
| --- | --- | --- | --- |
| `name` | string | Required | The member name: 1 to 100 letters, digits, `_`, or `-`, starting with a letter or `_` (`^[A-Za-z_][A-Za-z0-9_-]{0,99}$`), unique in the payload ignoring letter case. It has no dot, because a mapping path uses dots for nesting. |
| `variable` | string | Exactly one of `variable` and `source` | A variable the definition declares or produces, of any type, that is not a shared-variable alias. Its stored JSON value is copied whole. |
| `source` | string | Exactly one of `variable` and `source` | `instanceId`, the throwing instance's ID as a JSON number, or `businessKey`, its business key as text. `businessKey` needs the definition's entry events to configure `businessKey`. |
| `required` | Boolean | Optional; `false` | When true, a variable with no value or a stored JSON null, or an instance without a business key, refuses the throw. An optional entry without a value is left out, and an optional variable holding null sends null. |

A member of an entry that the model does not know fails binding, as in the scope. Values are read from the stored instance variables once, after the step's earlier writes, never from a shared variable, so a throw adds no shared key to the [lock-order proof](../Flowbit/README.md#shared-variables). An empty list sends no payload, and a list whose optional entries all have no value sends `{}`. The payload object is limited to 256 KiB, measured as PostgreSQL stores it.

**Who can read a payload.** The broadcast stores the payload, and the [broadcast routes](api-guide.md#signals) and the **Signals** page return it to every user with a role from `Signals.RequiredRole`, whether or not that user can read the throwing instance; the receiving definitions read it through their mappings. Send only what the receivers need, and never a secret. Finished broadcasts are kept until the `signalBroadcasts` [retention](deployment.md#retention-and-backup) category removes them.

Payload messages include `Signal throw event #7 payload entry 1 name must be 1 to 100 letters, digits, '_', or '-', starting with a letter or '_'.`, `Signal throw event #7 payload sends 'reason' more than once.`, `Signal throw event #7 payload entry 'reason' must define either variable or source.`, and `Signal throw event #7 payload entry 'reason' reads 'cancelReason', which is never declared or produced by this workflow.`

### Which waits a thrown signal reaches

A throw freezes its recipients in one statement, inside the transaction of the step that throws. Other instances' waits count when they are committed and active at that moment. The throwing instance's own waits count as that transaction has left them:

| Wait of the throwing instance | Receives the signal? |
| --- | --- |
| A wait of the host the throwing token just left | No: leaving the host cancelled it earlier in the transaction. The throwing token itself is never a recipient. |
| A wait another token has held since an earlier transaction | Yes. |
| A wait another token reached earlier in the same routing pass | Yes. |
| A wait a token reaches later in the same routing pass | No: it does not exist yet. Do not rely on the order in which parallel branches are routed. |
| A wait on another branch of a fork whose flow leads straight into a signal end event | No, whatever the order of the flows: the end event throws in the fork's own step. An intermediate throw takes a step of its own, so it reaches the branches routed before it. |
| A wait that ends later in the same transaction, for example at a terminate end event | It is frozen, and its delivery is skipped with `subscription_ended`. |

**An automatic task in front of the throw.** Put a `task` with `asyncBefore: true` before a throw or end event when any of these matters. The throw then runs in a Worker job, in a transaction that starts after the earlier step has committed, so:

- every wait that the earlier step created exists and receives the signal;
- a user's action, or another request, commits even when the throw is refused, and the job retries and then opens an incident instead;
- a throw to many recipients holds its locks in the Worker, not in a request.

The [order cancellation example](../examples/signals/03-signal-throw-order-cancellation.json) needs no such task, because its shipment task has waited since an earlier transaction.

### Refused signal throws

A throw that cannot be built, or that a limit stops, raises an error whose message starts with the event's label, and its step fails; nothing is dropped silently, and no transaction commits part of a broadcast. No message contains a business key or a payload value.

| Cause | Message after the label, for example `Signal throw event #9` |
| --- | --- |
| The token's chain of automatic steps and signals reached the [loop guard](developer-guide.md#guard-against-signal-loops) | `was not thrown: its chain of automatic steps and signals reached Workflow.Async.MaxConsecutiveAutomaticActivations (1000).` |
| The business key variable has no value, is not text, is blank, or is longer than 300 characters | `cannot read a business key from variable 'orderNumber': it has no value.`, with the cause that applies |
| The scope `ownBusinessKey`, or a required `businessKey` source, in an instance without a business key | `uses this instance's business key, but this instance has none.` |
| A required payload variable has no value, or holds null | `cannot send payload entry 'reason': variable 'cancelReason' has no value.`, or `... is null.` |
| The payload is larger than 256 KiB | `payload is 300000 bytes; the limit is 262144.` |
| `Signals.MaxOpenWorkflowBroadcasts` broadcasts thrown by workflows are still delivering | `was not thrown: 10000 signal broadcasts thrown by workflows are still delivering; see Signals.MaxOpenWorkflowBroadcasts.` |
| More waits match than `Signals.MaxSubscriptionsPerBroadcast` | `would reach more than 10000 waiting subscriptions; narrow its scope or raise Signals.MaxSubscriptionsPerBroadcast.` |

What fails depends on where the step runs:

| Where the throw runs | Result of a refusal |
| --- | --- |
| A request: a start, a user action, a delivered message, an immediate administrative action, or an administrative variable update whose values wake a conditional event | `400` with the message, and the transaction rolls back. |
| A Worker job's continuation, such as `asyncBefore`, `asyncAfter`, a timer, a durable conditional wake, a call result, or a multi-instance completion | The job fails by its failure policy: retries where it has them, then a `job_execution_failed` incident that names the job's node; the job's failure description names the throw event. Retrying the incident runs the step again. |
| A signal delivery whose continuation reaches the throw | The delivery fails with `signal_delivery_rejected`, and the receiving wait stays; **Retry failed** delivers it again. |
| The start of a call activity's child | The attempt's changes are discarded and the call fails with `call_start_failed`. |
| An item of an administrative action batch | The item's transaction rolls back and the item is `failed` with the message; other items are unaffected. |
| An item of a variable-update batch | The item's transaction rolls back and the item is `skipped` with the message, as for any business failure in that batch; other items are unaffected. |

## Boundary host rules

A `callActivity` hosts only one catch-all `errorBoundaryEvent`; timer and conditional boundaries attach to it only when it is multi-instance, and signal boundaries never. Timer, conditional, and signal boundaries attach to a normal or multi-instance `userTask`, an `intermediateMessageCatchEvent`, an `intermediateTimerCatchEvent`, an `intermediateSignalCatchEvent`, or a `task`/`serviceTask`/`scriptTask` with `asyncBefore: true`. Timer and conditional boundaries also attach to a multi-instance service or script task, which always has `asyncBefore: true`, and to a multi-instance `callActivity`; a signal boundary on any of these [multi-instance bodies](#automatic-multi-instance-properties) is rejected. A conditional catch, gateway, start, end, throw event, or another boundary is not an eligible host. A host may have at most **eight timer, conditional, and signal boundaries combined**, plus its separate error-boundary allowance when it is a service, script, or call activity.

Host problems are reported with the boundary's kind, for example `Signal boundary event #14 host #3 must be a durable wait or an automatic task with asyncBefore enabled.` and `Signal boundary event #14 host #3 is a multi-instance service, script, or call activity, which signal boundaries do not support.` The limit returns `Host node #3 has 9 timer, conditional, and signal boundary events; at most 8 are allowed.` whichever kinds the nine are, and `cancelActivity` on any other node type returns `Flow node #5 defines cancelActivity but is not a timer, conditional, or signal boundary event.`

On a [multi-instance body](#automatic-multi-instance-properties), a boundary belongs to the whole body, never one item. A conditional subscription starts when the token enters the node, even while it waits behind another body of the same node, and can interrupt that waiting entry without touching the other body. A timer starts when the body initializes, not while it is queued. An interrupting occurrence stops unfinished items, cancels their call children, publishes no result, and routes once; writes that items had already committed stay. A non-interrupting occurrence creates a sibling branch while the body keeps running, and an occurrence already captured survives the body's normal completion. Conditions still read only declared, persisted instance variables, never item locals, `mi.*`, or shared aliases.

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
| `job.failureHandling` | string | Optional; `"boundaryFirst"`. | `boundaryFirst` uses a matching error boundary before retries, and retries a failure with no match; `retryFirst` retries first, then considers the error boundary after exhaustion, using the final attempt's result. An unresolved failure becomes an incident. See [HTTP status matching](#http-status-matching). |
| `job.retryDelays` | array of strings | Optional/null; `["PT10S", "PT1M", "PT5M"]`. | Delay before each retry, up to 10 positive fixed-unit ISO-8601 durations, each at most 128 characters. `[]` explicitly disables automatic retries. The initial attempt is additional to the retries. Calendar months/years and nonpositive delays are invalid. |

For multi-instance tasks the async flags apply to the parent execution, not independently to each child. On a [multi-instance service or script task](#automatic-multi-instance-properties), `asyncBefore` is required, the `job` policy also governs each item's own job, and `asyncAfter` runs once after the result is published. Workers must be deployed for jobs to advance. A durable REST call may be repeated after a failure; remote side effects need their own idempotency contract. See [durable execution](bpmn-support.md#synchronous-and-durable-execution).

The [durable-job examples](../examples/durable-jobs/README.md) contrast the two
failure policies and include a local REST retry/incident recovery walkthrough.

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
| `defaultValue` | JSON value | Conditional. | Instance process variables require a default unless nullable; nullable omission initializes null. Optional inputs use their default when omitted. Shared bindings cannot supply defaults. Must resolve to the type/array contract; authored numeric/Boolean strings and supported string templates are coerced. A numeric string must be a finite number: saving refuses text such as `1e9999` or `NaN`, and a template that renders such text stays text and fails the type check. |
| `validation` | string | Optional/null. | NCalc rule parsed at authoring and evaluated against final values. Blank means no rule. Nullable process values skip validation when null; optional input rules still run when the input is omitted. Shared rules use only `value` and supported pure functions and must match the catalog contract. |
| `scope` | string | Top-level only; omitted means `"instance"`. | `instance` stores per-instance data; `shared` binds to a deployment catalog entry. Start/action inputs cannot set scope metadata. |
| `sharedKey` | string | Required for `scope: "shared"`; otherwise omit. | Existing catalog key, maximum 300 Unicode scalar values. Only one local alias for a given catalog key per workflow. |
| `access` | string | Required explicitly for `scope: "shared"`; otherwise omit. | `read` or `readWrite`. Producers need write access; binding contract and deployment permissions must match. |
| `label` | string | Optional; start/action inputs only. | Form text shown instead of `name`. Trimmed; blank means none. Maximum 300 Unicode scalar values. |
| `helpText` | string | Optional; start/action inputs only. | Plain text shown with the form field; line breaks are kept. Trimmed; blank means none. Maximum 1,000 Unicode scalar values. |
| `options` | array | Optional; start/action inputs only. | Fixed choices a form offers, as `{ "value", "label" }` objects; see [form presentation](#form-presentation). Only on a single (non-array) `string` or `number`. An empty list means none. |

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

### Form presentation

Start-event and user-action input declarations may carry `label`, `helpText`, and `options` for the forms that collect them. Like node and flow `attributes`, these properties never affect execution: the engine accepts any value of the declared type whether or not it is an option, and changing only these properties never blocks a workflow-version change. Process variables reject them.

```json
{
  "id": 1,
  "name": "reviewOutcome",
  "dataType": "string",
  "required": true,
  "validation": "reviewOutcome in ('approved', 'rejected')",
  "label": "Review outcome",
  "helpText": "Choose the outcome you agreed with the requester.",
  "options": [
    { "value": "approved", "label": "Approve" },
    { "value": "rejected", "label": "Reject" }
  ]
}
```

Each option needs a `value` of the declaration's type; `label` is optional, at most 300 Unicode scalar values, and forms show the value without it. A declaration may have at most 100 options.

- **String values** must not be blank, must not start or end with whitespace, have at most 300 Unicode scalar values, and are unique ignoring case, because NCalc compares strings case-insensitively.
- **Number values** have at most 15 significant digits and 28 decimal places, so the editor and forms hold them exactly. They must fit a .NET `decimal` and are unique by value, so `1` and `1.0` are duplicates. The editor saves a number in its shortest form, for example `1.50` as `1.5`.
- Option values are literal; `${...}` templates are not resolved.

Flowbit.Ui forms offer only the listed values, but options do not restrict what the engine accepts: an API client can send any value of the declared type. To reject other values, keep a `validation` rule such as the one above. Message starts have no form, so a start event converted to a message start drops these properties.

Start/action defaults can refer to earlier resolved values through `${name}` and available `${sys.*}`, `${config.*}`, or `${setting.*}` context. NCalc uses bracketed dotted names such as `[sys.user]`. Available functions and context depend on the owning property: conditional predicates accept persisted instance variables and reject external context; `FlowInfo` is supported only in the documented routing, MI completion, and script contexts.

Entry input names cannot collide with top-level process variables, including implicit business/idempotency keys. Service and message-catch outputs may produce instance names without a top-level declaration; if a process declaration exists, its contract must match. Script assignment targets, dynamic role sources, and MI collection/result variables must be declared process variables. Refer to each producer's rules instead of assuming all producers share one target policy.

See [input collection](developer-guide.md#collect-and-validate-variables) for null/default handling and [shared variables](../Flowbit/README.md#shared-variables) for catalog authorization and transaction restrictions.

## Output mappings

Service responses, message payloads, and signal payloads use the same JSON mapping fields. For a `messageStartEvent`, mappings also declare the entry variables; for `serviceTask`, `intermediateMessageCatchEvent`, `intermediateSignalCatchEvent`, and `signalBoundaryEvent`, they describe operation-specific writes. They are not the node's `variables` list. On a signal event, `path` reads the broadcast's payload, `dataType` and `isArray` are always required, and the mapping cannot read or write a shared alias; see [signal configuration](#signal-configuration).

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

If an optional path is absent and there is no default, that mapping produces no write and preserves an existing value. Its authored validation still runs against the final context, so optional does not bypass a validation rule. A present wrong-type value fails even when the mapping is optional or has a fallback. All mappings are resolved and validated before writes. A service mapping failure fails the activity and may be caught by an error boundary; a message-catch failure rejects delivery and leaves the wait in place, and a signal-event failure fails that delivery and leaves the wait or the boundary in place. Message-start failure creates no instance.

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
4. Apply the node's topology rules. Entry events have no incoming flow and one unconditional output; boundaries attach by `attachedToRef` and have no incoming sequence flow; ends have incoming flow and no output; a link throw has incoming flow and no output, and its link catch has no incoming flow and one unconditional output; gateway shape determines split versus merge.
5. Save the definition through the [HTTP definition API](api-guide.md). Server validation, deployment settings, shared catalog contracts, and worker publication gates remain authoritative. Editor export alone does not publish it.

Legacy documents may normalize differently from new authoring: old step/action vocabulary migrates to nodes/flows; Exclusive splits with all priorities omitted gain ordered priorities; old selectable MI defaults become a selectable outcome plus a hidden fallback. Write the current vocabulary explicitly. Changing properties on active tasks, waits, gateway scopes, or durable jobs can block in-place version switching; see [version selection and changes](developer-guide.md#select-and-change-workflow-versions).

This reference follows the [shared JSON model](../Flowbit/src/Flowbit.Shared/Models/WorkflowModel.cs), [normalization](../Flowbit/src/Flowbit.Shared/Models/WorkflowModelMigrator.cs), and [definition validation](../Flowbit/src/Flowbit.Service/Services/WorkflowDefinitionValidator.cs), together with the expression analyzers and runtime behavior. For implementation details and limits beyond authoring, use the [runtime reference](../Flowbit/README.md).

[Documentation home](index.md) · [BPMN behavior](bpmn-support.md) · [Developer guide](developer-guide.md)
