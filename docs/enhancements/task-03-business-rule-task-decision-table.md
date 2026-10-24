# TASK-03: Business rule task with decision tables

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New task type |
| Effort | M |
| Priority | Not ranked |
| Depends on | None |
| Related | [UI-08](ui-08-editor-test-run.md) |

## Summary

A `businessRuleTask` evaluates a decision table: NCalc input expressions, rules whose cells test those inputs, typed outputs, and a hit policy such as first match, unique, collect, or priority. The outputs are written to process variables in one atomic step. The editor edits the table as a grid.

## Why it adds value

- Business users maintain rules such as approval limits, discount tiers, and routing matrices in a table instead of chains of exclusive gateways and long conditions.
- Rules are versioned with the workflow and can be checked against sample inputs before publishing.

## First release acceptance criteria

- [ ] A `businessRuleTask` embeds a decision table with input expressions, rules, and typed outputs, using the `first` and `unique` hit policies.
- [ ] Every cell is parse-checked when the definition is saved, in both the editor and the API.
- [ ] The matching rule's outputs are written atomically, with coercion to declared types and validation.
- [ ] No matching rule, or a `unique` conflict, follows an attached error boundary or otherwise fails the transition.
- [ ] The editor edits the table as a grid.

## Later releases

- The `collect` and priority hit policies.
- Decision definitions versioned separately and shared by several workflows.
- Testing a table with sample inputs in the editor (see [UI-08](ui-08-editor-test-run.md)).

## Current state

Verified on 2026-10-24: not implemented.

- [Unsupported capabilities](../bpmn-support.md#unsupported-capabilities) lists DMN decision tables and FEEL.
- Rules are expressed today as exclusive-gateway conditions or script-task assignments.

## Builds on

- `SequenceFlowConditionEvaluator` (NCalc plus Flowbit's helper functions) for evaluation and author-time parse checks.
- Script-task write semantics: coercion to declared types, validation, and error-boundary routing on failure.
- Typed output mapping validation.

## Open questions

- Cell syntax: full NCalc expressions, or a simpler test syntax such as `> 1000`, `"gold","silver"`, and `-` for any value?
- An inspector grid or a dialog?
- Is DMN XML import out of scope?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
