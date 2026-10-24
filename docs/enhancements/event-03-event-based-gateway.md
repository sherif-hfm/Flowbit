# EVENT-03: Event-based gateway

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New gateway |
| Effort | M |
| Priority | Not ranked |
| Depends on | None |
| Related | [EVENT-01](event-01-expression-timers.md), [EVENT-02](event-02-message-correlation-by-business-key.md), [EVENT-04](event-04-signal-events.md) |

## Summary

An `eventBasedGateway` is followed by catch events: message, timer, conditional, and later signal. The first event to occur wins and the other waits are cancelled.

## Why it adds value

- Races such as "the customer replies, the order is cancelled, or 7 days pass" become one clear construct. Today they need a message catch with timer or conditional boundaries, which supports only one kind of message.

## First release acceptance criteria

- [ ] An `eventBasedGateway` has one incoming flow and two or more unconditional outgoing flows, each to an intermediate message, timer, or conditional catch; the editor and the API both validate this.
- [ ] Entering the gateway activates every target wait; the first trigger wins and cancels the other waits, including their timer jobs, in the same transaction.
- [ ] A late trigger for a cancelled wait is rejected.
- [ ] A message delivery selects the right catch behind the gateway.
- [ ] Version switching while an event-based gateway is waiting is either supported or explicitly blocked.

## Later releases

- Signal catches behind the gateway, after [EVENT-04](event-04-signal-events.md).
- Event-based gateways that start an instance.

## Current state

Verified on 2026-10-24: not implemented.

- [Unsupported capabilities](../bpmn-support.md#unsupported-capabilities) lists event-based gateways.
- Timer and conditional boundaries can attach to a message catch, which covers one message plus timers or conditions.

## Builds on

- The existing intermediate message, timer, and conditional catch events.
- The cancellation machinery behind scoped interrupts and `joinCancellation`.
- Activation fences that turn late triggers into no-ops.

## Open questions

- How a delivery names its catch: by the catch's external ID, as for parallel waits today?
- The editor shape and marker.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
