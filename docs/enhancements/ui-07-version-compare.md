# UI-07: Version compare

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Flowbit.Ui feature |
| Effort | S–M |
| Priority | Not ranked |
| Related | [UI-01](ui-01-live-instance-diagram.md) |

## Summary

Compare two versions of a workflow family: added, removed, and changed nodes, flows, and variables highlighted on the diagram, plus a JSON diff, with a flag on changes that would block moving running instances to the newer version.

## Why it adds value

- Reviews before publishing become safer, and the effect on running instances is clear up front.

## Current state

Verified on 2026-10-24: not implemented.

- Workflows offers Publish, Unpublish, Set default, Start, Edit, Download JSON, and Delete for each version ([Manage workflow versions](../ui-guide.md#manage-workflow-versions)), but no comparison.
- Version-change preview reports blockers and warnings for a specific instance or batch.

## Builds on

- The [UI-01](ui-01-live-instance-diagram.md) viewer.
- The version-change compatibility evaluator.

## Open questions

- Match nodes and flows by ID, by `externalId`, or both?
- Which compatibility checks can run on two definitions alone, without a specific instance?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
