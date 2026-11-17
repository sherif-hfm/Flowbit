# EVENT-04K: A general name for managed API clients

[Parent EVENT-04](event-04-signal-events.md) · [Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Integration |
| Effort | M |
| Priority | Not ranked; last of eleven in the [EVENT-04 follow-up order](README.md#event-04-follow-up-subtasks) |
| Parent | [EVENT-04](event-04-signal-events.md) |
| Depends on | [EVENT-04](event-04-signal-events.md) (Done) |
| Related | [EVENT-04F](event-04f-signal-catalog.md) |

## Summary

Rename "shared-variable clients" to "API clients" in the page, the HTTP routes, and the documentation. The same clients also throw and read signals.

Do this together with the next change that adds a scope or a grant for one client, such as [EVENT-04F](event-04f-signal-catalog.md). On its own it adds no capability.

## Why it adds value

- The name misleads. A client that only throws signals is created on a page called **Shared-variable clients**, through routes under `/api/shared-variable-clients`.
- Each further scope makes the name less accurate.

## First release acceptance criteria

- [ ] The page, its navigation entry, and the guides call them API clients. The old page address redirects to the new one.
- [ ] The HTTP API offers the client operations under a general route. The plan decides whether the old routes stay as aliases, and for how long.
- [ ] Scope names, client IDs, and secrets are unchanged. Every existing client keeps working without rotation.
- [ ] The plan decides whether the table and the code names change. A table rename documents its rollout order.
- [ ] The OpenAPI catalog, the API reference, the UI guide, the deployment guide, and the database schema reference are updated, with real-browser verification of the page.

## Later releases

- Removing the old routes, if they stay as aliases first.

## Current state

Verified on 2026-11-17: not implemented.

- Clients are stored in `shared_variable_clients` and managed through six operations under `/api/shared-variable-clients` ([SharedVariableClientEndpoints.cs](../../Flowbit/src/Flowbit.Api/Endpoints/SharedVariableClientEndpoints.cs)).
- The page is `/shared-variable-clients` ([SharedVariableClients.razor](../../Flowbit/src/Flowbit.Ui/Components/Pages/SharedVariableClients.razor)), described under [Shared-variable clients](../ui-guide.md#shared-variable-clients-and-distribution-search).
- `SharedVariableClientScopes` in [SharedVariableDtos.cs](../../Flowbit/src/Flowbit.Shared/Dtos/SharedVariableDtos.cs) holds all four scopes: `shared-variables.read`, `shared-variables.write`, `signals.throw`, and `signals.read`.
- [EVENT-04](event-04-signal-events.md#13-delivery-sequence-rollout-and-risks) accepted the old name as a risk and recorded the rename as later work.

## Builds on

- The existing client authentication, authorization requirement, and credential-source middleware, which need new names but no new behavior.

## Open questions

- Does the table change its name, or only the routes, the page, and the code?
- Do the old routes remain as aliases, and when are they removed?
- Is the rename worth a release of its own, or does it ship with the next scope?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
