# UI-07: Version compare

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Flowbit.Ui feature |
| Effort | M |
| Priority | Recommended order #5 |
| Depends on | [UI-01](ui-01-live-instance-diagram.md) |
| Related | None |

## Summary

Compare two versions of a workflow family: added, removed, and changed nodes, flows, and variables listed and highlighted on the diagram, and later a flag on changes that would block moving running instances to the newer version.

## Why it adds value

- Reviews before publishing become safer, and the effect on running instances becomes clear up front.

## First release acceptance criteria

- [ ] On Workflows, a user picks two versions of a family and sees the nodes, flows, and variables added, removed, or changed, matched by ID.
- [ ] The same differences are highlighted on the [UI-01](ui-01-live-instance-diagram.md) diagram.
- [ ] Comparing needs only permission to read the definitions and changes nothing.

## Later releases

- Flags for changes that would block in-place version switching, from compatibility checks that need no specific instance.
- Matching by `externalId`.

## Current state

Verified on 2026-10-24: not implemented.

- Workflows offers Publish, Unpublish, Set default, Start, Edit, Download JSON, and Delete for each version ([Manage workflow versions](../ui-guide.md#manage-workflow-versions)), but no comparison.
- Version-change preview reports blockers and warnings for a specific instance or batch.

## Builds on

- The [UI-01](ui-01-live-instance-diagram.md) viewer.
- The version-change compatibility evaluator.

## Open questions

- Which version-change compatibility checks can run on two definitions alone?
- How to show attribute-only changes, which never block version switching.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
