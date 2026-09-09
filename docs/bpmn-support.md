# BPMN support and execution semantics

[Documentation home](index.md) · [First workflow](getting-started.md) · [Developer guide](developer-guide.md) · [API reference](api-guide.md)

Flowbit is a **BPMN-aligned JSON workflow engine**. It uses familiar events, tasks, gateways, and sequence flows, with persisted execution and a browser editor. Its native format is Flowbit JSON; it does not import or export BPMN 2.0 XML or claim complete BPMN conformance.

Use this guide to check whether an existing process can be expressed in Flowbit before choosing it as your execution engine. The [example catalog](../examples/README.md) supplies runnable definitions, actors, inputs, and prerequisites.

## Contents

- [Supported node types](#supported-node-types)
- [Graph and routing rules](#graph-and-routing-rules)
- [Human work and multi-instance tasks](#human-work-and-multi-instance-tasks)
- [Messages, timers, and conditions](#messages-timers-and-conditions)
- [Synchronous and durable execution](#synchronous-and-durable-execution)
- [Variables, expressions, and evidence](#variables-expressions-and-evidence)
- [Flowbit extensions](#flowbit-extensions)
- [Unsupported capabilities](#unsupported-capabilities)
- [Choosing an example](#choosing-an-example)

## Supported node types

The current model supports **21 node types**. “Worker” means the standalone `Flowbit.Worker` process. Every runtime workflow requires the API and PostgreSQL. A “No” below applies to the element itself; a downstream async task, timer, or durable conditional still requires a Worker.

| JSON `type` | Runtime behavior | Worker | Example or detail |
| --- | --- | --- | --- |
| `startEvent` | Authenticated user entry with start roles and typed inputs. A workflow may have several manual starts. | No | [Roles, claim, and bypass](../examples/user-tasks/01-roles-claim-and-bypass.json) |
| `messageStartEvent` | System-only entry through the workflow-family message-start endpoint; authenticates configured client credentials and correlation headers, then maps typed payload fields. | No | [Typed message start](../examples/messages/01-message-start-typed-mapping.json) |
| `timerStartEvent` | Creates instances on a persisted one-time or recurring schedule belonging to the published default version. | Yes | [Recurring start](../examples/timers/03-recurring-timer-start.json) |
| `userTask` | Persists actionable work with roles, assignment, optional claim ownership, and selectable outgoing actions. Supports normal and multi-instance work. | Only with async flags or durable boundaries | [Human work](#human-work-and-multi-instance-tasks) |
| `task` | Automatic pass-through step; no human work item or arbitrary application handler is invoked. | Only with async flags | [Async before and after](../examples/durable-jobs/01-async-before-after.json) |
| `serviceTask` | Invokes the `rest` connector with templated request values, bounded HTTP execution, and typed output mappings. | With async flags; required for shared bindings | [REST examples](../examples/service-tasks/README.md) |
| `scriptTask` | Runs ordered NCalc assignments or sandboxed JavaScript and persists validated outputs atomically. | Only with async flags | [Script examples](../examples/scripts/README.md) |
| `exclusiveGateway` | Split chooses the first true route by ascending priority, otherwise its required default. Merge forwards each arrival independently unless cancelling-join behavior is enabled. | No | [Exclusive routing](../examples/gateways/01-exclusive-priority-and-default.json) |
| `parallelGateway` | Split creates every branch. Merge waits for one token from every static incoming flow. | No | [Parallel fork and join](../examples/gateways/02-parallel-fork-and-join.json) |
| `inclusiveGateway` | Split creates every true route, otherwise its required default. Merge applies the unpaired enabling rule using graph reachability and active token positions. | No | [Inclusive split and merge](../examples/gateways/03-inclusive-conditional-split-and-merge.json) |
| `complexGateway` | Persists activation counts, phase, and cycle; evaluates `activationCondition` and start/reset outgoing conditions. | No | [Complex phases](../examples/gateways/05-complex-start-reset-cycle.json) |
| `intermediateMessageCatchEvent` | Persists a message wait; valid instance-scoped delivery maps output and advances the waiting token. | No | [Message catch](../examples/messages/02-message-catch-delivery-idempotency.json) |
| `intermediateTimerCatchEvent` | Persists a timer wait and resumes when its scheduled job runs. | Yes | [Timer delay](../examples/timers/02-intermediate-timer-delay.json) |
| `intermediateConditionalCatchEvent` | Waits until its condition over persisted instance variables is true; evaluates on entry and relevant variable-write batches. | Only for `durableAsync` | [Conditional catch](../examples/basics/05-conditional-event.json) |
| `errorBoundaryEvent` | Handles a service/script task failure through its attached error path; may capture an error string. One error boundary per host. | Follows host execution mode | [Script error boundary](../examples/scripts/04-javascript-error-boundary.json) |
| `timerBoundaryEvent` | Interrupts its host on expiry or creates a non-interrupting sibling branch; recurring reminders are supported. | Yes | [Reminder and deadline](../examples/timers/05-user-task-reminder-and-deadline.json) |
| `conditionalBoundaryEvent` | Observes instance-variable changes while its host is active; interrupts the host or captures repeatable false-to-true occurrences. | For `durableAsync` or an async host | [Conditional boundaries](#conditional-boundaries) |
| `scopedInterruptEvent` | Flowbit extension: interrupts the nearest active activation of a referenced Parallel, Inclusive, or Complex split and continues with the surviving token. | No | [Scoped interrupt](../examples/gateways/06-scoped-interrupt-parallel-branch.json) |
| `endEvent` | Completes its token. The instance completes when its remaining work has finished. | No | [Example catalog](../examples/README.md) |
| `terminateEndEvent` | Completes the instance and atomically cancels other active tokens, tasks, waits, and gateway scopes. | No | [Quorum with terminate end](../examples/gateways/04-complex-two-of-three-merge.json) |
| `errorEndEvent` | Faults the instance with an authored error code/description and cancels sibling work. | No | [REST failure and error end](../examples/service-tasks/02-rest-error-boundary-and-error-end.json) |

## Graph and routing rules

Definitions use integer authored node and flow IDs. A sequence flow has its own ID and refers to its source and target using `sourceRef` and `targetRef`. IDs must be unique within their respective collections, references must exist, and variable names cannot collide case-insensitively. Lanes organize the diagram; their geometry does not determine permissions or execution scope.

- A workflow needs at least one manual, message, or timer entry. Entries have no incoming flow and exactly one unconditional outgoing flow. `initialEventId`, when present, identifies a manual `startEvent`.
- Automatic tasks, service/script tasks, intermediate catches, and boundaries have one unconditional outgoing flow. End events have at least one incoming flow and no outgoing flows. A boundary is attached through `attachedToRef`, with no incoming sequence flow.
- A gateway must be a **split** with one incoming and at least two outgoing flows, or a **merge** with at least two incoming and one outgoing flow. One-in/one-out and many-in/many-out gateways are rejected; use adjacent split and merge nodes when needed.
- Exclusive splits require exactly one default and a condition plus unique positive `conditionPriority` on every other route. Defaults have neither condition nor priority. Exclusive merges have one unconditional output.
- Inclusive splits require exactly one unconditional default and a condition on every other route, without priorities. Inclusive merges have one unconditional output. Synchronization is based on tokens that can still reach the merge, not solely on a paired split ID.
- Parallel outputs are unconditional. A Parallel merge waits for all static inputs even if the model makes one unreachable at runtime; model the split and merge topology accordingly.
- Complex gateways require `activationCondition`. Non-default outputs require conditions; at most one unconditional default is allowed and `conditionPriority` is unsupported. `IncomingCount(flowId)`, `TotalIncomingCount()`, and `[gateway.waitingForStart]` express count and phase behavior.

Several tokens may be active in one instance. A loop may create new visits to a node and new work items; a node ID alone does not identify the current work item. Read [runtime identifiers and integration behavior](developer-guide.md) before building a task client.

## Human work and multi-instance tasks

A normal `userTask` creates one persisted work item for the visiting token. Outgoing flows represent user actions. The task's roles, action roles, assignment, claim requirements, and stored-state conditions determine who may act. An empty literal role list is unrestricted for that role check; it does not bypass the other checks.

`requiresClaim` adds ownership to authorization. Action-specific `canActWithoutClaim` and optional `canActWithoutClaimRoles` can permit an otherwise-authorized actor to bypass the claim. Assignee expressions and claim/assignment inheritance snapshot ownership on entry. Task and action `rolesVariable` values are also captured together on entry; changing the source variable does not change waiting work. Authorized role managers use the dedicated roles API to replace its saved policy.

**Normal user tasks cannot have default flows.** The API returns available actions; a client selects an action ID and supplies that action's inputs. Action conditions are evaluated against stored state before submitted values are applied. `inboxVisibilityCondition` is a separate database predicate that filters personal work before counting and paging; its absence does not remove task or action authorization.

Multi-instance configuration is supported on `userTask` only:

| Capability | Semantics |
| --- | --- |
| `mode: "parallel"` / `"sequential"` | Activates every child together or one child at a time. One parent token owns the execution. |
| `source: "collection"` | Snapshots a declared `string[]` of usernames and directly assigns children. |
| `source: "cardinality"` | Evaluates a bounded NCalc count; children use task roles and claim behavior. |
| `onePerActor: true` | Cardinality option allowing each username to complete at most one child; inboxes show a representative item and competing selections can return 409. |
| `completionEvaluation` | `afterEach` is the default and can finish early. `afterAll` delays aggregate evaluation until every item completes. Interrupts remain immediate. |
| Outcome conditions | `CountFlow`, `PercentFlow`, and `mi.total/completed/remaining`; lowest `completionPriority` wins among matching outcomes. |
| Engine-only fallback | Exactly one `isDefault: true`, `isSelectable: false` route with no condition or priority. It is used if all items finish without another outcome winning. |
| Interrupting outcome | Cancels unfinished children and advances the parent once. Parent interrupt endpoints can be used by authorized actors without owning an active child. |
| Result evidence | Ordered child records include selected flow, actor, roles, timestamp, and local values; a parent interrupt appends its own final record. |

An aggregate outcome is token routing, whereas each child's action is a vote. Treat them as different events in audit and reporting. See the [multi-instance examples](../examples/multi-instance/README.md) and [waiting-task role policy reference](../Flowbit/README.md#waiting-task-roles).

## Messages, timers, and conditions

### Messages

Message starts and catches are HTTP integration points. They use node-configured machine credentials and required headers, independently of user bearer authentication. Message starts select a workflow family and its published default version. Catches address an existing instance and select its matching wait. Neither is a global broker subscription or cross-instance BPMN message-flow implementation.

Output mappings are typed contracts with required fields, defaults, and validations. A failed mapping does not partially commit outputs. Start idempotency, business-key uniqueness, and catch-delivery idempotency are separate contracts; configure them deliberately and inspect actual conflict responses in the [API reference](api-guide.md). See the [message examples](../examples/messages/README.md).

### Timers

Each timer specifies exactly one fixed schedule:

| Property | Example value | Meaning |
| --- | --- | --- |
| `timeDate` | `"2030-01-01T09:00:00Z"` | Absolute timestamp with an explicit UTC offset. |
| `timeDuration` | `"PT30S"` | Positive fixed duration from activation. |
| `timeCycle` | `"R5/PT30S"` | Five occurrences at a 30-second interval; `R/PT30S` has no authored count limit. |

Calendar months/years, cron, time-zone calendars, and expression-based schedules are unsupported. Recurring intervals must be at least one second. The schedule is persisted, but firing can be late if the Worker is unavailable or busy. Recurring misfires more than one minute late skip to a subsequent eligible occurrence; a finite timer catch with no remaining occurrence opens an incident rather than inventing a trigger.

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

The expression must reference 1–64 declared, persisted **instance variables**, within 4,000 Unicode scalar values. Shared aliases and non-observable `sys.*`, `config.*`, `setting.*`, `mi.*`, `gateway.*`, and `FlowInfo` are rejected. Supported pure NCalc functions may be used. This is change-driven evaluation of committed variable batches, not polling arbitrary external conditions.

- Missing `deliveryMode` means `atomic`: evaluate and advance within the writer's transaction.
- `durableAsync`: when true, latch the occurrence and persist its job and exact activation fence in that transaction. The Worker later advances it even if another write has made the expression false.

All variable producers use the same coordinator, including administrative variable updates. Restarting an API or Worker does not lose a persisted wait or latched wake. See the [conditional runtime reference](../Flowbit/README.md#intermediate-conditional-catch-events).

### Conditional boundaries

Timer and conditional boundaries attach to a normal/MI user task, message catch, timer catch, or automatic `task`/`serviceTask`/`scriptTask` with `asyncBefore: true`. A conditional catch cannot itself host these boundaries. Each host may have eight timer and conditional boundaries combined; its one possible error boundary is separate.

Missing `cancelActivity` means interrupting. Conditional subscriptions start false, so initial true captures immediately. A non-interrupting boundary creates sibling work and rearms after false; true-to-true writes do nothing, while later false-to-true changes capture independent occurrences even if earlier durable occurrences are still queued.

For simultaneous conditions, non-interrupting boundaries are captured in node-ID order, then the lowest-ID interrupting boundary wins. Successful host outputs are evaluated before normal exit; a service/script error keeps error-boundary precedence. Interrupting a running REST call cannot physically stop the remote side effect, but a fenced late result cannot restore cancelled work. See [conditional boundary details](../Flowbit/README.md#conditional-boundary-events).

## Synchronous and durable execution

Without async flags, pass-through nodes execute in the current transition until the instance rests or finishes. Instance mutations are transactional and locked in PostgreSQL. A bounded hop count protects one synchronous routing segment from infinite automatic loops. Synchronous REST can hold that transition open while waiting on the external service; an uncaught failure rolls back the transition.

`task`, `serviceTask`, `scriptTask`, and `userTask` accept `asyncBefore` and `asyncAfter`. On multi-instance work the flags apply to the parent, not each child. Async boundaries persist jobs so the API can commit before the Worker continues. Timers and `durableAsync` conditionals always use that queue. REST tasks reading or writing shared-variable bindings must use `asyncBefore: true`.

Durable service/script work uses short staging and finalization transactions, with external HTTP or script execution between them. Lease generations, activation fences, and output-version checks prevent an expired attempt from overwriting newer state. They do **not** make remote HTTP side effects exactly-once. Send a stable downstream idempotency key derived from `sys.jobId`; `sys.jobAttempt` identifies the attempt.

Node `job` policy selects `boundaryFirst` (default) or `retryFirst` failure handling. Omitting `retryDelays` uses the engine's retry schedule of 10 seconds, 1 minute, and 5 minutes; an explicit empty array disables automatic retries. Up to ten positive fixed ISO-8601 delays can be authored. Unresolved failures become inspectable incidents with fenced manual retry. See [deployment and Worker operations](deployment.md) and the [durable runtime reference](../Flowbit/README.md#durable-async-work-and-timers).

## Variables, expressions, and evidence

Process declarations support scalar `string`, `number`, `boolean`, `date`, `datetime`, and `json`, with the model's supported array forms, defaults, required inputs, and validation. Start inputs and user-action inputs are explicit contracts. Shared bindings reference a deployment catalog and are accessed through its dedicated API; they are not exposed as instance-variable history. See the [developer guide](developer-guide.md) for exact value shapes.

Flowbit uses NCalc rather than FEEL for routing and assignment expressions, plus sandboxed Jint JavaScript for scripts. Function availability depends on the expression context. For example, `FlowInfo` is valid in Exclusive routing, multi-instance completion, and scripts, but not action visibility, input validation, or conditional events.

`FlowInfo(flowId, 'path')` and JavaScript `execution.getFlowInfo(flowId)` inspect persisted **actions** separately from **traversals**. A child MI vote is action-only; its aggregate result is traversal-only. Summaries are bounded per instance/flow, and detailed occurrences provide audit history. They contain post-feature-deployment evidence, without fabricated historical backfill. [FlowInfo reference](../Flowbit/README.md#instance-wide-flow-evidence-flowinfo).

## Flowbit extensions

| Extension | Contract and limits |
| --- | --- |
| `scopedInterruptEvent.gatewayRef` | Refers to an upstream Parallel, Inclusive, or Complex split. Resolves the nearest active activation in branch ancestry, cancels sibling/nested work, promotes the trigger to its parent scope, and continues. A stale activation records `scopedInterruptSkipped`. BPMN normally models such scope using an interruptible subprocess. |
| Merge `joinCancellation` | `{ "gatewayRef": splitId }` cancels unfinished descendants of that particular split activation after the merge enables. It never changes Parallel/Inclusive/Complex enabling rules. Exclusive becomes explicit first-arrival-wins. The survivor is promoted to the parent scope; unrelated work remains active. No common active referenced activation means 409 and rollback. |
| Worklist policy | Node/action roles, captured dynamic role policies, claim bypass, assignment/claim inheritance, inbox predicates, and runtime delegation extend BPMN's general resource concepts with concrete authorization behavior. |
| MI voting and parent interrupts | Selectable votes, engine-only outcomes, priorities, `afterEach`/`afterAll`, one-per-actor participation, and result collections define Flowbit's aggregation contract. |
| Execution and integration metadata | Async flags, retries, transport idempotency, business-key policy, typed REST mappings, shared bindings, and FlowInfo have Flowbit JSON semantics. Node/flow `attributes` are client metadata and do not alter execution. |

Changes to active gateway, role, wait, job, or output contracts can block an in-place workflow-version switch. Versioning does not automatically migrate incompatible open work. Review [version integration](developer-guide.md) and [upgrade restrictions](deployment.md#upgrade-and-compatibility-rules).

## Unsupported capabilities

- BPMN XML import/export, BPMN DI interchange, or automatic execution of definitions exported by other BPMN products.
- Pools, collaborations, BPMN message flows, and choreography; lanes are visual organization only.
- Embedded/event/transaction subprocesses, call activities, reusable subprocess invocation, and compensation scopes.
- Event-based gateways; signal, escalation, compensation, cancel, link, and general throw-event families beyond the supported table.
- General BPMN send/receive task implementations, non-REST service connectors, arbitrary external task executors, or multi-instance behavior on other activity types.
- DMN decision tables, FEEL, cron schedules, business calendars, and calendar-relative timer durations.

Do not assume a similarly named element has every option found in a full BPMN engine. Definition validation and the current execution contracts above determine support.

## Choosing an example

| Goal | Start here |
| --- | --- |
| Build an approval inbox | [First workflow](getting-started.md), then [user-task examples](../examples/user-tasks/README.md). |
| Fan out independent reviews | [Parallel example](../examples/gateways/02-parallel-fork-and-join.json); use its `Finance`, `Legal`, and `Security` review roles. |
| Count votes or implement quorum | [Multi-instance examples](../examples/multi-instance/README.md); compare with [Complex gateway quorum](../examples/gateways/04-complex-two-of-three-merge.json). |
| Integrate a remote service or callback | [REST examples](../examples/service-tasks/README.md) and [message examples](../examples/messages/README.md). |
| Pause, remind, or escalate | [Timer and conditional examples](../examples/README.md), with [Worker configuration](deployment.md#worker-operation). |
| Interrupt one branch activation | [Gateway guide](../examples/gateways/README.md), including scoped interrupts and cancelling joins. |

Next: [Integrate your application](developer-guide.md) · [Deploy the runtime](deployment.md) · [Documentation home](index.md)
