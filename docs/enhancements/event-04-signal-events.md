# EVENT-04: Signal events

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Done |
| Category | New events |
| Effort | L |
| Priority | Not ranked |
| Depends on | None |
| Related | [EVENT-02](event-02-message-correlation-by-business-key.md), [EVENT-03](event-03-event-based-gateway.md) |

## Summary

Signal start, intermediate catch, boundary, intermediate throw, and end events. Throwing a signal by name, from a workflow or through the API, reaches every subscribed instance, optionally limited to one workflow family or business key.

The first release adds the two catching events and a broadcast through the API. Throwing from a workflow and starting instances from a signal come later.

## Why it adds value

- Broadcast situations such as "policy changed", "product recall", or "fiscal year closed", where many open instances must react at once.
- Today the only way to wake several instances is to deliver a message to each one by instance ID, which the caller must have stored.
- A signal boundary lets one broadcast interrupt work that is already open, or add a step beside it, without the sender knowing which tasks exist.

## First release acceptance criteria

These criteria are revised by this plan on 2026-11-16. The revision names the two node types, states exactly which waits a broadcast reaches, and adds authorization, limits, failure handling, and the rules for versions and cancellation. A review of the plan against the code on the same day added lowercase names, the explicit scope, the limit on what a client reads, the token limit, and the cost rule for workflows without signals.

