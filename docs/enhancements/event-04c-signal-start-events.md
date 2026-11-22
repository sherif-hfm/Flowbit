# EVENT-04C: Signal start events

[Parent EVENT-04](event-04-signal-events.md) · [Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New events |
| Effort | M–L |
| Priority | Not ranked; third of eleven in the [EVENT-04 follow-up order](README.md#event-04-follow-up-subtasks) |
| Parent | [EVENT-04](event-04-signal-events.md) |
| Depends on | [EVENT-04](event-04-signal-events.md) (Done) |
| Related | [EVENT-04B](event-04b-signal-throw-and-end-events.md), [EVENT-04A](event-04a-signal-scope-by-business-key-or-instance.md) |

## Summary

A `signalStartEvent` starts a new instance of its workflow each time a signal with its name is thrown in a scope that includes the workflow. Typed mappings turn the payload into the start variables.

## Why it adds value

- A broadcast can open new work, not only move work that is already waiting.
- The sender does not need to know which workflows react. Publishing a definition with a signal start adds a reaction.
- Today a workflow started by an external occurrence needs a message start, and the caller must address each workflow by its key.

## First release acceptance criteria

- [ ] `signalStartEvent` has no incoming flow and one unconditional outgoing flow, and carries a `signal` object with a literal lowercase `name` and typed `outputMappings` that declare its start variables, as a message start does. It is system-only: the start page does not offer it, and `POST /api/instances` rejects it.
- [ ] Only the published default version of a family listens. Publishing, changing the default, and deleting a version move or end the subscription in the same transaction.
- [ ] A throw freezes the listening starts together with its other recipients. Each frozen start creates exactly one instance or ends as `failed`, `skipped`, or `cancelled`. Retries, duplicate leases, and restarts never create two.
- [ ] The new instance starts as the fixed actor `signal`, records the broadcast in its history, and is linked from its delivery row on the **Signals** page.
- [ ] A payload that fails a mapping fails only that delivery and creates no instance.
- [ ] A start that configures a business key or idempotency takes its values from the payload. A duplicate ends that delivery with a reason and affects no other delivery.
- [ ] The recipient limit counts starts, and the broadcast's recipient counts tell waits and starts apart.
- [ ] A definition with a signal start publishes only when durable publication is enabled.
- [ ] The editor draws and authors the event. Documentation, an example, the portable authoring package, automated tests including PostgreSQL race tests, and real-browser verification with a running Worker are complete.

## Later releases

- Several signal starts with the same name in one definition.
- A start that also filters on the payload, such as a region.

## Current state

Verified on 2026-11-17: not implemented.

- [Unsupported capabilities](../bpmn-support.md#unsupported-capabilities) lists signal start events.
- A row in `signal_subscriptions` requires an instance, a token, and an activation, and a row in `signal_deliveries` requires an instance and a token ([SignalEntities.cs](../../Flowbit/src/Flowbit.Infrastructure/Entities/SignalEntities.cs)). A start has none of them.
- A delivery locks an existing instance and moves its token. Nothing in the delivery path creates an instance.
- `messageStartEvent` is the existing system-only entry with typed mappings, and [TimerStartReconciliationService.cs](../../Flowbit/src/Flowbit.Worker/TimerStartReconciliationService.cs) keeps timer start subscriptions in step with the published defaults.
- Once one entry of a definition enables a business key, every entry must configure it, so a signal start in such a definition needs a key in its payload.
- Since [EVENT-04B](event-04b-signal-throw-and-end-events.md) (2026-11-21), a workflow throws signals with `intermediateSignalThrowEvent` and `signalEndEvent`. Such a broadcast has the thrower kind `instance`, with `ThrowerInstanceId`, `ThrowerNodeId`, and `ThrowerNodeName`, and every broadcast carries `AutomaticActivationCount`: the throwing token's chain count plus one for a throw from a workflow, and 0 for a throw through the API. A delivery gives that count to the receiving token, and a throw at `Workflow.Async.MaxConsecutiveAutomaticActivations` is refused. A call activity's child already starts at its calling token's count.

## Builds on

- The message start event: typed start mappings, the slim acknowledgement, and starting without a user.
- Timer start reconciliation, for subscriptions that belong to a definition and not to an instance.
- The delivery job processor, its outcomes, and its exhausted-lease reconciliation.
- Start idempotency and business key claims, which already reject a duplicate start inside the start transaction.
- The chain count of [EVENT-04B](event-04b-signal-throw-and-end-events.md#loop-guard): a started instance's first token can take the delivering broadcast's `AutomaticActivationCount`, as a call activity's child takes its caller's, and the rule in AGENTS.md that a new way to create a token hands the count over.
- The `instance` thrower, so a started instance's history can name the instance and event that threw the signal, as a `signalReceived` row does with `thrownByInstanceId` and `thrownByNodeId`.

## Open questions

- Where does a start subscription live: in `signal_subscriptions` with optional instance columns, or in a table keyed by definition?
- Which scopes reach a start? A family scope and all workflows are clear. A business key or instance scope ([EVENT-04A](event-04a-signal-scope-by-business-key-or-instance.md)) names existing instances and probably starts nothing.
- A workflow that throws ([EVENT-04B](event-04b-signal-throw-and-end-events.md)) the signal that starts it creates instances without end. Does saving reject that, or does a limit stop it? EVENT-04B suggests the limit: if the started instance's first token continues the broadcast's chain count, such a loop stops at `Workflow.Async.MaxConsecutiveAutomaticActivations` with a refused throw, while a start that a person, a message, a timer, or a throw through the API causes starts a new chain.
- Does a start delivery count toward the same recipient limit as a wait, given that it costs a whole start transaction?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
