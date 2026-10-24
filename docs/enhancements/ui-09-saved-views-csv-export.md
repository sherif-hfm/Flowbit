# UI-09: Saved views and CSV export

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Flowbit.Ui feature |
| Effort | S |
| Priority | Not ranked |
| Depends on | None |
| Related | [UI-02](ui-02-process-analytics-heatmap.md), [UI-10](ui-10-bulk-task-actions.md) |

## Summary

Per-user saved filter and sort combinations on My work, Instances, Task management, and Activity, and CSV export of search results.

## Why it adds value

- Repeated searches take one click, and results reach spreadsheets without API scripting.

## First release acceptance criteria

- [ ] My work and Instances can save, apply, rename, and delete personal views of their filters and sorting, stored in the browser.
- [ ] My work and Instances export all matching results to CSV, up to a row limit, through the same authorized search.
- [ ] An export that reaches the row limit says so.

## Later releases

- Views for Task management and Activity.
- Views stored on the server that follow a user across browsers, and views shared with a team.

## Current state

Verified on 2026-10-24: not implemented.

- Flowbit.Ui has no saved views and no CSV export. Browser storage is used only by the shared editor.

## Builds on

- The POST search request DTOs: a saved view can be a stored request body.
- Existing paging and authorization, which an export must not bypass.

## Open questions

- The row limit.
- Which columns, including returned variables, an export contains.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
