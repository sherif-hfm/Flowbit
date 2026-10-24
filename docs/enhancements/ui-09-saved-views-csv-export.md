# UI-09: Saved views and CSV export

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Flowbit.Ui feature |
| Effort | S |
| Priority | Not ranked |
| Related | [UI-02](ui-02-process-analytics-heatmap.md), [UI-10](ui-10-bulk-task-actions.md) |

## Summary

Per-user saved filter and sort combinations on My work, Instances, Task management, and Activity, and CSV export of the current results.

## Why it adds value

- Repeated searches take one click, and results reach spreadsheets without API scripting.

## Current state

Verified on 2026-10-24: not implemented.

- Flowbit.Ui has no saved views and no CSV export. Browser storage is used only by the shared editor.

## Builds on

- The POST search request DTOs: a saved view can be a stored request body.
- Existing paging and authorization, which an export must not bypass.

## Open questions

- Store views on the server per user, or in browser storage?
- Export limits (row count, streaming) and identical authorization to the search.
- Views shared with a team?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
