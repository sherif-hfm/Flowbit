# EVENT-09: Cron schedules for timer starts

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New event capability |
| Effort | M |
| Priority | Next tier: deadline and timer follow-up 3 |
| Depends on | None |
| Related | [EVENT-08](event-08-business-calendars.md), [EVENT-01](event-01-expression-timers.md) |

## Summary

`timerStartEvent` schedules written as cron expressions with a named time zone, such as "every weekday at 08:00".

## Why it adds value

- Business schedules such as weekday mornings or month-ends cannot be written as fixed ISO repeating intervals, which cannot skip weekends or follow calendar months.

## First release acceptance criteria

- [ ] A `timerStartEvent` accepts a cron expression in a documented dialect with a named time zone, as an alternative to `timeCycle`.
- [ ] Next occurrences are correct across daylight-saving transitions, and the existing misfire rule applies.
- [ ] The editor and the API reject invalid expressions.
- [ ] The editor shows the next few occurrences of the schedule.

## Later releases

- Cron schedules on intermediate and boundary timers.
- Skipping holidays from a business calendar ([EVENT-08](event-08-business-calendars.md)).

## Current state

Verified on 2026-10-24: not implemented.

- [Timers](../bpmn-support.md#timers) states that cron and calendar months and years are unsupported; recurring schedules use ISO repeating intervals in `timeCycle`, such as `R5/PT30S`.

## Builds on

- Timer-start schedule activation and reconciliation with the published default version.
- Next-occurrence resolution and the one-minute misfire rule in [WorkflowEngineService.Jobs.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.Jobs.cs).

## Open questions

- A five- or six-field dialect, and which special characters it supports.
- Behavior for skipped and repeated hours at daylight-saving changes.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
