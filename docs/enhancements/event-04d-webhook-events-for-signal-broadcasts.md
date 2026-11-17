# EVENT-04D: Webhook events for signal broadcasts

[Parent EVENT-04](event-04-signal-events.md) · [Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Integration |
| Effort | M |
| Priority | Not ranked; fourth of eleven in the [EVENT-04 follow-up order](README.md#event-04-follow-up-subtasks) |
| Parent | [EVENT-04](event-04-signal-events.md) |
| Depends on | [EVENT-04](event-04-signal-events.md) (Done), [INT-01](int-01-outbound-lifecycle-webhooks.md) (Done) |
| Related | [EVENT-04B](event-04b-signal-throw-and-end-events.md), [INT-02](int-02-webhook-verification-follow-ups.md) |

## Summary

Lifecycle webhook event types for signal broadcasts. A receiver learns that a broadcast finished, and with which counts, without polling.

## Why it adds value

- An integration that throws a signal wants to know when delivery ended and whether a delivery failed. Today it polls `GET /api/signal-broadcasts/{id}`.
- Operators can send "completed with issues" to their alerting through the webhook subscriptions they already have.
- Once workflows throw signals themselves ([EVENT-04B](event-04b-signal-throw-and-end-events.md)), no caller is waiting for the result, so an event is the only notice.

## First release acceptance criteria

- [ ] The event catalog adds at least one broadcast event, raised when a broadcast finishes as completed, completed with issues, or cancelled.
- [ ] Capture runs in a deferred constraint trigger on `signal_broadcasts`, gated by `Webhooks.CaptureEnabled`. It takes no lock and writes nothing when no active subscription matches.
- [ ] One finished broadcast produces one event, although recounts update its row many times.
- [ ] The body holds the broadcast's ID, name, status, and outcome counts. It never holds the payload, the thrower, a business key, or a result description, and every string in it is bounded by a column limit.
- [ ] Existing event types and bodies are unchanged. `LifecycleEventCatalog` and the capture function change together, and `LifecycleEventCaptureTests` covers the new rules.
- [ ] The **Webhooks** page offers the new types, verified in a real browser. The developer guide's event catalog, the API reference, and the database schema reference are updated.

## Later releases

- An event when a broadcast is thrown.
- An event for each failed delivery.
- Subscriptions that filter broadcasts by signal name.

## Current state

Verified on 2026-11-17: not implemented.

- `LifecycleEventTypes` in [WebhookDtos.cs](../../Flowbit/src/Flowbit.Shared/Dtos/WebhookDtos.cs) has eleven subscribable types for instances, tasks, delegations, and incidents, and none for signals ([event catalog](../developer-guide.md#event-catalog)).
- Capture exists only in the deferred constraint triggers on `workflow_instances`, `user_tasks`, `workflow_incidents`, and `user_delegations`.
- A delivery job that runs out of attempts opens an incident, which `incident.opened` already reports. A single failed delivery opens no incident and raises no event.
- A broadcast's row is updated by every recount while it delivers, so a trigger must react to the status change, not to every update.

## Builds on

- The capture functions and `LifecycleEventCatalog`, which build each body once in catalog order.
- The webhook delivery queue, signing, retries, and the **Webhooks** page, which need no change for a new type.
- The broadcast's status and counters, which already commit in one transaction under the broadcast's row lock.

## Open questions

- Is there one "finished" event that carries the status, or one type per outcome?
- A broadcast to all workflows belongs to no family. How does a subscription that is limited to workflow keys match it?
- Does the body name the scope when the scope is a business key ([EVENT-04A](event-04a-signal-scope-by-business-key-or-instance.md))? Event bodies never carry business keys today.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
