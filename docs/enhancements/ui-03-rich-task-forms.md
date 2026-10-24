# UI-03: Rich task forms

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Flowbit.Ui feature |
| Effort | M |
| Priority | Not ranked |
| Related | [UI-06](ui-06-comments-and-attachments.md), [UI-11](ui-11-localization-rtl.md) |

## Summary

Optional presentation metadata on variable declarations: `label`, `helpText`, `placeholder`, `widget` (select, radio, checkbox, date picker, textarea, or number), `options` (fixed, or taken from a variable or setting), field order and sections, and read-only display fields that show instance values on the task form.

## Why it adds value

- Start and task forms become easier to use and produce fewer input errors.

## Current state

Verified on 2026-10-24: not implemented.

- `VariableModel` ([WorkflowModel.cs](../../Flowbit/src/Flowbit.Shared/Models/WorkflowModel.cs)) holds `name`, `scope`, `sharedKey`, `access`, `dataType`, `isArray`, `nullable`, `required`, `defaultValue`, and `validation`, but no presentation metadata.
- Start and task forms render text inputs; booleans, dates, and arrays are typed as text ([Enter typed values](../ui-guide.md#enter-typed-values)).

## Builds on

- The existing typed input contracts and server-side validation, which metadata must never replace.
- The editor's variable inspector.
- Node and flow `attributes` as a precedent for metadata that never affects execution.

## Open questions

- Should the engine enforce `options` as validation, or keep them presentation-only?
- Options from variables or workflow settings: resolved on the server when the form loads?
- Is conditional field visibility in scope?
- Updates needed to the AI authoring guides and the portable skill.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
