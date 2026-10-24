# UI-08: Test run in the editor

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Flowbit.Ui feature (with engine changes) |
| Effort | L |
| Priority | Not ranked |
| Depends on | None |
| Related | [TASK-03](task-03-business-rule-task-decision-table.md), [UI-01](ui-01-live-instance-diagram.md) |

## Summary

Step through a workflow from the editor with sample start inputs, mocked REST responses, chosen user actions, and later a simulated clock for timers, while seeing the path taken and every variable change. Scenarios can later be saved and rerun as regression tests.

## Why it adds value

- Authors find logic errors before publishing.
- AI-generated workflows can be checked by behavior, not only by structural validation.

## First release acceptance criteria

- [ ] From the editor, an author runs the current workflow, saved or not, with sample start inputs and without publishing it.
- [ ] Service tasks use mock responses supplied by the author; no real HTTP request is sent.
- [ ] The author chooses user actions step by step and sees the path, positions, and variable changes.
- [ ] A test run leaves no runtime data behind and changes no shared variable.

## Later releases

- A simulated clock for timers, and simulated message deliveries.
- Saved scenarios rerun as regression tests.
- Test runs offered to the AI assistant as a tool, which AGENTS.md reserves for an explicit user decision.

## Current state

Verified on 2026-10-24: not implemented.

- Validation and readiness checks are static. Behavior can only be observed by publishing a version and starting real instances.

## Builds on

- The real engine, preferred over a second interpreter that could drift from it.
- Typed start and action contracts for scenario inputs.
- The AI authoring validation flow ([AI authoring](../ai-authoring.md)).

## Open questions

- Execution environment: an isolated schema, rolled-back transactions, or an in-memory engine adapter?
- How shared-variable reads work in a test run without exposing real catalog values.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
