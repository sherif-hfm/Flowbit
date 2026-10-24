# EVENT-01: Expression timers

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New event capability |
| Effort | M |
| Priority | Next tier: deadline and timer follow-up 1 |
| Depends on | None |
| Related | [UI-04](ui-04-task-due-date-and-priority.md), [EVENT-08](event-08-business-calendars.md), [EVENT-09](event-09-cron-schedules.md), [EVENT-03](event-03-event-based-gateway.md) |

## Summary

Timer catches and timer boundaries whose `timeDate` or `timeDuration` comes from instance data, such as `timeDate: "${contractEnd}"` or `timeDuration: "${slaDuration}"`, evaluated once when the timer activates.

## Why it adds value

- Most real deadlines depend on data, such as a contract end date or an SLA chosen per request. Today a timer can only use a value fixed in the definition.

## First release acceptance criteria

- [ ] `timeDate` and `timeDuration` on timer catches and timer boundaries accept an expression over instance variables as well as a fixed value.
- [ ] The expression is evaluated once when the timer activates, and the resolved due time is saved; later variable changes do not move it.
- [ ] A missing or unusable result (wrong type, malformed value, or a duration that is not positive) fails the transition with a clear error, like other activation-time validation; a `timeDate` already in the past fires as soon as the Worker processes it.
- [ ] Definition validation, in the editor and the API, checks the expression syntax and that it references declared variables.
- [ ] Version-switch rules for active expression timers are defined and tested.

## Later releases

- Expression-based `timeCycle`.
- Recomputing a due time when a source variable changes.
- Durations counted in working time ([EVENT-08](event-08-business-calendars.md)).

## Current state

Verified on 2026-10-24: not implemented.

- [Timers](../bpmn-support.md#timers) states that expression-based schedules are unsupported.
- Each timer has exactly one fixed `timeDate`, `timeDuration`, or `timeCycle`.

## Builds on

- Timer jobs already persist a due time (`DueAt` in [JobModels.cs](../../Flowbit/src/Flowbit.Service/Models/JobModels.cs)) and resolve recurring occurrences in [WorkflowEngineService.Jobs.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.Jobs.cs).
- Evaluating once at activation and saving the result keeps firing deterministic and safe across restarts.
- The same evaluate-once-and-save pattern as [UI-04](ui-04-task-due-date-and-priority.md) due dates.

## Open questions

- `${var}` template substitution, or an NCalc expression?
- Which value types are accepted: `date`, `datetime`, ISO 8601 duration strings, or a number of seconds?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
