# EVENT-04G: Parallel signal delivery and a higher recipient limit

[Parent EVENT-04](event-04-signal-events.md) · [Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New events |
| Effort | M |
| Priority | Not ranked; seventh of eleven in the [EVENT-04 follow-up order](README.md#event-04-follow-up-subtasks) |
| Parent | [EVENT-04](event-04-signal-events.md) |
| Depends on | [EVENT-04](event-04-signal-events.md) (Done) |
| Related | [EVENT-04E](event-04e-signal-broadcast-expiry.md) |

## Summary

Deliver one large broadcast with several jobs at once, and raise the recipient limit above 100,000 once delivery keeps up.

Schedule this when a deployment's measurements show that delivery time is a problem.

## Why it adds value

- One job delivers one definition's recipients one after another. At the measured 20.4 deliveries per second, 10,000 recipients take about 8 minutes, and 100,000 would take more than 80.
- A signal that should stop work is weak when its last recipient hears it an hour later.

## First release acceptance criteria

- [ ] Several jobs can deliver one broadcast's recipients of one definition at the same time, up to a configured number. Each delivery still ends exactly once.
- [ ] Parallel jobs never deadlock and do not queue behind each other on one instance. The lock order of a delivery is unchanged.
- [ ] One broadcast cannot occupy every activity slot. Timers, service tasks, and other jobs keep a share.
- [ ] Cancel, retry of failed deliveries, lease expiry, and the recount stay correct with several jobs per definition.
- [ ] A recorded run on a named machine shows the gain in deliveries per second, and no cost for a workflow without signal events.
- [ ] The hard cap on recipients rises only with evidence that freezing and cancelling a broadcast of the new size stay inside the command timeout.
- [ ] The engine settings and deployment guides describe the new settings and how to size them.

## Later releases

- Fair delivery among several open broadcasts.
- A delivery queue separate from `workflow_jobs`.

## Current state

Verified on 2026-11-17: not implemented.

- [SignalBroadcastJobProcessor.cs](../../Flowbit/src/Flowbit.Service/Services/SignalBroadcastJobProcessor.cs) handles one job for one broadcast and definition. It loops over pages of 100 deliveries (`DeliveryPageSize`), with a new scope and transaction for each delivery, on the `activity` queue.
- A broadcast reaches at most `Signals.MaxSubscriptionsPerBroadcast` recipients (default 10,000, hard cap 100,000), and at most `Signals.MaxOpenBroadcasts` broadcasts (default 20) deliver at once ([engine settings](../engine-settings.md#signal-settings)).
- The [EVENT-04 performance run](event-04-signal-events.md#current-state) on 2026-11-16, on a two-core machine: a throw froze 10,000 recipients in 493 ms and 100,000 in 2,202 ms, cancelling 100,000 took 1,753 ms, and one job delivered 10,000 recipients in 498 s.

## Builds on

- Job leasing, exhausted-lease reconciliation, and the cursor paging of `ListPendingDeliveryIdsAsync`.
- The Worker's activity concurrency setting.
- The webhook delivery queue, as the model of a queue with its own concurrency and connection pool.

## Open questions

- Does parallel work stay on the `activity` queue, or does delivery get a queue and pool of its own, as webhooks have?
- Do jobs divide the deliveries by ID range, or does each job skip rows that another job has locked?
- Is the rate limited by the cost of one delivery transaction and not by the number of jobs? Measure before choosing a design.
- What limits the parallel jobs of one broadcast, and of all broadcasts together?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
