# EVENT-04F: Signal catalog

[Parent EVENT-04](event-04-signal-events.md) · [Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New events |
| Effort | M–L |
| Priority | Not ranked; sixth of eleven in the [EVENT-04 follow-up order](README.md#event-04-follow-up-subtasks) |
| Parent | [EVENT-04](event-04-signal-events.md) |
| Depends on | [EVENT-04](event-04-signal-events.md) (Done) |
| Related | [EVENT-04B](event-04b-signal-throw-and-end-events.md), [EVENT-04H](event-04h-expression-signal-names.md), [EVENT-04K](event-04k-rename-managed-api-clients.md) |

## Summary

A catalog of signal definitions. Each entry has a name, a description, a declared payload contract, and the roles allowed to throw it. Saved definitions and throws are checked against it.

## Why it adds value

- Today any name can be awaited and thrown. A mistyped name in a definition creates a wait that no broadcast reaches, and saving cannot detect it.
- A payload that does not fit a receiver's mappings fails delivery by delivery, after the throw. A contract rejects it at the throw.
- One setting, `Signals.RequiredRole`, authorizes every signal. With a catalog, a recall can need more authority than a price update.
- The editor and the **Signals** page can offer names instead of free text.

## First release acceptance criteria

- [ ] Settings administrators create, edit, and archive entries through the API and a page, with optimistic concurrency. An entry has a name under the existing name rule, a description, typed payload fields, and throw roles.
- [ ] A throw of a cataloged name checks the payload against the contract before recipients are frozen, and a rejection names the field.
- [ ] A user needs one of the entry's roles to throw it. How this combines with `Signals.RequiredRole` and with managed API clients is defined.
- [ ] Saving a definition checks its signal names and mapping types against the catalog, and publication checks them again.
- [ ] Names without an entry keep working by default, so existing definitions and throws behave as before. A deployment can require an entry.
- [ ] Archiving an entry is refused or warned about while active subscriptions use its name.
- [ ] The hosted editor suggests cataloged names. The standalone editor works without the catalog.
- [ ] Documentation, automated tests, and real-browser verification of the catalog page, the **Signals** page, and the editor are complete.

## Later releases

- Versioned contracts.
- Entries limited to workflow families.
- Importing and exporting the catalog.

## Current state

Verified on 2026-11-17: not implemented.

- There is no catalog. The subscription summary shows which names have waits ([EVENT-04 decisions](event-04-signal-events.md#open-questions)).
- A signal name is any text that passes the rule in [SignalEventRules.cs](../../Flowbit/src/Flowbit.Shared/Models/SignalEventRules.cs). The editor suggests the names used in the open diagram.
- A payload is any JSON object of at most 256 KiB. Only each receiver's mappings check it.
- Authorization is one role setting for users and the `signals.throw` and `signals.read` scopes for managed API clients ([engine settings](../engine-settings.md#signal-settings)).

## Builds on

- The shared-variable catalog: contracts that a definition must match when it is saved and published, and archiving blocked by open references.
- Business calendars: a named resource that definitions reference, with a references table and refused deletion.
- `WorkflowDefinitionReadinessChecker`, which already checks named resources at publication.
- The hosted editor's optional capabilities, such as `timerSchedules`, for name suggestions.
- The typed mapping shape of message and signal mappings, as the form of a payload field.

## Open questions

- Is the catalog optional or required, and is that a deployment setting?
- Is a contract a list of typed fields, as mappings are, or a JSON Schema?
- Do an entry's roles replace `Signals.RequiredRole` or add to it?
- Grants for one client need a change to the client model. Does that belong here or to [EVENT-04K](event-04k-rename-managed-api-clients.md)?
- What happens to open subscriptions and saved definitions when a contract changes?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
