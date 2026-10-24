# EVENT-06: Link events

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New events |
| Effort | S |
| Priority | Not ranked |
| Related | [UI-01](ui-01-live-instance-diagram.md) |

## Summary

Intermediate link throw and catch events, paired by name, that act as "go to" connectors in large diagrams. Execution behaves like a normal sequence flow from the throw event to its matching catch event.

## Why it adds value

- Large diagrams, especially AI-generated ones, become easier to read without long crossing edges.

## Current state

Verified on 2026-10-24: not implemented.

- [Unsupported capabilities](../bpmn-support.md#unsupported-capabilities) lists link events.

## Builds on

- Definition validation and normalization in `ValidateDefinition`, `WorkflowModelMigrator`, and the editor's save validator.

## Open questions

- Resolve each pair to a virtual flow when the definition loads, or execute both as pass-through nodes with their own history rows?
- Validation: unique catch names, at least one throw per catch, and a single catch per name.
- Editor rendering and navigation from a throw to its catch.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
