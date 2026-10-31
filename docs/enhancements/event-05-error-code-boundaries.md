# EVENT-05: HTTP status matching on error boundaries

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Done |
| Category | New event capability |
| Effort | M |
| Priority | Not ranked |
| Depends on | None |
| Related | [TASK-02](task-02-external-task.md), [TASK-01](task-01-call-activity.md) |

## Summary

Allow several `errorBoundaryEvent` nodes on one REST service task, each matching an observed HTTP status or an inclusive status range, with an optional catch-all. For example, route `409` to duplicate handling, other `4xx` failures to data correction, and `5xx` failures to an integration recovery path. An exact status takes precedence over a range that contains it.

This page is the plan this feature was built from, kept for its decisions. See [HTTP status matching](../node-reference.md#http-status-matching) for current behavior, and [implementation evidence](#implementation-evidence) for what was and was not verified.

## Why it adds value

- Different failures need different handling. Today one catch-all boundary must be followed by a gateway that inspects `statusVariable`.
- Matching directly on the failed invocation avoids requiring an output variable just to choose an error path.
- The same selection rules can govern synchronous execution and durable jobs while preserving their retry and transaction contracts.

## First release acceptance criteria

The plan revises these criteria on 2026-10-30. The original blanket ban on overlapping matchers conflicted with the `409` versus `4xx` example. The release rejects ambiguity within the same precedence tier, while deliberately allowing an exact status to override a range on another boundary. It also adds a fix for connections lost while reading a response, because that failure currently escapes the invoker and cannot reach any boundary.

- [x] A REST `serviceTask` supports up to eight attached error boundaries, including at most one catch-all. A `scriptTask` retains its single catch-all; HTTP matchers are invalid on script hosts.
- [x] A boundary's optional `errorMatch` lists integer HTTP statuses and/or inclusive ranges. Absent or `null` means catch-all; a present empty or malformed matcher is invalid and never becomes a catch-all during normalization.
- [x] Selection runs only after a task failure, using structured status metadata from that invocation: exact status, then range, then catch-all. Definition-array order and node IDs cannot decide the winner.
- [x] Duplicate exact statuses across boundaries and intersecting ranges across boundaries on the same host are rejected by both the editor and API. An exact status inside another boundary's range is valid. Each matcher also rejects its own duplicates and redundant overlaps.
- [x] Status `0` is not an HTTP selector. Failures without a usable response status, including current timeout/preflight results and completed responses outside `100..599`, use only the catch-all. A successful task never takes an error boundary; a failed output mapping after HTTP `200` may match a configured `200` boundary.
- [x] A connection lost while reading a response body is a task failure that keeps the received status in both execution modes, rather than an exception that escapes the invoker.
- [x] `boundaryFirst` handles a matching failure before retries; without a match it uses the existing retry/incident path. `retryFirst` considers a match only after automatic retries are exhausted. Selection uses the current attempt's result. The policy stays per host, and its documented limit is that one host cannot route one status immediately while retrying another and then routing it.
- [x] Only the selected boundary's error output and continuation are committed. Existing host failure, variable atomicity, cancellation, shared-variable locking, and durable job fences remain intact.
- [x] Old definitions, exports, and staged jobs with one unfiltered boundary retain their behavior, apart from the lost-connection fix above. No database migration, new endpoint, or new node type is required.
- [x] The shared editor supports multiple boundaries, accessible matcher editing and diagnostics, and visible matcher summaries in authoring and read-only diagrams. It warns when a REST host with matchers has no catch-all, and it offers a way to remove matcher data from a node that cannot use it. Import/export, undo/redo, host movement, and type changes preserve the contract.
- [x] Runtime, validator, compatibility, API, and editor tests pass; real-browser standalone/hosted authoring and Worker-enabled execution are verified; owning documentation and examples are updated before the item is marked Done.

## Later releases

- Named error codes thrown by scripts, including a separately designed script error contract.
- Business-error codes from external tasks ([TASK-02](task-02-external-task.md)) and child instances ([TASK-01](task-01-call-activity.md)).
- Explicit timeout/network/configuration failure selectors. Status `0` remains an output convention, not a substitute for these categories.
- Per-boundary or per-selector retry policy, so a host can route `409` immediately, retry `503`, and then route the exhausted `503`. This release keeps the host-level policy; see [durable processing](#5-durable-processing-persistence-and-locking).
- Error propagation from `errorEndEvent`, subprocess scopes, non-interrupting error boundaries, dynamic expressions, regular expressions, and user-authored matcher priorities.
- Larger boundary counts or a broader event-layout redesign.
- Narrowing service-job output-version fences to only the ultimately selected write set; this release retains the current conservative fencing policy.

## Current state

This describes the repository before the feature was built, on 2026-10-30.

- [WorkflowModel.cs](../../Flowbit/src/Flowbit.Shared/Models/WorkflowModel.cs) exposes `attachedToRef` and `errorVariable` for error boundaries. `errorCode` belongs to `errorEndEvent`; it is not a boundary selector.
- `POST /api/workflows` and `PUT /api/workflows/{id}` bind `WorkflowModel` with ASP.NET's default JSON settings, and Flowbit.Ui's import and hosted-save paths deserialize it with `JsonSerializerDefaults.Web` before posting it again. Testing the proposed shape under those settings showed that numeric strings are read as numbers and unknown members are ignored. Only [WorkflowAuthoringJson.cs](../../Flowbit/src/Flowbit.Shared/Authoring/WorkflowAuthoringJson.cs) is strict today.
- [WorkflowDefinitionValidator.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowDefinitionValidator.cs), through `ValidateErrorBoundary`, limits a host to one error boundary. The editor mirrors that rule in `validateSaveErrorBoundaries`.
- [WorkflowEngineService.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.cs) uses `FindErrorBoundary`, which returns the first attached boundary, and `TaskExecutionOutcome`, which currently contains only success and a reason. The pass-through loop catches service/script failures and follows that single boundary.
- [WorkflowEngineService.Jobs.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.Jobs.cs) uses the same first-boundary assumption in retry decisions, service/script staging, error-output collection, and version fencing.
- The persisted `ServiceTaskResult` already contains `Completed`, `StatusCode`, `Body`, and `Error`. [HttpServiceTaskInvoker.cs](../../Flowbit/src/Flowbit.Infrastructure/Http/HttpServiceTaskInvoker.cs) reports status `0` for preflight failures and timeouts, and a response over the body limit retains the received status, as the comment on `ServiceTaskResult` promises. Caller cancellation is rethrown. A completed response can carry a status above `599`.
- The invoker has a defect on this path. Run against a socket that closes mid-body, it throws `HttpIOException`, which its catch filter does not include. Reading the callers, nothing in the synchronous path converts that exception, so the request should fail with a 500 and no boundary runs; this was not run end to end. The durable stage's generic catch records status `0` and the raw exception message.
- [WorkflowVersionCompatibilityEvaluator.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowVersionCompatibilityEvaluator.cs) already includes attached error-boundary contracts when comparing durable nodes. New matcher configuration must participate in that comparison.
- The [shared editor](../../Flowbit/src/Flowbit.Ui/wwwroot/editor/flowbit-editor.js) hides its add-error-boundary action once a host has one. Its loader explicitly copies supported node properties, so a new matcher needs deliberate import support. Boundary placement, host movement, and deletion already operate over collections. Boundary circles have no canvas caption, only a tooltip, and `nodePosition` spreads them around the whole host border by sibling index, so adding or removing one moves the others. `applyTypeInvariants` runs whenever a node is selected.

## Builds on

- Existing interrupting error-boundary topology: a service/script host, no incoming flow, and one unconditional outgoing flow.
- Existing `statusVariable` and `errorVariable` output validation and transactional writes.
- Durable invocation snapshots/results, retry policy, lease and activation fences, and incident recovery.
- Existing boundary lifecycle records, shared-variable access planning, and version-change preview/apply checks.
- The stable `errorEndEvent.errorCode` vocabulary as context for later named errors only. This release does not reuse that field or change error-end semantics.

## Open questions

The first-release decisions are resolved in this plan:

| Question | Decision |
| --- | --- |
| May `409` coexist with `400–499`? | Yes, on different boundaries: exact wins. Duplicate exacts and overlapping ranges remain invalid. |
| How do retries interact with selection? | Select from each failed outcome; apply the host's existing policy to whether that selection may execute now. See [durable processing](#5-durable-processing-persistence-and-locking). |
| How do timeout/network failures match? | No status selector for `0`; use the catch-all. Do not infer a response from error text or a previously stored variable. |
| Does a `200` matcher mean successful responses take an error route? | No. It is eligible only when that invocation subsequently becomes a task failure, such as a failed output mapping. |
| Are script/business errors included? | Existing script catch-all behavior remains; named codes and new failure categories are deferred. |
| Can one host route `409` immediately, retry `503`, and then route it? | Not in this release. The durable policy stays per host; see [durable processing](#5-durable-processing-persistence-and-locking). Per-boundary retry is a later release. |
| How does a connection lost mid-body match? | Fix the invoker so it is a task failure that keeps the received status, then match that status. |
| Is an explicit `null` selector array valid? | Yes, it means omitted. It cannot widen routing, because a matcher with no selector stays invalid. |
| Where does a diagram show a matcher? | On the boundary's outgoing-flow label. Boundary circles stay captionless. |

## Plan

### 1. Scope and invariants

Implement HTTP status selection on existing error boundaries attached to REST service tasks. Keep current execution modes, normal continuations, error-variable rules, and interrupting behavior. Boundaries gain no authored `cancelActivity`, async flags, or job policy of their own. A handler path named "retry" is ordinary workflow routing; attaching a `5xx` boundary does not itself schedule a job retry. Under the default `boundaryFirst` policy it does the opposite: a matching `5xx` routes on its first failure. See [durable processing](#5-durable-processing-persistence-and-locking).

Limit a REST host to eight error boundaries, including its catch-all. Keep the existing timer/conditional-boundary allowance independent. Verify the combined maximum visually: 28-pixel boundary circles share the 190×88 task perimeter, so sixteen of them sit about 35 pixels apart along the border. If hit targets overlap, adjust placement within this feature; do not silently lower the API limit or enlarge all task shapes. They did overlap: that spacing is measured along the border, and two circles either side of a corner sat about 25 pixels apart. `boundaryPerimeterDistance` now puts a boundary on each corner once the spacing is too tight, which on a task means more than 14 boundaries; positions up to that count did not change.

Preserve these invariants:

1. A failed host activates at most one error boundary. No error-boundary subscription table or competing trigger jobs are introduced.
2. Successful tasks follow their normal outgoing flow, regardless of matching status values.
3. No match has the same effect as no boundary today: synchronous failure rolls back the transition; durable work follows its existing retries and incident handling.
4. Matching does not read `statusVariable`, error strings, response-body JSON, actor claims, or mutable workflow variables.
5. `errorEndEvent` still faults the instance. It does not throw into a boundary attached elsewhere.
6. The cached workflow definition remains immutable. Selection and canonical comparison never sort or modify its live arrays in place.

### 2. Workflow JSON, model, and backward compatibility

Add nullable `FlowNodeModel.ErrorMatch` with proposed shared types `ErrorBoundaryMatchModel` and `HttpStatusRangeModel`. Use a dedicated helper, proposed as `ErrorBoundaryMatchRules`, for limits, validation, canonical ordering, and matching semantics. Mirror the simple rules in the dependency-free editor.

| JSON property | Contract |
| --- | --- |
| `errorMatch` | Optional object, only on `errorBoundaryEvent`. Absent/`null` means catch-all. A non-null object is valid only on a REST service host and must contain at least one selector. |
| `errorMatch.httpStatusCodes` | Optional array of integers from `100` through `599`, inclusive. Omitted or `null` means no exact selectors. |
| `errorMatch.httpStatusRanges` | Optional array of objects with required integer `from` and `to`, both from `100` through `599`, with `from < to`. Both endpoints are inclusive. Use an exact selector for a single status. Omitted or `null` means no range selectors. |
| Selector limit | At most 32 entries in total across both arrays, before deduplication or normalization. An empty array is permitted only when the other array supplies a selector. |

Reject numeric strings, fractional numbers, booleans, null array elements, missing range endpoints, reversed/equal endpoints, out-of-range numbers, duplicate statuses, intersecting ranges, and an exact status inside a range in the same matcher. Reject unsupported matcher/range members rather than silently dropping selectors or turning a misspelling into broader routing. Treat an explicit `null` selector array as omitted: a nullable list cannot tell the two apart, and it cannot widen routing because a matcher with no selector stays invalid.

A typed model does not enforce this alone, because the API and Flowbit.Ui deserialize with web defaults. Declare both attributes on `ErrorBoundaryMatchModel` and `HttpStatusRangeModel`; testing the proposed shape confirmed that each one is needed and works:

- `[JsonNumberHandling(JsonNumberHandling.Strict)]`: web defaults otherwise read `"409"` and `"from": "400"` as numbers.
- `[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]`: web defaults otherwise ignore a misspelled `httpStatusRangez`.

Do not change the policy for unrelated model types. The rules helper still has to handle what deserialization lets through: a `null` range element passes even with both attributes, so guard it rather than fail with a null reference; a missing `from` or `to` becomes `0`, which the range rule rejects; and `{}` has no selectors. Add the endpoints to `Required(Type)` so the strict authoring path names a missing one.

Make authored, AI, and editor structural checks agree, with two harmless differences that stay out of parity fixtures: JavaScript reads `409.0` as `409` while the server rejects it, and web-default binding matches property names case-insensitively while the editor and the strict authoring parser do not.

The following is a **node fragment**, assuming REST host `10` exists. Each boundary also needs its own outgoing flow in a complete definition:

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

Compatibility and normalization:

- Keep the current document format; no new schema version is needed for an additive optional field. Old documents and legacy migrations produce catch-all boundaries without synthesizing empty matchers.
- Update [WorkflowModelMigrator.cs](../../Flowbit/src/Flowbit.Shared/Models/WorkflowModelMigrator.cs), the editor's `normalizeLoadedModel`, export canonicalization, and explicit node-field copies. Preserve valid matchers through save/load and prepare-export without mutating the current document.
- Validate authored misuse before migration can erase it. A matcher on another node type or script host must not silently disappear and pass server validation. Add a `ValidateAuthored` phase for it, which the definition service and AI checks already run before `WorkflowModelMigrator.Normalize`.
- Canonicalize only valid matcher copies: sort exact statuses numerically and ranges by `from`, then `to`; omit empty selector arrays and omit `errorMatch` for catch-all export. Treat absent and explicit `null` as equivalent in compatibility checks. On the server, do this in `WorkflowModelMigrator.Normalize`, which runs before save and on every record read, so runtime and compatibility code never sort a live array.
- Never deduplicate invalid data or convert `{}`, wrong types, or invalid values into a catch-all. A rejected load preserves the current document; invalid draft data accepted by the editor's existing tolerant path remains visible and fails save until corrected. The loader keeps any non-null `errorMatch` on every node type as a deep copy, as it does for `joinCancellation`.
- Keep `errorCode`/`errorDescription` restricted to error ends. Do not introduce string code syntax, `4xx` strings, expressions, or wildcard strings in the JSON. Friendly editor captions still serialize numeric endpoints.

### 3. Validation and deterministic boundary selection

Replace the one-boundary check in `WorkflowDefinitionValidator.ValidateErrorBoundary` and `validateSaveErrorBoundaries` with host-level validation after each boundary's shape/topology is checked. Preserve the existing validator phase order and unchanged ordered errors for unrelated valid/invalid workflows.

For each host:

1. Enforce host support, the eight-boundary limit, and at most one catch-all. Since script hosts cannot have filtered boundaries, they still support only one boundary.
2. Build an exact-status owner map. Two boundaries owning the same exact status are invalid even when they share a continuation.
3. Compare inclusive ranges. Any intersection between ranges on different boundaries is invalid; range width is not a priority. Adjacent `400–449` and `450–499` are valid; `400–450` and `450–499` are not.
4. Allow an exact status on one boundary inside a range on another. A catch-all may coexist with both tiers.
5. Check every boundary's existing `errorVariable` rules and collisions with its service host's status/output targets. Several mutually exclusive boundaries may use the same valid error variable; only the chosen one writes it.

Propose one pure runtime selector, `SelectErrorBoundary(definition, hostNodeId, outcome)`, separate from a collection helper such as `GetErrorBoundaries`. A successful outcome yields no boundary. For a failed outcome, check exact selectors, then ranges, then catch-all. Without a usable HTTP status, skip the first two tiers. Do not use `FirstOrDefault` as an ambiguity policy; corrupt persisted data must fail with a deterministic configuration diagnostic rather than execute an arbitrary branch. The synchronous path reports it as a `WorkflowDomainException` (rollback + 400). A job reports it as a `WorkflowJobInvariantException`, which opens `job_invariant_violation` at once; a `WorkflowDomainException` would be retried and repeat the external call before reaching an incident.

Add `int? HttpStatusCode` to the internal `TaskExecutionOutcome`. A failed service outcome carries the actual `ServiceTaskResult.StatusCode` only when it is in `100..599`; preflight failures, scripts, and status `0` carry no HTTP status. Keep human-readable `Reason` separate. There is no new public error code or persisted outcome DTO.

| Failed outcome | Selection metadata and behavior |
| --- | --- |
| Completed HTTP `409` | Exact `409`, otherwise its containing range, otherwise catch-all. |
| Completed HTTP `503` | Exact `503`, otherwise its containing range, otherwise catch-all. |
| Response over the body limit, or a connection lost while reading the body, after HTTP `503` | Uses `503` even though `Completed` is false. The lost-connection case needs the [invoker fix](#4-synchronous-engine-and-execution-history). |
| HTTP `200` followed by a failed body read, or by mapping/type/status-output failure represented as a task outcome | Uses `200`; an exact/range may handle that task failure. |
| Timeout, preflight failure, or other result with status `0` | Catch-all only. The current invoker returns `0` on timeout even if headers arrived earlier; do not reconstruct a lost status. |
| Completed response outside `100..599`, such as `600` | Catch-all only. `statusVariable` still receives the actual status. |
| Script failure | Catch-all only, preserving current script behavior. |
| Caller cancellation, lost lease, stale activation, output-version conflict, or database/infrastructure exception | Keep existing cancellation/conflict/recovery handling; apart from the invoker's lost-connection fix, do not broaden the exceptions converted into catchable task outcomes. |

Because the service-task HTTP client follows redirects, selection sees the final response, and a `3xx` selector only matches a response the client does not follow, such as `304` or a redirect with no `Location`.

Semantic validation diagnostics identify the boundary and host IDs, offending property/path, conflicting boundary where applicable, and specific invalid value/range. Keep the API's existing error envelope. Wrong JSON types, numeric strings, and unknown members fail earlier, at binding, with no boundary or host ID. Pin both paths with tests rather than promising node-level detail there. The [review](#review-on-2026-10-31) changed what binding failures say: the API's bare 400 and the import page's deserializer message now both give the cause and the JSON path. Full AI structural validation reports structural issues with the established code/path/element/cause format; definition-rule validation keeps its existing first-error contract.

### 4. Synchronous engine and execution history

In [HttpServiceTaskInvoker.cs](../../Flowbit/src/Flowbit.Infrastructure/Http/HttpServiceTaskInvoker.cs), add `IOException` to the transport-failure catch filter. `HttpIOException` derives from it, so a connection lost while reading the body then returns `Completed=false`, the received status, and the existing sanitized `Transport request failed.` reason, as the comment on `ServiceTaskResult` already promises. A timeout during the body still returns status `0`, and caller cancellation is still rethrown.

This also changes existing definitions, on purpose. A synchronous task with a boundary now routes through it, and one without a boundary rolls back with 400 instead of failing with 500. A durable task records the received status instead of `0` and the raw exception message. Land the fix first, with its own test, because it is a defect regardless of this feature.

In `WorkflowEngineService.cs`:

- Populate status metadata in `ExecuteRestServiceTaskAsync`, including unsuccessful responses and failure after a successful response. Keep `FailServiceTaskBeforeInvocationAsync` and script failures status-less. Preserve status-variable write attempts and failure-reason precedence.
- Replace failure routing through `FindErrorBoundary` with the selector. Select once for that outcome and pass the result through the existing continuation path.
- With no match, throw through the current domain-failure path before committing error routing. Staged status/output/history writes roll back with the transition. The external REST side effect remains outside database rollback, as it does today.
- With a match, write only its `errorVariable`, retain the failed host visit, execute the chosen boundary visit and outgoing flow, and skip the host's normal flow. Do not write error variables or visits for unselected boundaries.
- Preserve error-versus-conditional precedence: a matching error boundary cancels the failed host's conditional subscriptions before observing the resulting variable batch. Other catches and sibling tokens continue under existing conditional-drain and runnable-activation checks.
- Reuse current timer/conditional cancellation, gateway-scope handling, node-execution attribution, bounded failure descriptions, and history conventions. Do not add execution statuses, completion reasons, or claim/audit attribution rules.
- Log the result of selection once per failed attempt, as metadata only: instance, host node, HTTP status or none, the selected boundary or none, and the matching tier. Use Information for a selection and Warning for no match, so an operator can tell an unmatched failure from a host with no boundary. Do not put the failure reason, response body, or variable values on this line.

Existing host/boundary node-execution records, boundary flow occurrences, configured status/error outputs, and durable attempt results provide execution evidence. Do not overload node-execution `ErrorCode` with an HTTP status or add a new audit payload solely to record selector internals.

### 5. Durable processing, persistence, and locking

Update `WorkflowEngineService.Jobs.cs` alongside synchronous routing; changing dispatch alone would leave retries and output snapshots incorrect.

| Policy/state | Required behavior |
| --- | --- |
| `boundaryFirst`, current outcome matches | Route immediately, even if retry delays remain. |
| `boundaryFirst`, current outcome has no match | Retry while delays remain; otherwise create the existing incident. |
| `retryFirst`, retries remain | Retry without routing, even when a boundary currently matches. |
| `retryFirst`, retries exhausted | Route the final attempt's match, or open the existing incident if none matches. |
| Explicit `retryDelays: []` | Consider the first failure immediately because there are no automatic retries. |
| Successful retry | Normal continuation; an earlier attempt's match cannot route later. |
| Selected boundary's synchronous continuation fails | Finalization rolls back. While attempts remain, `ScheduleRetryAsync` clears the stored result, so the next attempt calls the service again and selects from its new result, which can reach a different boundary or succeed. With no attempts left, open the existing incident. This is existing job behavior that matchers make more visible. |

The policy belongs to the host, not to a selector, and that limits what a first-release host can express:

- With the default `boundaryFirst`, attaching a `5xx` boundary stops automatic retries for those statuses, because the first matching failure routes.
- With `retryFirst`, a `409` is sent again for every configured retry (three by default) before it routes.
- A host therefore cannot route `409` immediately, retry `503`, and then route the exhausted `503`. The closest `boundaryFirst` model leaves `5xx` unmatched, which retries and then opens an incident.

Document this limit in the node reference and show the trade-off in the durable example.

Implement the following together:

- Rebuild failed outcomes with status in `ApplyStagedServiceResultAsync`, using the persisted `ServiceTaskResult`; do not re-invoke a service to recover matching metadata. Keep its serialized shape unchanged so old staged results remain readable.
- Change `ShouldRetryBeforeBoundary` to use the match for the actual outcome, rather than the existence of any boundary. Evaluate the selector before the retry decision under both policies, including while `retryFirst` still has retries, so corrupt data stops at the first failure. Select consistently for retry choice and eventual continuation; preserve retry counts/delays and incident/manual-retry behavior.
- Replace first-boundary assumptions in `StageAndInvokeServiceJobAsync`, `ServiceOutputTargets`, snapshot expression collection, and error-output target collection with the union of all attached boundaries' potential error outputs. Deduplicate variable names with current case-insensitive rules.
- Audit script staging/finalization call sites too, while retaining script's single catch-all and current selected-write fencing.
- For service jobs, snapshot and fence the union of potential output/status/error variables, including shared versions, extending the existing conservative all-target fence. Changing an unselected boundary's potential output while a call is in flight can therefore cause a conflict. Document and test this limitation; do not relax concurrency guarantees in this feature.
- Keep REST invocation outside the finalization transaction and preserve the current shared-catalog, instance, token, and job lock acquisition order in each phase. Shared access planning covers every reachable boundary continuation before invocation, not just the path later selected.
- Review `SharedVariableAccessPlanner` and `SharedVariableTransactionLockOrderValidator`: they already enumerate boundary collections, but tests must prove later error paths participate in durability and lock-order checks. Preserve the `asyncBefore` requirement for REST transitions touching shared bindings.
- Commit selected output writes, host/boundary records, continuation, subscription cancellation, and job completion atomically. A failed retry-finalization transaction leaves no boundary side effects.
- Keep lease ownership, activation ID, automatic activation count, and wait/job fences authoritative. If a timer, conditional boundary, cancellation, or another valid transition wins while an invocation runs, discard the stale result; do not dispatch an error boundary afterward.
- Preserve `asyncAfter`: matcher changes must not re-run the host or reinterpret downstream continuation failures as fresh failures of the already-completed host.

No tables, columns, constraints, subscription records, migrations, or backfills are planned. Definitions already live in JSONB and job results retain HTTP status. Historical rows and retry schedules are not rewritten. If implementation discovers a persistence change is necessary, revise this section and the deployment/schema work before shipping.

### 6. Version switching, recovery, and rollout compatibility

- `CanonicalNode` serializes the whole node with no allowlist, so `errorMatch` reaches `BoundaryContracts` and `DurableNodeContract` in `WorkflowVersionCompatibilityEvaluator` once it is on the model. `Normalize` sorts selectors on save and on every record read, so selector order cannot change the contract. Prove both with a test rather than assuming them.
- Adding/removing a boundary or changing its matcher, attachment, error output, or outgoing continuation is incompatible while that service activation is protected by an open durable contract. Preview, single-instance apply, and batch version-change paths must agree. Preserve stronger existing open-job blockers.
- Reordering selectors or changing omitted catch-all to explicit `null` compares equal; cosmetic edits retain existing compatibility. Do not equate broader rewrites merely because today's response would select the same handler.
- Preserve rules for instances waiting elsewhere; do not globally block version changes because a definition contains a matcher. Future activations use the selected definition under existing version-switch rules.
- Manual incident retry uses existing snapshots, attempt/fencing rules, and compatible immutable definitions. A fresh activation or supported reactivation may capture a new snapshot; no new recovery action is introduced.
- Upgrade API, Worker, and UI/editor assets together before saving/publishing matcher-bearing definitions. Old readers may ignore an unknown property or choose the first boundary, so mixed-version execution is unsupported. A stale Flowbit.Ui replica also loses data: its import and hosted-save paths deserialize into `WorkflowModel` and post it again, which silently drops `errorMatch`, so a single filtered boundary saves as a catch-all.
- Before rollback, inventory definitions and live work using matchers or multiple boundaries, then use supported replacement/drain/cancel operations before older binaries run. Even a single filtered boundary is unsafe on an old runtime because it could become a catch-all. Do not edit persisted JSON or job rows manually.

### 7. API, authorization, and AI authoring

Use existing workflow-definition create/update/read/publish routes. The only public contract addition is the optional nested workflow JSON property. There is no boundary-management endpoint, permission, runtime DTO, or status filter to add.

- Preserve definition authorization, actor resolution, optimistic concurrency, publish readiness, and execution authorization. Invalid matchers fail before persistence through current validation/deserialization response conventions; valid definitions round-trip through reads and downloads.
- Update model-derived schema handling in [WorkflowAuthoringJson.cs](../../Flowbit/src/Flowbit.Shared/Authoring/WorkflowAuthoringJson.cs), including `Required(Type)` for range endpoints and strict property inspection. Selector arrays are nullable in the generated schema, matching the rule that `null` means omitted. Check development OpenAPI wherever the workflow model is exposed.
- Include matchers in authored-shape checks, definition validation, and AI `validate_workflow`/`validate_item` paths. Item validation can check local shape; it cannot claim host-wide non-overlap without the surrounding definition.
- Update the portable authoring guide/package and readiness coverage. Keep AI agent instructions, tool names, tool descriptions, and the six-tool contract unchanged; this feature does not authorize redesigning them.
- AI tests use mocked provider HTTP. No billed provider run is needed.

### 8. Shared editor and Flowbit.Ui

Keep changes in the canonical [editor assets](../../Flowbit/src/Flowbit.Ui/wwwroot/editor/). Hosted editor, standalone editor, and read-only instance diagrams share rendering and serialization; no separate Razor matcher implementation is needed.

**Host controls and matcher fields**

- Replace the singular `findErrorBoundary` UI assumption and `renderErrorBoundarySection` with a boundary list showing name, matcher summary, and select/remove actions. Keep **Add error boundary** available until the host limit; script hosts retain one catch-all.
- The first added boundary defaults to the existing catch-all. Another defaults to an explicitly empty HTTP matcher that is visibly incomplete and blocks save until configured; never create a second implicit catch-all or invent a business status.
- On a boundary, provide **Catch all failures / Match HTTP status**, exact-status rows, and range rows. Validate integer values without truncation/coercion. Use `attachInspectorHelp()` for precedence, inclusive endpoints, the failure-only `200` example, no-response handling, and host retry policy. Other persistent notes are for invalid/incomplete state.
- Switching to catch-all is an explicit, undoable edit that clears that boundary's matcher. If another catch-all exists, reject the switch with a useful message. Merely selecting/rendering a node must never rewrite matcher data.
- When a REST host has a filtered boundary and no catch-all, show a warning on the host that timeouts, network failures, and unmatched statuses will not be caught. The configuration is valid, so the warning does not block save; it is a state-dependent `.empty-note`, which the inspector help convention allows.
- Invalid matcher data needs a repair path, because it blocks save. A non-boundary node has no matcher fields, so when it carries `errorMatch`, show a warning with an explicit remove action, as `joinCancellation` does for imported values. On a script-hosted boundary, keep **Catch all failures** available so the filter can be cleared.
- Reuse matcher helpers for live/save errors, with labelled controls and linked help/error text. Preserve keyboard focus, native text undo, and editor-root history commits.

**Rendering and graph edits**

- Retain BPMN error-boundary shapes, which stay captionless: text around the host border would collide with the host's own text, its connectors, and neighboring boundaries. Show a compact summary such as `409` or `400–499` on the boundary's outgoing-flow label, ahead of any authored flow name, through `flowLabelInfo`, which already handles placement, tooltips, and viewer rendering. Its `markerWidth` assumes one-character markers, so measure the summary text. Leave `flowRoutingLabelMetrics` unchanged so existing connectors do not move. Keep the full list in the flow and boundary tooltips, their accessible names, and the inspector.
- Show `All failures` for a catch-all only when its host has another error boundary, so existing single-boundary diagrams and documentation images do not change. Malformed matchers display as invalid, never as `All failures`. A boundary with no outgoing flow has no label; that state is already invalid, and the inspector still shows its matcher.
- Avoid obscuring edge anchors and preserve clickable targets with mixed error/timer/conditional boundaries at the maximum count. `nodePosition` derives positions from sibling index, so adding or removing a boundary moves its siblings and reroutes their flows. Viewer mode uses the same summaries without authoring controls. `WorkflowAuthoringLayout` needs no change, because the summaries live on flows.
- Verify host/lane movement, reconnecting flows, deleting a boundary, and deleting its host. Existing collection-based geometry/deletion behavior must work for every boundary and incident flow.
- Update `changeNodeTypeFromInspector`: reject REST service-to-script conversion while filtered error boundaries remain and identify those boundaries. Do not strip filters into catch-alls. A single catch-all may survive the existing service/script conversion. Conversion to an unsupported host follows existing cleanup for every attached boundary and flow.
- Explicit boundary-to-other-type conversion removes matcher fields with the other boundary metadata. Do this in `changeNodeTypeFromInspector`, as it already does for `joinCancellation`. Do not copy the `delete node.errorVariable` pattern into `applyTypeInvariants`: it runs whenever a node is selected, so it would silently repair invalid imported data, which must stay visible.
- Preserve dirty state, saved baseline, undo/redo, rejected-load preservation, AI snapshot/apply checks, and workflow-family identity. Standalone export and hosted save share local checks; hosted rejection leaves the draft intact.

No new operational screen is needed. Inspect existing instance diagrams/history, jobs, and incidents with multi-boundary definitions to verify selected-route and recovery visibility.

### 9. Implementation sequence and review gates

| Step | Work | Exit condition |
| --- | --- | --- |
| 1. Characterize and fix | Pin catch-all routing, status 0, failed 200 mapping, retry policies, snapshot targets, and legacy round trips. Fix the invoker's lost-connection defect with its own test. | Existing behavior and affected first-boundary assumptions are covered before changing them, and a connection lost mid-body reaches task-failure handling. |
| 2. Contract and validation | Shared types/rules, strict JSON attributes, normalization, schema, host overlap checks, and JS parity. | Valid/invalid fixtures agree across API, editor, and AI, apart from the two parsing differences in [the JSON contract](#2-workflow-json-model-and-backward-compatibility). Numeric strings and misspelled members fail through the real API route. Old definitions remain catch-all. |
| 3. Outcomes and selection | Pure selector and synchronous routing, preserving writes and lifecycle records. | Selection matrix, no-match rollback, and selected-only assertions pass. |
| 4. Durable integration | Match-aware retries, all-boundary snapshots/lock planning, replay, fences, and compatibility. | PostgreSQL tests prove atomicity, recovery, old snapshots, and concurrency. |
| 5. Authoring and examples | Multiple-boundary controls, fields, flow labels, missing-catch-all warning, invalid-data repair, graph/type edits, and canonical HTTP examples. | Both editor hosts round-trip definitions and report useful diagnostics. |
| 6. Verification and documentation | Automated suites, browser smoke/Worker acceptance, owning guides, catalog, and rollout review. | Acceptance criteria are evidenced; then mark the item and backlog Done. |

Keep implementation slices reviewable, but release authoring, API validation, and runtime support together. Do not publish usable examples before all executing hosts support the feature.

### 10. Automated tests and real-browser verification

New test-class names below are proposals. Extend existing suites where they already own the behavior.

| Area | Required cases |
| --- | --- |
| Shared rules and JSON | Absent/null catch-all; exact/range/mixed; 100/599 limits; 0/99/600; strings/fractions; null selector arrays treated as omitted; null elements rejected without a crash; missing/unknown members; `{}`/empty arrays; 32/33 selectors; duplicates, reversed/equal endpoints, own overlap; old documents/migration; canonical order and invalid-data preservation. |
| Host validation | Eight/nine boundaries; two catch-alls; script matcher; wrong node; missing/non-REST host; topology violations; same exact on different hosts allowed; duplicate exact on one host rejected; touching ranges rejected; adjacency accepted; exact-over-range accepted; every error-variable contract checked. |
| Pure selection | All endpoints; exact override regardless of node/array order; range then catch-all; no match; unusable status, including `600`; successful task never selects; corrupt ambiguity fails deterministically. |
| Synchronous execution | 409/other 4xx/5xx/fallback/no-match; no status variable; stale stored status ignored; retained status on an oversized body and on a connection lost mid-body after `503` and `200`; timeout/preflight 0; completed `600` uses the catch-all only; failed mapping after 200; status-output failure; selected-only error output/visit/flow; no normal continuation; rollback on handler failure; caller cancellation; selection log lines hold metadata only. |
| Durable retry policy | Both policies × match/no-match × available/exhausted retries; default/empty/custom schedules; changing status across attempts; eventual success; script catch-all regression; incidents/manual retry; asyncAfter does not rerun the host; a failed handler continuation calls the service again and reselects; a corrupt matcher opens `job_invariant_violation` without a retry. |
| Staging/persistence | New and old results/snapshots; restart after result persistence before finalization without duplicate invocation; all potential error outputs captured; shared versions; selected output alone written; unselected output edit produces the documented conservative conflict. |
| Transactions/races | PostgreSQL rollback; concurrent/cancelled finalization; lease recovery; timer/conditional interrupt discards late result; duplicate completion cannot double-route; conditional error precedence; shared-lock ordering through later boundary continuations. |
| Compatibility/API | Matcher/attachment/output/flow changes blocked where required; reorder/null equivalence allowed; cosmetic edits; preview/apply/batch agree; unrelated waiting-node behavior; create/read/update/publish round trip; invalid request writes nothing; numeric strings, misspelled members, and null elements rejected through `POST /api/workflows`, `PUT /api/workflows/{id}`, and the import page's JSON options, not only the strict authoring parser; binding-failure responses pinned; unchanged permissions/concurrency. |
| Editor/AI | Validator parity and ordered errors; loader/export copies; list/add/remove; invalid rows block save; duplicate catch-all switch rejected; flow-label summaries leave connector geometry unchanged; a lone catch-all shows no summary; missing-catch-all warning; selecting a node leaves invalid matcher data untouched; wrong-node and script-host matcher repair; host/type cleanup; service-to-script rejection; undo/redo/baseline; AI schema/package and item versus full-workflow checks. |
| Examples/regressions | New examples validate and run; old REST/script catch-all examples remain valid; timer/conditional/gateway/MI behavior unchanged; viewer labels remain read-only. |

Relevant existing suites under [Flowbit.Tests](../../Flowbit/tests/Flowbit.Tests/) include `DefinitionValidationTests`, `EditorValidatorTests`, `EditorValidatorCharacterizationTests`, `EditorRuntimeSmokeTests`, `EditorViewerModeTests`, `HttpServiceTaskInvokerTests`, `ServiceTaskSharedStatusTests`, `DurableWorkflowProcessorTests`, `NodeExecutionLifecycleApiTests`, `WorkflowVersionCompatibilityEvaluatorTests`, `InstanceVersionChangeApiTests`, `SharedVariableAccessPlanTests`, `SharedVariableRuntimeTests`, `WorkflowAuthoringPackageTests`, and `ExampleWorkflowCatalogTests`. Add focused `ErrorBoundaryMatchRulesTests` and `ErrorBoundaryMatchingRuntimeTests` if this keeps concerns clearer.

Use deterministic HTTP stubs for classification and a fixture-owned loopback endpoint for end-to-end calls. Use a socket that closes mid-body for the invoker's lost-connection test, because that is what raises the real `HttpIOException`. Use existing clock/retry infrastructure and bounded polling, not arbitrary sleeps. In-memory fakes cannot establish transaction, locking, lease, or restart guarantees; use disposable PostgreSQL fixtures for those cases. Preserve the existing caught-failure/error-end and bounded-diagnostic regression tests.

For implementation verification, run from the repository root; these commands work in PowerShell and Bash:

```text
dotnet test Flowbit/tests/Flowbit.Tests/Flowbit.Tests.csproj
```

Then publish fresh hosts and build/install Chromium using the [browser smoke setup](../../Flowbit/tests/Flowbit.BrowserTests/README.md#commands) and [Worker-enabled acceptance setup](../../Flowbit/tests/Flowbit.BrowserAcceptanceTests/README.md). After those prerequisites:

```text
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore
dotnet test Flowbit/tests/Flowbit.BrowserAcceptanceTests/Flowbit.BrowserAcceptanceTests.csproj -c Release --no-build --no-restore
git diff --check
```

Required real-browser scenarios:

- **Standalone and hosted authoring, 1440×900 and 1024×768:** use clicks/typing to add the four example boundaries, enter selectors, trigger/fix duplicate/range/catch-all errors, connect continuations, move host/lane, delete boundaries, test conversions, confirm the missing-catch-all warning appears and clears, repair an imported matcher on a wrong node, and exercise keyboard undo/redo. Save/reload and verify JSON through supported export/API reads. Exercise native file-picker success/cancel manually where available.
- **Hosted narrow layout, 390×844:** inspect fields, help, validation messages, and list controls for clipping and keyboard access. Test the maximum mixed boundary count and inspect readable flow labels/hit targets at useful zoom levels.
- **Read-only instance diagram:** inspect selected boundary routes and matcher labels at desktop/narrow widths; selection/panning must stay read-only.
- **Worker-enabled runtime, 1440×900 and 390×844:** use controlled REST responses 409, 422, 503, a no-response failure, and a connection lost mid-body; show exact/range/fallback routing, unmatched incident creation, both retry policies, a changed status on a later attempt, and manual recovery. Include restart and a late result after an interrupting timer.
- Keep ordinary smoke Worker-free. Add retry/restart/timer-race cases to the separate [acceptance suite](../../Flowbit/tests/Flowbit.BrowserAcceptanceTests/); gates forward actual API responses unchanged. Do not inject UI state or mutate database rows to manufacture outcomes.
- Inspect console errors/warnings and screenshots. Record automated commands/results separately from browser/version, actual localhost URLs, interactions, viewport sizes, console results, and screenshots. Report unavailable checks explicitly.

These were the implementation gates. [Implementation evidence](#implementation-evidence) records the result.

### 11. Documentation and examples to ship with implementation

Current-behavior documentation shipped with the implementation.

| Owner | Required implementation update |
| --- | --- |
| [Node and property reference](../node-reference.md#errorboundaryevent) | JSON, bounds, precedence, hosts, output collisions, failed-200 semantics, no-response behavior, statuses outside `100..599`, followed redirects, retry interaction, the per-host policy limit, and the missing-catch-all hazard. |
| [BPMN support](../bpmn-support.md) | Replace the single catch-all limitation with the HTTP subset; retain interrupting-only and no error-end propagation limitations. |
| [Developer guide](../developer-guide.md) | HTTP-only examples, atomic writes, no-match behavior, retries, version compatibility, and remote-side-effect idempotency implications, including a failed handler path calling the service again. |
| [API reference](../api-guide.md) | Optional workflow JSON, validation examples, binding failures versus validation errors, existing error envelopes and unchanged authorization; compare with DTOs/routes and development OpenAPI. |
| [Flowbit.Ui guide](../ui-guide.md) | Boundary list, match fields/help, flow-label summaries, missing-catch-all warning, invalid-data repair, diagnostics, type-change restrictions, and existing incident recovery flow. |
| [Deployment and operations](../deployment.md) | Coordinated upgrade, stale-UI data loss, mixed-version/rollback restrictions, Worker policies, status-0 fallback, conservative output fences, and recovery. |
| [Runtime reference](../../Flowbit/README.md) and [AGENTS.md](../../AGENTS.md) | Replace single-boundary assumptions in architecture, data model, runtime rules, and BPMN mapping; document outcomes, selection, all-boundary snapshots, locks, immutable contracts, and the invoker's lost-connection handling. |
| [Authoring guide](../../authoring/flowbit-authoring/references/authoring-guide.md) and generated package | Schema, exact-over-range example, limits, and validation guidance. Keep AI tool/instruction wording unchanged. |
| [Example catalog](../../examples/README.md) and affected category READMEs/JSON | Complete synchronous routing and durable retry examples with endpoint setup, actors/inputs, Worker requirement, expected paths, no-response fallback, the per-host retry trade-off, and recovery. Keep existing script catch-all examples valid. |
| Browser suite READMEs | Scenario/viewport coverage, endpoint/Worker prerequisites, and evidence locations. |
| [Enhancement backlog](README.md) and this page | Keep statuses synchronized; record final evidence and file still-wanted later-release work before marking Done. |

Review the [root README](../../README.md) for statements/images made misleading by implementation; change only affected material. The [database schema reference](../database-schema.md) needs no object/migration update under this plan; revise relevant JSON-payload explanations only if their documented contract changes. Update [getting started](../getting-started.md) only if a walkthrough changes. No guide is added or renamed, so documentation-home restructuring is unnecessary.

Validate changed relative links, anchors, and images. Parse this page's fragment as JSON; validate eventual complete examples through catalog tests and real HTTP runtime. Keep runnable instructions equivalent in PowerShell and Bash.

### 12. Completion checklist

- [x] Final model/runtime behavior agrees with the decisions and selection/retry tables.
- [x] Old documents and persisted results/snapshots remain compatible; no migration is required, or the plan has been revised before shipping one.
- [x] First-boundary assumptions are removed or deliberately retained only for script catch-all behavior.
- [x] The invoker's lost-connection fix shipped with its test, and its effect on existing definitions is documented.
- [x] The per-host retry limit and the missing-catch-all hazard are documented.
- [x] Unit/integration, API, editor, and PostgreSQL race/recovery checks pass with recorded commands/results.
- [x] Standalone, hosted, viewer, and Worker browser scenarios have separate evidence, screenshots, and console results; gaps are explicit.
- [x] Owning guides, authoring package, and runnable examples describe shipped behavior and limitations.
- [x] Coordinated upgrade/rollback checks are reviewed; feature-bearing definitions are not exposed to older runtimes.
- [x] Only after acceptance criteria are met, mark this page and the backlog Done and record implementation evidence.

## Implementation evidence

Recorded on 2026-10-30 and brought up to date on 2026-10-31, after the [review](#review-on-2026-10-31), on the working tree before commit. Commands ran from the repository root, with one suite running at a time.

| Check | Result |
| --- | --- |
| `dotnet test Flowbit/tests/Flowbit.Tests/Flowbit.Tests.csproj` | 3,520 of 3,520 passed. |
| `dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore` | 97 of 97 passed in Chromium, including E14 to E17 at 1440×900 and 1024×768, H12 at 1440×900, 1024×768, and 390×844, and E18, H13, and H14 at 1440×900. Scenarios fail on a console error or an unexpected dialog. |
| `dotnet test Flowbit/tests/Flowbit.BrowserAcceptanceTests/Flowbit.BrowserAcceptanceTests.csproj -c Release --no-build --no-restore` | 83 of 83 passed with a real Worker, covering real `409`, `422`, `503`, a lost body, a timeout, both retry policies, a Worker stopped in the middle of a call, a late result after an interrupting timer, a condition on the status variable, and incident recovery through the Jobs & incidents page, plus the hosted editor and read-only diagram at 1440×900, 1024×768, and 390×844. |

The first acceptance run on 2026-10-30, with other suites running at the same time, passed 61 of 71. Eight of the failures were a test timing problem with animated scrolling in `InstanceDetailAcceptanceTests`, which also failed on the previous commit (`ae033cb`) and is now fixed; see the [acceptance suite README](../../Flowbit/tests/Flowbit.BrowserAcceptanceTests/README.md). One was an expression-timer scenario that passed when rerun alone, and one was the `lost-503` race described below.

### Review on 2026-10-31

A review against this plan, after the page was first marked Done, found the problems below. All are fixed, and each has a permanent test.

- **Error versus conditional precedence.** [Section 4](#4-synchronous-engine-and-execution-history) required this precedence and [section 10](#10-automated-tests-and-real-browser-verification) a test for it, but a durable host did not have it. Routing observed the stored status before it selected a boundary, so a conditional boundary reading `statusVariable` beat a matching error boundary, and a failure with no match and no retries left completed through that condition and opened no incident. The previous commit did the same with a catch-all. A resumed failure now holds the conditional drain until the loop has selected a boundary and cancelled the host's conditions, or thrown.
- **Hosted boundary list.** The hosted page also loads Bootstrap, whose `.badge` stopped the summary wrapping, so it ran up to 53 pixels under **Remove** and was cut off at 390 wide. Standalone was fine, and the suites looked only there. `.flowbit-editor .badge` now sets what Bootstrap would otherwise set. This also changes the other inspector badges in hosted mode: they were bold and centred, and now match standalone.
- **Keyboard focus.** Removing or opening a boundary from the host's list dropped focus to the page body, where the hosted editor's shortcuts do not apply. Focus now moves to the next row, or to the boundary's heading.
- **Retry policy for a script write.** A durable script whose write failed during finalization skipped the host's policy: `retryFirst` routed at once, and no selection line was logged. It now behaves like any other failed attempt. This is a small behavior change on a rare path.
- **Log wording.** A `retryFirst` attempt that was about to retry logged `selected`. It now says the failure matches a boundary but will retry first.
- **Binding failures.** The API returned `400` with an empty body outside Development, and the import page named .NET types and left the path out for an unknown property. Both now give the cause and the JSON path, through `JsonReadError`. The API change reaches every route, because the setting behind it is global: a request that cannot be bound now returns its status with `{"error": ...}` everywhere.
- **OpenAPI.** `from` and `to` were not marked required, and two internal comments appeared as public descriptions.
- **Harness races.** `lost-503` failed now and then because the test's mock could reset the connection before the client had read its headers. The client then never saw a status and correctly used the catch-all, so the mock now waits first. H3 navigated away as soon as the URL changed, while the editor's unsaved-changes guard was still in place, and now waits for the editor to be removed.
- **Script error variable.** A durable script whose write was refused at finalization wrote its boundary's error variable without checking that variable's snapshot version, despite the script audit and the concurrency guarantees in [section 5](#5-durable-processing-persistence-and-locking), so a concurrent change was overwritten. It now opens `output_version_conflict`, as a script that throws already did, and a retry works from a new snapshot. A refused write needs a binding that disagrees with its catalog entry, which save and publish refuse, so only a definition stored another way reaches it.
- **Partial batch.** The same refusal committed the batch's instance writes and any shared write ordered before the refused one, because following a boundary commits the transaction. `WorkflowVariableStore.WriteAsync` now checks every shared value against its locked catalog row before it stages anything, which also covers REST outputs and synchronous scripts.
- **Import race.** The `/workflows` page showed a new file's name before reading the file and kept the previous contents importable, so a quick click imported, and by default published, the previous file with no error. The page now removes the previous file at selection, ignores a read that a newer selection has overtaken, and locks the picker during an import. H13's wait for the file name, added earlier in this review, had hidden the race; it is sound now because the name appears only after the read, and H14 covers the race itself. This was not caused by EVENT-05.
- **Inspector names.** **Failures caught** was an unnamed combobox with no link to its help, as were most inspector controls: 13 of 15 on a REST host. `linkInspectorLabels()` now names every field from its label after each render, `attachInspectorHelp()` adds the panel to the control's `aria-describedby`, and the service **Body** has its own name. E18 checks every node type. This also changes what a click on a field's label does: it now focuses the control.

The review also added the required tests that had been missing: `retryFirst` with no match, rollback when a handler path fails synchronously, a failed status write with a matcher attached, a worker that lost its lease, version-change preview, apply, and batch with matchers, AI checks on bad matchers, undo and redo of matcher edits, selection log lines, the OpenAPI schema, and the `/workflows` import page.

**Not verified:** the native file picker's success and cancel paths, which need a person. Saves in the suites use the download fallback.

**Later releases** stay listed on this page. No separate backlog items were filed for them.
