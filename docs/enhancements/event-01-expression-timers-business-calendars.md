# EVENT-01: Expression timers, cron, and business calendars

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New event capability |
| Effort | M |
| Priority | Recommended order #2, with [UI-04](ui-04-task-due-date-and-priority.md) |
| Related | [UI-04](ui-04-task-due-date-and-priority.md), [EVENT-03](event-03-event-based-gateway.md) |

## Summary

Timers whose schedule comes from data, such as `timeDate: "${contractEnd}"` and `timeDuration: "${slaDuration}"`; cron cycles for timer starts; and business calendars (working days, working hours, holidays, and a time zone) so a duration like "3 working days" skips non-working time.

## Why it adds value

- Most real deadlines depend on data or on business days. Today a timer can only use a fixed value written into the definition.

## Current state

Verified on 2026-10-24: not implemented.

- [Timers](../bpmn-support.md#timers) states that calendar months and years, cron, time-zone calendars, and expression-based schedules are unsupported.
- Each timer has exactly one fixed `timeDate`, `timeDuration`, or `timeCycle`.

## Builds on

- Timer jobs already persist a due time (`DueAt` in [JobModels.cs](../../Flowbit/src/Flowbit.Service/Models/JobModels.cs)) and resolve recurring occurrences in [WorkflowEngineService.Jobs.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.Jobs.cs).
- Evaluating an expression once when the timer activates, then saving the resolved due time, keeps firing deterministic and safe across restarts.
- Workflow settings are a possible home for calendar definitions.

## Open questions

- Evaluate once at activation, or recompute when a source variable changes?
- Calendar storage: workflow settings, engine settings, or a new table with an administration page; and how a workflow selects a calendar.
- Cron dialect and time-zone handling, including daylight-saving transitions.
- Version-switch compatibility rules for active expression timers.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
