# UI-05: Notification center and live refresh

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Flowbit.Ui feature (with API changes) |
| Effort | M |
| Priority | Recommended order #5, with [TASK-04](task-04-email-and-chat-send.md) |
| Related | [TASK-04](task-04-email-and-chat-send.md), [INT-01](int-01-outbound-lifecycle-webhooks.md), [UI-04](ui-04-task-due-date-and-priority.md), [UI-06](ui-06-comments-and-attachments.md) |

## Summary

An in-app notification bell with read and unread state, and optional email, for events such as a task assigned to you, a delegation offered or accepted, a task due soon or overdue, and, for administrators, an incident opened. The API pushes changes so My work, instance detail, and Jobs & incidents refresh on their own.

## Why it adds value

- Users learn about new work without reloading pages.

## Current state

Verified on 2026-10-24: not implemented.

- No notification code and no push channel from the API to the UI exist under `Flowbit/src`.
- [Find your way around](../ui-guide.md#find-your-way-around) asks users to refresh after another actor or the Worker changes state, and [Jobs & incidents](../ui-guide.md#monitor-jobs-and-incidents) does not poll.
- The administrative-actions batch page polls every three seconds while a batch is active.

## Builds on

- The Blazor Server circuit for pushing updates to open pages.
- PostgreSQL `LISTEN`/`NOTIFY` in the API as a change signal.
- The AI assistant's streamed responses as a precedent for streaming from the API.
- Note: the UI calls the API only over HTTP, so pushes must originate in the API.

## Open questions

- Transport from the API to the UI: server-sent events, long polling, or SignalR?
- Notification storage, retention, and per-user preferences.
- Delivering `NOTIFY` signals with several API replicas.
- Email through the [TASK-04](task-04-email-and-chat-send.md) connectors or a separate system channel?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
