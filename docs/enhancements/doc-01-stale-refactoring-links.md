# DOC-01: Stale links to the removed docs/refactoring pages

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Done |
| Category | Documentation housekeeping |
| Effort | S |
| Priority | Not ranked |
| Depends on | None |
| Related | None |

## Summary

Commit `87a649c` deliberately removed `docs/refactoring/` as outdated, but tracked documents still linked to pages in that folder.

## Why it adds value

- Readers and agents who follow these links reach missing pages. AGENTS.md asks contributors to validate relative links when documentation changes.

## First release acceptance criteria

- [x] No tracked document links to `docs/refactoring/`.
- [x] Each affected document keeps its own explanation and points to current code, tests, or the docs/index.md refactoring history where context helps.
- [x] Changed relative links and anchors resolve.

## Later releases

None.

## Current state

Done on 2026-10-24. No tracked document links to `docs/refactoring/` any more:

| File | Change |
| --- | --- |
| [docs/index.md](../index.md) | The "Planned refactoring" section, which linked to `README.md`, `gaps.md`, `remaining-gaps-implementation-plan.md`, and the Stage 7–11 pages, is now a link-free [Refactoring history](../index.md#refactoring-history) summary. |
| [AGENTS.md](../../AGENTS.md) | The save-validator description points to `EditorValidatorCharacterizationTests`, which pins the ordered error arrays, instead of `stage-04-editor-validation.md`. |
| [Flowbit/README.md](../../Flowbit/README.md) | The "See the Stage 8/Stage 9 implementation record" sentences are removed; the surrounding text already states the outcome. |
| [Flowbit.BrowserTests README](../../Flowbit/tests/Flowbit.BrowserTests/README.md) | The Stage 7 plan link now points to the refactoring history in docs/index.md. |
| [Flowbit.BrowserAcceptanceTests README](../../Flowbit/tests/Flowbit.BrowserAcceptanceTests/README.md) | The Stage 5 and Stage 11 plan links now point to the refactoring history in docs/index.md. |

Copies under the git-ignored `artifacts/` and `.claude/worktrees/` folders are not part of the repository. The authoring package builder in `AuthoringKnowledge.cs` still skips any `docs/refactoring/` source, which is harmless now that the folder is gone.

## Builds on

- The removal commit `87a649c`; the pages remain available in its parent commit.

## Open questions

None.

## Plan

Remove the links, keep each document's own explanation, and point to current code, tests, or the docs/index.md refactoring history where context helps. Completed.
