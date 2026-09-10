# Flowbit

.NET 10 workflow runtime for definitions exported by `flowbit-editor.html`.

Start with the [developer documentation](../docs/index.md) for HTTP onboarding, the complete API reference, BPMN support, and deployment. This README retains the detailed runtime implementation reference.

## Projects

- `src/Flowbit.Api` - ASP.NET Core Web API, endpoints and composition root.
- `src/Flowbit.Service` - engine logic, service interfaces, repository ports.
- `src/Flowbit.Infrastructure` - EF Core, PostgreSQL, migrations, repositories.
- `src/Flowbit.Shared` - DTOs and C# workflow definition model.
- `src/Flowbit.Ui` - Blazor Server UI that calls the API.
- `src/Flowbit.Worker` - durable PostgreSQL job/timer dispatcher, lease
  heartbeats, timer-start reconciliation, metrics, and retention cleanup.
- `tests/Flowbit.Tests` - xUnit definition/editor tests plus an in-process
  API host backed by an isolated PostgreSQL Testcontainer.
- `tools/*` - the existing definition verifier, live API regression runner, and
  instance load runner.

## Workflow examples

The categorized [workflow example catalog](../examples/README.md) contains
editor-loadable definitions for the JSON authoring contract, including gateways,
timers, scripts, service and message integrations, user-task ownership, start
keys, and multi-instance execution. Each entry documents its inputs, actor roles,
expected result, and any API, Worker, or trusted configuration prerequisites.

## Storage

### Waiting-task roles

User tasks and selectable, non-default outgoing flows can use `rolesVariable`
instead of nonempty literal `roles`. It names a declared process `string[]`
variable, including a shared binding. Values are captured together when the
task or multi-instance approval is created and stored in an immutable role
policy. Later variable changes, reassignment, and sequential reviewer activation
do not reevaluate them. A new task visit captures new values. Missing, null,
wrongly typed, or blank-only values reject task creation. Variable-derived and
manually supplied lists accept at most 100 entries with 300 Unicode characters
per trimmed role, deduplicated case-insensitively. Literal `roles: []` and an
explicit empty management list mean unrestricted role access; ownership and
other action conditions still apply.

Set workflow `taskRoleManagementRoles` to enable a separate management
authority. Missing or empty disables it; assignment managers and distribution
clients do not inherit it. In Flowbit.Ui, **Task management** lists active and
pending persisted work and offers **Edit roles** to authorized managers. Roles
can change while assignees and claimants remain in place. Multi-instance edits
affect its parent interrupt actions and every active/pending reviewer together;
finished reviewer policies remain historical.

Use GET/POST `/api/user-tasks/{taskId}/roles` for normal tasks or GET/POST
`/api/multi-instance-executions/{executionId}/roles` for an approval. Read the
current policy before posting `expectedRolePolicyId`, replacement `roles`, the
complete selectable `flows: [{ flowId, roles }]` list, and optional `reason`.
Identical retries succeed unchanged; a different edit with a stale policy ID
returns 409. One `taskRolesChanged` history entry records each committed edit.
These management reads bypass personal inbox conditions without granting user
actions. Management search accepts `status=active|pending|open`, defaulting to
active for existing API callers.

Inbox membership continues to use `user_tasks.Roles` in PostgreSQL before
counting/paging; returned-page action checks batch-load captured policies.
There are no inbox role-variable evaluations or per-task policy queries.
Compatible version switches preserve saved policies and manual edits. Changes
to role sources or the captured outgoing action contract block a switch with
affected open work; adding only `taskRoleManagementRoles` remains compatible.

Deploy by applying the additive role-policy migration, then upgrading every
API and Worker replica before publishing variable-role definitions or exposing
role management. The migration captures existing open work from its stored
task roles and immutable definitions without inventing historical changes.
Mixed legacy and policy-aware replicas are unsupported. See the
[example](../examples/user-tasks/05-variable-roles-and-role-management.json).

### Tables

- All Flowbit tables, owned sequences, indexes, constraints, and EF migration
  history live in the fixed PostgreSQL `flowbit` schema.
- Workflow definitions are versioned JSONB snapshots in
  `flowbit.workflow_definitions`.
- Runtime state is normalized in `flowbit.workflow_instances`,
  `flowbit.execution_tokens`, `flowbit.user_tasks`,
  `flowbit.gateway_executions`, `flowbit.gateway_branches`,
  `flowbit.complex_gateway_states`,
  `flowbit.multi_instance_executions`, `flowbit.multi_instance_flow_counts`,
  `flowbit.node_executions`, `flowbit.instance_variables`,
  `flowbit.instance_variable_current_values`,
  `flowbit.instance_history`,
  `flowbit.sequence_flow_occurrences`, and `flowbit.sequence_flow_summaries`.
- Durable work is stored in `flowbit.workflow_jobs`,
  `flowbit.workflow_job_attempts`, `flowbit.workflow_job_snapshots`,
  `flowbit.workflow_incidents`, and `flowbit.timer_subscriptions`.
- Deployment-wide shared-variable catalog, current-value, immutable revision,
  definition binding/dependency, wake outbox, delivery, and incident state is
  stored in the corresponding `flowbit.shared_variable_*` tables. Catalog rows
  use `Revision` for API compare-and-swap; `ValueRevision` changes only when the
  effective value changes and is the precise durable-job output fence.
- Runtime mutations use one lock order: instance, active gateway
  executions/states/branches, active tokens, active multi-instance executions,
  then active/pending user tasks; rows are ordered by ID within each group.
  Stale competing actions return 409 instead of advancing twice.
- Instance summary/detail projections include grouped active, pending, claimed,
  and assigned task counts. Claim ownership is exposed only by task DTOs; instance
  DTOs do not project a task claimant.
- Node and sequence-flow roles are enforced from the authenticated actor's JWT.
- Workflow-level `taskAssignmentRoles` authorize assignment managers to assign,
  reassign, and unassign active normal or multi-instance user-task work items
  owned by that immutable workflow version.
- Optional workflow-level `taskDistribution` credentials enable the external
  distributor API. It authenticates against the current published default and
  may manage tasks across every active version of the stable workflow key.
  Credential values may be literal or `${setting.*}` / `${config.*}` references;
  prefer references because literal secrets are visible in versioned definition
  JSON.
- A normal user task with `requiresAssignment=true` is stored as active but is
  omitted from regular inbox/task reads until it has an assignee. Assignment
  managers and, when configured, the external distributor can still see it.
  Such a definition must provide at least one `taskAssignmentRoles` entry after
  trimming, blank removal, and case-insensitive deduplication, or complete
  `taskDistribution` credentials. Whenever `taskDistribution` is present, both
  fields are required even when manager roles are also configured.
  `assignmentMode` supports `fresh`, `previous`, and `fromNode` ownership
  inheritance; this gate is intentionally separate from `requiresClaim` and
  cannot be combined with it.
- `flowbit.instance_variables` remains the append-only variable audit history.
  An `AFTER INSERT` trigger transactionally upserts the newest row per
  `(InstanceId, VariableName)` into `flowbit.instance_variable_current_values`.
  Search and latest-value enrichment read that bounded projection; execution
  detail and audit reads continue to use history.

## Shared variables

A top-level process-variable declaration can bind a workflow-local alias to one
deployment-wide catalog key:

```json
{
  "name": "approvalAmount",
  "scope": "shared",
  "sharedKey": "examples.approval.amount",
  "access": "read",
  "dataType": "number",
  "isArray": false,
  "nullable": false
}
```

`scope`, `sharedKey`, and `access` are accepted only on top-level variables.
Scope and access values are canonical lowercase `shared`, `read`, or
`readWrite`. Catalog/API key lookup is case-insensitive, but a saved definition
must use the catalog key's exact canonical casing; runtime locks canonical keys
in ordinal order. Aliases retain the engine's existing case-insensitive variable
semantics. A saved or published definition must match the catalog entry's type,
array, nullability, and validation contract exactly. Shared values are not copied
into instance variable history or returned by ordinary instance APIs.

Runtime locking is driven by an immutable access plan cached by workflow
definition ID. It derives exact shared reads and targets a node can produce
(mappings, assignments, submitted values, status and error variables).
JavaScript with dynamic `setVariable` conservatively includes that node's
`readWrite` bindings. The engine locks only those exact shared keys, in ordinal
key order, after the instance/runtime locks. Conditional-event conditions are
instance-only and cannot reference a shared alias. REST service tasks that read
or write a shared binding must use
`asyncBefore: true`; the stage/commit/invoke/finalize protocol keeps shared and
instance locks out of the external HTTP interval.

