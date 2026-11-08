# INT-01: Outbound lifecycle webhooks

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | In progress |
| Category | Integration |
| Effort | L — revised from M after planning the event ledger, delivery queue, security, and recovery work |
| Priority | Recommended order #6 |
| Depends on | None |
| Related | [UI-13](ui-13-notification-center.md), [UI-05](ui-05-live-refresh.md), [TASK-04](task-04-email-and-chat-send.md), [TASK-02](task-02-external-task.md) |

## Summary

Subscriptions deliver committed lifecycle events to external systems: instance started, completed, faulted, cancelled, or reactivated; task created, assigned, completed, or cancelled; delegation granted; and incident opened. PostgreSQL triggers record each subscribed event in the transaction that makes the change. A separate PostgreSQL-backed queue in the Worker sends HTTP requests signed according to [Standard Webhooks](https://www.standardwebhooks.com/), records attempts, retries transient failures, backs off from unavailable receivers, and exposes failed deliveries for recovery.

The event record also provides the foundation for [UI-13](ui-13-notification-center.md). [UI-05](ui-05-live-refresh.md) remains independent. The first release is implemented, and this document stays its plan and design record: [Current state](#current-state) lists what is verified and what still blocks **Done**. The shipped behavior is described in the [developer guide](../developer-guide.md#lifecycle-webhooks), [API reference](../api-guide.md#lifecycle-webhooks), [UI guide](../ui-guide.md#lifecycle-webhooks), [deployment guide](../deployment.md#lifecycle-webhooks), and [database schema](../database-schema.md#lifecycle-webhooks).

## Why it adds value

- CRM, BI, and notification systems learn about changes without polling the API, through receivers that verify Standard Webhooks signatures or integration platforms that accept a tokenized catch URL. Endpoints that require their own authentication header need a relay until custom headers ship.
- Operators can recover delivery without repeating workflow actions.
- A reusable event ledger separates committed changes from receiver availability.

## First release acceptance criteria

Revised by this plan on 2026-11-07. Check these only after implementation and verification.

- [ ] All eleven event types in the [catalog](#2-event-catalog-and-producer-coverage) are recorded by database triggers in their source transactions, covering API, Worker, raw SQL, set-based multi-instance, message-start, call-activity, and administrative paths.
- [x] Rolled-back mutations, unchanged command retries, and transitions that match no active subscription record nothing. Reactivation records `instance.reactivated` and can produce a later, distinct terminal event.
- [x] Settings administrators manage subscriptions, filters, signing-key rotation, pause/resume, test events, and archival through the API and Flowbit.Ui.
- [x] Payloads contain object identifiers, node external IDs, and bounded lifecycle metadata, never variables, business keys, workflow definitions, JWT claims, diagnostics, or credentials. Internal recipient identifiers support UI-13 without being sent externally.
- [x] Target subscriptions are captured transactionally. Later edits cannot redirect old events or subscribe to historical events retroactively.
- [x] The Worker uses at-least-once delivery semantics, stable identifiers, signed immutable bytes, a 72-hour retry window, per-subscription concurrency and backoff, and no ordering guarantee. Unavailable receivers can end in visible failure or automatic pause; successful receipt is not guaranteed.
- [x] Receiver failure cannot change workflow state, create workflow incidents, or consume workflow-job slots. Database failure while recording an event rolls back the source mutation.
- [x] Failed deliveries and attempts are visible and support revision-protected retry/discard, individually and in bounded bulk.
- [x] HTTPS, destination checks, bounded HTTP execution, protected keys, and Standard Webhooks signature verification are specified and tested.
- [x] Retention protects pending and recoverable work without altering runtime history or reactivation eligibility.
- [ ] Migration, the database capture switch, multiple Workers, crash recovery, receiver backoff, browser interactions, receiver deduplication, and the delivery throughput targets are verified with synthetic local data.

## Later releases

- Selected variable values, business keys, node attributes, and custom payloads, with a separate authorization design.
- Ordering per instance, historical replay to new subscriptions, and redelivery of succeeded events.
- Workflow-family-owned administration, per-user subscriptions, and preferences.
- Task unassignment, claim changes, and incident-resolution events.
- Receiver OAuth, mutual TLS, custom headers, Standard Webhooks asymmetric (`v1a`) signatures, brokers, and other delivery adapters.
- Automatic deletion of immutable subscription revisions; v1 retains them to avoid capture/cleanup races.

## Current state

Verified against the repository on 2026-11-08: the first release is implemented, and the findings of an independent review that day are fixed. The item stays **In progress** until the gaps listed last are closed.

- **Capture.** Migration `20261107154545_AddOutboundLifecycleWebhooks` adds the event, subscription, revision, signing-key, delivery, attempt, audit, and key-ring tables, the capture switch `flowbit.set_lifecycle_event_capture`, a `flowbit.uuid_v7()` function for PostgreSQL 17, and deferred constraint triggers on `workflow_instances`, `user_tasks`, `workflow_incidents`, and `user_delegations`. No application code emits events, so every API, Worker, and raw SQL writer is captured once the switch is on.
- **Delivery.** The Worker's [LifecycleEventFanoutService.cs](../../Flowbit/src/Flowbit.Worker/LifecycleEventFanoutService.cs) and [WebhookDispatcher.cs](../../Flowbit/src/Flowbit.Worker/WebhookDispatcher.cs) fan out, lease, sign, send, retry, back off, and pause, gated by `Webhooks:DeliveryEnabled`. `/health/ready` reports a webhook problem as `Degraded` without failing job processing, and `/metrics` adds webhook series.
- **Management.** Eighteen settings-administrator routes in [WebhookEndpoints.cs](../../Flowbit/src/Flowbit.Api/Endpoints/WebhookEndpoints.cs) under `/api/webhook-event-types`, `/api/webhook-subscriptions`, and `/api/webhook-deliveries`; the `delegationId` filter on delegation management search; the `/webhooks` and `/webhooks/deliveries/{id}` pages; and the `webhookDeliveries` and `lifecycleEvents` retention categories.

The 2026-11-08 review fixes:

- **Dispatch.** Only a failed probe doubles receiver backoff, and a backoff without probes for over an hour restarts instead of pausing. Held work is never scanned or polled every 250 ms. A host whose certificates cannot unwrap every ring key is unavailable rather than failing deliveries, and a temporary key-protection error releases a lease unspent. Prepare-send drops revoked or expired keys and releases after a rotation. Finalize retries briefly, a failed heartbeat retries, and readiness tracks each loop separately. Fanout share-locks subscriptions, so a delivery is never left pending behind a settled archive. The Worker pool defaults to 20 connections and warns below `MaxConcurrentSends` + 4.
- **Capture.** Both unique signing-key indexes exist. Incident bodies stay bounded: an over-long node external id is null. Version-change and variable-update batch-job incidents name no node.
- **API.** Bulk date filters accept any UTC offset. DTO string forms omit secrets and URLs. Page overflow, unknown failure codes, unknown query keys, and unknown bulk members return 400. Archived subscriptions refuse discard and retry with 409.
- **UI.** Action errors survive the refresh, and any failure is reported without ending the session; a refused clipboard copy keeps the secret. Non-administrators see only the permission message. Identity changes, double clicks, and a rotate conflict are handled. Bulk confirmations count fresh and say when State does not apply. Focus moves to forms, the secret, and confirmations. The detail page shows the exact signed body. Archived subscriptions offer no recovery actions.

Verified so far:

- Automated tests on PostgreSQL 17. Every webhook-affected class passed together (411 of 411): capture, API, API contract, dispatch, key protection, Worker health, retry and destination policy, signing, and UI webhook tests, plus schema, migration snapshot, retention, OpenAPI, delegation, Worker-surface, connection-pool, and authoring-package suites. The full suite passed 4,120 of 4,164 cases. Its 44 failures are in the administrative-action, call-activity, error-boundary, expression-timer, and business-calendar groups, whose tests lease the shared job queue and pick up other tests' due jobs. Rerun alone, every class passes except `ExpressionTimerRuntimeTests`, whose two failures reproduce identically when that class runs alone on the base commit.
- The [producer sweep](../../Flowbit/README.md#verification) ran the whole suite with capture on and a catch-all subscription on 2026-11-07, before the review fixes: 4,023 of 4,067 cases passed, no webhook test failed, and every other failure also fails without capture or passed when rerun alone with capture on. It recorded all eleven event types, among them 1,455 `instance.started`, 3,489 `task.created`, 81 `incident.opened`, and 12 `delegation.granted` events.
- Chromium (the Claude desktop browser pane) against published API, UI, and Worker hosts on `127.0.0.1`, with a loopback receiver that verified every signature itself. On 2026-11-07, at 1024×768:
  - create paused and save the one-time secret, a test event while paused, resume, and capture on;
  - real instance and task events, retries and backoff probing on `500`, terminal `400` failures, single and bulk retry;
  - automatic pause on `410`, resume, rotation with dual signatures and revocation;
  - a URL edit whose old deliveries kept their captured destination, discarding an old destination's work;
  - pause with bulk discard, archive, and the retention page;
  - layout checks at 1440×900 and 390×844.

  The API protected each secret and a separate Worker process signed with it. On 2026-11-08, after the fixes, on a fresh database on a UTC+03:00 machine:
  - focus moved to the form's name field and then to the one-time secret;
  - a refused clipboard copy kept the secret and asked for a manual copy;
  - a second test while one was outstanding reported its 409 after the refresh;
  - real events failed with `400`, and bulk retry with local-time **From** and **To** filters retried all nine (it previously returned 500); the receiver verified every redelivery;
  - the bulk confirmation took focus and said when State does not apply;
  - the detail page showed the exact signed body, and an archived subscription offered no recovery actions;
  - a non-administrator saw only the permission message at 390×844, 1024×768, and 1440×900, with no horizontal overflow;
  - the console showed no errors or warnings.

Still open before **Done**:

- Delivery throughput targets and capture cost with 0, 1, and 100 subscriptions and a 1,000-child multi-instance commit are not measured.
- Neither browser suite has webhook scenarios, and the multi-Worker crash and restart acceptance run was not built. Dispatch tests cover the remaining failure modes against real PostgreSQL instead:
  - acquirers racing at the same time;
  - lease loss during a send and a hard stop;
  - a Worker crash after the receiver accepted;
  - expired leases and stale finalizers.
- Message-start and administrative-action producers have no per-path event assertions; the producer sweep shows only that they commit with capture on. Call-activity admission, version-change batch, and Worker timer-start incident paths now have them.
- Restoring the key ring on another host, and the certificate-loss recovery steps in the [deployment guide](../deployment.md#signing-keys-and-the-key-ring), are not rehearsed end to end. Tests cover two hosts sharing the ring, a certificate change through `PreviousCertificates`, and a host that cannot unwrap the ring.

## Builds on

- Transaction ownership and the existing instance/gateway/token/MI/task lock order.
- Trigger-maintained derived rows and migration-owned SQL functions, including the fenced shared-variable cutover function.
- PostgreSQL leases, fenced completion, short acquisition transactions, and polling with transactional wakeup hints.
- Settings-administrator authorization, optimistic revisions, typed API clients, and management-page patterns.
- The retention coordinator, separate connection budget, preview protections, and durable manual cleanup.

## Open questions resolved by this plan

| Question | First-release decision |
| --- | --- |
| Global or family-scoped administration? | Deployment-wide `Settings.RequiredRole` authority. A workflow-key filter controls matching, not authorization. |
| How are transitions detected? | Deferred PostgreSQL constraint triggers on `workflow_instances`, `user_tasks`, `workflow_incidents`, and `user_delegations`, gated by a database capture switch. Application-level emission was rejected: every current and future producer, including raw SQL and set-based paths, would have to remember the call, and a per-process switch would need a traffic drain and silently lose events on a misconfigured replica. |
| Signing and rotation? | Standard Webhooks v1: HMAC-SHA256 over `id.timestamp.body`; generated 32-byte `whsec_` secrets, one-time disclosure, bounded dual-signature overlap. |
| UI-13 fields? | Internal canonical recipient identifiers plus target references. UI-13 registers its own internal target in the triggers when it ships. Notifications must still reauthorize access when displayed. |
| Events nobody subscribes to? | Not stored. A transition that matches no active target writes nothing. |
| Reuse workflow jobs? | Dedicated delivery queue/dispatcher in the existing Worker process; reuse patterns without runtime execution ownership. |
| When do filters apply? | At commit, in the trigger's subscription query inside the source transaction. |
| Task created semantics? | A work item becoming active, including a pending item completed administratively without activation. Pending allocation alone emits nothing. This does not guarantee visibility to every actor. |
| Receiver outages? | A 72-hour retry window, per-subscription backoff with single probes, bulk retry/discard, and automatic pause on `410 Gone` or after 72 hours without success. |
| Backfill or ordering? | Neither in v1; capture starts when the database switch is turned on, and delivery and retries are unordered. |

## Plan

### 1. Scope, invariants, and compatibility

Implement an event ledger, trigger-based transactional capture, asynchronous fanout, a dedicated delivery queue, management APIs, and a Webhooks page. Runtime C# mutation code is unchanged: it never describes events, resolves receiver DNS, decrypts webhook keys, or sends HTTP.

- Detect transitions where they are committed: row changes on the four source tables. Raw SQL, set-based statements, `ON CONFLICT DO NOTHING` inserts, Worker paths, and future producers are covered without per-path calls. EF interception, API middleware, telemetry listeners, or history polling would miss transitions.
- Store an event only when it matches at least one active target. A deployment without subscriptions pays a capture-switch and subscription read per qualifying transition and writes nothing.
- One database row, `lifecycle_event_capture_state`, is the capture switch, read by every trigger, so all writers agree, including binaries not yet upgraded. Capture applies to transitions committed after the switch's own commit. `Webhooks:DeliveryEnabled` stays a per-Worker switch, initially false, because it only controls sending. After cutover, handle outages by disabling delivery or pausing subscriptions; turning capture off is the emergency stop for a trigger defect and leaves a permanent gap.
- A successful business commit implies its event/targets exist. A trigger failure fails that commit; receiver failure occurs later and cannot fail it.
- No workflow JSON, editor controls, schema version, loader, `WorkflowModelMigrator`, AI tool, or node/version-switch compatibility change is required. Old documents behave identically.
- Capture the definition ID/version applying when the transition commits. A later version switch never rewrites an event.
- Preserve the one-existing-instance-per-transaction rule for call activities. Parent and child events belong to their own transactions.
- Data-repair migrations set the transaction-local setting `flowbit.lifecycle_events` to `off` unless they deliberately announce their changes.

### 2. Event catalog and producer coverage

Use these case-sensitive event names. Subscription lists are nonempty, contain supported types only, and are deduplicated. New event types never implicitly opt in existing subscriptions.

| Type | Emit when | Required boundaries |
| --- | --- | --- |
| `instance.started` | A new instance row is inserted. | Ordinary, message, timer, and child-call starts. Returning an existing idempotent start inserts nothing and emits nothing. |
| `instance.completed` | The instance status changes to `completed`. | Terminate-end completion counts; a single parallel token ending does not change the status. |
| `instance.faulted` | The instance status changes to `faulted`. | Error ends/uncaught child failures; a paused job incident alone is not a faulted instance. |
| `instance.cancelled` | The instance status changes to `cancelled`. | User, administrative, and propagated call cancellation; repeated cancellation changes nothing and emits nothing. |
| `instance.reactivated` | The status changes from `completed` or `cancelled` back to `running`. | Terminal reactivation; its new work item emits `task.created` in the same transaction. |
| `task.created` | A work item becomes active: inserted active, activated from pending, or completed directly from pending. | One event per normal item or MI child, never the MI parent. Include async-before and sequential MI activation. An administratively completed pending child emits `task.created` then `task.completed`, matching its node visit, which starts at its completion time. |
| `task.assigned` | A non-null assignee is first set or changes to a different actor. | Expression, collection, inheritance, management, and machine distribution. Pending assignments include `taskStatus: "pending"`. Unassignment and claim/unclaim are excluded. |
| `task.completed` | A work item's status changes to `completed`. | Normal, delegated, administrative, and MI-item actions. |
| `task.cancelled` | A work item changes to `cancelled` from `active`, or from `pending` when it has an assignee. | Boundary interrupts, MI quorum and interrupts, cancelling joins, scoped interrupts, terminate ends, instance cancellation, and administrative force/timer actions. A pending item without an assignee was never announced and emits nothing. |
| `delegation.granted` | One new delegation grant is persisted for a workflow family. | One per grant in a batch; `acceptanceState` is `pending` or `notRequired`. Instance ID is null. |
| `incident.opened` | A new incident row is inserted. | All three insert paths, including the set-based `lease_exhausted` insert and timer-start incidents without an instance. Returning an existing incident, or a conflicting row skipped by `ON CONFLICT DO NOTHING`, inserts nothing and emits nothing. Webhook failure is not this event. |

Immediate start/end records both events in one transaction. An item activated/completed in one transaction may likewise emit both even if never observed waiting: events describe committed transitions, not current availability. One row change that qualifies for several events emits them in the order created, assigned, then completed or cancelled.

Producers write an item's initial assignee with its insert (expression, collection) or in one later update (inheritance), so no temporary assignee is announced; the producer matrix keeps that true. A pending assignment and later activation have separate events; activation alone does not repeat assignment. Compare previous/new `Assignee` with existing case-insensitive identity semantics; a claimant never counts as an assignee. Do not deduplicate terminal/assignment events by entity/type or assignee value: reactivation followed by another completion and A-to-B-to-A reassignment are legitimate new events.

The triggers cover these paths by construction. The producer matrix still exercises each one:

| Area | Existing paths to exercise |
| --- | --- |
| Start/end/reactivation | `StartInstanceCoreAsync`, `StartByMessageAsync`, timer and child-call starts through `AddInstanceAsync`, `SetInstanceTerminalStatusAsync`, and reactivation's `SetInstanceStatusAsync`. |
| Task allocation/activation | `AddExecutionTokenAsync`, `AddExecutionTokensAsync`, `UpdateExecutionTokenAsync`, the raw SQL insert in `AddMultiInstanceAsync`, `ActivatePendingNodeAsync`, `ActivateNextMultiInstanceItemAsync`. |
| Assignment/completion/cancellation | `NewUserTask`, `ApplyAssignmentInheritanceAsync`, `SetUserTaskAssignmentAsync`, `UpdateUserTaskAssignmentAsync`, `CompleteUserTaskAsync`, `CompleteMultiInstanceItemAsync` (including administrative `completeAllChildren` over pending children), `CancelMultiInstanceTasksSetBasedAsync`, and every single-task cancellation path. |
| Delegation | `UserDelegationService.CreateCoreAsync` and its batch grant insert, for self-service and administrative grants. |
| Incidents | `WorkflowJobRepository.EnqueueIncidentAsync`, `OpenIncidentAsync`, and `OpenExhaustedLeaseIncidentsAsync`. |

### 3. Envelope, identity, and internal recipients

Define `LifecycleEventDtos.cs` and a service-side catalog of names, descriptions, and field lists for the event-types route and tests. The trigger functions are the only producer of event bodies; tests parse every golden body against those DTOs. Never serialize runtime entities or instance-detail DTOs. Example external body:

```json
{
  "schemaVersion": 1,
  "eventId": "019a5e2d-6d40-7000-8000-000000000001",
  "type": "task.assigned",
  "timestamp": "2026-11-07T09:00:00.000000Z",
  "workflowKey": "purchase-review",
  "workflowDefinitionId": 42,
  "workflowVersion": 3,
  "instanceId": 801,
  "data": {
    "userTaskId": 1201,
    "executionTokenId": 901,
    "nodeId": 5,
    "nodeExternalId": "TASK_MANAGER_REVIEW",
    "nodeExecutionId": 1301,
    "multiInstanceExecutionId": null,
    "itemIndex": null,
    "taskStatus": "active",
    "assignmentOperation": "assigned"
  }
}
```

- `eventId` is a globally unique UUIDv7 from a migration-owned `flowbit.uuid_v7()` function; PostgreSQL 18 adds a built-in. Every attempt for every subscription sends this ID as `webhook-id`. `timestamp` follows the Standard Webhooks payload convention: the ISO 8601 UTC transition time at the stored microsecond precision, taken from the source row (`CreatedAt` for an insert; `UpdatedAt` for activation, assignment, and reactivation; `CompletedAt` for completion and cancellation; `FinishedAt` for terminal instance events), not commit order.
- Common fields are the example's envelope fields; definition ID/version and instance ID are nullable. Delegations have a family key but no selected version. The table below fixes each event's data contract.
- Task data contains applicable item/token/node/MI identifiers and the node `externalId`, which is version-stable; integrations should prefer it to the version-scoped node ID. Assignment operation is `assigned` or `reassigned`. External bodies omit assignee/actor identities, business keys, display names, URLs, variables, form values, comments, role arrays, claims, node attributes, and failure messages. Business keys are start-variable values, so they wait for the selected-values design; node attributes could exceed the body bound.
- Store exact UTF-8 JSON bytes once, without a BOM, built by the trigger in catalog field order, and reuse them for every attempt. Indexed scalar columns support queries; JSONB reserialization is not the canonical byte source. Bound bodies to 16 KiB through catalog field limits/tests, never silent truncation.
- Separate internal `recipientUserIds` and target references capture the canonical new assignee or delegate. `task.created` also snapshots the effective assignee at activation, letting UI-13 defer a pending assignment and revalidate/release its notification when activation arrives, including out-of-order arrival. No role expansion or identity-provider lookup is added. Delivery-detail DTOs omit these internal fields.
- UI-13 can consume recipients later but must enforce current access and handle pending work before displaying notifications. It adds an internal target to the triggers' match, so its events are stored without an external subscriber. INT-01 does not implement unread state, recipient fanout, or consumer checkpoints. UI-13 must add its own retention protection; indefinite availability is not assumed.
- There are no occurrence keys or transition counters. A qualifying row change queues its trigger call once, rolled-back work queues nothing, and an unchanged update fails the trigger's `WHEN` clause.

Every field listed for an event is present; nullable fields use JSON null. Fields belonging only to another event type are absent. IDs use existing numeric API types except event/delivery/subscription/key UUIDs. Version 1 does not include a completion flow ID or free-form completion or cancellation reason.

| Event data | Required fields | Nullable fields |
| --- | --- | --- |
| `instance.started` | `status: "running"` | None. |
| `instance.completed` / `instance.faulted` / `instance.cancelled` | `status: "completed"` / `"faulted"` / `"cancelled"`, respectively. | None. |
| `instance.reactivated` | `status: "running"`, `previousStatus` (`completed` or `cancelled`). | None. |
| All four task types | `userTaskId`, `executionTokenId`, `nodeId`, `taskStatus`. | `nodeExternalId`; `nodeExecutionId` for legacy visits; `multiInstanceExecutionId` and zero-based `itemIndex` are both null for normal tasks and populated for MI children. |
| `task.created` additions | No additional field; `taskStatus` is `active`. | None beyond common task fields. |
| `task.assigned` additions | `assignmentOperation` (`assigned` or `reassigned`); `taskStatus` is `active` or `pending`. | None beyond common task fields. |
| `task.completed` additions | No additional field; `taskStatus` is `completed`. | None beyond common task fields. |
| `task.cancelled` additions | `previousTaskStatus` (`active` or `pending`); `taskStatus` is `cancelled`. | None beyond common task fields. |
| `delegation.granted` | `delegationId`, `acceptanceState` (`pending` or `notRequired`). | Envelope definition ID/version and instance ID are null. |
| `incident.opened` | `incidentId`, `jobId`, `status: "open"`. | `nodeId` and `nodeExternalId` when no authored node applies; `executionTokenId` when the source job has none; envelope instance ID for global jobs. |
| `webhook.test` (not subscribable; see the test route) | `subscriptionId`. | Envelope workflow key, definition ID/version, and instance ID are null. |

For incidents, external `jobId` is copied from `OriginalJobId`, preserving the occurrence's job identity even after the nullable live job link is detached by retention. At commit the trigger reads token references from the source job and the node external ID from the definition JSON. Phase 1 fixtures must include ordinary instance, reactivation, MI task, cancellation, delegation, instance-less incident, and test envelopes, not only the assignment example above.

### 4. Schema and transactional capture

Add an additive `AddOutboundLifecycleWebhooks` migration in the fixed `flowbit` schema. Proposed tables and database objects:

| Object | Main responsibilities/columns |
| --- | --- |
| `lifecycle_events` | UUID ID, type/schema/time, source table and row ID, indexed source IDs, canonical body bytes, internal recipient JSON, captured target revision IDs, `FanoutCompletedAt`. |
| `lifecycle_event_capture_state` | One row: enabled flag, changed time and operator. |
| `webhook_subscriptions` | UUID ID, name/description, current configuration revision ID, state (`active`, `paused`, `archived`), pause reason (`administrator`, `receiverGone`, `receiverUnavailable`), maximum concurrency, optimistic revision, timestamps/actors. |
| `webhook_subscription_revisions` | Immutable ID/subscription ID, endpoint URL, event set, optional exact workflow key, configuration revision, creation metadata. |
| `webhook_subscription_dispatch_state` | One row per subscription, written by the Worker and by resume: consecutive failures, backoff start/until, last success/failure. Keeps dispatcher writes off the management revision. |
| `webhook_signing_keys` | Key/subscription IDs, protected secret bytes, current/previous state, overlap expiry, revocation/creation metadata. |
| `webhook_deliveries` | UUID ID, event/subscription/destination-revision IDs, test flag, state, due time, retry generation, nullable `GenerationStartedAt`/`DeadlineAt`, generation/lifetime attempts, lease owner/token/generation/expiry, result/settlement times, optimistic revision. Unique `(EventId, SubscriptionId)`. |
| `webhook_delivery_attempts` | Delivery ID and unique lifetime attempt number, retry/lease generation, signing key IDs, start/end, safe outcome code, HTTP status, duration. No response body. |
| `webhook_management_audit` | Append-only operation (including bulk operations and automatic pauses), canonical administrator or `system`, target IDs, timestamp, bounded structured changes. No keys, signatures, payloads, URL paths or queries, or remote error text. |
| `webhook_data_protection_keys` | The certificate-wrapped webhook Data Protection key ring (see [signing](#7-signing-and-destination-security)). |
| Functions and triggers | `flowbit.uuid_v7()`, the per-table capture functions and their deferred triggers, `flowbit.set_lifecycle_event_capture(enabled, operator)`, and `flowbit.record_webhook_test_event(subscription_id)`. Follow the existing trigger functions' `SECURITY INVOKER` and pinned `search_path`. |

Use restrictive FKs within webhook structures. Runtime IDs in events are immutable references, not cascading FKs to instance/task/history/definition/incident rows: delivery must survive runtime retention without cross-instance FK locks. Later resource lookups can return 404.

Add indexes for pending fanout, due/expired leases, delivery filters, attempt history, event lookup, retention, and the triggers' match of active subscription revisions by event type and workflow key; use partial indexes for active queue states. Add constraints for valid states, nonnegative counters, and consistent lease fields. SQL remains authoritative for filtering, counts, ordering, and paging.

Capture sequence:

1. Deferred row-level constraint triggers (`DEFERRABLE INITIALLY DEFERRED`) on the four source tables use column lists and `WHEN` clauses, which PostgreSQL evaluates when the row changes, so unrelated updates never queue a call. Queued calls run at commit in the order their changes happened, after rows written later in the transaction, such as node executions, exist.
2. Each call returns at once when the capture switch is off or `flowbit.lifecycle_events` is `off` for the transaction.
3. It derives the transition from `OLD`/`NEW` and, in one statement, selects current revisions of active subscriptions matching the event type and exact workflow key (null means every family). With no match, nothing is written. A subscription edit committed after that statement affects later captures.
4. Otherwise it inserts the event with its target revision IDs and calls `pg_notify('flowbit_lifecycle_events', '')`. The notification is only a wakeup hint; polling is authoritative. Only transactions that stored an event notify, because PostgreSQL serializes the commits of notifying transactions.
5. Capture reads subscription rows without locks and creates no FK to them, so it never waits on management or dispatch. Initially cap non-archived subscriptions at 100, enforced atomically on subscription creation; never truncate matches.

A set-based MI statement queues one call per child, bounded by `Workflow.MultiInstance.MaxInstances`; measure that commit cost before raising limits.

URL/filter edits create immutable revisions for future captures. Existing targets keep their original destination. Pause stops new matching and holds captured work; resume does not backfill the gap. Archive is irreversible in v1 and settles outstanding work as discarded in bounded batches. Fanout of an archived target records a discarded delivery. An old destination that is decommissioned keeps its outstanding deliveries: discard them in bulk, filtered by destination revision, and let the receiver reconcile through the [callback routes](#receiver-callbacks).

Retain subscription/revision metadata indefinitely in v1. IDs in an event array have no FK deletion fence: an old revision could otherwise be deleted while an uncommitted transaction has captured it. Do not claim `NOT EXISTS` alone makes revision cleanup safe. Signing secrets never appear in immutable revisions or event targets; URLs, which may carry receiver tokens, are confidential configuration (see [destination security](#7-signing-and-destination-security)).

### 5. Fanout, Worker execution, and locks

Add `LifecycleEventFanoutService` and `WebhookDispatcher` hosted services and focused repository/service ports. Do not create fake workflow/node identities or engine job kinds.

Fanout uses short transactions: select pending events `FOR UPDATE SKIP LOCKED`, insert deliveries with `ON CONFLICT` on `(EventId, SubscriptionId)`, and mark fanout complete in the same transaction. A crash commits all effects or none. Do not rely on a highest-seen ID cursor: source transactions can commit out of order.

```text
pending / retryScheduled -> delivering -> succeeded
                                     -> retryScheduled
                                     -> failed
expired delivering lease -> retryScheduled (previous outcome unknown)
failed -> pending (single or bulk retry, new retry generation)
pending / retryScheduled / failed -> discarded (single or bulk discard, or archive)
delivering -> discarded (archive fences the lease; remote outcome may be unknown)
```

Pause, including automatic pause, is a subscription gate, not a destructive delivery rewrite; test deliveries ignore it. `succeeded`/`discarded` are settled; `failed` remains recoverable and retention-protected.

1. Acquire only available capacity in a short transaction. Lock a subscription's dispatch-state row, skip the subscription while it is backing off, and claim due deliveries up to its maximum concurrency minus its valid leases (one probe when backoff ends). Check deadline/budget, create attempts, and assign random lease tokens plus monotonically increasing lease generations, using the PostgreSQL clock and `SKIP LOCKED`. Visit subscriptions in least-recently-acquired order so one busy subscription cannot starve the others.
2. Release connections/locks before DNS, signing, and HTTP. Recheck state/key safety in a short prepare-send transaction and record send intent before network I/O. A pause or delivery shutdown before send intent records `abortedBeforeSend`, releases the lease, and consumes no attempt budget; archive discards it. Budget counts send intents, including unknown outcomes. Permanent key/configuration failure becomes failed. Pause/archive/revoke cannot recall an already-sent request; test and document that race.
3. Send the captured destination and immutable body with current subscription signing keys. Retry does not move to a later URL revision.
4. Finalize only if lease token/generation still match and the lease is unexpired by the database clock, then update the dispatch state's failure count or backoff. Stale Workers cannot overwrite newer outcomes. A crash after remote acceptance can cause duplicate receipt.
5. Heartbeat while ownership remains valid; lease loss cancels local I/O. Shutdown stops acquisition, drains briefly, then cancels. Recover abandoned attempts as `outcomeUnknown`, not proof that nothing was sent.

Preserve runtime lock order: instance -> gateway state -> token -> MI execution -> user task. Capture runs at commit, after every runtime lock its transaction takes, and takes no further row locks. Management/dispatch/cleanup never lock runtime rows. For webhook rows locked together, use subscription -> dispatch state -> signing key (if needed) -> delivery -> attempt. The dispatcher normally locks only dispatch state, so delivery never blocks management edits; automatic pause and resume take the subscription row first. Fanout locks events and inserts deliveries without subscription write locks; retention conditionally deletes/skips busy rows rather than reversing orders. Prove the FK, pause, and archive paths follow this rule with PostgreSQL concurrency tests.

Archival first commits the subscription gate, then a bounded coordinator discards outstanding deliveries, invalidates their lease generations, and closes send-intent attempts as `outcomeUnknown` (or `abortedBeforeSend` if no intent existed). Late finalizers fail the fence; late fanout remains discarded. Continue this coordinator/fanout while HTTP delivery is disabled so archival can finish.

Initial operational defaults: 16 HTTP sends per Worker, per-subscription maximum concurrency 4 (settable from 1 to 16), fanout batch 100, poll interval 5 seconds, HTTP timeout 15 seconds (the low end of the Standard Webhooks recommendation), lease 60 seconds, heartbeat 15 seconds. Enforce per-subscription concurrency under the dispatch-state row lock, not just a process semaphore. An expired request may still reach the receiver while recovery starts: this is not strict remote serialization. Give webhook sends their own concurrency so dead receivers do not starve timers/call results. Update `WorkerConnectionPool` budgets/tests for dispatcher/fanout capacity. No database connection remains held during remote I/O.

Throughput targets, measured and recorded in phase 5 before release: with the defaults and a receiver answering in 100 ms, one subscription sustains at least 25 deliveries per second and one Worker at least 100 across subscriptions; also record database transactions per delivery. Ordering is not promised, so nothing requires one request at a time. That limit would cap a subscription near 18,000 deliveries an hour at 200 ms, so the 400,000 or more events of a 200,000-instance `InstanceLoadTest` run would take over 22 hours to deliver.

### 6. Acknowledgement, retry, and receiver behavior

Send `POST` with JSON UTF-8 content type, fixed user agent, and the Standard Webhooks headers in [signing](#7-signing-and-destination-security). Receivers acknowledge only after durable acceptance; `202` is valid when the receiver owns a durable queue.

| Outcome | Action |
| --- | --- |
| Any 2xx response headers | Succeeded; ignore response body; reset the subscription's failure count. This proves HTTP acceptance, not downstream completion. |
| Network failure, timeout, 408, 425, 429, 5xx | Retry within budget; count toward subscription backoff. |
| 3xx | Fail `redirect_refused`; never follow redirects. |
| 410 | Fail `receiver_gone` and pause the subscription automatically, as Standard Webhooks advises. |
| Other 4xx | Fail as receiver rejection; require operator recovery. |
| Destination policy denial, invalid certificate, unusable signing configuration | Fail with a safe specific code; never weaken security to recover. |
| Shutdown/lost lease | Unknown outcome, recovered using the lease fence. |

Allow 12 send intents per retry generation and a 72-hour generation deadline, following the Standard Webhooks guidance to retry over several days. Set `GenerationStartedAt` on the first prepare-send intent using the database clock and `DeadlineAt` to that time plus 72 hours. Waiting before a first send does not age this budget. After starting, pause, backoff, delivery shutdown, and Worker downtime do not stop the clock. Recheck exhaustion at acquisition and prepare-send; an expired generation becomes failed without another request, even if it was previously scheduled within budget.

Before retry `n`, wait the `n`th of these delays, multiplied by a random factor between 0.9 and 1.1 as `JobDispatcher` does: 10 seconds, 1 minute, 5 minutes, 30 minutes, 1 hour, 2 hours, 4 hours, 8 hours, 12 hours, 16 hours, and 20 hours. They total about 64 hours and at most 70 with jitter, so the twelfth send fits inside the deadline. For 429/503, honor valid `Retry-After` as a lower bound capped at 24 hours; fail if scheduling would exceed the generation deadline. Persist chosen times so restart does not recalculate them. Manual retry clears generation start/deadline until its first send intent; lifetime attempt history remains unchanged.

Receiver backoff keeps a subscription-wide outage from spending every delivery's budget. Five consecutive retryable outcomes, or a 429/503 with a valid `Retry-After`, start backoff: acquisition holds all of the subscription's deliveries, consuming no attempts, until the backoff ends (30 seconds, doubling to a 1-hour cap while probes fail, or the `Retry-After` time), then sends one probe at a time. A 2xx ends backoff. After 72 hours of continuous backoff without a success, the subscription is paused with reason `receiverUnavailable`, following the Standard Webhooks advice to disable an endpoint that fails consistently. Automatic pauses are audited as `system`, hold captured work, and do not backfill on resume.

Manual retry retains the event/delivery IDs, body, captured destination, and old attempts. It increments `RetryGeneration` and resets that generation's budget/deadline. Require an active subscription, usable signing configuration, retained failed delivery, and matching revision. Success, discard, archive, and expired data cannot be replayed through this operation. Stale/double-click requests cannot enqueue multiple generations.

Bulk retry and discard take a subscription plus the delivery-list filters (type, family, instance, destination revision, failure code, occurrence range) and act on at most 1,000 matching deliveries per request, oldest first, under the per-delivery rules: retry applies only to retained failed deliveries of an active subscription with usable keys; discard applies to pending, scheduled, and failed deliveries and skips active leases. Each request returns counts and whether more deliveries match, writes one audit entry, and is safe to repeat.

Receivers deduplicate atomically by `webhook-id`, the event ID, before side effects; every subscription and attempt delivering an event uses the same value. Keep deduplication records for the agreed operator replay horizon: automatic retries span 72 hours and retained failures can be retried manually later, so v1 promises no short universal deduplication TTL. Arrival order, event timestamps, and IDs do not establish workflow ordering. Retrieve current state through the [callback routes](#receiver-callbacks); a webhook is not an access token.

### 7. Signing and destination security

Sign deliveries as the [Standard Webhooks specification](https://github.com/standard-webhooks/standard-webhooks/blob/main/spec/standard-webhooks.md) defines for symmetric signatures, so receivers can verify with existing libraries. The primitive is [.NET HMACSHA256](https://learn.microsoft.com/en-us/dotnet/api/system.security.cryptography.hmacsha256?view=net-10.0).

```text
webhook-id: <event UUIDv7, canonical lowercase>
webhook-timestamp: <UTC Unix seconds for this attempt>
webhook-signature: v1,<Base64 HMAC-SHA256 with the current key>
```

Sign the UTF-8 bytes of `webhook-id`, a full stop, `webhook-timestamp`, and another full stop, followed by the exact body bytes. Each attempt has a fresh timestamp and a stable body and ID. During rotation overlap, the signature header carries a second `v1` entry made with the previous key, separated by one space. Key IDs are not sent; attempts record which keys signed. Fix golden vectors and verify them with an independent Standard Webhooks implementation in tests.

Receivers check that the body's `eventId` equals `webhook-id`, reject timestamps outside their tolerance (document a five-minute default), verify at least one accepted secret with constant-time comparison, then deduplicate durably. Verify raw bytes before JSON reserialization; timestamp checking does not replace deduplication.

Secret management:

- Generate 32 random bytes on create/rotate and return them once as a `whsec_`-prefixed Base64 secret plus key ID. No secret-read route exists; a lost response requires another rotation. Create subscriptions paused so the receiver can be configured and tested before activation.
- Rotation installs a current key and optional previous key overlap: default one hour, maximum 24 hours. Sign with both during overlap. A second rotation during overlap returns 409 unless the previous key is revoked first. Reads expose IDs/status/expiry only.
- Retries use current signing keys; delayed work does not hold retired keys forever. Operators must provision keys at every captured destination with outstanding work. A different receiver should use a new subscription and archive the old one.
- Support previous-key revocation and emergency pause. Already-started requests may still contain old signatures. Never log/store secrets in DTO string representations, telemetry, browser storage, or generic engine settings.
- Add `IWebhookSecretProtector` with a dedicated ASP.NET Data Protection application name, `Flowbit.Webhooks`, shared by API and Worker. Persist its key ring in `webhook_data_protection_keys` (through `PersistKeysToDbContext` or an equivalent `IXmlRepository`) and require `ProtectKeysWithCertificate`: API and Worker share the ring without a shared volume, and database backups hold only wrapped keys. Unlike [UiAuthentication.cs](../../Flowbit/src/Flowbit.Ui/Auth/UiAuthentication.cs), never fall back to DPAPI or ephemeral keys. Production needs restricted certificate permissions and tested backups, following [Microsoft's Data Protection configuration guidance](https://learn.microsoft.com/en-us/aspnet/core/security/data-protection/configuration/overview?view=aspnetcore-10.0).
- Protection-key rotation differs from receiver-key rotation. Preserve the certificate across restore/upgrades: losing it makes every subscriber secret unrecoverable, and every subscription must then be rotated. Invalid key configuration blocks webhook readiness/activation; never silently generate replacement subscriber secrets.

Use a dedicated HTTP client, not `IServiceTaskInvoker`. Existing service-task transport lacks the required reusable DNS/IP policy. Following the threats in [OWASP's SSRF prevention guidance](https://cheatsheetseries.owasp.org/cheatsheets/Server_Side_Request_Forgery_Prevention_Cheat_Sheet.html), implement:

- Absolute HTTPS URLs, maximum 2,048 characters, with no userinfo or fragment. Query strings are allowed, because tokenized catch URLs such as Logic Apps and Power Automate carry their token there. Port 443 only, unless an operator allowlist entry names the host and port. No arbitrary credential headers, cookies, default credentials, redirects, ambient proxy, or certificate bypass.
- Treat the path and query as confidential receiver configuration. Only subscription and delivery reads for settings administrators return the full URL; logs, metrics, audit entries, and error text use scheme, host, and port.
- Save-time host validation and connection-time IPv4/IPv6 checks. Block loopback, private, link-local, unspecified, multicast, metadata, and other non-public addresses, including mapped/alternate forms. Connect to a validated address while retaining the hostname for TLS/SNI; an earlier DNS check followed by uncontrolled resolution is insufficient.
- Operator-only host/port/CIDR allowlists for internal HTTPS receivers. Subscription admins cannot edit this host policy. Development fixtures can explicitly permit their own loopback HTTP address; reject that exception outside Development.
- Total timeout covering DNS/connect/headers, bounded response headers (16 KiB initially), normal TLS, bounded connection lifetime, and no response-body buffering/logging. Re-evaluate addresses on new connections.
- Logs contain IDs, durations, status codes, and safe reason codes, not destination paths or queries, signature values, keys, receiver bodies, or exceptions containing them.

### 8. API contracts and authorization

Add `WebhookEndpoints.cs`, DTOs, management/query services, and OpenAPI descriptions. Every route uses authentication plus `SettingsAuthorizationFilter`, including reads and rotation. Resolve canonical actors for audit. Optional workflow keys must reference an existing family at configuration time; this filter does not grant ordinary workflow managers webhook authority.

| Route | Contract |
| --- | --- |
| `GET /api/webhook-event-types` | Supported types, schema version, descriptions, nullable entity fields, and the read-only capture switch state. |
| `GET /api/webhook-subscriptions` | Paged metadata/configuration, revision, key status, receiver health, aggregate queue counts; no secrets. |
| `POST /api/webhook-subscriptions` | Create paused subscription/configuration/key; 201/Location and one-time secret, `Cache-Control: no-store`. |
| `GET /api/webhook-subscriptions/{id}` | Detail, receiver health, and safe current/historical destination metadata. |
| `PUT /api/webhook-subscriptions/{id}` | Replace name/description, URL, event set, optional family, and maximum concurrency with `expectedRevision`; immutable revision for changed delivery configuration. State changes use the routes below. |
| `POST /api/webhook-subscriptions/{id}/pause` | Pause without an expected revision, because pausing is always safe; repeating it changes nothing. |
| `POST /api/webhook-subscriptions/{id}/resume` | `expectedRevision`; activates a paused subscription, including after an automatic pause, and clears backoff. |
| `POST /api/webhook-subscriptions/{id}/test` | Queue one signed `webhook.test` event for this subscription only, delivered even while paused; 202, or 409 while a test is outstanding. |
| `POST /api/webhook-subscriptions/{id}/rotate-secret` | `expectedRevision`, `overlapSeconds`; one-time secret/key ID with no-store. |
| `POST /api/webhook-subscriptions/{id}/revoke-previous-secret` | Revision-protected retirement of previous key. |
| `POST /api/webhook-subscriptions/{id}/archive` | Revision-protected irreversible archive; 202 while bounded discard work settles. |
| `POST /api/webhook-subscriptions/{id}/deliveries/retry` | Bulk retry of matching failed deliveries, at most 1,000 per request; returns counts and whether more match. |
| `POST /api/webhook-subscriptions/{id}/deliveries/discard` | Bulk discard of matching pending, scheduled, and failed deliveries, skipping active leases; same limit and response. |
| `GET /api/webhook-deliveries` | SQL-paged filters: subscription, event ID, type, family, instance, destination revision, state, failure code, occurrence dates. |
| `GET /api/webhook-deliveries/{id}` | External event body, destination revision, state/budget, safe failure, retry eligibility; no internal recipients. |
| `GET /api/webhook-deliveries/{id}/attempts` | Paged timing/status/key IDs/unknown outcomes; no response body. |
| `POST /api/webhook-deliveries/{id}/retry` | Matching `expectedRevision`; queue one new generation, return 202 and state. |
| `POST /api/webhook-deliveries/{id}/discard` | Matching `expectedRevision`; settle queued/failed work and audit. An active lease returns 409 unless archive is fencing it. |

Validation: name 1–200 characters, description at most 1,000, validated URL, supported nonempty event set, nullable exact workflow key, maximum concurrency from 1 to 16 (default 4), positive integer expected revision. Bulk filters follow the delivery-list rules. The server generates keys; generic settings cannot expose/edit them.

Use existing paged response conventions: page 1, page size 50, maximum 200, exact database counts. Sort subscriptions by creation time/ID descending, deliveries by occurrence time/ID descending, attempts by lifetime number ascending. `from` is inclusive, `to` exclusive. Reject invalid filters/unsupported sorts rather than ignoring them.

Errors follow existing exception mapping: 400 invalid input, 401 unauthenticated/invalid actor, 403 missing settings authority, 404 unknown resource, 409 stale revision/state/capacity/rotation/outstanding-test conflict, and 503 when key protection is unavailable for create, rotate, or test. Give safe actionable text and consistent OpenAPI response types. No unauthenticated test endpoint, historical export, or automatic mutation replay is introduced.

Archived subscriptions reject edits, rotation, pause/resume, tests, and reactivation with 409; repeat archive may return the existing archived state without new work. Only safe reads and the outstanding archival coordinator remain available.

#### Receiver callbacks

Bodies carry identifiers only, so receivers read details through existing authorized routes with an ordinary API access token: an OpenID Connect client-credentials token whose identity claim resolves through the normal actor rules and that carries the needed roles, or a dedicated Local-mode account. The developer guide's receiver walkthrough documents this setup.

| Event | Route | Authority |
| --- | --- | --- |
| `instance.*` | `GET /api/instances/{id}` | Any authenticated actor. |
| `task.*` | `GET /api/user-tasks/manage?taskId=` | One of the task version's `taskAssignmentRoles`. |
| `task.*` | `GET /api/task-distribution/workflows/{workflowKey}/tasks?taskId=` | The family's `taskDistribution` client credentials. |
| `task.*` | `GET /api/node-executions/{nodeExecutionId}` | `NodeExecution.RequiredRole` or the version's `taskAssignmentRoles`. |
| `delegation.granted` | `GET /api/user-delegations/manage?delegationId=`; INT-01 adds this exact filter to the existing search. | `Delegation.AdminRoles`. |
| `incident.opened` | `GET /api/incidents/{incidentId}` | `WorkflowJobs.RequiredRole` (default `admin`). |

### 9. Flowbit.Ui

Add `/webhooks`, a delivery detail route, navigation, typed `WorkflowApiClient` methods, and page-state helpers. All data/actions use authenticated HTTP; no repository, Worker, or key-ring access from the UI. The editor remains unchanged.

- List subscriptions with destination, state and automatic pause reason, receiver health/backoff, filters, queue/failure counts, latest result, and refresh. Show old destination revisions when outstanding work still targets them. Warn when the capture switch is off.
- Create/edit uses labeled URL, event checkboxes, optional family, maximum concurrency, and description. Pause/Resume and Send test event are separate buttons. Explain pause gaps, no resume backfill, future-only URL edits, and that a test is delivered while paused.
- Creation/rotation has a one-time secret panel and copy/dismiss controls. Keep secrets only in transient component state; clear on dismissal/navigation/disposal/permission loss. No local storage, URL secrets, or secret-bearing screenshots.
- Deliveries expose filters, paging, attempts, safe failure, next time, captured destination, and Retry/Discard, plus bulk Retry/Discard for the current filters behind a confirmation that states the matching count. Prevent duplicate clicks; 409 refreshes current state without automatically replaying requests or overwriting edits.
- Archived subscriptions remain inspectable. Explain in-flight completion after pause/archive and the limits of HTTP acknowledgement.
- Cover empty/loading/error/403 states, keyboard/focus behavior, and narrow widths. Manual refresh only; UI-05 push is separate.

### 10. Retention, operations, and rollout

Add `lifecycleEvents` and `webhookDeliveries` to `RetentionCategories`, migration seeds, repository execution order/preview/protections, UI labels, API docs, and tests. Both default to keep forever, like existing history categories; stored volume follows subscribed traffic, because untargeted transitions store nothing.

- Delivery retention ages from `SettledAt`: delete attempts before succeeded/discarded deliveries. Failed/pending/retrying/leased/paused work remains protected; failure must be retried or explicitly discarded first.
- Event retention ages from `OccurredAt`, not instance finish time. Delete only fanout-complete events with no remaining deliveries. Future notification consumers must add durable protection before consumption.
- Keep management audit, immutable revisions, and the webhook key ring indefinitely in v1 and include their growth in sizing. Remove expired/revoked secret material only after overlap and in-flight protection ends. Archival retires both current and previous keys after outstanding work is fenced; persist a removal-not-before time beyond the last issued lease expiry plus HTTP timeout, since a fenced request may still be in flight. Retain key IDs/timestamps as audit metadata.
- Use existing bounded cleanup and separate pool. Retry/cleanup must share row-level eligibility protection. A pre-upgrade retention run may finish its old category list; the next run includes new categories.
- Webhook retention never sets `HistoryPrunedAt`, prunes workflow history, alters permanent idempotency/message receipts, or changes reactivation eligibility. Preview reports eligible/protected rows.

Metrics cover recorded events, capture switch state, pending fanout/oldest age, queue depth, oldest due time, attempts/outcomes, latency, failure count, receiver backoff and automatic pauses, lease recovery, and cleanup. Labels are bounded event/state/reason sets, not IDs/URLs/actors. Logs may correlate event/delivery IDs without payloads. Receiver outage affects delivery health/backlog, not host liveness. The API returns 503 for key routes when key protection is unusable; the Worker reports webhook problems as degraded on `/health/ready` without failing job processing. Provide runbooks for backlog, receiver backoff, key recovery, failures, trigger defects (turning capture off is the emergency stop), and database growth.

Rollout:

1. Apply the additive migration with capture and delivery off. It creates tables, functions, and triggers; `CREATE TRIGGER` briefly blocks writes to the four source tables, so use the normal migration-first window. There is no historical event backfill.
2. Configure the webhook certificate and network policy on API and Worker; update compose/env/mounts. Back up the database and certificate.
3. Upgrade every API/Worker, then UI. Capture needs no new binaries, but management and delivery do; the Worker's `DatabaseSchemaGate` already waits for the migration.
4. Create a paused subscription for a synthetic receiver, send a test event, activate it, run `SELECT flowbit.set_lifecycle_event_capture(true, '<operator>');`, enable delivery, and verify a real lifecycle occurrence. No traffic drain is needed: every writer, upgraded or not, is captured once the switch commits.
5. Check signatures, deduplication, retries, backoff, bulk recovery, backlog, and retention preview before production activation. Delivery shutdown preserves capture/queued work.
6. Rollback stops delivery and preserves tables/keys; older binaries keep running with the triggers in place. Turning capture off is reserved for a trigger defect and leaves an explicit gap. Destructive down migration is not routine rollback for retained/pending data.

### 11. Implementation phases and completion gates

Keep the capture switch and delivery off until end-to-end release gates pass. Each phase includes tests and owning documentation.

| Phase | Work | Exit gate |
| --- | --- | --- |
| 1. Contracts/inventory | Pin the catalog, trigger transition rules, golden bodies, Standard Webhooks vectors, state/retry/backoff rules, DTOs, limits. | Every producer path mapped to its row change and expected events; representative payloads/signature vectors fixed. |
| 2. Storage/capture | Migration, entities/repositories, capture functions and triggers, capture switch, immutable revisions, protected keys and key ring, audit. | Upgrade populated database; trigger, rollback, and concurrency constraints pass on real PostgreSQL. |
| 3. Producer coverage | Prove every API/Worker/raw SQL path, set-based MI, and `ON CONFLICT` insert, with no application producer code. | Full producer matrix passes, including writes made outside the current repositories. |
| 4. API | Management, pause/resume, test events, rotation, paging, single and bulk recovery, the delegation filter, authorization, OpenAPI. | HTTP privilege/concurrency/secret-disclosure checks pass. |
| 5. Dispatch | Fanout, leases/fairness/concurrency, receiver backoff and automatic pause, signatures, SSRF transport, retries/attempts/manual recovery. | Real PostgreSQL and controlled receiver prove duplicate/crash/multi-Worker behavior and the throughput targets. |
| 6. Operations | Retention, pool budgets, telemetry, readiness, deployment/key-recovery/emergency-stop runbooks. | Cleanup races, old coordinator state, restore, outage isolation, and notification load pass. |
| 7. UI/release | Pages/client/navigation, browser acceptance, receiver walkthrough, final docs. | Evidence reviewed and every acceptance criterion met before Done. |

Phases 3/4 can proceed in parallel after phase 2 contracts settle; UI can proceed against phase 4 contracts while dispatch is completed. Producer coverage, SSRF, key restore, receiver backoff, and retention protection are first-release requirements.

### 12. Tests and verification

| Area | Required scenarios |
| --- | --- |
| Atomicity | Rollback and savepoint rollback remove source/event; a trigger error rolls back the source at commit; unchanged retries, capture off, the transaction-local `off` setting, and zero matching subscriptions store nothing. |
| Instances | Ordinary/message/timer/call start, immediate end, partial parallel end, terminate/error/cancel, reactivation (`instance.reactivated`, then its `task.created`) followed by another end, child completion independent of parent delivery. |
| Tasks | Direct/async-before activation, parallel/sequential MI, administrative completion of pending children (`task.created` then `task.completed`), cancellation through each listed path, pending cancellation with and without an assignee, activation and assignment in one update (event order), all assignment sources, A-to-B-to-A, delegated/admin completion. |
| Global events | Delegation per-family batch/rollback/both acceptance states, all three incident insert paths including set-based `ON CONFLICT DO NOTHING`, nullable instance and node. |
| Capture/fanout | Zero/multiple filters, concurrent edit/pause/archive, snapshot at commit, old URL, no backfill, capacity, bulk MI commit cost, notification only with targets, duplicate fanout, out-of-order commits. |
| Leases | Two Workers with per-subscription concurrency and fair acquisition, expiry/loss/stale completion, crash after receiver success, archive fencing, workflow cancellation preserving delivery. |
| Retry/receiver | Every HTTP class, 410 automatic pause, backoff start/probe/recovery, automatic pause after 72 hours, Retry-After holds/date/delta limits, schedule within the deadline including jitter, shutdown unknown outcome, single and bulk retry/discard with stale clicks and repeats, test events while paused, stable bytes/IDs. |
| Signing | Golden vectors checked by an independent Standard Webhooks verifier, UTF-8/body/header tampering, old timestamps, dual overlap/revocation, no keys in reads/logs/errors, multi-host decrypt, certificate loss and restore. |
| HTTP security | Allowed public/private targets, blocked IPv4/IPv6/mapped/link-local/metadata, DNS rebinding, non-443 ports with and without an allowlist entry, query strings never logged, redirect/TLS/proxy, slow/large headers, ignored body, cancellation. |
| API/UI | 401/403/permission loss, canonical audit actor, family filter not authority, 400/404/409/503, SQL pages/counts, one-time secrets, pause without a revision, OpenAPI agreement, callback routes with a service identity. |
| Retention/load | Protected fanout/leases/failures, delivery-before-event deletion, retry races, old runs, no `HistoryPrunedAt` change, pool limits, capture cost with 0/1/100 subscriptions and 1,000-child MI, notification commit serialization under `InstanceLoadTest`, delivery throughput targets. |

Use real PostgreSQL for transactions, triggers, locking, raw SQL, leases, and retention. Mock HTTP for isolated tests; use a fixture-owned local receiver for end-to-end transport/crash scenarios. No production endpoints, real credentials, or billed providers are needed.

Extend precedents including `NodeExecutionLifecycleCoverageTests`, `MultiInstanceBulkPersistenceTests`, `TaskAssignmentApiTests`, `UserDelegationApiTests`, `MessageStartApiTests`, `InstanceReactivationApiTests`, `CallActivityLifecycleTests`, `DurableJobRepositoryTests`, `WorkerDispatcherTests`, `WorkerConnectionPoolTests`, and retention/page-state suites. Measure capture with 0, 1, and 100 subscriptions and realistic MI fanout; record query count/write amplification/commit latency before raising limits. Assert no connections are held across receiver delays.

From repository root, normal regression commands work in PowerShell and Bash:

```text
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet
git diff --check
```

Follow publish/build/Chromium setup in the [smoke README](../../Flowbit/tests/Flowbit.BrowserTests/README.md) and [Worker acceptance README](../../Flowbit/tests/Flowbit.BrowserAcceptanceTests/README.md). Keep ordinary smoke Worker-free; delivery belongs in the separate acceptance suite. After that setup:

```text
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore
dotnet test Flowbit/tests/Flowbit.BrowserAcceptanceTests/Flowbit.BrowserAcceptanceTests.csproj -c Release --no-build --no-restore
```

In Chromium on localhost, use actual clicks/typing/keyboard to create paused, install a synthetic key, send a test event, activate, trigger a real workflow, inspect success/failure, retry singly and in bulk, rotate, pause/resume, edit URL with queued work, discard singly and in bulk, archive, and inspect retention. Exercise non-admin and lost-permission identities at 1440x900, 1024x768, and 390x844. Inspect console errors/warnings and screenshots without secret values; manually review layout/focus beyond smoke assertions.

Acceptance setup uses authorized HTTP and real Worker processing. Controlled receiver gates may delay/respond but must not inject application state, change database rows, or falsify API responses. Loopback exceptions are fixture-only. Report exact commands/results, browser/URLs, interactions, viewports, console result, screenshots, and unavailable checks separately.

### 13. Documentation deliverables

This planning change updates this page and backlog status/effort only. Update these owning pages as implementation ships; do not advertise planned endpoints as already available.

| Owning page | Required update |
| --- | --- |
| [API reference](../api-guide.md) | Routes/DTOs/filter/paging/roles/errors/concurrency, pause/resume/test/bulk routes, the delegation `delegationId` filter, and one-time secret responses. |
| [Developer guide](../developer-guide.md) | Catalog/boundaries, bytes and Standard Webhooks verification, deduplication/order/retries/backoff, callback routes and service identities, HTTP-only setup and receiver walkthrough. |
| [UI guide](../ui-guide.md) | Navigation, create/test/activate/edit, secret handling, pause gaps and automatic pauses, rotation, captured destinations, single and bulk recovery, archive. |
| [Deployment](../deployment.md) | Capture switch and emergency stop, migration trigger locks and the `flowbit.lifecycle_events` repair setting, certificate-wrapped key ring storage/restore, SSRF policy, pools, health/metrics, backlog/retention/rollback. |
| [Database schema](../database-schema.md) | Tables/columns/indexes/checks/FKs, capture functions/triggers and switch, retained revisions, target arrays without FKs, cleanup dependencies. |
| [Runtime reference](../../Flowbit/README.md) and [AGENTS.md](../../AGENTS.md) | Trigger-based capture invariants (a new lifecycle status or source table must update the capture functions), transaction/lock invariants, dedicated queue, nullable identities, Worker duties. |
| [Root README](../../README.md) | Shipped capability and guide links; screenshots only when rendered product changes make them misleading. |
| [Getting started](../getting-started.md) | Only if default startup/prerequisites change; runnable examples aligned in PowerShell/Bash. |
| [UI-13](ui-13-notification-center.md) and [backlog](README.md) | Shipped recipient contract, internal-target extension point, and retention obligations; mark INT-01 Done only with verification evidence. |

No node-reference, BPMN-support, authored example, or editor screenshot change is expected because workflow JSON/execution rules remain unchanged. Reassess if implementation expands scope. Link any new guide from [Documentation home](../index.md). Validate changed relative links/anchors and parse every JSON example before completing each phase.
