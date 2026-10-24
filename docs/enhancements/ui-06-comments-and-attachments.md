# UI-06: Comments and attachments

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Flowbit.Ui feature (with engine changes) |
| Effort | M–L |
| Priority | Not ranked |
| Depends on | None |
| Related | [UI-13](ui-13-notification-center.md), [UI-03](ui-03-rich-task-forms.md), [TASK-04](task-04-email-and-chat-send.md) |

## Summary

A discussion thread with @mentions on each instance and task, and file attachments, such as invoices and contracts, on instances, on tasks, or through a `file` variable type in forms.

## Why it adds value

- Document-centric processes keep files and discussion next to the work, with an audit trail.

## First release acceptance criteria

- [ ] Plain-text comments on an instance are attributed to their author and cannot be edited or deleted.
- [ ] Files can be attached to an instance within configured size and type limits.
- [ ] Comments and files are visible only to users allowed to see the instance, and adding them requires a permission decided in the plan.
- [ ] Retention policies cover comments and attachments.

## Later releases

- Comment threads on tasks.
- @mentions that notify people, after [UI-13](ui-13-notification-center.md).
- A `file` variable type in forms.
- A malware-scanning hook.

## Current state

Verified on 2026-10-24: not implemented.

- No comment or attachment entities exist; the only document handling is PDF text extraction for the AI assistant.

## Builds on

- Instance history and actor audit.
- [Retention policies](../ui-guide.md#manage-retention), extended with new categories.
- The authorization scopes of instance detail and task access.

## Open questions

- Storage: PostgreSQL or a configurable blob store?
- The instance-detail API requires authentication but does not apply personal task visibility. Which narrower permission should adding comments and files require?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
