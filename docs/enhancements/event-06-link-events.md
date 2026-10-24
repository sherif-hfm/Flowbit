# EVENT-06: Link events

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New events |
| Effort | M |
| Priority | Not ranked |
| Depends on | None |
| Related | [UI-01](ui-01-live-instance-diagram.md) |

## Summary

Intermediate link throw and catch events, paired by name, that act as "go to" connectors in large diagrams. Execution behaves like a normal sequence flow from the throw event to its matching catch event.

## Why it adds value

- Large diagrams, especially AI-generated ones, become easier to read without long crossing edges.

## First release acceptance criteria

- [ ] Intermediate link throw and catch events pair by name within one workflow.
- [ ] Validation in the editor and the API requires unique catch names, a catch for every throw, no incoming flow on a catch, and no outgoing flow on a throw.
- [ ] Execution from a throw to its catch behaves like the equivalent sequence flow.
- [ ] The editor draws both shapes and highlights the paired event when either one is selected.

## Later releases

None planned.

## Current state

Verified on 2026-10-24: not implemented.

- [Unsupported capabilities](../bpmn-support.md#unsupported-capabilities) lists link events.

## Builds on

- Definition validation and normalization in `ValidateDefinition`, `WorkflowModelMigrator`, and the editor's save validator.

## Open questions

- Resolve each pair to a virtual flow when the definition loads, or record both events in history and node executions?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
