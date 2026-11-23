# EVENT-04C: Signal start events

[Parent EVENT-04](event-04-signal-events.md) · [Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Done |
| Category | New events |
| Effort | M–L |
| Priority | Not ranked; third of eleven in the [EVENT-04 follow-up order](README.md#event-04-follow-up-subtasks) |
| Parent | [EVENT-04](event-04-signal-events.md) |
| Depends on | [EVENT-04](event-04-signal-events.md) (Done) |
| Related | [EVENT-04B](event-04b-signal-throw-and-end-events.md), [EVENT-04A](event-04a-signal-scope-by-business-key-or-instance.md), [EVENT-04F](event-04f-signal-catalog.md), [EVENT-04J](event-04j-shared-variables-in-signal-mappings.md) |

## Summary

A `signalStartEvent` starts a new instance of its workflow each time a signal with its name is thrown in a scope that includes the workflow. Typed mappings turn the payload into the start variables.

A workflow family listens through its published default version. A throw scoped to the family or to all workflows freezes one start for each listening family together with its other recipients, and the Worker creates each instance in a transaction of its own.

## Why it adds value

- A broadcast can open new work, not only move work that is already waiting.
- The sender does not need to know which workflows react. Publishing a definition with a signal start adds a reaction.
- Today a workflow started by an external occurrence needs a message start, and the caller must address each workflow by its key.

## First release acceptance criteria

These criteria are revised by this plan on 2026-11-22. The revision names the scopes that reach a start, says which version a frozen start creates, rejects `idempotency` on a signal start instead of reading it from the payload, states what a throw can now wait for, and states who gains the right to start a workflow. A decision on the same day added the save rule against a workflow that throws the signal it starts on.

- [ ] `signalStartEvent` has no incoming flow and exactly one unconditional outgoing flow. It carries a `signal` object with a literal lowercase `name` and typed `outputMappings` that declare its start variables, as a message start's mappings do, and it reads no shared alias. One definition has at most one signal start per name. The API and the editor apply the same rules with the same wording.
- [ ] It is system-only: the start page does not offer it, and `POST /api/instances`, the message-start route, and a call activity cannot start it.
- [ ] Only the published default version of a family listens. Saving a first version as published, publishing the default, changing the default, and deleting it move or end the family's subscriptions in the same transaction.
- [ ] A throw scoped to a workflow family or to all workflows freezes the listening families' starts together with its other recipients, at most one start per family. A business-key scope and an instance scope reach no start.
- [ ] Each frozen start creates exactly one instance, of the version that is the family's published default when the delivery runs, or ends as `failed`, `skipped`, or `cancelled`. Retries, duplicate leases, restarts, a cancel, and **Retry failed** never create two.
- [ ] The new instance starts as the fixed actor `signal`, records the broadcast and its thrower in its history, and is linked from its delivery row on the **Signals** page.
- [ ] A payload that fails a mapping fails only that delivery and creates no instance.
- [ ] A signal start takes part in the business-key contract of the entry events and takes its key from a payload mapping. A duplicate ends that delivery as `skipped` with a reason that names the owning instance, and affects no other delivery. `idempotency` is rejected on a signal start.
- [ ] The recipient limit counts starts. The broadcast, its recipients by family, the subscription summary, and the review on the **Signals** page tell waits and starts apart.
- [ ] A throw still waits for no instance. It can wait for a publish, a default change, or a deletion of a listening family, for the length of that transaction, and no throw can race a deletion into a broadcast that cannot finish.
- [ ] Saving rejects a definition that throws, to a scope that includes its own family, a signal it starts on.
- [ ] A started instance's first token continues the broadcast's chain count, so a loop of starts through other families stops at `Workflow.Async.MaxConsecutiveAutomaticActivations`. A throw through the API starts a new chain.
- [ ] A definition with a signal start publishes only when durable publication is enabled. A definition without signal waits runs no signal statement in any instance transition, as today.
- [ ] Whoever may throw a signal may start the workflows that listen for it. No role is added, and the guides say so.
- [ ] Version changes, the shared-variable lock-order proof, the graph analyses, lifecycle capture, and the AI authoring package handle the type.
- [ ] The editor draws and authors the event, and the read-only instance diagram shows it.
- [ ] Existing workflow JSON keeps its behavior, and the schema change is additive. One HTTP contract changes: a delivery's `instanceId` can be null. The rollout order is documented.
- [ ] Documentation, an example, the portable authoring package, automated tests including PostgreSQL race tests, and real-browser verification with a running Worker are complete.

## Later releases

- Several signal starts with the same name in one definition.
- A start that also filters on the payload, such as a region.
- A throw by business key that starts an instance when no instance has the key.
- Start idempotency read from a payload member.
- Shared-variable aliases in start mappings, with [EVENT-04J](event-04j-shared-variables-in-signal-mappings.md).
- A bound of its own for chains of signal starts, lower than the limit for automatic steps.
- Permissions per signal, so that the right to throw one signal does not include the right to start every workflow that listens for another ([EVENT-04F](event-04f-signal-catalog.md)).
- A list on the **Signals** page of the workflows that start on each signal.
- A line in the set-default and delete confirmations of the Workflows page that names the signals the family would stop or start listening for.
- A workflow that starts another instance of itself by signal, which saving rejects in this release.

## Current state

Done on 2026-11-22: implemented as planned, with the deviations below, and verified case by case against the [test table](#13-tests-and-verification).

- **Contract.** `signalStartEvent` in `BpmnFlowNodeTypes`, `EntryEventRules` for the entries' declarations and nouns, `SignalEventRules.StartProblems` and the self-start rule at the end of `ThrowProblems`, both server validators, the migrator, and the editor's type, inspector start mode, and save validation, fed by the shared `SignalStartFixtures`.
- **Persistence.** `AddSignalStartEvents` and `ValidateSignalStartConstraints`; `signal_start_subscriptions` kept by the four default-changing operations of `WorkflowDefinitionRepository`; the delivery kind, the nullable delivery columns, `StartDeliveryCount`, and the per-family `startCount`.
- **Runtime.** The second freeze statement, `StartFromSignalAsync` with `CreateStartedInstanceAsync` shared with the message start, the new outcomes and history note, the retry of a failed start, and the chain count.
- **API and Flowbit.Ui.** The `kind` filter, the new DTO members, the Signals page's review, counts, kind filter, and delivery rows, the history rows, and the diagram's node line.
- **Documentation and example.** The owning pages listed in [section 12](#12-documentation-and-example), [Example 05](../../examples/signals/05-signal-start-recall-notice.json), the authoring guide, AGENTS.md, and the suite READMEs.

Deviations from the plan:

- The start's visit completes with the reason `signalReceived`, as a catch's does.
- The page keeps its earlier wording where no start is involved: "No instance is waiting for 'x' now." and "froze N recipients".
- Verification found that `SignalBroadcastWriter` saved each delivery job and its link separately while EF tracked every job, so the cost grew with the square of the definitions reached. A throw to 1,000 listening families held its transaction for 14.8 s. The writer now inserts all jobs and all links in one save each, and the same throw takes 1.7 s. Waits spread over many definitions benefit too.
- The Signals page's payload help and the editor's workflow panel now mention signal starts.
- The **Kind** filter's first option reads **Waits and starts** instead of **All**, which names what it lists.
- A start that ran on its family's default and created no instance names that version and start event on its row, as a started one does; the plan named them only for a delivered start (review of 2026-11-23).

### Review of 2026-11-23

A second review compared the code with this plan. It found no defect in the lock order, the default-changing operations, the freeze and retry statements, exactly-once delivery, or cancel, and these gaps, now fixed:

- **Editor and API.** The editor kept imported `roles` (and `variables`) on a signal start until the node was selected, so a hosted save could fail with the API-only message; the loader now drops them. The mapping messages (a missing type, path, or name, a default of the wrong type, a reserved prefix, a duplicate name) and the business-key messages now use the API's wording, the start's own problems come after its topology and business key as on the server, and a duplicate name names the first earlier start in document order. `SignalStartFixtures` gained 19 cases, 41 in all, that run on both sides. The editor's business-key wording changed for plain and message starts too, because the code is shared.
- **Inspector.** The duplicate-name line shows on both starts and no longer hides for a name that contains `signal.name`; the throw's section and **Scope** help name the kinds that start workflows; **Enable business keys** keeps focus after its redraw.
- **Server.** A start or message start mapping whose `variable` is JSON `null` returned `500`; it now returns the `400` for a missing name. The subscription summary merges waits and starts in one statement before the 200-row cut, where two separate cuts could count a family's waits as 0. `ValidateSignalStartConstraints.Down` checks the down guard first, so a refused downgrade no longer drops the start index. A start that fails or is skipped after reading the default records that version and node (above). The OpenAPI descriptions of the throw, deliveries, retry, and summary routes mention starts.
- **Not a defect.** The review suspected that a script's own error text, which can quote the business key, reaches a failed start's description. Routing from a start runs script and service tasks as Worker jobs (`forceDurableActivities`), so it cannot; `AScriptAfterTheStart_RunsAsAJobSoItsErrorNeverReachesTheDelivery` pins it.
- **Tests added for cases the table named:** the self-start rule on an intermediate throw and for another name; a conflict while starting, an unexpected error on the last attempt (`delivery_error`), and a lost final lease (`lease_exhausted`), each with a retry where it applies; a default that changes to none; a cancel while a start holds its delivery; `POST /api/instances`, the message-start route, and a call against a family whose only entry is a signal start; version changes that remove or add a signal start; editor type changes from a signal catch and a throw; and the summary's cut at limits of 1 to 3.
- **Verification.** `dotnet test Flowbit/Flowbit.slnx`: 6,185 of 6,185 in 19 m 16 s. Browser smoke E18, E25 (1440×900 and 1024×768), and H18 against freshly published hosts: 4 of 4. By hand in the Chromium pane, on the standalone editor served from localhost at 1024×768 and 1440×900: both starts of a shared name show the duplicate line, which clears as the name is typed with focus kept in the field; **Enable business keys** keeps focus and Tab reaches **Business key variable**; the **Scope** help names the kinds that start workflows; the save dialog lists the business-key problems before the start's own, in the API's wording; and an imported signal start with roles, variables, and idempotency exports without them without being selected. The console stayed empty. The hosted editor was not rechecked by hand: no element or style changed, and H18 passed.

### Evidence

- **Full suite.** `dotnet test Flowbit/Flowbit.slnx` with every case of the test table: 6,052 of 6,052 passed in 19 m 32 s. The last additions then passed with the 962 tests whose names contain `Signal`, `OpenApi`, or `ExampleWorkflow`: the shared fixture cases for a throw of the start's name to one business key and to this instance, `callOnly` and the business-key rule beside a message start and a timer start, the entry and signal members of every example unchanged by normalization, the OpenAPI members and `kind` filter, a deletion that leaves no row, a family that freezes its start and a wait together, and the start's traversal kind.
- **Where each case is tested.** `SignalStartValidationTests` and `SignalStartFixtures` (definition rules, both validators); `SignalStartRuntimeTests` with its `Delivery` and `Chains` files (subscriptions, freeze with all six scope kinds of a workflow throw, delivery, default at delivery, exactly once, rollback, business keys and logs, retry, the three loop-guard cases and the widening loop, the four races, cost, visibility, version change, schema, migration and its two down guards, retention); `SignalStartAnalysisTests` (reachability, lock-order proof, access plan); `EditorSignalStartTests`; `SignalFormattingTests`; `OpenApiContractTests`; and the catalog layout test, which lays out Example 05 with every example.
- **Browser smoke suite.** E18, E23, E24, E25 (1440×900 and 1024×768), H16, H17, H18, and R16 (three sizes): 13 of 13 in 1 m 15 s, against freshly published hosts.
- **Worker acceptance suite.** `SignalAcceptanceTests`, S1 to S13: 18 of 18 in 5 m 33 s before the writer's batching, and 18 of 18 in 5 m 50 s after it, against republished hosts.
- **Manual checks** in the Chromium browser pane, against the published API, Worker, and UI on `http://127.0.0.1:25152` with a database of their own:
  - The hosted editor at 1440×900 draws Example 05's start with the outlined triangle and a dashed flow. Tab moves through the name, each mapping row, **+ Add mapping**, and the business-key controls in order.
  - On the Signals page, typing the name shows "1 new instance in 1 family: example-signal-start-recall-notice (1 start)". The review reads **Reaches now** "1 new instance", and the throw "froze 1 new instance". The pending start reads "No instance" and "Not started yet". After the Worker delivered it, the row read **Started an instance** with a link to the instance, and the kind filter, driven by keyboard, showed it under **Starts** and nothing under **Waits**.
  - Instance detail shows the business key `R-1001 · all`, **Started by** `signal`, "Started by signal 'warehouse.recall-announced' from manual-admin (broadcast #1)", and "Left the signal start event". With recorded history on, the selected start reads "Signal warehouse.recall-announced · starts an instance".
  - The standalone editor at 1024×768, with a signal end event that throws the start's name to **This workflow**, shows the self-start line on the start. Renaming the start clears it as you type, and undo brings it back. In the dark theme the shape and inspector render, and the save dialog lists the same message.
  - The hosted editor at 390×844 does not overflow.
  - The console showed no errors, except reconnect attempts while the UI was stopped to be republished.
- **Performance**, on this machine (2 cores; Development hosts with Warning logs; PostgreSQL in Docker). The base commit (399eee3) and this change were published to the same disk and run alternately, twice each, on fresh databases, without a Worker:

  | Measure (median) | Base, runs a / b | This change, runs a / b |
  | --- | --- | --- |
  | Set-default, family without signal events | 23.9 / 23.8 ms | 25.2 / 25.5 ms |
  | Throw to a family without a start subscription | 22.5 / 23.0 ms | 24.5 / 23.4 ms |
  | Starts of a workflow without signal events, 8 at a time | 40.3 / 40.9 per second | 39.3 / 40.1 per second |

  A default change costs about 1.5 ms more, for locking and syncing the family's start rows, and a throw about 1 ms, for the second freeze statement; start throughput is within the runs' noise. With a Worker, this change alone:

  | Measure | Result |
  | --- | --- |
  | Publish 1,000 listening families, 8 at a time | 6.9 s |
  | Throw to all workflows reaching 1,000 starts | 1.7 s for the request (14.8 s before the batching); every instance after 41.4 s |
  | Set-default, family with a signal start / without signal events | mean 26.0 / 22.8 ms |
  | Throw to a family without / with a start subscription | mean 21.5 / 51.6 ms |
  | 20 concurrent throws to one listening family | all accepted within 305 ms; 20 instances |

- **Simulated failures.** A crash after the delivery's commit is the delivery job running after the delivery already committed; a crash before it is a start whose throw is refused in its own routing after its instance and business-key claim were written, which rolls both back. No process is killed.

### Found in passing

A durable script task (`asyncBefore`) whose NCalc or JavaScript reads `FlowInfo` failed into an incident in every workflow, signal start or not: `WorkflowEngineService.Jobs.cs` deserialized the job's saved `SequenceFlowInfoSnapshot` directly, which the type does not support, while `AutomaticMultiInstanceCoordinator` read the same snapshot field by field. Fixed in the same change: both now read a stored snapshot with `SequenceFlowInfoSnapshot.FromStoredJson`, and the stored JSON is unchanged, so jobs already queued load. `DurableScriptFlowInfoTests` runs a durable script in each format; both cases fail with the old line and pass with the fix. `TraversalKind_TheFlowLeavingASignalStartIsTraversedAsSignalStart` reads `FlowInfo` from a gateway, in the start's own routing.

### Facts the plan rests on

Verified against the code on 2026-11-22, before the change.

Throw and delivery:

- **Freeze.** `FreezeDeliveriesAsync` in [SignalBroadcastRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/SignalBroadcastRepository.cs) runs one literal `INSERT ... SELECT` per scope kind from `signal_subscriptions`, without a locking clause, limited to the recipient limit plus one. The business-key statement joins `workflow_instances`.
- **Delivery rows.** `signal_deliveries` requires `SubscriptionId`, `InstanceId`, `TokenId`, and `ActivationId`, holds no foreign key to any of them, and is unique per broadcast and subscription.
- **Jobs.** `SignalBroadcastWriter` enqueues one `signalBroadcastDeliver` job per represented workflow definition, and the job row references that definition. `SignalBroadcastJobProcessor` pages the pending deliveries of one broadcast and one definition and calls `ISignalDeliveryExecutor.DeliverSignalAsync` for each, in a scope and transaction of its own. It maps exceptions to outcomes: a payload problem and a domain rejection fail the delivery, and a conflict skips it as `stale`.
- **Lost jobs.** When a delivery job loses its final lease, [WorkflowJobRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/WorkflowJobRepository.cs) fails the pending deliveries of the job's broadcast and definition with `lease_exhausted`.
- **Retry.** `RetryFailedDeliveriesAsync` moves failed deliveries to pending and rebinds each to the version its instance runs now. `EnqueueDeliveryJobsAsync` throws when a definition no longer exists.
- **Deletion.** `AssessDeletionCoreAsync` in [WorkflowDefinitionRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/WorkflowDefinitionRepository.cs) already refuses to delete a version while a delivering broadcast has a pending delivery for it, because `DeleteAsync` removes the version's jobs that have no instance.
- **Chain count.** `StartInstanceCoreAsync` already gives a call activity's child its caller's count through `SetExecutionTokenAutomaticActivationCountAsync`.
- **Summary.** `SummarizeActiveAsync` counts active subscriptions by name and family, and the **Signals** page reads every recipient count from it.
- **Locking clause.** PostgreSQL 17.10 accepts `INSERT ... SELECT ... ORDER BY ... LIMIT ... FOR SHARE OF s`. Checked with temporary tables against the local stack's database.
- **Isolation.** The unit of work and `WorkflowDefinitionRepository` begin transactions at the default level, READ COMMITTED. Only two read-only inbox queries use REPEATABLE READ. The row-lock behavior below depends on READ COMMITTED.
- **Row locks, tried with two sessions** on a disposable PostgreSQL 17 container:
  - A freeze that waits for a default change then freezes a row that was updated in place, with its new values. It skips a deleted row, and it does not see a row that was deleted and inserted again.
  - An operation that locks the rows `FOR UPDATE` waits for a freeze in flight. A count of the freeze's deliveries taken before the lock sees none; the same count after the lock sees them.
  - Two freezes hold a share lock on the same row at once.

Definitions and entry events:

- **Four places change a family's default.** All are in `WorkflowDefinitionRepository`: `AddAsync` when a first version is saved as published, `SetPublishedAsync` when the family's default is published, `SetDefaultAsync`, and `DeleteAsync` when it promotes a successor. Each holds the exclusive family advisory lock. Unpublishing the default is refused. Every start takes the shared family lock first (`LockFamilyForStartAsync`).
- **Timer starts are not a model.** `SetDefaultAsync` and `DeleteAsync` retire timer-start subscriptions in their transaction, but [TimerStartReconciliationService.cs](../../Flowbit/src/Flowbit.Worker/TimerStartReconciliationService.cs) creates them later, up to one reconcile period after the default changed. A signal thrown in that gap would be missed.
- **`IsEntry`.** It is true for `startEvent`, `messageStartEvent`, and `timerStartEvent`. It feeds the validator's entry rules, the lock-order proof, `WorkflowAiProposalChecks.Reachable`, the assistant's layout roots, and both `HasBusinessKeys` functions.
- **Per-type branches.** The business-key, idempotency, collision, and topology rules in [WorkflowDefinitionValidator.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowDefinitionValidator.cs), and `EnsureEntryRuntimeContract` in the engine, each choose an entry's declared names or its label with a branch on `IsMessageStart`.
- **Single-outgoing rule.** The validator's node loop lists every type that must have exactly one outgoing flow, with a label chain, and `ValidateEntryTopology` returns without a message when the count is not one. A type missing from that list passes with no flow or with two. The engine's `SelectPassThroughFlow`, the note switch of `ResolvePassThroughAsync`, and `SequenceFlowTraversalKind` have label chains of their own.
- **Lifecycle capture.** No migration that defines a capture function names an entry type, so capture needs no change. `WorkflowAiDiagnostics.DefinitionPointer` finds a node from any `<word> #<id>`, so a new noun needs no change there.
- **Successor.** `DeleteAsync` promotes only a published version, and the service is the only caller of `SetDefaultAsync` and always passes `true`.
- **Business keys and idempotency.** Once one entry configures `businessKey`, every entry except a timer start must. A message start's key must be a required single `string` mapping with a path and no default. `idempotency` is `{ headerName, variable }` and is read from an HTTP header.
- **Message start.** `StartByMessageAsync` is a method of its own that resolves the typed mappings in a context without an instance before it reserves anything. It shares no code with `StartInstanceCoreAsync`, which the timer-start job calls with `joinAmbientTransaction` and `forceDurableActivities`.
- **Who can start what.** `POST /api/instances` and the start page accept `IsStart` only, the message-start route selects `IsMessageStart` only, and a call starts the target's default plain start.
- **`signal`.** `SignalEventModel.OutputMappings` is a list of the typed message-mapping model, and the collectors of variable declarations already read `signal.outputMappings` of any node. `IsSignalEvent` is true for the four types that carry `signal` and gates the migrator's clearing of `signal` and `RequiresDurableProcessing`. `HasSignalWaits` gates every subscription statement of an instance transition.

Editor ([flowbit-editor.js](../../Flowbit/src/Flowbit.Ui/wwwroot/editor/flowbit-editor.js)):

- **No entry predicate.** `connectionTargetProblem`, `scopedInterruptTargetProblem`, `validateSaveEntries`, `validateSaveEntryIdentity`, and `signalThrowHasBusinessKeys` each list the three entry types.
- **Shapes.** `renderNodes` draws no ring for a start type. `icon-signal-catch` is the outlined triangle.
- **Inspector.** `renderSignalSection` has a wait mode and a throw mode. The wait mode's mapping rows and the message start's rows both end in `appendTypedOutputMappingFields`, which shows the type, **Required**, the default, and the validation. `renderBusinessKeySection` and `renderIdempotencySection` are called for a start and a message start.
- **Load and export.** The loader chooses a `signal` normalizer by `isSignalWaitType` or `throwsSignalType` and keeps `businessKey` and `idempotency` only for a start and a message start. Export deletes `variables` on a message start and a timer start.

Flowbit.Ui and suites:

- The **Signals** page words every count as waits, and `SignalDeliveries.razor` links `instances/{InstanceId}` for every row.
- The smoke suite ends at E24, H17, and R16, and it runs with durable publication disabled. The acceptance suite's signal scenarios end at S9.
- Tests pin 28 node types, 73 examples, 71 tables, and 91 assistant workspace files.

## Builds on

- The message start event: typed start mappings resolved before anything is reserved, and starting without a user.
- The four default-changing operations of `WorkflowDefinitionRepository` and their exclusive family lock.
- The delivery job processor, its outcomes, its exhausted-lease reconciliation, and the rule that a pending delivery blocks the deletion of its version.
- Business-key claims, which already reject a duplicate start inside the start transaction.
- The chain count of [EVENT-04B](event-04b-signal-throw-and-end-events.md#loop-guard), and the rule in AGENTS.md that a new way to create a token hands the count over.
- The `instance` thrower, so a started instance's history can name the instance and event that threw the signal.
- `renderSignalSection`, `appendTypedOutputMappingFields`, and `renderBusinessKeySection` in the editor.

## Open questions

No question remains open. The plan decides the four original questions and the ones it raised.

| Question | Decision |
| --- | --- |
| Where does a start subscription live? | In a new table, `signal_start_subscriptions`, with one row per family and signal name. `signal_subscriptions` stays the table of waits: its rows live as long as a wait and are written under the instance lock, and the version rules, reactivation, the summary, and instance detail all read it that way. |
| Which scopes reach a start? | A family scope and all workflows. A business-key scope and an instance scope name existing instances and start nothing. From a workflow, the scope kinds `thisWorkflow`, `workflow`, and `allWorkflows` reach starts. |
| Does saving reject a workflow that throws the signal that starts it? | Yes, decided on 2026-11-22. A throw or end event cannot throw a name that a signal start of the same definition starts on, with a scope that includes the definition's own family: `thisWorkflow`, `allWorkflows`, or `workflow` with its own key. No design was found that needs a workflow to start itself by signal, the mistake is costly, and the rule can be lifted later without breaking a saved definition. Both validators apply it, and the editor reports it while the diagram is edited. |
| What does that rule not see? | A loop through other families, which no single definition shows. The started instance's first token continues the broadcast's chain count, so such a loop stops at `Workflow.Async.MaxConsecutiveAutomaticActivations` with a refused throw. A user's action in between starts a new chain. |
| What does the chain count not bound? | A loop that widens. When two families start on one name and throw a second, and a third family starts on the second and throws the first, every round doubles the instances, and the chain count limits only the depth. Such a loop is slowed by the Worker and stopped by `Signals.MaxOpenWorkflowBroadcasts`, after it may have created more instances than that budget. An operator stops it by setting a default without the signal start: listening ends at once, and frozen starts are then skipped. The guides say so. |
| Does a start count toward the recipient limit? | Yes, toward the same limit. A broadcast freezes at most one start per family, so starts are few beside waits, and a second limit would add a setting without a protection. |
| Which version does a frozen start create when the default changes before the delivery runs? | The version that is the family's published default when the delivery runs, if it still starts on the name. A frozen start belongs to the family, not to a version, as every other start by key resolves the default when it starts. Otherwise the delivery is skipped with `subscription_ended`. |
| Who keeps the subscriptions in step? | The four operations that change a family's default, in their own transaction. No Worker reconciliation is added: a signal start has nothing to schedule, and a subscription that appears a period late misses signals. |
| How is a throw kept from racing a deletion? | The freeze share-locks the start subscription rows it reads, and an operation that can change a family's default first locks the family's rows for update. So the operation waits for a throw in flight and then sees its pending delivery, or the throw waits and then sees the rows as the operation left them. |
| Does that break "a throw waits for no instance"? | No. The rows belong to a family, and only a publish, a default change, or a deletion writes them. A throw can wait for one such transaction; it never waits for instance work. |
| Does a signal start take `idempotency`? | No, and both validators reject it. Idempotency reads an HTTP header, which a broadcast does not have. The delivery row already gives one start per broadcast and family, the API replays a repeated throw by its own `idempotencyKey`, and a throw from a workflow commits once with its step. Two broadcasts that announce the same case are a matter of identity, which `businessKey` decides. Reading a key from the payload is a later release. |
| Does it take a `businessKey`? | Yes, by the entry rule: once one entry configures a key, the signal start must, and its key is a required single `string` mapping with a path and no default. A timer start stays exempt because it has no payload; a signal start has one. |
| What is a duplicate business key? | A `skipped` delivery with the code `business_key_conflict` and a description that names the owning instance. It is an expected outcome, like a wait that ended, and **Retry failed** does not run it again. |
| Can one definition have several signal starts? | Yes, with different names. The same name twice is rejected, so a family has at most one start per name and a broadcast at most one delivery per family. |
| What do the mappings declare? | Start variables, by the message start's rules: each states `dataType` and `isArray`, has a path or a default, and cannot name a process variable. The signal rule also holds: no mapping writes or reads a shared alias. |
| Who may start a workflow by signal? | Whoever may throw: a user with a role from `Signals.RequiredRole`, a managed client with `signals.throw`, and a published definition that throws. Publishing a signal start is the author's consent. A start role would have nobody to check: the thrower does not address the workflow. |
| What does **Retry failed** do with a failed start? | It moves the delivery to the version that is the family's default now. When the family no longer starts on the name, the delivery ends as `skipped` instead. |
| Is a job kind, a completion reason, or a route added? | No. The delivery job, the visit reasons, and the seven signal routes stay. New are a delivery kind, two result codes (`started`, `business_key_conflict`), one history note and traversal kind (`signalStart`), and one count on the broadcast. |
| How does the instance record the broadcast? | With a `signalReceived` row on the start event, whose payload has the event kind `start`, before the row that leaves the start event. Instance detail already links that note to its broadcast. |
| Do existing counts change their meaning? | No. A broadcast's counts are deliveries, and a start is a delivery. The summary's `count` stays the number of waits, and `startCount` is added beside it. |
| What does `IsSignalEvent` mean afterwards? | Still every type that carries `signal`: now five. `IsSignalWait` and `ThrowsSignal` do not change, so a definition whose only signal event is a start runs no subscription statement in an instance transition. |

## Plan

Written on 2026-11-22 following [Writing a plan](README.md#writing-a-plan). Effort stays **M–L**: the item adds one node type, a recipient that is not an instance, a table that follows the default version, and a second delivery path, and it touches the engine, both validators, the editor, and Flowbit.Ui. It adds no job kind, setting, or route.

### 1. Scope and invariants

| Type | Flows | Behavior |
| --- | --- | --- |
| `signalStartEvent` | No incoming, exactly one unconditional outgoing | An entry event. A delivered signal creates an instance on it, and the engine follows its flow at once, as it does for a message start. |

The invariants of the [parent plan](event-04-signal-events.md#1-scope-and-invariants) and of [EVENT-04B](event-04b-signal-throw-and-end-events.md#1-scope-and-invariants) hold. These are new or change:

1. **A start is a recipient.** It is frozen in the throw's transaction with the waits, as one delivery row per broadcast and family. Delivery adds no start and repeats none.
2. **One instance per frozen start, or none.** The delivery row is locked and finished in the transaction that creates the instance.
3. **Only the published default listens.** A family's subscription rows change only under its exclusive family lock, in the transaction that changes its default.
4. **A frozen start belongs to the family.** It creates an instance of the default that is published when the delivery runs.
5. **A throw waits for no instance.** It share-locks the start subscription rows it freezes, so it can wait for one publish, default change, or deletion of a listening family, and they can wait for it. This replaces "the throw locks no existing row" for these rows only.
6. **A start delivery locks no existing instance.** It takes the shared family lock, then its delivery row, then a business-key claim, and then writes new rows. It never locks the broadcast.
7. **Receivers are independent.** A start that fails or is skipped changes no other delivery and not the throw.
8. **The chain continues into the new instance.** Its first token takes the broadcast's `AutomaticActivationCount`.
9. **A signal start is system-only.** It runs as the fixed actor `signal` with no roles and no claims, and no request can select it.
10. **Start mappings write instance variables only.** No mapping reads or writes a shared alias.
11. **The cost is on the throw, not on instances.** An instance transition runs no new statement. A throw with a family or all-workflows scope runs one more indexed statement. An operation that can change a family's default runs one locking read of the family's rows.
12. **The right to throw includes the right to start.** No role is checked when a start is delivered.
13. **A business key is business data.** No log statement, metric, or result description contains it.
14. **A definition does not start itself.** Saving rejects a throw that would reach a signal start of its own definition.

This release adds no payload filter on a start, no start by business-key scope, no idempotency from the payload, no shared alias in a start mapping, no second start with the same name, no start role, no webhook event, and no Worker reconciliation.

### 2. Workflow JSON and validation

This is a fragment, not a complete workflow:

```json
{
  "initialEventId": null,
  "flowNodes": [
    {
      "id": 1,
      "type": "signalStartEvent",
      "name": "Recall announced",
      "laneId": 1,
      "x": 80,
      "y": 160,
      "signal": {
        "name": "warehouse.recall-announced",
        "outputMappings": [
          { "variable": "recallNumber", "path": "recallNumber", "dataType": "string", "isArray": false, "required": true },
          { "variable": "product", "path": "product.name", "dataType": "string", "isArray": false, "required": true },
          { "variable": "reason", "path": "reason", "dataType": "string", "isArray": false, "required": false, "defaultValue": "Not stated" }
        ]
      },
      "businessKey": { "variable": "recallNumber", "uniqueness": "all" }
    }
  ],
  "sequenceFlows": [
    { "id": 101, "sourceRef": 1, "targetRef": 2 }
  ]
}
```

| Subject | Contract |
| --- | --- |
| `signal.name` | The rule of the other signal events: required literal text of 1 to 100 lowercase letters, digits, `.`, `_`, or `-`, starting with a letter or digit. |
| `signal.outputMappings` | The start-variable declarations, at most 100; an empty list starts an instance without variables. Each mapping states `variable`, `dataType`, and `isArray`, and has a `path` unless it has a `defaultValue`. `required`, `defaultValue`, and `validation` work as on a message start. |
| Mapping targets | Names are unique ignoring letter case, cannot begin with `sys.`, `config.`, or `setting.`, and cannot name a process variable. |
| Shared aliases | A mapping cannot write a shared alias, and its default and validation cannot read one, as on a signal catch. |
| `signal.scope` and `signal.payload` | Rejected, as on a catch and a boundary. |
| Self-start | A throw or end event of the definition cannot throw a name that one of its signal starts starts on, with the scope kind `thisWorkflow`, `allWorkflows`, or `workflow` and the definition's own key. The kinds `thisInstance`, `businessKey`, and `ownBusinessKey` reach no start and are allowed, and so is `workflow` with another key. |
| One start per name | Two signal starts of one definition cannot have the same name. Different names are allowed. |
| `businessKey` | Optional. Once any entry except a timer start configures it, the signal start must. Its variable is a mapping that is required, a single `string`, with a path and no default. |
| `idempotency` | Rejected. |
| Other settings | `roles`, `variables`, `callOnly`, `message`, `timer`, async flags, job policies, boundaries, and multi-instance settings are rejected. |
| Topology | No incoming flow. Exactly one outgoing flow, which is unconditional and carries no user-action or multi-instance settings. |
| `initialEventId` | Cannot reference a signal start. A definition whose only entries are signal starts leaves it null. |
| Other entries | A definition can mix a signal start with plain, message, and timer starts. |
| Same name as a wait | Allowed. A throw to the family then starts an instance and reaches the waits of the running ones. |

Messages, the same in the API and the editor. The checks run in this order after the shared type and topology rules, and the first problem of a node is the one the fixtures compare.

| Problem | Message |
| --- | --- |
| Missing `signal` or name | `Signal start event #1 must define signal.name.` |
| Invalid name | `Signal start event #1 signal.name must be 1 to 100 lowercase letters, digits, '.', '_', or '-', starting with a letter or digit.` |
| Incoming flow | `Signal start event #1 cannot have incoming sequence flows.` |
| Not exactly one outgoing flow | `Signal start event #1 must have exactly one outgoing sequence flow.` |
| Outgoing flow with a condition or action settings | `The outgoing sequence flow from signal start event #1 must be unconditional and cannot define action or multi-instance metadata.` |
| `scope` or `payload` | `Signal start event #1 cannot define signal.scope or signal.payload; only signal throw and end events send a signal.` |
| Mapping without a type | `Signal start event #1 output mapping for 'product' must declare dataType and isArray.` |
| Mapping without a path or a default | `Signal start event #1 output mapping for 'product' must have a path unless defaultValue is configured.` |
| Other mapping problems | The message start's and the signal catch's messages, with the noun `Signal start event`. |
| Name used twice | `Signal start event #5 starts on 'orders.created', which signal start event #1 already starts on; a workflow starts once per signal.` |
| `idempotency` | `Signal start event #1 cannot define idempotency; a broadcast starts one instance per workflow, and businessKey rejects a repeated case.` |
| Self-start, reported on the throw or end event | `Signal throw event #7 throws 'orders.created' to a scope that includes this workflow, which starts on it at signal start event #1; every instance would start another.` |
| Key variable is not a mapping | `Entry event #1 businessKey variable 'recallNumber' is not a typed output mapping on the signal start event.` |
| Key mapping has the wrong shape | The existing `Entry event #1 businessKey mapping 'recallNumber' must be a required scalar string with an explicit path and no defaultValue.` |
| Unsupported settings, API only | `Signal start event #1 cannot define task, role, variable, timer, conditional, message, async, or multi-instance settings.` |
| No entry event | The existing sentence gains the type: `Workflow must have at least one entry event (startEvent, messageStartEvent, timerStartEvent, or signalStartEvent).` |

Each node message starts with `<noun> #<id>`, which `WorkflowAiDiagnostics.DefinitionPointer` turns into a JSON pointer for the assistant. The changed entry sentence changes arrays that `EditorValidatorCharacterizationTests` pins; update them with it.

Model and service changes:

- **Entry helper, first without behavior change.** Add `EntryEventRules` to `Flowbit.Shared` with the label of an entry event, the names it declares, and the mapping its business key names. Replace the per-type branches listed under [Facts](#facts-the-plan-rests-on) with it. Exit with the whole suite green and no other change.
- **`BpmnFlowNodeTypes`.** Add `SignalStartEvent` and `IsSignalStart`. `IsEntry`, `IsPassThrough`, `IsSupported`, and `IsSignalEvent` gain it. `IsStart`, `IsSignalWait`, and `ThrowsSignal` do not.
- **`SignalEventRules`.** `Label` returns `Signal start event #1`. Add `StartProblems(definition, node)` with the ordered problems of one start, `HasSignalStarts(definition)`, cached per definition object as `HasSignalWaits` is, and `SignalStarts(definition)`, which lists each start's node and name for the subscription rows. `ThrowProblems` gains the self-start rule as its last check, for a throw and an end event alike; it compares a `workflow` scope's key with the definition's `id`.
- **`WorkflowDefinitionValidator`.** A new `ValidateSignalStart` runs after `ValidateSignalThrow` in the node loop. `ValidateAuthoredSignalEventMetadata` rejects `idempotency`, `roles`, and `variables` on the authored node before the migrator clears them, and allows `businessKey`. `ValidateSignalMappings` runs for the start with its noun. `ValidateBusinessKeys`, `ValidateEntryProcessVariableCollisions`, and `ValidateEntryTopology` cover the start through `EntryEventRules`. The single-outgoing rule of the node loop and its label chain gain the type; without that, a signal start with no flow or with two would save.
- **Engine labels.** The label chain of `SelectPassThroughFlow`, the note switch of `ResolvePassThroughAsync`, and `SequenceFlowTraversalKind` gain the type, with the note and traversal kind `signalStart`.
- **`WorkflowModelMigrator`.** `ApplyNodeInvariants` gets a branch modeled on the message start's: it clears the task, role, variable, service, script, timer, and attachment settings and `idempotency`, keeps `signal` and `businessKey`, and normalizes the mappings with `NormalizeSignalMappings`. Older documents do not contain the type and normalize as before.
- **`WorkflowDefinitionReadinessChecker`.** `RequiresDurableProcessing` is true for the start through `IsSignalEvent`. No publication check is added: a start names no other family.
- **`WorkflowAuthoringJson.CreateSchema`** reads the node types from the constants, so the schema gains the type when the constant is added.

### 3. Persistence and migration

Two migrations. `AddSignalStartEvents` runs in a transaction. `ValidateSignalStartConstraints` runs outside one, because `signal_deliveries` can be large; a stopped run can apply it again. Update the entities, the records, `AppDbContext`, and the model snapshot with them.

| Object | Change |
| --- | --- |
| `signal_start_subscriptions` | New table: `Id`, `WorkflowKey`, `SignalName` (collation `C`), `WorkflowDefinitionId`, `SignalNodeId`, `SignalNodeName`, `CreatedAt`. A row exists exactly while the family's published default has a signal start with the name. It has no status column. |
| Its keys | A unique index on `("SignalName", "WorkflowKey")`, which serves both freeze statements; an index on `("WorkflowKey")` for the family's locking read; and a restrictive foreign key on `("WorkflowDefinitionId", "WorkflowKey")` to `workflow_definitions`, as `signal_subscriptions` has. |
| `signal_deliveries."Kind"` | New `varchar(16) NOT NULL DEFAULT 'wait'`: `wait` or `start`. |
| `signal_deliveries` columns | `SubscriptionId`, `InstanceId`, `TokenId`, and `ActivationId` become nullable. For a start, `InstanceId` is null until its instance exists and then holds the created instance; it stays a plain column. |
| `CK_signal_deliveries_kind_shape` | New, added `NOT VALID` and validated by the second migration. A `wait` has all four columns. A `start` has no subscription, token, activation, or attached node, `CancelActivity` false, and an instance exactly when its status is `delivered`. |
| `IX_signal_deliveries_start` | New partial unique index on `("BroadcastId", "WorkflowKey")` where `"Kind" = 'start'`, built concurrently by the second migration: one start per broadcast and family. |
| `signal_broadcasts."StartDeliveryCount"` | New `integer NOT NULL DEFAULT 0`: how many of `TotalDeliveryCount` are starts. `CK_signal_broadcasts_start_count` holds it between 0 and `TotalDeliveryCount` and is validated in the first migration, as the table holds one row per broadcast. |
| `signal_broadcasts."RecipientsJson"` | Each family entry gains `startCount`. Entries stored earlier read as 0. |

- **Derived rows.** `signal_start_subscriptions` holds nothing that the published defaults do not determine. No delivery refers to a row's ID, so the rows can be replaced.
- **Existing rows.** Every existing delivery is a `wait` with all four columns, which satisfies the new check. Nothing is backfilled.
- **Readers.** Every reader of a delivery handles a start's null columns: the record and the DTO mapper, the retry statement, the exhausted-lease statement, the deletion assessment, and the page. Step 3 lists them with a search for each column before it makes the columns nullable.
- **Cost.** Adding a column with a constant default and dropping `NOT NULL` are catalog changes. The check's validation and the concurrent index build scan `signal_deliveries` without blocking writes.
- **Retention.** The `signalBroadcasts` category removes a finished broadcast with its deliveries of both kinds. After that, the started instance's history still names the broadcast ID, as a receiver's does today.
- **Unchanged.** `signal_subscriptions`, `signal_broadcast_jobs`, `workflow_jobs`, `CK_node_executions_completion_reason`, the lifecycle capture functions, and `LifecycleEventCaptureTests.JobKindReportsNode`. No job kind, completion reason, setting, or retention category is added.
- **Counts.** The schema grows from 71 to 72 tables.
- **Down migration.** It refuses while a delivery of kind `start` exists or a stored definition contains a `signalStartEvent`, because the restored `NOT NULL` columns would reject the first and nothing would run the second. Retention removes such deliveries once their broadcasts are finished and old enough.

### 4. Start subscriptions and the default version

The rows are written in `WorkflowDefinitionRepository` and nowhere else, by two private helpers:

- **`LockSignalStartsAsync(workflowKey)`** selects the family's rows `FOR UPDATE`. It is the first statement after the exclusive family lock in every operation that can change the family's default, before any other write of that transaction.
- **`SyncSignalStartsAsync(workflowKey, default)`** runs once the operation knows the family's new published default. It makes the family's rows equal that version's signal starts: it updates in place the row of a name that the old and the new default both start on, deletes the rows of names that are gone, and inserts the rows of new names. In `DeleteAsync` it runs before the definition row is deleted, toward the successor the assessment chose, because the rows hold a restrictive foreign key to the version they name.

| Operation | The family's published default | Rows |
| --- | --- | --- |
| `AddAsync`, first version of a key, saved as published | None, then this version | Insert its starts. |
| `AddAsync`, any other case | Unchanged | None. |
| `SetPublishedAsync(true)` on the family's default | None, then this version | Insert its starts. |
| `SetPublishedAsync(true)` on another version | Unchanged | None. |
| `SetPublishedAsync(false)` | Refused for the default | None. |
| `SetDefaultAsync(true)` | The earlier default, then this version | Lock, then update, delete, and insert. |
| `SetDefaultAsync(false)`, which no caller uses today | This version, then none | Lock, then delete. |
| `DeleteAsync` of the default | This version, then the promoted successor or none | Lock, then assess, then update, delete, and insert, and only then delete the definition row. |
| `DeleteAsync` of another version | Unchanged | None. |

- **Why the lock comes first.** A throw holds share locks on the rows it froze until it commits. An operation that locks the rows first therefore waits for that throw, and its deletion assessment then finds the throw's pending delivery and refuses with the existing `409`. Without the lock, the assessment could run before the throw commits and the deletion could remove the definition under a delivery job.
- **Why a persisting name is updated in place.** A throw that waits for a default change reads the rows again when the change commits. It finds an updated row with its new values, but it would not find a row that was deleted and inserted again. So a throw that overlaps a default change still starts the family when both versions start on the name.
- **Write order.** `SetDefaultAsync` and `DeleteAsync` already write definition rows after their checks. The lock on the subscription rows comes before those writes, so an operation never waits for a throw while it holds a lock that the throw needs for its job rows.
- **What waits behind the operation.** While an operation waits for a throw, it holds the family's exclusive lock, so every start of that family waits too. The wait ends when the throwing transaction ends.
- **A version change of instances** moves no subscription. Only the default decides who listens.
- **Cost.** An operation on a family without rows locks nothing, and `SyncSignalStartsAsync` returns at once when neither the old nor the new default has a signal start.
- **No reconciliation.** The table is right after every committed operation. Setting the default again rebuilds a family's rows, which the deployment guide names as the repair after a mixed rollout.

### 5. Freeze, counts, and lock order

`FreezeDeliveriesAsync` runs a second statement for the family and all-workflows scopes, after the statement for the waits and with the allowance the waits left. It returns both counts.

```sql
INSERT INTO flowbit.signal_deliveries
    ("BroadcastId", "Kind", "WorkflowDefinitionId", "WorkflowKey", "SignalNodeId",
     "CancelActivity", "Status", "AttemptCount", "CreatedAt", "UpdatedAt")
SELECT @broadcastId, 'start', s."WorkflowDefinitionId", s."WorkflowKey", s."SignalNodeId",
       FALSE, 'pending', 0, now(), now()
FROM flowbit.signal_start_subscriptions s
WHERE s."SignalName" = @signalName
  AND s."WorkflowKey" = @workflowKey   -- family scope only
ORDER BY s."Id"
LIMIT @remaining
FOR SHARE OF s
```

One literal statement per scope kind, as today. The business-key and instance scopes run no second statement.

| Subject | Rule |
| --- | --- |
| Recipient limit | Waits and starts count together against `Signals.MaxSubscriptionsPerBroadcast`. Over the limit, the writer deletes the broadcast and every frozen delivery before it reports the refusal, as it does today. |
| Refusal wording | "waiting subscriptions" becomes "recipients" in the API's `400` and in a workflow throw's refusal, because the limit now counts starts. |
| Counts | The writer stores `StartDeliveryCount` and the per-family `startCount`. `SignalBroadcastWriteResult` gains the start count, and the history payload of a `signalThrown` row gains `startCount`. |
| Jobs | One delivery job per represented definition, as today. A start's definition is the frozen default, so its job exists for a version that exists. |
| Budgets | Unchanged. A broadcast that freezes only starts is open while it delivers and counts toward its thrower's budget. |

What the freeze sees:

| Case | Start frozen? |
| --- | --- |
| A family whose published default has a signal start with the name | Yes. |
| A draft, or a published version that is not the default, with such a start | No. |
| A family whose default changed in a transaction that committed first | For the new default, when it starts on the name. |
| A family whose default change is in flight | The throw waits for it, then as the row above. |
| A business-key or instance scope | No. |
| The throwing instance's own family, with `thisWorkflow`, `allWorkflows`, or `workflow` and its own key | Yes, when the family's default starts on the name. Saving rejects one version that both throws and starts so, which leaves the case of an instance of an older version that throws while a newer default starts. The started instance runs the default, which cannot throw the name back, and it continues the chain count. |
| A family with a signal start and waits of the same name | Both: one start, and each wait. |
| A wait of the instance that this broadcast starts | Not a recipient. The wait begins after the throw. |

Lock order:

- **A throw** takes no runtime lock. It share-locks start subscription rows in ascending ID after every other lock its transaction holds, and it takes no family lock afterwards. Engine code already takes a family lock first or not at all.
- **A default-changing operation** takes the exclusive family lock, then the family's subscription rows, then its definition rows.
- **A start delivery** takes the shared family lock, then its delivery row, then a business-key claim, and then writes new rows. Routing may then take shared-variable rows by the access plan, as any start does.
- **Cancel and retry** lock the broadcast row, then delivery rows, and for a retry the subscription rows in share mode. A delivery never locks the broadcast.
- **Held locks.** A throw from a workflow holds its share locks until its step commits. A step that goes on to a synchronous service task holds them for that call's timeout, and a publish or default change of a listening family waits that long. The guides say so beside the advice to put an `asyncBefore` task in front of a throw.

### 6. Start delivery

Add a `WorkflowEngineService.SignalStarts.cs` partial with `StartFromSignalAsync`. `DeliverSignalAsync` calls it when the delivery it previews has the kind `start`, so the processor's loop, paging, and recount do not change.

**Extraction, first without behavior change.** Move the part of `StartByMessageAsync` from the business-key reservation to the ownership inheritance into a private method that both starts call. It takes the workflow, the entry event, the actor, the resolved values, the idempotency input, and options for durable activities, the chain count, and a history row to add before routing. Move the typed mapping resolution into a second method that takes the mappings, the payload, and the context. Exit with `MessageStartApiTests` and the whole suite green.

Steps of one start delivery, in one transaction:

1. **Family lock.** `LockFamilyForStartAsync` for the delivery's family.
2. **Delivery row.** Lock it. A delivery that is no longer pending returns its stored outcome.
3. **Default.** Read the family's published default and find its signal start with the broadcast's name. Without one, finish as `skipped` with `subscription_ended`.
4. **Entry checks.** The checks every start runs: the entry contract, the conditional-definition check, and the business-key and required-assignment checks of the family.
5. **Mappings.** Resolve the typed mappings against the broadcast's payload in the context without an instance that a message start uses, with defaults, required values, types, and validations.
6. **Business key.** Normalize it and reserve the claim.
7. **Instance.** Create it on the start event as `signal`, bind the key, give the first token the broadcast's chain count when it is above 0, write the mapped values and the process-variable defaults, add the `signalReceived` row, and route with `forceDurableActivities`, then run multi-instance initialization and ownership inheritance.
8. **Finish.** Mark the delivery `delivered` with `started`, the instance, and the version and node that started, and commit.

| Outcome | Status | Result code |
| --- | --- | --- |
| An instance was created | `delivered` | `started` |
| The family has no published default, or its default does not start on the name | `skipped` | `subscription_ended` |
| An instance already owns the business key | `skipped` | `business_key_conflict` |
| The payload fails a mapping | `failed` | `signal_payload_invalid` |
| The start is rejected: an entry check, a process-variable default or validation, or a throw that is refused during routing | `failed` | `signal_delivery_rejected` |
| A conflict while starting | `failed` | `signal_delivery_rejected` |
| An unexpected error | Retried by the job, then `failed` | `delivery_error` |
| The job lost its final lease | `failed` | `lease_exhausted` |
| The broadcast was cancelled first | `cancelled` | `broadcast_cancelled` |

- **Conflicts.** A wait's conflict is skipped as `stale` because its instance changed. A start has no earlier state, so its conflict fails and can be retried. The engine reports a start's skip or failure with one new exception type that carries the status and the code, and the processor records it as it records the others.
- **Descriptions.** `business_key_conflict` reads `Instance #42 already has this start's business key.` No description contains a key or a payload value. A mapping failure can quote a validation expression, which a managed client does not receive, as today.
- **Exactly once.** A second lease of the same job blocks on the delivery row and then finds it finished. A crash before the commit leaves the delivery pending and no instance. A crash after it leaves a finished delivery that the retried job skips. A cancel waits for the row and leaves a delivered start as it is.
- **Retry failed.** `RetryFailedDeliveriesAsync` share-locks the family's subscription row for the broadcast's name. A failed start whose family still has the row becomes pending with the row's version and node. One whose family has no row ends as `skipped` with `subscription_ended`. A retry that leaves nothing pending recounts and returns the broadcast without a job.
- **Loop guard.** A broadcast thrown through the API stores 0, so its starts begin a new chain. One thrown by a workflow stores its thrower's count plus one. A throw at the limit inside the start's own routing is refused, which fails the delivery and creates no instance.

Records:

- **Instance.** `StartedBy` is `signal`, with no roles and no claims.
- **History.** First a `signalReceived` row from the start event to itself, with the broadcast ID, the signal name, the event node, the event kind `start`, and the thrower, including the throwing instance and node for an `instance` thrower. Then the row that leaves the start event, with the new note `signalStart`. Neither has an action ID, so claim and assignment inheritance ignore them.
- **Flow evidence.** The start's outgoing flow records a traversal of kind `signalStart` and no action.
- **Node activity.** The start's visit completes with the reason `signalReceived`, as a signal catch's does.
- **Variables.** The mapped values are written by `signal` on the start's visit.
- **Lifecycle webhooks.** No event type is added. The instance is captured as any started instance, and no capture function names an entry type.
- **Logs.** One information line with the broadcast, the delivery, and the instance IDs.

### 7. Versions, analyses, and other features

- **Version changes.** No token rests on a start event, and no job or subscription of an instance names one, so no blocker or warning is added. `ValidateExactTopology` compares the type and its flow as it compares any node.
- **Reactivation.** No rule is added.
- **Shared-variable lock order.** The proof starts a segment at every `IsEntry` node. The start's access plan has no key from its mappings and the reads of the process-variable defaults that every entry has. A test pins it.
- **Graph analyses.** `WorkflowAiProposalChecks.Reachable`, the assistant's layout roots, the validator's rule that a scoped interrupt cannot target an entry, and the reachability rules take the start through `IsEntry`.
- **Call activities.** A call cannot target a signal start. A child can throw a signal that starts other workflows, and the chain count continues through both.
- **Timer starts and business calendars.** Unchanged. A signal start has no timer, and timer-start reconciliation reads only timer starts.
- **Conditional events and expression timers.** A start's mappings are declarations for their analyzers, which already read `signal.outputMappings`.
- **Deleting a version.** The existing blocker for a pending delivery covers a pending start. A failed or skipped start blocks nothing.

### 8. API contracts and authorization

No route is added.

| Contract | Change |
| --- | --- |
| `SignalDeliveryDto` | Gains `kind`: `wait` or `start`. `instanceId` becomes nullable: null for a start that has created no instance, and the created instance for a delivered start. `signalNodeId` is the start event. |
| `GET /api/signal-broadcasts/{id}/deliveries` | Gains an optional `kind` filter; another value is `400`. The `instanceId` filter finds the start that created an instance. |
| `SignalBroadcastSummaryDto` and `SignalBroadcastDetailDto` | Gain `startCount`. Each entry of `recipientsByWorkflowKey` gains `startCount`. |
| `GET /api/signal-subscriptions/summary` | Each item gains `startCount`, and a family that only starts on the name is listed with a `count` of 0. With a `businessKey` or `instanceId` filter, `startCount` is 0. |
| `POST /api/signal-broadcasts` | The request does not change. A family or all-workflows throw can now create instances, and the recipient limit counts starts. |
| Result codes | `started` and `business_key_conflict`. `subscription_ended` can describe a start. |
| Retry failed | Moves a failed start to the family's default, or ends it as `skipped`. |
| Instance history | Can contain `signalStart`, and a `signalReceived` payload can have the event kind `start`. |
| Flow evidence | `FlowInfo` can return the traversal kind `signalStart`. |
| `POST /api/instances` and the message-start route | Still cannot start a signal start: the first returns its existing `400`, and the second never selects one. |
| Node types | Responses and the node-execution filters accept the type, which the filters read by reflection. |
| Definition routes | Saving applies the rules of [section 2](#2-workflow-json-and-validation). Saving as published, publishing, set-default, and delete maintain the subscriptions and return no new error. |

- **Authorization.** No route changes its policy. A user with a role from `Signals.RequiredRole`, a managed client with `signals.throw`, and a published definition that throws can each start a workflow that listens. A client that only threw to waits before the upgrade gains this once a definition with a signal start is published. The deployment guide and the node reference state it.
- **Managed clients.** A client sees the `kind` and the `instanceId` of the deliveries of its own broadcasts, without `resultDescription`, as today.
- **Payload visibility.** Mapped payload values become instance variables of the started instance and are readable by whoever can read that instance.
- **OpenAPI.** Give the new properties `<param>` comments, update the comment on `instanceId`, and update the hand-written schema tables of the API reference.

### 9. Editor

All changes are in the canonical assets under `Flowbit/src/Flowbit.Ui/wwwroot/editor/`.

Types and shapes:

- **Type and predicates.** Add `SIGNAL_START_EVENT` to `NODE_TYPE` and `isSignalStartEventType`. `isEventType`, `isSingleOutgoingType`, `isPassThroughType`, and `isSignalEventType` gain it; `isSignalWaitType` and `throwsSignalType` do not. Add `isEntryEventType` for the four entry types and use it in `connectionTargetProblem` and `scopedInterruptTargetProblem`.
- **Option.** "Signal Start Event (start on a broadcast signal)" follows "Message Start Event (webhook start)". The option needs a `NODE_ICON_SYMBOL` entry, which a test already requires.
- **Shape.** The start circle without an inner ring, with `icon-signal-catch`, the outlined triangle. `flowbit-editor.css` adds the type to the message start's body rule and to the icon group of the catching events. No new symbol is needed.
- **Flow.** Its outgoing flow is dashed, as every pass-through flow is, and gets no label of its own.
- **Search.** `searchDiagram` names the type "signal start".

Load, export, and type changes:

- **Normalizer.** `normalizeSignalDefinition` gets the typed-start mode that `normalizeMessage` has, and the loader, the export, and `applyTypeInvariants` choose it for the start.
- **Entry settings.** The loader and `applyTypeInvariants` keep `businessKey` for the start and drop `idempotency`, `roles`, and `variables`, so only a raw request reports those three. Export deletes `variables`, as for a message and a timer start.
- **Type changes.** In `changeNodeTypeFromInspector`, a node that becomes a signal start gets `{ name, outputMappings: [] }` with the name it had as a signal event. Between a message start and a signal start the mappings cross, because both declare start variables. Step 8 first confirms how a plain start's variables become a message start's mappings and uses the same path. A new signal start gets `businessKey` when keys are enabled, and `businessKeysEnabled` counts signal starts.

Inspector:

- **Signal section.** `renderSignalSection` gets a start mode: **Signal name** with the suggestions and the live status line of the catch, and **Start variables from the payload**, the catch's mapping rows without suggestions of declared variables, since a start variable cannot be a process variable.
- **Business key.** `renderBusinessKeySection` is shown for the start, with its mappings as the choices. `renderIdempotencySection` is not.
- **Status lines.** The section's `role="status"` region reports the name problem, a name that another signal start of the diagram uses, and the self-start problem when a throw or end event of the same diagram throws the name with a scope that includes this workflow. The throw's section reports the same problem with the save message, since the throw is the event that save validation names. Both lines appear and clear while either name or the scope is edited.
- **Help** behind `attachInspectorHelp`: which throws start the workflow, that only the published default listens, that one broadcast starts one instance per workflow, that anyone who may throw the signal can start it, what a duplicate business key and a payload that fails a mapping do, and that it needs the Worker. The throw's scope help says which scope kinds also start workflows.
- **Names and focus.** Every control is labeled, so the E18 census passes, and a control that redraws the section returns focus to the control it changed.

Save validation, inside the `// BEGIN/END WORKFLOW SAVE VALIDATOR` markers:

- A new phase `validateSaveSignalStarts` runs after `validateSaveSignalThrows` with the messages and the order of [section 2](#2-workflow-json-and-validation).
- `validateSaveSignalThrows` gains the self-start rule as the last check of a throw and an end event, where `SignalEventRules.ThrowProblems` has it. It compares a `workflow` scope's key with the model's `id`. `SignalThrowFixtures` gains its cases.
- The literal type lists gain the type: `supportedNodeTypes`, `validateSaveEntries` with its per-type entry targets, `validateSaveEntryIdentity` with a branch that reads the key's mapping from `signal.outputMappings`, `signalThrowHasBusinessKeys`, `signalEventLabel`, the entry roots of the graph-scope phase, the entry sentence, and the sentence that every entry must configure `businessKey`.
- One fixture set, `SignalStartFixtures`, feeds `SignalEventRules`, the API validator, and the editor's validator.
- The hand-written predicate stubs in `EditorValidatorTests` gain the new names.

The variable catalog lists a start's mappings as it lists a message start's. Viewer mode draws the shape and needs no other change.

### 10. Flowbit.Ui

- **Signals page, summary.** The page's unfiltered summary now lists the families that start on a signal, which stay listed for as long as their default listens. Its limit of 200 rows is unchanged.
- **Signals page, review.** The status line and the review count starts beside waits, for example "2 waits and 1 new instance in 2 families: `orders` (2), `recall-notices` (1 start)". **Waiting now** becomes **Reaches now**. With no recipient the line keeps its wording, "No instance is waiting for 'x' now.", and a throw that froze no start keeps "froze N recipients". The two introductory sentences say that a signal can also start workflows.
- **Signals page, deliveries.** `SignalDeliveries` shows a start as "Start" in the **Event** column. Its **Instance** cell links the created instance, and reads "No instance" with the family when there is none. A **Kind** filter with **All**, **Waits**, and **Starts** sits beside the status filter and goes through the page's identity guard and request counter.
- **`SignalFormatting`.** `ResultLabel` takes the kind: `started` is "Started an instance", `business_key_conflict` is "An instance already has this business key", and for a start `subscription_ended` is "The workflow no longer starts on this signal", `signal_delivery_rejected` is "The workflow rejected the start", and no result is "Not started yet".
- **Broadcast summary.** The **Recipients** tile names the starts, and the family list reads `recall-notices · 1 (1 start)`.
- **Instance history.** A `signalReceived` row with the event kind `start` reads "Started by signal 'x'" with the thrower and the broadcast link that the row has today. A `signalThrown` row names its starts.
- **Instance diagram.** `ExecutionPositionText.NodeTypeLabel` names the type "Signal start", and a selected signal start shows its signal name and "starts an instance".
- **Unchanged.** The start page filters with `IsStart` and never lists a signal start. The Workflows page, My work, and Retention need no change.
- **Access.** The broadcast link opens the **Signals** page, which needs a role from `Signals.RequiredRole`, as today.

### 11. AI authoring and the portable package

- The hosted assistant's instructions, tool names, and tool descriptions do not change.
- [The portable authoring guide](../../authoring/flowbit-authoring/references/authoring-guide.md) replaces the sentence that signal start events are not supported. It gains a short paragraph: when a signal start fits and when a message start does, that the mappings declare the start variables, that a family or all-workflows throw starts one instance per listening workflow, that a business key rejects a repeated case, and that saving rejects a workflow that throws, to a scope that includes itself, the signal it starts on.
- The hand-written `capabilities` list of `AuthoringPackageBuilder` names signal start events. The schema and the manifest are generated.
- The layout is unchanged: the type name ends in `Event`, and `IsEntry` makes it a root. A test pins that the new example lays out without overlaps.

### 12. Documentation and example

| Owning page | Update |
| --- | --- |
| [BPMN support](../bpmn-support.md) | The node count (29), a row in the supported table with Worker "Yes", the [Signals](../bpmn-support.md#signals) section, and the signal start removed from the [unsupported list](../bpmn-support.md#unsupported-capabilities). |
| [Node reference](../node-reference.md) | The count, a catalog row, a `### signalStartEvent` section, the start's mappings under [Signal configuration](../node-reference.md#signal-configuration), which scope kinds start workflows, the rule against a workflow that throws the signal it starts on, and who can start one. A package test requires the heading. |
| [Developer guide](../developer-guide.md#broadcast-signals) | A subsection **Start a workflow with a signal**: which throws start, the published default, one instance per broadcast and family, the outcomes, the business key, who can start, the loop guard, and a comparison with a message start. Examples stay HTTP-only. |
| [API reference](../api-guide.md#signals) | `kind`, the nullable `instanceId`, the `kind` filter, the start counts, the two result codes, the summary's `startCount`, and the new history note and traversal kind. |
| [Flowbit.Ui guide](../ui-guide.md) | Authoring the event, the review's counts, the delivery row of a start, and the history row. |
| [Deployment](../deployment.md#signal-events) | Both migrations, an upgrade-table row, the rollout order of [section 14](#14-delivery-sequence-rollout-and-risks), who gains the right to start workflows, the repair of a family's rows, what a publish can wait for, how to stop a family from listening and how to stop a loop of starts, and the down guard. |
| [Database schema](../database-schema.md) | The new table, the delivery columns and their nullability, the check, the index, and the broadcast's count. |
| [Engine settings](../engine-settings.md#signal-settings) | That `Signals.MaxSubscriptionsPerBroadcast` counts starts and that `Workflow.Async.MaxConsecutiveAutomaticActivations` bounds a chain of signal starts. No setting is added. |
| [Runtime reference](../../Flowbit/README.md#signal-events) and [AGENTS.md](../../AGENTS.md) | The type, the subscription table and its two helpers, the freeze with its share lock, the start delivery and its lock order, the outcomes, the chain count, the data model, the BPMN mapping, and the editor conventions. Both drop the sentence that signal starts do not exist. |
| [Root README](../../README.md), [AI authoring](../ai-authoring.md), and the [documentation home](../index.md) | The node count and the building-block table. |
| [Signal examples](../../examples/signals/README.md) and the [example catalog](../../examples/README.md) | One example with a walkthrough, the count (74), and the coverage matrix. |
| Browser suite READMEs | The new scenarios and the census fixture. |
| This page, the [backlog](README.md), and the [parent](event-04-signal-events.md#later-releases) | Evidence and status. |

The example, **`05-signal-start-recall-notice.json`**: a signal start on `warehouse.recall-announced` maps `recallNumber`, `product`, and `reason`, uses `recallNumber` as its business key with the uniqueness `all`, and leads to the user task **Write the customer notice** and an end event. Its walkthrough, in Flowbit.Ui and over HTTP in PowerShell and Bash:

1. Review and throw the signal to the workflow family. One instance starts, its delivery row links it, and its history links the broadcast.
2. Throw it to all workflows with another recall number. A second instance starts.
3. Throw the first recall number again. The delivery is skipped because the first instance has the key.
4. Throw a payload without `product`. The delivery fails, and the broadcast is completed with issues.

It needs the Worker. Validate changed relative links, anchors, and the example JSON, compare the changed DTOs with their registrations, tests, and the development OpenAPI document, and run the walkthrough against a local stack with a Worker.

### 13. Tests and verification

| Layer | Cases |
| --- | --- |
| Extractions | The whole suite passes unchanged after `EntryEventRules` and after the message start's extraction. |
| Shared rules | `SignalEventRules.StartProblems`: the name, each mapping problem, a shared alias written and read, the same name twice, and the label. `ThrowProblems`: the self-start rule as the last problem of a throw and of an end event. `SignalStartFixtures` feeds the C# rules and the editor's validator through Jint. |
| Definition validation | Every message of [section 2](#2-workflow-json-and-validation) through the API; `idempotency`, `roles`, `variables`, and `callOnly` on a start; the self-start rule for a throw and an end event with each of `thisWorkflow`, `allWorkflows`, and `workflow` with the own key, and its absence for `workflow` with another key, for the three kinds that reach no start, and for another signal name; the business-key contract with a signal start beside a plain start, a message start, and a timer start; `initialEventId` on a signal start; a definition whose only entry is a signal start; the durable-publication gate; stored definitions of the existing examples byte-identical after normalization. |
| Subscriptions (PostgreSQL) | After each row of the table in [section 4](#4-start-subscriptions-and-the-default-version), the family's rows equal the starts of its published default. A persisting name keeps its row ID. A draft and a published version that is not the default add no row. A deletion that leaves no published version leaves no row. |
| Freeze | A family scope and all workflows freeze the start; a business-key scope and an instance scope do not. One start per family for a family with several versions. A family with a start and waits freezes both. The recipient limit counts starts, and over it nothing is kept. The stored counts per broadcast and per family. A workflow throw with each of the six scope kinds. |
| Start delivery | The instance, its variables with required, optional, default, and missing values, its history rows and payloads, its actor, its traversal kind, and its delivery row. A broadcast without a payload starts a workflow whose mappings all have defaults and fails one with a required mapping. A wait that the new instance reaches in the same transaction is not a recipient of the broadcast that started it. A start with `asyncBefore` work behind it queues that work. Each outcome of the table in [section 6](#6-start-delivery) with its status, code, and description. |
| Default at delivery | The default changes between the throw and the delivery to a version that starts on the name with other mappings, to one that does not start on it, and to none. |
| Exactly once | Two leases of one job, a crash before and after the commit, a cancel during a start, and a retry of a failed start each leave at most one instance. |
| Business key | A duplicate with the uniqueness `all` and `active`, a key that is free again after the owner ended, two broadcasts with the same key delivered at once (one instance, one skipped), and a key next to a plain start of the same definition. No description or log line contains a key. |
| Retry failed | A failed start is retried against a new default, ends as skipped when the family no longer starts on the name, and a retry that leaves nothing pending completes the broadcast. |
| Loop guard | Two families that start each other, each on the signal the other throws, stop at a configured limit of 3 with a failed delivery. A user action between the start and the throw starts a new chain. A throw through the API stores 0. An instance of an older version that throws to its own family starts one instance of a newer default that starts on the name, and that instance continues the count. A loop that widens, with two families that start on one name and throw a second and a third family that starts on the second and throws the first, stops when `Signals.MaxOpenWorkflowBroadcasts`, set low for the test, refuses a throw, and setting a default without the start ends it with skipped deliveries. |
| Races (release gate) | A throw while another transaction holds a recipient's instance row does not wait. A throw that overlaps a default change starts the family once, on either version. For a throw that overlaps the deletion of the default, either the throw reaches no start or the deletion returns `409`, and no broadcast stays `delivering`. Two starts of one family run while a publish waits for both. |
| Cost | A command-capturing test: an instance transition of a definition whose only signal event is a start issues no `signal_` statement, and a throw with a business-key or instance scope issues no statement on `signal_start_subscriptions`. |
| Visibility | A managed client sees the kind and the instance of its own broadcast's start without a description, and gets `404` for another's. The `kind` filter with each value and an unknown value. The summary with and without a filter. |
| Versions and analyses | A version change of a running instance between versions that add, change, and remove a signal start has no blocker and moves no subscription. The lock-order proof, the access plan, reachability, and the layout of the example. |
| Infrastructure | Both migrations up and down, the shape check with each kind, the partial unique index, the down guard, and retention removing a finished broadcast with a start delivery. Confirm that no other test migrates the shared database down past the new migration while a start delivery exists. |
| Editor (Jint) | The shared fixtures and ordered errors, the normalizer's typed-start mode, the round trip, type changes in each direction, invariants, the option order, an icon entry for every option, the section's start mode, the business-key section, the status lines of the start and of the throw for the self-start rule, and the characterization arrays with the changed entry sentence. |
| Flowbit.Ui | `SignalFormatting` with each new label, the review's sentences, the delivery row of a pending and a delivered start, the **Kind** filter and its request, the summary tile, the history text, and `ExecutionPositionText`. |
| Contracts and package | The OpenAPI test for the new properties, the example catalog tests, the authoring package tests with the new heading, and the capability list. |
| Pinned counts | 29 node types, 74 examples, 72 tables, and the assistant workspace file count. |
| Performance (recorded evidence) | With both builds published to one disk: the start throughput of a workflow without signal events before and after the change; the duration of a throw to a family with no start subscription before and after; one broadcast to 1,000 listening families, with the freeze's duration and the time until every instance exists; many concurrent throws to one listening family, which all share-lock the same row; and the duration of a default change for a family with and without signal starts. |

Test isolation. A start subscription outlives the test that created it, for as long as its definition stays the published default, and the unit tests share one database:

- Every test that publishes a signal start uses a signal name of its own, and no existing test that throws to all workflows may use a name that a start test publishes. Step 6 checks the existing all-workflows tests for fixed names.
- A fixture that inserts a definition row directly writes no subscription row. Start tests create and publish definitions through the repository or the API.
- The acceptance fixture imports Example 05 under a key and a signal name made for the run, as it already rewrites keys. Otherwise one scenario's throw to all workflows would start instances in every other scenario's copy.

Browser smoke suite, which has no Worker:

- **E25, standalone editor**, at 1440×900 and 1024×768. Turn a node into a signal start; type a name; add mappings; enable the business key; read the status line for a second start with the same name; add a throw of the same name to this workflow, read the self-start problem on both events and in the save dialog, and clear it by choosing another scope; see the shape in both themes; read the save dialog's message for a missing name; undo and redo; save and reload.
- **H18, hosted editor**, at 1440×900 and 390×844. Author the event inside the application's styles, save a version, and see **Publish** report the closed durable-publication gate.
- **E18.** Add the type and its flow to `editor-inspector-census.json`.

Worker acceptance suite:

- **S10**, at 1440×900 and 390×844. Import Example 05. The review on the **Signals** page names one new instance. Throw to the family: the delivery reads "Started an instance" and links the instance, the instance's history reads "Started by signal" with the broadcast link, and My work shows the task. A throw to all workflows starts a second.
- **S11.** The first recall number again is skipped with "An instance already has this business key". A payload without `product` fails with "The payload failed a mapping", and the broadcast is completed with issues.
- **S12.** With the fixture's Worker stopped, throw, then save and set a new default whose task has another name. After the Worker starts, the instance runs the new default. Repeat with a default that has no signal start: the delivery is skipped.
- **S13.** A fixture definition ends in a signal end event that throws to the run's key of Example 05. Completing it starts a notice, the broadcast names the throwing instance, and the started instance's history names it too.

Then follow [mandatory real-browser verification](../../AGENTS.md#mandatory-real-browser-ui-verification) by hand on a local stack with a Worker: the standalone and hosted editors in both themes at 1440×900 and 1024×768, the hosted inspector at 390×844, the **Signals** page, and instance detail, with real clicks, typing, and keyboard navigation through the mapping and business-key controls. Check the console, and keep screenshots of the shape, the section's start mode, a broadcast with a start, and the history row.

Regression commands, run from the repository root with the same arguments in PowerShell 7 and Bash:

```text
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet
dotnet publish Flowbit/src/Flowbit.Api/Flowbit.Api.csproj -c Release -o artifacts/browser/hosts/api /p:UseAppHost=false
dotnet publish Flowbit/src/Flowbit.Ui/Flowbit.Ui.csproj -c Release -o artifacts/browser/hosts/ui /p:UseAppHost=false
dotnet build Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore
git diff --check
```

The suite READMEs cover Docker, the Chromium install, and the acceptance suite's own commands. Full solution runs have had order-dependent failures at the base commit, so compare failures against a base run before attributing one to this change. Before the status is set to Done, walk this table case by case against the tests.

### 14. Delivery sequence, rollout, and risks

1. **Extractions.** `EntryEventRules`, and the two methods taken out of `StartByMessageAsync`. Exit with every suite green and no behavior change.
2. **Contract.** The type, the predicates, `SignalEventRules`, both server validators, the migrator, and `SignalStartFixtures`. Exit with the definition tests green and older documents unchanged.
3. **Persistence.** Both migrations, the entities and records, the delivery kind, and the nullable columns. Exit with the migration tests and every existing signal test green.
4. **Subscriptions.** The two helpers and their calls in the four operations. Exit with the subscription tests green.
5. **Freeze.** The second statement, the counts, the writer, the summary, and the changed refusal wording. Exit with the freeze tests green.
6. **Start delivery.** `StartFromSignalAsync`, the outcomes, the retry, and the chain count. Exit with the delivery, exactly-once, loop, and race tests green.
7. **API and Flowbit.Ui.** The DTOs, the `kind` filter, the page, and the history rows.
8. **Editor.** The type, the section's start mode, and save validation. Exit with the Jint suites green.
9. **Documentation and package.** The owning pages, the example, the authoring guide, and the pinned counts.
10. **Verification.** E25, H18, E18, S10 to S13, the performance evidence, and the manual checks. Record the evidence under [Current state](#current-state), then set the status to Done here, in the [backlog](README.md#event-04-follow-up-subtasks), and in the [parent](event-04-signal-events.md#later-releases).

If steps 1 to 5 are released before step 6, `signalStartEvent` stays out of `IsSupported` until step 6, so both validators reject it and no start is frozen that nothing can deliver.

Rollout:

1. Apply `AddSignalStartEvents`, then `ValidateSignalStartConstraints`. The first holds short locks for catalog changes. The second validates one check and builds one index without blocking writes.
2. Upgrade every API, Worker, and UI replica. Mixed replicas are unsupported: an API replica without this release changes a default without moving the family's rows, and a Worker without it cannot run a start delivery.
3. Review who can throw signals. From the first published signal start on, they can start workflows.
4. Only then save a definition with a signal start. Publishing also needs `WorkflowDurableProcessing:PublicationEnabled`.
5. To roll the binaries back, first set another default for, or delete, every definition with a signal start, and let its start deliveries finish. The schema can stay. The down migration refuses while a start delivery or such a definition exists.

| Risk | Guard |
| --- | --- |
| A path changes a family's published default without moving its rows. | Two helpers called from the four operations, a test that compares the table with the defaults after each operation, and the list of operations in AGENTS.md beside the rule for new ones. |
| A throw races a deletion, and a delivery stays pending without a job. | The share lock in the freeze, the row lock at the start of each operation, the existing deletion blocker, and a race test that ends with no broadcast `delivering`. |
| A throw that overlaps a default change misses a family that both versions start. | A persisting name keeps its row, updated in place, and a race test starts the family once. |
| A start is created twice. | The delivery row is locked and finished in the start's transaction, a partial unique index allows one start per broadcast and family, and tests drive two leases, crashes, a cancel, and a retry. |
| A workflow throws the signal that starts it. | Both validators reject it, with shared fixtures for each scope kind, and the editor reports it on both events while the diagram is edited. |
| The save rule rejects a design someone needs. | No such design was found. The rule is one check that can be lifted without breaking a saved definition, and lifting it is recorded under [Later releases](#later-releases). |
| Workflows of different families start each other in a loop, which no single definition shows. | The chain count on the first token. A loop with one start per throw creates at most `Workflow.Async.MaxConsecutiveAutomaticActivations` instances, 1,000 by default, before a throw is refused. |
| A loop through several families widens, so the instances multiply with every round. | The chain count does not bound this. `Signals.MaxOpenWorkflowBroadcasts` refuses throws once that many broadcasts are undelivered, which can be after more instances than the budget exist. Setting a default without the signal start ends the loop at once, because frozen starts are then skipped. The deployment guide gives that step, and a test drives it. |
| A new default without the signal start makes a family stop listening, and nobody notices. | The node reference and the Flowbit.Ui guide state that only the published default listens, and the review on the **Signals** page shows the start count before a throw. A line in the confirmation dialog is a later release. |
| A start test or scenario leaves a family listening, and a later throw to all workflows starts instances there. | A signal name per test and per acceptance run, and the rules under [Tests and verification](#13-tests-and-verification). |
| The sync writes rows after the definition they name is deleted, and the foreign key refuses the deletion. | The sync runs before the definition row is deleted, toward the successor, and a test deletes a default with and without a successor. |
| Publishing a signal start gives the right to start a workflow to everyone who can throw. | The rule is stated in the editor's help, the node reference, and the deployment guide, and the rollout asks operators to review who can throw. Permissions per signal belong to [EVENT-04F](event-04f-signal-catalog.md). |
| A publish or default change waits for a long throwing step, and the family's starts wait behind it. | The wait is bounded by that step's transaction, which a synchronous service task bounds by its timeout. The guides recommend an `asyncBefore` task in front of a throw. |
| The nullable `instanceId` breaks a client that expects a number. | It is null only for a start without an instance, which exists only after a signal start is published. The API reference and the upgrade notes state it. |
| An author expects a business-key or instance scope to start a workflow. | The scope table in the node reference, the review's count on the **Signals** page, and the editor's scope help. |
| An author expects a throw to reach a start published a moment later. | A signal is not kept. The rule that only the published default listens is in the node reference and the editor's help, and the history row and the broadcast show the start count. |
| A start's failure is not noticed. | The delivery is `failed` with its reason, the broadcast is completed with issues, and **Retry failed** runs it again. Webhook events for broadcasts are [EVENT-04D](event-04d-webhook-events-for-signal-broadcasts.md). |
| The C# rules and the editor's validator drift. | Shared fixtures run on both sides with the same messages. |
| The extraction from `StartByMessageAsync` changes a message start. | It is a separate step whose exit is an unchanged suite. |
| A business key or a payload value appears in a log or a result description. | Descriptions name an instance ID and never a key, and a test reads the captured log. |
