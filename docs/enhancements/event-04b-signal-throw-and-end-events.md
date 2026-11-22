# EVENT-04B: Signal throw and end events

[Parent EVENT-04](event-04-signal-events.md) · [Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Done |
| Category | New events |
| Effort | L |
| Priority | Not ranked; second of eleven in the [EVENT-04 follow-up order](README.md#event-04-follow-up-subtasks) |
| Parent | [EVENT-04](event-04-signal-events.md) |
| Depends on | [EVENT-04](event-04-signal-events.md) (Done) |
| Related | [EVENT-04A](event-04a-signal-scope-by-business-key-or-instance.md), [EVENT-04C](event-04c-signal-start-events.md), [EVENT-04F](event-04f-signal-catalog.md) |

## Summary

An `intermediateSignalThrowEvent` and a `signalEndEvent` throw a signal from a workflow. Entering one creates the same broadcast the API creates, inside the throwing instance's transaction, and the Worker delivers it as it does today.

The definition states the signal's name, its scope, and an optional payload copied from the instance's variables. The recipients are the four scopes that [EVENT-04A](event-04a-signal-scope-by-business-key-or-instance.md) gave the API: one instance, one workflow family, the instance with one business key in a family, or all workflows. A definition chooses among them with six explicit kinds written from the thrower's point of view, such as "this instance" and "this instance's business key".

## Why it adds value

- Workflows react to each other without an integration that calls the API. An order process that throws "order cancelled" interrupts the open shipping and invoicing work.
- One branch of an instance can interrupt or extend another branch of the same instance, which today needs a gateway extension or a variable that a conditional boundary watches.
- The throw commits or rolls back with the step that made it, so a signal never announces a step that did not happen.
- Today a definition can wait for a signal but cannot send one.

## First release acceptance criteria

These criteria are revised by this plan on 2026-11-21. The revision names the scopes, says which waits of the throwing instance receive the signal, gives throws from workflows their own open-broadcast budget, defines what a refused throw does, adds a guard against signals that circulate, and states what publishing checks. A review of the plan against the code on the same day made every scope kind explicit, carried the guard through conditional events and call activities, lowered the budget's cap, and added the thrower filter.

- [x] `intermediateSignalThrowEvent` has at least one incoming flow and exactly one unconditional outgoing flow, and is pass-through. `signalEndEvent` has at least one incoming flow and no outgoing flow, and ends its token as a plain end event does. Both carry a `signal` object with a literal lowercase `name`, a required `scope`, and an optional `payload` of whole declared instance variables, the thrower's instance ID, or its business key. The API and the editor apply the same rules with the same wording.
- [x] A definition states one of six scope kinds: this instance, this workflow, a named workflow family, the instance with one business key in a named family, with the key from a declared variable or from the throwing instance's own business key, or all workflows. Every kind states all of its members. A missing member never selects another recipient, and an unknown member is rejected.
- [x] Entering the event creates one broadcast, its frozen recipients, and its delivery jobs in the instance's transaction. A transaction that rolls back leaves no broadcast, and a retried step creates exactly one.
- [x] The throw locks no other instance and waits for none. The throwing token continues without waiting for the deliveries.
- [x] A throw reaches every active wait in its scope, including the waits of other tokens of the throwing instance. The throwing token itself is never a recipient, and a wait that starts later in the same transaction is not reached.
- [x] The broadcast records the throwing instance and node as its thrower. The **Signals** page links to the instance, and the instance's history links to the broadcast. Receiving instances still continue as the fixed actor `signal`. The broadcast list can be filtered by the kind of thrower, and the page starts with the broadcasts that people and API clients threw. A broadcast outlives its thrower: cancelling the instance does not cancel it.
- [x] The recipient and payload limits apply. Throws from workflows have their own budget of unfinished broadcasts, `Signals.MaxOpenWorkflowBroadcasts`, and `Signals.MaxOpenBroadcasts` counts only broadcasts thrown through the API. A refused throw fails its step: a request returns `400` and changes nothing, a Worker job fails by its failure policy and opens an incident, and a signal delivery fails with its wait intact. A throw is never dropped silently.
- [x] A chain of signals thrown by workflows, with no user action, message, timer, or throw through the API in between, counts against `Workflow.Async.MaxConsecutiveAutomaticActivations` and is refused at the limit. The count follows the work through a conditional event that a step's values wake and into a call activity's child.
- [x] A definition with a throw or end event publishes only when durable publication is enabled and every workflow family it names exists. Publishing needs no role beyond the one that publishes definitions. A definition without signal events still runs no additional statement, and one with only throw events runs no subscription statement.
- [x] Version changes, reactivation, the shared-variable lock-order proof, the graph analyses, lifecycle capture, and the AI authoring package handle both types.
- [x] The editor draws and authors both events, and the read-only instance diagram shows them.
- [x] Existing workflow JSON and HTTP contracts keep their behavior. The schema change is additive, and the rollout order is documented. One engine rule becomes stricter for every definition, and the upgrade notes say so: a conditional event and a call activity's child continue the automatic chain of the step that caused them instead of starting a new one.
- [x] Documentation, two examples, the portable authoring package, automated tests including PostgreSQL race tests, and real-browser verification with a running Worker are complete.

## Later releases

- A payload built from expressions instead of whole variables, which also covers constants, other `sys.*` values, and shared variables.
- A throw event that waits until its deliveries finish.
- A throw that waits for capacity instead of failing its step when a budget is used up.
- A scope that names another instance by an ID held in a variable, and a scope for one call tree, together with the call-tree scope recorded under [EVENT-04A](event-04a-signal-scope-by-business-key-or-instance.md#later-releases).
- An option that leaves the throwing instance out of a family or all-workflows scope.
- A filter for the throwing instance on the broadcast list, and a **Signals thrown** list on instance detail.
- An error boundary that catches a refused throw.
- A retention period of its own for broadcasts thrown by workflows, and a metrics endpoint on the API so that a throw refused in a request is counted where it happens.
- Throwing only names that the signal catalog declares ([EVENT-04F](event-04f-signal-catalog.md)).

## Current state

Verified on 2026-11-22: implemented on branch `claude/event-04b-signal-throw`, not yet merged. Every plan section is built and documented. A review of the code against this plan on the same day found three defects in the loop guard, smaller code findings, documentation errors, and missing tests of [section 12](#12-tests-and-verification); all of them are fixed and listed under [Review findings and their fixes](#review-findings-and-their-fixes), and the evidence below passes.

Where the implementation differs from the plan:

- **Refusal wording.** A required payload variable that holds JSON null is refused with `... variable 'x' is null.`; `has no value` is kept for a missing one.
- **History payload key.** The throw's history row names its event as `eventNodeId`, as the receivers' `signalReceived` row does.
- **A call's own activation.** Entering a call activity already counts one automatic activation, so a child continues at the calling token's count after that entry.
- **Editor ordering.** When a signal end event has both a topology problem and a name, scope, or payload problem, the editor lists the latter first; every single-problem fixture gives the server's first message. The editor drops `scope` and `payload` from an imported catch or boundary, so only a raw request reports them.
- **Metrics.** The Worker's `/metrics` exports `flowbit_signal_throw_refusals_total{cause}`.
- **S9's budget.** S9 sets `Signals.MaxOpenWorkflowBroadcasts` to one more than the broadcasts thrown by workflows that are already delivering, not to 1, so a broadcast another scenario left open cannot refuse the first cancellation on the shared stack.
- **Recent broadcasts header.** The manual pass found the two filters cut to "All statu" and "People a" at 390 pixels wide, so the heading now wraps and the filters sit side by side when there is room and stack when there is not. Its empty state reads "No broadcast matches the selected status and thrower.", since the new filter can empty the list on its own.
- **Order of two problems.** A node with two problems reports them in another order than the message table of [section 2](#2-workflow-json-and-validation): the shared topology rules run before the name, and every `workflowKey` problem before a missing `businessKeyVariable`. The API and the editor agree with each other.
- **A variable-update batch item.** A refused throw skips the item, with the refusal's code and message, because the variable-update batch skips an item for any business failure; the plan first said it fails. An administrative action batch item does fail.
- **A business key that is not text.** The cause is kept, but no valid definition reaches it: `businessKeyVariable` must name a declared single `string`.
- **Refusals in each context.** Each cause is driven in a request. Each other context is driven with one cause, the budget, because a context handles a refusal the same way whatever its cause.
- **The monitor.** The test answers ten signals at a limit of 3 instead of 2,000 at the default; it shows the same thing, that an answer continues only the chain of the signal it answers.
- **Records after the commit.** The throw's broadcast metric and information line run through `IUnitOfWork.AfterCommit`, added for them.

Evidence:

- **Full suite.** `dotnet test Flowbit/Flowbit.slnx`: 5,842 of 5,849 passed on the first run. The 7 failures were the two package checks that needed the new node reference headings, a reflection test of the extended `CaptureConditionalBoundaryAsync`, two pinned counts, and `RetentionRepositoryTests.MigrationDowngradeCannotRemovePermanentPruningMarkers`, which failed because a new call-activity test in a class ordered before it left call rows that the call-activity downgrade guard refuses. The test moved to `CallActivityAdmissionTests`, which runs after it. The final full run passed 5,850 of 5,850 in 17 m 54 s.
- **New tests.** `SignalThrowValidationTests` with `SignalThrowFixtures` (both validators, the same first message for every case), `EditorSignalThrowTests`, `SignalThrowRuntimeTests` (throws, end events, refusals, budgets, the loop guard through a boundary and a conditional event, forks, the cost test, the list filter, publication, the migration, and the backlog gauge), `CallActivityAdmissionTests.A_child_continues_the_calling_tokens_automatic_chain`, and `SignalFormattingTests`.
- **Browser smoke suite.** E18 (census with both events), E23, E24 at 1440×900 and 1024×768, H16, H17 (1440×900 with a 390×844 check), and R16 at three sizes pass against the published API and UI. R16 passed again after the Recent broadcasts header change.
- **Worker acceptance suite.** S1 to S9 pass, including the new S7 at 1440×900 and 390×844, S8, and S9. S7 and S9 passed again after the header change; S9's refusal capture shows the message above the task's actions.
- **Whole browser suites.** After the last UI change, the whole smoke suite passed 142 of 142 in 14 m 34 s and the whole Worker acceptance suite 169 of 169 in 50 m 1 s.
- **After the review fixes.** Each of the three loop-guard tests fails when its fix is undone and passes with it. The full suite passed 5,940 of 5,940 in 18 m 54 s, after two order dependences of the shared database were removed (see [test isolation](#review-findings-and-their-fixes)). Against freshly published hosts, the smoke scenarios E18, E23, E24 (1440×900 and 1024×768), and R16 (three sizes) passed 8 of 8; E24's captures show the signal end event's thick ring and filled triangle in both themes. With a freshly published Worker, the signal acceptance scenarios S1 to S9 passed 13 of 13 in 4 m 40 s. The new `node #9` fallback for an unnamed throw event on the **Signals** page is checked by `SignalFormattingTests` only; the browser scenarios throw from named events.
- **Manual check, after the review fixes.** The standalone editor served from the working tree in the desktop app's browser pane, at 1440×900 and 1024×768: an imported payload entry naming both `cancelReason` and `instanceId` showed the new warning and **Remove the source**; activating it with a click and, at 1024×768, with Tab and Enter removed the source and left focus on **Value**, and undo restored it. **Remove this signal** on an imported call left focus on the node's heading, and the next Tab reached **Name**. No page overflow and no console messages.
- **Manual check, standalone editor.** On the published UI host at `http://127.0.0.1:25152/editor/flowbit-editor.html` in the desktop app's browser pane, at 1024×768 and 1440×900 in the light and dark themes: both shapes, the throw's dashed unlabelled outgoing flow, the scope select changed with the keyboard with focus kept after the redraw, **Workflow key** shown for **Another workflow family** with the server's status message until a key was typed, and the throw's **This workflow** scope with its empty-payload note.
- **Manual check, hosted editor.** Examples 03 and 04 opened at `/workflows/{id}/edit` at 1440×900, 1024×768, and 390×844, in the light appearance hosted mode always uses. With the keyboard only: diagram search selected the throw; **Help for Scope** opened with Enter; ArrowDown on **Scope** went through all six kinds with focus kept and only each kind's fields shown; typing a workflow key and a business key variable cleared the status line live; Tab reached the payload's help, name, value, **Required**, **Delete entry**, and **+ Add payload entry** in that order; Enter on the last added an entry and focused its name; ArrowDown on its value chose **This instance's ID**; Ctrl+Z returned the document to **Saved**. The signal end event showed its scope, key, variable, and payload with no **Outgoing flows** section. Neither the inspector nor the page overflowed at any of the three sizes.
- **Manual check, runtime.** On the same stack with a Worker: two orders of Example 03 started from the start page; the first cancelled from task detail with a typed reason. Its history showed `Threw signal 'orders.cancelled' to this instance: 1 recipient` and the receipt from instance #1, both linking **Broadcast #1**; its shipment moved to **Stop the shipment** with `stopReason` written by `signal`; the second order kept its armed boundary. The read-only diagram described the throw as thrown to this instance. **Broadcast #1** showed **Thrown by** `Instance #1 · Announce the cancellation` linked to the order; the **Thrown by** filter, reached with Tab and changed with the arrow keys, listed it under **Workflows** and **Everyone** but not **People and API clients**. The Signals page fitted 1440×900, 1024×768, and 390×844 after the header change. The console had no errors apart from reconnect attempts while the UI was stopped to republish it. The kept screenshots are the acceptance captures (`s7-history`, `s7-signals`, `s9-refused`, `s9-succeeded`) under `artifacts/acceptance/runs`, which is not committed.
- **Performance.** On four virtual cores, against an isolated PostgreSQL 17 container, with both builds published to `D:`, Warning-level logging, and Release builds:
  - **Start throughput.** `tools/InstanceLoadTest` started [Exclusive gateway example 01](../../examples/gateways/01-exclusive-priority-and-default.json), which has no signal events, 20,000 times after a 1,000-start warm-up, alternating builds on separate databases: `master` 60.6 and 59.2 starts per second (mean 59.9), this change 59.9 and 58.3 (mean 59.1). The gap is smaller than the spread between two runs of one build, and one start issues the same 41 SQL commands in the same order on both builds.
  - **A user action that throws.** Taking a user task's flow into a throw scoped to another family took 110 ms with no recipients and 111 ms with one (medians of 20), and 252 ms with 10,000 (median of 10, each freezing 10,000 deliveries). The same action into a plain end event took 83 ms.
  - **Narrow broadcasts beside service jobs.** With the Worker stopped, 1,000 instances each threw one signal by business key to one waiting instance while 1,000 `asyncBefore` REST service tasks were queued beside them. Once the Worker started, every delivery finished 58.5 s after its first job, and the service jobs took 52.4 s (19.1 per second), against 34.3 s (29.1 per second) for the same 1,000 service jobs alone. The Worker ran the 2,000 jobs at 34 per second together, so the deliveries shared its capacity without stopping the other work. All 1,000 deliveries were delivered.
  - **Throws while the backlog grows.** With the Worker stopped, 10,000 instances each threw to one recipient at 49.5 starts per second, against 66.0 for the receivers, which throw nothing. Each thousand ran at 48.5 to 51.1 per second from the first to the tenth, so the budget's count does not slow throws as open broadcasts accumulate, and all 10,000 were accepted under the default budget of 10,000.
  - **Backlog gauge.** With those 10,000 open broadcasts, the gauge's pending count took 12.6 ms with the earlier per-broadcast probe and 2.0 ms from the stored counts (medians of 21), both returning 10,000. The oldest-broadcast query both versions run took 1.7 ms, so one sample went from about 14 ms to about 4 ms.

### Review findings and their fixes

Five reviews of the code, tests, and documentation against this plan on 2026-11-22 found the following. All of it is fixed on the same branch.

**Defects in the loop guard**, each with a test that fails without its fix:

- **A conditional boundary that is true when its host is entered reset the chain.** `EnsureAttachedConditionalBoundarySubscriptionsAsync` called `TriggerConditionalEventsAsync` without a chain count, so the captured branch, or the interrupted host token, started at 0. A signal boundary whose branch entered such a host and threw the same signal looped without a bound. It now passes the entering token's count (`LoopGuard_CountsALoopThroughAConditionalBoundaryThatIsTrueOnEntry`). [Section 5](#loop-guard) named the two drain functions and missed this third caller.
- **An automatic multi-instance body's writes** woke conditional events with the count 0. `DrainAutomaticMultiInstanceNotificationsAsync` now passes the body token's count (`LoopGuard_AMultiInstanceBodysWritesContinueTheBodysChain`).
- **A multi-instance call's result** did not give the body token the child's count. `PublishAutomaticCallItemAsync` now gives it the greater of its own and the `callResume` job's, as a single call does (`LoopGuard_AParentThrowingAfterItsChildEndsContinuesTheChildsChain`, multi-instance case).

**Smaller code findings:**

- The throw's broadcast metric and information line ran before the step committed. They now go through a new `IUnitOfWork.AfterCommit`, which runs them only when the transaction commits, so a step that rolls back or is retried records nothing (`AStepThatFailsAfterTheThrowLeavesNothingAndItsRetryThrowsOnce`).
- The family check for publication loaded every version of each named family. It now asks `IWorkflowDefinitionRepository.ExistsByKeyAsync`.
- An unnamed throw event showed only `Instance #42` on the **Signals** page; it now shows `Instance #42 · node #9`.
- The inspector showed only the variable of an imported payload entry that names both `variable` and `source`. It now names the source beside it, with **Remove the source**, which returns focus to **Value**. **Remove this signal**, for an imported signal on a node that cannot use one, now moves focus to the node's heading.
- The `ThrownByRoles` comment names an instance beside a client, and `SignalEventRulesTests` names `HasSignalWaits`.
- A node with two problems reports them in another order than the message table of [section 2](#2-workflow-json-and-validation); this is kept and listed above as a difference.

**Documentation:**

- [Example 04's walkthrough](../../examples/signals/README.md), and Example 03's in the same way, now says that its commands replace steps 1 and 2, so the second start of order `ORD-4001` no longer stops with `409 business_key_conflict`.
- The [runtime reference](../../Flowbit/README.md) names E1–E24, H1–H17, R1–R16, and 142 cases, the refusal counter on the Worker, `IUnitOfWork.AfterCommit` for the throw's records, and "signal waits" where only waits are meant.
- [Deployment](../deployment.md#signal-events) and [engine settings](../engine-settings.md) say which hand-overs started at 0 before and which kept their count. The [developer guide](../developer-guide.md) says a target that is not a signal catch or boundary blocks a version change.
- AGENTS.md describes what `normalizeSignalThrowDefinition` drops and names `signalEndEvent` among the ends that complete an instance.
- [Engine settings](../engine-settings.md) counts 24 settings, as its table lists; it said 22 before this release too, and a test now holds the number to the table.

**Tests that could not fail as written:** the recipient-limit refusal is now also checked through the writer inside a transaction that commits, which only the writer's delete keeps empty, because every current caller rolls back before it records a refusal. Both budget tests leave a broadcast of the other kind open and set the budget to one more than their own kind's open count, so counting the wrong kinds refuses. The icon test reads only the throw's own `<symbol>`.

**Test isolation.** The new tests exposed two order dependences of the shared test database. `RetentionRepositoryTests.MigrationDowngradeCannotRemovePermanentPruningMarkers` migrated that database down, so any class that ran before it and left call, automatic multi-instance, or long-string rows broke it; it now migrates a database of its own. Delivery jobs left queued by signal tests were leased by the one-job helpers of the automatic multi-instance classes once those ran later. A cancelled broadcast's job in the new tests is now run, and every `SignalThrowRuntimeTests` case fails if it leaves an unfinished delivery job. Nine cases of the EVENT-04 and EVENT-04A classes `SignalRuntimeTests` and `SignalScopeTests` leave one on purpose or by omission, so those classes now retire such jobs after each case.

**Missing tests of [section 12](#12-tests-and-verification).** Every row of the table is now covered, in `SignalThrowRuntimeTests` (whose new cases are in its `LoopGuard`, `Refusals`, `Scopes`, `Transactions`, and `ChainRule` files), `SignalThrowAnalysisTests`, `EditorSignalThrowTests`, `SignalFormattingTests`, and `WorkflowAuthoringPackageTests`, and in the extended E18 and E24:

| Row | Covered by |
| --- | --- |
| Throw | `ThisWorkflow_ReachesTheWaitsOfEveryVersionOfTheFamily`; `Workflow_ReachesOnlyTheNamedFamily`, with the own key and another; `OwnBusinessKey_ReachesTheInstanceWithTheThrowersKeyInTheNamedFamily`; `AllWorkflows_ReachesTheWaitsOfEveryFamily`; `AFamilyWithoutBusinessKeysOrWithoutADefinitionCompletesWithNoDelivery`; `Payload_CopiesWholeValuesAndBothSourcesAndLeavesOutAnOptionalEntryWithoutAValue` (array, object, null, missing, both sources, and `{}`); `CancellingTheThrowerAfterTheCommitLeavesItsBroadcastDelivering`. |
| Same transaction | `AnAsyncBeforeTaskInFrontOfTheThrowReachesAParallelCatchInEveryOrder`, for both events in both orders; `SignalEndEventBehindAFork_ReachesNoSiblingBranchInTheOtherOrderEither`; `TheHostThatTheTokenLeftIsNotFrozen`; `AWaitATerminateEndEventCancelsAfterTheThrowIsSkipped`. |
| End event | `SignalEndEvent_ThrowsOnceFromEachPlaceWithTheTokensCountPlusOne`: a user action, a multi-instance outcome, a message, an automatic advance, a job's continuation, and a signal thrown through the API and by a workflow, each with the count it should store. `SignalEndEvent_InAChildCompletesItAndReturnsToTheParent`. |
| Atomicity | `AStepThatFailsAfterTheThrowLeavesNothingAndItsRetryThrowsOnce`, in a request and in a retried job; `RecipientLimit_TheWriterKeepsNothingForACallerThatCommits`. |
| Refusals | Each cause in a request, with its message: a business key variable without a value, blank, or too long; an instance without a business key; a required payload variable missing or null; a payload over its limit; the budget; the recipient limit; the chain limit. Each context: an `asyncAfter` job (retries, an incident on the task's node, success after its retry), a signal delivery (`signal_delivery_rejected`, the wait kept, **Retry failed**), a call child's start (`call_start_failed`), an administrative batch item (`failed`), and an administrative variable update alone (`400`) and in a batch. The counter in a job and in a delivery. No business key or payload value in a response or a log line. |
| Budgets | `WorkflowBudget_CountsOnlyOpenBroadcastsThrownByWorkflows`, which also throws to nobody without using the budget; `ApiBudget_CountsOnlyOpenBroadcastsThrownThroughTheApi`; `WorkflowBudgetSetting_ParsesAPositiveWholeNumberUpToItsCap`. |
| Loop guard | Two instances that throw to each other stop at the limit. A user action, a message, and a timer in the loop each start a new chain, and an API throw stores 0. These continue the chain: conditional boundaries raised by delivered values, interrupting and not; a conditional catch released atomically and `durableAsync`; a script after the delivery; a child called after a delivery; a parent that throws after its child ends, single and multi-instance; and a conditional boundary true on entry. A monitor that answers ten signals, each thrown after a user action, is never refused at a limit of 3. |
| Chain rule without signals | `ChainRule_AConditionalBoundaryContinuesTheCountOfWhatWokeIt`: a user action, a message, and an administrative update start at 0, and an automatic step continues its count. `ChainRule_ALoopThroughAConditionalBoundaryReachesTheLimit` opens `automatic_loop_limit`. The earlier `CallActivityAdmissionTests.A_child_continues_the_calling_tokens_automatic_chain` covers the call's child. |
| Races | `Race_AThrowDoesNotWaitForARecipientsLockedInstanceRow`, for the five scopes whose recipient can be another instance; `Race_TwoInstancesThrowingToEachOtherAtOnceBothCommit`; `Race_TheDeliveryToTheThrowerAndAUserCompletingTheHostHaveOneWinner`. |
| Cost | `DefinitionWithoutSignalEvents_RunsNoSignalStatement`; `DefinitionWithOnlyThrowEvents_TouchesSubscriptionsOnlyThroughTheFreeze`, entering a throw; and the earlier test for a transition that enters none. |
| Records | `ClaimInheritance_IgnoresTheThrowAndReceiptRows`; `FlowEvidence_RecordsTheThrowsOutgoingFlowAsASignalThrownTraversal`; `Logs_NameIdsAndCausesButNoBusinessKey`; the end event's history row in every end-event case. |
| Versions and analyses | `VersionChange_OfAThrowsNameScopeAndPayloadHasNoBlocker`; `SignalThrowAnalysisTests`: the empty access plan, the lock-order proof through a throw and into a signal end event, and reachability. The layout test already lays out every example. |
| Visibility | `ManagedClient_CannotSeeABroadcastThrownByAnInstance`; `AUserCancelsAndRetriesABroadcastThrownByAnInstance`. |
| Infrastructure | The migration test inserts a `client` thrower too; the gauge is compared after a cancel and a retry. The only test that migrates the shared database down, `RetentionRepositoryTests.MigrationDowngradeCannotRemovePermanentPruningMarkers`, deletes every broadcast first. |
| Others | `AMissingFamilyBlocksSetDefaultAndIsOnlyAPublicationBlockerForTheAssistant`; `Capabilities_NameEverySignalEvent`; `EngineSettingsReference_StatesTheNumberOfSettingsItLists`. |
| Browser | E24 also chooses **This instance's business key** and **This workflow**, reads the save dialog's message for a missing scope, and checks the inner ring, both events' icon, and the end ring after switching the theme. E18's census gives both events their flows. |

### Facts the plan rests on

Verified on 2026-11-21, before implementation.

Throw and delivery:

- **The throw owns its transaction.** `ThrowAsync` in [SignalBroadcastService.cs](../../Flowbit/src/Flowbit.Service/Services/SignalBroadcastService.cs) validates the request, replays an idempotency key, resolves the scope, and then begins a transaction of its own for the insert, the freeze, the recipient list, and the delivery jobs. Nothing in it can join an instance's transaction.
- **Limits.** The open-broadcast limit counts every `delivering` broadcast and is reported with `WorkflowConflictException`. The recipient limit is known only after the freeze statement has inserted its rows.
- **Thrower.** A thrower is a `SignalCaller` whose kind is `user` or `client` (`SignalThrowerKinds` in [SignalDtos.cs](../../Flowbit/src/Flowbit.Shared/Dtos/SignalDtos.cs)). `CK_signal_broadcasts_thrower` allows only those two values, and `ThrownBy` is a required `varchar(300)`. A managed client sees a broadcast only when its kind and ID match the thrower.
- **Freeze.** `FreezeDeliveriesAsync` in [SignalBroadcastRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/SignalBroadcastRepository.cs) runs raw SQL without a locking clause. Inside an instance's transaction it also sees that transaction's own uncommitted subscription changes.
- **Delivery.** `DeliverSignalAsync` in [WorkflowEngineService.Signals.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.Signals.cs) never reads the thrower except for the history payload. It resets the receiving token's automatic-activation count in three places: when it adds a sibling token, when `InterruptBoundaryHostAsync` moves the host's token onto an interrupting boundary, and when it advances from the event.
- **Jobs.** A delivery job has no instance, so it does not count against the throwing instance's open-job limit.
- **Backlog gauge.** `GetBacklogAsync` counts pending deliveries with one index probe into `signal_deliveries` for each `delivering` broadcast, and the Worker samples it every five seconds. Its comment assumes "the few open broadcasts". Each broadcast row already stores `TotalDeliveryCount` and its four final counts.
- **List.** `GET /api/signal-broadcasts` filters by `name`, `workflowKey`, and `status`. It has no filter for the thrower.
- **Metrics.** Only the Worker has a metrics endpoint ([WorkerOperationalEndpoints.cs](../../Flowbit/src/Flowbit.Worker/WorkerOperationalEndpoints.cs)). The API logs a mapped `400` as a warning.

Engine:

- **End events are entered in five places.** A token that moves onto an end event is completed where it moves: `CloseAndAdvanceMultiInstanceAsync`, `TakeFlowCoreAsync`, `DeliverMessageAsync`, and `AdvanceAutomaticTokenAsync` in [WorkflowEngineService.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.cs), and `AdvanceFromSignalEventAsync`. Each decides the token's status with `BpmnFlowNodeTypes.IsEnd`. The routing loop never visits an end event.
- **Pass-through nodes.** `ResolvePassThroughAsync` handles a link throw in its own branch and gives every other pass-through node a history note that also becomes the flow's traversal kind. `SequenceFlowTraversalKind` has no caller.
- **Failures.** `WorkflowDomainException` is sealed. The API maps it to `400`, a signal delivery records it as `signal_delivery_rejected`, and a call start turns it into `CallFailureCodes.StartFailed` after discarding the attempt's changes. One automatic multi-instance error route converts it into a conflict, which opens a `job_conflict` incident while the token still waits.
- **Automatic chains.** Each token carries `AutomaticActivationCount`. `CountAutomaticActivationAsync` counts a new automatic activation against `Workflow.Async.MaxConsecutiveAutomaticActivations` ([WorkflowAutomaticActivationGuard.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowAutomaticActivationGuard.cs)), and user actions, messages, timers, and signal deliveries reset the count. A fork gives its branches the parent's count, and a join continues at the greatest contributing count.
- **Where a chain is lost today.** A conditional event starts a new chain whoever woke it: a non-interrupting boundary's branch is created with a count of 0 in `TriggerConditionalEventsAsync`, and an interrupting one resets the host's token in `InterruptBoundaryHostAsync`. A call activity's child starts through `StartInstanceCoreAsync` with a count of 0, although the `callStart` job carries the calling token's count.
- **Two ways into a conditional event.** `ResumeForVariableChangesAsync(instance, actor)` serves user actions, messages, signal deliveries, administrative variable updates, multi-instance initialization, and an automatic error boundary. `DrainConditionalVariableChangesAsync` serves the routing loop after a node's outputs. Neither takes the count of the step that wrote the values.

Model, validation, and analyses:

- **`IsSignalEvent` means "waits".** It is true for the catch and the boundary and gates five things: the check that rejects `signal` on other types, the migrator's clearing of `signal`, `RequiresDurableProcessing`, `SignalEventRules.HasSignalEvents`, and the version rule for open subscriptions. `HasSignalEvents` in turn gates every subscription statement.
- **Unknown members.** The workflow model ignores a member it does not know, except in the call activity models, which carry `JsonUnmappedMemberHandling.Disallow` and fail binding with the member's path.
- **`signal` has two members.** `SignalEventModel` has `name` and a non-null `outputMappings` list. Six collectors read `signal.outputMappings` of any node as variable declarations, among them `CallActivityRules.SourceDeclarations` and the conditional analyzer.
- **Declared sources.** `CallActivityRules.SourceDeclarations` in [CallActivityRules.cs](../../Flowbit/src/Flowbit.Shared/Models/CallActivityRules.cs) lists every variable a definition declares or produces and flags shared aliases. `CaptureMappedValues` copies whole values from the stored instance variables.
- **Nothing rests on a pass-through node.** Version checks compare active tokens, open jobs, and open subscriptions. A node that no token rests on gets only the generic topology and flow checks.
- **No value checks.** `instance_history."Note"` and `sequence_flow_occurrences."Kind"` have no check constraint. `CK_node_executions_completion_reason` lists every completion reason.
- **Schema by reflection.** The authoring schema reads the node types from the `BpmnFlowNodeTypes` constants, and the assistant's layout sizes any type whose name ends in `Event` as an event.

Editor:

- **Literal lists.** The save validator names node types as string literals, because the Jint suites run its span alone: `supportedNodeTypes`, `validateSaveSignalEvents`, `validateSaveEndEvents`, the "defines signal" check, and `signalEventLabel`. The CSS rules are per type.
- **Predicates.** `isEndEventType` drives the refusal of outgoing connections, the hidden **Outgoing flows** section, and the type-change guard. `isSignalEventType` drives the loader, the export, `applyTypeInvariants`, and the name suggestions.
- **Normalizer.** `normalizeSignalDefinition` returns only `name` and `outputMappings`, so any other member of `signal` is lost on load and export.
- **Icons.** `icon-signal-catch` is an outlined triangle. There is no filled one. A type missing from `NODE_ICON_SYMBOL` silently draws the task icon.
- **Tests.** `EditorSignalEventTests` asserts that **Signal Catch Event** is the option right after **Conditional Catch Event**. `EditorValidatorTests` stubs the type predicates by hand for the spans it lifts.

Suites:

- The smoke suite runs with durable publication disabled, so it cannot publish a definition with a signal event. Its scenarios end at E23, H16, and R16. The acceptance suite's signal scenarios end at S6.
- The acceptance suite imports an example under a key made for the run (`ExampleWorkflowAcceptanceTests`), so a literal `workflowKey` inside an example does not name the imported copy of another example.

## Builds on

- `ThrowAsync`, `FreezeDeliveriesAsync`, and `EnqueueDeliveryJobsAsync`, which already freeze recipients in one statement without a runtime lock.
- `SignalBroadcastScope` from [EVENT-04A](event-04a-signal-scope-by-business-key-or-instance.md): the four scopes as a record that the freeze statement takes, and `BusinessKeyRules`, the one normalization of a business key.
- The pass-through loop `ResolvePassThroughAsync` and the link throw event, the nearest existing throw event in the model, the validators, and the editor.
- The call activity's whole-variable input mappings: `CallActivityRules.SourceDeclarations` for what a definition declares, and `CaptureMappedValues` for copying stored values.
- `WorkflowAutomaticActivationGuard` and `CountAutomaticActivationAsync`, the existing bound on chains of automatic work.
- [SignalEventRules.cs](../../Flowbit/src/Flowbit.Shared/Models/SignalEventRules.cs) and [SignalEventFixtures.cs](../../Flowbit/tests/Flowbit.Tests/SignalEventFixtures.cs), which keep the API and the editor on the same rules.
- The publication checks for call targets in [WorkflowDefinitionReadinessChecker.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowDefinitionReadinessChecker.cs): a draft saves without its target, and publication checks it.

## Open questions

No question remains open. The plan decides the six original questions and the ones it raised.

| Question | Decision |
| --- | --- |
| Which scopes can a definition author? | Six kinds, stated with `signal.scope.kind`: `thisInstance`, `thisWorkflow`, `workflow` with a literal `workflowKey`, `businessKey` with a `workflowKey` and a `businessKeyVariable`, `ownBusinessKey` with a `workflowKey`, and `allWorkflows`. They store the four scopes the API has. |
| Why six kinds instead of four with optional members? | A missing member must never choose a recipient. With an optional `businessKeyVariable`, a forgotten or mistyped member would have sent the signal to the instance with the thrower's own key, without an error. So each kind requires all of its members, the thrower's own key has a kind of its own, and the two new models reject unknown members as the call activity models do. |
| Why `thisWorkflow` beside `workflow`? | A literal key names the wrong family once a definition is copied or imported under another key, as the acceptance suite does with every example. `thisWorkflow` takes the family from the throwing instance. |
| Does the thrower receive its own signal? | The throwing token never does: it rests on no wait when it throws, and the waits of the host it just left were cancelled earlier in the same transaction. Other tokens of the instance do when the scope includes the instance, as BPMN broadcasts to every catcher. `thisInstance` exists for exactly that. Flowbit has no subprocess, so no boundary can surround the throw event itself. |
| When does a wait of the throwing instance count? | When it is active in the database at the moment of the throw, including one created earlier in the same transaction. A wait that a parallel branch reaches later in the same routing pass is not reached. A signal is not a queue, and the guides say not to rely on the order in which parallel branches are routed. A signal end event throws earlier than an intermediate throw: when its token enters it, which for a fork is the fork's own step, before any branch has subscribed. An automatic task with `asyncBefore` in front of the event moves the throw into a later transaction, in which every wait of the earlier step exists. |
| What happens when a limit refuses the throw? | The step fails, as a script task without an error boundary fails. A request returns `400` and rolls back. A Worker job fails by its failure policy and then opens an incident. A signal delivery fails with `signal_delivery_rejected` and its wait stays. Nothing is dropped, and no token waits. |
| How does an author keep a user's action from failing? | With an automatic `task` that has `asyncBefore: true` in front of the throw event. The action then commits, and the Worker runs the throw with retries and an incident. This needs no new job kind. A throw that waits for capacity by itself is a later release. |
| Does a throw from a workflow use `Signals.MaxOpenBroadcasts`? | No. That limit protects the API, and at its default of 20 a few hundred instances that each throw one narrow signal would block it and each other. Throws from workflows get `Signals.MaxOpenWorkflowBroadcasts` (default 10,000, hard cap 100,000), and `Signals.MaxOpenBroadcasts` counts only broadcasts thrown through the API. A broadcast without recipients still never counts. |
| Can the Worker's backlog gauge take that many open broadcasts? | Not as it is: it probes the delivery table once per open broadcast every five seconds. The gauge changes to a sum over the counts stored on the open broadcasts, one scan of the partial index's rows, and the setting's cap is 100,000. The evidence records the sample's duration at 10,000 open broadcasts. |
| Is the payload a list of whole variables or a template? | A list of whole values, as call activity inputs are. Each entry copies one stored instance variable under a member name, or one of two values of the thrower that no variable holds: its instance ID or its business key. A receiver that should answer the thrower needs one of them. A payload from expressions is a later release. |
| Who can read a payload? | The receiving definitions, through their mappings, and everyone with a role from `Signals.RequiredRole`, through the broadcast routes, whether or not they can read the throwing instance. The guides say so, and no shared alias can be sent. |
| Can a payload or a scope read a shared variable? | No. A throw reads stored instance variables only, so it adds no key to the lock-order proof, and a shared value never reaches a broadcast's payload. |
| Is a guard needed against a signal that circulates? | Yes. A broadcast thrown by a workflow carries its thrower's automatic-activation count plus one, and a delivery of such a broadcast gives that count to the receiving token instead of resetting it. A throw at the limit is refused. The open-broadcast budget stops a storm that fans out; the count stops a thin loop that never fills the budget. |
| Is the count on the receiving token enough? | No. Between a delivery and the next throw the work can change tokens: the delivered values wake a conditional event, or the token starts a call activity's child. Both start at 0 today, so a loop through either would never be counted. The count is handed over at both points ([section 5](#loop-guard)). |
| Why not one count on the instance? | It was considered and rejected. A count that deliveries only raise climbs without end in a long-lived instance that answers signals and never has a user action, such as a monitor, and would refuse legitimate work after a few hundred exchanges. A count that deliveries overwrite, or that any external trigger on the instance resets, is lost to an unrelated delivery or timer between a delivery and the throw. A count that follows the work has neither fault. |
| Does the hand-over change definitions without signals? | Yes, in one way: a conditional event woken by an automatic step, and a call activity's child, continue that step's chain instead of starting at 0. A chain reaches the limit only after 1,000 automatic activations without a user action, message, or timer, so this stops only work that today runs without any bound. The rule cannot be limited to definitions that throw, because a chain can pass through a definition that only receives. |
| Does cancelling the throwing instance cancel its broadcasts? | No. The broadcast is committed, belongs to no instance, and is delivered, cancelled, and retried like one the API threw. |
| Is publishing a definition enough authority to throw to all workflows? | Yes. Whoever may publish a definition may already change any definition, call any REST endpoint with configured secrets, and start other families through call activities. A second role would add a step without adding a boundary. Permissions per signal belong to [EVENT-04F](event-04f-signal-catalog.md). |
| Must a named workflow family exist? | For publication and set-default, yes: a family that does not exist is most likely a mistyped key. A draft saves without it, so two definitions that throw to each other can both be saved and then published. At run time nothing is checked: a family that has since been deleted has no waits. |
| What if nothing waits, or the target family has no business keys? | The broadcast completes with no deliveries. Its recipient count is on the broadcast and in the instance's history. A throw is refused only when it cannot be built or a limit stops it. |
| What if the business key or a required payload variable has no value? | The throw is refused. The scope or the payload cannot be built, and a signal to the wrong recipients is worse than a failed step. |
| Where does the end event throw? | Where the token enters it: one helper, called at each of the five places that complete a token at an end event. `signalEndEvent` is an end event for every analysis, so the validator, the migrator, the lock-order proof, and the instance's completion need no second rule. |
| Does the throw need a job kind, a completion reason, or a route? | No. The delivery job, the visit reasons `normal` and `normalEnd`, and the seven signal routes stay as they are. One history note, `signalThrown`, is new. |
| How is the thrower recorded? | `ThrownByKind` gains `instance`, `ThrownBy` holds the instance ID as text, and three new columns hold the instance, the node, and the node's name. The instance column is a plain ID without a foreign key. |
| Do throws from workflows bury the manual ones on the **Signals** page? | They would. The list route gains a `thrownByKind` filter, and the page gets a **Thrown by** filter that starts at **People and API clients**, so the page shows after the upgrade what it shows today. |
| Where is a refused throw counted? | In `flowbit.signals.throw_refusals`, which only the Worker exports. A throw refused in an API request is found in the API's warning log, which names the event and the cause. |
| Does `signal` become two models? | No. `SignalEventModel` gains `scope` and `payload`, null and omitted on a catch and a boundary. A throw must leave `outputMappings` empty, so no collector takes a throw for a producer. |
| What does `IsSignalEvent` mean afterwards? | Every type that carries `signal`: all four. The two types that own subscriptions get `IsSignalWait`, and `HasSignalEvents` becomes `HasSignalWaits`. The rename is its own first step and changes no behavior. |

## Plan

Written on 2026-11-21 following [Writing a plan](README.md#writing-a-plan), and revised the same day after a review against the code. Effort stays **L**: the item adds two node types, a second transaction shape for the throw, a guard on signal chains, and a migration, and it touches the engine, both validators, the editor, and Flowbit.Ui.

### 1. Scope and invariants

| Type | Flows | Behavior |
| --- | --- | --- |
| `intermediateSignalThrowEvent` | At least one incoming, exactly one unconditional outgoing | Pass-through. In its routing step it throws its signal, then follows its flow. |
| `signalEndEvent` | At least one incoming, no outgoing | An end event. When a token enters it, it throws its signal, and the token ends as at a plain `endEvent`. |

The invariants of the [parent plan](event-04-signal-events.md#1-scope-and-invariants) hold unchanged. These are new or matter here:

1. **A throw from a workflow is the API's broadcast.** It uses the same tables, freeze statements, delivery jobs, outcomes, cancel, retry, and retention. Only the thrower and the transaction differ.
2. **The throw commits with its step.** The broadcast, its deliveries, and its jobs are written in the instance's transaction and nowhere else.
3. **Throwing never waits for another instance.** The throw takes no lock on an existing row. Its inserts hold no foreign key to an instance, token, or subscription.
4. **The thrower never waits for a recipient.** The token continues at once, and no failure of a delivery reaches the throwing instance.
5. **A refused throw fails its step.** It is never dropped, and no transaction commits part of a broadcast.
6. **The throwing token is never a recipient.** Other waits of the throwing instance are recipients when the scope includes them.
7. **Signals thrown by workflows continue the automatic chain.** A delivery of such a signal does not reset the receiving token's automatic-activation count, and the count follows the work when it changes tokens or instances. Only a person, a message, a timer, or a throw through the API starts a new chain.
8. **Workflows without throw events pay nothing.** A transition runs a throw statement only on a throw or end event. A definition whose only signal events throw runs no subscription statement.
9. **A throw reads instance variables only.** No scope and no payload reads a shared alias, so the lock-order proof gains no key.
10. **A business key is business data.** It is stored on the broadcast. No log statement, metric, job payload, or history payload contains it.
11. **A missing member never chooses a recipient.** Every scope kind requires all of its members, and an unknown member of `scope` or of a payload entry is rejected.
12. **A broadcast outlives its thrower.** Cancelling, ending, or faulting the throwing instance after the commit changes nothing about its broadcasts.
13. **A payload is readable by signal administrators.** It is stored on the broadcast and returned by the broadcast routes to every holder of a role from `Signals.RequiredRole`.

This release adds no signal start event ([EVENT-04C](event-04c-signal-start-events.md)), no webhook event for broadcasts, no expiry, no catalog, no payload from expressions, no waiting throw, no error boundary on a throw, no new job kind, no new completion reason, and no new route.

### 2. Workflow JSON and validation

These are fragments, not a complete workflow:

```json
{
  "flowNodes": [
    {
      "id": 7,
      "type": "intermediateSignalThrowEvent",
      "name": "Announce the cancellation",
      "laneId": 1,
      "x": 520,
      "y": 160,
      "signal": {
        "name": "orders.cancelled",
        "scope": { "kind": "thisInstance" },
        "payload": [
          { "name": "reason", "variable": "cancelReason", "required": true },
          { "name": "orderInstance", "source": "instanceId" }
        ]
      }
    },
    {
      "id": 12,
      "type": "signalEndEvent",
      "name": "Recall announced",
      "signal": {
        "name": "warehouse.product-recall",
        "scope": {
          "kind": "businessKey",
          "workflowKey": "example-signal-boundaries-recall",
          "businessKeyVariable": "orderNumber"
        },
        "payload": [
          { "name": "reason", "variable": "recallReason" }
        ]
      }
    }
  ],
  "sequenceFlows": [
    { "id": 601, "sourceRef": 6, "targetRef": 7 },
    { "id": 701, "sourceRef": 7, "targetRef": 8 },
    { "id": 1101, "sourceRef": 11, "targetRef": 12 }
  ]
}
```

The six scope kinds. Every member listed is required, and no other member is allowed:

| `scope.kind` | Members | Recipients | Stored broadcast scope |
| --- | --- | --- | --- |
| `thisInstance` | None | The active waits with the name in the throwing instance. | Instance, with the thrower's ID and family. |
| `thisWorkflow` | None | The active waits with the name in every instance of the throwing instance's family, in every version. | Family, with the thrower's key. |
| `workflow` | `workflowKey` | The active waits with the name in every instance of that family. | Family. |
| `businessKey` | `workflowKey`, `businessKeyVariable` | The active waits with the name in that family's instance whose business key is the variable's value. | Business key. |
| `ownBusinessKey` | `workflowKey` | The active waits with the name in that family's instance whose business key is the throwing instance's own. | Business key. |
| `allWorkflows` | None | Every active wait with the name. | All workflows. |

| Subject | Contract |
| --- | --- |
| `signal.name` | The rule of the catching events: required literal text of 1 to 100 lowercase letters, digits, `.`, `_`, or `-`, starting with a letter or digit. |
| `signal.scope` | Required object. `kind` is one of the six values, compared exactly. There is no default scope, and a member the model does not know fails binding. |
| `scope.workflowKey` | Required for `workflow`, `businessKey`, and `ownBusinessKey`, rejected for the other three. A literal key by the rules of a call activity's `workflowKey`: no surrounding white space, at most 300 characters, and no `${...}`. It may be the definition's own key. |
| `scope.businessKeyVariable` | Required for `businessKey`, rejected for every other kind. It names a variable that the definition declares or produces as a single `string` and that is not a shared alias. |
| `ownBusinessKey` | The definition's entry events must configure `businessKey`. |
| `signal.payload` | Optional list of at most 100 entries. A missing, null, or empty list sends no payload. |
| Payload entry `name` | The member name in the payload object: 1 to 100 letters, digits, `_`, or `-`, starting with a letter or `_`. Names are unique ignoring letter case. A name has no dot, because a receiver's mapping path uses dots for nesting. |
| Payload entry value | Exactly one of `variable` and `source`. A member the model does not know fails binding. |
| Payload entry `variable` | A variable that the definition declares or produces, of any type, and that is not a shared alias. Its stored JSON value is copied whole. |
| Payload entry `source` | `instanceId`, the throwing instance's ID as a JSON number, or `businessKey`, its business key as text. `businessKey` needs the definition's entry events to configure `businessKey`. |
| Payload entry `required` | Optional, false when missing. A required entry whose variable has no value, or holds JSON null, refuses the throw, and so does a required `businessKey` source in an instance without one. An optional entry is left out when it has no value, and passes a stored null on. |
| `signal.outputMappings` | Must be missing or empty on a throw or end event. |
| `scope` and `payload` on a catch or boundary | Rejected. |
| Throw topology | At least one incoming flow and exactly one outgoing flow, which is unconditional and carries no user-action or multi-instance settings. |
| End topology | The rules of every end event: at least one incoming flow and no outgoing flow. |
| `thisInstance` without a wait | Rejected when no signal catch or boundary event in the definition has the same name: such a throw can never reach anything. |
| Other settings | Both events carry only the common node properties and `signal`. Async flags, job policies, roles, variables, boundaries, and multi-instance settings are rejected. |

Proposed messages, the same in the API and the editor. `Signal throw event` becomes `Signal end event` for the end event. The checks run in this order after the shared type and topology rules, and the first problem of a node is the one the fixtures compare.

| Problem | Message |
| --- | --- |
| Missing `signal` or name | `Signal throw event #7 must define signal.name.` |
| Invalid name | `Signal throw event #7 signal.name must be 1 to 100 lowercase letters, digits, '.', '_', or '-', starting with a letter or digit.` |
| Throw without an incoming flow | `Signal throw event #7 must have at least one incoming sequence flow.` |
| Throw without exactly one outgoing flow | `Signal throw event #7 must have exactly one outgoing sequence flow.` |
| Outgoing flow with a condition or action settings | `Signal throw event #7 must have one unconditional outgoing sequence flow without user-action or multi-instance metadata.` |
| End event topology | The existing `End event #12 must have at least one incoming sequence flow.` and `End event #12 cannot have outgoing sequence flows.` |
| Output mappings on a throw | `Signal throw event #7 cannot define output mappings; it sends signal.payload.` |
| Missing scope or unknown kind | `Signal throw event #7 signal.scope.kind must be 'thisInstance', 'thisWorkflow', 'workflow', 'businessKey', 'ownBusinessKey', or 'allWorkflows'.` |
| Kind needs a workflow key | `Signal throw event #7 signal.scope needs a workflowKey for kind 'workflow'.` |
| Kind needs a key variable | `Signal throw event #7 signal.scope needs a businessKeyVariable for kind 'businessKey'; use kind 'ownBusinessKey' for this instance's own business key.` |
| Invalid workflow key | `Signal throw event #7 signal.scope.workflowKey cannot have surrounding whitespace.`, `... must contain at most 300 characters.`, or `... is a literal key; ${...} is not replaced.` |
| Workflow key on another kind | `Signal throw event #7 signal.scope cannot define workflowKey for kind 'thisInstance'.` |
| Key variable on another kind | `Signal throw event #7 signal.scope cannot define businessKeyVariable for kind 'workflow'.` |
| Key variable not declared | `Signal throw event #7 signal.scope.businessKeyVariable 'orderNo' is never declared or produced by this workflow.` |
| Key variable not a single string | `Signal throw event #7 signal.scope.businessKeyVariable 'amount' is not declared as string.` |
| Key variable is a shared alias | `Signal throw event #7 signal.scope.businessKeyVariable 'rate' is a shared-variable alias, which a signal scope cannot read.` |
| Own business key without business keys | `Signal throw event #7 uses this instance's business key, but this workflow's entry events do not configure businessKey.` The same message covers the scope kind `ownBusinessKey` and the payload source `businessKey`. |
| More than 100 payload entries | `Signal throw event #7 defines more than 100 payload entries.` |
| Null payload entry | `Signal throw event #7 payload cannot contain null entries.` |
| Invalid payload name | `Signal throw event #7 payload entry 1 name must be 1 to 100 letters, digits, '_', or '-', starting with a letter or '_'.` |
| Payload name used twice | `Signal throw event #7 payload sends 'reason' more than once.` |
| Payload entry without one value | `Signal throw event #7 payload entry 'reason' must define either variable or source.` |
| Unknown payload source | `Signal throw event #7 payload entry 'sender' source must be 'instanceId' or 'businessKey'.` |
| Payload variable not declared | `Signal throw event #7 payload entry 'reason' reads 'cancelReason', which is never declared or produced by this workflow.` |
| Payload variable is a shared alias | `Signal throw event #7 payload entry 'reason' reads shared-variable alias 'rate', which a signal payload cannot read.` |
| `thisInstance` without a wait | `Signal throw event #7 throws 'orders.cancelled' to this instance, but no signal catch or boundary event in this workflow waits for it.` |
| `scope` or `payload` on a wait | `Signal catch event #9 cannot define signal.scope or signal.payload; only signal throw and end events send a signal.` |
| Unsupported settings, API only | `Signal throw event #7 cannot define task, role, variable, timer, conditional, message, async, or multi-instance settings.` |

Each message starts with `<noun> #<id>`, which `WorkflowAiDiagnostics.DefinitionPointer` turns into the JSON pointer returned to the AI assistant. A member of the wrong JSON type, such as a `payload` that is an object, fails binding with the existing `{"error": ...}` body and its path, and so does an unknown member of `scope` or of a payload entry, such as a misspelled `businesKeyVariable`. The editor reports both cases in words before the request is sent: `Signal throw event #7 signal.scope has an unknown member 'businesKeyVariable'.`

Model and service changes:

- **Predicates, first without behavior change.** Rename `BpmnFlowNodeTypes.IsSignalEvent` to `IsSignalWait` and `SignalEventRules.HasSignalEvents` to `HasSignalWaits`, with every caller. Exit with the whole test suite green and no other change.
- **`BpmnFlowNodeTypes`.** Add `IntermediateSignalThrowEvent` and `SignalEndEvent` with `IsSignalThrow`, `IsSignalEnd`, and `ThrowsSignal` (either). A new `IsSignalEvent` is true for all four signal types. `IsEnd` and `IsSupported` gain the end event; `IsPassThrough` and `IsSupported` gain the throw.
- **Callers of the two meanings.** The check that rejects `signal` on other types, the migrator's clearing of `signal`, and `RequiresDurableProcessing` use `IsSignalEvent`. Subscriptions, the single-outgoing rule for the two waits, the version rule for open subscriptions, and instance detail's **Waiting for signals** use `IsSignalWait`.
- **`SignalEventModel`** gains `Scope` (`SignalThrowScopeModel` with `kind`, `workflowKey`, and `businessKeyVariable`) and `Payload` (a list of `SignalPayloadEntryModel` with `name`, `variable`, `source`, and `required`). Both are null on a wait and omitted from its JSON, so stored definitions keep their exact shape. The two new models carry `JsonUnmappedMemberHandling.Disallow`, as the call activity models do. `SignalEventModel` itself stays tolerant, because stored definitions bind through it.
- **`SignalEventRules`** owns the scope kinds with the members each requires, the payload sources, the payload name pattern, the entry limit, `Label` for the four types, and a `ThrowProblems` function that returns the ordered problems of one throw or end event. It takes the definition's declarations from `CallActivityRules.SourceDeclarations`, which already flags shared aliases.
- **`WorkflowDefinitionValidator`.** A new `ValidateSignalThrow` runs after `ValidateSignalCatch` in the node loop for both types. `ValidateAuthoredSignalEventMetadata` rejects unsupported settings through the widened `Label`, and `ValidateSignalCatch` and `ValidateSignalBoundary` reject `scope` and `payload`. The single-outgoing rule and its label chain gain the throw, as does the label chain of the engine's `SelectPassThroughFlow`. The end rules already cover `signalEndEvent` through `IsEnd`.
- **`WorkflowModelMigrator`.** `ApplyNodeInvariants` gives the throw the clearing of a link event and the end event the clearing of an end event, and keeps `signal` on both. It sets `payload` to an empty list on a throw when it is null, and never rewrites a name, a scope, or an entry. Older documents contain neither type and normalize as before.
- **`WorkflowDefinitionReadinessChecker`.** `RequiresDurableProcessing` is true for all four signal types. A new check for publication and set-default requires each family that a `workflow`, `businessKey`, or `ownBusinessKey` scope names to exist: `Signal throw event #7 throws to workflow 'shipping', which has no definition.` The definition's own key always passes. `WorkflowAiAuthoringService.ValidateModelAsync` reports the same problem among its publication blockers, beside the call targets, and never among its save blockers.
- **`WorkflowAuthoringJson.CreateSchema`** describes `scope` and `payload` with their required members.

### 3. Persistence and migration

One additive migration, `AddSignalThrowEvents`, in a transaction. Update the entity, the records, `AppDbContext`, and the model snapshot with it.

| Object | Change |
| --- | --- |
| `signal_broadcasts."ThrowerInstanceId"` | New nullable `bigint`, a plain column without a foreign key: the throwing instance. |
| `signal_broadcasts."ThrowerNodeId"` | New nullable `integer`: the throw or end event. |
| `signal_broadcasts."ThrowerNodeName"` | New nullable text of the node-name length: the event's name when it threw, so the page needs no definition. |
| `signal_broadcasts."AutomaticActivationCount"` | New `integer NOT NULL DEFAULT 0`: the chain count a delivery gives its receiving token. It is 0 for a broadcast thrown through the API. |
| `CK_signal_broadcasts_thrower` | Replaced. `ThrownByKind` is `user`, `client`, or `instance`. For `instance`, `ThrowerInstanceId` and `ThrowerNodeId` are set and `IdempotencyKey` is null; for the other two kinds, the thrower columns are null. `AutomaticActivationCount` is 0 or more. |
| `IX_signal_broadcasts_open_thrower` | New partial index on `("ThrownByKind")` where `"Status" = 'delivering'`. Both open-broadcast counts read it. |
| `engine_settings` | Seed `Signals.MaxOpenWorkflowBroadcasts` (`10000`) with a description, without overwriting an existing row. |

- **Thrower text.** For an `instance` thrower, `ThrownBy` holds the instance ID in invariant digits, `ThrownByRolesJson` is `[]`, and `ThrownByClaimsJson` is null. `signal_broadcast_jobs."RequestedBy"` holds the same text.
- **Setting.** `Signals.MaxOpenWorkflowBroadcasts` accepts 1 to 100,000. It is parsed by the rule the other signal limits use, with 10,000 as its default.
- **Backlog gauge.** `GetBacklogAsync` changes its pending count to `sum("TotalDeliveryCount" - "DeliveredCount" - "SkippedCount" - "FailedCount" - "CancelledCount")` over the `delivering` broadcasts, which the new partial index finds. It no longer probes `signal_deliveries` once per open broadcast. Step 3 first confirms that every delivery page refreshes those counts; if a page can leave them stale for longer than its own run, the gauge keeps its probe and the cap drops to 10,000.
- **Retention.** The `signalBroadcasts` category already removes a finished broadcast with its deliveries and job links, whoever threw it. A throw from a workflow stores a row and its payload even when nothing waits, and the category keeps rows forever by default, so the deployment guide tells operators to set a period before they publish a definition that throws.
- **Existing rows.** Every existing row has a `user` or `client` thrower, null thrower columns, and a count of 0, which satisfies the new check. Nothing is backfilled.
- **Cost.** PostgreSQL adds a column with a constant default as a catalog change. The check is validated in the same migration; it scans `signal_broadcasts`, which holds one row per broadcast. No statement needs to run outside a transaction.
- **Unchanged.** `signal_subscriptions`, `signal_deliveries`, `workflow_jobs`, `CK_node_executions_completion_reason`, and the lifecycle capture functions. No job kind, completion reason, or retention category is added.
- **Counts.** The schema keeps its 71 tables, the engine settings grow from 21 to 22, and the retention categories stay at ten.
- **Down migration.** It refuses while a broadcast with an `instance` thrower exists, because the restored check would reject it. Retention removes such broadcasts once they are finished and old enough. It then removes the setting, the index, and the columns, and restores the check.

### 4. Throw transaction and lock order

**One writer for both throwers.** Extract the part of `ThrowAsync` that runs inside its transaction into a `SignalBroadcastWriter` in the service layer: insert the broadcast, freeze the recipients, check the recipient limit, list the recipient definitions, store the counts and the status, and enqueue one delivery job per represented definition. It refuses to run without an ambient transaction, as `RefreshCountsAsync` does. `ThrowAsync` keeps the request validation, the hash, the replay, the scope checks, its own transaction, the idempotency lock, and the API's open-broadcast count, and calls the writer. Its behavior does not change.

The engine calls the writer inside the transaction of the step. That transaction already holds the instance hierarchy in the established order, and it may hold shared-variable rows from earlier nodes of the segment. The throw adds:

1. **Guard.** Evaluate the throwing token's chain count against `Workflow.Async.MaxConsecutiveAutomaticActivations` ([section 5](#loop-guard)).
2. **Flush.** Call `SaveChangesAsync`, so the step's tracked changes, such as a subscription created earlier in the same routing pass, are in the database before the raw freeze statement runs.
3. **Scope.** Build a `SignalBroadcastScope` from the node and the instance. Nothing is read from the database for a family.
4. **Payload.** Copy the payload from the stored instance variables.
5. **Budget.** Count unfinished broadcasts with an `instance` thrower against `Signals.MaxOpenWorkflowBroadcasts`, without a lock and stopping at the limit. It is a soft threshold, as the API's is.
6. **Write.** Call the writer with the thrower, the chain count, and `Signals.MaxSubscriptionsPerBroadcast`.
7. **Record.** Add the history payload, the metric, and one log line with IDs and counts.

Steps 1 to 5 can refuse before anything is written. The recipient limit is known only after the freeze, so the writer deletes the broadcast it inserted, and with it the frozen deliveries, before it reports that limit. A caller that catches the refusal therefore cannot commit part of a broadcast.

- **No new lock.** The throw locks no existing row. The freeze reads other instances' subscriptions from its statement's snapshot and waits for no transaction. The inserts reference only the new broadcast and, for a job, a workflow definition, as the API's throw does.
- **No cycle.** A delivery locks the instance hierarchy, the subscription, and the delivery row. A throw inside a delivery inserts a new broadcast and locks nothing of the broadcast being delivered. Cancel and retry lock a broadcast row and then delivery rows, and never a runtime row.
- **Held locks.** A wide throw holds the thrower's instance lock, and any shared rows of the segment, for the time of the freeze: about half a second for 10,000 recipients and two seconds for 100,000 in the parent's measurements. The recipient limit bounds it, and the guides recommend narrow scopes for throws from workflows.

**What the freeze sees.** The statement runs in the instance's transaction, so "active when thrown" means: committed and active for other instances, and as this transaction has left them for the throwing instance.

| Wait of the throwing instance | Recipient? |
| --- | --- |
| The waits of the host the throwing token just left | No. They were cancelled earlier in the transaction. |
| A wait another token has held since an earlier transaction | Yes. |
| A wait another token reached earlier in the same routing pass | Yes. |
| A wait a token reaches later in the same routing pass | No. |
| A wait on another branch of a fork that leads straight into a signal end event | No, whatever the order of the flows. The end event throws when its token enters it, in the fork's own step, and a branch subscribes in its own later step. An intermediate throw runs in its own step, so it reaches the branches routed before it. |
| A wait that ends later in the same transaction, for example at a terminate end event | Frozen, then skipped by its delivery with `subscription_ended`. |

An author who needs every wait of the same step to be reached puts an automatic `task` with `asyncBefore: true` in front of the throw or end event. The throw then runs in a Worker job, in a transaction that starts after all of those waits are committed. The node reference, the developer guide, and the editor's help give this one pattern for three needs: reaching the waits of the same step, keeping a user's action from failing on a refused throw, and keeping a wide throw's freeze out of a request.

Delivery is unchanged. The Worker delivers after the commit, each recipient in its own transaction, so a delivery to the throwing instance never re-enters the throwing transaction. A broadcast is not tied to its thrower afterwards: cancelling the throwing instance, or its end at a terminate or error end event, leaves the broadcast and its deliveries as they are.

**Where a throw can run, and what a refusal does.** A refusal is a `WorkflowDomainException` whose message starts with the event's label.

| Context | Result of a refusal |
| --- | --- |
| An API request: a start, a user action, a delivered message, an immediate administrative action | `400` with the message. The transaction rolls back. |
| An administrative variable update whose values wake a conditional event that leads to a throw | A single update returns `400` and changes nothing. In a batch, the item is skipped with the refusal's message, the batch's existing rule for a business failure. |
| A Worker job's continuation: `asyncBefore`, `asyncAfter`, a timer, a conditional wake, a finished call, a multi-instance completion | The job fails by its failure policy: retries where it has them, then a `job_execution_failed` incident. The incident names the job's node, which is the node the job ran for and not the throw event; its message names the throw event. Retrying the incident runs the step again. |
| A signal delivery whose continuation reaches a throw | The delivery fails with `signal_delivery_rejected` and the message. The receiving wait stays, and **Retry failed** delivers it again. |
| The start of a call activity's child | The attempt's changes are discarded and the call fails with `CallFailureCodes.StartFailed`. |
| An administrative batch item | The item's transaction rolls back and the item is `failed`. |

### 5. Engine lifecycle

Add a `WorkflowEngineService.SignalThrows.cs` partial with one method that both events use, `ThrowSignalFromNodeAsync(instance, token, node, definition, actor)`. It reads the chain count from the token it is given. It runs the steps of [section 4](#4-throw-transaction-and-lock-order) and returns the history payload. The engine receives the writer as an optional constructor dependency, as it receives the two signal repositories; a throw event reached without it fails with "Signal persistence is unavailable."

#### Where each event throws

**Intermediate throw.** `ResolvePassThroughAsync` gets a branch after the link throw's: for `IsSignalThrow` it calls the method with the token, then advances through `AdvanceAutomaticTokenAsync` with the note `signalThrown` and the returned payload. The throw event has no boundary, no async phase, and an empty shared-variable plan, so the steps before that branch do nothing for it.

**End event.** A new `ThrowSignalAtEndAsync` returns at once unless the entered node is a `signalEndEvent`. Each of the five places that complete a token at an end event calls it after the token's update is saved and before the instance's completion is decided, so the instance is still running when its other waits are frozen.

| Place | Entered by | Count on the token after its update |
| --- | --- | --- |
| `TakeFlowCoreAsync` | A user action | 0 |
| `CloseAndAdvanceMultiInstanceAsync` | A multi-instance user task's outcome or interrupt | 0 |
| `DeliverMessageAsync` | A delivered message | 0 |
| `AdvanceAutomaticTokenAsync` | Every automatic advance, including gateways, jobs, calls, and administrative actions | The token's own count; 0 when the token leaves a timer or an administrator moved it |
| `AdvanceFromSignalEventAsync` | A delivered signal | The delivering broadcast's count |

No place passes a number of its own. Each hands the helper the token as its update left it, and the helper reads the count from that token, so a place added later cannot pass a wrong one. A test drives every row of this table and expects one broadcast with the count the row names plus one.

The two events differ in when they throw. The intermediate throw runs in its own routing step. The end event throws in the step that moves its token there, which is earlier: [section 4](#4-throw-transaction-and-lock-order) says what that means behind a fork.

#### Scope and payload

| Scope kind | Built from |
| --- | --- |
| `thisInstance` | `SignalBroadcastScope.ForInstance` with the instance's ID and its workflow key. |
| `thisWorkflow` | `SignalBroadcastScope.Family` with the instance's workflow key. |
| `workflow` | `SignalBroadcastScope.Family` with the literal key. |
| `businessKey` | `SignalBroadcastScope.ForBusinessKey` with the literal key and the stored value of `businessKeyVariable`. `BusinessKeyRules.Normalize` trims and checks it. |
| `ownBusinessKey` | `SignalBroadcastScope.ForBusinessKey` with the literal key and the instance's own `BusinessKey`. |
| `allWorkflows` | `SignalBroadcastScope.AllWorkflows`. |

- **Variables.** When the node has a payload or a `businessKeyVariable`, the method reads the stored instance variables once with `LoadInstanceVariablesAsync`, after the flush. It never reads the routing overlay, which can hold shared values of earlier nodes.
- **Payload.** Entries are copied in order into one JSON object. A `source` entry takes the instance's ID or its `BusinessKey` from the instance row the step already holds. An empty entry list sends no payload; a list whose optional entries all have no value sends `{}`. The object is limited to 256 KiB by `JsonbText.ByteCount`, the measure the check constraint uses.
- **No lookup.** The engine does not check that the family exists or has enabled business keys. Such a throw freezes nothing and is stored as `completed`.

#### Limits and refusals

| Cause | Message after the event's label |
| --- | --- |
| The chain count is at the limit | `was not thrown: its chain of automatic steps and signals reached Workflow.Async.MaxConsecutiveAutomaticActivations (1000).` |
| The business key variable has no value, is not text, is blank, or is longer than 300 characters | `cannot read a business key from variable 'orderNumber': it has no value.`, with the cause that applies |
| The instance has no business key of its own, for the scope kind `ownBusinessKey` or a required `businessKey` source | `uses this instance's business key, but this instance has none.` |
| A required payload variable has no value or is null | `cannot send payload entry 'reason': variable 'cancelReason' has no value.` |
| The payload is larger than 256 KiB | `payload is 300000 bytes; the limit is 262144.` |
| The budget of unfinished broadcasts is used up | `was not thrown: 10000 signal broadcasts thrown by workflows are still delivering; see Signals.MaxOpenWorkflowBroadcasts.` |
| More recipients than the limit | `would reach more than 10000 waiting subscriptions; narrow its scope or raise Signals.MaxSubscriptionsPerBroadcast.` |

No message contains a business key or a payload value. Each refusal increments a new counter, `flowbit.signals.throw_refusals`, tagged with its cause. Only the Worker exports metrics, so the counter shows the refusals in jobs and deliveries. A throw refused in an API request appears in the API's warning log, with the event's label and the cause; the deployment guide names both places.

`Signals.MaxOpenBroadcasts` changes its count: `CountOpenAsync` takes the thrower kinds to count, the API counts `user` and `client`, and the engine counts `instance`. Without throw events the API's count is what it was.

#### Loop guard

A workflow that catches a signal and throws one that reaches a waiting workflow can circulate without end, in one instance through a non-interrupting boundary or across instances and families.

The count is the one tokens already carry, and it follows the work. It was considered to keep one count on the instance instead; the [open questions](#open-questions) say why that fails.

- **At the throw.** `WorkflowAutomaticActivationGuard.EvaluateNext` takes the throwing token's count. At the limit the throw is refused. Otherwise the broadcast stores the count plus one.
- **At the delivery.** `DeliverSignalAsync` gives the receiving token the broadcast's `AutomaticActivationCount` in the three places where it resets the count today: the sibling token of a non-interrupting boundary, `InterruptBoundaryHostAsync`, and the advance from the event. A broadcast thrown through the API stores 0, so those deliveries still reset.
- **Where the work changes tokens.** Between a delivery and the next throw the work can leave the receiving token. Each such point hands the count over:

| Hand-over | Today | With this release |
| --- | --- | --- |
| Fork and join | The branches inherit; a join continues at the greatest count. | Unchanged. |
| A conditional boundary's branch or interrupted host token | 0, whoever woke the event. | The count of the step whose values woke it. |
| A released conditional catch | Its own count; step 6 confirms that no path resets it. | The greater of its own count and that step's. |
| A call activity's child | Its first token starts at 0. | Its first token starts at the calling token's count, which the `callStart` job already carries. |
| A call's result | The parent's token keeps its own count. | The greater of its own count and the count on the `callResume` job. Step 6 confirms that the job is built from the token that ended the child. |

- **The step that woke a conditional event.** `ResumeForVariableChangesAsync` and `DrainConditionalVariableChangesAsync` get the count as a parameter and pass it to `TriggerConditionalEventsAsync`, which uses it where it writes 0 today. A signal delivery passes its broadcast's count. The routing loop, multi-instance initialization, and an automatic error boundary pass the count of the token whose node wrote the values. A user action, a message, and an administrative variable update pass 0, which is the parameter's default, so a caller that is not changed keeps today's behavior. The count is written in the writer's transaction, when the event is captured or latched, so a `durableAsync` event needs nothing on its job.
- **Fresh allowance.** A user action, a delivered message, and a timer start a new chain, as they do for automatic activities. A periodic throw behind a timer therefore never reaches the limit.
- **No token write.** The throw does not change the throwing token's own count. Every hop of a loop passes through a broadcast, which carries the increment.
- **Other definitions.** The two changed hand-overs apply to every definition, with or without signals. A chain that passes through a conditional event or into a child no longer starts again at 0, so it can reach the limit where today it runs without a bound. Nothing changes for a chain that a person, a message, or a timer interrupts. The upgrade notes say this, and a test pins each row of the table for a definition without signals.
- **What the guard does not see.** A workflow that reaches Flowbit through HTTP from a service task, by throwing through the API or by starting an instance with a message, starts a new chain, as it does today. The guides say that a workflow throws with a throw event, not with a request to the API.
- **When it stops.** The refused throw fails its step: usually a delivery, which leaves the receiving instance waiting and lists the reason on the **Signals** page. Raising the setting is the way to allow a longer chain.

#### Records

- **History.** One new note, `signalThrown`. For the intermediate throw it is the note of the advance row from the throw event to the next node. For the end event it is an additional row from the end event to itself, after the row that moved the token there. Its action ID is null, so claim and assignment inheritance ignore it, and its performer is the step's actor.
- **History payload.** The broadcast ID, the signal name, the event node, the event kind (`throw` or `end`), the scope kind, the target workflow key, and the recipient count. It holds no business key and no payload value.
- **Receivers.** `SignalHistoryPayload` adds the throwing instance and node for an `instance` thrower. The receiving instance still continues as `signal`.
- **Node activity.** The throw's visit completes with `normal`, and the end event's visit with `normalEnd`.
- **Flow evidence.** The throw's outgoing flow records a traversal of kind `signalThrown`, and no action.
- **Telemetry.** `SignalRuntimeTelemetry.RecordBroadcast` gets the thrower kind `instance`, and the Worker's telemetry forwards the refusal counter with the other signal instruments.
- **Lifecycle webhooks.** No event type is added. An instance that ends at a signal end event is captured as one that ends at a plain end event.

### 6. Versions, reactivation, and graph analyses

- **Version changes.** No token rests on a throw event, and no open job or subscription names one, so no blocker or warning is added. A changed name, scope, or payload applies to the next entry. `ValidateExactTopology` compares both types and their flows as it compares any node. Broadcasts already thrown, and their pending deliveries, do not depend on the thrower's version.
- **Reactivation.** No rule is added. A token that passes a throw event again throws again, which the guides state. The blocker for active subscriptions is unchanged.
- **Shared-variable lock order.** `RouteChoices` already ends a path at `IsEnd` and follows the one flow of any other pass-through node. `SharedVariableAccessPlanner` never reads `signal`, so both types have an empty plan. A test pins both.
- **Graph analyses.** Both types are ordinary nodes with flows. `BuildStructuralAdjacency`, `GatewayTopologyIndex`, `FindUnsafeReactivationTopology`, and `WorkflowAiProposalChecks.Reachable` need no edge and no case.
- **Call activities.** A child instance throws like any other. A signal end event completes the child normally, and its result returns to the parent as before. The child's first token continues the calling token's chain, and the parent's token continues at the greater count when the result returns ([section 5](#loop-guard)).
- **Signal starts.** [EVENT-04C](event-04c-signal-start-events.md) can give a started instance's first token the broadcast's chain count, which bounds a workflow that throws the signal that starts it.

### 7. API contracts and authorization

No route is added.

| Contract | Change |
| --- | --- |
| `SignalBroadcastSummaryDto` and `SignalBroadcastDetailDto` | Gain `thrownByInstanceId`, `thrownByNodeId`, and `thrownByNodeName`, null unless `thrownByKind` is `instance`. `thrownByKind` can be `instance`, and `thrownBy` is then the instance ID as text. |
| `POST /api/signal-broadcasts` | The `409` for `Signals.MaxOpenBroadcasts` now counts only broadcasts thrown through the API. Nothing else changes. |
| `GET /api/signal-broadcasts` | Gains an optional `thrownByKind` filter, which can be repeated: `user`, `client`, or `instance`. Another value is `400`. Without it the list is what it is today, plus the broadcasts thrown by instances. |
| Cancel and retry | A user with a role from `Signals.RequiredRole` cancels and retries a broadcast thrown by an instance as any other. |
| Managed API clients | Unchanged. A client sees only the broadcasts it threw, so a broadcast thrown by an instance returns `404`. |
| Instance history | Can contain `signalThrown`, and a `signalReceived` payload can name a throwing instance. |
| Node types | Responses and the node-execution filters accept the two new types, which the filters read by reflection. |
| Flow evidence | `FlowInfo` can return the traversal kind `signalThrown`. |
| Definition routes | Saving applies the rules of [section 2](#2-workflow-json-and-validation). Publishing and set-default can return `400` for a family that does not exist. |
| Engine settings | A 22nd setting. |

- **Authorization.** The definition routes keep their policy. No signal role is checked when a definition is published or when an instance throws.
- **Payload visibility.** The broadcast routes return a broadcast's payload to every user with a role from `Signals.RequiredRole`, as they do for a payload sent through the API. For a throw from a workflow those are instance variables, which such a user may otherwise be unable to read. The node reference and the developer guide state this beside the payload's description.
- **OpenAPI.** Give the three new DTO properties `<param>` comments, update the comment that says the thrower kind is `user` or `client`, and update the hand-written schema tables of the API reference.

### 8. Editor

All changes are in the canonical assets under `Flowbit/src/Flowbit.Ui/wwwroot/editor/`.

#### Types and shapes

- **Types.** Add both to `NODE_TYPE`. Add `isSignalThrowEventType`, `isSignalEndEventType`, and `throwsSignalType`, and rename today's `isSignalEventType` to `isSignalWaitType`. A new `isSignalEventType` is true for all four.
- **Predicates.** The throw joins `isEventType`, `isSingleOutgoingType`, and `isPassThroughType`, so its flow is dashed. The flow shows only a name the author gives it; `flowLabelInfo` adds no label of its own, unlike the `signal` label on a signal boundary's flow. The end event joins `isEventType` and `isEndEventType`, which already refuses outgoing connections, hides **Outgoing flows**, and guards the type change.
- **Options.** `NODE_TYPE_OPTIONS` gains "Signal Throw Event (broadcast a signal)" after the signal catch and "Signal End Event (end and broadcast a signal)" after the error end event. The catch stays right after the conditional catch, which a test asserts.
- **Lists.** The type names in `searchDiagram`, `NODE_ICON_SYMBOL`, and the inner-ring list in `renderNodes`, which gains the throw. A test asserts that every option has an icon entry, so a missing one cannot draw the task icon.
- **Icon.** `editor-template.js` gains `icon-signal-throw`, the catch's triangle filled with `currentColor`, as the link throw is the filled form of the link catch.
- **Styles.** `flowbit-editor.css` gains body, ring, selected, and icon rules for the throw beside the signal catch's, and adds the end event to the three end-event groups, so it has the thick ring in both themes.

#### Inspector

`renderSignalSection` serves the two new types in a throw mode:

- **Signal name**, with the suggestions and the live status line of the catch. The suggestions list every signal name in the diagram.
- **Scope**, a select with **Choose a scope**, **This instance**, **This workflow**, **Another workflow family**, **A business key from a variable**, **This instance's business key**, and **All workflows**. Choosing a kind shows **Workflow key** and **Business key variable** where that kind requires them and removes the members of the other kinds in the same change.
- **Business key variable** is required for its kind, with the declared single-string variables as suggestions. An empty field is a problem, never a second meaning.
- **Payload**, rows with **Name in the payload**, **Value**, and **Required**, plus **+ Add payload entry**. **Value** offers the declared variables, **This instance's ID**, and **This instance's business key**. Choosing a variable fills an empty name with the variable's name when that is a valid payload name.
- **Unknown members.** A member of `scope` or of a payload entry that the editor does not know is kept on import, reported by save validation by name, and removable from the section, as a call activity's unknown settings are.
- **Status lines.** The section's status region reports the first problem of the scope and the payload as they are typed, with the save message. For **This instance** it also says when no wait in the diagram has the name.
- **Help** behind `attachInspectorHelp`: what a throw reaches, that a wait on a parallel branch of the same step may not exist yet, that the token does not wait, what happens when a throw is refused, the `asyncBefore` task in front of a throw, and that signal administrators can read the payload. The catch's help, which says a signal is thrown from the page or the API, also names the two events.
- Every control is labeled, so the E18 census passes, and a control that redraws the section returns focus to the control it changed.

#### Load, export, and type changes

- **Normalizer.** A new `normalizeSignalThrowDefinition` keeps `name`, `scope`, and `payload` as authored and drops an empty `outputMappings`. It keeps a non-empty `outputMappings` and any value of the wrong type for save validation, and the section offers to remove them.
- **Loader and export.** The loader and `prepareWorkflowExport` choose the normalizer by type. `keepImportedCallSettings` keeps `signal` as it does today.
- **Invariants.** `applyTypeInvariants` gets a branch for the throw, modeled on the link events, and keeps `signal` in the end-event branch for the signal end event only.
- **Type changes.** A node that becomes a throw or end event gets `{ name, scope: { kind: "" }, payload: [] }`, with the name it had when it was a signal event. One that becomes a wait gets `{ name, outputMappings: [] }`. Mappings and payload entries never cross from one form to the other.
- **Test harnesses.** The hand-written predicate stubs in `EditorValidatorTests` and the `NODE_TYPE` stub of the icon test gain the new names.

#### Save validation

- A new phase `validateSaveSignalThrows` runs after `validateSaveSignalEvents` with the messages of [section 2](#2-workflow-json-and-validation). It reads the declarations from `callSourceDeclarations`, which already lives inside the validator markers.
- `validateSaveSignalEvents` rejects `scope` and `payload` on a wait. `validateSaveEndEvents`, `supportedNodeTypes`, the "defines signal" check, and `signalEventLabel` gain the new literals.
- The scope kinds with the members each requires, the payload sources, the payload name pattern, the known member names, and the limit live inside the `// BEGIN/END WORKFLOW SAVE VALIDATOR` markers. The phase reports an unknown member of `scope` or of a payload entry in words, where the server's binding gives only a path. One fixture set, `SignalThrowFixtures`, feeds `SignalEventRules`, the API validator, and the editor's validator, and a test holds the editor's pattern to the C# constant.

#### Viewer

Viewer mode draws both shapes and needs no other change: it has no per-type list.

### 9. Flowbit.Ui

- **Signals page.** `SignalFormatting.ThrowerLabel` takes the three new fields and returns, for example, `Instance #42 · Announce the cancellation`. The **Thrown by** row of `SignalBroadcastSummary` links it to `/instances/42`, and `SignalBroadcastHistory` shows the text. The throw form, the review, and the polling do not change.
- **Thrown by filter.** **Recent broadcasts** gets a **Thrown by** select with **People and API clients**, **Workflows**, and **Everyone**. It starts at **People and API clients**, so the list shows what it shows today, and a broadcast opened with `?broadcastId=` opens whatever the filter says. A change goes through the page's identity guard and request counter like its other reloads.
- **Instance history.** `InstanceHistory.razor` describes `signalThrown` as "Threw signal 'orders.cancelled' to this instance: 1 recipient" with the scope in words, and links the broadcast as it does for `signalReceived`. A `signalReceived` row names "instance #42" as the thrower for an `instance` thrower.
- **Instance diagram.** `ExecutionPositionText.NodeTypeLabel` names the types "Signal throw" and "Signal end". A selected throw or end event shows its signal name and its scope in words in `InstanceDiagram.razor`.
- **Unchanged.** **Waiting for signals** lists waits only. The start page, My work, Retention, and the shared-variable clients page need no change. **Engine settings** shows the new setting as it shows every row.
- **Access.** The broadcast link opens the **Signals** page, which needs a role from `Signals.RequiredRole`, as the `signalReceived` link does today.

### 10. AI authoring and the portable package

- The hosted assistant's instructions, tool names, and tool descriptions do not change. Its guides are the owning documents of [section 11](#11-documentation-and-examples).
- [The portable authoring guide](../../authoring/flowbit-authoring/references/authoring-guide.md) replaces the sentence that signals are thrown only through the API. It gains a short paragraph: when a workflow should throw a signal, that a scope is required and the narrowest one that works is the right one, that every kind needs all of its members, that the payload copies whole variables, that the token does not wait, and that an automatic task with `asyncBefore` in front of a throw keeps a user's action from failing and lets the waits of the same step receive the signal.
- The assistant's validation reports a named family that does not exist as a publication blocker, never as a save blocker ([section 2](#2-workflow-json-and-validation)), so the assistant can propose two definitions that throw to each other.
- The hand-written `capabilities` list in `AuthoringPackageBuilder` names signal throw and end events. The schema and the manifest are generated.
- The layout is unchanged: both type names end in `Event`. A test pins that the two examples lay out without overlaps.

### 11. Documentation and examples

| Owning page | Update |
| --- | --- |
| [BPMN support](../bpmn-support.md) | The node count (28), two rows in the supported table with Worker "Yes", the [Signals](../bpmn-support.md#signals) section with throws from workflows and their scopes, the signal row under [Flowbit extensions](../bpmn-support.md#flowbit-extensions), and the [unsupported list](../bpmn-support.md#unsupported-capabilities) reduced to signal start events. |
| [Node reference](../node-reference.md) | The count, two catalog rows, `### intermediateSignalThrowEvent` and `### signalEndEvent` sections, and `scope` and `payload` under [Signal configuration](../node-reference.md#signal-configuration), with which waits of the throwing instance receive a signal, why an end event behind a fork reaches no sibling branch, and who can read a payload. A package test requires both headings. |
| [Developer guide](../developer-guide.md#broadcast-signals) | A subsection **Throw a signal from a workflow**: the six scope kinds, the payload and who can read it, what a refused throw does in a request and in a job, the automatic task with `asyncBefore` and the three things it solves, the loop guard with its hand-overs and what it does not see, that a broadcast outlives its thrower, and the two budgets. The message and signal comparison gains the thrower. Examples stay HTTP-only. |
| [API reference](../api-guide.md#signals) | The thrower kind `instance` and the three new properties in the schema tables, the `thrownByKind` filter, the changed meaning of the `409`, and the new node types, history note, and traversal kind where those vocabularies are described. |
| [Flowbit.Ui guide](../ui-guide.md) | Authoring both events beside [Signal catch events](../ui-guide.md#signal-catch-events), the thrower and the **Thrown by** filter on the [Signals page](../ui-guide.md#throw-signals), and the history row on instance detail. |
| [Deployment](../deployment.md#signal-events) | The migration, an upgrade-table row, the rollout order of [section 13](#13-delivery-sequence-rollout-and-risks), the stricter chain rule for conditional events and call activities, the refusal counter on the Worker and the warning in the API's log, the advice to set a `signalBroadcasts` retention period before a definition that throws is published, a capacity note for workflows that throw while the Worker is stopped, and the down guard. |
| [Database schema](../database-schema.md#signal_broadcasts) | The four columns, the replaced check, the index, and the sentence that a broadcast can be written inside an instance's transaction. |
| [Engine settings](../engine-settings.md#signal-settings) | `Signals.MaxOpenWorkflowBroadcasts` with its cap, the count of 22, the seeding table, what `Signals.MaxOpenBroadcasts` counts, and that `Workflow.Async.MaxConsecutiveAutomaticActivations` also bounds signal chains and now follows a chain through conditional events and call activities. |
| [Runtime reference](../../Flowbit/README.md#signal-events) and [AGENTS.md](../../AGENTS.md) | The two types, the writer and the two transaction shapes, the five end-entry places and their helper, the refusal rule, the loop guard, the renamed predicates, the data model, the BPMN mapping, and the editor conventions. |
| [Root README](../../README.md), [AI authoring](../ai-authoring.md), and the [documentation home](../index.md) | The node count and the building-block table. |
| [Signal examples](../../examples/signals/README.md) and the [example catalog](../../examples/README.md) | Two examples with walkthroughs, the count (73), and the coverage matrix; every node type must appear in an example. |
| Browser suite READMEs | The new scenarios and the census fixture. |
| This page, the [backlog](README.md), the [parent](event-04-signal-events.md#later-releases), and [EVENT-04C](event-04c-signal-start-events.md) | Evidence and status. For EVENT-04C: the chain count as its guard, and the `instance` thrower. |

The examples:

- **`03-signal-throw-order-cancellation.json`.** An order splits into two branches. **Prepare the shipment** carries an interrupting signal boundary for `orders.cancelled` that leads to **Stop the shipment**. On the other branch, the action **Cancel the order** leads to a throw event with the scope `thisInstance` and the payload `reason`, then to **Confirm the cancellation**. Taking the action interrupts the shipment task of the same order and of no other order.
- **`04-signal-end-recall-decision.json`.** A recall decision for one order ends in a `signalEndEvent` that throws `warehouse.product-recall` to [Example 02](../../examples/signals/02-signal-boundaries-recall.json) with the scope `businessKey` and `businessKeyVariable: "orderNumber"`. It interrupts that order's shipment without a call to the API. Its scope names Example 02 by its literal key, `example-signal-boundaries-recall`, so the walkthrough says to import Example 02 under that key, and a fixture that imports the examples under other keys rewrites `scope.workflowKey`.

Both need the Worker. Their walkthroughs start instances and take actions in Flowbit.Ui or over HTTP, in PowerShell and Bash; neither needs a throw request.

Validate changed relative links, anchors, and the example JSON. Compare the changed DTOs with their registrations, tests, and the development OpenAPI document, and run the new walkthroughs against a local stack with a Worker.

### 12. Tests and verification

| Layer | Cases |
| --- | --- |
| Rename | The whole suite passes unchanged after the predicate rename. |
| Shared rules | `SignalEventRules`: each of the six scope kinds with every required member missing and every other member present, both payload sources, an entry with both or neither of `variable` and `source`, the payload name pattern at its limits, duplicates ignoring case, and the label of each of the four types. `SignalThrowFixtures` feeds the C# rules and the editor's validator through Jint. |
| Definition validation | Every message of [section 2](#2-workflow-json-and-validation) through the API; `scope` and `payload` on a catch and a boundary; a wrong JSON type and an unknown member of `scope` and of a payload entry as binding errors with their paths; a stored catch whose `signal` has an unknown member still binding; unsupported settings; `ownBusinessKey` and the `businessKey` source with and without configured business keys; `thisInstance` with and without a wait; stored definitions of the existing examples byte-identical after normalization; the publication gate; and a named family that does not exist, which saves as a draft, is refused on publish and set-default, and is a publication blocker and not a save blocker in the assistant's validation. |
| Throw (PostgreSQL) | Each scope reaches exactly its waits, with the recipient count on the broadcast and in the history. `thisInstance`: another branch's catch, an interrupting boundary, and a non-interrupting boundary. `thisWorkflow` in a definition saved under another key than the one it was authored with, across two versions. `workflow` with the own key and with another family. `businessKey` from a variable with surrounding spaces, and `ownBusinessKey`. A family that does not exist, and one without business keys, complete with no deliveries. The payload with required, optional, missing, null, array, and object values, and with both sources. Cancelling the throwing instance after the commit leaves its broadcast delivering. |
| Same transaction | A wait reached earlier in the same routing pass is frozen and a later one is not. A fork into a catch and an intermediate throw reaches the catch in one flow order and not in the other. A fork into a catch and a signal end event reaches the catch in neither order. With an `asyncBefore` task in front of the throw or the end event, the catch is reached in every order. The left host's boundaries are not frozen. A wait cancelled by a terminate end event after the throw is skipped by its delivery. |
| End event | Each of the five places of [section 5](#where-each-event-throws) throws one broadcast. The instance completes when the last token ends there and keeps running otherwise, and a child that ends there returns to its parent. |
| Atomicity | A step that fails after the throw leaves no broadcast, delivery, or job. A job retried after such a failure creates exactly one broadcast. A refused recipient limit leaves no row, also when the caller catches the refusal. |
| Refusals | Each cause of [section 5](#limits-and-refusals) with its message, in a request (`400`, nothing changed), in an `asyncAfter` job (retries, then an incident, then success after retry), in a signal delivery (`signal_delivery_rejected`, wait intact, **Retry failed** works), in a call child's start, in an administrative batch item, and in an administrative variable update, single and in a batch. The counter increases for a refusal in a job and in a delivery. No message contains a business key or a payload value. |
| Budgets | Throws from workflows do not count toward `Signals.MaxOpenBroadcasts`, API throws do not count toward `Signals.MaxOpenWorkflowBroadcasts`, and a broadcast without recipients counts toward neither. The setting's parse rules and its cap of 100,000. |
| Loop guard | Two instances that catch and throw the same signal stop at the configured limit with a failed delivery. A non-interrupting boundary whose branch throws its own signal stops likewise. A user action, a message, and a timer in the loop reset the count. A delivery of a broadcast thrown through the API still resets it. Each of these loops also stops: the delivered values raise a non-interrupting conditional boundary whose branch throws; they raise an interrupting one; they release a conditional catch on another token, atomic and `durableAsync`; a script after the delivery writes the value that wakes the event; the receiving token calls a child that throws; and a child's end lets the parent throw. A monitor instance that answers 2,000 signals, each thrown after a user action in another instance, is never refused. |
| Chain rule without signals | For a definition without signal events: a conditional event woken by a user action, a message, or an administrative update starts at 0 as before; one woken by an automatic step continues that step's count; a call activity's child starts at the calling token's count; and the `automatic_loop_limit` incident opens at the limit across each of them. |
| Races (release gate) | A throw while another transaction holds a recipient's instance row `FOR UPDATE` does not wait, for each scope. Two instances that throw to each other at the same time both commit. A delivery to the throwing instance runs after the commit and has one winner against a user completing the host. |
| Cost | A command-capturing test: a definition without signal events issues no `signal_` statement, one with only throw events issues no `signal_subscriptions` statement, and a transition that does not enter a throw or end event issues no throw statement. |
| Records | The `signalThrown` row and payload for both events, the receiver's payload with the throwing instance, the visit reasons, the traversal kind, claim inheritance ignoring both rows, and a captured log without a business key. |
| Versions and analyses | A running instance moves to a version that changes a throw's name, scope, and payload without a blocker. The lock-order proof with both types, the empty access plan, the reachability check, and the layout of both examples. |
| Visibility | A managed client gets `404` for a broadcast thrown by an instance and does not list it. A user cancels and retries one. The `thrownByKind` filter with each value, repeated values, and an unknown value (`400`); the list without it still returns every kind. |
| Infrastructure | The migration up and down, the replaced check with each thrower kind, the down guard, the seeded setting and its cap, the open-broadcast counts using the new index, and the backlog gauge equal to the exact count of pending deliveries after each delivery page, a cancel, and a retry. Confirm that no other test migrates the shared database down past the new migration while a broadcast with an `instance` thrower exists. |
| Editor (Jint) | The shared fixtures and ordered errors, both normalizers, the round trip of a throw without `outputMappings`, type changes in each direction, invariants, the option order, an icon entry for every option, the inspector's six scope options and the members each shows, the payload's value choices, an unknown member kept on import and named by save validation, the status lines, and the characterization arrays unchanged. |
| Flowbit.Ui | `SignalFormatting` with an `instance` thrower, the summary's link, the **Thrown by** filter with its default and its request, the history texts, `ExecutionPositionText`, and the diagram's description of a selected throw. |
| Contracts and package | The OpenAPI test for the three new properties, the example catalog tests, the authoring package tests with both headings, and the capability list. |
| Pinned counts | 28 node types, 73 examples, 22 engine settings, and the assistant workspace file count. |
| Performance (recorded evidence) | With `tools/InstanceLoadTest`, the start throughput of a workflow without signal events before and after the change, with both builds published to one disk. The duration of a user action that throws to 0, 1, and 10,000 recipients. Many narrow broadcasts, which the parent's measurements never had: 1,000 instances that each throw one signal to one recipient, with the time until every delivery has finished and the throughput of service-task jobs queued beside them, since delivery jobs share the Worker's queue. The duration of one backlog sample with 10,000 open broadcasts, before and after the gauge's change. |

Browser smoke suite, which has no Worker:

- **E24, standalone editor**, at 1440×900 and 1024×768. Turn a node into a signal throw; type a name; choose each scope and see its fields appear and leave; add a payload entry; read the status line for **This instance** without a wait; turn an end event into a signal end; see the filled triangle, the inner ring, and the thick ring in both themes; read the save dialog's message for a missing scope; undo and redo; save and reload.
- **H17, hosted editor**, at 1440×900 and 390×844. Author both events inside the application's styles, save a version, and see **Publish** report the closed durable-publication gate.
- **E18.** Add both types and their flows to `editor-inspector-census.json` and extend the ID ranges the scenario selects.

Worker acceptance suite:

- **S7**, at 1440×900 and 390×844. Start two orders of Example 03. Cancel the first from My work: its shipment task disappears and **Stop the shipment** opens, its history shows the `signalThrown` row with a link, and the second order keeps its shipment task. The **Signals** page lists the broadcast after **Thrown by** is set to **Workflows**, with its thrower and the link to the instance, and does not list it under **People and API clients**.
- **S8.** The fixture imports Example 02 under its key for the run and rewrites `scope.workflowKey` in Example 04 to that key before it imports Example 04. Start an order of Example 02 and a recall decision of Example 04 for the same order number. Completing the decision interrupts the order's shipment. The decision is `Completed`, and its history links the broadcast.
- **S9.** With the fixture's Worker stopped and `Signals.MaxOpenWorkflowBroadcasts` set to 1, a second cancellation in Example 03 shows the refusal's message in Flowbit.Ui and changes nothing. After the Worker restarts and the first broadcast finishes, the same action succeeds. The scenario restores the setting's earlier value when it ends, also when it fails, because the suite's other scenarios share the stack.

Then follow [mandatory real-browser verification](../../AGENTS.md#mandatory-real-browser-ui-verification) by hand on a local stack with a Worker: the standalone and hosted editors in both themes at 1440×900 and 1024×768, the hosted inspector at 390×844, the **Signals** page, and instance detail, with real clicks, typing, and keyboard navigation through the scope and payload controls. Check the console, and keep screenshots of both shapes, the throw's **Signal** section with each scope, a broadcast thrown by an instance, and the history row.

Regression commands, run from the repository root with the same arguments in PowerShell 7 and Bash:

```text
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet
dotnet publish Flowbit/src/Flowbit.Api/Flowbit.Api.csproj -c Release -o artifacts/browser/hosts/api /p:UseAppHost=false
dotnet publish Flowbit/src/Flowbit.Ui/Flowbit.Ui.csproj -c Release -o artifacts/browser/hosts/ui /p:UseAppHost=false
dotnet build Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore
git diff --check
```

The suite READMEs cover Docker, the Chromium install, and the acceptance suite's own commands. Full solution runs have had order-dependent failures at the base commit, so compare failures against a base run before attributing one to this change.

### 13. Delivery sequence, rollout, and risks

1. **Rename.** `IsSignalWait` and `HasSignalWaits` in C#, and `isSignalWaitType` in the editor. Exit with every suite green and no behavior change.
2. **Contract.** Both types, the model, `SignalEventRules`, both server validators, the migrator, the schema, the publication checks, and `SignalThrowFixtures`. Exit with the definition tests green and older documents unchanged.
3. **Persistence.** The migration, the entity and records, the thrower kind, the two open-broadcast counts, the `thrownByKind` filter, and the backlog gauge from the stored counts, after confirming when those counts are refreshed. Exit with the migration and gauge tests green.
4. **Writer.** `SignalBroadcastWriter` extracted from `ThrowAsync`, with the delete before the recipient-limit refusal. Exit with every existing signal test green.
5. **Intermediate throw.** The engine method, the routing branch, scope and payload, the refusals, the budget, and the records. Exit with the throw, atomicity, refusal, and race tests green.
6. **Loop guard.** The chain count on the broadcast and in the three delivery places, then the hand-overs at conditional events and call activities. First confirm two facts the table in [section 5](#loop-guard) names: that no path resets a released conditional catch's count, and which token a `callResume` job is built from. Exit with the loop tests and the chain-rule tests without signals green, and the API's deliveries still resetting.
7. **End event.** The helper at the five places. Exit with the end-event tests green.
8. **Editor and Flowbit.Ui.** Both events in the editor, the thrower on the page, and the history rows. Exit with the Jint suites and component tests green.
9. **Documentation and package.** The owning pages, the two examples, the authoring guide, and the pinned counts.
10. **Verification.** E24, H17, E18, S7 to S9, the performance evidence, and the manual checks. Record the evidence under [Current state](#current-state), then set the status to Done here, in the [backlog](README.md#event-04-follow-up-subtasks), and in the [parent](event-04-signal-events.md#later-releases).

Step 7 is separate because it changes five places in the engine. If steps 1 to 6 are released before it, `signalEndEvent` stays out of `IsSupported` and `IsEnd` until step 7, so both validators reject it.

Rollout:

1. Apply `AddSignalThrowEvents`. It is additive and holds its lock on `signal_broadcasts` for the catalog changes and one scan of that table.
2. Upgrade every API, Worker, and UI replica. Mixed replicas are unsupported: an API replica without this release strands a token on a throw event it does not know and never ends one at a signal end event, and a Worker without it resets the chain count on delivery. From this step on, a conditional event woken by an automatic step and a call activity's child continue that step's chain in every definition. The upgrade notes tell operators that an `automatic_loop_limit` incident can now open for a chain that ran unbounded before.
3. Set a retention period for `signalBroadcasts` if none is set. A definition that throws stores one broadcast per throw.
4. Only then save a definition with a throw or end event. Publishing also needs `WorkflowDurableProcessing:PublicationEnabled`.
5. To roll the binaries back, first retire every definition that contains a throw or end event and finish or cancel its instances. The schema can stay. The down migration refuses while a broadcast thrown by an instance exists.

| Risk | Guard |
| --- | --- |
| A path completes a token at an end event without calling the helper, so a signal end event ends silently. | One helper for the five places, a test that drives each, and the list of places in AGENTS.md beside the rule for new paths. |
| A refused throw is caught and its transaction commits part of a broadcast. | Everything that can be checked is checked before the first write, and the writer deletes its broadcast before it reports the recipient limit. A test catches the refusal and commits. |
| A throw makes a user's action fail while the Worker is stopped. | A budget of 10,000 unfinished broadcasts that only workflows use, broadcasts without recipients that never count, the automatic task with `asyncBefore` described in the guides and the editor's help, the refusal counter on the Worker, and the warning in the API's log. |
| A scope with a forgotten or mistyped member reaches the wrong instances. | Every kind requires all of its members, the thrower's own key is a kind of its own, and both new models reject unknown members. Tests cover each missing and each extra member. |
| The work leaves the token that carries the chain count, and a loop is never counted. | The hand-over table lists every point where work changes tokens or instances, a test drives a loop through each, and AGENTS.md states that a new way to create or wake a token must hand the count over. |
| The stricter chain rule stops an existing workflow. | It needs 1,000 automatic activations without a person, a message, or a timer. The upgrade notes name the incident, raising `Workflow.Async.MaxConsecutiveAutomaticActivations` restores the old reach, and tests pin the rule for definitions without signals. |
| Thousands of open broadcasts slow the Worker's telemetry. | The gauge reads the counts stored on the open broadcasts, the cap is 100,000, and the evidence records a sample at 10,000. |
| Narrow broadcasts crowd out service and script jobs. | The recorded throughput of 1,000 throwing instances beside queued service-task jobs, and the budget, which bounds the delivery jobs that can be waiting. |
| Broadcasts thrown by workflows fill the table and bury manual throws. | The deployment guide's retention advice in the rollout order, and the **Thrown by** filter with its default. |
| A definition copied under another key throws to the family it was copied from. | `thisWorkflow` for the own family, the editor's help, and the walkthrough and fixture note for Example 04. |
| Workflows and the API starve each other's budget. | Two settings that count different throwers, and a test for each direction. |
| A signal circulates between workflows. | The chain count on the broadcast, refused at the limit, with the open-broadcast budget for storms that fan out. A workflow that throws through the HTTP API from a service task is outside the guard, which the guides state. |
| The throw starts to wait for other instances. | No lock on an existing row, no foreign key on the new column, and the race tests for each scope. |
| A wide throw holds the thrower's instance lock and shared rows for seconds. | The recipient limit, the recorded duration of a throw to 10,000 recipients, and the guides' advice to use narrow scopes in workflows. |
| Authors expect a wait on a parallel branch to receive a signal thrown in the same step. | The rule, the earlier throw of an end event, and the `asyncBefore` task that removes the dependence are stated in the node reference and the editor's help. The history row shows the recipient count, and tests pin both orders for both events. |
| The renamed predicate changes which definitions run subscription statements. | The rename is a separate step whose exit is an unchanged suite, and the command-capturing test covers a definition with only throw events. |
| A throw's `outputMappings` are taken for variable declarations. | Both validators reject mappings on a throw, and the editor drops an empty list. |
| The C# rules and the editor's validator drift. | Shared fixtures run on both sides with the same messages, and a test compares the payload name pattern. |
| A definition throws to a mistyped family and nothing happens. | Publication and set-default require the family to exist, and the history row and the broadcast show a recipient count of zero. |
| A business key or a payload value appears in a log, a metric, or a refusal message. | Messages name variables and never values, the history payload holds no key, and a test reads the captured log. |
| A payload carries data to instances or people that should not see it. | The payload lists each variable by name, no shared alias can be sent, and the guides and the editor's help say that signal administrators can read it and that a payload is not a place for secrets. |
