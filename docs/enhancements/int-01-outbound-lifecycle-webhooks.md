# INT-01: Outbound lifecycle webhooks

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Integration |
| Effort | M |
| Priority | Recommended order #6 |
| Depends on | None |
| Related | [UI-13](ui-13-notification-center.md), [UI-05](ui-05-live-refresh.md), [TASK-04](task-04-email-and-chat-send.md), [TASK-02](task-02-external-task.md) |

## Summary

Subscriptions that deliver lifecycle events to external systems: instance started, completed, faulted, or cancelled; task created, assigned, or completed; delegation offered; and incident opened. Events are recorded in the same transaction as the change and delivered by the Worker with retries and signed payloads. The same event record is the foundation for [UI-13](ui-13-notification-center.md) notifications; [UI-05](ui-05-live-refresh.md) live refresh does not need it.

## Why it adds value

- CRM, BI, and notification systems learn about changes without polling the API.

## First release acceptance criteria

- [ ] The events listed in the summary are recorded in the same transaction as the change that causes them.
- [ ] Administrators manage webhook subscriptions (URL, event types, optional workflow-family filter, and signing secret) through the API and Flowbit.Ui.
- [ ] The Worker delivers each event at least once with retries and signs every payload; payloads carry identifiers, not variable values.
- [ ] Failed deliveries are visible and can be retried.
- [ ] Retention policies cover recorded and delivered events.

## Later releases

- Payloads that include selected data.
- Ordering guarantees per instance.

## Current state

Verified on 2026-10-24: not implemented.

- No outbound event subscription or webhook delivery exists under `Flowbit/src`. External systems poll the instance, inbox, and task-distribution searches, and a workflow can call out only through its own REST service tasks.

## Builds on

- Transactional instance mutations, so an event row commits only with its change.
- Durable jobs with retries and incidents for delivery.
- Machine-credential management patterns for subscriber secrets, and retention policies for recorded events.

## Open questions

- The signing scheme and secret rotation.
- Which event fields [UI-13](ui-13-notification-center.md) needs, so notifications can reuse this record unchanged.
- Are subscriptions global, or scoped per workflow family by permission?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
