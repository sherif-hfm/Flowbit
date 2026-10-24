# TASK-02: External task for pull-based workers

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New task type |
| Effort | L |
| Priority | Conditional: schedule when a concrete integration need justifies its lifecycle complexity |
| Depends on | None |
| Related | [EVENT-05](event-05-error-code-boundaries.md), [INT-01](int-01-outbound-lifecycle-webhooks.md) |

## Summary

An `externalTask` node publishes a unit of work on a named `topic`. External workers, written in any language, fetch and lock work through the API, then report one of three outcomes: completion with typed outputs, a failure (retried, then an incident), or a business error routed to an error boundary. A worker can extend its lock while it works.

## Why it adds value

- Flowbit makes no outbound call, so targets behind firewalls or in other networks need no inbound access.
- Long-running work (minutes or hours) never holds a transaction or a synchronous HTTP request open.
- Workers control their own throughput, and teams can implement them in any language.

## First release acceptance criteria

- [ ] An `externalTask` node with a `topic` creates a work item that authenticated machine clients can fetch and lock.
- [ ] A client fetches work only for topics it is allowed, receives only the node's declared input variables, and holds a lock for a bounded duration that it can extend.
- [ ] Completing with typed outputs validates all-or-nothing and advances the token exactly once; a completion with an expired or superseded lock is rejected.
- [ ] A reported failure follows the node's retry delays and opens an incident when retries run out; the incident's manual retry makes the work fetchable again.
- [ ] A reported business error follows the attached error boundary and records its code in `errorVariable`.
- [ ] Cancelling the instance or interrupting the node makes the work unfetchable, and later completions are rejected.

## Later releases

- Long polling.
- Topic queue depth and worker activity on Jobs & incidents.
- Business-error codes that select among several boundaries ([EVENT-05](event-05-error-code-boundaries.md)).

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

- Fetch API details: short or long polling, batch size, and lock-duration limits.
- Client authorization: per topic, per workflow family, or both, and which new client scopes?
- How shared-variable aliases are handled in worker payloads and outputs.
- Version-switch rules for instances with locked external work.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