Every committed catalog/audit mutation receives a globally unique `Revision`
from PostgreSQL. Sequence values can contain rollback gaps and revisions on
different keys are not commit-order timestamps. `ValueRevision` starts at zero
and advances only for an effective set or unset. Description/lifecycle changes
and identical-value writes leave it unchanged. New durable-job snapshots store
the shared-output value revisions and therefore conflict only with a real value
change; legacy snapshots without that field retain the conservative catalog-
revision comparison. The rollout dual-writes both snapshot shapes. A migration
compatibility trigger advances `CurrentRevision` and `ValueRevision` whenever an old writer advances
the authoritative current-value projection, preventing a new worker from
missing an effective legacy write during replacement.

An accepted JSON null for a `nullable: true` catalog contract skips custom
validation everywhere: API writes, synchronous and asynchronous scripts, and
repository writes. A script value-contract failure is a task failure and can
follow an attached error boundary without persisting a partial batch. Lease
loss, archived keys, database errors, broken invariants, and concurrent value
changes remain operational conflicts or incidents.

Shared aliases are rejected in conditional catches for both default `atomic`
and `durableAsync` delivery. Copy a required catalog value into an instance
variable when a workflow needs a state snapshot, or use a message event when an
occurrence must be delivered. Effective shared-value updates persist the catalog
revision and audit history but do not create conditional wake work.

The shared-variable HTTP API supports JWT administrators and scoped API clients
(`shared-variables.read` / `shared-variables.write`). Shared-variable revision
history is immutable operational/audit data and is not a secret store; never put
credentials or other secrets in shared values.

The hard-cut removal migration is guarded: it aborts before dropping the legacy
shared-conditional dependency, wake, delivery, and incident tables when any
legacy rows remain. Do not delete those rows manually or bypass the guard. Move
the deployment to a separate safe-drain release/runbook, replace the affected
definitions and finish or migrate their instances, then retry the guarded
migration. Stop every API and Worker replica before running it, deploy the API,
UI, and Worker as one coordinated version, and restart only after deployment is
complete. Mixed old/new replicas are unsupported for this cutover.

Production rollout is migration-first. Inventory published definitions for REST
tasks that use shared bindings without `asyncBefore`; republish them safely or
drain their instances before enabling the new validation. Run
[`tools/shared-variable-rest-inventory.sql`](tools/shared-variable-rest-inventory.sql)
against the target PostgreSQL database. Require its unsafe-REST result to be
empty. Its second result conservatively inventories every published definition
with multiple shared keys; each row must be saved/republished through the
monotonic validator or drained before rollout. The inventory conservatively
matches validation-expression references, so review reported false positives
rather than suppressing the check.

Steady-state locking is exact per node/selected flow. Every newly acquired
shared key across one synchronous transaction segment must be nondecreasing in
ordinal order. Validation models the full FIFO routing queue, forks, node/flow
producers, output fences, and error paths.
`asyncBefore` resets before a node; service/script `asyncAfter` has the same
implicit pre-node stage, while async-after and true resting/durable waits reset
downstream continuation. Unsafe new definitions are rejected. An immutable-ID
cached runtime proof makes an already-published unsafe definition fail with 409
before any shared row lock, so it must be drained and republished.

First deploy the database allocator function backed by the existing singleton
allocator and move all writers to that function. While allocator mode remains
`legacy`, the first workflow shared lock/write/value fence temporarily prelocks
all keys bound by that immutable definition in ordinal order. This stage-1 guard
prevents mixed-version singleton writers from deadlocking with new multi-key
transitions; it may increase contention. After every API and Worker writer is
upgraded, seed the PostgreSQL sequence, switch the function to pure `nextval`,
and set the allocator row to sequence mode. The compatibility prelock then
becomes a no-op, restoring permanent exact node/flow locking. A database trigger
rejects every legacy `LastRevision` update, so an old binary fails closed; the
rejection is the intentional compatibility fail-safe equivalent of renaming or
removing the old allocator table, while the row remains an EF-compatible
cutover/audit marker. Then deploy the remaining components, verify shared-value
reads and writes, and perform the sequence cutover. Mixed legacy replicas are
prohibited after sequence cutover.

Perform the fenced allocator cutover exactly once after all writer replicas are
upgraded (repeated calls are harmless):

```sql
SELECT flowbit.cutover_shared_variable_revision_sequence();
```

## Intermediate conditional catch events

An `intermediateConditionalCatchEvent` is a persisted wait whose condition can
observe declared instance variables. Its node JSON uses this shape:

```json
{
  "type": "intermediateConditionalCatchEvent",
  "conditional": {
    "condition": "approved == true",
    "deliveryMode": "durableAsync"
  }
}
```

`conditional.condition` is required and is limited to 4,000 Unicode scalar
values. It uses NCalc built-ins and Flowbit's pure string helpers, must reference
1-64 declared persisted instance variables, and cannot use a top-level
`scope: "shared"` alias, undeclared variables, unknown functions, `FlowInfo`,
gateway/multi-instance helpers, or non-observable `sys.*`, `config.*`,
`setting.*`, `mi.*`, and `gateway.*` context. Shared aliases are rejected for
both delivery modes. The node must
have at least one incoming flow and exactly one unconditional outgoing flow; it
cannot carry task, role, variable, async-job, or another event type's metadata.
`deliveryMode` accepts `atomic` or `durableAsync`; omission means `atomic`, and
the editor omits the field for that default.

The engine evaluates the condition when a token enters the event. If it is
false, the active token rests there. Thereafter every runtime instance-variable
producer records its writes, and a complete transactional write batch considers only
waiting events whose extracted dependency set intersects the changed names.
Each authored condition is evaluated once against the same post-batch
PostgreSQL current-variable snapshot, then a true result is applied to all of
that node's active wait activations. Evaluation and wake selection occur while
the writer holds the instance row lock and transaction.

- `atomic` advances each still-active matching token through its fixed outgoing
  flow in that writer transaction.
- `durableAsync` persists a `conditionalWake` control job and the token's exact
  activation/job wait fence in that transaction. A leased worker later advances
  the latched flow without re-evaluating the condition, so a subsequent write
  cannot retract an already durable wake.

Tokens, latches, jobs, variables, and immutable definition IDs are PostgreSQL
state. Worker leases and activation fences make delivery safe across API/worker
restarts and multiple replicas; notifications are only wake-up hints and
polling remains authoritative. Extracted dependency plans are bounded
per-process caches keyed by immutable definition ID and rebuild deterministically
after restart or eviction, so they are not correctness state. A running instance
may switch versions at an active conditional wait only when its normalized
condition, effective delivery mode, and outgoing-flow contract remain
compatible; compatible open jobs are rebound under the instance lock.

Definition validation enforces the instance-only dependency rule for every
delivery mode. This release has no legacy shared-conditional execution path; the
guarded removal migration must succeed before the matching application binaries
are deployed.

## Conditional boundary events

A `conditionalBoundaryEvent` attaches the same conditional expression contract
to a durable activity:

```json
{
  "type": "conditionalBoundaryEvent",
  "attachedToRef": 12,
  "cancelActivity": false,
  "conditional": {
    "condition": "riskScore >= 80",
    "deliveryMode": "durableAsync"
  }
}
```

Omitting `cancelActivity` makes the boundary interrupting; omitting
`deliveryMode` selects `atomic`. A boundary has no incoming flow and exactly one
unconditional outgoing flow. It may attach to a normal or multi-instance user
task, an intermediate message/timer wait, or a `task`, `serviceTask`, or
`scriptTask` with `asyncBefore: true`. A host may own at most eight timer and
conditional boundaries combined; its optional error boundary is counted
separately. No HTTP endpoint manually triggers a conditional boundary.

Every host activation owns persisted subscription rows fenced by instance,
host token, activation, and boundary node. Initial truth starts at false, so a
condition already true on entry is captured immediately. A non-interrupting
subscription creates a sibling boundary token and remains attached; it rearms
only after evaluating false, and each later false-to-true edge creates another
handler. A true-to-true write is a no-op. An interrupting capture cancels the
host's open task or multi-instance work, waits, timers, jobs, and sibling
subscriptions, then reuses the host token for the boundary path. If several
conditions match in one batch, every non-interrupting boundary is captured in
node-id order, followed by the lowest-id interrupting boundary; other
interrupting matches are suppressed.

