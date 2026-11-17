# EVENT-04A: Signal scope by business key or instance

[Parent EVENT-04](event-04-signal-events.md) · [Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New events |
| Effort | S–M |
| Priority | Not ranked; first of eleven in the [EVENT-04 follow-up order](README.md#event-04-follow-up-subtasks) |
| Parent | [EVENT-04](event-04-signal-events.md) |
| Depends on | [EVENT-04](event-04-signal-events.md) (Done) |
| Related | [EVENT-02](event-02-message-correlation-by-business-key.md), [EVENT-04B](event-04b-signal-throw-and-end-events.md), [EVENT-04H](event-04h-expression-signal-names.md) |

## Summary

A throw can limit its recipients to the instances with one business key, or to one instance. Today the narrowest scope is one workflow family.

## Why it adds value

- "Stop everything that is open for order 4711" becomes one throw. Today a targeted reaction needs a message to each instance by its ID, which the caller must have stored.
- The caller needs only the business key it already knows, not the instance IDs or which tasks are open.
- It prepares two later items. A throw event ([EVENT-04B](event-04b-signal-throw-and-end-events.md)) can address the instances with the thrower's own business key, and most reasons for a name built from an expression ([EVENT-04H](event-04h-expression-signal-names.md)) disappear.

## First release acceptance criteria

- [ ] `POST /api/signal-broadcasts` accepts an optional `businessKey` and an optional `instanceId`. A request still states its scope explicitly, and a combination the plan does not allow is rejected with a message that names it.
- [ ] A business key is compared exactly and case-sensitively, as a start compares it. A blank key is rejected, not read as "no key".
- [ ] The recipients are still frozen in one statement that takes no runtime lock. A subscription outside the scope gets no delivery row.
- [ ] The broadcast records its scope. The API responses and the **Signals** page show it in the throw form, the review step, the list, and the detail.
- [ ] A repeated `idempotencyKey` with a different business key or instance is a conflict, not a replay.
- [ ] A managed API client still reads, cancels, and retries only the broadcasts it threw.
- [ ] A throw without the new fields behaves exactly as before, stored broadcasts keep their meaning, and the schema change is additive.
- [ ] The API reference, developer guide, UI guide, and database schema reference are updated, with PostgreSQL tests for each scope and real-browser verification of the **Signals** page.

## Later releases

- Several business keys or instances in one throw.
- Limiting a signal to one call tree: a root instance and the children its call activities started.
- The subscription summary filtered by business key.

## Current state

Verified on 2026-11-17: not implemented.

- `ThrowSignalRequest` in [SignalDtos.cs](../../Flowbit/src/Flowbit.Shared/Dtos/SignalDtos.cs) has `name`, `workflowKey`, `allWorkflows`, `payload`, and `idempotencyKey`. Exactly one of `workflowKey` and `allWorkflows` states the scope ([API reference](../api-guide.md#signals)).
- `FreezeDeliveriesAsync` in [SignalBroadcastRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/SignalBroadcastRepository.cs) selects from `signal_subscriptions` by status, signal name, and optionally workflow key. The table holds `InstanceId` and `WorkflowKey` but no business key.
- `workflow_instances.BusinessKey` holds the key, with an index on `(WorkflowKey, BusinessKey, Status)`. `signal_subscriptions` has an index on `(InstanceId, TokenId)` for active rows. Both filters therefore have an index without a change to the subscription table.
- `signal_broadcasts` records only `WorkflowKey` as its scope, and `HashRequest` in [SignalBroadcastService.cs](../../Flowbit/src/Flowbit.Service/Services/SignalBroadcastService.cs) hashes the name, the workflow key, and the payload.
- Instances started before their family enabled business keys have no key ([business key configuration](../node-reference.md#business-key-configuration)), so a business-key scope never reaches them.

## Builds on

- `ThrowAsync` and `FreezeDeliveriesAsync`, which already take an optional workflow key.
- The business key columns and index of `workflow_instances`.
- The review step of the **Signals** page, which already ends when the name, scope, or payload changes.
- The throw-scope cases in `SignalRuntimeTests`.

## Open questions

- Does `businessKey` need a `workflowKey`? A key is unique only within one family, so with `allWorkflows` one key can match instances of several families. That may be the wanted "everything for order 4711", or a surprise.
- Is `instanceId` a scope of its own, replacing `workflowKey` and `allWorkflows`, or a filter added to one of them?
- Should the freeze statement join `workflow_instances`, or should each subscription copy its instance's business key when it is created? The join reads without locks and needs no backfill. The copy keeps the freeze on one table and its index.
- Does a managed API client need a new scope to address one instance?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
