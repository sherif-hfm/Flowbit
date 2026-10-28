# EVENT-08: Business calendars

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Done |
| Category | New event capability |
| Effort | M–L |
| Priority | Next tier: deadline and timer follow-up 3 (move up if SLAs are counted in working days) |
| Depends on | [UI-04](ui-04-task-due-date-and-priority.md) |
| Related | [EVENT-01](event-01-expression-timers.md), [EVENT-09](event-09-cron-schedules.md), [UI-12](ui-12-task-escalation.md) |

## Summary

Named calendars with working days, working hours, holidays, and a time zone. Timer durations and task due-date durations can name a calendar, so a duration such as "3 working days" skips non-working time.

## Why it adds value

- Many SLAs are counted in working time. Wall-clock durations make deadlines wrong around weekends and holidays.

## First release acceptance criteria

The [plan](#plan) revised these criteria on 2026-10-27. Timer starts are included. A calendar in use cannot be deleted. A definition is checked against the calendars that exist. The last two criteria are new: one covers version switches, the other real-browser verification.

- [x] Settings administrators manage named calendars through `/api/business-calendars` and a **Business calendars** page in Flowbit.Ui. A calendar has a weekly schedule of working periods, holidays that can carry their own hours, and an IANA time zone.
- [x] A timer `timeDuration` can name a calendar and is then counted in working time. This applies to timer catches, timer boundaries, and timer starts. A user-task `dueDate` duration ([UI-04](ui-04-task-due-date-and-priority.md)) can do the same, whether fixed or from an expression.
- [x] Due times are computed and saved when the timer activates or the work item is created; editing a calendar does not move saved due times.
- [x] A calendar cannot be deleted while a published definition, a running instance, or an open job uses it. A definition is saved or published only when every calendar it names exists.
- [x] Definitions without calendars behave exactly as before. A timer's calendar is part of its version-switch contract, like its expression. Changing a task's due-date calendar never blocks a version change.
- [x] Daylight-saving transitions in the calendar's time zone are covered by tests.
- [x] Verified in a real browser at desktop and phone widths.

## Later releases

- Recomputing open due times after a calendar change.
- Importing holiday lists.
- Skipping holidays in cron schedules ([EVENT-09](event-09-cron-schedules.md)).
- Calendars on recurring `timeCycle` timers.
- A calendar picker in the editor, and calendar names offered to the AI assistant.
- A calendar chosen by an expression ([EVENT-01](event-01-expression-timers.md)).

## Current state

Verified on 2026-10-27: done. The first release acceptance criteria are met.

- **Model and rules.** `TimerDefinitionModel.Calendar` and `FlowNodeModel.DueDateCalendar` ([WorkflowModel.cs](../../Flowbit/src/Flowbit.Shared/Models/WorkflowModel.cs)) name a calendar. `BusinessCalendarRules`, `TimerDefinitionRules.TryParseWorkingDuration`, and `UserTaskDueRules` hold the grammar and messages that `WorkflowDefinitionValidator` and the editor's save validation share. `WorkflowDefinitionReadinessChecker` refuses a name that does not exist or differs in casing when a version is saved, published, or made the default, and in AI authoring validation.
- **Calculation.** `BusinessCalendar` and `BusinessCalendarTimeZones` implement the [working-time rules](../node-reference.md#working-time-durations) with .NET `TimeZoneInfo` and IANA ids. `BusinessCalendarResolver` loads each calendar once per request or Worker scope. Timer catches and boundaries resolve their due time on activation, and the Worker's timer-start reconciliation does the same; `timer_subscriptions."ScheduleCalendar"` records the name. The runtime repository resolves a task's working-time due date at the work item's `CreatedAt`, once for all multi-instance children.
- **Persistence.** Migration `AddBusinessCalendars` adds `business_calendars`, `workflow_definition_business_calendars`, and `timer_subscriptions."ScheduleCalendar"`, and seeds `examples.weekdays`. The [database schema](../database-schema.md#business-calendars) now has 55 tables and 112 foreign keys, matching a freshly migrated database.
- **API and UI.** The six [`/api/business-calendars`](../api-guide.md#business-calendars) operations are limited to settings administrators. The Flowbit.Ui [Business calendars](../ui-guide.md#business-calendars) page lists, adds, edits, previews, and deletes calendars. The editor's timer and user-task inspectors have **Business calendar** and **Due date calendar** fields with live validation. [Example timers/06](../../examples/timers/06-working-day-deadline.json) uses `examples.weekdays`.
- **Version switches.** `WorkflowVersionCompatibilityEvaluator` compares a timer's calendar with the persisted schedule, ignoring case, and leaves `dueDateCalendar` out of node contracts.
- **Tests.** New: `BusinessCalendarTests` (including daylight-saving gaps and repeated hours in custom, London, and New York zones), `BusinessCalendarRulesTests`, `BusinessCalendarApiTests`, `BusinessCalendarRuntimeTests`, and `BusinessCalendarsPageStateTests`, plus additions to the definition-validation, version-compatibility, database-schema, editor, example-catalog, and AI docs-view tests. The full `Flowbit.Tests` run had 3,001 tests: 3,000 passed, and the AI docs-view test failed because the new example raised its file counts. After that count was updated, it and the page-state tests passed. In the browser smoke suite, the new R11 (1440×900 and 390×844) and E11 (1440×900 and 1024×768) scenarios passed with R10, E10, and H10, 11 cases in all, with no console errors or warnings.
- **Real-browser verification** on a local stack at 1440×900 and 390×844 covered adding, editing, and previewing calendars (Friday 27 March plus `P1D` across the London clock change, `PT5H` across a weekend, `PT4H` on the half-day holiday, and the refused `P1W`); a workflow naming `examples.weekdays`, whose task was due Thursday 17:00 UTC after a Tuesday 17:44 start and whose boundary timer was due Friday 17:00 UTC; My work's due column; the editor's calendar fields, live errors, a blocked save, a save refused for a calendar that does not exist, a saved version that reloads, and the version's reference rows; and a viewer's role message. It found that the preview's start picker reverted every entry, because Blazor sends `datetime-local` values with seconds and the binding's minute format could not read them. It also found that the list summary read "0 calendars." when the list could not be loaded. The browser pane suppresses native confirmations, so the confirmed deletes were left to R11. The console showed no errors apart from reconnect attempts while the UI was restarted and one caused by a synthetic pointer event sent from the test tools.
- **Date input fix.** A review on 2026-10-27 found that a leading zero typed over a preview start or holiday date already entered cleared the whole field: the browser briefly reports no value, and `@bind` wrote that empty value back. Both inputs are now uncontrolled. They render a value only when the form loads and keep the text the browser reports, which `DateTimeLocalText` and `BusinessCalendarText.ParseDate` read. R11 now types both on the keyboard, including leading zeros over a date already entered, and passed at 1440×900 and 390×844. Run against the earlier page, it failed at both sizes: the retyped start read `0026-10-22T09:05` instead of `2026-01-02T09:05`. With the new page-state and `DateTimeLocalTextTests` cases, the full `Flowbit.Tests` run passed all 3,021 tests. A real-browser check at 1024×768 and 390×844 typed over a filled start and holiday date, removed a holiday row, and saved; each field kept every segment, and the console had no errors or warnings.
- **Review fixes.** A later review on 2026-10-28 found three more problems, each reproduced first:
  - **Timer starts.** A timer start that could not be scheduled was logged on every reconciliation pass, once a second by default, and never opened the incident this page promised. It now keeps a paused subscription with one `timer_schedule_failed` incident; retrying the incident schedules the timer start again or opens a new one. Validation also rejects a working duration longer than the 36,525 days the calculation searches, such as `P36526D`, which no calendar can count.
  - **Reload latest.** When reloading failed, the page reopened the calendar from the list it already had, which dropped the unsaved changes and the conflict. It now keeps the draft and the conflict until a reload succeeds.
  - **Digits.** Another script's digits, such as `17:0١` or `P١D`, passed the patterns but failed the invariant number parsers with an exception, an HTTP 500 for calendars, previews, and workflow saves. The working-period, duration, and date patterns now read ASCII digits only, as the editor does, so these are ordinary validation errors.

## Builds on

- Due-time computation for timer jobs (`DueAt` in [JobModels.cs](../../Flowbit/src/Flowbit.Service/Models/JobModels.cs) and [WorkflowEngineService.Jobs.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.Jobs.cs)).
- The task due dates saved by [UI-04](ui-04-task-due-date-and-priority.md).
- The settings pages' administration pattern: `Settings.RequiredRole` permission and optimistic concurrency.
- Shared-variable definition bindings and their lifecycle blockers. Calendar references and the deletion guard follow the same pattern.

## Open questions

The plan decided each question; see [scope and decisions](#1-scope-and-decisions).

- Storage: a new table, or workflow settings? **Decided:** a new `business_calendars` table. Calendars are structured data with their own validation and a deletion guard. As workflow settings, expressions could also read them as `setting.*`.
- Does a workflow name its calendar once, or does each timer and task name one? **Decided:** each timer and each task names one, in `timer.calendar` and `dueDateCalendar`. Other durations in the same workflow, such as a 30-second retry timer, stay wall-clock.
- Partial working days and half-day holidays. **Decided:** each weekday has up to ten working periods, which covers lunch breaks and short days. A holiday either closes its date or gives it its own working hours, for example a half day.

## Plan

Written on 2026-10-27, following [Writing a plan](README.md#writing-a-plan).

### 1. Scope and decisions

The first release adds calendars that settings administrators manage. A timer duration or a user-task due-date duration can name a calendar so that it counts working time. The due time is computed once and saved: when the timer activates, or when the work item is created. Recomputation, holiday import, cron schedules, and calendars on recurring timers are not part of it.

- **Calendars.** A calendar has:
  - a name and an optional description;
  - an IANA time zone, such as `Europe/London`;
  - a weekly schedule;
  - holidays.

  Each weekday has up to ten working periods, such as 09:00–12:30 and 13:30–17:30. A day without periods is not a working day. A holiday either closes its date or replaces that date's periods with its own, for example a half day. Periods are wall-clock times in the calendar's time zone and cannot cross midnight. An end of `24:00` ends a period at midnight.
- **Explicit naming.** A timer names its calendar in `timer.calendar`, and a user task in `dueDateCalendar`. There is no workflow default, so other durations in the same workflow stay wall-clock.
- **Durations.** With a calendar, days count working days and keep the time of day. Hours, minutes, and seconds count working time. Weeks and fractional days are rejected with a calendar because their working length is ambiguous; write `P5D` or `P1DT4H` instead. Months and years stay unsupported.
- **Only durations.** A `timeDate` is an absolute time. A `timeCycle` cannot name a calendar in the first release. When a due-date expression gives a date-time, the calendar is ignored.
- **Evaluated once.** The due time is saved when the timer activates or the work item is created. Editing a calendar never moves a saved time. A reactivated task, or a timer that activates later, uses the calendar as it is at that moment.
- **Failures.** A timer fails the transition if:
  - its calendar is missing;
  - the host does not know the calendar's time zone;
  - its due time cannot be computed.

  This matches other activation-time validation. A Worker job retries, then opens an incident. A due date that cannot be computed is left empty and a warning is logged, as [UI-04](ui-04-task-due-date-and-priority.md) does for unusable values.
- **Deletion.** A calendar cannot be deleted while a published definition, a running instance, or an open job uses it. A definition can be saved and published only when its calendars exist.
- **Time zones.** .NET `TimeZoneInfo` with IANA ids, without an extra package. Each API and Worker host uses its own time-zone data.

How durations are counted, for a calendar with working hours Monday to Friday, 09:00 to 17:00:

| Start | Duration | Due | Why |
| --- | --- | --- | --- |
| Mon 14:00 | `P3D` | Thu 14:00 | Three working days later, at the same time. |
| Fri 14:00 | `P3D` | Wed 14:00 | Saturday and Sunday are not working days. |
| Sat 10:00 | `P1D` | Mon 17:00 | A start outside working time counts from Mon 09:00, and one working day ends at Mon 17:00. |
| Mon 08:00 | `P3D` | Wed 17:00 | Counts from Mon 09:00: Monday, Tuesday, and Wednesday. |
| Mon 16:00 | `PT2H` | Tue 10:00 | One working hour on Monday and one on Tuesday. |
| Thu 15:00 | `P1D` | Fri 13:00 | Friday is a holiday with hours 09:00 to 13:00. 15:00 is after them, so the due time is their end. |
| Mon 14:00 | `P3D` | Fri 14:00 | Wednesday is a holiday. |

The rules behind the table:

- **Days.** A start outside working time first moves to the next working time. The due time keeps that time of day on the Nth following working day, meaning a date with working time. The due time is moved earlier when that time is not working time on that day, or is exactly the start of a period. It becomes the end of the last period before that time. If no period ends before it that day, it becomes the end of the previous working day. No working time passes between the two moments, so the earlier one is used.
- **Hours, minutes, and seconds.** The clock runs only during working periods. The due time is the moment the last of that time elapses, so it can be the end of a period.
- **Both.** `P1DT4H` applies the days first, then the hours.
- **Daylight saving.**
  - A period boundary at a local time that the clock skips falls at the moment the clock jumps.
  - A boundary at a repeated local time falls at its first occurrence.
  - A period therefore lasts the real time between its boundaries, so one that contains a skipped hour is an hour shorter.
  - Days keep their wall-clock time of day across a change.
- **Limit.** The calculation searches up to 36,525 days, about 100 years, after the start. A calendar without working time in that window cannot give a due time.

### 2. Workflow JSON and model

| Property | Where | Rules |
| --- | --- | --- |
| `timer.calendar` | `timerStartEvent`, `intermediateTimerCatchEvent`, `timerBoundaryEvent` | Trimmed; blank means none. Allowed only with `timeDuration`, which must then use whole days and no weeks. |
| `dueDateCalendar` | `userTask`, including a multi-instance one | Trimmed; blank means none. Requires `dueDate`. Rejected when `dueDate` is a fixed date-time. A fixed duration must use whole days and no weeks. |

A calendar name is 1 to 100 characters long. It contains letters, digits, `.`, `_`, and `-`, and starts with a letter or digit. Names are unique ignoring case. A definition must spell a name exactly as the calendar does, as `sharedKey` does for shared variables.

```json
{
  "id": 3,
  "type": "timerBoundaryEvent",
  "name": "Escalate after three working days",
  "attachedToRef": 2,
  "timer": { "timeDuration": "P3D", "calendar": "uk-office" }
}
```

```json
{
  "id": 2,
  "type": "userTask",
  "name": "Review claim",
  "dueDate": "P2D",
  "dueDateCalendar": "uk-office"
}
```

- `TimerDefinitionModel` gains `Calendar` (`calendar`) and `FlowNodeModel` gains `DueDateCalendar` (`dueDateCalendar`). Each is omitted when null, so older documents serialize as before.
- `TimerDefinitionRules.TryParseWorkingDuration` reads a working duration: whole days plus a time part, using the timer grammar.
- `BusinessCalendarRules` holds the name grammar and the problem texts that validation and the editor share.
- `UserTaskDueValue` gains a working-time form that carries the duration and the calendar name.
- `WorkflowModelMigrator` trims both properties, turns blanks into null, and clears `dueDateCalendar` on other node types. Definition validation rejects it on other node types first.
- The editor's loader and export keep both properties. The authoring schema is generated from the model.

### 3. Engine, persistence, and migrations

- **Calendar model.** New Flowbit.Service classes (`BusinessCalendarContentRules`, `BusinessCalendarTimeZones`, and `BusinessCalendar`) validate and normalize calendars and compute due times. Their rules:

  | Part | Rule |
  | --- | --- |
  | Time zone | An IANA id that the host resolves. |
  | Periods | `HH:mm`, with `24:00` allowed as an end. Sorted and not overlapping. |
  | Week | At least one period. At most ten periods per day. |
  | Holidays | At most 1,000, with unique dates and names of up to 200 characters. |
  | Description | Up to 1,000 characters. |

- **Local times.** A local time maps to the first instant at which the clock shows that time or later. A repeated time therefore maps to its earlier occurrence. A skipped time maps to the transition instant, found by binary search between the two offsets.
- **Resolver.** A scoped resolver loads a calendar by name once per request or Worker scope and keeps it for that scope, as workflow settings are kept. Runtime reads take no locks.
- **Timers.** `WorkflowTimerSchedule.Resolve` takes the calendar. The engine loads it when a timer catch or boundary activates. The Worker's timer-start reconciliation loads it when it creates the start schedule. The timer subscription saves the calendar name in a new `ScheduleCalendar` column, next to its schedule kind and expression.
- **Due dates.** `ResolveUserTaskDueAndPriority` gives a working-time due value when the task names a calendar and the value is a duration. The runtime repository resolves that value at the time it records as the work item's `CreatedAt`. A multi-instance task resolves it once for all children. This is where UI-04 adds a plain duration.
- **References.** A new `workflow_definition_business_calendars` table records the calendars each definition version names.
  - It is written when a version is saved and again when it is published, after the shared-variable bindings.
  - The calendar rows are locked `FOR KEY SHARE`, in name order, while it is written.
  - Deleting a calendar locks its row. The delete is refused while a published definition, a running instance, or an open job uses the calendar, as archiving a shared variable is. Otherwise the references are deleted with it.
- **Readiness.** `WorkflowDefinitionReadinessChecker` checks that every named calendar exists and is spelled with its exact casing. The check runs when a version is saved, published, or made the default, and in AI authoring validation.
- **Migration `AddBusinessCalendars`.**
  - It adds the two tables and the nullable `timer_subscriptions."ScheduleCalendar"` column.
  - It seeds the `examples.weekdays` calendar (Monday to Friday, 09:00 to 17:00, `Etc/UTC`) when it is missing, as earlier migrations seed the example settings.
  - Down removes all of these.
- **Version compatibility.**
  - The timer node contracts already include the whole `timer` object. A changed calendar therefore blocks an in-place switch for an open timer, as a changed expression does.
  - The persisted timer descriptor also compares `ScheduleCalendar`.
  - `dueDateCalendar` is left out of node contracts, like `dueDate`.
- **Lock order and Worker.** The engine lock order is unchanged. The Worker needs no new job type; timer jobs and incidents work as before.
- **Upgrade order.** Apply the migration first. Then upgrade every API, UI, and Worker replica before any definition names a calendar. An older API drops the properties when it saves a definition, and an older replica computes wall-clock due times.

### 4. API contracts and authorization

The new `/api/business-calendars` group requires an authenticated settings administrator (`Settings.RequiredRole`), as the settings endpoints do.

| Operation | Meaning |
| --- | --- |
| `GET /api/business-calendars` | Every calendar, ordered by name. |
| `POST /api/business-calendars` | Creates a calendar and returns `201` with its location. A name already in use, ignoring case, returns `409`. |
| `PUT /api/business-calendars/{id}` | Replaces the description, time zone, weekly hours, and holidays; the name cannot change. `expectedUpdatedAt` is required, and a stale value returns `409`. |
| `DELETE /api/business-calendars/{id}?expectedUpdatedAt=` | Returns `204`. A stale value, or a calendar in use, returns `409` with the reasons. |
| `POST /api/business-calendars/preview` | Computes a due time from three inputs: an unsaved calendar, a local start time in its time zone, and a duration. |
| `GET /api/business-calendars/time-zones` | The IANA time-zone ids that the host knows. |

A calendar is returned like this:

```json
{
  "id": 1,
  "name": "uk-office",
  "description": "London office",
  "timeZone": "Europe/London",
  "weeklyHours": {
    "monday": [{ "start": "09:00", "end": "12:30" }, { "start": "13:30", "end": "17:30" }],
    "tuesday": [{ "start": "09:00", "end": "12:30" }, { "start": "13:30", "end": "17:30" }],
    "wednesday": [{ "start": "09:00", "end": "12:30" }, { "start": "13:30", "end": "17:30" }],
    "thursday": [{ "start": "09:00", "end": "12:30" }, { "start": "13:30", "end": "17:30" }],
    "friday": [{ "start": "09:00", "end": "13:00" }],
    "saturday": [],
    "sunday": []
  },
  "holidays": [
    { "date": "2026-12-24", "name": "Christmas Eve", "workingHours": [{ "start": "09:00", "end": "12:00" }] },
    { "date": "2026-12-25", "name": "Christmas Day", "workingHours": [] }
  ],
  "createdAt": "2026-10-27T09:00:00+00:00",
  "updatedAt": "2026-10-27T09:00:00+00:00"
}
```

- Validation errors return `400` with an `error` text. A missing calendar returns `404`.
- Responses always list all seven days, and every list is present.
- The six operations get OpenAPI descriptions and a **Business calendars** tag.

### 5. Editor and Flowbit.Ui changes

Editor assets in `Flowbit/src/Flowbit.Ui/wwwroot/editor/`:

- **Fields.**
  - The timer section shows a **Business calendar** field below the duration, when the timer uses a duration.
  - A user task has a **Due date calendar** field after **Due date**.
  - Both are free text, like a shared-variable key, because the editor receives no server data. The server checks that the calendar exists when the definition is saved.
- **Save validation.** It reports each of these, with the server's messages:
  - a calendar on a timer without a duration;
  - a duration with weeks or fractional days;
  - an invalid name;
  - `dueDateCalendar` without `dueDate`, or with a fixed date-time;
  - the property on other node types.

  Existing documents produce the same errors as before.
- **Load and export.** Both keep the properties and omit them when blank. Definitions without them export exactly as before.

Flowbit.Ui:

- **Business calendars** (`/business-calendars`), under **Manage**, lists calendars. Each row shows the time zone, a summary of the week, the number of holidays, and when the calendar last changed.
- The form edits:
  - the name, only when creating;
  - the description;
  - the time zone, with suggestions;
  - one line of periods per weekday, such as `09:00-12:30, 13:30-17:30`;
  - holidays, with optional hours.
- A preview computes a due time for the calendar being edited, from a start and a duration.
- A stale save keeps the form and offers **Reload latest**.
- Deleting asks for confirmation. If the calendar is in use, the page shows why it cannot be deleted.
- Identities without the settings administrator role see the same message as on the other settings pages.

### 6. Documentation

| Page | What to update |
| --- | --- |
| [Node and property reference](../node-reference.md) | `timer.calendar`, `dueDateCalendar`, and a section on working-time durations with the table above and the daylight-saving rules. |
| [BPMN support](../bpmn-support.md#timers) | Timers, the Flowbit extensions table, and the unsupported capabilities. |
| [API reference](../api-guide.md) | The six operations and their schemas. |
| [Flowbit.Ui guide](../ui-guide.md) | The Business calendars page and the editor fields. |
| [Database schema](../database-schema.md) | The two tables and the new column. |
| [Deployment](../deployment.md#upgrade-and-compatibility-rules) | The upgrade order and the time-zone data each host needs. |
| [Engine settings](../engine-settings.md) | The administrator role. |
| [Developer guide](../developer-guide.md) | Working-time due dates in worklists. |
| Other references | [AGENTS.md](../../AGENTS.md), the [runtime reference](../../Flowbit/README.md), the [root README](../../README.md), and the [portable skill's authoring guide](../../authoring/flowbit-authoring/references/authoring-guide.md). |
| Examples and tests | A new example in the [example catalog](../../examples/README.md), and the [browser smoke suite](../../Flowbit/tests/Flowbit.BrowserTests/README.md). |
| Backlog | This page and the [backlog](README.md). |

### 7. Tests and verification

- **Calculator.**
  - The table above.
  - Lunch breaks, holidays, half days, and a holiday with hours on a weekend.
  - Starts in breaks and outside working hours, and exact period ends.
  - Large durations, the search limit, and `24:00`.
- **Daylight saving.** Custom time zones built with `TimeZoneInfo.CreateCustomTimeZone`, so the results do not depend on the host:
  - skipped hours at 02:00 and at midnight;
  - a 30-minute change;
  - a repeated hour;
  - periods across a change;
  - day counting that keeps wall-clock time.

  Real zones: Europe/London on 29 March and 25 October 2026, and America/New_York on 8 March and 1 November 2026.
- **Rules.** Calendar validation, working-duration parsing, and the name grammar.
- **Definition validation.** Each rule and the readiness checks. Every example is still valid.
- **Model and migrator.** Older JSON round-trips unchanged. The properties round-trip, are trimmed, and are cleared on other node types.
- **Version compatibility.** A changed timer calendar blocks an open timer. The persisted descriptor is compared. A changed due-date calendar does not block.
- **API (PostgreSQL).**
  - Calendar CRUD, `401`, `403`, validation errors, and duplicate and stale `409`.
  - Delete is blocked by a published definition, a running instance, or an open job. It is allowed when only unpublished versions use the calendar.
  - Preview and time zones.
- **Runtime (PostgreSQL, fixed clock).**
  - Due times of timer catches and boundaries, and the saved calendar name.
  - Task due times computed from `CreatedAt`, including multi-instance children.
  - Editing a calendar leaves saved times unchanged.
  - A missing calendar fails a timer and leaves a due date empty.
  - Version switches and timer starts.
- **Migration and schema.** The migration up and down, the schema checks, and the OpenAPI documentation checks.
- **Editor (Jint).** Loading, the fields, export, and validation parity. The pinned characterization arrays are unchanged.
- **Flowbit.Ui.** The page's states, conflict handling, weekday parsing, and preview, and the client's requests.
- **Browser smoke.**
  - R11, at 1440×900 and 390×844, creates a calendar with a lunch break and a half-day holiday and previews across the London daylight-saving change. It then edits the calendar, sees a blocked delete, and deletes it.
  - E11, at 1440×900 and 1024×768, edits the calendar fields and checks that `P1W` with a calendar blocks the save. It then saves and reloads.
- [Real-browser verification](../../AGENTS.md#mandatory-real-browser-ui-verification) on a local stack at desktop and phone widths, with the console checked.
