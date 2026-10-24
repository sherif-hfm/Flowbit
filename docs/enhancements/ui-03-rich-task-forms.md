# UI-03: Rich task forms

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Flowbit.Ui feature |
| Effort | M |
| Priority | Recommended order #2 |
| Depends on | None |
| Related | [UI-06](ui-06-comments-and-attachments.md), [UI-11](ui-11-localization-rtl.md) |

## Summary

Start and action forms that use controls suited to each value's data type, plus optional presentation metadata on variable declarations: `label`, `helpText`, and fixed `options` first; later `placeholder`, field order and sections, options from data, and read-only display fields that show instance values on the task form.

## Why it adds value

- Every human workflow gets easier forms with fewer input errors. Today every input is a plain text box.
- Flowbit.Ui reads forms through the API, so the new metadata also reaches applications that embed Flowbit.

## First release acceptance criteria

- [ ] Start and action forms choose controls from the existing data types: a date picker for `date`, a date-time picker that adds an explicit offset for `datetime`, a number input for `number`, and a true/false control that can stay blank for `boolean`. Arrays and `json` keep their current text entry.
- [ ] For the same values, submissions are identical to today's, and a blank field is still omitted.
- [ ] Start-event and action (sequence-flow) variable declarations accept optional `label`, `helpText`, and a fixed `options` list; forms show them, and the editor's variable inspector edits them.
- [ ] Older definitions load unchanged, and the API returns the new fields to every client.
- [ ] Verified in a real browser at desktop and phone widths.

## Later releases

- `placeholder`, field order, and sections.
- Options taken from variables or workflow settings.
- Read-only display fields showing instance values on a task form.
- Conditional field visibility.

## Current state

Verified on 2026-10-24: not implemented.

- `VariableModel` ([WorkflowModel.cs](../../Flowbit/src/Flowbit.Shared/Models/WorkflowModel.cs)) holds `name`, `scope`, `sharedKey`, `access`, `dataType`, `isArray`, `nullable`, `required`, `defaultValue`, and `validation`, but no presentation metadata.
- Start and task forms render text inputs; booleans, dates, and arrays are typed as text ([Enter typed values](../ui-guide.md#enter-typed-values)).

## Builds on

- The existing typed input contracts and server-side validation, which metadata must never replace.
- The editor's variable inspector.
- Node and flow `attributes` as a precedent for metadata that never affects execution.

## Open questions

- Should the engine reject a submitted value outside `options`, or are options presentation-only?
- Which time zone does the date-time picker use: the browser's, or an explicit choice?
- Updates needed to the AI authoring guides and the portable skill.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
