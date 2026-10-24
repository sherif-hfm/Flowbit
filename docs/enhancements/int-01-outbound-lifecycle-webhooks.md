# INT-01: Outbound lifecycle webhooks

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Integration |
| Effort | M |
| Priority | Not ranked |
| Related | [UI-05](ui-05-notification-center-live-refresh.md), [TASK-04](task-04-email-and-chat-send.md), [TASK-02](task-02-external-task.md) |

## Summary

Subscriptions that deliver lifecycle events to external systems: instance started, completed, faulted, or cancelled; task created, assigned, or completed; and incident opened. Events are recorded in the same transaction as the change and delivered by the Worker with retries and signed payloads.

## Why it adds value

- CRM, BI, and notification systems learn about changes without polling the API.

## Current state

Verified on 2026-10-24: not implemented.

- No outbound event subscription or webhook delivery exists under `Flowbit/src`. External systems poll the instance, inbox, and task-distribution searches, and a workflow can call out only through its own REST service tasks.

## Builds on

- Transactional instance mutations, so an event row commits only with its change.
- Durable jobs with retries and incidents for delivery.
- Machine-credential management patterns for subscriber secrets, and retention policies for delivered events.

## Open questions

- Subscription management: API and Flowbit.Ui pages, scoped per workflow family or global.
- Signing and secret rotation, ordering guarantees, and payload content (identifiers only, or data as well).
- Should this, [UI-05](ui-05-notification-center-live-refresh.md), and [TASK-04](task-04-email-and-chat-send.md) share one event pipeline?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
