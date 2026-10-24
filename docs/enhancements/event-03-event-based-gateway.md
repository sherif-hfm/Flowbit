# EVENT-03: Event-based gateway

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New gateway |
| Effort | M |
| Priority | Not ranked |
| Related | [EVENT-01](event-01-expression-timers-business-calendars.md), [EVENT-02](event-02-message-correlation-by-business-key.md), [EVENT-04](event-04-signal-events.md) |

## Summary

An `eventBasedGateway` is followed by catch events: message, timer, conditional, and later signal. The first event to occur wins and the other waits are cancelled.

## Why it adds value

- Races such as "the customer replies, the order is cancelled, or 7 days pass" become one clear construct. Today they need a message catch with timer or conditional boundaries, which supports only one kind of message.

## Current state

Verified on 2026-10-24: not implemented.

- [Unsupported capabilities](../bpmn-support.md#unsupported-capabilities) lists event-based gateways.
- Timer and conditional boundaries can attach to a message catch, which covers one message plus timers or conditions.

## Builds on

- The existing intermediate message, timer, and conditional catch events.
- The cancellation machinery behind scoped interrupts and `joinCancellation`.
- Activation fences that turn late triggers into no-ops.

## Open questions

- Validation rules: exactly one incoming flow, allowed target types, and no conditions on outgoing flows.
- How a delivery selects among several message catches behind one gateway.
- Is an event-based gateway that starts an instance in scope?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
