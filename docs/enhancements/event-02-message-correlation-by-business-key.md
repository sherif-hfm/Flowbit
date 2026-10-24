# EVENT-02: Message correlation by business key

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New event capability |
| Effort | M |
| Priority | Conditional: bring forward if most adopters embed Flowbit over HTTP |
| Depends on | None |
| Related | [EVENT-03](event-03-event-based-gateway.md), [EVENT-04](event-04-signal-events.md) |

## Summary

Deliver a message to a workflow family by message name and business key, instead of by Flowbit instance ID. For example: `POST /api/workflows/{workflowKey}/messages/{messageName}` with the business key in the request.

## Why it adds value

- External systems know their own identifiers, such as an order or case number, but not Flowbit's internal instance ID. Today they must store the instance ID returned when the instance started.

## First release acceptance criteria

- [ ] A new endpoint delivers a message to a workflow family by message name and business key.
- [ ] It finds the one running instance that owns the business key and its waiting catch with that message name, then applies that catch's credentials, header checks, typed mappings, and delivery idempotency.
- [ ] No running owner, or no matching wait, returns a clear error and buffers nothing.
- [ ] The existing instance-addressed endpoint is unchanged.

## Later releases

- Correlation variables for families without business keys.
- Buffering messages until a matching wait appears.

## Current state

Verified on 2026-10-24: not implemented.

- Message catches are addressed by instance (`POST /api/instances/{id}/message`). Correlation is by instance ID only, with no cross-instance message-name matching ([Messages](../bpmn-support.md#messages)).

## Builds on

- Business-key claims, which already guarantee at most one running instance per key in a workflow family.
- Message-catch authentication, typed output mappings, and delivery idempotency.
- Selection of a parallel wait by its `catchEvent` external ID.

## Open questions

- Message name: a new node property, or the catch node's `externalId`?
- For the later correlation variables: how are they indexed and kept unambiguous?
- Waking several instances with one message belongs to [EVENT-04](event-04-signal-events.md); this item keeps a single target.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
