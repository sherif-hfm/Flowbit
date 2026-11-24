# EVENT-04E: Signal broadcast expiry

[Parent EVENT-04](event-04-signal-events.md) · [Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Done |
| Category | New events |
| Effort | M |
| Priority | Not ranked; fifth of eleven in the [EVENT-04 follow-up order](README.md#event-04-follow-up-subtasks) |
| Parent | [EVENT-04](event-04-signal-events.md) |
| Depends on | [EVENT-04](event-04-signal-events.md) (Done) |
| Related | [EVENT-04B](event-04b-signal-throw-and-end-events.md), [EVENT-04D](event-04d-webhook-events-for-signal-broadcasts.md), [EVENT-04G](event-04g-parallel-signal-delivery.md) |

## Summary

A throw through the API may set `expiresAfter`, a duration after which the broadcast stops delivering. Deliveries that have not run by then end as `skipped` with `broadcast_expired`, their waits stay open, and the broadcast finishes with the new status `expired`.

## Why it adds value

- Some announcements are useless when they arrive late. A signal meant to interrupt work now should not act hours later, when a stopped Worker returns.
- Expired broadcasts stop counting toward the open-broadcast limit without an administrator cancelling each one.
- A broadcast whose delivery job is stuck behind an incident ends with a status once its expiry passes, where today it stays `delivering` until someone retries the incident.

## First release acceptance criteria

These criteria are revised by this plan on 2026-11-23. The revision names the request field and the result code, makes the expiry a duration that the database turns into a time, adds the status `expired` with its webhook event type, and adds the rules for the open-broadcast limit and for a lost lease.

- [x] `POST /api/signal-broadcasts` accepts an optional `expiresAfter`, a fixed ISO-8601 duration from 1 second to 365 days. The broadcast stores `expiresAt`, which the database computes from its own clock at the throw. Without `expiresAfter`, nothing expires, as today.
- [x] A delivery that runs at or after `expiresAt` ends as `skipped` with `broadcast_expired`. It writes nothing to its instance, a start creates no instance, and the wait and its subscription stay in place.
- [x] The expiry is compared with the database clock (`clock_timestamp()`) in a statement issued after the delivery's locks are held, so a delivery that waited for a lock cannot act after the expiry.
- [x] Once a broadcast has expired, its pending deliveries are ended in one statement under the broadcast's row lock, without any recipient's instance being locked. The delivery job that notices the expiry does this, and a Worker sweep does it when no job runs. The broadcast then finishes as `expired`.
- [x] A broadcast that is past its expiry and not yet settled does not count toward `Signals.MaxOpenBroadcasts`, also while no Worker runs.
- [x] Failed deliveries cannot be retried once the expiry has passed (`409`). A retry before the expiry keeps the original `expiresAt`. A delivery job that loses its last lease after the expiry leaves its deliveries expired, not failed. Each rule is tested.
- [x] The API and the **Signals** page show `expiresAt`, the status `expired`, and `expiredCount`, the number of deliveries the expiry ended. A repeated `idempotencyKey` with a different expiry, or with an expiry where the first request had none, is a conflict.
- [x] A fourth lifecycle webhook event type, `signalBroadcast.expired`, announces the new status. The bodies of the three existing signal broadcast types do not change, and no existing subscription receives the new type until an administrator adds it.
- [x] The guides say that an expired signal leaves a catch waiting for a signal that will not come, and recommend a timer boundary on such a catch.
- [x] Documentation, automated tests including PostgreSQL race tests, and real-browser verification with a running Worker are complete before this item is marked Done.

## Later releases

- A default expiry for the deployment.
- An expiry authored on a throw event ([EVENT-04B](event-04b-signal-throw-and-end-events.md)). Until then a broadcast that a workflow throws never expires.
- An absolute expiry time in the request, for callers that hold a business deadline.
- Changing or removing the expiry of a broadcast that is still delivering.
- Showing on instance detail the signals that expired before they reached one of the instance's waits. Until then such a delivery is found through its broadcast's deliveries, filtered by instance.

## Current state

Done on 2026-11-24, on top of `1e9e020`: implemented as planned, with the deviations below, and checked case by case against the [test table](#10-tests-and-verification).

Before this item, nothing expired: the [EVENT-04 decisions](event-04-signal-events.md#open-questions) had left expiry out because it leaves instances waiting on a catch for a signal that was dropped, and during a Worker outage the open-broadcast limit rejected new throws until a broadcast finished or was cancelled.

### What changed

- **Contract.** `SignalExpiryRules` (`Flowbit.Shared/Models`) parses `expiresAfter` with `TimerDefinitionRules.TryParseFixedDuration` and bounds it to 1 second to 365 days, with the three messages of [section 2](#2-request-contract-and-validation); a well-formed length too long for a `TimeSpan` (`TimerDefinitionRules.IsFixedDurationBeyondRange`) is out of range. `ThrowSignalRequest.ExpiresAfter`, `SignalBroadcastStatuses.Expired` (in `All` before `Cancelled`, accepted by `IsFinished`), `SignalDeliveryResultCodes.BroadcastExpired`, and `ExpiresAt` and `ExpiredCount` on both broadcast DTOs and the records. `HashRequest` adds `expiresAfter:<ticks>` only when the field is present.
- **Persistence.** `AddSignalBroadcastExpiry` adds the two columns, replaces `CK_signal_broadcasts_status`, adds `CK_signal_broadcasts_expiry` and `IX_signal_broadcasts_open_expiry`, and replaces `capture_signal_broadcast_lifecycle()`; its Down refuses while a broadcast has an `ExpiresAt` or the status `expired`, then restores the EVENT-04D function. `SignalBroadcastRepository` gains `GetDatabaseTimeAsync`, `IsExpiredAsync`, `ExpireDeliveryAsync`, `ExpirePendingDeliveriesAsync`, and `ClaimNextExpiredAsync`; `AddAsync` reads `clock_timestamp()` once for a broadcast with a duration; `CountOpenAsync` leaves out a broadcast past `now()`; `RefreshCountsAsync` and the lost-lease reconciliation in `WorkflowJobRepository` count expired deliveries and derive `expired` first.
- **Runtime.** `DeliverSignalAsync` and `StartFromSignalAsync` ask `IsExpiredAsync` after their locks and before any other outcome. `SignalBroadcastExpirer` (`ISignalBroadcastExpirer`, `ExpireAsync` and `ExpireNextDueAsync`) settles a broadcast under its row lock and then adds the ended count to `flowbit.signals.deliveries` through the new `SignalRuntimeTelemetry.RecordDeliveries` and logs one line. `SignalBroadcastJobProcessor` checks before each page and settles when a delivery reports `broadcast_expired`, and after a settle completes instead of retrying an earlier unexpected failure. `RetryFailedAsync` returns `409` past the expiry under the row lock.
- **Worker.** `SignalBroadcastExpiryService` runs every `FlowbitWorker:SignalExpirySweepSeconds` (default 5, from 1 to 3,600, validated in `WorkerOptions.Validate`), reads the database time once, and settles at most 20 broadcasts a pass.
- **Webhook event.** `LifecycleEventTypes.SignalBroadcastExpired` is appended to `Subscribable` (now fifteen) and `SignalBroadcastTypes`; `LifecycleEventCatalog` has its entry with the shared fields, its own `status`, `skippedCount`, and `failedCount` descriptions, and `expiredCount`; `SignalBroadcastEventDataDto` gains a nullable `ExpiredCount`; `WebhookFormatting` labels it **Signal broadcast expired**.
- **Flowbit.Ui.** The Signals page has the **Expires after** row, whose uncontrolled amount box reports through `typedinput` and `typedcommit`, the **Expires** review row, and passes `TimeProvider`'s time to `SignalBroadcastSummary` for the **Expiry** row and its note; `SignalFormatting` owns `ReadExpiryAmount`, `TryBuildExpiry`, `ExpiryIso`, `ExpiryReviewText`, `ExpiryNote`, `CanRetry(broadcast, now)`, `RetryHiddenReason`, `ProgressDetail`, the labels, and the result wording; `StatusBadge` gives `expired` the warning tone.
- **API documentation.** The throw and retry descriptions in `SignalEndpoints` and `FlowbitOperationCatalog` name the expiry.

Deviations from the plan:

- **A failure recorded after the expiry ends as expired.** `SignalBroadcastJobProcessor.RecordAsync`, which records a delivery's failure in a transaction of its own after the delivery's transaction rolled back, now locks the delivery row and asks `IsExpiredAsync` first; past the expiry it ends the row as `broadcast_expired` and the job settles the rest. The plan listed three paths that end a pending delivery; this fourth one keeps invariant 2 whole for a failure, such as a broken call chain, that is thrown before the delivery's own check.
- **The retry reason also shows for an `expired` broadcast with failed deliveries**, not only for a `completedWithIssues` one past its expiry: both hold failures that cannot be retried.
- **The expiry row sits after the scope's match line** in step 1, so the line stays next to the scope fields. The unit list reads Seconds, Minutes, Hours, Days with **Minutes** selected.
- **`AddSignalBroadcastLifecycleEvents.CaptureFunctionSql` became `internal`**, so the new migration's Down restores exactly that text.
- **R18 fixes the browser's zone to `America/New_York`** and compares the **Expiry** row with the instant converted to that zone, as R12 does, so the check proves the browser's zone is used.
- **S14 retries the second throw for up to 30 seconds** while it returns `409`, because the database's clock decides and the Docker VM's clock can lag the host's, the skew that `86ec7a4` already allows for in the lease helpers.
- **The hosted sweep test starts `SignalBroadcastExpiryService` itself**, with the application's services and no job dispatcher, as the timer-start reconciliation tests start their service, where the test table says "through the hosted Worker". It proves the service's loop against PostgreSQL with a real one-second expiry. That the Worker host registers and starts the service is shown only by the Worker starting in S1 to S15 and in the manual check, where a delivery job may have settled the broadcast before the sweep did.
- **`EveryFinishedBroadcastStatusHasItsEventType` compares sets** and checks that the new type is last in both lists, because the status list keeps `expired` before `cancelled` while the type list appends.

### Verification of 2026-11-24

Automated, from the repository root:

| Command or run | Result |
| --- | --- |
| `dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet`, rerun on the final code | 6,303 passed, 0 failed (19 m 29 s). A run before the last two changes (`RecordAsync` and `SignalExpiryRulesTests`) had 6,284 passed and 0 failed; the 19 added cases are `SignalExpiryRulesTests`. |
| `SignalExpiryTests` (new, `PostgresApiCollection`) | 42 passed, among them the race tests of rows 1, 3, 4, and 6 of [section 5](#5-transactions-and-lock-order). |
| Producer sweep: the signal classes with `FLOWBIT_TEST_LIFECYCLE_CAPTURE=all` | 647 passed, 0 failed; the report counted 245 `signalBroadcast.completed`, 36 `signalBroadcast.completedWithIssues`, 16 `signalBroadcast.cancelled`, and 23 `signalBroadcast.expired` events. |
| Full smoke suite (`dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore`, after publishing the API, UI, and Worker hosts) | 151 passed, 0 failed (15 m 3 s), including R17 with four types and R18 at 1440×900, 1024×768, and 390×844. |
| Signal acceptance S1 to S15 (`SignalAcceptanceTests`) with a Worker | 21 passed, 0 failed (6 m 59 s). |
| Changed relative links, anchors, and JSON examples; `git diff --check` | The new links resolve; the diff check is clean. |

Manual, in the desktop app's built-in Chromium browser (zone Asia/Riyadh), on a disposable local stack: the published API (`http://127.0.0.1:25017`), UI (`http://127.0.0.1:25152`), and Worker with a generated webhook key-ring certificate, `Webhooks:DeliveryEnabled=true`, `Webhooks:Network:AllowLoopbackHttp=true`, a fresh database on the Compose PostgreSQL, and a loopback Node receiver that checked every Standard Webhooks signature. [Example 01](../../examples/signals/01-signal-catch-policy-change.json) was published over HTTP with three waiting claims.

1. **Webhooks** → **Add subscription** with real clicks and typing: the form offered the four signal broadcast types, **Signal broadcast expired** among them; the four were ticked, the subscription created paused, its test event verified at the receiver, and then resumed.
2. Worker stopped. **Signals**: the name, the workflow key, and the payload typed; **Expires after** `1`, Tab to the unit list, Down and Up back to **Minutes**; the review read **Expires** `1 minute after the throw` and **Reaches now** `3 waits`. After **Throw signal** the broadcast was **Delivering** with an **Expiry** row (the API's `expiresAt` was 60.02 seconds after `createdAt`); a minute later the page's poll added `The expiry has passed. A Worker ends the pending deliveries when it runs.` with **Cancel pending deliveries** still offered. After the Worker started, the page read **Expired**, Skipped 3 with `3 expired`, and each delivery **The broadcast expired before delivery** with 0 attempts; the Worker logged `Signal broadcast 1 expired; 3 pending deliveries were skipped.` and `/metrics` counted `flowbit_signal_deliveries_total{status="skipped",result_code="broadcast_expired"} 3`; the receiver verified `signalBroadcast.expired` with `skippedCount` 3 and `expiredCount` 3. Instance #1, opened from its delivery row, still listed its wait under **Waiting for signals** and had no received signal.
3. Worker running, a throw with **Expires after** `1` **Hours** (Down from Minutes): **Completed** with 3 delivered, and the receiver verified `signalBroadcast.completed` with its twelve fields and no `expiredCount`.
4. A fourth claim, a throw with `{ "version": "seven" }` and **Expires after** `30` **Seconds** (Up from Minutes): **Completed with issues** with **Retry 1 failed**. After the expiry, a click on **Refresh** removed the button and showed `This broadcast has expired, so its failed deliveries cannot be retried. Throw the signal again to reach the waits that are still open.`; `POST /api/signal-broadcasts/3/retry-failed` returned `409` with `Signal broadcast #3 expired at 2026-11-23T21:08:02.618192Z; its failed deliveries cannot be retried. …` and left `updatedAt` unchanged.
5. Keyboard: Tab from **Business key** reaches the amount, and Tab from the amount reaches the unit list, whose arrow keys change the unit. At 390×844, 1024×768, and 1440×900 the page did not overflow horizontally, and the console logged no error or warning.

Screenshots of the form, the open broadcast with the note, the expired broadcast with its deliveries, the retry before and after the expiry, and the R18 runs, with the receiver's verified events, are kept under `artifacts/manual/event-04e/`, which Git ignores like every artifact.

### Review fixes of 2026-11-24

A review of the finished item against this plan found no defect in the expiry rules, and three smaller problems, fixed the same day:

- **Unreadable text in Expires after read as no expiry.** A browser number box reports text it cannot read as an empty value: in Chromium, `5-` typed into an empty box raised no `change` event, and `10` edited to `10e` reported `""`. The page then reviewed **Expires** `Never` while the box still showed the text. The amount box is now uncontrolled and reports through `typed-inputs.js` (`typedinput` on each edit, `typedcommit` when focus leaves), whose arguments carry `badInput`; `SignalFormatting.ReadExpiryAmount` keeps such an entry as `UnreadableExpiryAmount`, which **Review signal** refuses with the amount message. Focus leaving the box without a change keeps the review. The page never writes the box back, so a re-render cannot wipe the text.
- **A settled job retried a delivery that no longer existed.** When a delivery failed unexpectedly on an attempt before the last and the same run then settled the broadcast as expired, the job still rethrew the failure: a failed attempt and a retry ten seconds later that found nothing pending. After a settle the job now completes, because the settle ended that delivery too.
- **An absurdly long duration got the grammar message.** `TimerDefinitionRules.TryParseFixedDuration` refuses a length too long for a `TimeSpan` as it refuses malformed text, so `P9999999999999999D` was answered with "must be a fixed ISO-8601 duration" and the page's amount `9999999999999999` **Days** with the whole-number message. The new `TimerDefinitionRules.IsFixedDurationBeyondRange` tells the two apart without changing what `TryParseFixedDuration` returns, so both now get the range message.

Evidence:

| Command or run | Result |
| --- | --- |
| `dotnet test Flowbit/tests/Flowbit.Tests/Flowbit.Tests.csproj --no-build --filter` over the expiry, rules, formatting, Signals page, timer-rule, typed-input, and broadcast-event classes | 327 passed, 0 failed. The new job test fails against the processor without the fix. |
| `dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet` on the final code | 6,328 passed, 0 failed (20 m 2 s): the 6,303 of the earlier run and the 25 added cases. |
| Smoke R18 (with the new `5-` step) and R16, after publishing the API and UI hosts | 3 and 9 passed, 0 failed. |
| Manual, built-in Chromium on the published API (`http://127.0.0.1:25017`) and UI (`http://127.0.0.1:25152`) with a fresh database | At 1440×900, `5-` typed key by key stayed in the box (`badInput` true, no write to the box's `value` from the page), and **Review signal** showed `Enter the expiry as a whole number of 1 or more, or leave it empty.` with no **Throw signal**. `10` reviewed as `10 minutes after the throw`; focusing and leaving the box kept that review; appending `e` ended it, and **Review signal** refused `10e`. At 390×844 the row fit, `5-` was refused the same way, and the page did not overflow horizontally. The console logged only Blazor's connection lines. |

### Plan review of 2026-11-23

A review of the plan against the code, on the day it was written, found these gaps. Each is corrected in the sections below.

- **Delivery time was not weighed against the expiry.** One job delivers a definition's recipients one at a time, and the [parent measured](event-04-signal-events.md#current-state) about 20 a second, so a broadcast to 10,000 waits of one definition needs about eight minutes. A short expiry on a large broadcast therefore ends most deliveries although nothing is wrong. The plan now states this limit in [section 1](#1-scope-and-invariants), and the guides and the risks cover it.
- **The sweep's claim could not use its index.** PostgreSQL never uses a volatile function such as `clock_timestamp()` as an index condition, so the claim would have read every row of the partial index on each pass. The sweep now reads the database time once per pass and compares with that value.
- **The open-broadcast count had no stated clock.** It uses the database's transaction time, `now()`, as the one stated exception to invariant 1.
- **The lost-lease statement read the clock for each row**, so one job's deliveries could end on both sides of the expiry. It now reads the time once.
- **The reason given for the lost-lease rule was wrong.** Every broadcast that finished with failures and then passes its expiry has failures that nobody can retry, so that cannot be the reason. The rule follows from invariant 2.
- **The delivery metric can count one delivery twice.** The job records a metric for every outcome its executor returns, also for a delivery it found already ended. The plan now says so and leaves it.
- **Documentation rows were missing:** the upgrade table of the deployment guide, its monitoring text, and its troubleshooting row for a broadcast that stays `delivering`, which says today that nothing expires; the count of event types in the runtime reference; the status lists in the DTO comments and in the event catalog's field description; guidance on choosing an expiry; and how to find the deliveries that expired for one instance.
- **Browser scenarios.** R17 pins three broadcast types in its code and in its README row, so it changes, and the **Webhooks** check moved there from R18. S14 cannot set the limit to 1 on a stack that other scenarios share; it sets it relative to the broadcasts that are already delivering, as S9 does, and restores it.
- **Test details.** The hosted sweep test did not say how the delivery job is kept from running first, and `ExpireNextDueAsync` was named only in the test table.
- **An instance shows no trace of an expired signal**, although its wait is still open. That follows from invariant 4. Showing it on instance detail is recorded under [Later releases](#later-releases).

Checked and left unchanged:

- **Isolation.** The only repeatable-read transactions are the two inbox reads; a delivery runs read committed. The check is correct at any level, because `ExpiresAt` never changes and `clock_timestamp()` ignores snapshots; only the race tests rely on read committed.
- **Binding.** The API configures no JSON options beyond the web defaults, which read a number from a string and never a string from a number, so a numeric `expiresAfter` fails binding.
- **Status badge.** No other page passes a status named `expired` to `StatusBadge`. The delegation pages compute their own tone.
- **Status derivation.** `RefreshCountsAsync` and the lost-lease reconciliation are the only two statements that derive a broadcast's status.

### Facts the plan rests on

Verified in the code on 2026-11-23, before the change.

- **Request and hash.** `ThrowSignalRequest` in [SignalDtos.cs](../../Flowbit/src/Flowbit.Shared/Dtos/SignalDtos.cs) has seven members and none for an expiry. The API ignores JSON members it does not know. `HashRequest` in [SignalBroadcastService.cs](../../Flowbit/src/Flowbit.Service/Services/SignalBroadcastService.cs) hashes the name, the family, and the payload, and adds a fourth line only for a business-key or instance scope, so keys stored before those scopes existed still replay.
- **Throw.** `ThrowAsync` validates, replays an earlier broadcast for a repeated key, reads the two limits, and takes one `now` from the injected `TimeProvider` for the broadcast's `CreatedAt` and its jobs. [SignalBroadcastWriter.cs](../../Flowbit/src/Flowbit.Service/Services/SignalBroadcastWriter.cs) inserts the broadcast, freezes the recipients, and enqueues one job per workflow definition, for the API and for a workflow's throw alike.
- **Open-broadcast limits.** `CountOpenAsync` in [SignalBroadcastRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/SignalBroadcastRepository.cs) counts the broadcasts with the status `delivering` and the given thrower kinds, without a lock. The API's budget and the workflows' budget both call it.
- **Delivery to a wait.** `DeliverSignalAsync` in [WorkflowEngineService.Signals.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.Signals.cs) reads the delivery without a lock and returns when it is no longer pending. It then locks the instance hierarchy, the subscription, and the delivery row, and ends as `skipped` with `subscription_ended` when the wait has ended. It reads the broadcast row, with its payload, only after those checks, and never locks it. `FinishDeliveryAsync` ends a delivery through `CompleteDeliveryAsync`, which adds one to `AttemptCount`.
- **Delivery to a start.** `StartFromSignalAsync` in [WorkflowEngineService.SignalStarts.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.SignalStarts.cs) reads the broadcast before its transaction, then takes the family's shared start lock and the delivery row. It can wait there for an operation that changes the family's default.
- **Delivery job.** [SignalBroadcastJobProcessor.cs](../../Flowbit/src/Flowbit.Service/Services/SignalBroadcastJobProcessor.cs) delivers one definition's pending deliveries in pages of 100, each delivery in its own scope and transaction, and recounts after every page and once at the end in a transaction that holds the broadcast's row lock. Every continuation passes `forceDurableActivities`, so a delivery makes no external call.
- **Cancel.** `CancelPendingDeliveriesAsync` ends every pending delivery of a broadcast in one `UPDATE`, without changing `AttemptCount`. The statement waits for a delivery that is inside its transaction and then leaves it as it finished. The [parent's measurements](event-04-signal-events.md#current-state) put a cancel of 100,000 pending deliveries at 1,753 ms.
- **Two recounts.** The status is derived from the delivery rows in two statements: `RefreshCountsAsync` in the broadcast repository, and the raw SQL of `ReconcileExhaustedSignalBroadcastsAsync` in [WorkflowJobRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/WorkflowJobRepository.cs). The second runs at the start of every `LeaseRunnableAsync` call, fails the pending deliveries of a job whose final lease expired with `lease_exhausted`, and writes its timestamps with `clock_timestamp()`.
- **Retry.** `RetryFailedAsync` accepts only the status `completedWithIssues` with the current `updatedAt`, returns failed deliveries to `pending`, reopens the broadcast, and enqueues new jobs.
- **Schema.** `CK_signal_broadcasts_status` allows `delivering`, `completed`, `completedWithIssues`, and `cancelled`. `CK_signal_broadcasts_counts` keeps the four outcome counters within `TotalDeliveryCount`. `IX_signal_broadcasts_open_thrower` is a partial index on `ThrownByKind` for delivering broadcasts. `signal_deliveries."ResultCode"` holds 64 characters and has no value check.
- **Clocks.** The repository methods take their timestamps from the `TimeProvider` of the API or Worker host. The freeze statement uses `now()`. A transaction runs at PostgreSQL's default isolation, read committed, so a statement issued after a lock wait sees rows committed during the wait. Host clocks and the database clock have disagreed in this project's own test runs: `86ec7a4` backdates job due times in the lease helpers for that reason.
- **Durations.** `TimerDefinitionRules.TryParseFixedDuration` in [WorkflowModel.cs](../../Flowbit/src/Flowbit.Shared/Models/WorkflowModel.cs) trims its text and accepts weeks, or days, hours, minutes, and seconds, each with an optional decimal fraction. It refuses years, months, and a zero length. Timers, due dates, and `UserTasks.DueSoonThreshold` use it.
- **Worker.** The Worker hosts the job dispatcher, queue telemetry, retention, timer-start reconciliation, and the webhook services ([Program.cs](../../Flowbit/src/Flowbit.Worker/Program.cs)). Nothing in it visits a broadcast except its delivery jobs and the backlog gauges. [TimerStartReconciliationService.cs](../../Flowbit/src/Flowbit.Worker/TimerStartReconciliationService.cs) is the model of a periodic pass: it waits for the schema gate and takes its interval from `WorkerOptions`.
- **Other readers of pending deliveries.** The `signalBroadcasts` retention category removes a broadcast whose status is not `delivering`, aged from `CompletedAt`. Deleting a workflow version is refused while a delivering broadcast has a pending delivery of that version.
- **Webhook capture.** `capture_signal_broadcast_lifecycle()` maps three statuses to event types and returns for any other status. Its triggers queue a call for every change to a status other than `delivering`. `LifecycleEventTypes.Subscribable` lists fourteen types, and `SignalBroadcastFields` in [WebhookModels.cs](../../Flowbit/src/Flowbit.Service/Models/WebhookModels.cs) is the one field list of the three broadcast types. `EveryFinishedBroadcastStatusHasItsEventType` in `SignalBroadcastLifecycleEventTests` fails when a finished status has no event type, and `EventCatalogListsTheFourteenSubscribableTypesAndTheCaptureSwitch` in `WebhookApiTests` pins the count.
- **Telemetry.** `SignalRuntimeTelemetry.RecordDelivery` counts one finished delivery by status and result code. A cancel records nothing for the deliveries it ends.
- **Signals page.** [Signals.razor](../../Flowbit/src/Flowbit.Ui/Components/Pages/Signals.razor) keeps the draft in private fields, holds the reviewed draft in a `ThrowReview` record with one idempotency key, ends the review on every edit, and polls the open broadcast every three seconds while it is delivering. Its status filter lists `SignalBroadcastStatuses.All`. `SignalFormatting` owns the labels, `CanRetry`, and `IsLive`. `StatusBadge` gives a status it does not know the neutral tone. Flowbit.Ui registers `TimeProvider.System`. `SignalsPageTests` sets the page's private fields by name.
- **Browser suites.** The smoke suite has no Worker and cannot publish a definition with a signal event, so its R16 throws to no recipient; its scenarios go up to R17. The acceptance suite runs Workers, stops and starts them in S4, S9, and S12, and has signal scenarios S1 to S13.

## Builds on

- The cancel path, which already ends every pending delivery of one broadcast in one statement under the broadcast's row lock, and waits correctly for deliveries in flight.
- `SignalDeliveryResultCodes` in [SignalDtos.cs](../../Flowbit/src/Flowbit.Shared/Dtos/SignalDtos.cs) and the skipped outcome.
- The recount that runs in a transaction holding the broadcast's row, and derives the status from the delivery rows.
- `TimerDefinitionRules.TryParseFixedDuration`, the duration grammar authors already know from timers and due dates.
- The fencing rule of webhook leases: compare with `clock_timestamp()` read after the row is locked, never with `now()`.
- The `signalBroadcast.*` lifecycle webhook events of [EVENT-04D](event-04d-webhook-events-for-signal-broadcasts.md). Their triggers already fire for any finished status, so a new status needs its type in `capture_signal_broadcast_lifecycle()`, `LifecycleEventCatalog`, and the tests, and no new trigger.
- `TimerStartReconciliationService` as the shape of a periodic Worker pass, and `SignalRuntimeTests`, `SignalStartRuntimeTests`, and `SignalBroadcastLifecycleEventTests` with their helpers.

## Open questions

No question remains open. The plan decides the three original questions and the ones it raised. The user confirmed four of the decisions on 2026-11-23, the ones that change what callers and receivers see: the duration in the request, the status `expired`, the event type `signalBroadcast.expired`, and the refusal to retry after the expiry. After the [plan review](#plan-review-of-2026-11-23), the user also confirmed that guidance is enough for the delivery-time limit, with no change to the design.

| Question | Decision |
| --- | --- |
| Is the expiry an absolute time or a duration from the throw? | A duration in the request, `expiresAfter`, and an absolute time on the broadcast, `expiresAt`. The caller's clock then plays no part, a retried request with the same key replays the first broadcast with its first `expiresAt`, and the later releases, a default for the deployment and an expiry on a throw event, are durations too. An absolute time in the request is recorded under [Later releases](#later-releases). |
| Which duration grammar? | The fixed ISO-8601 duration that timers and due dates use, such as `PT30S`, `PT10M`, or `P1D`, through `TimerDefinitionRules.TryParseFixedDuration`. One grammar serves the request now and the authored expiry later. |
| Which clock decides? | The database's. It computes `expiresAt` at the throw and makes every comparison with `clock_timestamp()`. The API host, the Worker host, and the caller can each be off by seconds or more, and two clocks in one comparison would let a signal act after its expiry. |
| Who settles expired deliveries: each delivery job when it reaches them, or one statement when the first job notices the expiry? | One statement, under the broadcast's row lock, as a cancel does. A delivery job runs it when it finds the expiry passed before a page, or when one of its deliveries is refused by the check. A Worker sweep runs it for a broadcast whose job is not running: a job in an incident, in retry backoff, or behind a long queue. Each delivery still checks the expiry under its own locks, which is what makes the rule exact. |
| Do expired deliveries count as skipped, or does a broadcast gain an expired counter and status? | All three. The delivery is `skipped` with `broadcast_expired`, so the four outcome counters still add up to the total for every existing reader. The broadcast gains `expiredCount`, which counts those skipped deliveries, as `startCount` counts the starts among the recipients. A broadcast that finishes with at least one expired delivery has the status `expired`, as a cancelled one has `cancelled`. |
| Why a status of its own, and not `completed` with a count? | A subscription filters by event type only. With `completed`, a receiver could learn of an expiry only by subscribing to every completed broadcast and reading each body, which is the flood that [EVENT-04D](event-04d-webhook-events-for-signal-broadcasts.md#10-delivery-sequence-rollout-and-risks) warns about. EVENT-04D chose one type per outcome for the same reason. The status is also the honest answer on the **Signals** page: a broadcast that reached nobody because the Worker was down did not complete. |
| Which status wins when an expired broadcast also has failed deliveries? | `expired`. The failures cannot be retried any more, and `failedCount` still shows them. |
| Does the expiry raise a webhook event? | Yes, a fourth type, `signalBroadcast.expired`. Its body is the shared body plus `expiredCount`. The three existing bodies stay byte for byte as they are, because a broadcast with one of those statuses has no expired delivery to report, except a cancelled one in a narrow race that `skippedCount` still covers. |
| Can failed deliveries of an expired broadcast be retried? | No. A retry after the expiry returns `409`, checked under the broadcast's row lock with the database clock. A retry before the expiry is allowed and keeps the original `expiresAt`, so a retried delivery that runs late is skipped like any other. Extending an expiry is recorded under [Later releases](#later-releases). |
| Does an expired signal end the wait? | No. The catch keeps waiting and a boundary keeps listening, for a later signal. The guides recommend a timer boundary on a catch that must not wait forever. |
| Does a start delivery expire? | Yes, by the same rule: no instance is created. |
| Does the expiry stop a delivery that is already running? | No. A delivery that passed its check finishes, and may commit shortly after the expiry. After the check it waits for no other instance and makes no external call. |
| Does a broadcast that is past its expiry and not yet settled count toward the open-broadcast limits? | No. The count leaves it out by comparing `expiresAt` with the database clock. The limit then keeps its purpose, bounding the broadcasts that can still act, also while no Worker runs. |
| What happens when a delivery job loses its last lease after the expiry? | Its pending deliveries end as expired, not as `failed` with `lease_exhausted`. They did not run, and invariant 2 gives a delivery that has not run by the expiry one outcome only. |
| Does the expiry allow for the time delivery takes? | No. It is counted from the throw and includes the time a job waits in the queue and the deliveries before it. A caller sizes it for the whole broadcast; the guides give the delivery rate. |
| Can a broadcast past its expiry still be cancelled? | Yes, until it is settled; it then has the status `cancelled`. Once it is `expired`, a cancel returns `409`, as for every finished broadcast. |
| Can a workflow's throw set an expiry? | Not in this release. The writer accepts one, so the later release adds only the authored property and its validation. |
| Is there a setting? | No engine setting. The bounds, 1 second and 365 days, are constants. The Worker gains one option for the sweep's interval. |
| Does the API report whether the expiry has passed? | No flag. `expiresAt` and the status are the contract, and the server decides with the database clock. Flowbit.Ui compares `expiresAt` with its own clock only to show a note and to hide **Retry failed**. |

## Plan

Written on 2026-11-23 following [Writing a plan](README.md#writing-a-plan). Effort is revised from S–M to **M**: the item needs a migration, changes the throw, delivery, retry, and lost-lease paths, adds a Worker pass, a status, and a webhook event type, and changes two pages. It changes no workflow JSON, no editor or viewer code, no AI authoring, and no version-change or reactivation rule, so the plan has no section for those.

### 1. Scope and invariants

An expiry is a cancel that the clock performs. It ends the deliveries that have not run, leaves every wait as it is, and finishes the broadcast.

The invariants of the [parent plan](event-04-signal-events.md#1-scope-and-invariants) hold unchanged. These matter here, and the first four are new:

1. **One clock.** The database computes `expiresAt`, and every decision that ends or refuses a delivery compares it with `clock_timestamp()`. No host clock decides, and no `now()`, which stays at the transaction's start while a lock wait can outlast the expiry. The count for the soft open-broadcast limits is the one place that uses `now()`.
2. **After the expiry, no delivery acts.** A pending delivery of an expired broadcast gets no outcome other than `skipped` with `broadcast_expired`, whichever path ends it: its own check, the settle statement, or the lost-lease reconciliation. The one exception is a cancel that arrives first.
3. **`expiresAt` never changes** after the throw. Every check can therefore read it without locking the broadcast.
4. **An expiry writes nothing to an instance.** No variable, history row, node visit, token, or subscription changes, and no instance is locked to settle a broadcast.
5. **A signal is a broadcast, not a queue.** An expired signal is dropped. A wait that is still open receives a later signal, never this one.
6. **A delivery never locks the broadcast row**, and everything else that changes deliveries locks it first, so no lock cycle exists.
7. **Workflows without signals pay nothing.** No transition runs a new statement. A broadcast without an expiry pays one primary-key read per delivery and one per page.
8. **Without `expiresAfter`, nothing changes.** Requests, responses apart from two new members, statuses, events, and stored idempotency keys behave as today.

This release does not add an expiry for throws from workflows, a default expiry, an absolute time in the request, a way to change an expiry, an expiry that ends waits, or any change to scopes, freezing, retention categories, version changes, or reactivation.

Two limits follow:

- **A delivery that has started finishes.** Invariant 2 is a check and not a stop: a delivery that passed its check before the expiry finishes its transaction, so an instance can show a `signalReceived` row committed moments after `expiresAt`.
- **The expiry does not allow for delivery time.** It is counted from the throw and includes the time a job waits in the queue and the deliveries before it. One job delivers a definition's recipients one at a time, about 20 a second in the [parent's measurement](event-04-signal-events.md#current-state), so a broadcast to 10,000 waits of one definition needs about eight minutes. A shorter expiry ends the later deliveries although nothing is wrong. Deliveries run in ascending ID, so the earliest subscriptions of each definition receive the signal. [EVENT-04G](event-04g-parallel-signal-delivery.md) would shorten delivery; this item does not.

### 2. Request contract and validation

```json
{
  "name": "warehouse.product-recall",
  "workflowKey": "example-signal-boundaries-recall",
  "payload": { "reason": "Supplier recall" },
  "expiresAfter": "PT10M"
}
```

| Rule | Value |
| --- | --- |
| Field | `expiresAfter`, an optional string. Null or absent means the broadcast never expires. |
| Grammar | The fixed ISO-8601 duration of timers: weeks, or days, hours, minutes, and seconds, with optional fractions. Years and months are refused, because their length is not fixed. Surrounding white space is trimmed. |
| Range | From 1 second to 365 days. |
| Meaning | The broadcast expires that long after the database's time when the throw runs. |

The three checks run after the scope checks and before the request is hashed, so they also apply to a request that would replay. The first problem is reported with `400`:

| Problem | Message |
| --- | --- |
| Present and blank | `expiresAfter cannot be blank; omit it for a signal that does not expire.` |
| Not a fixed duration | `expiresAfter must be a fixed ISO-8601 duration such as PT10M or P1D.` |
| Outside the range | `expiresAfter must be between 1 second and 365 days.` |

- **Too long to compute.** A well-formed duration whose length a `TimeSpan` cannot hold, such as `P9999999999999999D`, is outside the range, not malformed.
- **Wrong JSON type.** A number, such as `"expiresAfter": 600`, is a binding failure with the existing `{"error": ...}` body and its path. A test confirms it.
- **Idempotency.** The hash gains a line `expiresAfter:<ticks>` only when the field is present. A request without it hashes exactly as today, so stored keys still replay. The line holds the parsed length, so `PT60S` and `PT1M` are the same request. The same key with another length, or with an expiry where the first request had none, returns `409` with the existing message.
- **Replay.** A replay returns the stored broadcast with its first `expiresAt`, also when that time has passed. It never extends the expiry.

Code:

- **`SignalExpiryRules`**, a new dependency-free class in `Flowbit.Shared/Models`, beside `BusinessKeyRules`. It owns the two bounds and one function that returns the parsed duration or one of the three problems. The service and the **Signals** page call it.
- **`ThrowSignalRequest`** gains `ExpiresAfter` as its last parameter with a null default, so existing callers compile unchanged.
- **`SignalBroadcastStatuses`** gains `Expired`, listed in `All` before `Cancelled` and accepted by `IsFinished`. **`SignalDeliveryResultCodes`** gains `BroadcastExpired`, `broadcast_expired`.
- **`SignalBroadcastSummaryDto` and `SignalBroadcastDetailDto`** gain `ExpiresAt` and `ExpiredCount` as trailing parameters with defaults, with `<param>` comments for the OpenAPI schema. Their `Status` comments, which list four statuses today, name the fifth. `NewSignalBroadcastRecord` gains an optional `ExpiresAfter`, and `SignalBroadcastRecord` gains `ExpiresAt` and `ExpiredCount`.

### 3. Persistence and migration

One migration, `AddSignalBroadcastExpiry`, in a transaction. Update the entity, the records, `AppDbContext`, and the model snapshot with it.

| Object | Change |
| --- | --- |
| `signal_broadcasts."ExpiresAt"` | New nullable `timestamptz`: when the broadcast expires. Null means never. |
| `signal_broadcasts."ExpiredCount"` | New `integer`, not null, default 0: the skipped deliveries whose result is `broadcast_expired`. |
| `CK_signal_broadcasts_status` | Replaced, to allow `expired`. |
| `CK_signal_broadcasts_expiry` | New check: `ExpiredCount` is between 0 and `SkippedCount`; a broadcast without `ExpiresAt` has no expired delivery and not the status `expired`; a broadcast with the status `expired` has at least one expired delivery. |
| `IX_signal_broadcasts_open_expiry` | New partial index on `ExpiresAt` for rows with the status `delivering` and an `ExpiresAt`. The sweep reads it. |
| `capture_signal_broadcast_lifecycle()` | Replaced, as [section 7](#7-lifecycle-webhook-event) describes. The two triggers stay. |

- **Existing rows.** `ExpiresAt` is null and `ExpiredCount` is 0 in every existing row, which satisfies both checks. Nothing is backfilled.
- **Cost.** Both columns change only the catalog. Each of the two checks scans `signal_broadcasts` once in the migration's transaction, as the check of `AddSignalThrowEvents` did. The table has one row per broadcast. The index is empty when it is built.
- **Deliveries.** `signal_deliveries` does not change. `broadcast_expired` fits its `ResultCode` column, and an expired delivery has no description.
- **Counts.** The schema keeps its tables. No engine setting and no retention category is added.
- **Down migration.** It refuses while a broadcast has an `ExpiresAt` or the status `expired`. Dropping the column would turn a broadcast that expires into one that never does, and the restored check rejects the status. Retention removes such broadcasts once they are finished and old enough; cancel the ones that are still delivering. It then restores the earlier capture function and status check.

### 4. Expiry lifecycle

#### Throw

`ThrowAsync` validates `expiresAfter` with the scope and passes the parsed duration to the writer. In the throw's transaction, `AddAsync` reads the database time once for a broadcast with a duration (`SELECT clock_timestamp()`) and stores that time plus the duration as `ExpiresAt`. A throw without one runs no new statement. The response carries `expiresAt`.

- `createdAt` stays the API host's time. The two can differ from the requested length by the hosts' clock difference, and the guides say that `expiresAt` is the one to trust.
- A throw that reaches no recipient is stored as `completed` at once, with its `expiresAt`, and never becomes `expired`.
- `ThrowSignalFromNodeAsync` passes no expiry.

#### Open-broadcast limits

`CountOpenAsync` counts a delivering broadcast only when its `ExpiresAt` is null or later than the database's time. Both budgets use it, so the rule holds for workflows' broadcasts once they can expire. The backlog gauges do not change: they report the pending rows that exist, settled or not.

The statement compares with `now()`, the database's time when the throw's transaction began. This is the one use of `now()`: the limit is soft, the value is at most as old as that transaction, and the error only counts a broadcast that expired a moment ago.

#### The check in each delivery

`DeliverSignalAsync` asks the database, right after the delivery row is locked and found pending, and before any other outcome is decided:

```sql
SELECT COALESCE(b."ExpiresAt" <= clock_timestamp(), FALSE)
FROM flowbit.signal_broadcasts b
WHERE b."Id" = @broadcastId;
```

When the answer is true, the delivery ends as `skipped` with `broadcast_expired` through a new repository method that sets the status, the code, and both timestamps from `clock_timestamp()` and leaves `AttemptCount` alone. Its transaction commits with nothing else written, and the executor reports the outcome. `StartFromSignalAsync` asks the same question after the family lock and the delivery row, before it reads the family's default, so an expired start keeps the version that listened and names no instance.

- **Order of outcomes.** The expiry is checked before the wait is examined. A pending delivery of an expired broadcast is therefore `broadcast_expired` also when its wait has ended, which is what the settle statement would have written.
- **Why a statement of its own.** The broadcast row is read later for its payload, which can hold 256 KiB, and only for deliveries that pass the fence. The check reads no payload.

#### Settle

A new scoped service, `SignalBroadcastExpirer` behind `ISignalBroadcastExpirer`, ends an expired broadcast in one transaction. It has two methods: `ExpireAsync` for a broadcast that a job names, and `ExpireNextDueAsync` for the sweep, which first claims a due broadcast. Both run these steps:

1. Lock the broadcast row `FOR UPDATE`. Stop when the status is not `delivering`.
2. Read the database time once, for the rows the next step ends and for the recount.
3. End the pending deliveries, with the expiry condition inside the statement:

   ```sql
   UPDATE flowbit.signal_deliveries d
   SET "Status" = 'skipped',
       "ResultCode" = 'broadcast_expired',
       "CompletedAt" = @databaseNow,
       "UpdatedAt" = @databaseNow
   FROM flowbit.signal_broadcasts b
   WHERE b."Id" = @broadcastId
     AND b."ExpiresAt" <= clock_timestamp()
     AND d."BroadcastId" = b."Id"
     AND d."Status" = 'pending';
   ```

4. Recount with `RefreshCountsAsync`, which now derives the status `expired`.
5. Commit. Then count the ended deliveries in the `flowbit.signals.deliveries` metric and log one information line with the broadcast's ID and the number, and no name or payload.

The statement waits for a delivery that is inside its transaction and then leaves it as it finished, as a cancel does. After it, no delivery of the broadcast is pending, so the recount always finishes the broadcast. A second settle finds the status changed and does nothing. A settle that runs before the expiry ends no row, and its recount writes nothing.

`SignalRuntimeTelemetry` gains a method that adds a number of deliveries at once, so the existing counter shows `skipped` with `broadcast_expired` on the Worker's `/metrics`.

#### Delivery job

`SignalBroadcastJobProcessor` asks the check's question before its first page and before every later page. When the broadcast has expired, it calls the settle and leaves its loop. It does the same as soon as one of its deliveries reports `broadcast_expired`. The job then recounts and completes as today, also when a delivery earlier in the run failed unexpectedly and would otherwise be retried: the settle ended that delivery too.

- A Worker that returns after an outage therefore locks no instance for an expired broadcast: the first job that runs settles it.
- Jobs of the same broadcast for other definitions find nothing pending and complete.
- **Metric.** The job records a metric for every outcome its executor returns, also for a delivery it found already ended. After a settle by the sweep, a job in mid-page can therefore count one `broadcast_expired` more than the settle did. The counter serves dashboards, not audits, and this item leaves that behavior as it is.

#### Worker sweep

A new hosted service in the Worker, `SignalBroadcastExpiryService`, settles the broadcasts that no job is working on. It waits for the schema gate, then runs every `FlowbitWorker:SignalExpirySweepSeconds` (default 5, from 1 to 3,600). Each pass reads the database time once (`SELECT clock_timestamp()`) and settles at most 20 broadcasts, each in a transaction that first claims one with that time:

```sql
SELECT b."Id"
FROM flowbit.signal_broadcasts b
WHERE b."Status" = 'delivering'
  AND b."ExpiresAt" IS NOT NULL
  AND b."ExpiresAt" <= @databaseNow
ORDER BY b."ExpiresAt", b."Id"
LIMIT 1
FOR UPDATE SKIP LOCKED;
```

- **Why a parameter.** PostgreSQL does not use a volatile function such as `clock_timestamp()` as an index condition, so a comparison with it would read every row of the partial index on each pass. The value read once is slightly old, which can only delay a claim to the next pass. The settle statement still decides with `clock_timestamp()`. The claim states `IS NOT NULL`, so the planner need not infer the index's predicate.
- **Replicas.** A row that another replica, a recount, or a cancel holds is skipped and taken on a later pass, so sweeps never queue behind each other.
- **Failures.** A pass that fails logs a warning, and the next pass runs, as the timer-start reconciliation does.
- **Cost.** One read of the clock and one range read of a small partial index per pass and replica. A deployment that never sends `expiresAfter` has an empty index.
- **What it covers.** A delivery job that waits in an incident, in retry backoff, or behind other activity jobs. The broadcast finishes within one interval of its expiry. The job's incident stays open; retrying it runs a job that finds nothing pending.
- **While no Worker runs**, nothing settles. The broadcast stays `delivering` with a passed `expiresAt`, does not count toward the limits, and is settled by the first job or sweep after a Worker starts.

#### Recount and status

Both recount statements count the expired deliveries (`"Status" = 'skipped' AND "ResultCode" = 'broadcast_expired'`), write `ExpiredCount`, and derive the status of a delivering broadcast with nothing pending in this order: `expired` when any delivery expired, else `completedWithIssues` when any failed, else `completed`. A cancelled or expired broadcast keeps its status. `RefreshCountsAsync` also writes when only `ExpiredCount` changes.

The status is derived from the rows, not set by the settle. A broadcast whose last pending delivery was ended by its own check, with no settle in between, therefore finishes as `expired` in whichever recount runs next.

#### Lost lease

`ReconcileExhaustedSignalBroadcastsAsync` already locks the broadcasts of the exhausted jobs. Its delivery statement reads `clock_timestamp()` once, in a common table expression, and joins the broadcast. It writes `skipped` with `broadcast_expired`, without a description and without an attempt, when the broadcast's `ExpiresAt` is at or before that time; otherwise `failed` with `lease_exhausted`, as today. One reading keeps a job's deliveries on one side of the expiry. Its recount gains the expired count and the status order above. The `lease_exhausted` incident opens in both cases.

#### Cancel and retry

- **Cancel** is unchanged. A delivering broadcast can be cancelled whether or not its expiry has passed, and the row lock decides between a cancel and a settle: the second one finds nothing to do. A broadcast with the status `expired` returns the existing `409` for a finished broadcast.
- **Retry.** `RetryFailedAsync` asks the check's question after it has locked the broadcast and verified the status, and before it compares `expectedUpdatedAt`. Past the expiry it returns `409`: `Signal broadcast #57 expired at 2026-11-23T09:10:00Z; its failed deliveries cannot be retried. Throw the signal again to reach the waits that are still open.` A broadcast with the status `expired` gets the existing message that names its status.
- **A retry before the expiry** reopens the broadcast with its original `expiresAt`. If the expiry passes before the retried deliveries run, they end as expired and the broadcast finishes as `expired` with no failed delivery left.

#### Outcomes

| Delivery status | Code | Cause |
| --- | --- | --- |
| `skipped` | `broadcast_expired` | The broadcast's expiry passed before the delivery ran. Nothing was written, and a wait stays open. |

| Broadcast status | Meaning |
| --- | --- |
| `expired` | No delivery is pending, and the expiry ended at least one. Delivered, failed, and other skipped deliveries keep their outcomes. |

#### Not affected

- **Instances.** No history row, node visit, FlowInfo evidence, or variable records an expiry. Instance detail keeps listing the wait under **Waiting for signals**.
- **Versions and reactivation.** No rule changes. Subscriptions are untouched.
- **Retention.** An expired broadcast ages from `CompletedAt` like every finished one. One that is past its expiry and not settled is still `delivering` and stays.
- **Deleting a version** stays refused while one of its deliveries is pending, also for a broadcast that is past its expiry and not settled. A Worker, or a cancel, settles it.

### 5. Transactions and lock order

| Transaction | Locks, in order | Reads the expiry |
| --- | --- | --- |
| Delivery to a wait | Instance hierarchy, subscription, delivery row. | After the three locks, without locking the broadcast. |
| Delivery to a start | The family's shared start lock, delivery row. | After both. |
| Settle, from a job | Broadcast row, then delivery rows through the `UPDATE`. | Inside the `UPDATE`. |
| Settle, from the sweep | The same, with the broadcast row claimed through `SKIP LOCKED`. | In the claim and inside the `UPDATE`. |
| Retry | Broadcast row, then delivery rows. | After the broadcast row. |
| Lost lease | Broadcast rows in ID order, then delivery rows. | Inside the delivery statement. |
| Cancel, recount | Unchanged: the broadcast row first. | Not at all. |

A delivery holds runtime rows and one delivery row and never asks for a broadcast row. Every other transaction takes the broadcast row first and never a runtime row. No cycle is possible, and no transaction that settles a broadcast waits for an instance.

| Race | Result |
| --- | --- |
| A delivery passes its check, then the expiry passes, then a settle runs. | The settle waits for the delivery's row, finds it no longer pending, and leaves it `delivered`. |
| A settle commits, then a delivery reaches its row. | The delivery finds the row no longer pending and writes nothing. |
| A delivery waits for its instance while the expiry passes. | After its locks it reads the committed `ExpiresAt` against the current clock and ends as expired. |
| A start waits for a default change while the expiry passes. | The same, after the family lock. |
| Two sweeps, or a sweep and a job, settle one broadcast. | The row lock orders them. The second finds the status changed, and one event is raised. |
| A cancel and a settle. | The first to hold the row decides the status. The other does nothing, or returns `409`. |
| A retry and the expiry. | The retry's check runs under the row lock. A retry that passed it reopens the broadcast, and the next sweep or job settles it once the expiry has passed. |

PostgreSQL race tests for the first, third, fourth, and sixth rows are a release gate.

### 6. API contracts and authorization

No route is added.

| Route | Change |
| --- | --- |
| `POST /api/signal-broadcasts` | Accepts `expiresAfter`. `400` for the three problems of [section 2](#2-request-contract-and-validation). `409` for a repeated key with another expiry. The response carries `expiresAt` and `expiredCount`. |
| `GET /api/signal-broadcasts` | Accepts `status=expired`. Each item carries `expiresAt` and `expiredCount`. |
| `GET /api/signal-broadcasts/{id}` | The same two members. `status` can be `expired`. |
| `GET /api/signal-broadcasts/{id}/deliveries` | A delivery can have `resultCode` `broadcast_expired`, with a null `resultDescription` for every caller. |
| `POST /api/signal-broadcasts/{id}/cancel` | Unchanged. `409` for the status `expired`, as for every finished broadcast. |
| `POST /api/signal-broadcasts/{id}/retry-failed` | `409` once the expiry has passed. |
| `GET /api/signal-subscriptions/summary` | Unchanged. |
| `GET /api/webhook-event-types`, the subscription routes, and the delivery filters | Fifteen subscribable types; `signalBroadcast.expired` is accepted. |

- **Authorization.** Unchanged. A user with a role from `Signals.RequiredRole` and a managed client with `signals.throw` may set an expiry. A client sees the new members on its own broadcasts only.
- **Compatibility.** A caller that ignores unknown members reads every response as before. `skippedCount` includes the expired deliveries, so the four outcome counters still add up to `totalDeliveryCount`, and `pendingCount` is computed as today. A caller that switches on `status` must accept `expired`, which only appears after someone sends `expiresAfter`.
- **A replica without this release** ignores `expiresAfter` and throws a broadcast that never expires. Its response has no `expiresAt`, so a caller can tell.
- **OpenAPI.** The descriptions of the throw and retry operations in `SignalEndpoints` and `FlowbitOperationCatalog` name the expiry. The schema tables of the API reference are written by hand and change with the DTOs.

### 7. Lifecycle webhook event

| Type | Raised when the status changes to | Body |
| --- | --- | --- |
| `signalBroadcast.expired` | `expired` | The twelve fields of the other broadcast types, then `expiredCount`. |

- **Where it comes from.** The transaction that derives the status: a settle by a job or by the sweep, the lost-lease reconciliation, or the recount after a delivery's own check ended the last pending row. The triggers of [EVENT-04D](event-04d-webhook-events-for-signal-broadcasts.md#5-capture-migration-function-and-triggers) already fire for it.
- **Capture function.** `AddSignalBroadcastExpiry` replaces `capture_signal_broadcast_lifecycle()` with `CREATE OR REPLACE`. The new version maps `expired` to the type, reads `"ExpiredCount"` with the other counters, and appends `,"expiredCount":n` only for that type. The bodies of `signalBroadcast.completed`, `signalBroadcast.completedWithIssues`, and `signalBroadcast.cancelled` stay byte for byte as they are.
- **Body.** `status` is `expired`. `skippedCount` includes `expiredCount`. `failedCount` can be above zero, and those deliveries cannot be retried. The body gains no string, so its bound of 3 KiB holds. It carries no `expiresAt`: a receiver that needs it reads the broadcast.
- **Matching, envelope, and privacy** follow the other broadcast types: the scope's family and instance, and no payload, key, or identity.
- **Catalog.** `LifecycleEventTypes` gains `SignalBroadcastExpired`, appended to `Subscribable` and to `SignalBroadcastTypes`. `LifecycleEventCatalog` gains the entry and a field list that is the shared one plus `expiredCount`, with a `status` description of its own, because the shared one lists three values. `SignalBroadcastEventDataDto` gains a nullable `ExpiredCount` for the typed test parser.
- **Subscriptions.** No subscription receives the type until an administrator adds it. A receiver that waits for a finish event of its own throws must add it before it sends `expiresAfter`; otherwise an expired broadcast is silent for it. The guides and the rollout say so.
- **Alerting.** `signalBroadcast.completedWithIssues` does not fire for a broadcast that expired with failed deliveries. A receiver that alerts on problems subscribes to both types.

### 8. Flowbit.Ui

Changes are in [Signals.razor](../../Flowbit/src/Flowbit.Ui/Components/Pages/Signals.razor), its display components under `Components/Shared/Signals/`, `SignalFormatting`, `StatusBadge`, and `WebhookFormatting`. The editor, the viewer, instance detail, and the portable authoring package do not change.

**Throw form.** Step 1 gains one row under the scope fields:

| Control | Detail |
| --- | --- |
| **Expires after (optional)** | A number box (`signal-expiry-amount`, whole numbers from 1) and a unit list (`signal-expiry-unit`: Seconds, Minutes, Hours, Days; Minutes first). Help text: "Deliveries that have not run by then are skipped, and their waits stay open for a later signal. Leave it empty for a signal that does not expire." |

- The page has no date picker for the expiry, so the rules for `datetime-local` inputs and browser time zones do not apply to the form.
- Any edit of the amount or the unit ends the review, as every other edit does. An identity change clears both.
- The amount box is uncontrolled and reports through `typedinput` and `typedcommit`, whose arguments carry the browser's `badInput`: a number box reports text it cannot read, such as `5-` or `10e`, as an empty value, which must not read as no expiry. Focus leaving the box without a change keeps the review.
- **Review signal** refuses an amount that is not a whole number of 1 or more, text the browser could not read included ("Enter the expiry as a whole number of 1 or more, or leave it empty."), and a length over the limit ("A signal can expire after at most 365 days."), before any request.
- The review gains a row **Expires**: "10 minutes after the throw", or "Never". `ThrowReview` holds the ISO text that the throw sends, built by a pure function in `SignalFormatting`.

**Open broadcast.**

- `SignalBroadcastSummary` shows a row **Expiry** with `LocalTime` when the broadcast has one, and "3 expired" under the **Skipped** count when any delivery expired.
- While the broadcast is delivering and its `expiresAt` is behind the UI's clock, the row adds: "The expiry has passed. A Worker ends the pending deliveries when it runs." The page passes the current time to the component, which stays display-only. The three-second poll keeps running, so the page shows **Expired** soon after a Worker settles it.
- **Retry failed** is hidden once `expiresAt` is behind the UI's clock, and the page says why: "This broadcast has expired, so its failed deliveries cannot be retried. Throw the signal again to reach the waits that are still open." `CanRetry` takes the time as a parameter. The API's `409` remains the rule; the page already shows it and refreshes.
- **Cancel pending deliveries** stays available while the broadcast is delivering.

**Lists.**

- The status label **Expired**, with the warning tone in `StatusBadge`. The status filter gains it through `SignalBroadcastStatuses.All`.
- **Recent broadcasts** adds "· 3 expired" to the progress line when any delivery expired.
- Deliveries: `ResultLabel` reads "The broadcast expired before delivery" for a wait and "The broadcast expired before the start" for a start.

**Webhooks page.** `WebhookFormatting` labels the type **Signal broadcast expired**. The checkbox, the delivery filter, the family note, and **All events** for fifteen types follow from `Subscribable` and `SignalBroadcastTypes`.

### 9. Documentation and examples

| Owning page | Update |
| --- | --- |
| [API reference](../api-guide.md#signals) | `expiresAfter` with its grammar, range, and three messages; that it is counted from the throw and includes delivery time; the idempotency rule; the status `expired` and the `broadcast_expired` row of the outcome table; that `skippedCount` includes `expiredCount`; the `409` of a retry after the expiry; that an unsettled expired broadcast does not count toward the limit; the schema tables and examples of `ThrowSignalRequest`, `SignalBroadcastSummaryDto`, and `SignalBroadcastDetailDto`; "fourteen" becomes "fifteen" in the webhook validation rule. |
| [Developer guide](../developer-guide.md#broadcast-signals) | A new part **Let a signal expire** with an HTTP example: when to use it, that the database's clock decides, that the wait stays open, and the advice to put a timer boundary on a catch that must not wait forever. It tells callers to size the expiry for the whole broadcast, with the delivery rate of one job, and to check `totalDeliveryCount` in the response. It shows how to find the deliveries that expired for one instance, with the `instanceId` filter of the deliveries route. [Which waits receive it](../developer-guide.md#which-waits-receive-it) no longer says that nothing expires. [Read progress](../developer-guide.md#read-progress) and [handle failed deliveries](../developer-guide.md#handle-failed-deliveries) name the status and the retry rule. The [event catalog](../developer-guide.md#event-catalog) and [event bodies](../developer-guide.md#event-bodies) gain the type, its extra field, and the two notes for receivers of [section 7](#7-lifecycle-webhook-event). |
| [BPMN support](../bpmn-support.md#signals) and [node reference](../node-reference.md#intermediatesignalcatchevent) | An expired signal never reaches the catch, which keeps waiting; use a timer boundary for a wait with a deadline. [Which waits a thrown signal reaches](../node-reference.md#which-waits-a-thrown-signal-reaches) names the expiry. A throw or end event cannot set one. |
| [Flowbit.Ui guide](../ui-guide.md#throw-signals) | The **Expires after** row, the review row, the **Expiry** row with its note, why **Retry failed** disappears, and the status. Under [lifecycle webhooks](../ui-guide.md#lifecycle-webhooks): the fourth type. |
| [Deployment](../deployment.md#signal-events) | "While the Worker is stopped" no longer says that nothing expires: it describes what an expiry does during an outage and after it, and that the database server's clock decides. A rollout part for this item with the order of [section 11](#11-delivery-sequence-rollout-and-risks), and a row for `AddSignalBroadcastExpiry` in the [upgrade table](../deployment.md#upgrade-and-compatibility-rules). `SignalExpirySweepSeconds` in the [Worker options](../deployment.md#worker-operation). The fourth type under [signal broadcast events](../deployment.md#signal-broadcast-events). The lost-lease paragraph names the expired outcome. Under [signals and actions](../deployment.md#signals-and-actions): the `broadcast_expired` value of the deliveries metric, and the troubleshooting row for a broadcast that stays `delivering`, which says today that nothing expires. |
| [Engine settings](../engine-settings.md#signalsmaxopenbroadcasts) | Both open-broadcast settings say that a broadcast past its expiry does not count. |
| [Database schema](../database-schema.md#signal_broadcasts) | The two columns, the replaced and the new check, the index, the result code under [`signal_deliveries`](../database-schema.md#signal_deliveries), and the replaced capture function under [lifecycle webhooks](../database-schema.md#lifecycle-webhooks). |
| [Runtime reference](../../Flowbit/README.md#signal-events) and [AGENTS.md](../../AGENTS.md) | The invariants of [section 1](#1-scope-and-invariants): the one clock, the check after the locks, the settle under the broadcast's row lock, the sweep, the derived status, and the two recount statements that must change together. In the runtime reference also the count of event types in its [verification](../../Flowbit/README.md#verification) section, which becomes fifteen. In AGENTS.md also the webhook invariant with the fourth type, the **Signals** page entry, the retention sentence, and the smoke matrix range, which becomes R1–R18. |
| [Root README](../../README.md) | Only if its signal bullet lists what a throw can set. Confirm when implementing. |
| [Signal examples](../../examples/signals/README.md) | Under Example 01, one optional step that throws with `expiresAfter` while the Worker is stopped and shows the wait still open, with PowerShell and Bash commands. No example JSON changes. |
| [Portable authoring guide](../../authoring/flowbit-authoring/references/authoring-guide.md) | One sentence beside its advice on messages: a signal can be thrown with an expiry, so a catch that must not wait forever needs a timer boundary. |
| Browser suite READMEs | R18 and the changed R17 row in the smoke matrix; S14 and S15 in the acceptance coverage. |
| This page, the [backlog](README.md), and the [parent](event-04-signal-events.md#later-releases) | Evidence and status. The later releases that are still wanted become items once this one is Done. |

Public examples stay HTTP-only. Validate changed relative links, anchors, and JSON examples, and compare the signal operations with their registrations, the DTOs, the tests, and the development OpenAPI document.

### 10. Tests and verification

PostgreSQL tests go in a new class `SignalExpiryTests` in the `PostgresApiCollection`, so that `SignalRuntimeTests` does not grow further. The database clock cannot be faked, so a helper moves a broadcast's `ExpiresAt` into the past with raw SQL, as the lease helpers backdate due times. Two cases use a real expiry of one second.

| Layer | Cases |
| --- | --- |
| Rules | `SignalExpiryRulesTests`: `PT1S`, `PT10M`, `P1D`, `P52W`, and `P365D` are accepted; blank text, `P1M`, `P1Y`, `10m`, `PT0S`, `PT0.5S`, and `P366D` give their problems, and lengths too long for a `TimeSpan` or a decimal, such as `P9999999999999999D`, are out of range; `PT60S` and `PT1M` parse to one length. `IsFixedDurationBeyondRange` is true only for such lengths, keeps the grammar's length limit, and leaves `TryParseFixedDuration`'s answers unchanged. |
| Throw | `expiresAt` is the database's time plus the length, between the database times read before and after the request. No `expiresAfter` leaves it null. Each of the three problems returns `400` with its message, `P9999999999999999D` among those out of range, and a number returns the binding error. A managed client sets an expiry and reads it back. A workflow's throw stores none. A throw without recipients is `completed` and stays so after its expiry. |
| Idempotency | The same key and length replays with `200`, also as `PT60S` after `PT1M`, and after the expiry has passed. Another length, and an expiry where the first request had none, return `409`. A recorded hash of a request without an expiry is unchanged. |
| Settle by the job | Three waits, the expiry in the past, and another transaction holding every recipient's instance row: the job finishes without waiting. Every delivery is `skipped` with `broadcast_expired` and no attempt, every subscription is active, and no instance has a new history row or variable. The broadcast is `expired` with `expiredCount` 3 and `skippedCount` 3. A run whose first delivery fails unexpectedly while the expiry passes settles on the second delivery's report and completes its job with one completed attempt, instead of retrying. |
| Check after the locks | A transaction holds the instance; the delivery starts and waits; the expiry is moved into the past; the instance is released. The delivery ends as expired and the instance is unchanged. The same with the family's exclusive lock for a start: no instance is created, and the delivery keeps its version and has no instance. |
| Partly delivered | One delivery runs, then the expiry passes: the broadcast is `expired` with one delivered and the rest expired. |
| Boundaries | An expired delivery to an interrupting and to a non-interrupting boundary leaves the host active and the boundary subscribed, with its occurrence count unchanged. |
| Settle races | A settle waits for a delivery row that another transaction holds and then leaves it as that transaction finished it. A settle that runs twice changes nothing the second time and leaves `updatedAt` alone. A settle before the expiry changes nothing. A cancel before a settle gives `cancelled`; a cancel after it returns `409`. |
| Sweep | A due broadcast whose job waits in an incident is settled by `ExpireNextDueAsync`, the incident stays open, and retrying it completes the job. A due broadcast whose row another transaction holds is skipped without waiting. Through the hosted Worker, with the delivery job's due time moved an hour ahead so that the dispatcher leaves it, a broadcast with a one-second expiry finishes as `expired`. `WorkerOptions.Validate` refuses 0 and 3,601 seconds. |
| Lost lease | After the expiry, the reconciliation leaves the job's deliveries expired, the broadcast `expired`, and the incident open. Before the expiry, they fail with `lease_exhausted`, as today. |
| Retry | After the expiry, a retry of a `completedWithIssues` broadcast returns `409` with its message and changes nothing. Before it, a retry reopens the broadcast with the same `expiresAt`; when the expiry then passes, the retried deliveries expire and the broadcast is `expired` with no failed delivery. A retry of an `expired` broadcast returns `409`. |
| Limits | With `Signals.MaxOpenBroadcasts` set to the number of delivering broadcasts thrown through the API, the next throw returns `409`. Once one of them is past its expiry, the next throw is accepted. The test restores the setting. |
| Status and checks | A broadcast with expired and failed deliveries is `expired`. `RefreshCountsAsync` and the lost-lease reconciliation derive the same status and counters from the same delivery rows. Raw SQL that sets `ExpiredCount` above `SkippedCount`, or the status `expired` without `ExpiresAt` or without an expired delivery, is refused. |
| Retention and deletion | The `signalBroadcasts` cleanup removes an old `expired` broadcast with its deliveries and keeps one that is past its expiry and not settled. Deleting a version with a pending delivery of such a broadcast is refused, and allowed after the settle. |
| No cost without signals | The existing command-count test still finds no `signal_` statement for a workflow without signals. |
| Telemetry | A settle adds the number of ended deliveries to the counter, tagged `skipped` and `broadcast_expired`. |
| Webhook capture | The golden body of `signalBroadcast.expired` has the thirteen data fields in catalog order, and the golden bodies of the three other types are unchanged. A settle by a job, a settle by the sweep, and the lost-lease reconciliation each store one event. A subscription with the three earlier types stores nothing for an expired broadcast. `EveryFinishedBroadcastStatusHasItsEventType`, the catalog test (renamed for fifteen types), `ParseTyped`, and the schema tests pass. |
| Migration | Up, then down, then up. Down is refused while a broadcast has an `ExpiresAt` or the status `expired`. `MigrationDowngradeCannotRemovePermanentPruningMarkers` still passes; it deletes signal broadcasts before it migrates down. |
| API | The OpenAPI document has `expiresAfter`, `expiresAt`, and `expiredCount`. The list accepts `status=expired`. Subscriptions and the delivery filters accept the new type. |
| Flowbit.Ui | `SignalFormattingTests`: the status and result labels, the ISO text for each unit, the review text, `CanRetry` before and after the expiry, and the progress line. `SignalsPageTests`: an edit of the expiry ends the review, and leaving the box unchanged keeps it; the throw sends `expiresAfter`; an invalid amount, and text the browser could not read, is refused before any request; the amount box has no `value` and uses the typed events; the **Expiry** row, its note, and the hidden retry with its reason; an identity change clears the fields. `WebhooksUiTests`: the label, and **All events** for fifteen types. |

Browser smoke suite, which has no Worker:

- **R18, Signals page expiry**, at 1440×900, 1024×768, and 390×844. An administrator types a signal name, chooses **All workflows**, types `10` into **Expires after**, and chooses **Minutes**. The review shows **Expires** "10 minutes after the throw". Changing the unit returns the page to **Review signal**. An amount of `0` is refused, and so is `5-`, which the box reports as empty with `badInput`. After **Throw signal** the open broadcast shows the **Expiry** row with a time in the browser's zone, and the status filter lists **Expired**. The page does not overflow horizontally.
- **R17, Webhooks form**, changed. Its list of signal broadcast types, in `WebhooksSmokeTests` and in its README row, gains **Signal broadcast expired**, so the form and the delivery filter are checked for four types at the same three sizes.

Worker acceptance suite:

- **S14, expiry during an outage**, at 1440×900 and 390×844. [Example 01](../../examples/signals/01-signal-catch-policy-change.json) under its own family, with waiting instances and the fixture's Worker stopped. A throw from the page with an expiry of five seconds shows **Delivering**, and after the expiry the note that a Worker will end the deliveries. A second throw is accepted although the scenario has set `Signals.MaxOpenBroadcasts` to the number of broadcasts thrown through the API that were delivering after the first throw, as S9 sets its limit relative to the stack; it restores the setting at its end. After the Worker starts, the broadcast reads **Expired**, every delivery reads "The broadcast expired before delivery", and instance detail still lists the wait. A throw without an expiry then reaches every instance.
- **S15, no retry after the expiry**, at 1440×900. A payload that fails the mapping, thrown with an expiry of ten seconds while the Worker runs: the broadcast finishes with issues and offers **Retry failed**. After the expiry and a refresh the button is gone and the reason is shown, and the retry route returns `409`.

Then follow [mandatory real-browser verification](../../AGENTS.md#mandatory-real-browser-ui-verification) by hand on a local stack with published API, UI, and Worker hosts, the webhook key ring, capture on, and a loopback receiver that verifies each signature:

1. Subscribe the receiver to the four signal broadcast types.
2. Stop the Worker. Throw the signal of Example 01 from **Signals** with an expiry of one minute, using real typing and clicks, to waiting instances. Watch the note appear after a minute, start the Worker, and see **Expired**, the counts, and the receiver's `signalBroadcast.expired` with `expiredCount`.
3. With the Worker running, throw with an expiry of one hour: the broadcast completes as before, and the receiver gets `signalBroadcast.completed` with the body it had before this item.
4. Throw with a failing payload and a short expiry, wait, and confirm that **Retry failed** is gone and that the route returns `409`.
5. Use the keyboard through the form (Tab to the amount, type, Tab to the unit, arrow keys), repeat the layout check at the three sizes, and read the console.

Keep screenshots of the form with the expiry, the open broadcast with the note, and the expired broadcast with its deliveries.

Regression commands, run from the repository root with the same arguments in PowerShell 7 and Bash:

```text
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet
dotnet publish Flowbit/src/Flowbit.Api/Flowbit.Api.csproj -c Release -o artifacts/browser/hosts/api /p:UseAppHost=false
dotnet publish Flowbit/src/Flowbit.Ui/Flowbit.Ui.csproj -c Release -o artifacts/browser/hosts/ui /p:UseAppHost=false
dotnet build Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore
git diff --check
```

The suite READMEs cover Docker, the Chromium install, and the acceptance suite's own commands. Full solution runs have had order-dependent failures at the base commit, so compare failures against a base run before attributing one to this change. Rerun the signal acceptance scenarios S1 to S13 once on the final build, because every one of them finishes broadcasts through the changed recount. Run the signal test classes once with `FLOWBIT_TEST_LIFECYCLE_CAPTURE=all`, as [EVENT-04D](event-04d-webhook-events-for-signal-broadcasts.md#9-tests-and-verification) did, and record the event counts.

Before setting the status to Done, walk this table case by case against the tests that exist.

### 11. Delivery sequence, rollout, and risks

1. **Contract.** `SignalExpiryRules`, the request field, the status, the result code, the DTO members, and the hash line. Exit with the rules tests green and every existing signal test unchanged.
2. **Persistence.** The migration with its checks, index, and capture function, the entity, and the repository methods: the database time, the check, the single-delivery expiry, the settle statement, the claim, the count, and both recounts. Exit with the schema and migration tests green.
3. **Runtime.** The throw, the check in both delivery paths, `SignalBroadcastExpirer`, the job processor, the retry rule, and the lost-lease statement. Exit with `SignalExpiryTests` green apart from the sweep cases.
4. **Worker.** `SignalBroadcastExpiryService` and its option. Exit with the sweep cases green.
5. **Webhook event.** The type, the catalog entry, the DTO, and the label. Exit with the capture and catalog tests green.
6. **Flowbit.Ui.** The form, the review, the open broadcast, and the lists. Exit with the component tests green.
7. **Documentation.** The owning pages of [section 9](#9-documentation-and-examples).
8. **Verification.** R18, S14, S15, the reruns, and the manual checks. Record the evidence under [Current state](#current-state), then set the status to Done here, in the [backlog](README.md#event-04-follow-up-subtasks), and in the [parent](event-04-signal-events.md#later-releases).

Rollout:

1. Apply `AddSignalBroadcastExpiry`. It holds its lock on `signal_broadcasts` for the catalog changes, two scans of that table, and the function. Nothing behaves differently yet.
2. Upgrade every API, Worker, and UI replica. Mixed replicas are unsupported: an API replica without this release ignores `expiresAfter`, and a Worker without it delivers after the expiry and never writes the status `expired`.
3. Add `signalBroadcast.expired` to the webhook subscriptions that should hear of it.
4. Only then send `expiresAfter`.
5. To roll the binaries back, first stop every caller that sends `expiresAfter`, and let the broadcasts that have an expiry finish, or cancel them. The schema can stay. The down migration refuses while a broadcast with an `ExpiresAt` or the status `expired` remains.

| Risk | Guard |
| --- | --- |
| A signal acts after its expiry because two clocks disagree. | One clock: the database computes `expiresAt` and makes every comparison. Tests assert `expiresAt` against the database's time, not the host's. |
| A delivery waits for a lock, the expiry passes, and the delivery acts. | The check is a statement issued after the locks, with `clock_timestamp()`. Two race tests hold a lock across the expiry. |
| A delivery that passed its check commits after the expiry, and someone reads that as a defect. | The guides state the rule: a delivery that has started finishes. After its check a delivery waits for no other instance and makes no external call. |
| Settling locks thousands of instances, or takes as long as delivering. | One `UPDATE` on delivery rows under the broadcast's row lock, and no runtime lock. A test settles while every recipient's instance is held. A cancel of 100,000 rows, the same statement shape, took under two seconds. |
| One of the two recount statements is changed and the other is not, so a lost lease writes another status than a job would. | AGENTS.md names both statements. The lost-lease tests cover both sides of the expiry, and a test compares the status each statement derives from the same rows. |
| An expired catch waits forever, and the author did not expect it. | The page's help text, the guides, and the authoring guide say that the wait stays open and recommend a timer boundary. Instance detail still lists the wait. |
| A receiver waits for `signalBroadcast.completed` and never hears of an expired broadcast. | The catalog description, the developer guide, and the rollout tell receivers to add the fourth type before they send `expiresAfter`. Only broadcasts with an expiry can have the status. |
| An alert on `signalBroadcast.completedWithIssues` misses failures in a broadcast that expired. | The guides recommend both types for alerting, and the `expired` body carries `failedCount`. |
| During an outage, a caller that throws with short expiries is no longer stopped by the limit, and unsettled broadcasts pile up. | Each costs one settle statement when a Worker returns, through its own delivery job. The limit still bounds the broadcasts that can act, the backlog gauges show the pending rows, and the shortest expiry is one second. The deployment guide states it. |
| The sweep adds load to every deployment. | One read of the clock and one range read of a partial index that is empty unless broadcasts with an expiry are delivering, every five seconds per Worker replica. The claim compares with a value, not with a volatile function, so the read is an index condition. |
| A short expiry on a large broadcast ends most deliveries although the Worker is healthy, and the status `expired` reads like a fault. | The guides give the delivery rate of one job and tell callers to size the expiry for the whole broadcast. The response's `totalDeliveryCount` shows the size at once, and `expiredCount` shows what was dropped. Faster delivery is [EVENT-04G](event-04g-parallel-signal-delivery.md). |
| The database server's clock is stepped, or a failover moves to a server whose clock differs. | An expiry is as exact as that clock. A settle is final, so a step can only move the moment for deliveries that have not ended. The deployment guide says that the database server's clock decides. |
| An operator looks at an instance that still waits and cannot see that a signal for it expired. | The developer guide shows the lookup through the broadcast's deliveries, filtered by instance. Showing it on instance detail is a [later release](#later-releases). |
| A caller that switches on `status` meets `expired` and fails. | The status appears only for broadcasts thrown with `expiresAfter`, which no caller sends before this release. The API reference lists it. |
| A stored idempotency key stops replaying. | The hash of a request without an expiry is unchanged, and a test pins a recorded hash. |
| The page hides **Retry failed** a few seconds early or late, because the UI host's clock differs from the database's. | The page's clock only shows and hides; the API decides. A `409` is shown with its reason, and the page refreshes. |
| The retention downgrade test fails on rows this item adds. | It deletes signal broadcasts before it migrates down; the migration test of this item checks the guard on its own data. |
