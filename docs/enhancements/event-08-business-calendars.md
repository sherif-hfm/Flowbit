# EVENT-08: Business calendars

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
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

- [ ] Administrators manage named calendars (working days, working hours, holidays, and a time zone) through the API and a Flowbit.Ui page.
- [ ] Timer `timeDuration` values and user-task due-date durations ([UI-04](ui-04-task-due-date-and-priority.md)) can name a calendar and are then counted in working time.
- [ ] Due times are computed and saved when the timer or task starts; editing a calendar does not move saved due times.
- [ ] Daylight-saving transitions in the calendar's time zone are covered by tests.

## Later releases

- Recomputing open due times after a calendar change.
- Importing holiday lists.
- Skipping holidays in cron schedules ([EVENT-09](event-09-cron-schedules.md)).

## Current state

Verified on 2026-10-24: not implemented.

- [Timers](../bpmn-support.md#timers) states that time-zone calendars are unsupported.
- [Unsupported capabilities](../bpmn-support.md#unsupported-capabilities) lists business calendars and calendar-relative timer durations.

## Builds on

- Due-time computation for timer jobs (`DueAt` in [JobModels.cs](../../Flowbit/src/Flowbit.Service/Models/JobModels.cs) and [WorkflowEngineService.Jobs.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.Jobs.cs)).
- The task due dates saved by [UI-04](ui-04-task-due-date-and-priority.md).
- The settings pages' administration pattern: `Settings.RequiredRole` permission and optimistic concurrency.

## Open questions

- Storage: a new table, or workflow settings?
- Does a workflow name its calendar once, or does each timer and task name one?
- Partial working days and half-day holidays.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
