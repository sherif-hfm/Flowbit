# EVENT-04E: Signal broadcast expiry

[Parent EVENT-04](event-04-signal-events.md) · [Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New events |
| Effort | S–M |
| Priority | Not ranked; fifth of eleven in the [EVENT-04 follow-up order](README.md#event-04-follow-up-subtasks) |
| Parent | [EVENT-04](event-04-signal-events.md) |
| Depends on | [EVENT-04](event-04-signal-events.md) (Done) |
| Related | [EVENT-04B](event-04b-signal-throw-and-end-events.md), [EVENT-04G](event-04g-parallel-signal-delivery.md) |

## Summary

A throw may set an expiry time. Deliveries that are still pending at that time end as skipped with a reason, and the broadcast finishes.

## Why it adds value

- Some announcements are useless when they arrive late. A signal meant to interrupt work now should not act hours later, when a stopped Worker returns.
- Expired broadcasts stop counting toward the open-broadcast limit without an administrator cancelling each one.

## First release acceptance criteria

- [ ] A throw accepts an optional expiry. Without one, nothing expires, as today.
- [ ] A delivery that runs at or after the expiry ends as `skipped` with its own result code, writes nothing, and leaves the wait and its subscription in place.
- [ ] The expiry is compared with the database clock read after the delivery's locks are held, so a delivery that waited for a lock cannot act after the expiry.
- [ ] Once a broadcast has expired, its pending deliveries are settled and it finishes, without each recipient's instance being locked.
- [ ] Whether failed deliveries of an expired broadcast can be retried is defined and tested.
- [ ] The API and the **Signals** page show the expiry and the number of expired deliveries. A repeated `idempotencyKey` with a different expiry is a conflict.
- [ ] The guides say that an expired signal leaves a catch waiting for a signal that will not come, and recommend a timer boundary on such a catch.

## Later releases

- A default expiry for the deployment.
- An expiry authored on a throw event ([EVENT-04B](event-04b-signal-throw-and-end-events.md)).

## Current state

Verified on 2026-11-17: not implemented.

- Nothing expires. The [EVENT-04 decisions](event-04-signal-events.md#open-questions) left expiry out because it leaves instances waiting on a catch for a signal that was dropped.
- While the Worker is stopped, deliveries wait, and signals act when it returns. The open-broadcast limit (`Signals.MaxOpenBroadcasts`, default 20) then rejects new throws until a broadcast finishes or is cancelled ([deployment](../deployment.md#signal-events)).
- Cancelling a broadcast ends its pending deliveries as `cancelled` with `broadcast_cancelled`, under the broadcast's row lock.

## Builds on

- The cancel path, which already settles every pending delivery of one broadcast in one transaction.
- `SignalDeliveryResultCodes` in [SignalDtos.cs](../../Flowbit/src/Flowbit.Shared/Dtos/SignalDtos.cs) and the skipped outcome.
- The recount that runs in a transaction holding the broadcast's row.
- The `signalBroadcast.*` lifecycle webhook events of [EVENT-04D](event-04d-webhook-events-for-signal-broadcasts.md), raised by triggers on `signal_broadcasts` for each committed change to `completed`, `completedWithIssues`, or `cancelled`. A status or result that expiry adds must reach `capture_signal_broadcast_lifecycle()`, `LifecycleEventCatalog`, and its tests in the same change; until then the function ignores the new status, and an expired broadcast that finishes as one of the three existing statuses raises that status's event.

## Open questions

- Is the expiry an absolute time or a duration from the throw?
- Who settles expired deliveries: each delivery job when it reaches them, or one statement when the first job notices the expiry?
- Do expired deliveries count as skipped, or does a broadcast gain an expired counter and status?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
