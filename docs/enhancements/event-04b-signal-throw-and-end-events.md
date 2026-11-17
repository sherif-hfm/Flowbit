# EVENT-04B: Signal throw and end events

[Parent EVENT-04](event-04-signal-events.md) · [Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New events |
| Effort | L |
| Priority | Not ranked; second of eleven in the [EVENT-04 follow-up order](README.md#event-04-follow-up-subtasks) |
| Parent | [EVENT-04](event-04-signal-events.md) |
| Depends on | [EVENT-04](event-04-signal-events.md) (Done) |
| Related | [EVENT-04A](event-04a-signal-scope-by-business-key-or-instance.md), [EVENT-04C](event-04c-signal-start-events.md), [EVENT-04F](event-04f-signal-catalog.md) |

## Summary

An `intermediateSignalThrowEvent` and a `signalEndEvent` throw a signal from a workflow. Entering one creates the same broadcast the API creates, inside the throwing instance's transaction, and the Worker delivers it as it does today.

Plan this after [EVENT-04A](event-04a-signal-scope-by-business-key-or-instance.md), so the scope a definition authors can include the thrower's own business key.

## Why it adds value

- Workflows react to each other without an integration that calls the API. An order process that throws "order cancelled" interrupts the open shipping and invoicing work.
- The throw commits or rolls back with the step that made it, so a signal never announces a step that did not happen.
- Today a definition can wait for a signal but cannot send one.

## First release acceptance criteria

- [ ] `intermediateSignalThrowEvent` has incoming flows and one unconditional outgoing flow, and is pass-through. `signalEndEvent` has incoming flows and no outgoing flow, and ends its token as a plain end event does. Both carry a `signal` object with a literal lowercase `name`, an explicit scope, and an optional payload built from declared variables. The API and the editor apply the same rules with the same wording.
- [ ] Entering the event creates one broadcast, its frozen recipients, and its delivery jobs in the instance's transaction. A transaction that rolls back leaves no broadcast, and a retried step creates exactly one.
- [ ] The throw locks no other instance and waits for none. The throwing token continues without waiting for the deliveries.
- [ ] The broadcast records the throwing instance and node as its thrower. The **Signals** page links to the instance, and the instance's history links to the broadcast. Receiving instances still continue as the fixed actor `signal`.
- [ ] The recipient, open-broadcast, and payload limits apply. What the instance does when a limit refuses its throw is defined and tested, and a throw is never dropped silently.
- [ ] Whether the throwing instance's own waits receive the signal is defined and tested.
- [ ] A definition with a throw or end event publishes only when durable publication is enabled, and a definition without signal events still runs no additional statement.
- [ ] Version changes, reactivation, the shared-variable lock-order proof, the graph analyses, lifecycle capture, and the AI authoring package handle both types.
- [ ] The editor draws and authors both events, and the read-only instance diagram shows them.
- [ ] Documentation, an example, the portable authoring package, automated tests including PostgreSQL race tests, and real-browser verification with a running Worker are complete.

## Later releases

- A payload built from expressions instead of whole variables.
- A throw event that waits until its deliveries finish.
- Throwing only names that the signal catalog declares ([EVENT-04F](event-04f-signal-catalog.md)).

## Current state

Verified on 2026-11-17: not implemented.

- [Unsupported capabilities](../bpmn-support.md#unsupported-capabilities) lists signal throw and end events, and [Signals](../bpmn-support.md#signals) says a workflow cannot throw a signal itself.
- `ThrowAsync` in [SignalBroadcastService.cs](../../Flowbit/src/Flowbit.Service/Services/SignalBroadcastService.cs) opens its own transaction, so a throw cannot yet join an instance's transaction.
- A thrower is a `SignalCaller` whose kind is `user` or `client` (`SignalThrowerKinds` in [SignalDtos.cs](../../Flowbit/src/Flowbit.Shared/Dtos/SignalDtos.cs)). There is no kind for an instance.
- The API throw sees only committed subscriptions. A throw inside an instance's transaction also sees that instance's own uncommitted subscription changes.
- `ThrowAsync` reports the open-broadcast limit with `WorkflowConflictException`. In a Worker job that exception can mean a stale job, so a throw from a workflow needs its own failure.
- A payload is a JSON object of at most 256 KiB, and a job row must name a workflow definition and a node, so a broadcast enqueues one `signalBroadcastDeliver` job per recipient definition.

## Builds on

- `ThrowAsync`, `FreezeDeliveriesAsync`, and `EnqueueDeliveryJobsAsync`, which already freeze recipients in one statement without a runtime lock.
- The pass-through loop `ResolvePassThroughAsync` and the link throw event, the nearest existing throw event in the model, the validators, and the editor.
- The call activity's whole-variable input mappings (`CallActivityRules`) as a model for a payload built from variables.
- [SignalEventRules.cs](../../Flowbit/src/Flowbit.Shared/Models/SignalEventRules.cs), `HasSignalEvents`, and `SignalEventFixtures`, which keep the API and the editor on the same rules.

## Open questions

- Which scopes can a definition author: its own family, a named family, all workflows, or the thrower's business key? The API requires an explicit scope, and the event should too.
- Does the thrower receive its own signal? BPMN broadcasts to every catcher, including the throwing process. An interrupting boundary around the throw event would then cancel the branch that threw.
- What happens when a limit refuses the throw: does the transition fail, with retries and an incident in a job, or does the token wait?
- Is the payload a list of whole variables, as call activity inputs are, or a template?
- A workflow that catches a signal and throws the same one can circulate without end. Is a guard needed beyond the open-broadcast limit?
- The API requires a role from `Signals.RequiredRole`. Is publishing a definition enough authority to throw to all workflows?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
