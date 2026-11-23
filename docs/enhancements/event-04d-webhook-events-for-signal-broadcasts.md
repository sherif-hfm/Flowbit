# EVENT-04D: Webhook events for signal broadcasts

[Parent EVENT-04](event-04-signal-events.md) · [Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Done |
| Category | Integration |
| Effort | M |
| Priority | Not ranked; fourth of eleven in the [EVENT-04 follow-up order](README.md#event-04-follow-up-subtasks) |
| Parent | [EVENT-04](event-04-signal-events.md) |
| Depends on | [EVENT-04](event-04-signal-events.md) (Done), [INT-01](int-01-outbound-lifecycle-webhooks.md) (Done) |
| Related | [EVENT-04A](event-04a-signal-scope-by-business-key-or-instance.md), [EVENT-04B](event-04b-signal-throw-and-end-events.md), [EVENT-04C](event-04c-signal-start-events.md), [EVENT-04E](event-04e-signal-broadcast-expiry.md), [INT-02](int-02-webhook-verification-follow-ups.md) |

## Summary

Three lifecycle webhook event types for signal broadcasts: `signalBroadcast.completed`, `signalBroadcast.completedWithIssues`, and `signalBroadcast.cancelled`. A receiver learns that a broadcast finished, how it finished, and with which counts, without polling.

## Why it adds value

- An integration that throws a signal wants to know when delivery ended and whether a delivery failed. Today it polls `GET /api/signal-broadcasts/{id}`.
- Operators can send "completed with issues" to their alerting through the webhook subscriptions they already have, by subscribing to that one type.
- Once workflows throw signals themselves ([EVENT-04B](event-04b-signal-throw-and-end-events.md), Done), no caller is waiting for the result, so an event is the only notice.

## First release acceptance criteria

These criteria are revised by this plan on 2026-11-23. The revision names the three types, says exactly which status changes raise an event, states how a subscription limited to a workflow family matches, and replaces "never the thrower" with "never the thrower's identity": the body names the kind of thrower and, for a workflow, the throwing instance.

- [x] The event catalog adds three subscribable types, `signalBroadcast.completed`, `signalBroadcast.completedWithIssues`, and `signalBroadcast.cancelled`, with one shared field list.
- [x] One event is raised for each committed change of a broadcast's status to `completed`, `completedWithIssues`, or `cancelled`: a delivery job's last recount, a throw that reaches nothing, a cancel, a retry that leaves nothing pending, and the reconciliation of a delivery job that lost its last lease. A recount that changes only counters, a retry that reopens the broadcast, a refused throw, and retention raise nothing. A retried broadcast that finishes again raises a new event.
- [x] Capture runs in deferred constraint triggers on `signal_broadcasts`, gated by `Webhooks.CaptureEnabled` and the transaction-local `flowbit.lifecycle_events` setting. It takes no lock, reads no payload, and writes nothing when no active subscription matches. No application code emits the event.
- [x] The body holds the broadcast's ID, its signal name, the kind of its scope, the kind of its thrower and, for a workflow, the throwing instance's ID, its status, and its counts as the transaction commits them. The envelope names the scope's family and instance. The body never holds the payload, a business key, the thrower's name or client ID, roles, claims, a cancellation reason, or a result description, and every string in it is bounded by a column limit.
- [x] A subscription limited to a workflow family receives the broadcasts whose scope names that family. A broadcast to all workflows reaches only subscriptions without a family. The guides and the **Webhooks** page say so.
- [x] Existing event types, bodies, and subscriptions are unchanged, and no existing subscription receives a new type until an administrator adds it. `LifecycleEventCatalog`, the event DTO, and the capture function change together, and `LifecycleEventCaptureTests` and `DatabaseSchemaTests` cover the new rules. As [section 9](#9-tests-and-verification) decided, the new capture cases are in `SignalBroadcastLifecycleEventTests`, which shares `LifecycleEventCaptureTests`' support.
- [x] The **Webhooks** page offers the new types, verified in a real browser at three viewport sizes, and a receiver verifies a delivered event of each type on a local stack with a Worker.
- [x] The developer guide's event catalog, the API reference, the Flowbit.Ui guide, the database schema reference, the deployment guide, the runtime reference, AGENTS.md, and the root README are updated.

## Later releases

- An event when a broadcast is thrown, or reopened by a retry.
- An event for each failed delivery. It would carry the recipient's family, so a subscription limited to a family would also hear about failures of a broadcast to all workflows.
- Subscriptions that filter broadcasts by signal name.
- Matching a broadcast to all workflows against family-limited subscriptions through the families it reached.
- The throwing node and the recipient counts per family in the body.
- A link from a delivery's detail on the **Webhooks** page to its broadcast on the **Signals** page.
- A way to tell from an `incident.opened` event that its job delivers a signal broadcast, and which one.

## Current state

Done on 2026-11-23, on top of `2370ee4`: implemented as planned, with the deviations below, and checked case by case against the [test table](#9-tests-and-verification) in the [implementation review](#implementation-review-of-2026-11-23).

### What changed

- **Catalog.** `LifecycleEventTypes` appends the three types to `Subscribable` (now fourteen) and adds `SignalBroadcastTypes`. `LifecycleEventCatalog` has one shared field list for them, descriptions that end with the family rule, and envelope descriptions that give `workflowKey`, `workflowDefinitionId`, `workflowVersion`, and `instanceId` their meaning for a broadcast. `SignalBroadcastEventDataDto` has the twelve fields in body order.
- **Capture.** The migration `AddSignalBroadcastLifecycleEvents` (SQL only; its designer equals the unchanged model snapshot) creates `capture_signal_broadcast_lifecycle()` and the triggers `TR_signal_broadcasts_lifecycle_insert` and `TR_signal_broadcasts_lifecycle_status` exactly as [section 5](#5-capture-migration-function-and-triggers) describes. No C# status writer changed.
- **Flowbit.Ui.** `WebhookFormatting` labels the three types and owns the pure condition and text of the family note; `Webhooks.razor` shows the note under **Workflow family**, linked with `aria-describedby`, and its page subtitle now names signal broadcast events. No CSS was needed: the long type name wraps inside its label at 390 pixels.
- **Tests.** `WithCaptureAsync` moved into `WebhookTestDatabase`; `LifecycleEventBodies.ParseTyped` knows the three types. The fanout-and-dispatch case is `WebhookDispatchTests.HostedWorkerDeliversASignalBroadcastEventWithItsStoredBytes`, because that class owns the loopback receiver and the Worker host; every other PostgreSQL case is in `SignalBroadcastLifecycleEventTests` (22 cases).

Deviations from the plan:

- The **Webhooks** page subtitle now names signal broadcast events; it listed only instance, task, delegation, and incident events.
- `LifecycleEventTypes.SignalBroadcastTypes` names the three types for the form's note and the tests.
- `EveryFinishedBroadcastStatusHasItsEventType` fails when a status is added to `SignalBroadcastStatuses` without an event type. The plan relied on AGENTS.md and the [EVENT-04E](event-04e-signal-broadcast-expiry.md) page alone for that risk.
- The lost-lease case proves "one transaction" by comparing the `xmin` of the two stored events.
- The delivery-detail screenshot was taken by hand in the browser pane, not by a scenario; it is kept with the form screenshots under `artifacts/manual/event-04d/`, which Git ignores like every artifact.

### Verification of 2026-11-23

Automated, from the repository root:

| Command or run | Result |
| --- | --- |
| `dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet` | 6,210 passed, 0 failed (19 m 53 s). |
| `SignalBroadcastLifecycleEventTests`, `WebhookApiTests`, `WebhookApiContractTests`, `WebhookDispatchTests`, `LifecycleEventCaptureTests`, `DatabaseSchemaTests`, `WebhooksUiTests` | First run: 175 of 176 passed; the failure was a new UI assertion that compared against HTML-encoded `·`. After that test was corrected, the 45 `WebhooksUiTests` passed, and all of these classes passed in the full run above. |
| Producer sweep: the signal classes (`FullyQualifiedName~Flowbit.Tests.Signal`) with `FLOWBIT_TEST_LIFECYCLE_CAPTURE=all` | 555 passed, 0 failed. The report counted 236 `signalBroadcast.completed`, 33 `signalBroadcast.completedWithIssues`, and 12 `signalBroadcast.cancelled` events beside the existing types. |
| Smoke R17 and S1 (`WebhooksSmokeTests`, `EngineSettingsSmokeTests`), Chromium | 6 passed: R17 at 1440×900, 1024×768, and 390×844. Its screenshots `webhooks-signal-types.png` are under `artifacts/browser/runs/20261123-165649-60c7d401/`, with copies in `artifacts/manual/event-04d/`. |
| Full smoke suite (`dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore`, after publishing the API and UI hosts) | 148 passed, 0 failed (14 m 10 s). |
| Signal acceptance S1 to S13 (`SignalAcceptanceTests`) with a Worker | 18 passed, 0 failed (5 m 36 s). |
| Changed relative links, anchors, and JSON examples | All resolve; the new JSON example parses. `git diff --check` is clean. |

Manual, in the desktop app's built-in Chromium browser, on a disposable local stack: the published API (`http://127.0.0.1:25017`), UI (`http://127.0.0.1:25152`), and Worker with a generated webhook key-ring certificate, `Webhooks:DeliveryEnabled=true`, `Webhooks:Network:AllowLoopbackHttp=true`, a fresh database on the Compose PostgreSQL, and a loopback Node receiver that verified every Standard Webhooks signature, the timestamp, and `eventId` against `webhook-id`. The examples were published over HTTP.

1. **Webhooks** → **Add subscription** with real clicks and typing: the three types with labels and type names, no family. Typing `orders` into **Workflow family** showed the note with `aria-describedby="webhook-workflow-key-help webhook-workflow-key-signal-note"`; clearing it with Ctrl+A and Delete removed both. **Create paused and reveal secret**, **I saved it**, **Send test event** (verified at the receiver, HTTP 204), **Resume**.
2. With capture on, [Example 01](../../examples/signals/01-signal-catch-policy-change.json)'s signal thrown from **Signals** to three waiting claims: the receiver verified `signalBroadcast.completed` with 3 recipients and 3 delivered, as the page showed. The body held no user name.
3. The payload `{ "version": "seven" }` to a fourth claim: `signalBroadcast.completedWithIssues` with `failedCount` 1. **Retry 1 failed** without a correction: a second `completedWithIssues` event with a new `eventId` for the same broadcast.
4. Worker stopped, a throw to two claims, **Cancel pending deliveries**: the event was stored at the cancel with fanout pending; after the Worker restarted, the receiver verified `signalBroadcast.cancelled` with `cancelledCount` 2.
5. [Example 03](../../examples/signals/03-signal-throw-order-cancellation.json), **Cancel the order** with the reason `REASON-MARKER customer request`: `signalBroadcast.completed` with `thrownByKind` `instance`, `thrownByInstanceId` 6, scope `instance`, envelope `instanceId` 6, and neither the reason nor the order number in the body.
6. A second subscription limited to `example-signal-catch-policy-change` (the note appeared while typing the family): a throw to all workflows reached only the subscription without a family; a throw to the family reached both.
7. **Deliveries** filtered by each new type returned 2 `completedWithIssues`, 1 `cancelled`, and 5 `completed`. The Example 03 delivery's detail showed the stored body in catalog order and an **Instance #6** link. A keyboard pass (Tab to **Signal broadcast completed with issues**, Space, Tab to **Workflow family**, typing) kept focus while the note appeared. At 1440×900, 1024×768, and 390×844 the page had no horizontal overflow and the three labels stayed inside the viewport. The console showed only Blazor connection information.

The receiver verified all 9 requests (1 `webhook.test`, 5 `completed`, 2 `completedWithIssues`, 1 `cancelled`). The hosts logged no error apart from EF probing the migrations table of the fresh database. The stack and its database were removed afterwards. `artifacts/manual/event-04d/` keeps the form with the family note (`webhooks-form-family-note.jpg`) and the Example 03 delivery's detail (`webhook-delivery-detail-signal-broadcast.jpg`).

### Implementation review of 2026-11-23

A second pass compared the code, the tests, and the documentation with this plan, row by row for the [test table](#9-tests-and-verification) and the [documentation table](#8-documentation). It found no defect in the capture function, the triggers, the catalog, or the page, and these gaps, now closed:

- **Tests the plan named but the first pass lacked.** The risk table promises a test in which a subscription with the eleven earlier types stores nothing; `Gates_StoreNothingAndASubscriptionEditAffectsOnlyLaterFinishes` now has it. The two bulk delivery routes accept the new types in `eventType` ([section 6](#6-api-contracts-and-authorization)), which `SignalBroadcastTypesAreAcceptedBySubscriptionsAndTheDeliveryFilter` now checks beside the list filter. The lost-lease case asserted both events but not that one transaction stored them.
- **A test helper leased more than it needed.** The lost-lease helper asked the job repository for up to ten control jobs of other suites and left them leased. It now asks for one, as `LifecycleEventCaptureTests` does, and repeats until its own job waits in its incident.
- **AGENTS.md** still introduced lifecycle webhooks as instance, task, delegation, and incident transitions, and its **Webhooks** page entry did not mention the family note.
- **This page** reported the first targeted run as 176 passed; it was 175 of 176, with the one failure in a new test.

Checked and left unchanged:

- The [deployment rollout](../deployment.md#rollout) step that says `CREATE TRIGGER` blocks writes to "the four source tables" describes `AddOutboundLifecycleWebhooks`, which still creates triggers on four tables.
- [Engine settings](../engine-settings.md#webhookscaptureenabled) names no event source, as the plan expected.
- No documentation image shows the **Webhooks** form. Besides the pages already updated, only the [INT-01](int-01-outbound-lifecycle-webhooks.md) and [UI-13](ui-13-notification-center.md) plans list event types, as the scope of their own items. The authoring package copies the guides at build time and excludes this folder.
- Hand-written migrations have no byte-order mark, like the new one.

After these changes, the six affected classes ran again: 175 of 175 passed in 1 m 3 s (`SignalBroadcastLifecycleEventTests`, `WebhookApiTests`, `WebhooksUiTests`, `DatabaseSchemaTests`, `LifecycleEventCaptureTests`, `WebhookDispatchTests`). They change tests and documentation only, so the browser suites, the acceptance scenarios, and the manual check were not repeated.

Not covered by this item, as planned: a saved subscription and its deliveries in a repeatable browser scenario stay with [INT-02](int-02-webhook-verification-follow-ups.md), and capture cost under load was not measured.

### Plan review of 2026-11-23

A review of the plan against the code on the day it was written found these gaps. Each is corrected in the sections below.

- **A status writer was missing.** The plan listed five places that write a broadcast's status. A sixth is raw SQL in the job repository: when a delivery job loses its last lease, the reconciliation fails that job's pending deliveries and recounts the broadcast in the transaction that opens the `lease_exhausted` incident. The triggers cover it without a change, but the rule table, the facts, and the tests did not name it.
- **A broadcast can stay `delivering`.** A delivery job that stops with any other incident leaves its broadcast open, and no broadcast event announces that. The plan now states the limit and tells receivers to subscribe to `incident.opened` as well.
- **The incident fact was wrong.** A delivery that fails unexpectedly on every attempt ends as `failed` and opens no incident.
- **The body bound was wrong.** "Under 1 KiB" ignored a family key and a signal name whose characters escape to six bytes. The bound is 3 KiB.
- **The bound test missed a column.** The envelope copies `signal_broadcasts."WorkflowKey"`, so that column belongs in the test beside the signal name.
- **Tests were missing** for the lease reconciliation, an idempotent replay, and the rule that the four counters add up to the total.
- **Recovery from a capture defect was not described.** A failed recount leaves a broadcast `delivering`, where it counts against `Signals.MaxOpenBroadcasts`.
- **Documentation.** AGENTS.md names the smoke matrix as R1–R16, and the statement about [INT-02](int-02-webhook-verification-follow-ups.md) read as if nothing stayed with it.

### Facts the plan rests on

Verified in the code on 2026-11-23, before the change.

- **Types and catalog.** `LifecycleEventTypes.Subscribable` in [WebhookDtos.cs](../../Flowbit/src/Flowbit.Shared/Dtos/WebhookDtos.cs) lists eleven types for instances, tasks, delegations, and incidents, and none for signals. `LifecycleEventCatalog` in [WebhookModels.cs](../../Flowbit/src/Flowbit.Service/Models/WebhookModels.cs) holds each type's description and data fields in body order, and [LifecycleEventDtos.cs](../../Flowbit/src/Flowbit.Shared/Dtos/LifecycleEventDtos.cs) holds the typed bodies that the tests parse.
- **Capture.** Capture exists only in the deferred constraint triggers on `workflow_instances`, `user_tasks`, `workflow_incidents`, and `user_delegations`, created by `AddOutboundLifecycleWebhooks`. Each function first calls `flowbit.lifecycle_capture_wanted(types)`, which reads the transaction-local setting, the `Webhooks.CaptureEnabled` row, and whether an active subscription lists one of the types. It then calls `flowbit.lifecycle_event_targets(type, workflow_key)` and `flowbit.record_lifecycle_event(...)`, which builds the envelope, inserts the row, and calls `pg_notify`.
- **Matching.** `lifecycle_event_targets` matches a revision when its `WorkflowKey` is null or equals the event's key. An event without a key therefore matches only revisions without a key. Until now only `webhook.test` has no key.
- **Column limits.** `lifecycle_events."Type"` and `webhook_deliveries."EventType"` hold 64 characters; the longest new name has 35. `signal_broadcasts."SignalName"` holds 100 characters, `"Status"` 32, and `"ThrownByKind"` 16, and checks limit the last two to fixed values.
- **Status writes.** In [SignalBroadcastRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/SignalBroadcastRepository.cs), `AddAsync` always inserts a broadcast as `delivering`. `SetRecipientsAsync` then sets `completed` in the same transaction when the throw froze no recipient. `RefreshCountsAsync` writes the counters and the status in one `UPDATE` that always names `"Status"`, and only when a counter or the status changes. `MarkCancelledAsync` sets `cancelled`, and `ReopenAsync` sets `delivering`.
- **Cancel writes the status before the counts.** `CancelAsync` in [SignalBroadcastService.cs](../../Flowbit/src/Flowbit.Service/Services/SignalBroadcastService.cs) cancels the pending deliveries, calls `MarkCancelledAsync`, and only then recounts. The row version that changes the status still has the old `CancelledCount`.
- **A retry can finish a broadcast without delivering.** `RetryFailedAsync` reopens the broadcast only when a delivery became pending. A failed start whose family no longer listens becomes `skipped`, so the recount can move `completedWithIssues` straight to `completed`, and `CompletedAt` keeps its first value.
- **A refused throw leaves no row.** At the recipient limit, [SignalBroadcastWriter.cs](../../Flowbit/src/Flowbit.Service/Services/SignalBroadcastWriter.cs) deletes the broadcast in the transaction that inserted it, before any status change.
- **Recounts.** [SignalBroadcastJobProcessor.cs](../../Flowbit/src/Flowbit.Service/Services/SignalBroadcastJobProcessor.cs) recounts after every page of 100 deliveries and once at the end, each time in a transaction that holds the broadcast's row lock. One broadcast is delivered by one job per workflow definition. A job that runs again with nothing pending still recounts once.
- **A lost lease is reconciled in raw SQL.** `ReconcileExhaustedSignalBroadcastsAsync` in [WorkflowJobRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/WorkflowJobRepository.cs) runs when a delivery job's final lease expires. In the transaction that opens the `lease_exhausted` incident it locks the broadcast, fails the pending deliveries of that job's definition, and writes the counters and the status in one `UPDATE`. The status stays `delivering` while another definition still has pending deliveries. No C# status write is involved.
- **Other failures.** A delivery that fails unexpectedly stays pending while its job retries, and the final attempt marks it `failed` and completes the job without an incident. A job opens an incident and leaves its broadcast `delivering` when its payload names no broadcast, or when the job itself fails on its last attempt, for example in a recount. Retrying that incident runs the job again. `FailPendingDeliveriesAsync` on the broadcast repository has no caller.
- **Retention.** The `signalBroadcasts` category deletes finished broadcasts. `lifecycle_events` holds no foreign key to runtime rows, so an event outlives its broadcast.
- **Webhooks page.** [Webhooks.razor](../../Flowbit/src/Flowbit.Ui/Components/Pages/Webhooks.razor) builds the **Events** checkboxes and the delivery filter from `LifecycleEventTypes.Subscribable`, and shows a subscription as **All events** when it lists as many types as that list has. `EventTypeLabel` in [WebhookFormatting.cs](../../Flowbit/src/Flowbit.Ui/Components/Shared/WebhookFormatting.cs) falls back to the type name for a type it does not know.
- **Management.** `ValidateConfigurationAsync` in [WebhookManagementService.cs](../../Flowbit/src/Flowbit.Service/Services/WebhookManagementService.cs) refuses a type that is not in `Subscribable` and stores the accepted types in catalog order. A subscription's types change only through an edit.
- **Worker.** Fanout and dispatch copy the event's type, family, and instance and send the stored bytes. Neither branches on the type, except for the test flag.
- **Delivery detail.** `WebhookDeliveryDetail.razor` shows an **Instance** link when the delivery has an instance, and nothing else that depends on the type.
- **Tests that pin the list.** `EventCatalogListsTheElevenSubscribableTypesAndTheCaptureSwitch` in `WebhookApiTests`, and `LifecycleEventBodies.ParseTyped` in [WebhookTestSupport.cs](../../Flowbit/tests/Flowbit.Tests/WebhookTestSupport.cs), which throws for a type it does not know. In `DatabaseSchemaTests`, `LifecycleCaptureTriggersAreDeferredConstraintTriggersOnInvokerFunctions` expects an exact list of the triggers whose function is named `capture_…_lifecycle`, so it fails until the new triggers are listed, and `LifecycleEventBodyStringsAreBoundedByTheirSourceColumns` lists the columns that bodies copy. No test reads the product documentation for webhooks.
- **Capture helper.** `WithCaptureAsync`, which turns capture on for one test and restores it, is private to `LifecycleEventCaptureTests`.
- **Producer sweep.** With `FLOWBIT_TEST_LIFECYCLE_CAPTURE` set, `PostgresApiFixture` turns capture on with one subscription to every subscribable type before the first test, so every test then runs with the triggers writing events.
- **Browser suites.** The smoke suite has scenarios up to R16 and covers the **Webhooks** page only for time-zone labels and the capture setting. Neither browser suite configures the webhook key ring, so neither can create a subscription. The signal acceptance scenarios go up to S13.

## Builds on

- The capture helpers `lifecycle_capture_wanted`, `lifecycle_event_targets`, and `record_lifecycle_event`, which need no change, and `LifecycleEventCatalog`, which lists each body's fields in order.
- The webhook delivery queue, signing, retries, recovery, and retention, which need no change for a new type.
- The broadcast's status and counters, which already commit in one transaction under the broadcast's row lock.
- `LifecycleEventCaptureTests` with its raw-SQL cases, `WebhookTestDatabase`, and the producer sweep.

## Open questions

No question remains open. The plan decides the three original questions and the ones it raised. The user confirmed five of the decisions on 2026-11-23, after the plan review: how a family-limited subscription matches, the scope's instance in the envelope, the type names, the event for a broadcast that reached nothing, and the throwing instance in the body.

| Question | Decision |
| --- | --- |
| One "finished" event that carries the status, or one type per outcome? | One type per outcome. A subscription filters by type and by family only, so one type per outcome is the only way to send "completed with issues" alone to an alerting receiver. Instance events follow the same pattern. The body still carries `status`. |
| What are the types called? | `signalBroadcast.completed`, `signalBroadcast.completedWithIssues`, and `signalBroadcast.cancelled`. The prefix names the API resource, the suffix is the stored status, and the later-release events fit beside them as `signalBroadcast.thrown` and `signalDelivery.failed`. |
| A broadcast to all workflows belongs to no family. How does a family-limited subscription match it? | It does not. The event's family is the family the scope names, and a broadcast to all workflows has none, so it reaches only subscriptions without a family. This is how the existing match already treats a missing family, and it needs no new matching code. Matching through the families a broadcast reached is recorded under [Later releases](#later-releases). |
| Does the body name the scope when the scope is a business key? | It names the kind of scope, `businessKey`, and never the key. The envelope names the family. A receiver that needs the key reads the broadcast through the API with its own credentials. |
| Does the body name the thrower? | It names the kind: `user`, `client`, or `instance`. For `instance` it also carries the throwing instance's ID, a runtime ID like those other bodies carry, because a workflow's throw has no caller who knows the broadcast. It never carries a user name or a client ID. This revises the criterion "never the thrower". |
| What do the envelope's `workflowKey` and `instanceId` mean for a broadcast? | They are the broadcast's scope. Both are null for all workflows. A family or business-key scope has the family. An instance scope has the instance and its family. `workflowDefinitionId` and `workflowVersion` are always null. The delivery filters for family and instance then find broadcast events by their scope. |
| Does a broadcast that reached nothing raise an event? | Yes, `signalBroadcast.completed` with `totalDeliveryCount` 0, in the transaction of the throw. For a workflow's throw this is the only notice that the signal reached no one. |
| Does a retry raise events? | Reopening raises nothing. When the retried broadcast finishes again, it raises a new event with a new ID, as an instance that is reactivated and ends again does. A retry that leaves nothing pending changes `completedWithIssues` to `completed` and raises `signalBroadcast.completed`. |
| Where do the counts come from? | From the row as the transaction commits it, not from the row version that changed the status. A cancel writes the status before it recounts, so the changing version has stale counts. |
| Which time is the event's `timestamp`? | The `UpdatedAt` of the row version that changed the status. `CompletedAt` keeps its first value when a retry corrects `completedWithIssues` to `completed`. |
| Is an insert trigger needed, although a broadcast is always inserted as `delivering`? | Yes. Capture detects transitions where they commit, for raw SQL and future writers too. A row inserted as finished raises its event; a row inserted as `delivering` queues nothing. |
| What announces a broadcast that never finishes? | Nothing from this item. A broadcast stays `delivering` while a delivery job waits in an incident that is not a lost lease, and "still open" is not a status change. `incident.opened` reports that job, so a receiver that must hear of every problem subscribes to it as well. Its body names the job, not the broadcast; tying the two together is recorded under [Later releases](#later-releases). An expiry time ([EVENT-04E](event-04e-signal-broadcast-expiry.md)) would end such a broadcast with a status. |
| Does a subscriber need `Signals.RequiredRole`? | No. Webhooks are managed with `Settings.RequiredRole` for the whole deployment, and the body holds a name, a scope, and counts, never a payload. |
| Does the down migration need a guard? | No. It removes two triggers and one function and deletes no row. Stored events still deliver, and a subscription keeps a type name that no longer matches anything until an administrator edits it. |

## Plan

Written on 2026-11-23 following [Writing a plan](README.md#writing-a-plan). Effort stays **M**: the item needs a migration with a capture function, a catalog change, a small change to one page, and documentation. It changes no workflow JSON, no editor or viewer code, no engine routing, no signal delivery, no API route, and no version-change or reactivation rule, so the plan has no section for those.

### 1. Scope and invariants

The first release adds three event types that announce how a broadcast finished. The invariants of [INT-01](int-01-outbound-lifecycle-webhooks.md#1-scope-invariants-and-compatibility) and of the [parent plan](event-04-signal-events.md#1-scope-and-invariants) hold unchanged. These matter here:

1. **Capture lives only in triggers.** No C# code describes or emits the event. Every writer of `signal_broadcasts` is covered: the API, the Worker, a workflow's throw, and raw SQL.
2. **A committed status change has exactly one event**, when a subscription matches. A rolled-back change has none.
3. **Capture takes no lock.** It reads `engine_settings` and the subscription tables without locking, as the existing capture functions do. A throw still never waits for a recipient, and a delivery still locks the instance, the subscription, and the delivery row in that order and never the broadcast.
4. **Workflows without signals pay nothing.** The triggers are on `signal_broadcasts` only. A deployment whose subscriptions list none of the new types pays one gate read for each finished broadcast and writes nothing.
5. **A body is built once** by the trigger as compact UTF-8 in catalog order and reused for every attempt. Every string in it is bounded by a column limit.
6. **Bodies carry no business data.** No payload, business key, actor, role, claim, cancellation reason, or result description.
7. **New types never opt in an existing subscription.**

This release does not add an event for a throw, a reopening, or a single delivery, a filter by signal name, matching by recipient family, or any change to delivery, retries, cancellation, retention, the **Signals** page, or the managed client scopes.

One limit follows from the rule "an event for a status change": a broadcast that stays `delivering` raises nothing. That happens while a delivery job waits in an incident that is not a lost lease. The body schema version stays 1: the release adds types and gives four envelope fields a meaning for those types, and it changes no existing body.

### 2. Event types and when they are raised

| Type | Raised when the status changes to | Typical cause |
| --- | --- | --- |
| `signalBroadcast.completed` | `completed` | Every delivery ended as delivered, skipped, or cancelled. Also a throw that froze no recipient, and a retry that left nothing pending. |
| `signalBroadcast.completedWithIssues` | `completedWithIssues` | At least one delivery failed. |
| `signalBroadcast.cancelled` | `cancelled` | A user or a client cancelled the broadcast while it was delivering. |

The rule is the status change, whatever caused it:

| Change | Where it happens today | Event |
| --- | --- | --- |
| `delivering` to `completed` or `completedWithIssues` | The recount that finds no pending delivery, in a delivery job. | The type of the new status. |
| `delivering` to `completed` in the throw's transaction | `SetRecipientsAsync`, when the throw froze no recipient. For a workflow's throw this is the transaction of the throwing step. | `signalBroadcast.completed` |
| `delivering` to `completedWithIssues` after a lost lease | The reconciliation in the job repository, in the transaction that opens the `lease_exhausted` incident, once no other definition has a pending delivery. | `signalBroadcast.completedWithIssues`, beside the `incident.opened` of that transaction. |
| `delivering` to `cancelled` | `MarkCancelledAsync` | `signalBroadcast.cancelled` |
| `completedWithIssues` to `delivering` | `ReopenAsync`, in a retry. | None. |
| `completedWithIssues` to `completed` | The recount of a retry that left nothing pending. | `signalBroadcast.completed` |
| Counters change, status stays | Every other recount, and a reconciliation that leaves deliveries of another definition pending. | None. |
| Stays `delivering` behind an incident | A delivery job that stopped with an incident other than a lost lease. | None, until the incident is retried and the job's recount finishes the broadcast. |
| Nothing written | A repeated `idempotencyKey` that returns the stored broadcast. | None. |
| Inserted and deleted in one transaction | A throw refused at the recipient limit. | None. |
| Deleted | Retention. | None. |

- **One finish, one event.** A broadcast to several workflow definitions is recounted by several jobs, and each job recounts after every page. Only the recount that changes the status raises an event.
- **A retry can add events.** A broadcast that finishes with issues, is retried, and finishes again has two events with different IDs. Receivers deduplicate by event ID, not by broadcast ID.
- **The counters add up.** No delivery is pending when a broadcast has one of the three statuses, so in every event the four outcome counters add up to `totalDeliveryCount`.
- **A status that the same transaction replaces is not announced.** Only the status that commits is. One edge remains for raw SQL only: a transaction that changes the status away and back to the status it commits raises two events.
- **Order.** In a workflow's throw that reaches nothing, the broadcast event and the instance's own events commit together. Delivery stays unordered, as for every event.
- **An event can arrive before the thrower knows the broadcast.** A throw that reaches nothing stores its event with the broadcast, so a receiver can get the event before the caller has read the broadcast's ID from the response. A receiver that matches events to its own throws must accept an ID it has not stored yet.

### 3. Event body

All three types share one field list. An example for a broadcast that a workflow threw to one family:

```json
{
  "schemaVersion": 1,
  "eventId": "019ab3c4-5d6e-7000-8000-000000000001",
  "type": "signalBroadcast.completedWithIssues",
  "timestamp": "2026-11-23T09:00:04.125000Z",
  "workflowKey": "example-signal-boundaries-recall",
  "workflowDefinitionId": null,
  "workflowVersion": null,
  "instanceId": null,
  "data": {
    "broadcastId": 57,
    "signalName": "warehouse.product-recall",
    "scope": "workflow",
    "thrownByKind": "instance",
    "thrownByInstanceId": 801,
    "status": "completedWithIssues",
    "totalDeliveryCount": 12,
    "startCount": 1,
    "deliveredCount": 9,
    "skippedCount": 2,
    "failedCount": 1,
    "cancelledCount": 0
  }
}
```

Envelope:

| Field | Value for a broadcast event |
| --- | --- |
| `timestamp` | The `UpdatedAt` of the row version that changed the status. |
| `workflowKey` | The family the scope names, or the family of the scope's instance. Null for a broadcast to all workflows. |
| `workflowDefinitionId`, `workflowVersion` | Always null. A broadcast belongs to no version. |
| `instanceId` | The instance of an instance scope. Null otherwise. It is never the throwing instance. |

Data fields, in body order:

| Field | Type | Source | Notes |
| --- | --- | --- | --- |
| `broadcastId` | integer | `Id` | Read details with `GET /api/signal-broadcasts/{broadcastId}`. |
| `signalName` | string | `SignalName` | At most 100 characters. |
| `scope` | string | Derived | `allWorkflows` when `WorkflowKey` is null, else `instance` when `InstanceId` is set, else `businessKey` when `BusinessKey` is set, else `workflow`. |
| `thrownByKind` | string | `ThrownByKind` | `user`, `client`, or `instance`. |
| `thrownByInstanceId` | integer, nullable | `ThrowerInstanceId` | Set only for an `instance` thrower. |
| `status` | string | `Status` | `completed`, `completedWithIssues`, or `cancelled`; it matches the type. |
| `totalDeliveryCount` | integer | `TotalDeliveryCount` | The recipients the throw froze: waits and family starts. |
| `startCount` | integer | `StartDeliveryCount` | How many of the recipients are family starts. The name follows the API's `startCount`. |
| `deliveredCount`, `skippedCount`, `failedCount`, `cancelledCount` | integer | The four counters | A `completed` broadcast can have skipped deliveries. A cancelled one can have delivered ones. |

- **Names.** The count fields use the names of `SignalBroadcastSummaryDto`, so the event and the API share one vocabulary.
- **Bound.** The free texts are the envelope's family, at most 300 characters, and the signal name, at most 100. The API accepts only lowercase names, but the column limits only their length, so the bound assumes that every character escapes to six bytes: 2,400 bytes for both texts and under 600 for everything else. A body stays under 3 KiB, below the 5 KiB the existing bodies stay under and far below the 16 KiB check on `lifecycle_events`. A usual body has about 500 bytes.
- **Left out on purpose.** The payload, the business key, `ThrownBy`, the roles and claims, the throwing node's ID and name, `CancelledBy`, `CancellationReason`, the recipients per family, and every delivery's result description.
- **No recipients.** `RecipientUserIds` is empty: a broadcast has no user to notify, so [UI-13](ui-13-notification-center.md) gains nothing from these events.

Code:

- **`LifecycleEventTypes`** gains `SignalBroadcastCompleted`, `SignalBroadcastCompletedWithIssues`, and `SignalBroadcastCancelled`, appended to `Subscribable` after `IncidentOpened`. The order is the order of the checkboxes and of stored event sets.
- **`LifecycleEventCatalog`** gains one private field list and three entries before `webhook.test`. Each description says when the type is raised and ends with the matching rule of [section 4](#4-subscription-matching). The envelope descriptions of `workflowKey`, `workflowDefinitionId`, `workflowVersion`, and `instanceId` gain their meaning for a broadcast.
- **`SignalBroadcastEventDataDto`** in `LifecycleEventDtos.cs` has the twelve fields in body order, for the golden-body tests.

### 4. Subscription matching

`lifecycle_event_targets` is unchanged. The capture function passes the broadcast's `WorkflowKey`.

| Broadcast scope | Subscription without a family | Subscription for family A |
| --- | --- | --- |
| Family A | Matches | Matches |
| A business key in family A | Matches | Matches |
| An instance of family A | Matches | Matches |
| Family B, or anything in it | Matches | No match |
| All workflows | Matches | No match, also when instances of A received the signal |

- **Why not match by recipients.** A body is built once for all targets, the recipient list on the broadcast is cut at 50 families, and the match would need a second query for every finished broadcast. The cost and the edge cases are not justified before someone asks for it.
- **What to tell operators.** A receiver that alerts on failed signal deliveries subscribes without a family, or the callers throw to a family. The catalog descriptions, the developer guide, the UI guide, and a note on the **Webhooks** form say so.
- **The thrower's family plays no part.** A broadcast that an instance of family A throws to family B is an event of family B.

### 5. Capture: migration, function, and triggers

One migration, `AddSignalBroadcastLifecycleEvents`, in a transaction. It creates one function and two triggers. It changes no table, so the EF model and its snapshot do not change; the migration is written by hand with its designer file, as other SQL-only migrations are.

```sql
CREATE CONSTRAINT TRIGGER "TR_signal_broadcasts_lifecycle_insert"
AFTER INSERT ON flowbit.signal_broadcasts
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW
WHEN (NEW."Status" <> 'delivering')
EXECUTE FUNCTION flowbit.capture_signal_broadcast_lifecycle();

CREATE CONSTRAINT TRIGGER "TR_signal_broadcasts_lifecycle_status"
AFTER UPDATE OF "Status" ON flowbit.signal_broadcasts
DEFERRABLE INITIALLY DEFERRED
FOR EACH ROW
WHEN (OLD."Status" IS DISTINCT FROM NEW."Status" AND NEW."Status" <> 'delivering')
EXECUTE FUNCTION flowbit.capture_signal_broadcast_lifecycle();
```

- **The `WHEN` clause does the filtering.** Every recount names `"Status"` in its `UPDATE`, so the column list alone would queue a call for each. PostgreSQL evaluates `WHEN` when the row changes, so a recount that keeps the status queues nothing.
- **Names and settings** follow the existing triggers and functions: `SECURITY INVOKER`, `SET search_path = pg_catalog, pg_temp`, and schema-qualified names.

`flowbit.capture_signal_broadcast_lifecycle()` runs at commit and does this:

1. Map `NEW."Status"` to its type. Return when the status has no type.
2. Return unless `flowbit.lifecycle_capture_wanted(ARRAY[type])`. This is the capture switch, the transaction-local `flowbit.lifecycle_events` setting, and the check that any active subscription lists the type.
3. Read the broadcast again by its primary key, selecting only the columns of [section 3](#3-event-body) and `"BusinessKey" IS NOT NULL` in place of the key. It never reads `PayloadJson`, which can hold 256 KiB. Return when the row is gone or its status is no longer `NEW."Status"`.
4. Read the targets with `flowbit.lifecycle_event_targets(type, "WorkflowKey")`. Return when there is none.
5. Call `flowbit.record_lifecycle_event` with the type, `NEW."UpdatedAt"`, source table `signal_broadcasts`, the broadcast's ID as the source ID, the scope's family, no definition, the scope's instance, no user task, the data object, no recipients, and the targets.

- **Why step 3 reads the row again.** The function runs at commit, inside the transaction that changed the status, so it sees that transaction's last version of the row. A cancel recounts after it sets the status; the counts it commits are the ones a receiver should get. The same step makes a status that the transaction replaced, and a row that it deleted, raise nothing.
- **Strings.** The signal name, the scope kind, the thrower kind, and the status go through `to_json`, as the existing functions do. Numbers are cast to text.
- **Lock order.** The function takes no row lock. It inserts one row into `lifecycle_events` and sends the wakeup notification, as every capture does. The transaction that finishes a broadcast already holds that broadcast's row lock and holds it a little longer, for one insert. Nothing in webhook management, fanout, or dispatch locks `signal_broadcasts`, so no new lock cycle exists.
- **Notification.** `record_lifecycle_event` calls `pg_notify` only when it stores an event. PostgreSQL serializes the commits of notifying transactions, so a deployment with a matching subscription serializes its broadcast finishes, which are short and rare beside task events.
- **Cost.** Without a subscription to a new type: one function call for each finished broadcast, with two small reads. With one: one more read of the broadcast, one insert, and one notification.
- **Failure.** A failure in the function fails the commit that finishes the broadcast, as for every capture trigger. The function has nothing data-dependent that can fail: every string is bounded and escaped, and the body is far below its check. Turning `Webhooks.CaptureEnabled` off stops it at step 2.
- **Recovery from a failure.** A recount that fails leaves its broadcast `delivering` with every delivery already finished, and the job retries and then opens an incident. Such a broadcast counts against `Signals.MaxOpenBroadcasts` or `Signals.MaxOpenWorkflowBroadcasts` and is never removed by retention. To recover, turn capture off and retry the incidents: each job runs again, finds nothing pending, and recounts. The deployment guide records these steps.
- **Lost leases.** The reconciliation of [section 2](#2-event-types-and-when-they-are-raised) writes the status in raw SQL, outside the broadcast repository. The triggers see it as any other update. Its transaction also inserts the incident, so one commit can store an `incident.opened` event and a broadcast event.
- **Worker.** No Worker code changes. A delivery job's recount now may store an event when it commits. Fanout and dispatch treat the new types as they treat every type.
- **Retention.** Nothing changes. The `lifecycleEvents` and `webhookDeliveries` categories cover the new events, and the `signalBroadcasts` category deletes a broadcast without touching its events.
- **Versions and reactivation.** Not affected: no definition, subscription, delivery, or instance column changes.
- **Down migration.** It drops the two triggers and the function, and needs no guard. Events that were stored still deliver.

### 6. API contracts and authorization

No route, request, response shape, or status changes.

| Route | Effect |
| --- | --- |
| `GET /api/webhook-event-types` | Returns fourteen subscribable types. The three new entries share their data fields, and four envelope descriptions are extended. |
| `POST` and `PUT /api/webhook-subscriptions` | Accept the new types in `eventTypes`. The message for an unsupported type lists fourteen. |
| `GET /api/webhook-deliveries` and the two bulk routes | Accept the new types in `eventType`. The `workflowKey` and `instanceId` filters find broadcast events by their scope. |
| `GET /api/webhook-deliveries/{id}` | Shows the new bodies, as it shows every stored body. |
| The signal routes | Unchanged. |

- **Authorization.** Settings administrators manage subscriptions, as today. No signal role or client scope is involved in subscribing.
- **Reading details after an event.** A receiver calls `GET /api/signal-broadcasts/{broadcastId}` and `GET /api/signal-broadcasts/{broadcastId}/deliveries` with its own access token and a role from `Signals.RequiredRole`. A managed client reads only the broadcasts it threw, so it gets `404` for the others. After retention removed the broadcast, the route returns `404`.
- **OpenAPI.** The operation texts in `WebhookEndpoints` and `FlowbitOperationCatalog` name no event type, so they stay. The hand-written API reference says "eleven" once and changes.

### 7. Flowbit.Ui

Changes are in `WebhookFormatting` and [Webhooks.razor](../../Flowbit/src/Flowbit.Ui/Components/Pages/Webhooks.razor). The **Signals** page, the editor, the viewer, and the portable authoring package do not change.

| Type | Label |
| --- | --- |
| `signalBroadcast.completed` | Signal broadcast completed |
| `signalBroadcast.completedWithIssues` | Signal broadcast completed with issues |
| `signalBroadcast.cancelled` | Signal broadcast cancelled |

- **Form.** The **Events** checkboxes and the delivery filter's **Event type** list come from `Subscribable`, so they gain the three types once the labels exist.
- **Family note.** While a signal broadcast type is ticked and **Workflow family** is not empty, the form shows under that field: "A signal broadcast to all workflows matches only a subscription without a workflow family." The note is linked to the field with `aria-describedby`. It is a state-dependent line; the field's permanent help text stays. The condition is a pure function in `WebhookFormatting`, so a unit test covers it without the page.
- **Event summary.** A subscription that lists the eleven earlier types no longer reads **All events**; it lists its types, which is correct, because it does not receive the new ones. A subscription with all fourteen reads **All events**.
- **Narrow screens.** `signalBroadcast.completedWithIssues` is the longest type name the form has shown. If its checkbox overflows at 390 pixels, let the type name wrap inside its label; do not shorten it.
- **Delivery detail.** No change. It shows the stored body, and its **Instance** link appears for a broadcast with an instance scope, because the envelope names that instance. It has no link to the broadcast; the body shows `broadcastId`, and `/signals?broadcastId=` opens it.

### 8. Documentation

| Owning page | Update |
| --- | --- |
| [Developer guide](../developer-guide.md#lifecycle-webhooks) | The opening sentence. Three rows in the [event catalog](../developer-guide.md#event-catalog) with the rule of [section 2](#2-event-types-and-when-they-are-raised), and a paragraph on matching and on retries raising a second event. Under [event bodies](../developer-guide.md#event-bodies): the envelope's meaning for a broadcast, the data row, the example of [section 3](#3-event-body), and the list of what bodies omit. A row under [read details after an event](../developer-guide.md#read-details-after-an-event). Three notes for receivers: a broadcast behind a job incident stays open and raises nothing, so subscribe to `incident.opened` as well; a lost lease raises that incident and the broadcast event together; and an event can arrive before the thrower has stored the broadcast's ID. Under [broadcast signals](../developer-guide.md#read-progress) and [throw a signal from a workflow](../developer-guide.md#throw-a-signal-from-a-workflow): a sentence that a receiver can subscribe in place of polling, and that `completedWithIssues` alone suits alerting. |
| [API reference](../api-guide.md#lifecycle-webhooks) | "Eleven" becomes "fourteen" in the validation rule. One sentence in the [signals section](../api-guide.md#signals) that links to the events. |
| [Flowbit.Ui guide](../ui-guide.md#lifecycle-webhooks) | The opening example of events, the family note under [create, test, and activate](../ui-guide.md#create-test-and-activate), and a sentence under [throw signals](../ui-guide.md#throw-signals) that points to the events. |
| [Database schema](../database-schema.md#lifecycle-webhooks) | `signal_broadcasts` among the capture sources, the signal name in the body-bound rule, the meaning of `InstanceId` and `SourceTable` on [`lifecycle_events`](../database-schema.md#lifecycle_events), a row for the two triggers and the function in the list of migration-created objects, and a sentence on [`signal_broadcasts`](../database-schema.md#signal_broadcasts). |
| [Deployment](../deployment.md#lifecycle-webhooks) | An upgrade row for `AddSignalBroadcastLifecycleEvents`, the rollout order of [section 10](#10-delivery-sequence-rollout-and-risks), the fact that the capture switch also stops broadcast events, the recovery steps of [section 5](#5-capture-migration-function-and-triggers) for broadcasts that a failed recount left open, and under [signal events](../deployment.md#signal-events) a pointer and the fact that a lost lease now also raises a broadcast event. |
| [Engine settings](../engine-settings.md#webhookscaptureenabled) | No change: the page names no event source. Confirm this when implementing. |
| [Runtime reference](../../Flowbit/README.md#lifecycle-webhooks) and [AGENTS.md](../../AGENTS.md) | `signal_broadcasts` and `capture_signal_broadcast_lifecycle()` in the capture invariant, the rule that a new broadcast status must extend the function, the catalog, and the tests together, and the count of event types in the [verification](../../Flowbit/README.md#verification) section. In AGENTS.md also the signal events section, with the raw-SQL reconciliation named as a writer of the status, and the smoke matrix range, which becomes R1–R17. |
| [Root README](../../README.md) | The lifecycle webhooks bullet names finished signal broadcasts. |
| Browser suite READMEs | The new smoke scenario R17 in the smoke suite's matrix. |
| This page, the [backlog](README.md), and the [parent](event-04-signal-events.md#later-releases) | Evidence and status. |
| [EVENT-04E](event-04e-signal-broadcast-expiry.md) and [INT-02](int-02-webhook-verification-follow-ups.md) | For EVENT-04E: a status or reason that expiry adds must reach the capture function and the catalog in the same change. For INT-02: this item brings the producer assertions for its own types. The browser coverage of a saved subscription and its deliveries, which INT-02 already tracks, stays there and now also covers these types. |

The example catalog, the signal examples, the node reference, BPMN support, and the authoring guides need no change: no workflow JSON and no example changes. Public examples stay HTTP-only. The item adds no runnable walkthrough; the developer guide's receiver setup already shows how to subscribe, and its type list is an example.

Validate changed relative links and anchors and the JSON example. Compare the event-types route with `LifecycleEventCatalog`, the DTO, and the tests.

### 9. Tests and verification

PostgreSQL tests go in a new class `SignalBroadcastLifecycleEventTests` in the `PostgresApiCollection`, so that `LifecycleEventCaptureTests` does not grow further. It reuses `WebhookTestDatabase` and the signal test helpers. `WithCaptureAsync` moves from `LifecycleEventCaptureTests` into the shared test support, so both classes turn capture on and restore it the same way. Every case that reads a body also checks that the four outcome counters add up to `totalDeliveryCount`.

| Layer | Cases |
| --- | --- |
| Golden bodies | For each type: the body has the envelope and data fields in catalog order, parses into `SignalBroadcastEventDataDto`, and has no other field. `ParseTyped` knows the three types. |
| Finish by delivery | Waits in two workflow definitions, so that two jobs recount: after both jobs ran there is one `signalBroadcast.completed` with the final counts, and none before the last pending delivery ended. |
| Issues | A payload that fails a mapping: one `signalBroadcast.completedWithIssues` with `failedCount` 1. A subscription to `signalBroadcast.completed` alone stores nothing for it. |
| Nothing reached | An API throw without waits: one `signalBroadcast.completed` with `totalDeliveryCount` 0, present when the request returns. |
| Cancel | Pending deliveries and no Worker: the cancel stores one `signalBroadcast.cancelled` whose `cancelledCount` is the number of pending deliveries, not 0. A later recount stores nothing. A cancel of a finished broadcast, refused with `409`, stores nothing. |
| Retry | A retry that reopens stores nothing. After the retried delivery ends, a second event with another ID exists and the first is unchanged. A retry whose only failed delivery is a start that no longer listens stores `signalBroadcast.completed` whose `timestamp` is the retry's time, not the first completion's. |
| Lost lease | A delivery job whose final lease expired: the reconciliation's transaction stores `signalBroadcast.completedWithIssues` with the failed count, and `incident.opened` for a subscriber to that type. With a second definition whose deliveries are still pending, the reconciliation stores no broadcast event, and the event comes when the second job finishes. |
| Open behind an incident | A delivery job stopped by an incident with deliveries still pending: no broadcast event exists. After the incident is retried and the job finishes, one does. |
| Replay | A repeated `idempotencyKey` returns the stored broadcast with `200` and stores no second event. |
| Refused throw | At the recipient limit the throw returns `400`, and no event and no broadcast exists. |
| Workflow thrower | An intermediate throw event and a signal end event: `thrownByKind` is `instance` and `thrownByInstanceId` the throwing instance. A throw that reaches nothing commits its event with the step. A throw that is refused fails its step and stores no event. |
| Client and user | A managed client's broadcast has `thrownByKind` `client`, and a user's has `user`. Neither body contains the client ID or the user name. |
| Scopes | Family, business key, instance, and all workflows: the `scope` value, the envelope's `workflowKey` and `instanceId`, and null definition fields for each. A broadcast with family starts reports `startCount`. |
| Matching | The five rows of [section 4](#4-subscription-matching), with a subscription for family A and one without a family. |
| Gates | Capture off, a paused subscription, an archived subscription, and `SET LOCAL flowbit.lifecycle_events = 'off'` each store nothing. A subscription edit affects only later finishes. |
| Privacy | A broadcast with marker texts in its payload, business key, thrower, cancellation reason, and a failed delivery's result description: none of the markers is in any body. |
| Raw SQL | A row inserted as `completed` stores one event. An update of counters alone stores none. An update to `delivering` stores none. Two status changes in one transaction store only the committed status. A row finished and deleted in one transaction stores none. |
| Bound | A broadcast written with raw SQL whose 300-character family and 100-character signal name consist of control characters, with the largest counts and IDs: the commit succeeds and the body stays under 3 KiB. `LifecycleEventBodyStringsAreBoundedByTheirSourceColumns` adds `signal_broadcasts.WorkflowKey` (300) and `signal_broadcasts.SignalName` (100). |
| No wait | Another transaction holds a subscription row and its revision `FOR UPDATE`: a finish with capture on commits without waiting. |
| Retention | The `signalBroadcasts` cleanup deletes a finished broadcast, stores no event, and leaves the earlier event in place. |
| Schema | `LifecycleCaptureTriggersAreDeferredConstraintTriggersOnInvokerFunctions` lists the two new triggers in its trigger list and `capture_signal_broadcast_lifecycle` in its function list, which checks `SECURITY INVOKER` and the pinned `search_path`. Migration up and down: after down a finish stores nothing, and after up it does again. |
| Fanout and dispatch | One case through the Worker's fanout and dispatcher with the test receiver: a `signalBroadcast.completed` event is delivered with a valid signature and the stored bytes. |
| API | The renamed catalog test expects fourteen subscribable types and the shared field list. Create and update accept the new types. The delivery filter accepts them. `webhook.test` stays refused. |
| Flowbit.Ui | `WebhooksUiTests`: the three labels, the pure condition of the family note for each combination of a signal broadcast type and a family, and through the page, as the other page cases do, **All events** for fourteen types, a list for eleven, and the note in the form's markup. |
| Producer sweep (recorded) | The signal test classes run with `FLOWBIT_TEST_LIFECYCLE_CAPTURE` set: none fails, and the report counts events of all three types. This shows that capture breaks no signal path. |

Browser smoke suite, which has no Worker and no webhook key ring:

- **R17, Webhooks form**, at 1440×900, 1024×768, and 390×844. An administrator opens **Webhooks**, chooses **Add subscription**, and sees the three types with their labels and type names. Ticking one and typing a workflow family shows the family note; clearing the family hides it. The page has no horizontal overflow, and the **Event type** filter lists the three types. The scenario does not save, because the stack cannot protect a signing secret.

Then follow [mandatory real-browser verification](../../AGENTS.md#mandatory-real-browser-ui-verification) by hand on a local stack with published API, UI, and Worker hosts, the webhook key ring, delivery enabled, capture on, and a loopback receiver that verifies each signature:

1. Create a subscription with the three types and no family, save the secret, send a test event, and resume.
2. Throw the signal of [Example 01](../../examples/signals/01-signal-catch-policy-change.json) from **Signals** with waiting instances: the receiver gets `signalBroadcast.completed` with the counts the page shows.
3. Throw it with a payload that fails the mapping: the receiver gets `signalBroadcast.completedWithIssues`. Choose **Retry failed** after correcting nothing: a second event arrives.
4. Stop the Worker, throw, cancel, and start the Worker: the receiver gets `signalBroadcast.cancelled` with the cancelled count.
5. Run [Example 03](../../examples/signals/03-signal-throw-order-cancellation.json): the event of the workflow's throw has `thrownByKind` `instance`.
6. Add a second subscription limited to one family and throw to all workflows: only the first subscription has a delivery.
7. On **Webhooks**, filter the deliveries by each new type, open a delivery, and compare the body with the catalog. Repeat the layout check at the three sizes, use the keyboard through the form, and read the console.

Keep screenshots of the form with the new types and the family note, and of a delivery's detail.

Regression commands, run from the repository root with the same arguments in PowerShell 7 and Bash:

```text
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet
dotnet publish Flowbit/src/Flowbit.Api/Flowbit.Api.csproj -c Release -o artifacts/browser/hosts/api /p:UseAppHost=false
dotnet publish Flowbit/src/Flowbit.Ui/Flowbit.Ui.csproj -c Release -o artifacts/browser/hosts/ui /p:UseAppHost=false
dotnet build Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore
git diff --check
```

The suite READMEs cover Docker and the Chromium install. Full solution runs have had order-dependent failures at the base commit, so compare failures against a base run before attributing one to this change. The signal acceptance scenarios S1 to S13 need no change; rerun them once on the final build, because every one of them finishes broadcasts through the new triggers.

### 10. Delivery sequence, rollout, and risks

1. **Catalog.** The three constants, the catalog entries, the DTO, and the envelope descriptions. Exit with the catalog and API tests green; capture tests for the new types still fail.
2. **Capture.** The migration with the function and the triggers, and the schema tests. Exit with `SignalBroadcastLifecycleEventTests` green and the existing capture and signal tests unchanged.
3. **Flowbit.Ui.** The labels and the family note. Exit with the component tests green.
4. **Documentation.** The owning pages of [section 8](#8-documentation).
5. **Verification.** The producer sweep over the signal classes, R17, the signal acceptance scenarios, and the manual checks. Record the evidence under [Current state](#current-state), then set the status to Done here, in the [backlog](README.md#event-04-follow-up-subtasks), and in the [parent](event-04-signal-events.md#later-releases).

Rollout:

1. Apply `AddSignalBroadcastLifecycleEvents`. Creating a trigger blocks writes to `signal_broadcasts` for a moment. Nothing is captured yet, because no subscription lists a new type.
2. Upgrade every API and UI replica. An API replica without this release refuses the new types with `400`, and a UI without it does not offer them. Workers deliver the new events with or without this release; upgrade them with the rest.
3. Add the types to subscriptions. Capture starts with the first broadcast that finishes after the edit commits. Nothing is backfilled.
4. To roll the binaries back, nothing must be stopped. The triggers keep capturing for subscriptions that list the types, and Workers keep delivering. An earlier UI drops the new types from a subscription when it saves an edit.

A deployment that upgrades its binaries before the migration offers the types and captures nothing until the migration is applied.

| Risk | Guard |
| --- | --- |
| The function fails and blocks every finish, so broadcasts stay `delivering`, count against the open-broadcast limits until throws are refused, and a workflow's throw that reaches nothing fails its step. | Bounded, escaped strings and a body far below its limit, tested with the worst texts. The producer sweep runs every signal test with capture on. `Webhooks.CaptureEnabled` is the emergency stop, and retrying the jobs' incidents then finishes the open broadcasts. |
| A receiver waits for a broadcast event that never comes, because a delivery job stopped with an incident. | The guides state the limit and recommend `incident.opened` beside the broadcast types. A lost lease, the usual cause, does finish the broadcast and raises its event. Tying an incident to its broadcast is recorded under [Later releases](#later-releases). |
| A later change writes the status in another place and expects to add an event call there. | Capture needs no call: the triggers cover the repository, the raw-SQL reconciliation, and any new writer. AGENTS.md says so, and the lost-lease test proves it for the one writer outside the broadcast repository. |
| A recount raises an event each time, and receivers get duplicates. | The `WHEN` clause requires a status change, and the two-job test counts one event. |
| A cancel reports zero cancelled deliveries. | The function reads the committed row, and the cancel test checks the count. |
| A family-limited subscription silently misses a broadcast to all workflows. | The catalog description, the guides, and the form's note state the rule, and the match table is tested. |
| A receiver treats two events for one broadcast as a duplicate and drops the second. | The guides say that a retry raises a new event and that receivers deduplicate by event ID. |
| An integration that throws one signal for each order floods a receiver subscribed to `signalBroadcast.completed`. | The guides recommend `signalBroadcast.completedWithIssues` alone for alerting. Per-subscription concurrency and receiver backoff already bound the sending. |
| A body leaks business data. | The function never reads the payload or the key, and the privacy test uses marker texts in every excluded column. |
| An existing subscription starts to receive events it did not ask for. | A subscription's types change only through an edit, and a test with an eleven-type subscription stores nothing. |
| A later change adds a broadcast status, such as expiry, and no event announces it. | AGENTS.md and the EVENT-04E page say that the function, the catalog, and the tests change with the status. The function ignores a status without a type, so it never fails on one. |
| The long type name overflows the form on a phone. | R17 checks 390×844, and the type name may wrap. |
