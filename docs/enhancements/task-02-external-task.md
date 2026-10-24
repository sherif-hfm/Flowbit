# TASK-02: External task for pull-based workers

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New task type |
| Effort | M |
| Priority | Recommended order #4 |
| Related | [EVENT-05](event-05-error-code-boundaries.md), [TASK-05](task-05-multi-instance-automatic-activities.md), [INT-01](int-01-outbound-lifecycle-webhooks.md) |

## Summary

An `externalTask` node publishes a unit of work on a named `topic`. External workers, written in any language, fetch and lock work through the API, then report one of three outcomes: completion with typed outputs, a failure (retried, then an incident), or a business error routed to an error boundary. A worker can extend its lock while it works.

## Why it adds value

- Flowbit makes no outbound call, so targets behind firewalls or in other networks need no inbound access.
- Long-running work (minutes or hours) never holds a transaction or a synchronous HTTP request open.
- Workers control their own throughput, and teams can implement them in any language.

## Current state

Verified on 2026-10-24: not implemented.

- [Unsupported capabilities](../bpmn-support.md#unsupported-capabilities) lists arbitrary external task executors.
- Automatic integration today is the outbound REST `serviceTask` and the inbound message start and catch events.

## Builds on

- [Durable execution](../bpmn-support.md#synchronous-and-durable-execution): leases, lease generations, activation fences, `retryDelays` (default 10 seconds, 1 minute, and 5 minutes), the `boundaryFirst` and `retryFirst` policies, and incidents with fenced manual retry.
- Typed output mappings and their all-or-nothing validation.
- Machine-client credentials with scopes, rotation, and revocation, as used by [shared-variable clients](../ui-guide.md#shared-variable-clients-and-distribution-search).
- `sys.jobId` and `sys.jobAttempt` for downstream idempotency.

## Open questions

- Fetch API: long polling or short polling, batch size, lock duration, and lock-extension limits.
- Client authorization: per topic, per workflow family, or both, and which new client scopes?
- Payload: which variables are sent to a worker (an explicit list or all of them), and how shared aliases are handled.
- Business error codes and how they select a boundary ([EVENT-05](event-05-error-code-boundaries.md)).
- Operations: topic queue depth and worker visibility on Jobs & incidents.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