The work ships in two stages, described in [section 13](#13-delivery-sequence-rollout-and-risks). The item is Done when both are.

- [x] `intermediateSignalCatchEvent` and `signalBoundaryEvent` carry a `signal` object with a literal lowercase `name` and optional typed `outputMappings`. The API and the editor apply the same rules with the same wording.
- [x] A catch subscribes when a token arrives on it, and a boundary when its host is entered. The subscription ends in the same transaction as the wait: when a signal is delivered to a catch or an interrupting boundary, or when the host completes, is interrupted, or is cancelled.
- [x] `POST /api/signal-broadcasts` throws a signal by name with an optional JSON payload. The request names one workflow family or states explicitly that it addresses all of them. A user needs a role from `Signals.RequiredRole`. A managed API client needs the `signals.throw` scope, and it reads, cancels, and retries only the broadcasts it threw. A repeated request with the same idempotency key creates no second broadcast.
- [x] The throw freezes its recipients in one statement: the subscriptions that are committed and active at that moment. A wait that starts later does not receive the signal, and a throw that would exceed the configured limit is rejected, not truncated.
- [x] The Worker delivers through paged durable jobs. Each frozen delivery ends exactly once as `delivered`, `skipped`, `failed`, or `cancelled`. Retries, duplicate leases, and restarts cannot deliver one broadcast twice to one subscription.
- [x] A delivery maps the payload through the node's typed mappings, writes the values, and moves the token in one transaction. A catch continues along its flow, an interrupting boundary cancels its host, and a non-interrupting boundary adds a sibling token and stays subscribed. A payload that fails a mapping fails only that delivery, writes nothing, and leaves the instance waiting.
- [x] Delivery progress is visible through the API and on a **Signals** page: counts per outcome, each delivery with its instance and reason, cancellation of undelivered work, and a retry of failed deliveries.
- [x] Throwing never waits for an instance's transaction, a delivery locks one instance in the established lock order, and no external call runs inside a delivery transaction.
- [x] A workflow without signal events pays nothing: its transitions run no additional statement, and a load comparison confirms it.
- [x] Version changes, reactivation, instance and scope cancellation, retention, lifecycle capture, and the shared-variable lock-order proof handle signal subscriptions and the new job kind.
- [x] The editor draws both events, authors them, including a boundary on its host, and shows them in the read-only instance diagram. Instance detail lists the signals an instance is waiting for, including armed boundaries.
- [x] Existing workflow JSON and HTTP contracts keep their behavior. The schema change is additive, and the rollout order is documented.
- [x] Documentation, examples, the portable authoring package, automated tests including PostgreSQL race tests, and real-browser verification with a running Worker are complete before this item is marked Done.

## Later releases

Each item is a follow-up subtask with its own page. The list is in the [recommended order of implementation](README.md#event-04-follow-up-subtasks), which also gives the reasons for the order.

1. [EVENT-04A](event-04a-signal-scope-by-business-key-or-instance.md): limiting a signal to one business key or one instance. Done.
2. [EVENT-04B](event-04b-signal-throw-and-end-events.md): intermediate signal throw events and signal end events. A throw from a workflow creates the same broadcast inside the throwing instance's transaction.
3. [EVENT-04C](event-04c-signal-start-events.md): signal start events.
4. [EVENT-04D](event-04d-webhook-events-for-signal-broadcasts.md): lifecycle webhook event types for broadcasts.
5. [EVENT-04E](event-04e-signal-broadcast-expiry.md): an expiry time for a broadcast, after which undelivered work is skipped.
6. [EVENT-04F](event-04f-signal-catalog.md): a catalog of signal definitions with declared payload contracts and permissions per signal.
7. [EVENT-04G](event-04g-parallel-signal-delivery.md): parallel delivery of one large broadcast, and a higher recipient limit.
8. [EVENT-04H](event-04h-expression-signal-names.md): a signal name built from an expression, such as a name that contains an order number.
9. [EVENT-04I](event-04i-signal-boundaries-on-multi-instance-bodies.md): signal boundaries on multi-instance service, script, and call activities.
10. [EVENT-04J](event-04j-shared-variables-in-signal-mappings.md): shared-variable aliases in signal mappings.
11. [EVENT-04K](event-04k-rename-managed-api-clients.md): a general name for managed API clients, which this release still stores and manages as shared-variable clients.

Signal catches behind an event-based gateway are recorded under [EVENT-03](event-03-event-based-gateway.md#later-releases).

## Current state

Implemented and verified on 2026-11-16: done. Both stages shipped together, and the first release acceptance criteria are met. A review on 2026-11-17 found the gaps listed under [Review follow-up](#review-follow-up), which are fixed.

- **Model and rules.** `BpmnFlowNodeTypes` in [WorkflowModel.cs](../../Flowbit/src/Flowbit.Shared/Models/WorkflowModel.cs) adds `IntermediateSignalCatchEvent` and `SignalBoundaryEvent` with `IsSignalCatch`, `IsSignalBoundary`, `IsSignalEvent`, and `IsObservableBoundary` (timer, conditional, and signal boundaries, which share the limit of eight). [SignalEventRules.cs](../../Flowbit/src/Flowbit.Shared/Models/SignalEventRules.cs) owns the lowercase name rule, the mapping limit, the boundary name rules (`BoundaryNameProblems`), and `HasSignalEvents`, the per-definition flag behind invariant 9. `WorkflowDefinitionValidator` adds `ValidateSignalCatch` and `ValidateSignalBoundary`; the limit and `cancelActivity` messages were reworded in both validators. `WorkflowModelMigrator` keeps a boundary's `attachedToRef` and defaults its `cancelActivity` to true.
- **Persistence.** `AddSignalEvents` creates `signal_subscriptions`, `signal_broadcasts`, `signal_deliveries`, and `signal_broadcast_jobs`, seeds `Signals.RequiredRole`, `Signals.MaxSubscriptionsPerBroadcast`, and `Signals.MaxOpenBroadcasts`, adds `signalReceived` to the completion-reason check, and replaces `capture_workflow_incident_lifecycle()`; `ValidateSignalEventConstraints` validates the new checks outside a transaction. Its `Down` refuses while signal state exists. Stage B needed no migration. The retention category is `signalBroadcasts`.
- **Runtime.** `EnsureSignalSubscriptionsAsync` subscribes a catch on arrival and a host's signal boundaries on entry, after the conditional re-check; every path that ends a wait cancels them, guarded by `HasSignalEvents` except instance cancellation. `SignalBroadcastService` freezes recipients in one statement under the open-broadcast and recipient limits and replays an idempotent throw; `SignalBroadcastJobProcessor` delivers one definition's recipients in pages of 100. `DeliverSignalAsync` continues a catch (`caught`), interrupts a host through `InterruptBoundaryHostAsync`, the sequence shared with the conditional capture (`interrupted`), or adds a branch within `Workflow.Gateway.MaxActiveTokens` (`branched`); a conditional boundary may take the token first (`superseded`). Payload, rejection, token-limit, invariant, and call-chain failures fail only their own delivery, and an exhausted lease fails a job's pending deliveries.
- **Versions, reactivation, and analyses.** `ValidateOpenSignalSubscriptions` and `ValidateAttachedSignalBoundaries` add the blockers `open_signal_subscription_node_missing`, `open_signal_subscription_contract_changed`, and `attached_signal_boundary_contract_changed` and the warning `signal_mapping_contract_changed`; `DurableNodeContract` includes signal boundaries. Reactivation blocks on active subscriptions, and the lock-order proof treats a catch and a boundary as external resting points.
- **API and clients.** `SignalEndpoints` adds the seven routes and `FlowbitOperationCatalog` documents them; managed clients gain the `signals.throw` and `signals.read` scopes and see only their own broadcasts, without result descriptions.
- **Flowbit.Ui.** The **Signals** page throws, reviews, polls, cancels, and retries; instance detail lists **Waiting for signals** with armed boundaries and links history rows to their broadcast; the diagram names a signal event's signal and interrupting flag.
- **Editor.** Both types with their shapes, the dashed non-interrupting ring, **+ Add signal boundary event** on durable hosts, the **Signal** section with live name problems and payload mappings, invariants, and save validation that gives the server's first error for every case in `SignalEventFixtures`.
- **Documentation and examples.** The node reference, BPMN support, API, developer, UI, deployment, database schema, engine settings, and AI authoring guides, both READMEs, AGENTS.md, the portable authoring guide, and the browser-suite READMEs. The examples are [01-signal-catch-policy-change.json](../../examples/signals/01-signal-catch-policy-change.json) and [02-signal-boundaries-recall.json](../../examples/signals/02-signal-boundaries-recall.json); the catalog has 71 workflows and 26 node types.
- **Tests.** `SignalRuntimeTests` (26 PostgreSQL cases: subscription lifetime, throw scope and limits, idempotency, both boundary paths, the token limit, payload and invariant failures, cancel and retry, client visibility, versions, a throw beside a locked instance, delivery racing cancellation, and a command-capturing test that a workflow without signals issues no `signal_` statement), `SignalEventRulesTests`, `SignalEventFixtures` through both validators, `EditorSignalEventTests`, `SignalsPageTests`, and `SignalFormattingTests`. A full run of Flowbit.Tests passed 5,559 of 5,566 cases; the seven failures, two of them pinned counts since updated, all pass alone.
- **Browser.** The smoke suite passed 138 of 139 cases in 17 minutes; the one failure, H16, had a broken regular expression and passed when rerun alone. That includes E18 with both signal events in the census, E23 at 1440×900 and 1024×768, H16, and R16 at three sizes. The Worker acceptance scenarios S1 to S5 passed: S1 and S2 at 1440×900 and 390×844, and S3, S4 (cancellation with the Worker stopped, then an expired lease recovered without a duplicate), and S5.
- **Performance.** `tools/InstanceLoadTest` against an isolated PostgreSQL 17 container, with both builds published to `D:`, ran 20,000 starts of a workflow without signal events after a 1,000-start warm-up, alternating builds: `master` 65.8 and 62.0 starts per second (mean 63.9), this change 62.8 and 68.6 (mean 65.7), so no cost is measurable within the run-to-run spread. A throw froze 10,000 recipients in 493 ms and 100,000 in 2,202 ms, well inside the 30-second command timeout, and cancelling the 100,000-recipient broadcast took 1,753 ms. One delivery job delivered 10,000 recipients in 498 s, 20.4 per second, with none failed or skipped, on a two-core machine with Warning-level Worker logging.
- **Manual checks.** In the built-in browser against a disposable local stack (the published API, Worker, and UI with PostgreSQL 17), the canonical example 02 was published and started. Instance detail listed both armed boundaries; a price update thrown from the **Signals** page with a typed name, family, and payload completed within seconds as `Boundary added a branch`, and a recall as `Boundary interrupted its host`. Instance detail then showed both branches, the mapped `unitPrice` and `recallReason`, and history rows linking each broadcast; the console held only Blazor connection messages. The check found that the confirmation read "froze 1 recipients"; it now uses the singular.

### Review follow-up

A review of the implementation on 2026-11-17 found these gaps. Each is fixed and covered by a test; the two runtime tests were confirmed to fail with their fix removed.

- **Administrative timer override.** Firing a timer boundary from `/administrative-actions` left the host's signal and conditional boundary subscriptions active, because the token then moves on from a timer boundary, which skips the usual host cleanup. Later broadcasts failed their delivery to it, and version changes and reactivation were blocked by a wait that no longer existed. `ExecuteAdministrativeTimerBoundaryAsync` now cancels both, as `FireTimerBoundaryAsync` does (`AdministrativeTimerBoundaryOverrideApiTests.Override_EndsTheHostsSignalAndConditionalSubscriptions`).
- **Recount racing a cancel or retry.** The delivery job recounted in one autocommit statement, which waited for a cancel's row lock and then wrote a status from its older snapshot, so a cancelled broadcast could become `completed` again; a retry could likewise be overwritten. The job now recounts in a transaction that locks the broadcast row first, and `RefreshCountsAsync` refuses to run outside one (`SignalRuntimeTests.RecountWaitingForACancel_KeepsTheCancelledStatus`).
- **Backlog gauge.** The pending-deliveries gauge counted the whole `signal_deliveries` table every five seconds. Only a delivering broadcast has pending deliveries, so the count now starts from the open broadcasts and reads each one's `(BroadcastId, Status, Id)` index. No migration was needed.
- **Signals page.** The review now shows the payload, and editing the payload ends the review, as editing the name or scope did; a throw whose payload differs from the reviewed one is refused. An identity change or a `401`/`403` also clears the draft. A late refresh, delivery page, or history page for a broadcast, filter, or page that is no longer shown is dropped (`SignalsPageTests`).
- **API contract.** A retry without `expectedUpdatedAt` returns `400`, not `409`. A retry moves each delivery to the version its instance runs now, so it no longer fails with `500` when the throw-time version was deleted; deleting a version is refused while one of its deliveries is pending. A `pageSize` of 0 or less becomes 1, as documented. A blank `workflowKey` is refused instead of meaning all workflows. A repeated `idempotencyKey` replays before the family's existence is checked. A managed client no longer sees who cancelled its broadcast or why.
- **Validator parity.** The editor reported the eight-boundary host limit before the first boundary's own problems, and named the first declared shared alias where the server names the first referenced one; two `SignalEventFixtures` cases cover each. `SignalEventRules.FindProblems`, which nothing used, is removed, and a test now holds the editor's name pattern to `SignalEventRules.NamePattern`.
- **Tests added.** The recipient, open-broadcast, and payload limits; a user without the role, a client without the scope, and both credentials at once; `signalBroadcasts` retention; a boundary on an `asyncBefore` task; a terminate end ending another branch's wait; a retry after a version change; and the editor's limit note at eight boundaries.
- **Documentation.** The [example catalog](../../examples/README.md) counts 71 workflows and 26 node types, [Example 02](../../examples/signals/README.md) has PowerShell and Bash throw commands, and the unsubscribe table and section 7 above are corrected.
- **Verification.** Flowbit.Tests filtered to the editor, signal, retention, administrative, definition, boundary, example, authoring, OpenAPI, and lifecycle-capture tests passed 2,034 of 2,035. The one failure, `RetentionRepositoryTests.MigrationDowngradeCannotRemovePermanentPruningMarkers`, failed because signal rows left by earlier tests met the `AddSignalEvents` down guard; the test now clears them first and passes beside the signal tests. In the built-in browser against a disposable stack (published API, Worker, and UI with PostgreSQL 17), with two instances of example 02 waiting:
  - **Review.** The review showed the payload. Editing it and clicking **Throw signal** at once returned the page to **Review signal** and threw nothing, and the reviewed edit (`unitPrice` 99) reached both instances.
  - **Identity change.** Applying another identity in a second tab emptied the first tab's draft and closed its open broadcast.
  - **Viewports.** At 390×844, a long payload wrapped in the review with no horizontal overflow, and the recall interrupted both hosts. At 1024×768 and 1440×900 the layout held.
  - **Console and logs.** The console held only Blazor connection messages, and the UI and Worker logs had no errors.

  The Playwright suites were not rerun.

### Facts the plan rested on

Verified on 2026-11-16, before implementation:

- **Documentation.** [Unsupported capabilities](../bpmn-support.md#unsupported-capabilities) lists signal events. [Messages](../bpmn-support.md#messages) address one instance by its ID, so nothing wakes several instances at once.
- **Model.** `BpmnFlowNodeTypes` in [WorkflowModel.cs](../../Flowbit/src/Flowbit.Shared/Models/WorkflowModel.cs) has 24 types and none for signals. Node type columns hold 64 characters, and job kinds and token wait states have no value check.
- **Subscriptions.** A conditional boundary has a row in `conditional_boundary_subscriptions`, fenced by instance, host token, host activation, and boundary node. `EnsureAttachedConditionalBoundarySubscriptionsAsync` creates the rows at the top of each routing step in `ResolvePassThroughAsync`. `CancelAttachedBoundaryWaitsAsync` cancels them when the host exits, and `CancelDurableWorkForTokensAsync` when work is torn down. A token on a message catch or an untriggered conditional catch rests with no wait state.
- **Boundary rules.** `ValidateObservableBoundary` serves timer and conditional boundaries: the allowed hosts, no incoming flow, one unconditional outgoing flow, and `BoundaryEventRules.MaxObservableBoundariesPerHost` (eight).
- **Cost of ending a wait.** `CancelAttachedBoundaryWaitsAsync` runs on every host exit of every instance and issues its three statements whether or not the definition has boundaries.
- **Limits and normalization.** `Workflow.Gateway.MaxActiveTokens` is checked only when a gateway splits; tokens that non-interrupting boundaries add are not counted. `WorkflowModelMigrator` keeps `cancelActivity` only on timer and conditional boundaries and clears it elsewhere.
- **Message delivery.** `DeliverMessageAsync` resolves the typed mappings with `ResolveTypedOutputs` before it writes anything, writes the batch, lets conditional events observe it through `ResumeForVariableChangesAsync`, and advances the token only if `IsSameRunnableActivation` still holds.
- **Batches.** The three batch families freeze their selection in the create request. Their jobs run on the `activity` queue: one leased job loops over pages of 100 items with a new scope and transaction per item, and has four attempts. A job row must name a workflow definition and a node, so a variable-update batch that spans versions enqueues one job per represented definition and records them in `instance_variable_update_batch_jobs`. Batch headers and items belong to no retention category.
- **Worker routing.** Every continuation a job starts passes `forceDurableActivities`, so a service or script task after it becomes a job of its own.
- **Machine clients.** `shared_variable_clients` stores clients with a fixed set of two scopes. One authorization requirement accepts either a JWT user with a role from an engine setting or a client with a scope.
- **Locks and foreign keys.** `GetInstanceForUpdateAsync` holds instance and token rows `FOR UPDATE`. An insert with a foreign key to such a row waits for that transaction, as the [call activity plan](task-01-call-activity.md#5-transaction-and-lock-protocol) recorded.
- **Gates and checks.** `WorkflowDefinitionReadinessChecker.RequiresDurableProcessing` decides which definitions need the Worker. `CK_node_executions_completion_reason` lists every allowed completion reason. `LifecycleEventCaptureTests.JobKindReportsNode` must classify every job kind.
- **Editor.** Node types, boundary types, icons, and search names are explicit lists. Boundaries are created from buttons in the host's inspector, and the loader keeps only the properties it names.
- **Flowbit.Ui.** `ExecutionPositionText` describes waits, and `/administrative-actions` is the model of a guarded, polling administration page. No page throws or lists signals.
- **Browser suites.** The smoke suite runs with durable publication disabled, so a definition that needs the Worker cannot be published there. The acceptance suite runs Workers.

## Builds on

- Durable, paged Worker fan-out: [AdministrativeActionBatchJobProcessor.cs](../../Flowbit/src/Flowbit.Service/Services/AdministrativeActionBatchJobProcessor.cs) with `WorkflowJobProcessorRouter`, [InstanceVersionChangeBatchJobProcessor.cs](../../Flowbit/src/Flowbit.Service/Services/InstanceVersionChangeBatchJobProcessor.cs) for cursor paging that a failing item cannot starve, [InstanceVariableUpdateBatchService.cs](../../Flowbit/src/Flowbit.Service/Services/InstanceVariableUpdateBatchService.cs) for one job per represented definition, and [WorkflowJobRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/WorkflowJobRepository.cs) for leasing and exhausted-lease reconciliation.
- The subscription and fencing patterns of conditional boundaries: [WorkflowEngineService.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.cs) (`EnsureAttachedConditionalBoundarySubscriptionsAsync`, `CaptureConditionalBoundaryAsync`), [WorkflowEngineService.Jobs.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.Jobs.cs) (`CancelAttachedBoundaryWaitsAsync`, `CancelDurableWorkForTokensAsync`, `FireTimerBoundaryAsync`), and [ConditionalBoundarySubscriptionRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/ConditionalBoundarySubscriptionRepository.cs).
- Message catch delivery: `DeliverMessageAsync`, `ResolveTypedOutputs`, `MessageOutputMappingModel`, and `ValidateTypedOutputMappings` in [WorkflowDefinitionValidator.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowDefinitionValidator.cs).
- Managed API clients: [SharedVariableClientAuthentication.cs](../../Flowbit/src/Flowbit.Api/Auth/SharedVariableClientAuthentication.cs), [SharedVariableAuthorization.cs](../../Flowbit/src/Flowbit.Api/Auth/SharedVariableAuthorization.cs), and `SharedVariableClientScopes` in [SharedVariableDtos.cs](../../Flowbit/src/Flowbit.Shared/Dtos/SharedVariableDtos.cs).
- [WorkflowVersionCompatibilityEvaluator.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowVersionCompatibilityEvaluator.cs), [SharedVariableTransactionLockOrderValidator.cs](../../Flowbit/src/Flowbit.Service/Services/SharedVariableTransactionLockOrderValidator.cs), [WorkflowDefinitionReadinessChecker.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowDefinitionReadinessChecker.cs), and [RetentionRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/RetentionRepository.cs).
- The [canonical editor](../../Flowbit/src/Flowbit.Ui/wwwroot/editor/flowbit-editor.js), its [template](../../Flowbit/src/Flowbit.Ui/wwwroot/editor/editor-template.js), and its [styles](../../Flowbit/src/Flowbit.Ui/wwwroot/editor/flowbit-editor.css); [AdministrativeActions.razor](../../Flowbit/src/Flowbit.Ui/Components/Pages/AdministrativeActions.razor) and [ExecutionPositionText.cs](../../Flowbit/src/Flowbit.Ui/Components/Shared/ExecutionPositionText.cs).
- The [browser smoke suite](../../Flowbit/tests/Flowbit.BrowserTests/README.md) and the [Worker acceptance suite](../../Flowbit/tests/Flowbit.BrowserAcceptanceTests/README.md).
- Message starts as the model for instances started by the system, for the later signal start event.

## Open questions

No question remains open. The plan decides the three original questions and the ones it raised.

| Question | Decision |
| --- | --- |
| What happens to an instance that subscribes while a fan-out is in progress? | It does not receive that signal. The throw freezes its recipients in one statement, and a subscription committed after that statement is never added, however long delivery takes. A signal is not stored for later waits. |
| Which machine-client scopes allow throwing signals? | `signals.throw` to throw, cancel, and retry, and `signals.read` to read broadcasts and their deliveries. Both are new scopes on the existing managed API clients. A JWT user needs a role from the new `Signals.RequiredRole` setting instead. |
| What can a client see and change? | Only the broadcasts it threw: it lists, reads, cancels, and retries those, and any other broadcast returns `404`. It receives result codes without the engine's description, which can quote a validation expression of the receiving workflow. The subscription summary is for users only. |
| What are the fan-out limits? | At most `Signals.MaxSubscriptionsPerBroadcast` recipients (default 10,000, hard cap 100,000), at most `Signals.MaxOpenBroadcasts` unfinished broadcasts (default 20, hard cap 1,000), and a payload of at most 256 KiB. A throw over a limit is rejected. The open-broadcast limit is a soft threshold that concurrent throws may pass by a few. A delivery that would add a token beyond `Workflow.Gateway.MaxActiveTokens` fails. |
| What is a signal name? | A literal `signal.name`: 1 to 100 lowercase letters, digits, `.`, `_`, or `-`, starting with a letter or digit, compared exactly. Uppercase letters are rejected when a definition is saved and when a signal is thrown, so a throw cannot miss its waits because of letter case. There is no catalog of signals in this release; the subscription summary shows which names have waits. |
| Must a throw name its scope? | Yes. It gives a `workflowKey` or sets `allWorkflows` to true: exactly one. Unrelated workflows can choose the same short name, and a throw without a scope would reach all of them unnoticed. The response lists the recipients per workflow family. |
| Where are recipients frozen: in the request or in a Worker job? | In the request's transaction, as batches freeze their selection. The caller then gets the recipient count at once, and "existed when thrown" has one meaning: visible to that statement. |
| Does a signal act at the moment of the throw? | No. It acts on each wait when that delivery runs. A wait that ends in between, for example because a user completed the task, is skipped and reported. Synchronous delivery would lock many instances in one request, and one instance held by a long REST call would stall the throw. |
| What happens while the Worker is stopped? | Deliveries wait, and signals act when the Worker returns, as timers fire late. Nothing expires in this release: an expiry would leave instances waiting on a catch for a signal that was dropped. An administrator can cancel a broadcast that is no longer wanted, and two gauges show pending deliveries and the age of the oldest unfinished broadcast. |
| Are deliveries `workflow_jobs` or a queue of their own? | `workflow_jobs`, as the batches use. Leases, retries, incidents, and the Jobs & incidents page then apply unchanged. A job row must name a definition, so a broadcast enqueues one job per represented definition. |
| What happens when a payload fails a mapping? | That delivery fails with `signal_payload_invalid`, nothing is written, and the instance keeps waiting with its subscription. An administrator can retry failed deliveries, for example after moving the instances to a corrected version. |
| Which identity continues the instance? | The fixed system actor `signal`, with no roles or claims, as a finished call continues its parent as `callActivity`. The thrower is recorded on the broadcast and in the history payload, so an administrator's broadcast never makes them the actor for claim or assignment inheritance. |
| In which order do mapped values, conditional events, and the signal's own continuation apply? | A catch behaves as a message catch does: values first, then conditional events, then the catch's exit unless a conditional boundary interrupted it. A boundary captures first, so an interrupting signal cancels its host before the host's conditional boundaries can see the values. |
| Can mappings use shared variables? | No. A signal mapping cannot read or write a shared alias. One broadcast would write the same shared key from every recipient, and a boundary with shared outputs would need a new model in the lock-order proof. |
| Which hosts can carry a signal boundary? | The hosts of a conditional boundary, plus a signal catch, minus multi-instance service, script, and call activities. Timer, conditional, and signal boundaries share the limit of eight per host. |
| Can a catch carry boundaries? | Yes: timer, conditional, and signal boundaries, as a message catch carries timer and conditional ones. "Wait for this signal, at most seven days" needs it. |
| What must match for a version change while a subscription is open? | The node type, signal name, host, interrupting flag, and outgoing flow must be identical. Changed mappings only warn, as for a message catch, so waiting instances can be moved to a version with corrected mappings. |
| Is there a retention category? | Yes, a tenth: `signalBroadcasts`, keep forever by default. A broadcast holds its payload and up to 100,000 delivery rows. |
| Can an instance show what it waits for? | Yes. Instance detail lists the instance's active signal subscriptions. Without it, an armed boundary is invisible, and "why did this instance not react" has no answer. |
| Is the item built in one step? | No, in two stages. Stage A ships the catch, the throw, delivery, and the Signals page. Stage B adds the signal boundary, which carries most of the engine risk. Stage A creates the whole schema and wires every cancellation path, so stage B needs no migration. |

## Plan

Written on 2026-11-16 following [Writing a plan](README.md#writing-a-plan). Effort is revised from M–L to **L**: the item adds a runtime concept with its own tables, job kind, API, authorization scopes, and page, and it touches version changes, reactivation, cancellation, and retention. The plan is one design, built in the two stages of [section 13](#13-delivery-sequence-rollout-and-risks).

### 1. Scope and invariants

Add two node types. An `intermediateSignalCatchEvent` rests until a signal with its name is delivered, then continues along its one outgoing flow. A `signalBoundaryEvent` is attached to a host and reacts while the host is active: interrupting by default, non-interrupting with `cancelActivity: false`. Signals are thrown only through the API in this release.

Preserve these invariants:

1. **A signal is a broadcast, not a queue.** It reaches the waits that exist when it is thrown and is never kept for a later wait. Use a message event for something that must be delivered.
2. **Recipients are frozen once.** The throw decides the recipient set in one statement. Delivery adds no recipient and repeats none.
3. **One outcome per recipient.** A delivery row for one broadcast and one subscription is unique, and its final status commits in the same transaction as the instance change it describes.
4. **A subscription lives exactly as long as its wait.** Subscription rows are written only under the instance lock, in the transaction that starts or ends the wait.
5. **Throwing never waits for an instance.** The throw takes no runtime lock, and delivery rows hold no foreign key to an instance, token, or subscription.
6. **One instance per delivery transaction.** A delivery locks the runtime rows of one instance in the established order. No external call runs inside it.
7. **Receivers are independent.** One receiver's failure never rolls back another delivery or the throw.
8. **The Worker is required.** A definition with a signal event publishes only when durable publication is enabled.
9. **Workflows without signals pay nothing.** A definition with no signal event runs no additional query or statement in any transition. Instance cancellation is the one exception: it has no definition at hand, and loading one would cost more than the single cancel statement it runs.

This release adds no signal start, throw, or end event, no business-key scope, no buffering, no signal catalog, and no BPMN XML. Message events keep their behavior.

### 2. Workflow JSON and validation

These are fragments, not a complete workflow:

```json
{
  "flowNodes": [
    {
      "id": 9,
      "type": "intermediateSignalCatchEvent",
      "name": "Wait for the new policy",
      "laneId": 1,
      "x": 520,
      "y": 160,
      "signal": {
        "name": "policy-changed",
        "outputMappings": [
          { "variable": "policyVersion", "path": "version", "dataType": "number", "isArray": false, "required": true }
        ]
      }
    },
    {
      "id": 14,
      "type": "signalBoundaryEvent",
      "name": "Recall announced",
      "attachedToRef": 3,
      "cancelActivity": false,
      "signal": { "name": "product-recall", "outputMappings": [] }
    }
  ],
  "sequenceFlows": [
    { "id": 901, "sourceRef": 9, "targetRef": 10 },
    { "id": 1401, "sourceRef": 14, "targetRef": 15 }
  ]
}
```

| Subject | Contract |
| --- | --- |
| `signal.name` | Required literal text: 1 to 100 lowercase letters, digits, `.`, `_`, or `-`, starting with a letter or digit. No template, no trimming, exact comparison. The guides recommend a dotted prefix, such as `claims.policy-changed`, so that unrelated workflows do not share a name by accident. |
| `signal.outputMappings` | Optional list of at most 100 typed mappings in the shape message catches use: `variable`, `path`, `dataType`, `isArray`, `required`, `defaultValue`, and `validation`. `path` reads the broadcast's payload. |
| Mapping targets | A declared instance-scoped process variable, whose type, array shape, nullability, and validation then apply, or a new instance variable that the mapping declares. Never a shared alias or a reserved `sys.`, `config.`, `setting.`, or `mi.` name. |
| Shared variables | No default template and no validation that a delivery evaluates may read a shared alias. That includes the validation rule of a targeted process variable. |
| Catch topology | At least one incoming flow and exactly one outgoing flow, which is unconditional and carries no user-action or multi-instance settings. |
| Boundary topology | `attachedToRef` names its host. No incoming flow, and exactly one unconditional outgoing flow. |
| `cancelActivity` | Boundary only. Missing means `true`, interrupting. |
| Boundary hosts | A normal or multi-instance user task; a message, timer, or signal catch; or a `task`, `serviceTask`, or `scriptTask` with `asyncBefore: true` that is not a multi-instance body. Never a call activity or a conditional catch. |
| Boundaries on one host | Timer, conditional, and signal boundaries together are limited to eight. Two signal boundaries on one host cannot use the same name, and a boundary on a signal catch cannot use the catch's name. |
| Catch as a host | A signal catch accepts timer, conditional, and signal boundaries. |
| Other settings | A signal event carries only the common node properties, `signal`, and, on a boundary, `attachedToRef` and `cancelActivity`. Async flags, job policies, roles, variables, and multi-instance settings are rejected. |

Proposed messages, the same in the API and the editor. `Signal catch event` becomes `Signal boundary event` for a boundary:

| Problem | Message |
| --- | --- |
| Missing `signal` or name | `Signal catch event #9 must define signal.name.` |
| Invalid name | `Signal catch event #9 signal.name must be 1 to 100 lowercase letters, digits, '.', '_', or '-', starting with a letter or digit.` |
| Catch without an incoming flow | `Signal catch event #9 must have at least one incoming sequence flow.` |
| Not exactly one outgoing flow | `Signal catch event #9 must have exactly one outgoing sequence flow.` |
| Outgoing flow with a condition or action settings | `Signal catch event #9 must have one unconditional outgoing sequence flow without user-action or multi-instance metadata.` |
| Boundary without a host | `Signal boundary event #14 must reference a host via attachedToRef.` |
| Host of an unsupported type | `Signal boundary event #14 host #3 must be a durable wait or an automatic task with asyncBefore enabled.` |
| Multi-instance automatic host | `Signal boundary event #14 host #3 is a multi-instance service, script, or call activity, which signal boundaries do not support.` |
| Boundary with an incoming flow | `Signal boundary event #14 cannot have incoming sequence flows.` |
| Same name twice on one host | `Signal boundary event #15 uses signal 'product-recall', which signal boundary event #14 on host #3 already uses.` |
| Boundary repeats its host catch's name | `Signal boundary event #14 uses signal 'policy-changed', the signal its host signal catch event #9 waits for.` |
| More than 100 mappings | `Signal catch event #9 defines more than 100 output mappings.` |
| Mapping writes a shared alias | `Signal catch event #9 output mapping 'rate' cannot write shared variable alias 'rate'; signal mappings write instance variables only.` |
| Mapping reads a shared alias | `Signal catch event #9 output mapping 'rate' cannot read shared variable alias 'baseRate' in its default or validation.` |
| `signal` on another node type | `Flow node #5 defines signal but is not a signal event.` |
| Unsupported settings, API only | `Signal catch event #9 cannot define task, role, variable, timer, conditional, message, async, or multi-instance settings.` |

Mapping shape problems come from the shared typed-mapping validation with the kind `Signal catch event` or `Signal boundary event`, in its usual wording; the editor labels the field **Payload path**. Each message starts with `<noun> #<id>`, which `WorkflowAiDiagnostics.DefinitionPointer` turns into the JSON pointer returned to the AI assistant.

Two existing messages are reworded in both validators, because signal boundaries join their rule: the limit message becomes `... timer, conditional, and signal boundary events; at most 8 are allowed.`, and the `cancelActivity` message becomes `... but is not a timer, conditional, or signal boundary event.` Pinned characterization arrays change only where they contain one of these two messages.

Model and service changes:

- **`BpmnFlowNodeTypes`.** Add `IntermediateSignalCatchEvent` and `SignalBoundaryEvent` with `IsSignalCatch`, `IsSignalBoundary`, and `IsSignalEvent`. `IsBoundary`, `IsPassThrough`, and `IsSupported` gain the boundary; `IsSupported` gains the catch, which is a resting node like the other catches. The authoring schema's type list reads the constants by reflection.
- **`FlowNodeModel.Signal`**, a nullable `SignalEventModel` with `Name` and `OutputMappings`. The list reuses `MessageOutputMappingModel`, whose properties and JSON names are the ones needed. `dataType` and `isArray` are required; signals have no untyped legacy form.
- **`SignalEventRules`**, a new dependency-free class in `Flowbit.Shared/Models`. It owns the name pattern and its description, the mapping limit, and `FindProblems` for the name, duplicate, and host-name rules. Definition validation and the throw endpoint both use it, and the editor mirrors it.
- **`BoundaryEventRules.MaxObservableBoundariesPerHost`** counts signal boundaries too.
- **`WorkflowDefinitionValidator`.**
  - `ValidateAuthored` rejects `signal` on other node types, unsupported settings on signal events, and `cancelActivity` on anything that is not a timer, conditional, or signal boundary.
  - `ValidateObservableBoundary` serves the signal boundary with the event name `Signal boundary event`. Its durable-wait list gains the signal catch, for all three boundary kinds. A signal boundary on a multi-instance automatic body gets the message above.
  - A new `ValidateSignalEvent` checks the name through `SignalEventRules`, calls `ValidateTypedOutputMappings`, and applies the shared-variable rules. It finds shared reads from the parsed references of templates and expressions, as the conditional and timer analyzers do, never by matching text.
  - The one-outgoing-flow rule and the engine's label chain name both events.
- **Declarations.** Wherever the server lists `message.outputMappings` as variable declarations or expression sites, add `signal.outputMappings`: the declared-producer set that conditional and timer expressions accept, inbox-visibility symbols, `FlowInfo` and gateway-helper usage checks, and `CallActivityRules.SourceDeclarations`. `ValidateSharedVariableProducerAccess` needs no entry, because signal mappings cannot write shared aliases.
- **`WorkflowModelMigrator`.** `ApplyNodeInvariants` clears task, role, variable, script, message, timer, and conditional settings on signal events and removes `signal` from other types. It never rewrites a name. The branch that defaults `cancelActivity` to true on timer and conditional boundaries, and clears it elsewhere, gains the signal boundary; without that, normalization would erase a non-interrupting flag. Older documents contain neither type and normalize as before.
- **`WorkflowDefinitionReadinessChecker`.** `RequiresDurableProcessing` returns true for a definition with a signal event, and the gate's message names signal definitions.
- **`WorkflowAuthoringJson.CreateSchema`** adds the `signal` object.

### 3. Persistence and migration

Two additive migrations under the `flowbit` schema: `AddSignalEvents`, and `ValidateSignalEventConstraints` for the one statement that runs outside a transaction. Update entities, repository ports and implementations, `AppDbContext`, and its model snapshot together.

| Object | Fields and constraints |
| --- | --- |
| `signal_subscriptions` | `Id`; `InstanceId` (cascade); `WorkflowDefinitionId` and `WorkflowKey` (composite restrictive key to the definition); `TokenId` (restrictive), the waiting token of a catch or the host token of a boundary; `ActivationId`; `SignalNodeId`, `SignalNodeName`; nullable `AttachedToNodeId`, set for a boundary; `OutgoingFlowId`; `SignalName` varchar(100) with `C` collation; `CancelActivity`, always true for a catch; `Occurrence`, the number of signals delivered; `Status` (`active`, `completed`, `cancelled`); `CreatedAt`, `UpdatedAt`, `CompletedAt`. |
| Subscription checks and indexes | Checks for status, `Occurrence >= 0`, terminal time, and "a catch always consumes". Unique `(InstanceId, TokenId, ActivationId, SignalNodeId)`, the activation fence. `(SignalName, WorkflowKey, Id)` where `Status = 'active'`, which the throw and the summary read. `(InstanceId, TokenId)` where active, for cancellation by token. |
| `signal_broadcasts` | `Id`; `SignalName`; nullable `WorkflowKey`, null for a broadcast to all workflows; nullable `Payload` jsonb; `Status` (`delivering`, `completed`, `completedWithIssues`, `cancelled`); `TotalDeliveryCount`, `DeliveredCount`, `SkippedCount`, `FailedCount`, `CancelledCount`; `ThrownByKind` (`user` or `client`), `ThrownBy`, `ThrownByRolesJson`, `ThrownByClaimsJson`; nullable `IdempotencyKey` with `C` collation and a `RequestHash`; `CancelledBy`, `CancellationReason`; `CreatedAt`, `UpdatedAt`, `CompletedAt`, `CancelledAt`. |
| Broadcast checks and indexes | Checks for status, counts (non-negative, their sum at most the total, the total at most 100,000), an object payload of at most 262,144 bytes as PostgreSQL prints it, and terminal time. Unique `(ThrownByKind, ThrownBy, IdempotencyKey)` where the key is set. `(Status, UpdatedAt, Id)` and `(SignalName, CreatedAt, Id)`. |
| `signal_deliveries` | `Id`; `BroadcastId` (cascade); plain `SubscriptionId`, `InstanceId`, `WorkflowDefinitionId`, `WorkflowKey`, `TokenId`, `ActivationId`, `SignalNodeId`, `AttachedToNodeId`, and `CancelActivity`, copied from the subscription at the throw; `Status` (`pending`, `delivered`, `skipped`, `failed`, `cancelled`); `AttemptCount`; `ResultCode`, `ResultDescription` (at most 1,000 characters); `CreatedAt`, `UpdatedAt`, `CompletedAt`. |
| Delivery checks and indexes | Unique `(BroadcastId, SubscriptionId)`. `(BroadcastId, WorkflowDefinitionId, Status, Id)` for the job's pages, `(BroadcastId, Status, Id)` for the API, and `(InstanceId, Id)`. Checks for status and terminal time. No foreign key to an instance, token, or subscription. |
| `signal_broadcast_jobs` | `BroadcastId` (cascade), `WorkflowDefinitionId`, nullable `JobId` (set null when the job row is deleted), `OriginalJobId`, `Reason` (`throw` or `retry`), `RequestedBy`, `CreatedAt`. It follows `instance_variable_update_batch_jobs` and records who retried. |
| `workflow_jobs` | New kind `signalBroadcastDeliver`, phase `deliver`, queue `activity`, no instance or token, anchored to one represented definition and its default start node as variable-update batch jobs are. The payload holds the broadcast ID and the definition ID. No new column. |
| `node_executions` | `CK_node_executions_completion_reason` gains `signalReceived`. `AddSignalEvents` replaces the check as `NOT VALID`; `ValidateSignalEventConstraints` validates it. |
| `capture_workflow_incident_lifecycle()` | Replaced so that `signalBroadcastDeliver` is among the kinds that report no node. |
| `retention_policies` | Seed `signalBroadcasts` with no period, without overwriting an existing row. |
| `engine_settings` | Seed `Signals.RequiredRole` (`admin`), `Signals.MaxSubscriptionsPerBroadcast` (`10000`), and `Signals.MaxOpenBroadcasts` (`20`) with descriptions, without overwriting existing rows. |

Notes:

- **Why deliveries hold plain IDs.** A foreign-key check locks the referenced row `FOR KEY SHARE`, which waits for a transaction holding that row `FOR UPDATE`. With such keys, a throw would wait for every recipient that is in the middle of a transition, including one inside a synchronous REST call.
- **Sizes.** The payload limit counts `JsonbText.ByteCount`, the measure the check constraint uses, so the code refuses first.
- **Clients.** `shared_variable_clients.Scopes` is `text[]` without a value check, so the two new scopes need no schema change.
- **Counts.** The schema grows from 67 to 71 tables, the engine settings from 18 to 21, and the retention categories from nine to ten.
- **Retention.** `signalBroadcasts` deletes finished broadcasts older than its period, counted from `CompletedAt`; deliveries and job links go with them. It needs a new `TableKind` in `RetentionRepository`, an entry in `ExecutionOrder`, and a label on the Retention page. Once a broadcast is deleted, its idempotency key can be used again. Delivery jobs fall under `completedJobs` as other jobs do. Subscription rows stay with their instance, like timer and conditional subscriptions. History rows and node visits that a delivery wrote follow `workflowHistory` and `nodeActivity`.
- **Existing rows.** Nothing is backfilled. Existing instances have no subscription and need none.
- **Down migration.** It refuses while any signal subscription, broadcast, `signalBroadcastDeliver` job, or node visit with the reason `signalReceived` exists.

### 4. Transactions and lock order

The design adds no lock to existing transitions and keeps the runtime lock order. It uses three transaction shapes.

**Throw.** The request's transaction takes no runtime lock.

1. Take the advisory lock for the caller's idempotency key, when one is given, and look for an earlier broadcast with it.
2. Count unfinished broadcasts against `Signals.MaxOpenBroadcasts`, without a lock. The limit is a soft threshold: concurrent throws may pass it by a few, and no throw queues behind another's recipient insert.
3. Insert the broadcast row.
4. Freeze the recipients with one statement:

   ```sql
   INSERT INTO flowbit.signal_deliveries (...)
   SELECT ... FROM flowbit.signal_subscriptions s
   WHERE s."Status" = 'active'
     AND s."SignalName" = @name
     AND (@workflowKey IS NULL OR s."WorkflowKey" = @workflowKey)
   ORDER BY s."Id"
   LIMIT @max + 1;
   ```

   The recipients are the subscriptions that are committed and active in that statement's snapshot. More than `@max` inserted rows rejects the throw and rolls everything back.
5. Enqueue one `signalBroadcastDeliver` job per distinct definition among the new rows, with its link row, and store the counts. A broadcast without recipients is stored as `completed` and enqueues nothing.

**Delivery.** Each delivery runs in its own scope and transaction and locks, in this order: the instance hierarchy through `GetInstanceForUpdateAsync` with user tasks; the subscription row; the delivery row. It takes no shared-variable row, because signal mappings cannot use shared aliases. Everything it changes, including the delivery's final status, commits together. A second attempt finds the delivery no longer `pending` under those locks and does nothing.

**Subscription changes inside other transitions.** A transition that starts or ends a wait already holds the instance lock. It locks the subscription rows it changes after tokens, in the order the version change documents: jobs, incidents, timers, conditional-boundary subscriptions, then signal subscriptions.

Cancel and retry of a broadcast lock the broadcast row and then update delivery rows by status. They never lock runtime rows, and a delivery never locks the broadcast row, so no cycle exists. The job refreshes the broadcast's counters between pages in short transactions of their own.

`GetInstanceForUpdateAsync` applies the call-chain fence. A delivery to a child whose call was cancelled ends as `skipped` with `stale`. An invalid chain, like an active subscription whose token is not waiting there, fails only that delivery at once with `delivery_error` and an error log. Nothing transitions, and no job incident opens, because one would hold back every other recipient of the job.

PostgreSQL race tests are a release gate: a throw while a recipient's transaction holds its instance lock, a delivery against a user completing the host, and a delivery against instance cancellation.

### 5. Engine lifecycle

Add `SignalBroadcastService` for the API commands, `SignalBroadcastJobProcessor` for paging, and an engine partial `WorkflowEngineService.Signals.cs` that implements an `ISignalDeliveryExecutor` port, as the engine implements the batch executors. `ISignalSubscriptionRepository` and `ISignalBroadcastRepository` are new ports.

#### Subscribe

`EnsureSignalSubscriptionsAsync` runs in `ResolvePassThroughAsync` for the token's current node. Its place matters: after the activation re-check that follows `EnsureAttachedConditionalBoundarySubscriptionsAsync`, and under the same guard that skips a resumed failed activity. A conditional boundary that is already true interrupts the host inside that earlier call, and a subscription created before the re-check would belong to a token that has left the host.

- For a signal catch it creates the catch's own row.
- For a host that `IsBoundaryHostEligibleAsync` accepts, it creates one row per attached signal boundary. This is host entry, before an `asyncBefore` job runs, as for conditional boundaries.

The call returns before any query when the node is neither a signal catch nor the host of a signal boundary. The activation uniqueness makes it idempotent. Nothing is evaluated on entry. The catch then takes the existing resting branch, which arms its timer boundaries, and its token has no wait state, like a token on a message catch. Reactivation creates a target's signal boundaries through the same call.

#### Unsubscribe

`ISignalSubscriptionRepository.CancelByTokenIdsAsync` joins every place that cancels conditional boundary subscriptions today: the two helpers that end waits and the four paths that cancel subscriptions directly.

| Path | Code |
| --- | --- |
| The host exits: a user action, a delivered message, a closed multi-instance execution, an `asyncAfter` continuation, any automatic advance from a node that is not a timer | `CancelAttachedBoundaryWaitsAsync` |
| Work is torn down: instance cancellation, terminate end, fault, a cancelled gateway scope | `CancelDurableWorkForTokensAsync` |
| An interrupting conditional boundary captures the host | `CaptureConditionalBoundaryAsync` |
| An interrupting timer boundary fires | `FireTimerBoundaryAsync` |
| An administrative action fires a timer boundary | `ExecuteAdministrativeTimerBoundaryAsync`, which also cancels the host's conditional boundary subscriptions |
| A timer catch that hosts boundaries fires | `FireTimerCatchAsync` |
| An error boundary is selected for a failed service or script host | The error-capture branch of `ResolvePassThroughAsync` |

Each of these calls runs only when the instance's definition contains a signal event, a flag computed once per cached definition. `CancelAttachedBoundaryWaitsAsync` runs on every host exit, so an unguarded call would add a statement to every transition of every workflow. The guard is safe across version changes: an open subscription blocks a change to a definition that lacks its signal event, so an instance on a definition without signal events has no subscription to cancel.

A test asserts that no subscription is active for a token that is not active on the subscription's node, after every path above. A command-count test asserts that a definition without signal events runs the same statements per transition as before.

#### Throw

`SignalBroadcastService.ThrowAsync` validates the request, replays an earlier broadcast for a repeated idempotency key, and runs the throw transaction of [section 4](#4-transactions-and-lock-order). It snapshots the thrower: identity, kind, roles, and, for a JWT user, the claims selected by `WorkflowAudit:AllowedClaims`. It never logs the payload.

#### Deliver

One job handles the deliveries of one broadcast and one definition. It loops over pages of 100 pending deliveries in ascending ID with a cursor, so a failing delivery cannot starve later ones, and refreshes the broadcast's counters after each page. When no pending delivery remains for the whole broadcast, it sets `completed`, or `completedWithIssues` when any delivery failed. Jobs for other definitions and other broadcasts run in parallel within `FlowbitWorker:ActivityConcurrency`.

Each delivery:

1. Takes the locks of [section 4](#4-transactions-and-lock-order). A delivery that is no longer `pending` ends here.
2. Ends as `skipped` with `subscription_ended` when the subscription is not active, which covers an instance that is no longer running.
3. Checks the fence: the token is active with the subscription's activation, on the catch node or on the boundary's host, and the host is still eligible. A mismatch on an active subscription is an invariant failure: it is logged and recorded at once as `delivery_error`, with no job incident, so it never holds back the job's other recipients.
4. Reads the node from the instance's current definition and resolves the mappings against the broadcast's payload with `ResolveTypedOutputs`, in the context of the fixed actor. A failure writes nothing.
5. Applies the signal, as the next table describes, and continues routing with `forceDurableActivities`.
6. Marks the delivery `delivered` with its result code and commits.

| Subscription | Order of work | Result code |
| --- | --- | --- |
| Catch | Write the values on the catch's visit. Let conditional events observe them through `ResumeForVariableChangesAsync`. If the token is still the same runnable activation: complete the subscription, cancel the catch's boundary waits, record the flow traversal and history, complete the visit, and route on, as `DeliverMessageAsync` does. | `caught`, or `superseded` when a conditional boundary took the token |
| Interrupting boundary | Complete the subscription. Cancel the host's work with the sequence `CaptureConditionalBoundaryAsync` uses: an automatic body, open user tasks, multi-instance children, timer, conditional, and other signal subscriptions, other jobs, and calls. Move the token to the boundary. Write the values on the boundary's visit, let conditional events observe them, and advance through the boundary's flow. | `interrupted` |
| Non-interrupting boundary | Check the instance's active tokens against `Workflow.Gateway.MaxActiveTokens`; at the limit the delivery fails and the boundary stays subscribed. Otherwise increase `Occurrence` and stay active. Add a sibling token on the boundary in the host's gateway branch. Write the values on the sibling's visit, let conditional events observe them, and advance the sibling. | `branched` |

The cancel sequence is shared with the conditional capture through one helper that takes the completion reason and the job-cancel reason, so the two cannot drift.

Nothing orders two broadcasts relative to each other. When two reach one catch, the first delivery to run is applied and the other is skipped. One broadcast advances one wait: when a catch is followed by another catch with the same name, the second starts a new subscription that the same broadcast does not reach.

Deliveries wait while no Worker runs, and nothing expires. A signal then acts when the Worker returns, on the waits that are still open.

#### Outcomes and failures

| Status | Code | Cause |
| --- | --- | --- |
| `delivered` | `caught`, `interrupted`, `branched`, `superseded` | See the table above. |
| `skipped` | `subscription_ended` | The wait ended between the throw and the delivery. |
| `skipped` | `stale` | A conflict or the call-chain fence: the instance changed in a way that makes this delivery obsolete. |
| `failed` | `signal_payload_invalid` | A mapping could not be resolved or validated. |
| `failed` | `signal_delivery_rejected` | Another authored failure after the mapping, such as routing that finds no flow. |
| `failed` | `signal_token_limit` | A non-interrupting boundary would add a token beyond `Workflow.Gateway.MaxActiveTokens`. |
| `failed` | `delivery_error` | An unexpected error on the job's final attempt, or at once an invariant failure or an invalid call chain. |
| `failed` | `lease_exhausted` | The job's final lease expired. |
| `cancelled` | `broadcast_cancelled` | The broadcast was cancelled first. |

- An authored failure (`WorkflowDomainException`) rolls the delivery back. A separate short transaction then records `failed` with the engine's message, bounded with `UnicodeText.Limit`. The instance keeps waiting with its subscription.
- A `WorkflowConflictException` records `skipped` with `stale`, as batch processors do.
- An unexpected exception leaves the delivery `pending` and is rethrown after the pass, so the job retries: four attempts with delays of 10 seconds, one minute, and five minutes. On the final attempt the delivery becomes `failed`.
- A final lease expiry is settled by a new `ReconcileExhaustedSignalBroadcastsAsync` branch in `OpenExhaustedLeaseIncidentsAsync`: that job's pending deliveries fail with `lease_exhausted` and the broadcast is recounted. The generic `lease_exhausted` incident still opens. Retrying that incident reruns a job that finds nothing pending, as for batch jobs.
- **Retry failed** returns a broadcast's failed deliveries to `pending` and enqueues new jobs. A delivery whose subscription has ended meanwhile is then skipped.
- **Cancel** marks every pending delivery `cancelled`. A delivery already in its transaction settles normally.

#### Jobs, actor, and evidence

- `WorkflowJobProcessorRouter` routes the new kind to the processor. `LifecycleEventCaptureTests.JobKindReportsNode` classifies it as reporting no node.
- The delivery runs as `signal` with no roles, claims, or delegation. `sys.user` is `signal` in every expression the continuation evaluates. The token's automatic-activation count resets as after any external trigger.
- **History.** One new note, `signalReceived`, with a payload of the broadcast ID, signal name, subscription ID, event node, host token, interrupting flag, and thrower. Its action ID is null, so `IsActorActionHistory` already excludes it from claim and assignment inheritance. History rows carry no actor claims; the broadcast row keeps the thrower's.
- **Node activity.** One new completion reason, `signalReceived`: the catch's visit completes with it, an interrupted host's visit is cancelled with it, and the boundary's visit completes with it through the note mapping in `AdvanceAutomaticTokenAsync`.
- **Flow evidence.** The outgoing flow records a traversal of kind `signalCatch` or `signalBoundary`, and no action.
- **Telemetry.** A new meter `Flowbit.Runtime.Signals` counts broadcasts and deliveries by outcome, without names or payloads. `WorkerTelemetry` forwards it to the Worker's metrics endpoint. The Worker's queue telemetry adds two gauges: pending signal deliveries, and the age of the oldest unfinished broadcast.
- **Lifecycle webhooks.** No event type is added. Instance and task changes that a delivery causes are captured by the existing triggers.

### 6. Versions, reactivation, and graph analyses

- **Version changes.** `WorkflowVersionCompatibilityEvaluator` gains `ValidateOpenSignalSubscriptions`, and `ValidateActiveNodes` includes the nodes of open subscriptions.

  | Code | Kind | Cause |
  | --- | --- | --- |
  | `open_signal_subscription_node_missing` | Blocker | The target has no such signal event. |
  | `open_signal_subscription_contract_changed` | Blocker | The node type, `signal.name`, `attachedToRef`, effective `cancelActivity`, or outgoing-flow contract differs. |
  | `attached_signal_boundary_contract_changed` | Blocker | An active host gains, loses, or changes a signal boundary. |
  | `signal_mapping_contract_changed` | Warning | Only `signal.outputMappings` differ. The target's mappings apply to later deliveries. |

  `DurableNodeContract` includes signal boundaries for hosts with open jobs. `ChangeInstanceWorkflowVersionCoreAsync` rebinds open subscriptions to the target definition and node name. A pending delivery needs no change: it names the subscription, and its job reads the node from the instance's current definition. Batch version changes use the same evaluator.
- **Reactivation.** `WorkflowReactivationRuntimeStateRecord` gains the count of active signal subscriptions, which blocks with `unresolved_durable_work`. A terminal instance has none. The preview needs no signal check, because a signal boundary has nothing to evaluate on entry.
- **Call activities.** A child instance subscribes like any other instance of its family. Cancelling a call cancels the child's tokens and with them its subscriptions.
- **Graph analyses.** A catch is an ordinary node with flows. `BuildStructuralAdjacency` in the validator adds the boundary's attachment edge, as it does for the other boundaries. `GatewayTopologyIndex` and `FindUnsafeReactivationTopology` do not follow timer or conditional attachments and do not follow signal attachments either.
- **Shared-variable lock order.** In `SharedVariableTransactionLockOrderValidator`, `IsExternalResting` gains the signal catch and `IsExternalRestingOrBoundary` gains the signal boundary. Each then seeds an external continuation that starts a new segment and holds no key of its own. `SharedVariableAccessPlanner` gives signal nodes an empty plan; a test pins that.
- **Multi-instance user tasks.** An interrupting signal boundary on a multi-instance user task cancels its unfinished items, as a conditional boundary does.

### 7. API contracts and authorization

New routes in a `SignalEndpoints` group:

| Route | Purpose | Permission |
| --- | --- | --- |
| `POST /api/signal-broadcasts` | Throw a signal. `202 Accepted` with `Location` and the broadcast; `200` when it replays an idempotency key. | Throw |
| `GET /api/signal-broadcasts?name=&workflowKey=&status=&page=&pageSize=` | List broadcasts, newest first, without payloads. | Read |
| `GET /api/signal-broadcasts/{id}` | One broadcast with its counts and payload. | Read |
| `GET /api/signal-broadcasts/{id}/deliveries?status=&instanceId=&page=&pageSize=` | Its deliveries, in ascending ID. `pageSize` is clamped to 1–200. | Read |
| `POST /api/signal-broadcasts/{id}/cancel` | Cancel pending deliveries, with an optional reason. Repeating it changes nothing; a finished broadcast returns `409`. | Throw |
| `POST /api/signal-broadcasts/{id}/retry-failed` | Return failed deliveries to pending, each moved to the version its instance runs now. Requires `expectedUpdatedAt` (`400` when missing). Allowed only for a broadcast that finished with issues; any other status or a stale value returns `409`. | Throw |
| `GET /api/signal-subscriptions/summary?name=&workflowKey=` | Counts of active subscriptions by signal name and workflow family, at most 200 rows with a `truncated` flag. It writes nothing. | Read, users only |

Throw request and the main part of its response. A broadcast to every family sends `"allWorkflows": true` in place of `workflowKey`:

```json
{
  "name": "policy-changed",
  "workflowKey": "claims-handling",
  "payload": { "version": 7, "effectiveDate": "2027-01-01" },
  "idempotencyKey": "policy-7"
}
```

```json
{
  "id": 42,
  "name": "policy-changed",
  "workflowKey": "claims-handling",
  "status": "delivering",
  "totalDeliveryCount": 1243,
  "deliveredCount": 0,
  "skippedCount": 0,
  "failedCount": 0,
  "cancelledCount": 0,
  "recipientsByWorkflowKey": [{ "workflowKey": "claims-handling", "count": 1243 }],
  "thrownBy": "integration-policy",
  "thrownByKind": "client"
}
```

`recipientsByWorkflowKey` lists at most 50 families, with a `truncated` flag beyond that, so a caller sees at once which workflows a broadcast reached.

| Request field | Rule |
| --- | --- |
| `name` | Required, by `SignalEventRules`. |
| `workflowKey` or `allWorkflows` | Exactly one: a workflow key, or `allWorkflows: true`. Neither or both returns `400`. A definition with the given key must exist; otherwise `400`. A family without waits is accepted and completes with no deliveries. |
| `payload` | Optional JSON object of at most 256 KiB. Missing and `null` both mean no payload; every mapping path is then missing, and its default or its required check applies. |
| `idempotencyKey` | Optional, with the trimming and length rules of batch idempotency keys. It is unique per thrower. The same request returns the first broadcast; a different request with the same key returns `409`. |

Errors use the existing shapes: `400` for an invalid request and for more recipients than `Signals.MaxSubscriptionsPerBroadcast`; `401` and `403`; `404` for an unknown broadcast; `409` for a reused key, a reached `Signals.MaxOpenBroadcasts`, and the cases in the table above. No exception type is added. The throw route limits its body to 1 MiB, as the batch routes do; the payload limit is checked on top of that.

Authorization:

- Two policies, **throw** and **read**, accept a JWT user or a managed API client. A JWT user needs a role from `Signals.RequiredRole` for both. A client needs `signals.throw` or `signals.read`.
- `SharedVariableAccessRequirement` gains the role setting key, so the handler serves both features. `SharedVariableClientScopes.Allowed` gains the two scopes. Client records, secrets, rotation, and management routes do not change.
- **A client sees only its own work.** It lists and reads the broadcasts it threw and cancels or retries only those; any other broadcast returns `404`. Its delivery rows carry the result code without `resultDescription`, because the engine's message can quote a validation expression of the receiving workflow. Users see every broadcast and the descriptions. The subscription summary accepts users only.
- The middleware that rejects a request with both a bearer token and client headers covers the new routes. The development OpenAPI document lists them in `FlowbitOperationCatalog`. Like the shared-variable routes, the routes that accept a managed client declare the `SharedVariableClientId` and `SharedVariableClientSecret` schemes beside `Bearer` (`Program.cs`).
- `Signals.RequiredRole` is read on each check, comma-separated, with `admin` for a missing or blank value. The two limits are read for each throw: a missing or invalid value gives the default, and a larger value is capped.

Other contract changes:

- Responses that carry a node type can carry the two new values. Node-execution filters accept the new type and reason by reflection.
- Instance history can contain `signalReceived`. `/api/jobs` can return the kind `signalBroadcastDeliver`. `/api/retention` lists a tenth category. Shared-variable client requests accept two more scope values.
- `InstanceDetailDto` gains `signalSubscriptions`: the instance's active subscriptions with signal name, event node, host node, interrupting flag, and start time, at most 50 with a total count. The query runs only for a definition with signal events, and the property follows instance-detail access.
- DTOs in `Flowbit.Shared/Dtos/SignalDtos.cs`: `ThrowSignalRequest`, `SignalBroadcastSummaryDto`, `SignalBroadcastDetailDto`, `SignalRecipientCountDto`, `SignalDeliveryDto`, `CancelSignalBroadcastRequest`, `RetrySignalDeliveriesRequest`, `SignalSubscriptionSummaryDto`, and `SignalSubscriptionDto`.

### 8. Editor

All changes are in the canonical assets under `Flowbit/src/Flowbit.Ui/wwwroot/editor/`.

#### Types and shapes

- Add both types to `NODE_TYPE` and add `isSignalCatchEventType`, `isSignalBoundaryEventType`, and `isSignalEventType`. The catch joins `isEventType` and `isSingleOutgoingType`; it is not a pass-through type, so its flow is solid like a message catch's. The boundary joins `isBoundaryEventType`, which also makes it single-outgoing and pass-through.
- `NODE_TYPE_OPTIONS` gains "Signal Catch Event (wait for a broadcast)". Boundary types stay out of the Type list, and `typeLabel` gets the boundary's label.
- The explicit lists gain the new types: the type names in `searchDiagram`, `isTraceBoundaryType`, `NODE_ICON_SYMBOL`, the boundary list in `createSaveValidationContext`, and `flowLabel`, which labels a signal boundary's flow "signal".
- `editor-template.js` gains `icon-signal-catch`, an outlined triangle. The filled triangle comes with the throw events. The catch is an `EVENT_D` circle with the inner ring of an intermediate event; the boundary is a `BOUNDARY_D` circle. `renderNodes` adds `non-interrupting`, the dashed ring, to a signal boundary with `cancelActivity: false`. `flowbit-editor.css` gains body, ring, selected, and icon rules for both types from the existing event color variables, so both themes follow.

#### Hosts and type changes

- `renderBoundaryHostSection` gains **+ Add signal boundary event**, and its hosts gain the signal catch. `findObservableBoundaries` includes signal boundaries, so the limit note and the three buttons agree. The signal button is hidden on a multi-instance automatic body.
- `addSignalBoundary` creates the node with `signal: { name: "", outputMappings: [] }` and `cancelActivity: true`, selects it, and starts a connection from it, as the other two do.
- `changeNodeTypeFromInspector` seeds an empty `signal` when a node becomes a catch and removes it when the node becomes anything else. When a host stops being eligible, its signal boundaries are removed with its timer and conditional ones.
- `applyTypeInvariants` gets a branch for each type, modeled on the conditional catch and boundary. The two test harnesses that extract it and the loader harness stub every predicate by hand and need the three new ones.

#### Inspector

A **Signal** section (`renderSignalSection`) appears for both types.

- **Signal name**: a text field that suggests the names other signal events in the diagram use, with a live problem line from `signalNameProblem`, which also reports an uppercase letter.
- **Output mappings**: the rows a message catch has, built with `appendTypedOutputMappingFields`, with the path labeled as a payload path. Target suggestions leave out shared aliases.
- The existing **Interrupt attached activity** checkbox and **Attached to** field serve the signal boundary.
- Explanations sit behind `attachInspectorHelp`: what a signal is, that it reaches only waits that exist when it is thrown, and how to throw one.
- Every control is labeled, so the E18 census passes.

#### Load, export, and save validation

- `normalizeLoadedModel` names `signal` in its property list for the two types, and `keepImportedCallSettings` names it too. A new `normalizeSignalDefinition` keeps the name as authored and normalizes mappings with `normalizeTypedOutputMapping`. A `signal` value that is not an object stays for save validation, and the section offers to repair it, as the conditional section does.
- `prepareWorkflowExport` renormalizes the config before validation.
- A new phase `validateSaveSignalEvents` runs after `validateSaveConditionalEvents` with the messages of [section 2](#2-workflow-json-and-validation). `validateSaveWaitBoundaries` gains the signal boundary, the signal catch as a host, and the combined limit. `validateSaveNodeTypes` gains both types and rejects `signal` elsewhere. `validateSaveAsyncJobsAndTimers` accepts `cancelActivity` on a signal boundary.
- `signalNameProblem` and the duplicate-name rule live inside the `// BEGIN/END WORKFLOW SAVE VALIDATOR` markers, because the Jint suites run only that span. One fixture set feeds `SignalEventRules` and the editor's validator.
- The editor sites that read `message.outputMappings` as declarations gain signal mappings: `buildInboxVisibilitySymbols`, `validateFlowInfoUsage`, `validateGatewayExpressionUsage`, `callSourceDeclarations`, and the allowed-settings lists in `validateSaveGateways` and `validateSaveGraphScopes`.

#### Viewer

Viewer mode draws both shapes and needs no other change: overlay marks and halos already cover every event and boundary. The editor needs no host capability for signals.

### 9. Flowbit.Ui

- **Signals page.** `/signals` (`Signals.razor`), listed under **Manage**. It follows `/administrative-actions`:
  1. **Choose a signal and its scope.** A name field with the names from the subscription summary, and a required choice between one workflow family and all workflows. The page shows how many waits match in each family.
  2. **Payload.** A JSON object in a text area, parsed on the page as the common variables of an administrative batch are.
  3. **Throw.** A review step states the name, scope, and current match count, and that the waits may have changed by the time the request runs.
  4. **Current broadcast.** Counts per outcome, status, thrower, **Cancel**, **Retry failed**, and a paged delivery table with a status filter, instance links, result code, and description. `?broadcastId=` opens a broadcast directly.
  5. **Recent broadcasts**, paged.
- **Page behavior.** Every call goes through an identity guard with an epoch. A `401` or `403` clears all signal data and shows a permission message, and an identity change clears and reloads. The page refreshes the open broadcast every three seconds while it is `delivering`, never after disposal, and renders after each timer refresh. Times use `LocalTime`.
- **Components.** Display-only children under `Components/Shared/Signals/` (`SignalBroadcastSummary`, `SignalDeliveries`, `SignalBroadcastHistory`) and a pure `SignalFormatting` helper. They inject nothing and start no request. `WorkflowApiClient.Signals.cs` holds the client methods.
- **Instance detail.** `ExecutionPositionText.NodeTypeLabel` names both types ("Signal wait", "Signal boundary"), and `DescribeWait` says "Waiting for a signal". The page polls while a position waits for a signal, as it does for a message, and its notice names the signal from the definition. `InstanceHistory.razor` describes `signalReceived` with the signal's name and thrower and links the broadcast. `InstanceDiagram.razor` shows a selected signal event's name and whether a boundary interrupts. A **Waiting for signals** list shows `signalSubscriptions`, so an armed boundary is visible while its host is the active position.
- **Other pages.** Shared-variable clients offers the two new scopes and says that a client can also throw signals. Retention labels the new category. Jobs & incidents shows the raw job kind and needs no change.

### 10. AI authoring and the portable package

- The hosted assistant's instructions, tool names, and tool descriptions do not change. Its guides are the owning documents, so it reads about signal events once [section 11](#11-documentation-and-examples) is done.
- [The portable authoring guide](../../authoring/flowbit-authoring/references/authoring-guide.md) gains a short paragraph: use a signal when many instances must react to one announcement; a signal reaches only waits that exist when it is thrown; use a message for one instance or for something that must not be missed; place a boundary on the task it interrupts.
- The hand-written `capabilities` list in `AuthoringPackageBuilder` gains signal events. The schema and manifest are generated.
- The layout is unchanged. A test pins that an example with a signal boundary lays out without overlaps.

### 11. Documentation and examples

| Owning page | Update |
| --- | --- |
| [BPMN support](../bpmn-support.md) | The node count (26), two rows in the supported table with Worker "Yes", a **Signals** section beside Messages, the boundary host and limit sentences, and the unsupported list reduced to signal start, throw, and end events. |
| [Node reference](../node-reference.md) | The count, two catalog rows, `### intermediateSignalCatchEvent` and `### signalBoundaryEvent` sections, a **Signal configuration** section, the boundary host rules, and payload paths under output mappings. A package test requires both headings. |
| [API reference](../api-guide.md) | A **Signals** section with the seven routes, their schemas and errors; the new node types, completion reason, history note, job kind, retention category, and client scopes where those vocabularies are described. |
| [Developer guide](../developer-guide.md) | When to use a signal instead of a message, the frozen recipients, idempotent throws, reading progress, and the version-change rules. Examples stay HTTP-only. |
| [Flowbit.Ui guide](../ui-guide.md) | The sidebar row, a section on throwing a signal and following its deliveries, authoring both events, and reading a signal wait on instance detail. |
| [Deployment](../deployment.md) | The two migrations, an upgrade-table row, a **Signal events** rollout section (the existing "Signals and actions" section is about monitoring), machine integrations, the new meter and gauges, retention, and what happens to signals while the Worker is stopped, with how to cancel a broadcast that is no longer wanted. |
| [Database schema](../database-schema.md) | The four tables, the table count and map, the job kind, the completion reason, and the replaced capture function. |
| [Engine settings](../engine-settings.md) | The three settings, the count of 21, the seeding table, and `Workflow.Gateway.MaxActiveTokens`, which now also bounds tokens that signal deliveries add. |
| [Runtime reference](../../Flowbit/README.md) and [AGENTS.md](../../AGENTS.md) | Signal subscriptions and their lifetime, the throw and delivery transactions, the lock order, the invariants of [section 1](#1-scope-and-invariants), the data model, the BPMN mapping, and the editor conventions. |
| [Root README](../../README.md), [AI authoring](../ai-authoring.md), [documentation home](../index.md) | The node count, the building-block table, and the Worker's list of work. |
| [Example catalog](../../examples/README.md) | A new `examples/signals/` folder with a README walkthrough: `01-signal-catch-policy-change.json`, a review that waits for a signal and maps its payload, and `02-signal-boundaries-recall.json`, a task with an interrupting and a non-interrupting signal boundary. Both need the Worker and document their throw request in PowerShell and Bash. Update the count (71) and the coverage matrix; every node type must appear in an example. |
| Browser suite READMEs | The new scenarios and the census fixture. |
| This page and the [backlog](README.md) | Evidence and status. |

Validate changed relative links, anchors, and the example JSON, and compare the new operations with their registrations, DTOs, authorization, tests, and the development OpenAPI document.

### 12. Tests and verification

| Layer | Cases |
| --- | --- |
| Shared rules | `SignalEventRules`: the name pattern at its limits, uppercase letters refused, duplicates on a host, a boundary repeating its host catch's name. One fixture set feeds the C# rules and the editor's validator through Jint. |
| Definition validation | Every message in [section 2](#2-workflow-json-and-validation) through the API, each host kind accepted or refused, the combined limit, shared reads and writes in mappings, unsupported settings, the publication gate, a non-interrupting flag that survives normalization, and older documents normalizing as before. |
| Subscriptions (PostgreSQL) | Rows for a catch and for each boundary host kind; idempotent creation; cancellation on every path of [section 5](#unsubscribe); a loop that creates a new subscription per visit; a boundary on a signal catch; a host waiting in `asyncAfter`; a host that an already-true conditional boundary interrupts on entry, which must leave no signal subscription; and the command count of a definition without signal events. |
| Throw | Both caller kinds and both credentials together; name and payload rules; a missing scope and a double scope; one family and all workflows, with the recipients per family; two concurrent throws that do not queue; a client that sees and changes only its own broadcasts, without descriptions; zero recipients; the recipient limit and the open-broadcast limit; an idempotent replay and a conflicting replay; a subscription committed after the throw; one job per represented definition. |
| Delivery | A catch advancing with mapped values; each boundary kind on each host kind; two broadcasts to one non-interrupting boundary; the token limit; two catches in a row with one name, which one broadcast advances by one step; a failed mapping that leaves the wait; a conditional boundary superseding a catch; an interrupting signal cancelling the host before its conditional boundaries see the values; a downstream REST task becoming a job; more than 100 deliveries; a failing delivery that does not starve later ones; a processor run twice; cancel; retry failed; final lease expiry. |
| Races (release gate) | A throw while a recipient's instance lock is held does not wait; a delivery against a user completing the host has one winner; a delivery against instance cancellation; two broadcasts for one catch. |
| Records | History note and payload, node visits and reasons, flow evidence, diagram history across a boundary, the node type and reason filters, and claim inheritance ignoring the `signal` actor. |
| Versions and reactivation | Each code in [section 6](#6-versions-reactivation-and-graph-analyses), rebinding, a delivery after a version change, the batch version change, and the reactivation blocker. |
| Analyses | The lock-order proof with signal nodes, the empty access plan, and structural adjacency through a signal boundary. |
| Infrastructure | Both migrations up and down, the down guard, the completion-reason check, the capture function with `JobKindReportsNode`, retention of finished broadcasts, and the settings' parse rules. |
| Editor (Jint) | The shared fixtures and ordered errors, type conversion, invariants, the host section and limit, the loader round trip, the inspector section, the dashed ring, and the characterization arrays unchanged apart from the two reworded messages. |
| Flowbit.Ui | The Signals page's guard, clearing, and polling; the display components; the client methods; `ExecutionPositionText`; and the history text. |
| Pinned counts | 26 node types, 71 examples, and the assistant workspace file count. Stage A alone moves them to 25 and 70. |
| Performance (recorded evidence) | With `tools/InstanceLoadTest`, the start throughput of a workflow without signal events before and after the change, with both builds published to one disk. The duration of a throw to 10,000 and to 100,000 recipients, which must stay inside the database command timeout. The delivery rate of one job. |

Browser smoke suite, which has no Worker:

- **E23, standalone editor**, at 1440×900 and 1024×768. Turn a node into a signal catch, name it, and add a mapping; add a signal boundary to a user task and make it non-interrupting; see the dashed ring, the limit note at eight boundaries, and the save dialog's message for a duplicate name; undo and redo; save and reload.
- **H16, hosted editor**, at 1440×900 and 390×844. Author both events inside the application's styles and save a version.
- **R16, Signals page**, at 1440×900, 1024×768, and 390×844. Throw a signal that no instance waits for and see it complete with no deliveries; open it by link; lose the permission and see the data cleared; replace the identity.
- **E18.** Add both types and their flows to `editor-inspector-census.json` and extend the ID ranges the scenario selects.

Worker acceptance suite, at the same three viewports:

- **S1.** Throw to more than 100 waiting instances from the page; follow the counts to completion; page through deliveries; open a delivered instance and read its history and diagram.
- **S2.** One task with an interrupting and a non-interrupting boundary: the first signal adds a branch and keeps the task in My work, the second removes it.
- **S3.** A payload that fails a mapping: the delivery fails and the instance still waits. After the instance is moved to a version with a corrected mapping, **Retry failed** delivers it.
- **S4.** Cancel a broadcast while the Worker is stopped; restart it and see no delivery of cancelled work; recover from an expired lease without a duplicate.
- **S5.** A managed API client throws over HTTP with an idempotency key twice; the page shows one broadcast thrown by the client.

S2 belongs to stage B; the others run in stage A.

Then follow [mandatory real-browser verification](../../AGENTS.md#mandatory-real-browser-ui-verification) by hand on a local stack with a Worker: the standalone and hosted editors in both themes at 1440×900 and 1024×768, the hosted inspector at 390×844, the Signals page, and instance detail, with real clicks, typing, keyboard navigation, and drags. Check the console, and keep screenshots of both shapes, the dashed ring, the Signal section, and the Signals page.

Regression commands, run from the repository root with the same arguments in PowerShell 7 and Bash:

```text
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet
dotnet publish Flowbit/src/Flowbit.Api/Flowbit.Api.csproj -c Release -o artifacts/browser/hosts/api /p:UseAppHost=false
dotnet publish Flowbit/src/Flowbit.Ui/Flowbit.Ui.csproj -c Release -o artifacts/browser/hosts/ui /p:UseAppHost=false
dotnet build Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore
git diff --check
```

The suite READMEs cover Docker, the Chromium install, the acceptance suite's own commands, and evidence paths. Full solution runs have had order-dependent failures at the base commit, so compare failures against a base run before attributing one to this change.

### 13. Delivery sequence, rollout, and risks

The item is built in two stages. Each can be released on its own, and the status stays In progress until both are done.

**Stage A: the catch, the throw, and delivery.** `signalBoundaryEvent` is not yet a supported type, so both validators reject it.

1. **Contract.** The catch type, `SignalEventRules`, the model, validator, migrator, schema, publication gate, and shared fixtures. The catch already accepts timer and conditional boundaries. Exit with definition tests green and older documents unchanged.
2. **Persistence.** Both migrations with the complete schema, entities, repositories, the retention category, and the settings. Exit with migration and retention tests green.
3. **Subscriptions.** The catch's subscription, every cancellation path of [section 5](#unsubscribe) with its per-definition guard, version changes, reactivation, and the lock-order proof. Exit with the subscription and command-count tests green.
4. **Throw.** The service, endpoints, policies, client scopes and their limits, and idempotency. Exit with the throw tests green.
5. **Delivery.** The processor, the executor's catch path, outcomes, lease reconciliation, cancel, retry, and telemetry. Exit with the delivery and race tests green.
6. **Editor and Flowbit.Ui.** The catch in the editor, the Signals page, and instance detail. Exit with the Jint suites and component tests green.
7. **Documentation and package.** The first example, the owning guides, the authoring guide, and the pinned counts.
8. **Verification.** The catch parts of E23, H16, and E18; R16; S1 and S3 to S5; the performance evidence; and the manual checks. Record the evidence under [Current state](#current-state).

**Stage B: the signal boundary.** It needs no migration and changes no cancellation path.

9. **Contract.** The boundary type, its host rules, the shared limit of eight, and the two reworded messages.
10. **Engine.** Boundary subscriptions on host entry, the two boundary delivery paths with the shared cancel helper, the token limit, and the version rules for attached boundaries. Exit with the boundary cases of the subscription, delivery, and race tests green.
11. **Editor and Flowbit.Ui.** The host section, the dashed ring, and armed boundaries on instance detail.
12. **Documentation and package.** The second example and the final counts.
13. **Verification.** The boundary parts of E23, H16, and E18; S2; and the manual checks. Record the evidence, then set the status to Done.

Rollout:

1. Apply `AddSignalEvents`, then `ValidateSignalEventConstraints`. The second runs outside a transaction and can be applied again after a stopped run.
2. Upgrade every API, Worker, and UI replica. Mixed replicas are unsupported: an API replica without this release leaves a token on a signal catch without a subscription, so no broadcast reaches it, and a Worker without it fails a delivery job and opens an incident.
3. Only then save a definition with a signal event, grant roles through `Signals.RequiredRole`, and give clients the new scopes. Publishing also needs `WorkflowDurableProcessing:PublicationEnabled`.
4. To roll the binaries back, first retire every definition that contains a signal event, finish or cancel its instances, and let open broadcasts finish.

| Risk | Guard |
| --- | --- |
| A path ends a wait without cancelling its subscription, so a later signal acts on work that is gone. | Signal subscriptions are cancelled wherever conditional ones are, the delivery checks its own fence, and a test scans for stale subscriptions after every path. |
| A throw stalls behind a busy instance. | No runtime lock and no foreign key from delivery rows; a race test holds an instance lock during a throw. |
| A delivery is applied twice. | The unique delivery row, the status check under the instance lock, and one commit for the status and the instance change. |
| The C# rules and the editor's validator drift. | Shared fixtures run on both sides with the same messages. |
| Users expect a signal to reach waits that start later, or to act at the instant of the throw. | The contract is stated in the node reference, the developer guide, the editor's help, and the page's review step; skipped deliveries are reported with their reason. |
| A wide broadcast occupies the activity slots. | One job per definition, the open-broadcast and recipient limits, and the Worker's activity concurrency setting, described in the deployment guide. |
| A failed mapping leaves many instances waiting. | Failed deliveries are counted and listed, mapping changes do not block a version change, and failed deliveries can be retried. |
| Payloads hold business data indefinitely. | The payload limit, the `signalBroadcasts` retention category, and a note that a payload is not a place for secrets. |
| Managed clients keep shared-variable names while they also throw signals. | Scope names say what they allow, the clients page explains it, and the rename is recorded under [Later releases](#later-releases). |
| Two unrelated workflows use the same signal name, and one broadcast moves both. | A throw names one family or asks for all, the response and the page list recipients per family, and the guides recommend a dotted prefix. |
| Signal support slows workflows that do not use it. | The per-definition guard, the command-count test, and the load comparison. |
| Repeated broadcasts grow an instance through a non-interrupting boundary. | The active-token limit applies to every token a delivery adds. |
| Broadcasts wait while the Worker is stopped, and the open-broadcast limit then rejects new throws. | Nothing expires, two gauges show the backlog, the deployment guide says what to expect, and a broadcast can be cancelled. |
| A client reads another caller's payload or a receiving workflow's rules. | A client is limited to the broadcasts it threw and receives result codes without descriptions. |
