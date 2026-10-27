# UI-15: Form layout, data options, and start-form discovery

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Flowbit.Ui feature |
| Effort | M |
| Priority | Not ranked |
| Depends on | [UI-03](ui-03-rich-task-forms.md) |
| Related | [UI-11](ui-11-localization-rtl.md) |

## Summary

The form scope left after [UI-03](ui-03-rich-task-forms.md): a `placeholder`, field order and sections, options taken from variables or workflow settings, an **Other value** entry beside fixed options, read-only fields that show instance values on a task form, conditional field visibility, and a start-form endpoint for callers without definition-administration access.

## Why it adds value

- Longer forms are easier to complete when fields are ordered and grouped, and a task form can show the values a decision depends on.
- Lists that change often, such as cost centers, need not be copied into every workflow version.
- People who start workflows without administering them can use the start page, and embedding applications can read start forms.

## First release acceptance criteria

- [ ] Start and action input declarations accept an optional `placeholder`, which forms show.
- [ ] A start-form endpoint returns the start events the caller may start, with their input declarations, without definition-administration access, and the start page uses it.
- [ ] Older definitions load unchanged, and the new metadata never affects execution.
- [ ] Verified in a real browser at desktop and phone widths.

## Later releases

- Field order and sections.
- Options taken from variables or workflow settings.
- An **Other value** entry beside fixed options, for lists that suggest values rather than close them. UI-03 forms offer only the listed options.
- Read-only display fields showing instance values on a task form.
- Conditional field visibility.

## Current state

Verified on 2026-10-26: not implemented.

- [UI-03](ui-03-rich-task-forms.md) added `label`, `helpText`, and fixed `options` to start and action inputs ([form presentation](../node-reference.md#form-presentation)). Fields appear in declaration order, and options are fixed in the definition.
- The start page reads start-event declarations through `GET /api/workflows/{id}`, which requires definition-administration access ([API reference](../api-guide.md#get-apiworkflowsid)).

## Builds on

- The shared `VariableInputField` component and `VariableInputConversion` in Flowbit.Ui.
- The editor's variable **Form** section and the shared presentation validation rules.

## Open questions

- Do order and sections belong on each declaration, or in a layout list on the owning start event or flow?
- Where do data-driven options resolve for a start form, which has no instance yet?
- Should the start-form endpoint also return start events the caller may not start, marked as unavailable?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
