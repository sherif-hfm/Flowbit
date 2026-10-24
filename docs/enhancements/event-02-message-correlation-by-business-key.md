# EVENT-02: Message correlation by business key

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New event capability |
| Effort | M |
| Priority | Not ranked |
| Related | [EVENT-03](event-03-event-based-gateway.md), [EVENT-04](event-04-signal-events.md) |

## Summary

Deliver a message to a workflow family by message name and business key, or by a declared correlation variable, instead of by Flowbit instance ID. For example: `POST /api/workflows/{workflowKey}/messages/{messageName}` with the business key in the request.

## Why it adds value

- External systems know their own identifiers, such as an order or case number, but not Flowbit's internal instance ID. Today they must store the instance ID returned when the instance started.

## Current state

Verified on 2026-10-24: not implemented.

- Message catches are addressed by instance (`POST /api/instances/{id}/message`). Correlation is by instance ID only, with no cross-instance message-name matching ([Messages](../bpmn-support.md#messages)).

## Builds on

- Business-key claims, which already guarantee at most one running instance per key in a workflow family.
- Message-catch authentication, typed output mappings, and delivery idempotency.
- Selection of a parallel wait by its `catchEvent` external ID.

## Open questions

- Message name: a new node property, or the catch node's `externalId`?
- Families without business keys: support a correlation variable, and how is it indexed and kept unambiguous?
- No matching wait: reject the delivery, or buffer the message until a wait appears?
- Waking several instances with one message is [EVENT-04](event-04-signal-events.md); keep this item to a single target.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
