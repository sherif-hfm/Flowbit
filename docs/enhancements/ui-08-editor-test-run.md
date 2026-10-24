# UI-08: Test run in the editor

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Flowbit.Ui feature (with engine changes) |
| Effort | L |
| Priority | Not ranked |
| Related | [TASK-03](task-03-business-rule-task-decision-table.md), [UI-01](ui-01-live-instance-diagram.md) |

## Summary

Step through a workflow from the editor with sample start inputs, mocked REST responses, chosen user actions, and a simulated clock for timers, while seeing the path taken and every variable change. Scenarios can optionally be saved and rerun as regression tests.

## Why it adds value

- Authors find logic errors before publishing.
- AI-generated workflows can be checked by behavior, not only by structural validation.

## Current state

Verified on 2026-10-24: not implemented.

- Validation and readiness checks are static. Behavior can only be observed by publishing a version and starting real instances.

## Builds on

- The real engine, preferred over a second interpreter that could drift from it.
- Typed start and action contracts for scenario inputs.
- The AI authoring validation flow ([AI authoring](../ai-authoring.md)).

## Open questions

- Execution environment: an isolated schema, rolled-back transactions, or an in-memory engine adapter?
- How to mock REST calls, message deliveries, and timers.
- Where scenarios are stored: with the definition, or separately.
- Offering test runs to the AI assistant as a tool is a user decision under AGENTS.md and needs an explicit request.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
