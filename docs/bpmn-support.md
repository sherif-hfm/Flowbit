# BPMN support and execution semantics

[Documentation home](index.md) · [First workflow](getting-started.md) · [Developer guide](developer-guide.md) · [API reference](api-guide.md)

Flowbit is a **BPMN-aligned JSON workflow engine**. It uses familiar events, tasks, gateways, and sequence flows, with persisted execution and a browser editor. Its native format is Flowbit JSON; it does not import or export BPMN 2.0 XML or claim complete BPMN conformance.

Use this guide to check whether an existing process can be expressed in Flowbit before choosing it as your execution engine. The [example catalog](../examples/README.md) supplies runnable definitions, actors, inputs, and prerequisites.

For each node's properties, required fields, defaults, nested configuration, and JSON fragments, use the [node and property reference](node-reference.md).

## Contents

- [Supported node types](#supported-node-types)
- [Graph and routing rules](#graph-and-routing-rules)
- [Human work and multi-instance tasks](#human-work-and-multi-instance-tasks)
- [Multi-instance service, script, and call activities](#multi-instance-service-script-and-call-activities)
- [Messages, timers, and conditions](#messages-timers-and-conditions)
- [Synchronous and durable execution](#synchronous-and-durable-execution)
- [Call activities](#call-activities)
- [Variables, expressions, and evidence](#variables-expressions-and-evidence)
- [Flowbit extensions](#flowbit-extensions)
- [Unsupported capabilities](#unsupported-capabilities)
- [Choosing an example](#choosing-an-example)

## Supported node types

The current model supports **24 node types**. “Worker” means the standalone `Flowbit.Worker` process. Every runtime workflow requires the API and PostgreSQL. A “No” below applies to the element itself; a downstream async task, timer, or durable conditional still requires a Worker.

| JSON `type` | Runtime behavior | Worker | Example or detail |
| --- | --- | --- | --- |
| `startEvent` | Authenticated user entry with start roles and typed inputs. A workflow may have several manual starts. | No | [Roles, claim, and bypass](../examples/user-tasks/01-roles-claim-and-bypass.json) |
| `messageStartEvent` | System-only entry through the workflow-family message-start endpoint; authenticates configured client credentials and correlation headers, then maps typed payload fields. | No | [Typed message start](../examples/messages/01-message-start-typed-mapping.json) |
| `timerStartEvent` | Creates instances on a persisted one-time, recurring, or cron schedule belonging to the published default version. | Yes | [Recurring start](../examples/timers/03-recurring-timer-start.json), [Weekday cron start](../examples/timers/10-weekday-morning-start.json) |
| `userTask` | Persists actionable work with roles, assignment, optional claim ownership, and selectable outgoing actions. Supports normal and multi-instance work. | Only with async flags or durable boundaries | [Human work](#human-work-and-multi-instance-tasks) |
| `task` | Automatic pass-through step; no human work item or arbitrary application handler is invoked. | Only with async flags | [Async before and after](../examples/durable-jobs/01-async-before-after.json) |
| `serviceTask` | Invokes the `rest` connector with templated request values, bounded HTTP execution, and typed output mappings. With `multiInstance`, calls it once per collection item. | With async flags; required for shared bindings and multi-instance | [REST examples](../examples/service-tasks/README.md), [multi-instance REST](../examples/multi-instance/08-rest-parallel-notifications.json) |
| `scriptTask` | Runs ordered NCalc assignments or sandboxed JavaScript and persists validated outputs atomically. With `multiInstance`, runs once per collection item. | With async flags; required for multi-instance | [Script examples](../examples/scripts/README.md), [multi-instance scripts](../examples/multi-instance/10-ncalc-script-per-item.json) |
| `callActivity` | Starts a child instance of another family's published default, rests until the child finishes, and copies typed inputs and results. With `multiInstance`, starts one child per collection item. Drawn as a task with a thick border. | Yes | [Call activity examples](../examples/call-activities/README.md) |
| `exclusiveGateway` | Split chooses the first true route by ascending priority, otherwise its required default. Merge forwards each arrival independently unless cancelling-join behavior is enabled. | No | [Exclusive routing](../examples/gateways/01-exclusive-priority-and-default.json) |
| `parallelGateway` | Split creates every branch. Merge waits for one token from every static incoming flow. | No | [Parallel fork and join](../examples/gateways/02-parallel-fork-and-join.json) |
| `inclusiveGateway` | Split creates every true route, otherwise its required default. Merge applies the unpaired enabling rule using graph reachability and active token positions. | No | [Inclusive split and merge](../examples/gateways/03-inclusive-conditional-split-and-merge.json) |
| `complexGateway` | Persists activation counts, phase, and cycle; evaluates `activationCondition` and start/reset outgoing conditions. | No | [Complex phases](../examples/gateways/05-complex-start-reset-cycle.json) |
| `intermediateMessageCatchEvent` | Persists a message wait; valid instance-scoped delivery maps output and advances the waiting token. | No | [Message catch](../examples/messages/02-message-catch-delivery-idempotency.json) |
| `intermediateTimerCatchEvent` | Persists a timer wait and resumes when its scheduled job runs. | Yes | [Timer delay](../examples/timers/02-intermediate-timer-delay.json) |
| `intermediateConditionalCatchEvent` | Waits until its condition over persisted instance variables is true; evaluates on entry and relevant variable-write batches. | Only for `durableAsync` | [Conditional catch](../examples/basics/05-conditional-event.json) |
| `intermediateLinkThrowEvent` | "Go to" connector: a token that enters it continues at the link catch event with the same name, in the same transaction and on the same gateway branch. Several throws may share one catch. | No | [Link events](../examples/basics/11-link-events.json) |
| `intermediateLinkCatchEvent` | Where a link continues: it has no incoming flow and passes the token on through its one outgoing flow. An instance cannot start here. | No | [Link throw and catch](node-reference.md#intermediatelinkcatchevent) |
| `errorBoundaryEvent` | Handles a service, script, or call failure through its attached error path; may capture an error string. Interrupting only. A REST service task may have up to eight, each matching HTTP statuses or ranges, with at most one catch-all; a script task or call has one catch-all. There are no named error codes, and an `errorEndEvent` does not throw into a boundary. | Follows host execution mode | [HTTP status routing](../examples/service-tasks/04-rest-http-status-routing.json), [Script error boundary](../examples/scripts/04-javascript-error-boundary.json) |
| `timerBoundaryEvent` | Interrupts its host on expiry or creates a non-interrupting sibling branch; recurring reminders are supported. | Yes | [Reminder and deadline](../examples/timers/05-user-task-reminder-and-deadline.json), [multi-instance deadline](../examples/multi-instance/12-rest-error-and-deadline-boundaries.json) |
| `conditionalBoundaryEvent` | Observes instance-variable changes while its host is active; interrupts the host or captures repeatable false-to-true occurrences. | For `durableAsync` or an async host | [Conditional boundaries](#conditional-boundaries) |
| `scopedInterruptEvent` | Flowbit extension: interrupts the nearest active activation of a referenced Parallel, Inclusive, or Complex split and continues with the surviving token. | No | [Scoped interrupt](../examples/gateways/06-scoped-interrupt-parallel-branch.json) |
| `endEvent` | Completes its token. The instance completes when its remaining work has finished. | No | [Example catalog](../examples/README.md) |
| `terminateEndEvent` | Completes the instance and atomically cancels other active tokens, tasks, waits, and gateway scopes. | No | [Terminate with live siblings](../examples/gateways/08-terminate-live-siblings.json) |
| `errorEndEvent` | Faults the instance with an authored error code/description and cancels sibling work. | No | [REST failure and error end](../examples/service-tasks/02-rest-error-boundary-and-error-end.json) |

## Graph and routing rules

Definitions use integer authored node and flow IDs. A sequence flow has its own ID and refers to its source and target using `sourceRef` and `targetRef`. IDs must be unique within their respective collections, references must exist, and variable names cannot collide case-insensitively. Lanes organize the diagram; their geometry does not determine permissions or execution scope.

- A workflow needs at least one manual, message, or timer entry. Entries have no incoming flow and exactly one unconditional outgoing flow. `initialEventId`, when present, identifies a manual `startEvent`.
- Automatic tasks, service/script tasks, intermediate catches, and boundaries have one unconditional outgoing flow. End events have at least one incoming flow and no outgoing flows. A boundary is attached through `attachedToRef`, with no incoming sequence flow.
- A link throw event has at least one incoming flow and no outgoing flow; a link catch event has no incoming flow and one unconditional outgoing flow. They pair by `name`, trimmed and then compared exactly, including letter case. Every throw needs a catch of its name and every catch needs at least one throw; link catch names must be unique ignoring letter case. Graph checks such as merge synchronization, scoped-interrupt and cancelling-join reachability, and reactivation safety follow a link as they follow a sequence flow.
- A gateway must be a **split** with one incoming and at least two outgoing flows, or a **merge** with at least two incoming and one outgoing flow. One-in/one-out and many-in/many-out gateways are rejected; use adjacent split and merge nodes when needed.
- Exclusive splits require exactly one default and a condition plus unique positive `conditionPriority` on every other route. Defaults have neither condition nor priority. Exclusive merges have one unconditional output.
- Inclusive splits require exactly one unconditional default and a condition on every other route, without priorities. Inclusive merges have one unconditional output. Synchronization is based on tokens that can still reach the merge, not solely on a paired split ID.
- Parallel outputs are unconditional. A Parallel merge waits for all static inputs even if the model makes one unreachable at runtime; model the split and merge topology accordingly.
- Complex gateways require `activationCondition`. Non-default outputs require conditions; at most one unconditional default is allowed and `conditionPriority` is unsupported. `IncomingCount(flowId)`, `TotalIncomingCount()`, and `[gateway.waitingForStart]` express count and phase behavior.

Several tokens may be active in one instance. A loop may create new visits to a node and new work items; a node ID alone does not identify the current work item. Read [runtime identifiers and integration behavior](developer-guide.md) before building a task client.

## Human work and multi-instance tasks

A normal `userTask` creates one persisted work item for the visiting token. Outgoing flows represent user actions. The task's roles, action roles, assignment, claim requirements, and stored-state conditions determine who may act. An empty literal role list is unrestricted for that role check; it does not bypass the other checks.

`requiresClaim` adds ownership to authorization. Action-specific `canActWithoutClaim` and optional `canActWithoutClaimRoles` can permit an otherwise-authorized actor to bypass the claim. Assignee expressions and claim/assignment inheritance snapshot ownership on entry. Task and action `rolesVariable` values are also captured together on entry; changing the source variable does not change waiting work. Authorized role managers use the dedicated roles API to replace its saved policy. A task's optional `dueDate` and `priority` are likewise resolved once when its work item is created; they order and badge worklists but never route, wait, or escalate.

**Normal user tasks cannot have default flows.** The API returns available actions; a client selects an action ID and supplies that action's inputs. Action conditions are evaluated against stored state before submitted values are applied. `inboxVisibilityCondition` is a separate database predicate that filters personal work before counting and paging; its absence does not remove task or action authorization.

Multi-instance configuration on a `userTask` creates human work items. Service tasks, script tasks, and call activities use a different, automatic contract described in the [next section](#multi-instance-service-script-and-call-activities).

| Capability | Semantics |
| --- | --- |
| `mode: "parallel"` / `"sequential"` | Activates every child together or one child at a time. One parent token owns the execution. |
| `source: "collection"` | On a user task, snapshots a declared `string[]` of usernames and directly assigns children. |
| `source: "cardinality"` | Evaluates a bounded NCalc count; children use task roles and claim behavior. |
| `onePerActor: true` | Cardinality option allowing each username to complete at most one child; inboxes show a representative item and competing selections can return 409. |
| `completionEvaluation` | `afterEach` is the default and can finish early. `afterAll` delays aggregate evaluation until every item completes. Interrupts remain immediate. |
| Outcome conditions | `CountFlow`, `PercentFlow`, and `mi.total/completed/remaining`; lowest `completionPriority` wins among matching outcomes. |
| Engine-only fallback | Exactly one `isDefault: true`, `isSelectable: false` route with no condition or priority. It is used if all items finish without another outcome winning. |
| Interrupting outcome | Cancels unfinished children and advances the parent once. Parent interrupt endpoints can be used by authorized actors without owning an active child. |
| Result evidence | Ordered child records include selected flow, actor, roles, timestamp, and local values; a parent interrupt appends its own final record. |

An aggregate outcome is token routing, whereas each child's action is a vote. Treat them as different events in audit and reporting. See the [multi-instance examples](../examples/multi-instance/README.md) and [waiting-task role policy reference](../Flowbit/README.md#waiting-task-roles).

## Multi-instance service, script, and call activities

A `serviceTask`, `scriptTask`, or `callActivity` with `multiInstance` is an automatic **body**: it runs its activity once for each element of a typed collection and publishes one ordered result collection. This corresponds to a BPMN multi-instance activity with a loop data input and output. One parent token owns the body; its items are durable work, never human work items, inbox entries, or sibling tokens. Each item of a call body starts its own child instance, which has its own tasks and history. See the [node reference](node-reference.md#automatic-multi-instance-properties) for the JSON contract and the [developer guide](developer-guide.md#process-a-collection-with-automatic-multi-instance) for integration.

| Aspect | First-release behavior |
| --- | --- |
| Source | `source: "collection"` only. `collectionVariable` names a declared instance variable or readable shared alias with `isArray: true` and any declared element type; use `json[]` for objects. The body copies the collection once, when it initializes, so later changes do not alter its items or their count. An empty array completes the body at once with `[]`. |
| Modes | `parallel` admits up to the effective concurrency: `maxConcurrency`, or the `Workflow.MultiInstance.DefaultConcurrency` setting when omitted, capped by `Workflow.MultiInstance.MaxConcurrency` and the item count. `sequential` admits one item and starts the next only after the previous one has committed. |
| Item context | Read-only `mi.item`, zero-based `mi.index`, and `mi.total`. Instance inputs are frozen when the body initializes. Other shared values, `config.*` and `setting.*` values, and `sys.now`/`sys.today` are read when each item first runs and kept for that item's retries. |
| Outputs | Item writes go to the body's declared local `outputVariables` or to `readWrite` shared aliases, never to ordinary instance variables. A called child writes its own variables normally; only its mapped results return. |
| Result | Once every item has succeeded, `resultVariable` receives index-ordered rows of `kind`, `index`, `item`, `status`, and the item's local `variables`. Authored `completionEvaluation` must be `afterAll`. A body has exactly one unconditional outgoing flow: no outcome flows, votes, quorum, priorities, or engine-only default. |
| Durability | Service and script bodies require `asyncBefore: true`; an optional `asyncAfter` applies once, after the result is published. A call body is always durable and rejects async flags and `job`. Every body needs a running Worker, even for an empty collection. |
| Failures | REST and script items follow the node's `job` policy and error-boundary precedence; an uncaught item waits in an incident. A call item's start and output failures wait in incidents or take the catch-all boundary, while a child that faults or is cancelled on its own takes the catch-all or faults the parent. A caught failure stops unfinished items and publishes a partial result of `completed`, `failed`, and `cancelled` rows before the error path continues. |
| Limits | `Workflow.MultiInstance.MaxInstances` items (default 1,000), a 1 MiB input snapshot, a 1 MiB result, 1 MiB per item for its frozen inputs and saved result together, and at most 100 local outputs. A result that cannot fit is refused at entry, before any item runs. |

**Whole-body boundaries.** Error, timer, and conditional boundaries attach to the body, never to one item. A REST body keeps up to eight error boundaries with HTTP status matching; a script or call body has one catch-all. Timer and conditional boundaries, up to eight combined, work in both `cancelActivity` modes on all three kinds, including a multi-instance call, although a single call cannot host them. A conditional subscription starts when the token enters the node, even while it waits behind another body of the same node; a timer starts when the body initializes. An interrupting occurrence stops unfinished items, cancels their call children, writes no aggregate, and routes once. A non-interrupting occurrence creates a sibling branch and the body keeps running. Conditional predicates still read only declared, persisted instance variables: not `mi.*`, item locals, or shared aliases, and shared writes do not wake them.

**Shared variables.** The collection, item inputs, item outputs and status, the result, and the error target can be shared aliases, subject to the catalog's access, type, and value-version checks. An item's shared writes commit once with its success, in commit order rather than index order, and are not combined into a reduction. A changed value opens an `output_version_conflict` incident over the saved result; retrying it publishes that saved result over the newer value without repeating the HTTP call, script, or child. See [shared variables](../Flowbit/README.md#shared-variables).

At most one body per node is open in an instance; a second token reaching the node waits until the first body settles. An instance with an open body cannot switch workflow versions.

Not in this release: the `cardinality` source, quorum and early completion, outcome flows and parent interrupts on automatic bodies, per-item boundaries, item skipping, replacement, or selective cancellation, compensation, implicit reductions and direct per-item writes to ordinary instance variables, streaming collections, live changes to a running body's concurrency or inputs, and call version bindings other than the published default. Because items cannot be skipped, an item that fails on every attempt with no applicable error boundary keeps its body waiting until the instance is cancelled or an interrupting timer or conditional boundary fires. See the [multi-instance examples](../examples/multi-instance/README.md) and the [multi-instance call example](../examples/call-activities/04-multi-instance-approval-call.json).

## Messages, timers, and conditions

### Messages

Message starts and catches are HTTP integration points. They use node-configured machine credentials and required headers, independently of user bearer authentication. Message starts select a workflow family and its published default version. Catches address an existing instance and select its matching wait. Neither is a global broker subscription or cross-instance BPMN message-flow implementation.

Output mappings are typed contracts with required fields, defaults, and validations. A failed mapping does not partially commit outputs. Start idempotency, business-key uniqueness, and catch-delivery idempotency are separate contracts; configure them deliberately and inspect actual conflict responses in the [API reference](api-guide.md). See the [message examples](../examples/messages/README.md).

### Timers

Each timer specifies exactly one schedule:

| Property | Example value | Meaning |
| --- | --- | --- |
| `timeDate` | `"2030-01-01T09:00:00Z"` | Absolute timestamp with an explicit UTC offset. |
| `timeDuration` | `"PT30S"` | Positive fixed duration from activation. |
| `timeCycle` | `"R5/PT30S"` | Five occurrences at a 30-second interval; `R/PT30S` has no authored count limit. |
| `cron` with `timeZone` | `"0 8 * * 1-5"`, `"Europe/London"` | Timer starts only, a Flowbit extension: each time a five-field cron expression matches in a named IANA zone, here weekdays at 08:00 London time. |

A cron schedule follows its zone's local time as the UTC offset changes. Both restricted day fields must match, unlike crontab, which runs when either matches. A skipped local time runs when the clock jumps forward; in a repeated hour, fixed or listed times run once, while a wildcard, range, or step in the minute or hour field runs in both offsets. The first run is the first occurrence after the version becomes the default; each run starts a new instance whether or not earlier ones are still open, and the schedule ends only when another version becomes the default or the definition is retired. See [cron schedules](node-reference.md#cron-schedules) for the exact dialect.

On an intermediate timer catch or a timer boundary, `timeDate` and `timeDuration` can also be NCalc expressions over declared, persisted instance variables, such as `"${contractEnd}"` or `"if(urgent, 'PT4H', 'P2D')"`. Each is evaluated once, when the timer activates, and its due time is saved; later variable changes do not move it. See [expression schedules](node-reference.md#expression-schedules). Timer starts and `timeCycle` remain fixed, and expressions cannot read shared variables or `sys.*`, `config.*`, or `setting.*` context.

A `timeDuration` can name a business calendar in `calendar`; it then counts that calendar's working days and working hours in its time zone, and the due time is saved when the timer activates. A timer start whose calendar cannot give a due time opens a `timer_schedule_failed` incident instead of being scheduled. See [working-time durations](node-reference.md#working-time-durations). Calendar months/years, cron on timer catches and boundaries, expression-based timer starts, cycles, and cron schedules, and calendars chosen by an expression are unsupported, and neither a `timeCycle` nor a cron schedule can name a calendar. Recurring intervals must be at least one second. The schedule is persisted, but firing can be late if the Worker is unavailable or busy. Recurring misfires, cron included, more than one minute late skip to a subsequent eligible occurrence without replaying a backlog; a finite timer catch with no remaining occurrence opens an incident rather than inventing a trigger.

Timer starts follow the published default definition's schedule activation. Switching defaults or unpublishing affects those schedules through reconciliation. A normal intermediate catch consumes a trigger and leaves the wait; recurring boundary timers can create several sibling reminders while their host remains active.

### Conditional catches

A conditional definition fragment is:

```json
{
  "conditional": {
    "condition": "approved == true",
    "deliveryMode": "durableAsync"
  }
}
```

The expression must reference 1–64 declared, persisted **instance variables**, within 4,000 Unicode scalar values. Shared aliases and non-observable `sys.*`, `config.*`, `setting.*`, `mi.*`, `gateway.*`, and `FlowInfo` are rejected, and saving rejects a bracketed name with spaces inside the brackets, such as `[ approved ]`, which could never match a variable. Supported pure NCalc functions may be used, in the spelling NCalc resolves (`Max`, but `if`); saving rejects another casing. This is change-driven evaluation of committed variable batches, not polling arbitrary external conditions.

- Missing `deliveryMode` means `atomic`: evaluate and advance within the writer's transaction.
- `durableAsync`: when true, latch the occurrence and persist its job and exact activation fence in that transaction. The Worker later advances it even if another write has made the expression false.

All variable producers use the same coordinator, including administrative variable updates. Restarting an API or Worker does not lose a persisted wait or latched wake. See the [conditional runtime reference](../Flowbit/README.md#intermediate-conditional-catch-events).

### Conditional boundaries

Timer and conditional boundaries attach to a normal/MI user task, message catch, timer catch, automatic `task`/`serviceTask`/`scriptTask` with `asyncBefore: true`, or multi-instance `callActivity`. A conditional catch cannot itself host these boundaries. Each host may have eight timer and conditional boundaries combined; its error boundaries are counted separately.

Missing `cancelActivity` means interrupting. Conditional subscriptions start false, so initial true captures immediately. A non-interrupting boundary creates sibling work and rearms after false; true-to-true writes do nothing, while later false-to-true changes capture independent occurrences even if earlier durable occurrences are still queued.

For simultaneous conditions, non-interrupting boundaries are captured in node-ID order, then the lowest-ID interrupting boundary wins. Successful host outputs are evaluated before normal exit. A service or script failure is not: a matching error boundary cancels the host's conditional boundaries before anything observes the failure's status or error variable, and a failure with no match rolls that write back and retries or opens an incident. Interrupting a running REST call cannot physically stop the remote side effect, but a fenced late result cannot restore cancelled work. See [conditional boundary details](../Flowbit/README.md#conditional-boundary-events).

## Synchronous and durable execution

Without async flags, pass-through nodes execute in the current transition until the instance rests or finishes. Instance mutations are transactional and locked in PostgreSQL. A bounded hop count protects one synchronous routing segment from infinite automatic loops. Synchronous REST can hold that transition open while waiting on the external service; an uncaught failure rolls back the transition.

`task`, `serviceTask`, `scriptTask`, and `userTask` accept `asyncBefore` and `asyncAfter`. On multi-instance work the flags apply to the parent, not each child; a multi-instance service or script task requires `asyncBefore`, and every item is durable work of its own. Async boundaries persist jobs so the API can commit before the Worker continues. Timers and `durableAsync` conditionals always use that queue. REST tasks reading or writing shared-variable bindings must use `asyncBefore: true`.

Durable service/script work uses short staging and finalization transactions, with external HTTP or script execution between them. Lease generations, activation fences, and output-version checks prevent an expired attempt from overwriting newer state. They do **not** make remote HTTP side effects exactly-once. Send a stable downstream idempotency key derived from `sys.jobId`; `sys.jobAttempt` identifies the attempt.

Node `job` policy selects `boundaryFirst` (default) or `retryFirst` failure handling. Omitting `retryDelays` uses the engine's retry schedule of 10 seconds, 1 minute, and 5 minutes; an explicit empty array disables automatic retries. Up to ten positive fixed ISO-8601 delays can be authored. Unresolved failures become inspectable incidents with fenced manual retry. See [deployment and Worker operations](deployment.md) and the [durable runtime reference](../Flowbit/README.md#durable-async-work-and-timers).

Runnable scenarios in the [durable-job guide](../examples/durable-jobs/README.md)
demonstrate each failure policy. The [nested scope](../examples/gateways/README.md#nested-scope-cancellation)
and [receipt/deadline](../examples/messages/README.md#example-03-receipt-versus-timeout)
walkthroughs show cancellation of durable waits while other work continues.

## Call activities

A `callActivity` invokes a reusable workflow; it does not embed a subprocess. The child is a normal instance with its own variables, tokens, tasks, history, and version, and one visit owns at most one child. See the [node reference](node-reference.md#callactivity) for its JSON.

| Aspect | First-release behavior |
| --- | --- |
| Target | The family's published default and its default plain start, chosen when a Worker creates the child and kept for that visit. Latest-published and exact-version bindings are not supported. |
| Data | Whole variables copied through typed mappings: inputs at entry, results when the child ends. Parent and child share no variable scope, and mappings have no expression or path. A single call cannot map shared aliases; a multi-instance call can read readable shared aliases and `mi.item`/`mi.index`/`mi.total`, and write item locals or `readWrite` shared aliases. |
| Identity | Child and parent continuation run as the fixed system actor `callActivity`. No roles, claims, or delegation carry over. |
| Success | A child's normal or terminate end, followed by valid output delivery. |
| Failure | Child fault or independent cancellation takes the one catch-all error boundary, or faults the parent. Failures before the child exists and invalid outputs take the boundary, or open a retryable incident while the parent keeps waiting. |
| Cancellation | Cancelling a parent, or the branch holding a call, settles the call in that transaction and stops new work on its descendants at once. Durable jobs then cancel them, so a descendant may stay `running` until a Worker has run. External effects are not undone. |
| Limits | `WorkflowCallActivities:MaxDepth` bounds nesting exactly; `MaxOpenInvocationsPerRoot` is a soft threshold on open calls in a tree. Each call entry, including one its error boundary catches, counts against the automatic-activation guard. |

With `multiInstance`, a call starts one child per collection item and pins the family's published default once for the whole body; see [multi-instance activities](#multi-instance-service-script-and-call-activities). Not supported on a call: HTTP status matching, authored async flags, and node job policies, and on a single call timer or conditional boundaries. Direct self-calls are rejected. Named child-fault matching, caller identity propagation, nested JSON extraction in mappings, and compensation are also out of scope.

## Variables, expressions, and evidence

Process declarations support scalar `string`, `number`, `boolean`, `date`, `datetime`, and `json`, with the model's supported array forms, defaults, required inputs, and validation. Start inputs and user-action inputs are explicit contracts. Shared bindings reference a deployment catalog and are accessed through its dedicated API; they are not exposed as instance-variable history. See the [developer guide](developer-guide.md) for exact value shapes.

Flowbit uses NCalc rather than FEEL for routing and assignment expressions, plus sandboxed Jint JavaScript for scripts. Function availability depends on the expression context. For example, `FlowInfo` is valid in non-default Exclusive, Inclusive, and Complex outgoing conditions, multi-instance completion, and scripts, but not Complex activation conditions, action visibility, input validation, or conditional events.

`FlowInfo(flowId, 'path')` and JavaScript `execution.getFlowInfo(flowId)` inspect persisted **actions** separately from **traversals**. A child MI vote is action-only; its aggregate result is traversal-only. Summaries are bounded per instance/flow, and detailed occurrences provide audit history. They contain post-feature-deployment evidence, without fabricated historical backfill. [FlowInfo reference](../Flowbit/README.md#instance-wide-flow-evidence-flowinfo).

## Flowbit extensions

| Extension | Contract and limits |
| --- | --- |
| `scopedInterruptEvent.gatewayRef` | Refers to an upstream Parallel, Inclusive, or Complex split. Resolves the nearest active activation in branch ancestry, cancels sibling/nested work, promotes the trigger to its parent scope, and continues. A stale activation records `scopedInterruptSkipped`. BPMN normally models such scope using an interruptible subprocess. |
| Merge `joinCancellation` | `{ "gatewayRef": splitId }` cancels unfinished descendants of that particular split activation after the merge enables. It never changes Parallel/Inclusive/Complex enabling rules. Exclusive becomes explicit first-arrival-wins. The survivor is promoted to the parent scope; unrelated work remains active. No common active referenced activation means 409 and rollback. |
| Worklist policy | Node/action roles, captured dynamic role policies, claim bypass, assignment/claim inheritance, inbox predicates, and runtime delegation extend BPMN's general resource concepts with concrete authorization behavior. |
| MI voting and parent interrupts | Selectable votes, engine-only outcomes, priorities, `afterEach`/`afterAll`, one-per-actor participation, and result collections define Flowbit's aggregation contract. |
| Automatic multi-instance items | Declared local `outputVariables`, the `maxConcurrency` window, the index-ordered result envelope, and partial results on a caught failure are Flowbit's loop data output contract for service, script, and call bodies. BPMN leaves item outputs and their aggregation to the implementation. |
| Call-only start | `startEvent.callOnly` limits the default plain start to call activities. It is a start restriction, not caller authorization: any published workflow can still call that family. |
| Execution and integration metadata | Async flags, retries, transport idempotency, business-key policy, typed REST mappings, shared bindings, and FlowInfo have Flowbit JSON semantics. Node/flow `attributes` are client metadata and do not alter execution. |
| Form presentation | Start and action input declarations may carry `label`, `helpText`, and fixed `options` for the forms that collect them. They do not alter execution or version compatibility, and options do not restrict accepted values; `validation` does. BPMN leaves forms to the implementation. See [form presentation](node-reference.md#form-presentation). |
| Task due date and priority | A `userTask` may carry `dueDate` (a fixed ISO-8601 duration, a fixed date-time, or an NCalc expression) and `priority` (an NCalc whole number), saved on each work item when it is created. The inbox returns, sorts, and filters by them, and reports `overdue` or `dueSoon` status. A missed due date has no execution effect; model enforced deadlines with a timer boundary. BPMN defines no task deadline attribute. See [due date and priority](node-reference.md#due-date-and-priority). |
| Business calendars | A timer `timeDuration` (`timer.calendar`) and a user-task `dueDate` duration (`dueDateCalendar`) can name a calendar that settings administrators manage: weekly working periods, holidays with optional hours, and an IANA time zone. Days then count working days at the same time of day, and hours count working hours. The due time is saved when the timer or work item starts, so editing a calendar never moves it. BPMN leaves business calendars to the implementation. See [working-time durations](node-reference.md#working-time-durations). |
| Cron timer starts | A `timerStartEvent` may set `timer.cron`, a five-field expression in Flowbit's restricted dialect, with a required IANA `timer.timeZone`, instead of an ISO-8601 schedule. BPMN timer definitions have no cron form. Occurrences are calculated and saved in UTC one at a time, follow the zone's daylight saving, and recur until the schedule is retired; the database refuses to complete one. See [cron schedules](node-reference.md#cron-schedules). |

Changes to active gateway, role, wait, job, or output contracts can block an in-place workflow-version switch. Versioning does not automatically migrate incompatible open work. Review [version integration](developer-guide.md) and [upgrade restrictions](deployment.md#upgrade-and-compatibility-rules).

## Unsupported capabilities

- BPMN XML import/export, BPMN DI interchange, or automatic execution of definitions exported by other BPMN products.
- Pools, collaborations, BPMN message flows, and choreography; lanes are visual organization only.
- Embedded/event/transaction subprocesses and compensation scopes. [Call activities](#call-activities) invoke a reusable workflow, within the listed limits.
- Event-based gateways; signal, escalation, compensation, cancel, and general throw-event families beyond the supported table. Link events connect two places in one definition only; a call activity is the way to reach another workflow.
- General BPMN send/receive task implementations, non-REST service connectors, and arbitrary external task executors.
- Multi-instance `task` nodes, events, and gateways, and the [later-release automatic multi-instance features](#multi-instance-service-script-and-call-activities) such as the cardinality source, per-item boundaries, and item skipping.
- DMN decision tables, FEEL, and month or year timer durations. Business calendars count only durations, not `timeDate`, `timeCycle`, or cron timers.
- Cron schedules on intermediate timer catches and timer boundaries, cron aliases, names, seconds, years, `?`, `W`, `#`, and `H`, cron schedules that skip a business calendar's holidays, and finite cron repetition counts.

Do not assume a similarly named element has every option found in a full BPMN engine. Definition validation and the current execution contracts above determine support.

## Choosing an example

| Goal | Start here |
| --- | --- |
| Build an approval inbox | [First workflow](getting-started.md), then [user-task examples](../examples/user-tasks/README.md). |
| Fan out independent reviews | [Parallel example](../examples/gateways/02-parallel-fork-and-join.json); use its `Finance`, `Legal`, and `Security` review roles. |
| Count votes or implement quorum | [Multi-instance examples](../examples/multi-instance/README.md); compare with [Complex gateway quorum](../examples/gateways/04-complex-two-of-three-merge.json). |
| Call a service, run a script, or start a child workflow for every item of a list | [Automatic multi-instance examples](../examples/multi-instance/README.md#automatic-multi-instance-activities) and the [multi-instance call](../examples/call-activities/04-multi-instance-approval-call.json). |
| Integrate a remote service or callback | [REST examples](../examples/service-tasks/README.md) and [message examples](../examples/messages/README.md). |
| Reuse a fragment across workflows | [Call activity examples](../examples/call-activities/README.md). |
| Replace a long return or crossing flow, or share a closing section | [Link events](../examples/basics/11-link-events.json). |
| Pause, remind, or escalate | [Timer and conditional examples](../examples/README.md), with [Worker configuration](deployment.md#worker-operation). |
| Interrupt one branch activation | [Gateway guide](../examples/gateways/README.md), including scoped interrupts and cancelling joins. |

Next: [Integrate your application](developer-guide.md) · [Deploy the runtime](deployment.md) · [Documentation home](index.md)
