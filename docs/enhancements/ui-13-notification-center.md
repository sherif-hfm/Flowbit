# UI-13: Notification center

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Flowbit.Ui feature (with engine changes) |
| Effort | M |
| Priority | Not ranked |
| Depends on | [INT-01](int-01-outbound-lifecycle-webhooks.md) |
| Related | [UI-05](ui-05-live-refresh.md), [TASK-04](task-04-email-and-chat-send.md), [UI-04](ui-04-task-due-date-and-priority.md), [UI-12](ui-12-task-escalation.md), [UI-06](ui-06-comments-and-attachments.md) |

## Summary

An in-app notification bell with an unread count, built on the lifecycle events that [INT-01](int-01-outbound-lifecycle-webhooks.md) records, such as a task assigned to you or a delegation offered to you. Later releases add more event kinds, preferences, and email.

## Why it adds value

- People learn about work meant for them without watching My work, and delegation offers stop going unnoticed.

## First release acceptance criteria

- [ ] Notifications are created from [INT-01](int-01-outbound-lifecycle-webhooks.md) events when a task is assigned to a user and when a delegation is offered to a user.
- [ ] A bell shows the unread count and a list, and users can mark notifications as read.
- [ ] A notification shows only what its recipient may already see, and links to the task or delegation.
- [ ] Retention policies cover notifications.

## Later releases

- Due-soon and overdue notifications, after [UI-04](ui-04-task-due-date-and-priority.md), and escalation notices, after [UI-12](ui-12-task-escalation.md).
- Incident notifications for administrators.
- Per-user preferences and email delivery ([TASK-04](task-04-email-and-chat-send.md)).
- A live unread count through [UI-05](ui-05-live-refresh.md) signals.

## Current state

Verified on 2026-10-24: not implemented.

- No notification code exists under `Flowbit/src`.
- Delegation offers appear only on My delegations, and Overview shows a My work count.

## Builds on

- The events recorded by [INT-01](int-01-outbound-lifecycle-webhooks.md).
- Retention policies.
- [UI-05](ui-05-live-refresh.md) change signals, for a live unread count in a later release.

## Open questions

- Tasks offered to a role pool: with an external identity provider, Flowbit validates tokens without a directory lookup, so it cannot list the users who hold a role. Is notifying explicit users enough, or should Local-mode deployments, which have a user directory, notify role members?
- Storage and read state per user.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
