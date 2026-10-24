# UI-06: Comments and attachments

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Flowbit.Ui feature (with engine changes) |
| Effort | M–L |
| Priority | Not ranked |
| Related | [UI-03](ui-03-rich-task-forms.md), [UI-05](ui-05-notification-center-live-refresh.md), [TASK-04](task-04-email-and-chat-send.md) |

## Summary

A discussion thread with @mentions on each instance and task, and file attachments, such as invoices and contracts, on instances, on tasks, or through a `file` variable type in forms.

## Why it adds value

- Document-centric processes keep files and discussion next to the work, with an audit trail.

## Current state

Verified on 2026-10-24: not implemented.

- No comment or attachment entities exist; the only document handling is PDF text extraction for the AI assistant.

## Builds on

- Instance history and actor audit.
- [Retention policies](../ui-guide.md#manage-retention), extended with new categories.
- The authorization scopes of instance detail and task access.

## Open questions

- Storage: PostgreSQL or a configurable blob store; size limits; a malware-scanning hook.
- Visibility: everyone who can see the instance, or limited to a task?
- Editable and deletable comments, or an immutable audit record?
- Mentions as notifications through [UI-05](ui-05-notification-center-live-refresh.md).

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
