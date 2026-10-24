# EVENT-05: Error-code matching on error boundaries

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New event capability |
| Effort | M |
| Priority | Not ranked |
| Depends on | None |
| Related | [TASK-02](task-02-external-task.md), [TASK-01](task-01-call-activity.md) |

## Summary

Allow several `errorBoundaryEvent` nodes on one host, each matching an error code or an HTTP status range, with an optional catch-all. For example: 409 to a duplicate path, 5xx to a retry path, and 4xx to a task where someone fixes the data.

## Why it adds value

- Different failures need different handling. Today one catch-all boundary must be followed by a gateway that inspects `statusVariable`.

## First release acceptance criteria

- [ ] An `errorBoundaryEvent` can list HTTP status codes or ranges to match, and a host may have several boundaries, with at most one catch-all.
- [ ] A REST failure follows the matching boundary in a fixed precedence (exact code, then range, then catch-all); with no match, the task fails as it does today.
- [ ] The editor and the API reject overlapping matchers on one host.
- [ ] The editor shows each boundary's codes.

## Later releases

- Error codes thrown by scripts.
- Business-error codes from external tasks ([TASK-02](task-02-external-task.md)) and child instances ([TASK-01](task-01-call-activity.md)).

## Current state

Verified on 2026-10-24: not implemented.

- Error boundaries are interrupting only, catch-all with no error-code match, and limited to one per host ([AGENTS.md BPMN alignment](../../AGENTS.md#bpmn-alignment)).

## Builds on

- `errorBoundaryEvent`, `statusVariable`, and `errorVariable`.
- The stable `errorCode` format already defined for `errorEndEvent`.

## Open questions

- Interaction with the `retryFirst` and `boundaryFirst` job policies.
- How timeouts and network failures match, given that `statusVariable` receives 0 when no response exists.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