Variable writers evaluate only authored nodes whose dependency sets intersect
the complete committed write batch. They load one current-variable snapshot,
evaluate each affected authored expression once, and fan the Boolean result out
to targeted active catch tokens and boundary subscriptions. All durable
occurrences are persisted before any atomic continuation runs. Durable boundary
jobs carry both subscription and occurrence fences, so multiple rearmed edges
can remain queued concurrently and stale/duplicate workers cannot repeat an
effect. Successful user actions, message outputs, service mappings, and script
writes are evaluated while the host is still active; an interrupting match wins
over normal completion. Service/script failure handling still gives an attached
error boundary precedence.

The instance row remains the transaction-serialization lock. Completion versus
capture, timer versus conditional interruption, and competing variable writers
therefore have a deterministic first-lock-wins outcome. An external call that is
already executing cannot be physically stopped mid-call; cancellation fences
its job and rejects a late result, matching timer-boundary behavior. Version
switching requires an exact active boundary contract (attachment, normalized
condition, delivery mode, interruption flag, and outgoing flow) and rebinds the
subscription and open jobs under the lock.

Deployment is additive and ordered: apply
`AddConditionalBoundaryEvents`, upgrade every API and Worker replica and the
editor, then allow definitions containing `conditionalBoundaryEvent` to be
published. Mixed conditional-boundary-aware and legacy replicas are unsupported,
and no historical subscription backfill is required.

## Gateways and scoped interruption

Gateway direction is inferred from topology. A split has exactly one incoming
and at least two outgoing flows; a merge has at least two incoming and exactly
one outgoing flow. One-in/one-out and many-in/many-out gateways are rejected, so
split and merge behavior must be authored as separate nodes. Their types are
independent: for example, a Parallel split may feed an Inclusive or Exclusive
merge.

- Exclusive splits keep ordered first-match/default routing; Exclusive merges
  pass each arriving token without synchronization.
- Parallel splits select every outgoing flow and Parallel merges wait for every
  static incoming flow.
- Inclusive splits select every true condition (or the required default).
  Inclusive merges use cached graph reachability plus current active token
  positions to apply the unpaired BPMN enabling rule and retain surplus tokens
  for later batches.
- Complex gateways persist a per-instance/node phase and cycle. Their
  `activationCondition` can use literal `IncomingCount(flowId)`,
  `TotalIncomingCount()`, and outgoing conditions can use
  `[gateway.waitingForStart]` to distinguish start from reset routing.

Any valid Exclusive, Parallel, Inclusive, or Complex merge may opt into a
scope-aware cancelling join:

```json
"joinCancellation": {
  "gatewayRef": 2
}
```

Omitting `joinCancellation` preserves the merge's existing behavior. The policy
is stored only in the versioned workflow-definition JSONB, so it requires no
database schema migration.

`gatewayRef` must identify a structurally upstream Parallel, Inclusive, or
Complex split whose activation contains every merge input. This is a Flowbit
extension: it does not change when the merge is enabled. After the merge fires,
Flowbit retains one contributing token, promotes it out of the referenced
activation, and atomically cancels the activation's other unfinished descendant
work before following the merge's outgoing flow. Cancellation is limited to
that activation, including nested branches, user and multi-instance tasks,
message/timer waits, and durable jobs; unrelated instance work remains active.

The gateway-specific firing rules still apply. An Exclusive merge fires on its
first arrival, making the option an explicit first-arrival-wins policy. Parallel
merges still wait for every static input, Inclusive merges still use the
unpaired enabling rule, and Complex merges still wait for their activation
condition; a cancelling Complex merge closes that activation without running a
reset-phase output. If the contributing tokens do not share an active referenced
scope at firing time, the transition returns `409 Conflict` and rolls back
rather than routing or partially cancelling work. Enabling, disabling, or
changing `gatewayRef` is a gateway-contract change, so a running instance cannot
switch to that workflow version while active gateway/branch state remains.

Every Parallel, Inclusive, or Complex firing is recorded in
`gateway_executions`; diverging firings create generic branch lineage even when
only one flow is selected. `scopedInterruptEvent` is a Flowbit extension whose
`gatewayRef` targets a structurally reachable Parallel, Inclusive, or Complex
split. It interrupts the nearest active matching activation in the triggering
token's ancestry, cancels sibling and nested work atomically, and follows its
single authored continuation. A stale event records `scopedInterruptSkipped`
and continues without cancellation.

Parallel, Inclusive, and Complex cancelling merges record `joinCancellation` on
their joined merge execution. Exclusive merges retain the normal `gateway`
history record and create no merge execution. In every case, the referenced
split execution records `interruptingJoin` when unfinished work was cancelled;
its interrupting token and merge node identify the winning route.

The fan-out limit is configured by `Workflow.Gateway.MaxActiveTokens` and
defaults to 1000. Adjacency and reachability plans are cached per immutable
workflow-definition ID.

## Instance-wide flow evidence (`FlowInfo`)

Definitions can query fixed-size, instance-lifetime summaries for a sequence
flow. NCalc uses a literal flow id and property path:

```text
FlowInfo(201, 'actions.count')
Contains(FlowInfo(201, 'actions.last.userRoles'), 'Manager')
FlowInfo(201, 'traversals.last.kind')
FlowInfo(201, 'all')
```

Supported paths are `all`; `actions.count`; `actions.last.user`, `userRoles`,
`occurredAt`, `kind`, or `values`; and the equivalent `traversals.*` paths.
`all` returns this shape (an unused known flow has zero counts and null `last`
values):

```json
{
  "flowId": 201,
  "actions": { "count": 1, "last": { "user": "alice", "userRoles": ["Manager"], "occurredAt": "...", "kind": "userTaskAction", "values": {} } },
  "traversals": { "count": 1, "last": { "user": "alice", "userRoles": ["Manager"], "occurredAt": "...", "kind": "userTaskAction", "values": {} } }
}
```

JavaScript script tasks use the same read-only shape through
`execution.getFlowInfo(201)`, for example
`execution.getFlowInfo(201).actions.last.userRoles`.

`actions` count explicit actor selections; `traversals` count token movement.
A normal user-task selection and a parent multi-instance interrupt are both; a
multi-instance child vote is action-only; its aggregate winner/default is
traversal-only; and automatic routing is traversal-only. The last evidence
snapshots the validated actor and all their roles at action time, so a directly
following gateway can route on the role of the user who selected the flow.

`FlowInfo` is allowed in non-default Exclusive, Inclusive, and Complex outgoing
conditions, multi-instance completion conditions, NCalc script assignments, and
JavaScript script tasks. It is not available in Complex activation conditions,
user-action visibility conditions, assignee/cardinality
expressions, or variable/output/header validation. `CountFlow` and `PercentFlow`
are unchanged and remain scoped to the current multi-instance execution.

Each recorded event appends to the audit ledger and transactionally updates one
summary row per instance/flow. Runtime expressions load summaries once and do
not scan or load detailed history, so evaluation cost does not grow with loops
or multi-instance fan-out. Definitions that do not reference `FlowInfo` skip the
summary query and write no occurrence or summary rows. The additive migration
does not backfill or guess old events; existing instances expose only evidence
recorded after deployment.

## Durable async work and timers

`task`, `serviceTask`, `scriptTask`, and `userTask` support `asyncBefore` and
`asyncAfter`. A multi-instance user task applies those flags to its parent
execution, not to every child item. Conditional catches may use
`conditional.deliveryMode: "durableAsync"` to enqueue control work. Definitions
may also use
`timerStartEvent`, `intermediateTimerCatchEvent`, and `timerBoundaryEvent` with
exactly one fixed ISO-8601 `timeDate`, `timeDuration`, or `timeCycle` schedule.
Timer boundaries may be interrupting or noninterrupting and may recur.

The worker leases bounded work with PostgreSQL `FOR UPDATE SKIP LOCKED`.
Workflow mutations still use the instance-first runtime lock order and verify
the job lease generation, token activation, wait phase, and subscription before
committing. Service calls and JavaScript execution happen between short staging
and finalization transactions, so the external/CPU-heavy body holds neither an
instance lock nor a database transaction. Output targets use variable-version
conflict detection; a concurrent write opens an incident instead of silently
overwriting it.

Worker defaults are eight total slots, six activity slots, and four concurrent
activity jobs per instance. Two slots therefore remain available for
timer/control work. PostgreSQL notifications are wake-up hints; one-second
polling remains authoritative, and an idle dispatcher shortens its backoff to
the next persisted due or lease-expiry deadline. Calls have at-least-once
external semantics and receive stable `sys.jobId` and `sys.jobAttempt` values,
so downstream services should use `sys.jobId` as an idempotency key.

