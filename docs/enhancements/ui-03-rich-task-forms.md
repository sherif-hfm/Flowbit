# UI-03: Rich task forms

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Done |
| Category | Flowbit.Ui feature |
| Effort | M |
| Priority | Recommended order #2 |
| Depends on | None |
| Related | [UI-06](ui-06-comments-and-attachments.md), [UI-11](ui-11-localization-rtl.md); [UI-15](ui-15-form-layout-and-data-options.md), which continues this scope |

## Summary

Start and action forms that use controls suited to each value's data type, plus optional presentation metadata on variable declarations: `label`, `helpText`, and fixed `options` first; later `placeholder`, field order and sections, options from data, and read-only display fields that show instance values on the task form.

## Why it adds value

- Every human workflow gets easier forms with fewer input errors. Today every input is a plain text box.
- Flowbit.Ui reads forms through the API, so the new metadata also reaches applications that embed Flowbit.

## First release acceptance criteria

The [plan](#plan) revised these criteria on 2026-10-26: options do not restrict the values the engine accepts, the date-time picker sends the browser's offset, and the instance-detail action and interrupt forms adopt the typed parsing that the other forms already use.

- [x] Start and action forms choose controls from the existing data types: a date picker for `date`, a date-time picker that adds the browser's explicit offset for `datetime`, a number input for `number`, and a true/false control that can stay blank for `boolean`. Arrays and `json` keep text entry.
- [x] For the same values, the start, task, and administrative forms submit what they submit today, and a blank field is still omitted. The instance-detail action and interrupt forms parse entries the same way as those forms.
- [x] Start-event and action (sequence-flow) variable declarations accept optional `label`, `helpText`, and a fixed `options` list; forms show them, and the editor's variable inspector edits them. The engine keeps accepting any value of the declared type.
- [x] Older definitions load unchanged, the API returns the new fields to every client, and changing only these fields never blocks a workflow-version change.
- [x] Verified in a real browser at desktop and phone widths.

## Later releases

This scope is recorded as [UI-15 Form layout, data options, and start-form discovery](ui-15-form-layout-and-data-options.md), together with a start-form endpoint for callers without definition-administration access.

- `placeholder`, field order, and sections.
- Options taken from variables or workflow settings.
- Read-only display fields showing instance values on a task form.
- Conditional field visibility.

## Current state

Verified on 2026-10-26: done. The first release acceptance criteria are met.

- `VariableModel` ([WorkflowModel.cs](../../Flowbit/src/Flowbit.Shared/Models/WorkflowModel.cs)) carries `label`, `helpText`, and `options`. `WorkflowDefinitionValidator` and the editor's save validation apply the [form presentation](../node-reference.md#form-presentation) rules, and `WorkflowVersionCompatibilityEvaluator` leaves the fields out of version contracts.
- [`VariableInputField`](../../Flowbit/src/Flowbit.Ui/Components/Shared/VariableInputField.razor) renders typed controls on the start page, task detail, instance-detail actions and interrupts, and the instance administrative panel. [`typed-inputs.js`](../../Flowbit/src/Flowbit.Ui/wwwroot/typed-inputs.js) reports rejected entries and the browser's offset with each input event. [Enter typed values](../ui-guide.md#enter-typed-values) describes the controls.
- The editor's variable inspector has a **Form** section, and [example user-tasks/03](../../examples/user-tasks/03-direct-assignee-expression.json) uses the fields.
- Coverage: `VariablePresentationModelTests`, `VariablePresentationApiTests`, `VariableInputFieldTests`, `TypedInputsScriptTests`, and the presentation cases in `DefinitionValidationTests`, `WorkflowVersionCompatibilityEvaluatorTests`, and `EditorRuntimeSmokeTests`, plus browser smoke scenarios [E9 and R9](../../Flowbit/tests/Flowbit.BrowserTests/README.md#what-is-covered): E9 at 1440×900 and 1024×768, and three R9 cases at 1440×900 and 390×844: one in Asia/Tokyo; one in Europe/Berlin that covers daylight-saving times, partly filled pickers, the mouse wheel, and the instance-detail action form; and one in America/New_York that covers a multi-instance interrupt, an instance administrative override, and the audit batch's declarations. After the second review's fixes on 2026-10-26, the solution suite (2,713 tests) and the full browser suite (69 cases) passed.
- Real-browser verification on a local stack at 1440×900 and 375×812 exercised the start, task, and instance-detail forms, a rejected number, and an editor save round trip, with no console errors. After the second review, a check at 1024×768 and 375×812 switched between two start events that declare the same input with different option captions, and started an instance with the option `1e-28`, which was stored exactly.

## Builds on

- The existing typed input contracts and server-side validation, which metadata must never replace.
- The editor's variable inspector.
- Node and flow `attributes` as a precedent for metadata that never affects execution.

## Open questions

The plan decided each question; see [scope and decisions](#1-scope-and-decisions).

- Should the engine reject a submitted value outside `options`, or are options presentation-only? **Decided:** presentation-only. Authors who need to restrict values keep using `validation`.
- Which time zone does the date-time picker use: the browser's, or an explicit choice? **Decided:** the browser's. The form sends the local date and time with the browser's UTC offset for that moment and shows the exact value before submission.
- Updates needed to the AI authoring guides and the portable skill. **Decided:** the node reference, developer guide, and BPMN support pages, which the assistant reads, and the portable skill's authoring guide describe the fields. The workflow schema is generated from the model, so it includes them without a separate change.

## Plan

Written on 2026-10-26, following [Writing a plan](README.md#writing-a-plan).

### 1. Scope and decisions

The first release gives start and action forms controls chosen by data type, and lets start-event and action variables carry a form label, help text, and fixed options. It changes no execution behavior.

- **Options are presentation-only.** Flowbit.Ui forms offer only the listed values, but the engine keeps accepting any value of the declared type, so other API clients can still send one. Like node and flow `attributes`, the new fields never affect execution or workflow-version compatibility. Authors who need to restrict values keep using `validation`, for example `reviewOutcome in ('approved', 'rejected')`.
- **Date-time values carry the browser's offset.** The picker shows local time in the browser's time zone. The form sends `yyyy-MM-ddTHH:mm:ss±HH:mm`, or `Z` when the offset is zero, using that zone's offset for the chosen moment, and shows the exact value before submission. A local time skipped when clocks go forward is rejected; a local time that occurs twice when clocks go back uses its first occurrence, with the summer-time offset.
- **One typed parser.** The start, task, and administrative forms already parse entries with `StartVariableInputParser`. The new controls produce the text a user would type, so their submissions are unchanged. The instance-detail action and multi-instance interrupt forms used a lenient converter that sent `json` as a string, split arrays into string arrays, and parsed numbers in the server's culture. They now use the same parser, as the [typed value rules](../ui-guide.md#enter-typed-values) already describe.
- **Forms covered.** The start page, task detail, instance-detail actions and interrupts, and the instance administrative override panel. The administrative batch page keeps its raw JSON payload.
- **AI guides.** The node reference, developer guide, and BPMN support pages, which the built-in assistant reads, and the portable skill's authoring guide describe the fields. The workflow schema is generated from the model.

Not in the first release: the [later releases](#later-releases) scope, and a start-form endpoint for callers without definition-administration access. Start-event declarations are readable only through `GET /api/workflows/{id}`, which requires that access.

### 2. Workflow JSON and model

A start-event or action variable declaration may add:

| Property | Rules |
| --- | --- |
| `label` | Text shown instead of the variable name. Trimmed; blank means none. At most 300 Unicode scalar values. |
| `helpText` | Plain text shown with the field; line breaks are kept. Trimmed; blank means none. At most 1,000 Unicode scalar values. |
| `options` | At most 100 `{ "value", "label" }` entries, only on a single (non-array) `string` or `number` variable. An empty list means none. |

An option's `value` has the variable's type:

- A string value must not be blank, must not start or end with whitespace, has at most 300 Unicode scalar values, and is unique ignoring case, because NCalc compares strings case-insensitively.
- A number value has at most 15 significant digits and 28 decimal places, so a JavaScript number in the editor and a `decimal` in forms both hold it exactly. It must fit a `decimal` and is unique by value, so `1` and `1.0` are duplicates.
- An option's `label` is optional, trimmed, and at most 300 Unicode scalar values; without one, forms show the value.

Process variables reject all three properties.

```json
{
  "id": 1,
  "name": "reviewOutcome",
  "dataType": "string",
  "isArray": false,
  "required": true,
  "label": "Review outcome",
  "helpText": "Record the outcome you agreed with the requester.",
  "options": [
    { "value": "approved", "label": "Approved" },
    { "value": "rejected", "label": "Rejected" }
  ]
}
```

- `VariableModel` gains `Label`, `HelpText`, and `Options`, each omitted when null, so older documents serialize exactly as before. A new `VariableOptionModel` holds `Value`, nullable so that a missing value is a validation error rather than a server error, and an optional `Label`. `VariablePresentationConstraints` holds the limits. The authoring schema requires an option's `value`.
- `WorkflowModelMigrator` trims labels and help text, turns blanks into null, and turns an empty `options` list into null. It never adds properties or changes option values. A legacy message start whose `variables` become output mappings loses the fields, because message starts have no form.
- The editor loader keeps the fields. Its fallback that reads `label` as the name of a variable without a `name` is unchanged; `label` is kept as presentation text only when `name` is present.

### 3. Engine, persistence, and migrations

No execution change and no database migration: definitions are stored as JSON, and administrative batch snapshots include the fields for new batches.

- `WorkflowDefinitionValidator` applies the section 2 rules to start-event, user-task, and flow input declarations and rejects the fields on process variables. The editor's save validation reports the same rules with the same messages.
- `WorkflowVersionCompatibilityEvaluator` leaves `label`, `helpText`, and `options` out of the node and flow contracts it compares. Changing only these fields therefore never blocks an in-place version change, also for an active multi-instance task or an open job.
- There is no lock-order or Worker impact. Upgrade every API, UI, and Worker replica before authoring the fields: an older API drops them when it saves a definition, and its strict validation and AI authoring reject them as unknown properties.

### 4. API contracts and authorization

No endpoint or DTO changes. Every response that carries variable declarations already returns the shared `VariableModel`, so the fields reach every client: workflow and instance detail, available flows for instances and user tasks, multi-instance interrupt flows, and administrative-action discovery and batch detail. Authorization is unchanged. The [API reference](../api-guide.md) documents the properties and the new option schema.

### 5. Editor and Flowbit.Ui changes

Editor assets in `Flowbit/src/Flowbit.Ui/wwwroot/editor/`:

- **Inspector.** Each start-event and action variable has a collapsible **Form** section with **Label**, **Help text**, and, for a single string or number, an **Options** list: value, optional label, and **Remove** on each row, and **Add option** up to 100. For another type that still has options, a note and **Remove options** replace the list. A process variable that has the fields shows a note and **Remove form fields**. Changing the type converts option values between numbers and text where possible, without moving focus. A number option typed with more precision than the section 2 limits stays as typed, so the editor never saves a changed value, and save validation reports it.
- **Save validation.** A new phase after the variable contracts reports the section 2 rules, only for fields that are present, so existing documents produce the same errors as before.
- **Export.** Saved JSON trims the fields and omits blank ones and empty option lists.

Flowbit.Ui:

- **`VariableInputField`**, a shared component, renders one declared value: the label or, without one, the variable name; the type, with the variable name when a label replaces it, so server messages that name the variable stay traceable; the required mark; help text; "Leave blank to use the default." when a default exists; and the control. Help text, hints, and errors are linked with `aria-describedby`, and an invalid entry sets `aria-invalid`.

  | Declaration | Control |
  | --- | --- |
  | `string` | Text box |
  | `string` or `number` with options | List with **Choose a value** and the options |
  | `boolean` | List with blank, **True**, and **False** |
  | `number` | Number box that accepts decimals |
  | `date` | Date picker |
  | `datetime` | Date-time picker in the browser's time zone, with the value that will be sent |
  | Arrays and `json` | Monospaced text area, full width |

- **Browser events.** `typed-inputs.js`, loaded after the Blazor script, registers a `typedinput` event. With each `input` event it reports whether the browser rejected the entry and, for a date-time, the browser's offset and time zone and whether the local time was skipped. The component stores the entry before any later click is handled. An entry the browser could not read, or a skipped time, shows an error and blocks the submission instead of being omitted. Picker inputs are not rewritten while the user types or the page refreshes.
- **Review fixes (2026-10-26).** A date or date-time started from empty and left partly filled raises no `input` event in Chromium, so it was silently omitted. A second event, `typedcommit`, re-reads a picker when focus leaves it. The mouse wheel also stepped a focused number box; the script now moves focus out of a typed number box when the wheel turns over it, so the page scrolls instead.
- **Second review fixes (2026-10-26).** A `decimal` rounded the option `1e-29` to `0`, and the editor's JavaScript numbers changed `9007199254740993`, so number options are now limited to 15 significant digits and 28 decimal places, and forms leave out an option outside those limits rather than offer a changed value. A list also refreshes its captions when another declaration of the same input changes only the captions, for example on another start event. The documentation now states that forms offer only the listed options.
- **Pages.** The start page, task detail, instance-detail actions and interrupts, and the instance administrative panel use the component. They keep their input identifiers and payload code; the instance-detail forms gain identifiers and labels linked to their inputs, and switch to the typed parser.
- **Layout.** Arrays and `json` span the form width; fields stack on phones.

### 6. Documentation

- [Node and property reference](../node-reference.md#variables-and-inputs), [developer guide](../developer-guide.md#collect-and-validate-variables), and [BPMN support](../bpmn-support.md): the fields, their rules, and that they never affect execution; for custom forms, the date-time convention.
- [Flowbit.Ui guide](../ui-guide.md#enter-typed-values): the controls, time zone handling, options, and the editor's Form section.
- [API reference](../api-guide.md): the new properties and the option schema.
- [Deployment](../deployment.md): upgrade every replica before authoring the fields.
- [AGENTS.md](../../AGENTS.md), the [portable skill's authoring guide](../../authoring/flowbit-authoring/references/authoring-guide.md), the [example catalog](../../examples/README.md) with an updated example, the [root README](../../README.md), the [runtime reference](../../Flowbit/README.md), and the [browser smoke suite](../../Flowbit/tests/Flowbit.BrowserTests/README.md).
- This page and the [backlog](README.md): status, criteria, and a new item for the later-release scope and start-form discovery.

### 7. Tests and verification

- **Model and migrator**: older JSON round-trips unchanged; the fields round-trip and normalize idempotently.
- **Definition validation**: accepted shapes and each rule; the authoring schema covers the new properties.
- **Version compatibility**: presentation-only changes keep the multi-instance and open-job contracts; a `required` change still blocks.
- **API** (PostgreSQL): every response listed in section 4 returns the fields, and older definitions gain no properties.
- **Editor (Jint)**: loader preservation, including the legacy name fallback; export canonicalization; save-validation parity; inspector editing with undo and redo; the pinned characterization arrays unchanged.
- **`typed-inputs.js` (Jint)** in a fixed time zone with daylight saving: offsets, skipped and repeated times, and rejected entries.
- **Flowbit.Ui component and page tests**: the control for each type, labels, help, accessibility attributes, and hints; stored values, including date-time composition and rejected entries; submissions through the typed parser identical to typed text; typed payloads from the instance-detail forms.
- **Browser smoke**: R9 at 1440×900 and 390×844 in a fixed time zone fills the start and task forms and reads the stored JSON back over HTTP. A second R9 case in Europe/Berlin covers skipped and repeated local times, partly filled dates and date-times, the mouse wheel over a number box, and the instance-detail action form. A third, in America/New_York, takes a multi-instance interrupt and an instance administrative override through typed controls and reads the audit batch's declarations back. E9 at 1440×900 and 1024×768 edits the Form section, checks that a too-precise number option blocks the save, saves, and reloads.
- [Real-browser verification](../../AGENTS.md#mandatory-real-browser-ui-verification) on a local stack at desktop and phone widths, with the console checked.