Durable automatic activity lineages are bounded by the shared engine setting
`Workflow.Async.MaxConsecutiveAutomaticActivations` (default `1000`; missing,
invalid, or nonpositive values use that default). The 1,001st consecutive
automatic `task`, `serviceTask`, or `scriptTask` activation is persisted as an
`automatic_loop_limit` incident before its body can run. User-task actions,
multi-instance completion, message delivery, and timer triggers start a fresh
allowance. An administrator can retry the incident to queue the same stable job
as activation one of a new allowance.

The Blazor **Operations** page and `/api/jobs` / `/api/incidents` resources
default to the `admin` role. Override the comma-separated global role list with
the `WorkflowJobs.RequiredRole` engine setting. Job, incident, and attempt
collections use opaque keyset cursors; their list rows intentionally exclude
snapshots, result payloads, stack traces, and embedded attempt collections.
Instance summaries expose only grouped open/queued/running/incident counts and
the nearest due time.

For an additive rollout, first deploy the schema and API with
`WorkflowDurableProcessing:PublicationEnabled=false`. Draft definitions may
still be saved, but publishing or making an async, timer, or durable-conditional
definition the default is rejected. Start at least one worker and confirm
`/health/ready` returns 200
after its first successful durable-queue query, then set the gate to `true`.
Existing definitions and running instances remain synchronous and require no
backfill.

Standard .NET meters publish worker and engine signals under `Flowbit.Worker`
and `Flowbit.Runtime.Jobs`. They include queue depth/age, timer lateness,
acquisition and instance-lock wait latency, lease loss, retries, output
conflicts, incidents, and cleanup volume. The worker also exposes these signals
in Prometheus text format at `/metrics`; `/health/live` is its process liveness
probe. These operational endpoints listen on `FlowbitWorker:HealthListenUrl`
(`http://0.0.0.0:8081` by default). Timer-start reconciliation uses one
transaction-scoped advisory leader and processes at most
`FlowbitWorker:TimerStartReconcileBatchSize` workflow families per pass, so
additional replicas provide failover without duplicating the full scan.

## History and audit retention

Open **Manage → Retention policies** (`/retention`) to configure deployment-wide
retention. The API uses the dynamic `Settings.RequiredRole` permission (default
`admin`). Each category accepts **Keep forever** or a positive whole number of
days. Previewing a draft policy neither saves it nor deletes data. Previews are
bounded and identify lower-bound counts when the sample limit is reached.

| Category | Retained data and age reference |
| --- | --- |
| Workflow history | Instance events and flow occurrences, aged from instance finish. |
| Variable history | Superseded instance-variable values, aged from instance finish. |
| Node activity | Terminal node executions, aged from instance finish. |
| Administrative audits | Eligible standalone variable-update/version-change audits, aged from instance finish. |
| Shared-variable history | Revisions aged from instance finish when workflow-linked, otherwise creation. |
| Completed jobs | Terminal unleased jobs without open incidents, attempts, and orphan snapshots. |
| Resolved incidents | Incidents aged from resolution. |

The five history/audit policies initially keep data forever. The first upgraded
Worker initializes the operational policies from
`FlowbitWorker:CompletedJobRetentionDays` and `ResolvedIncidentRetentionDays`
(defaults 30/90; bootstrap values must be 1–36,500 days). Subsequent configuration changes use the saved policies;
restarting a Worker does not overwrite them. Use consistent bootstrap settings
on all replicas. Before initialization the UI shows the operational policies as
awaiting the Worker.

`Flowbit.Worker` owns scheduled and manually requested cleanup. **Run now** queues
or joins a durable run using saved policies and respects the same throttling as
hourly runs. The UI server never performs cleanup. A dedicated retention pool
allows at most two connections per process, separate from the runtime pool.
Default Worker limits are:

| `FlowbitWorker` option | Default |
| --- | ---: |
| `RetentionBatchSize` | 250 |
| `RetentionBatchDelayMilliseconds` | 1000 |
| `RetentionIdleDelayMilliseconds` | 5000 |
| `RetentionMaxRunnableJobs` | 100 |
| `RetentionMaxQueueLagSeconds` | 30 |

`RetentionBatchSize` replaces the legacy `CleanupBatchSize` setting.

Cleanup yields to a busy workflow queue, uses short transactions, skips
contended instances, and resumes persisted progress after interruptions. Each
category has a per-run budget of 20 batches or 30 seconds of database work,
with a five-second statement timeout and a 500 ms lock timeout. Budget-limited
scans retain their cutoff and finite scan boundaries with bookmarks for the next
run while the policy is unchanged; completed scans wrap around so newly expired
records are eventually considered. Manual
runs do not bypass limits. Monitor workflow latency and database I/O when tuning
these settings: a separate connection pool does not isolate PostgreSQL disk work.

Some expired rows remain protected. Cleanup preserves instance/token/task state,
current-variable source history, lifetime FlowInfo summaries, keyed retry
results, message receipts and their history references, and all batch audit
structures. Shared revisions preserve catalog current/value references and
request receipts; retained variable and shared history can also pin node
executions. Retention never nulls a reference to force deletion.

`FinishedAt` records the latest terminal transition independently of ordinary
instance updates. Any actual instance-owned history deletion atomically sets
`HistoryPrunedAt`, after which reactivation is permanently unavailable—even if
the policy is later disabled. Instance and shared-variable details disclose
pruned history. No-op cleanup and job/incident cleanup do not disable reactivation.

Management routes are `GET /api/retention`,
`PUT /api/retention/policies/{category}` with `retentionDays` and
`expectedRevision`, read-only `POST /api/retention/preview`, and
`POST /api/retention/runs`. Policy edits use optimistic revision checks;
coordinator/status records retain bounded current and last-run results.

Apply the additive migration first, then upgrade every API and Worker replica
before enabling the new policies. Existing terminal rows use `UpdatedAt` as a
conservative finish-time backfill. Mixed old/new cleanup Workers are unsupported.
The migration rejects downgrade after any history has been pruned; restoring
the previous schema and its deleted history requires a full database backup.
This feature permanently deletes eligible live-table records; it does not export
archives, configure per-workflow periods, prune running-instance history, or
remove protected batch/retry records.

## Run Locally

Follow the canonical [getting-started guide](../docs/getting-started.md) for
PowerShell and Bash commands, development token acquisition, and an executable
HTTP tutorial. From the repository root, `docker compose up --build -d --wait`
builds and starts PostgreSQL 17, API, UI, and Worker using [compose.yaml](../compose.yaml).
The application Dockerfiles live in their project directories and require the
repository root as build context. The .NET SDK is needed only inside the builds;
the images run on the non-root .NET 10 ASP.NET Core runtime.

This local stack uses Development mode, loopback host ports, matching API/UI JWT
settings, and a persistent PostgreSQL named volume. Database health gates API
startup; the API migrates before its OpenAPI check allows the UI and Worker to
start. See [Docker configuration and lifecycle](../docs/deployment.md#local-docker-compose-stack)
for ports, optional `.env` values, volume retention/reset, and deployment limits.
The getting-started guide also retains the alternative host .NET setup.

For an existing database, use [deployment and migration guidance](../docs/deployment.md)
and the compatibility notes below. Development API startup applies migrations;
it does not import workflow definitions automatically.

The additive `SeedDefaultSettings` migration inserts these baseline settings
when they are missing in any environment where migrations are applied:

| Store | Logical keys | Seed value |
| --- | --- | --- |
| Engine | `Workflow.RequiredRole`, `WorkflowInstances.RequiredRole`, `NodeExecution.RequiredRole`, `WorkflowJobs.RequiredRole`, `Delegation.AdminRoles` | `admin` |
| Engine | `Workflow.Gateway.MaxActiveTokens`, `Workflow.MultiInstance.MaxInstances`, `Workflow.Async.MaxConsecutiveAutomaticActivations` | `1000` |
| Engine | `WorkflowVariableUpdates.MaxBatchInstances` | `10000` (hard cap) |
| Workflow context | `setting.examples.messageClientId` | JSON string `"example-message-client"` |
| Workflow context | `setting.examples.messageCorrelation` | JSON string `"orders:inbound"` |

The migration never overwrites existing settings. It deliberately does not seed
`Authentication.UserIdentityClaim`, task-distribution credentials, or secrets;
keep deployment secrets in `WorkflowContext:Config` or another secret-backed
configuration provider.

### Gateway migration rollout

The generic gateway runtime is intentionally incompatible with the retired
parallel-specific schema and JSON vocabulary. For a deployment that predates
`inclusiveGateway`, `complexGateway`, and `scopedInterruptEvent`:

1. Stop every Flowbit API and worker process.
2. Back up anything that must be retained, then reset the `flowbit` schema (or
   recreate the database).
3. Apply the current EF Core migrations to the empty schema.
4. Deploy and start the matching API and UI versions.
5. Re-import workflow definitions using only the canonical gateway node types
   and `scopedInterruptEvent.gatewayRef`.

Do not run old and new binaries against the same database. Legacy
`parallelInterruptEvent`, `parallelGatewayRef`, parallel-specific runtime rows,
and in-flight gateway executions are not migrated.

## Main API

- `GET /api/workflows`
- `GET /api/workflows/{id}`
- `POST /api/workflows`
- `PUT /api/workflows/{id}`
- `POST /api/workflows/{id}/publish`
- `POST /api/instances`
- `GET /api/instances?status=running` (SQL-authorized, opaque cursor paging)
- `GET /api/instances?includeVariables=true`
- `POST /api/instances/search` (advanced SQL-backed variable search)
- `GET /api/instances/inbox` (actor-scoped)
- `GET /api/instances/inbox?includeVariables=true` (actor-scoped)
- `POST /api/instances/inbox/search` (advanced, actor-scoped)
- `POST /api/user-tasks/manage/search` (advanced, assignment-manager scoped)
- `POST /api/task-distribution/workflows/{workflowKey}/tasks/search`
- `GET /api/node-executions` (authorized cross-workflow activity)
- `POST /api/node-executions/search` (advanced authorized activity search)
- `GET /api/node-executions/{id}` (authorized execution detail)
- `GET /api/jobs` (admin by default; opaque cursor paging)
- `GET /api/jobs/statistics`
- `GET /api/jobs/{id}`
- `GET /api/jobs/{id}/attempts` (opaque cursor paging)
- `GET /api/incidents` (admin by default; opaque cursor paging)
- `GET /api/incidents/{id}`
- `POST /api/incidents/{id}/retry`
- `GET /api/instances/{id}`
- `GET /api/instances/{id}/flows`
- `POST /api/instances/{id}/claim`
- `POST /api/instances/{id}/unclaim`
- `POST /api/instances/{id}/flows/{flowId}`
- `POST /api/instances/{id}/cancel`
- `GET /api/instances/{id}/reactivation` (workflow-admin eligibility preview)
- `POST /api/instances/{id}/reactivation` (workflow-admin terminal reactivation)
- `PATCH /api/instances/{id}/variables` (workflow-admin raw JSON upsert)
- `POST /api/instance-variable-update-batches/candidates/search`
- `POST /api/instance-variable-update-batches` (`202`, durable preparation)
- `GET /api/instance-variable-update-batches`
- `GET /api/instance-variable-update-batches/{batchId}`
- `GET /api/instance-variable-update-batches/{batchId}/items`
- `POST /api/instance-variable-update-batches/{batchId}/confirm`
- `POST /api/instance-variable-update-batches/{batchId}/cancel`
- `GET /api/user-tasks/manage` (assignment-manager scoped)
- `POST /api/user-tasks/{taskId}/assign`
- `POST /api/user-tasks/{taskId}/unassign`
- `GET /api/task-distribution/workflows/{workflowKey}/tasks`
- `POST /api/task-distribution/workflows/{workflowKey}/tasks/{taskId}/assign`
- `POST /api/task-distribution/workflows/{workflowKey}/tasks/{taskId}/unassign`
- `GET /api/auth/context` (server-resolved workflow actor and roles)
- `GET /api/instances/{id}/administrative-actions` (workflow-administrator discovery)
- `POST /api/instances/{id}/administrative-actions` (immediate audited override)
- `GET /api/multi-instance-executions/{executionId}/flows`
- `POST /api/multi-instance-executions/{executionId}/flows/{flowId}`

## Terminal instance reactivation

A caller holding the dynamic `Workflow.RequiredRole` administrator role can
preview and reactivate a `completed` or `cancelled` instance. The preview returns
the instance's current workflow-version and `UpdatedAt` fences, global runtime
blockers, warnings, and distinct eligible user tasks previously visited in that
same immutable version. Faulted or running instances are not supported.

`POST /api/instances/{id}/reactivation` requires `targetNodeId`,
`expectedWorkflowId`, `expectedUpdatedAt`, and a trimmed reason containing 1 to
1,000 Unicode characters. Eligible targets are completed or cancelled normal
node executions for top-level, non-multi-instance `userTask` nodes. Async-before
tasks, branch-scoped visits, unsafe gateway re-entry, currently true conditional
boundaries, dirty runtime artifacts, unresolved durable work, and retained
Complex Gateway state or lineage are rejected. The commit repeats every check
under the instance lock and returns `409 Conflict` for drift or a business-key
ownership conflict.

Reactivation continues the current process state; it does not roll variables or
external systems back. In one transaction the engine reacquires any released
business-key claim, changes the instance to `running`, creates a fresh root
token, user task, node execution and activation fence, initializes normal task
boundaries and ownership inheritance, and requires exactly one stable active
task at the requested node. All prior tokens, work items, executions, jobs,
subscriptions, variables, receipts, FlowInfo summaries, and history remain
unchanged. The engine appends an `instanceReactivated` history row but records no
sequence-flow occurrence because no authored flow was traversed. If task entry
cannot reach the stable-wait postcondition, the status, claim, new runtime rows,
and audit all roll back together.

## Administrative task actions

All administrative-action HTTP routes and public administrative service/engine
entry points require a nonblank actor and the shared `WorkflowAdministratorPolicy`.
The current `Workflow.RequiredRole` setting supplies comma-separated roles,
matched case-insensitively; absent/blank means `admin`, and custom values replace
that default. This covers catalogs, candidate search, audit reads, creation,
confirmation, cancellation, and direct instance execution before any retry result
is returned. It does not change ordinary task, inbox, claim, assignment,
delegation, or multi-instance interrupt permissions or captured role policies.

GET `/api/instances/{id}/administrative-actions` uses an instance-scoped database
candidate query with exact count and numbered paging. Ordinary tasks appear
individually and multi-instance parents appear once, with their unfinished
active/pending child count. The response combines each position's version,
token/activation/timestamp fences with selectable non-default direct flows and
their input definitions. Parallel positions remain independently addressable.

POST to the same route checks ownership and the displayed version, token
activation, position timestamp, and affected count under the established
instance → gateway state → token → multi-instance → open-task lock hierarchy.
It then inserts a real single batch/item audit and calls the scoped engine via
`IAdministrativeActionExecutor` in the same unit-of-work transaction. Ordinary
routing, conditional-boundary early returns, and `asyncAfter` paths commit only
when the engine owns its transaction; immediate execution leaves commit/disposal
to the outer service. Successful routing, history, FlowInfo evidence, the
succeeded item, and completed batch commit atomically. Failure rolls all of them
back. The response is actual refreshed instance state plus the audit batch ID.
No administrative preparation/execution job is enqueued, and the existing Worker
entry point retains its own transaction.

Direct overrides bypass task/action roles, assignment, claims, inbox visibility,
and the selected flow condition while retaining typed input validation,
downstream routing, and limits. `forceParent` cancels unfinished multi-instance
children without votes; `completeAllChildren` records the chosen action/common
inputs on every unfinished child and suppresses intermediate aggregate routing.
Both traverse the selected flow once and retain completed-child history.
When complete-all processes a still-pending sequential child, its node visit
starts and completes at the administrative action time, with the administrator
as its trigger. Existing active-visit start times are preserved; pending
children cancelled by force-parent remain unstarted.
Timer-boundary overrides remain on the durable batch API.

Batch preparation rechecks the current administrator setting against the stored
preparer roles per item; execution checks the stored confirmer roles. A denial
becomes `ineligible` during preparation or `skipped` with `authentication_changed`
during execution, without endless retries. These are role snapshots, not live
identity-provider checks. Already committed successes and administrative
`asyncAfter` continuations remain valid and retain their original audit context.
Authored asynchronous work still needs the Worker after a direct action.

This authorization change requires coordinated API/Worker rollout but no new
database migration. See the [upgrade rules](../docs/deployment.md#upgrade-and-compatibility-rules),
[HTTP contract](../docs/api-guide.md#instance-administrative-actions), and
[UI procedure](../docs/ui-guide.md#use-instance-administrative-actions).

## Administrative instance-variable updates

`PATCH /api/instances/{id}/variables` synchronously appends one or more raw JSON
values to a running workflow instance in one locked transaction. The request is
`{ "variables": [{ "name": "name", "value": ... }], "reason": "...",
"idempotencyKey": "..." }`. Existing values are never updated in place: every
request adds variable-history rows and the current-value trigger advances the
projection. The result reports one administrative update-operation ID, the
instance timestamp, and each variable's actual `added` or `updated` outcome and
history-row ID. Instance detail includes the correlated update audits and each
variable-history row carries its nullable update-operation ID.

These are deliberately administrative **raw JSON** writes. They bypass workflow
variable declarations, type and array contracts, nullability, and NCalc rules;
JSON `null` is a value and is not deletion. Names are trimmed, compared
case-insensitively, limited to 300 Unicode characters, and cannot start with
`sys.`, `config.`, `setting.`, or `mi.`. When an existing name differs only by
case, its stored casing is retained. Identical values still create audit rows.
Only running instances are accepted, and all endpoints require authentication
plus the dynamic `Workflow.RequiredRole` workflow-administrator policy.

The `/api/instance-variable-update-batches` resource freezes either explicit
instance IDs or all instances matching an applied family-scoped filter with
exclusions. Candidate search always enforces `running`, requires one workflow
key, optionally constrains one immutable version, and supports instance,
business-key, current-node, advanced current-variable, structured sort, cursor,
and `includeVariables` options. Only the applied mutation filter is persisted;
sort, cursor, page, and display options are not part of the frozen selection.

Batch creation returns `202 Accepted` and starts durable preparation. One
prepare job is created for every represented workflow definition. Preparation
classifies names as add/update, records active-job warnings, and produces
eligible/ineligible/warning counts. Confirmation fences those counts and the
batch timestamp, snapshots the confirming identity and roles, and creates one
execution job per represented definition. Instances then commit independently,
while all requested variables for one instance, its administrative audit, and
its successful batch-item transition commit together. Execution rechecks the
running/family contract; drift is skipped. Changes made after preparation are
intentionally overwritten under the instance lock. Cancelling stops only
unstarted items and never reverses successes.

The API and the standalone `Flowbit.Worker` must both be running for batches to
progress. Durable leases, retries, incidents, cleanup, and expired-lease
reconciliation use the shared PostgreSQL worker infrastructure; PostgreSQL
notifications remain wake-up hints. Active instance jobs do not block an update,
but the API and UI warn that a job may continue with a previously frozen variable
snapshot. Limits are 1 MiB per request body, 100 variable rows, 10,000 instances
per batch (configurable downward with
`WorkflowVariableUpdates.MaxBatchInstances`), 100,000 expanded writes, and
100 MiB of expanded serialized values.

## Node execution activity

`GET /api/node-executions` and `POST /api/node-executions/search` are the
read-only, cross-workflow activity search resources. The Blazor **Activity**
page uses the POST search route. `GET /api/node-executions/{id}` returns one
authorized execution with its execution-local detail. These routes do not grant
assignment, claim, cancellation, or workflow-mutation authority; the existing
inbox and Task Assignments APIs remain the action surfaces for human work.

A node execution represents one token visit to one node. Gateway splits
therefore produce a visit for each spawned branch token. A normal user task
shares one execution with its work item. A multi-instance user task instead
produces one execution per child work item and deliberately has no duplicate
parent execution row. Execution kind is `node` for a token visit and
`userTaskItem` for a multi-instance child. The supported lifecycle statuses are
`pending`, `active`, `completed`, `cancelled`, `faulted`, and `merged`;
completion reasons distinguish normal/user/message/conditional/multi-instance
work, gateway firing/interruption behavior,
caught errors, scoped or instance cancellation, and terminal end behavior. The
reason values are `normal`, `userAction`, `messageDelivery`,
`conditionalTriggered`,
`multiInstanceItem`, `multiInstanceCompleted`, `multiInstanceInterrupt`,
`boundaryCaught`, `normalEnd`, `terminateEnd`, `errorEnd`,
`instanceCancelled`, `gatewayScopeCancelled`, `gatewayJoinMerged`,
`parallelFork`, `parallelJoin`, `inclusiveSplit`, `inclusiveMerge`,
`complexActivation`, `complexReset`, `scopedInterrupt`, and
`scopedInterruptSkipped`.

The list returns `PagedResult<NodeExecutionSummaryDto>` and the detail route
returns `NodeExecutionDetailDto`. Summary rows include immutable workflow,
instance, token, node, branch, flow, actor, ownership, lifecycle, duration, and
cutover correlation. Detail adds actor and node-role snapshots, user-task
assignment and claim data, multi-instance item/result context, committed failure
information (descriptions are bounded to 1,000 Unicode characters), and only
variable changes attributed to that execution. It does not
present current instance variables as if they were an execution-time snapshot.
For those writes, `sourceActionId` retains the runtime source and can therefore
identify either a selected sequence flow or an automatic/message/boundary node.

List filters cover execution, instance, workflow version/key/version, business
key, token, user-task, multi-instance, branch, item, node, lifecycle, actor,
owner, flow, and timestamp/duration fields. Inclusive `From` and exclusive `To`
bounds are used for created, started, updated, and completed time ranges.
Repeated status, node-type, instance-status, and completion-reason values are
OR-combined within their group; different groups are AND-combined.

| Filter group | Query parameters |
| --- | --- |
| Identity/context | `executionId`, `instanceId`, `workflowId`, `workflowKey`, `workflowVersion`, `businessKey`, `tokenId`, `userTaskId`, `multiInstanceExecutionId`, `gatewayBranchId`, `itemIndex` |
| Node/lifecycle | `executionKind`, `nodeId`, `nodeName`, `nodeExternalId`, repeated `nodeType`, repeated `status`, repeated `instanceStatus`, repeated `completionReason`, `isMultiInstance`, `isCutoverSeeded` |
| People/flows | `owner`, `startedBy`, `completedBy`, `enteredViaFlowId`, `selectedFlowId`, `exitedViaFlowId`, `aggregateFlowId` |
| Time/duration | `createdFrom`, `createdTo`, `startedFrom`, `startedTo`, `updatedFrom`, `updatedTo`, `completedFrom`, `completedTo`, `minDurationMilliseconds`, `maxDurationMilliseconds` |

Repeated `var=name:value` parameters keep the existing instance-search
semantics: each pair tests the owning instance's **latest scalar value** using an
exact case-insensitive comparison. Up to ten pairs may be supplied and all must
match. They are current-instance filters, not historical values captured at
execution time.

Up to three unique `sort=field:asc|desc` clauses may select `id`, `instanceId`,
`workflowId`, `nodeId`, `createdAt`, `startedAt`, `updatedAt`, `completedAt`, or
`duration`. The default is `updatedAt:desc,id:desc`; nullable fields use
`NULLS LAST`, and an ID tie-breaker is always applied. Paging defaults to page 1
and 50 rows, permits at most 200 rows, and reports an exact database-backed
`totalCount`. Invalid identifiers, enums, time ranges, durations, variables, or
sort clauses return 400.

Visibility is evaluated in SQL before count, ordering, and paging. A caller can
read a row when one of their JWT roles appears either in the dynamic global
reader setting or in that immutable workflow version's `taskAssignmentRoles`.
Set the global reader roles in `flowbit.engine_settings` with namespace
`NodeExecution`, key `RequiredRole`, and a comma-separated value. A missing or
blank value defaults to `admin`. Both routes require authentication, so an
unauthenticated caller receives 401. An authenticated caller with no visible
workflow versions gets an empty page; an out-of-scope detail ID returns 404. UI
code intentionally does not duplicate or hard-code these role rules.

Node execution tracking is complete only for committed work from the migration
cutover onward. The migration seeds open active/pending user-task work plus
active non-multi-instance token visits, marks those rows with
`isCutoverSeeded=true`, and uses the cutover time for seeded active
`startedAt` values. It does not fabricate completed executions from legacy
`instance_history`. A failed transition that rolls back has no committed
execution record; a caught service/script failure is committed as a faulted host
execution followed by its boundary execution. Execution rows are retained
with their owning workflow instance by default; an administrator may enable
node-activity retention for finished instances. Pruning history permanently
blocks reactivation of the affected instance.

`instance_history` remains a heterogeneous transition and audit log used by
legacy instance detail and claim/assignment inheritance. It is not a complete
node-lifecycle ledger and should not be used to derive visit cardinality,
durations, or every cancellation.

The multi-instance execution endpoints expose only selectable interrupting flows
(`cancelRemainingInstances=true`) authorized by both the current node and flow
roles. They let an authorized actor interrupt the parent execution even without
an active child work item; unfinished child items are cancelled and the workflow
continues through the selected flow.

Task assignment mutations use `expectedUpdatedAt` for optimistic concurrency and
accept an optional audit reason. Assignment clears any existing claim and creates
direct ownership; unassignment clears both ownership fields and restores the
node's authored `requiresClaim` setting. For `requiresAssignment` tasks, unassign
returns the work item to the hidden assignment queue without rerunning
inheritance. Every real assignment change is recorded as an instance-history
audit entry. Manager authorization comes from the task's immutable workflow
version: workflows without `taskAssignmentRoles` expose no tasks through the
manager endpoints or `/task-management`.

The optional task-distribution endpoints are machine-facing and do not use JWT
roles.
They authenticate `X-Client-Id` / `X-Client-Secret` against `taskDistribution`
on the current published default definition, while listing and mutating tasks
across all versions of that workflow family. They preserve the same optimistic
concurrency and audit behavior as manager actions. The list is minimal by
default; `includeVariables=true` adds latest instance variables for the returned
page. Missing configuration disables only external distribution; configured
assignment managers can continue using the manager endpoints and
`/task-management`. Production callers must use TLS and should be rate-limited
at the gateway. Both assignment surfaces include the unassigned
`requiresAssignment` tasks they are authorized to manage; regular inbox users
cannot discover, claim, or act on those tasks. Instance detail responses redact
`taskDistribution.clientSecret`.

A manager-enabled required-assignment definition can start without family
distribution credentials. A distributor-only definition instead depends on the
current published default's credentials. Starting that version is rejected when
the credentials are unavailable, and default-version changes or deletion of the
default are guarded while running distributor-dependent instances would
otherwise lose their external assignment channel.

Example configuration:

```json
"taskDistribution": {
  "clientId": "workforce-service",
  "clientSecret": "${setting.taskDistribution.clientSecret}"
}
```

Known multi-instance `mode`, `source`, and `completionEvaluation` casing is
canonicalized when definitions are loaded. Unknown or explicitly null values,
duplicate node/flow ids, and case-variant duplicate variable names are rejected.
Cardinality and collection fan-out are bounded before allocation by
`Workflow.MultiInstance.MaxInstances`.

The JWT claim used as the canonical workflow actor can be configured in
`flowbit.engine_settings` with namespace `Authentication`, key
`UserIdentityClaim`, and a stable claim name such as `sub` or `oid`. The value is
loaded once at API startup. If the row is absent, the API retains the legacy
`Identity.Name`/`NameIdentifier` selection; a configured claim that is missing or
invalid causes a 401. Restart all API replicas after changing it, and migrate or
drain active assignments and claims before switching identity formats.

## Verification

Docker is required for the isolated PostgreSQL tests. Historical workflow inputs
are tracked in [test fixtures](tests/Flowbit.Tests/Fixtures/README.md) and copied
by the test project; no generated fixture directory or custom MSBuild target is
needed:

```powershell
dotnet test .\tests\Flowbit.Tests\Flowbit.Tests.csproj
dotnet run --project .\tools\MultiInstanceVerifier\MultiInstanceVerifier.csproj
dotnet run --project .\tools\MultiInstanceApiTests\MultiInstanceApiTests.csproj -- --manage-api
```

The live API runner writes Markdown and JSON evidence under the repository-level
`TestResults` directory and includes restart recovery plus a 1,000-item load case.

## Advanced variable search

Five additive POST endpoints accept endpoint-specific native selectors plus a
shared Mongo-inspired `variableFilter` object:

| Endpoint | Scope retained from its GET counterpart |
| --- | --- |
| `POST /api/instances/search` | Instance selectors, structured sort, cursor/paging, and `includeVariables` |
| `POST /api/instances/inbox/search` | Actor inbox selectors, structured sort, paging, and `includeVariables` |
| `POST /api/user-tasks/manage/search` | Task, instance, owner/ownership selectors and manager-scoped paging |
| `POST /api/task-distribution/workflows/{workflowKey}/tasks/search` | Distribution selectors, paging, and `includeVariables`; workflow key stays in the route and credentials stay in headers |
| `POST /api/node-executions/search` | All execution selectors/ranges, structured sort, and paging |

An empty JSON object preserves the corresponding unfiltered GET defaults. POST
sorting uses objects such as `{ "field": "updatedAt", "direction": "desc" }`;
each endpoint retains its existing allowed fields, default order, cursor rules,
page limits, authorization, status codes, and `PagedResult<T>` response shape.

Instance search example:

```http
POST /api/instances/search
Content-Type: application/json

{
  "workflowKey": "health-certificate",
  "variableFilter": {
    "$and": [
      { "request.medicalCenter.id": { "$eq": "MC-1042" } },
      { "request.services": { "$contains": "health-certificate" } }
    ]
  },
  "sort": [{ "field": "updatedAt", "direction": "desc" }],
  "pageSize": 50,
  "includeVariables": true
}
```

Actor inbox example:

```http
POST /api/instances/inbox/search
Content-Type: application/json

{
  "variableFilter": {
    "request.medicalCenter.id": { "$eq": "MC-1042" }
  },
  "sort": [{ "field": "taskUpdatedAt", "direction": "desc" }],
  "page": 1,
  "pageSize": 50,
  "includeVariables": true
}
```

Assignment-manager example:

```http
POST /api/user-tasks/manage/search
Content-Type: application/json

{
  "ownership": "unassigned",
  "variableFilter": { "request.region": { "$in": ["north", "central"] } },
  "page": 1,
  "pageSize": 50
}
```

External distributor example (credentials remain transport headers, not body
fields):

```http
POST /api/task-distribution/workflows/health-certificate/tasks/search
X-Client-Id: workforce-service
X-Client-Secret: <secret>
Content-Type: application/json

{
  "owner": "clinic-queue",
  "variableFilter": {
    "request.services": { "$containsAny": ["health-certificate", "screening"] }
  },
  "includeVariables": true,
  "page": 1,
  "pageSize": 50
}
```

Node-execution example:

```http
POST /api/node-executions/search
Content-Type: application/json

{
  "statuses": ["completed"],
  "createdFrom": "2026-07-01T00:00:00Z",
  "variableFilter": { "request.medicalCenter.id": { "$eq": "MC-1042" } },
  "sort": [{ "field": "updatedAt", "direction": "desc" }],
  "page": 1,
  "pageSize": 50
}
```

The first segment of a dotted field identifies the Flowbit variable and later
segments address JSON object properties. Array indexes are not supported; use
containment operators or `$elemMatch`. If a variable name or JSON key itself
contains a dot, use the unambiguous `$field` form:

```json
{
  "$field": {
    "$var": "request.medicalCenter",
    "$path": ["id"],
    "$eq": "MC-1042"
  }
}
```

Inside `$elemMatch`, the same escape hatch is element-relative: omit `$var`
and provide `$path` for a dotted property name on the current array element.

```json
{
  "items": {
    "$elemMatch": {
      "$field": {
        "$path": ["medical.center", "id"],
        "$eq": "MC-1042"
      }
    }
  }
}
```

Supported logical operators are `$and`, `$or`, and `$not`. Supported comparison
operators are `$eq`, `$eqIgnoreCase`, `$ne`, `$in`, `$nin`, `$gt`, `$gte`,
`$lt`, `$lte`, `$exists`, `$contains`, `$containsAny`, `$containsAll`, and
`$elemMatch`. Multiple ordinary fields are an implicit AND, as are multiple
operators on one field. A logical node cannot mix logical and field members;
wrap them in an explicit `$and` instead. `$elemMatch` evaluates its complete
nested predicate against one array element.

`$eq` is typed and case-sensitive; use `$eqIgnoreCase` for strings. Object
equality follows normalized PostgreSQL `jsonb` equality, while array equality is
order-sensitive. `$in` and `$nin` accept scalar alternatives; use containment
operators for JSON arrays/objects. Range operators accept JSON numbers only in
this version. Dates and datetimes support equality/membership but not ranges.
Missing paths do not satisfy comparisons, including `$ne` and `$nin`;
`$exists:false` is the only missing-path match, and explicit JSON `null` remains
distinct from a missing path.

Requests reject unknown operators, mixed node shapes, raw JSONPath, `$where`,
regex, executable expressions, and type-invalid operands with 400. A search body
is limited to 64 KiB; `variableFilter` is limited to five logical levels, 20
comparison predicates, 100 values per membership operator, and 16 JSON path
segments.

The validated filter is compiled only from whitelisted, parameterized PostgreSQL
templates and runs before count, sorting, and paging. Every repository composes
its mandatory role, actor, assignment, claim, delegation, workflow-family, or
execution-visibility predicate with the variable filter using AND. Consequently,
a caller's `$or` or `$not` cannot weaken authorization. Variable filtering is a
search predicate, not authorization: derive tenant/medical-center constraints
from trusted identity data, and keep outgoing-flow conditions as action-time
guards. No result membership is filtered in memory.

`flowbit.instance_variables` remains the append-only audit source. Migration
backfill plus an `AFTER INSERT` trigger maintains
`flowbit.instance_variable_current_values`, keeping only the greatest source ID
per instance/name. Its variable-name, `jsonb_ops` GIN, root case-insensitive
string, and root numeric indexes support the shared SQL compiler; targeted
expression indexes can be added later for measured hot nested paths. Latest
`includeVariables` enrichment also reads this projection.

All legacy GET routes remain unchanged. Repeated `var=name:value` filters retain
their exact case-insensitive latest-scalar-text behavior and are translated to
the shared SQL filter internally; repeated values remain AND-combined (including
the node-execution route's existing ten-filter limit). The Blazor Instances,
My Work, Task Assignments, Activity, and Task Distribution search surfaces use
the POST routes on every load, including an empty advanced filter; dashboard
summary reads retain their lightweight GET calls. `includeVariables=true` still
returns the latest JSON value for each name, omitting the property when false or
absent.

All five search surfaces expose a developer/admin multiline JSON editor with
syntax/root-object validation and formatting. API validation remains
authoritative for operators and operand types. Instances, My Work, and Task
Distribution can request and expand returned variable values; Task Assignments
and Activity use variables only as search predicates because their POST
contracts do not return them. The read-only `/task-distribution` diagnostic UI
is shown only in the Development environment; direct navigation outside
Development redirects to the UI home page. It remains
credential-authenticated by workflow key and `X-Client-Id` /
`X-Client-Secret`; credentials remain component state, the secret is sent only
as a header, and neither credential is placed in a URL or persisted by the UI.
These controls are diagnostics, not authorization: production clients must
derive tenant or medical-center predicates from trusted identity data.

### Instance and inbox sorting

The instance list and inbox accept up to three repeated `sort=field:direction`
parameters in priority order. Directions are `asc` and `desc`.

```text
GET /api/instances?sort=createdAt:asc&sort=id:desc
GET /api/instances/inbox?sort=instanceCreatedAt:desc&sort=userTaskId:asc
```

Instance fields are `id`, `createdAt`, and `updatedAt`. Inbox fields are
`userTaskId`, `instanceId`, `taskCreatedAt`, `taskUpdatedAt`,
`instanceCreatedAt`, and `instanceUpdatedAt`. Without an explicit sort, the
existing defaults remain `updatedAt DESC, id DESC` and
`taskUpdatedAt DESC, userTaskId DESC`, respectively. An implicit unique-ID
tie-breaker keeps pages deterministic. The instance list returns an opaque
`nextCursor`; pass it back as `cursor` for the next page. A cursor is bound to
its sort order, and a later page without the preceding cursor is rejected.
Instance visibility is applied in SQL before the exact count, ordering, paging,
and grouped job enrichment: callers with a role in
`WorkflowInstances.RequiredRole` (default `admin`) see every version, while
other authenticated callers see versions whose `taskAssignmentRoles` intersect
their JWT roles. Invalid, duplicate, or excessive sort clauses and malformed
cursors return 400.

Inbox responses expose explicit task and instance creation/update timestamps.
The older `createdAt` and `updatedAt` properties remain compatibility aliases
for `taskCreatedAt` and `taskUpdatedAt`.

## User tasks

Each token resting on a `userTask` owns a persisted work item. Task-addressed routes are authoritative when an instance may have more than one active work item:

| Route | Purpose |
| --- | --- |
| `GET /api/user-tasks/{taskId}` | Read an authorized task, its completion metadata, and caller capabilities. |
| `GET /api/user-tasks/{taskId}/flows` | List actions currently visible and executable by the caller. |
| `POST /api/user-tasks/{taskId}/claim` | Claim an active shared-pool task. |
| `POST /api/user-tasks/{taskId}/unclaim` | Release a claim as its owner or through a workflow `unclaimRoles` override. |
| `POST /api/user-tasks/{taskId}/flows/{flowId}` | Execute a visible action with optional typed variables. |
| `POST /api/user-tasks/{taskId}/assign` | Assign or reassign a task as a configured task-assignment manager. |
| `POST /api/user-tasks/{taskId}/unassign` | Return a managed assignment to its authored pool behavior. |
| `GET /api/user-tasks/manage` | Page through active tasks authorized by `taskAssignmentRoles`. |
| `GET /api/instances/{instanceId}/user-tasks` | Page through the caller's authorized task history for one instance. |
| `GET /api/instances/inbox` | List active work and caller-specific claim/action capabilities. |

The older instance-addressed `/api/instances/{id}/claim`, `/unclaim`, `/flows`, and `/flows/{flowId}` routes delegate to the same task core. They return `409` when no single active task can be addressed safely.

### Conditions are visibility and execution guards

A condition on a sequence flow leaving a user task is evaluated against values already stored on the workflow instance. For example, `amount > 5000` makes the action visible only while the stored `amount` satisfies that expression. The action endpoint re-evaluates the same condition while holding the instance/task lock, before applying submitted action variables. A caller cannot reveal or execute a hidden flow by posting a new `amount` in that same action.

Invalid or unresolved expressions evaluate to `false`. Task, flow, assignment, claim, and role checks still apply independently.

Inbox membership, ordering, and `TotalCount` come entirely from the database page. Latest variables and multi-instance state are loaded in page-bounded batches, after which flow roles, bypass roles, and conditions refine only the returned task's visible actions and capabilities. If no action is available, the task remains in the page with `canAct=false` and `canClaim=false`; service evaluation never removes or reorders an inbox item.

User tasks can opt into a separate SQL-authoritative membership rule with
`inboxVisibilityCondition`. For example:

```json
{
  "type": "userTask",
  "inboxVisibilityCondition": "[sys.claim.department] == [department] and [amount] + [tax] <= Number([config.approvalLimit])"
}
```

The bounded language supports parentheses, `and`/`or`/`not` (and
`&&`/`||`/`!`), `== != > >= < <=`, numeric `+ - * / %`, unary `+/-`, scalar
variable-to-variable comparisons, and `Number(expr)`. References may address
declared scalar instance variables, supported scalar `sys.*` values,
allowlisted `sys.claim.*`, and any `config.*` or `setting.*` key. PostgreSQL
evaluates the compiled rule before one-per-actor selection, count, ordering,
and paging. Missing/null/wrongly typed values, invalid numeric conversions,
overflow, and division/modulo by zero are `UNKNOWN`; only exact `TRUE` includes
the task. Personal task discovery and actions enforce the same database rule,
while management, distribution, assignment, and administrative operations keep
their existing privileged scope. This rule is independent of an outgoing
sequence flow's `condition`, which continues to control only that action.

String equality is case-insensitive without trimming; ordering is limited to
numbers, `yyyy-MM-dd` dates, and RFC3339 datetimes. Supported fixed context is
`sys.user`, `sys.actingFor`, `sys.now`, `sys.today`, and the instance/workflow/
node IDs and names; `sys.roles` and `mi.*` are intentionally unavailable.
Inside a bracketed reference, write `\]` for `]` and `\\` for `\`.

### Acting without a claim

An action may set:

```json
{
  "canActWithoutClaim": true,
  "canActWithoutClaimRoles": ["Supervisor"]
}
```

The bypass roles are additional to the user task's roles and the sequence flow's normal `roles`. They are checked only when the caller does not own the claim. Consequently, a normal claimant may take the action without a bypass role, while a supervisor may take it unclaimed or despite another actor's claim. An empty or missing `canActWithoutClaimRoles` list preserves the historical behavior: every otherwise-authorized actor may bypass. Claim bypass never overrides direct assignment.

### Capabilities and completion metadata

`UserTaskDto.capabilities` contains `claimedByMe`, `canClaim`, `canUnclaim`, and `canAct` for the current caller. The server derives these flags from active state, assignment, node and flow roles, stored conditions, claim ownership, bypass roles, and multi-instance ownership rules. Clients should render controls from these flags and still handle a locked re-check failure caused by concurrent activity.

Completed tasks expose `selectedFlowId`, `completedBy`, `result`, and `completedAt`. Normal task action history is correlated with both `tokenId` and `userTaskId`. Cancelled tasks do not receive action outcome metadata.
