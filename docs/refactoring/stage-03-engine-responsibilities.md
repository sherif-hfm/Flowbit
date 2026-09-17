# Stage 3 — Extract instance detail and execution projections

**Status: Planned.** This document describes future implementation. The types and
ownership below are targets, not current capabilities. Complete Stage 1 before
beginning this stage; Stage 2 is recommended but is not a technical dependency.

[Plan index](README.md) · [Next: editor validation](stage-04-editor-validation.md)

## Objective and boundary

Move instance detail assembly, execution-position projection, and their pure
mapping helpers out of `WorkflowEngineService`. This is one bounded reduction of
engine responsibilities; completion does not mean that the whole engine has
been decomposed.

The current implementation is in
[WorkflowEngineService.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.cs)
(`BuildDetailAsync`, `BuildExecutionProjectionAsync`, and `BuildProgressAsync`)
and the audit reader in
[WorkflowEngineService.VersionChange.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.VersionChange.cs)
(`BuildVersionChangeAuditDtosAsync`). Detail and execution projections are used
by both queries and mutation responses, including
[administrative execution](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.AdministrativeActions.cs).

Keep routing, transaction ownership, claim/action authorization, inbox visibility,
role-policy capture, and variable writes in their current owners. Task capability
assembly (`BuildUserTaskPresentationAsync`, `BuildUserTaskCapabilities`, and
`GetEligibleUserTaskFlows`) is outside this extraction: it evaluates permissions
and stored-state conditions, rather than merely formatting data.

## Target ownership and implementation sequence

1. Introduce a scoped `WorkflowInstanceProjectionService` in the Service project
   behind `IWorkflowInstanceProjectionService`. The implementation can be internal;
   the interface must be public because the public engine constructor accepts it.
   This is a Service-layer port, not a new HTTP API. It owns these operations:
   - `GetDetailAsync(long instanceId, CancellationToken)` returning
     `Task<InstanceDetailDto?>`.
   - `BuildExecutionAsync(WorkflowInstanceRecord instance, bool includeHistory,
     CancellationToken)` returning `Task<InstanceExecutionProjection>`.
   - `GetMultiInstanceProgressAsync(IReadOnlyCollection<long> executionIds,
     CancellationToken)` returning the existing ID-to-progress DTO dictionary;
     the single-ID overload delegates to it.
2. Move the private `InstanceExecutionProjection` record into a public Service
   model so it can be returned by that interface. Preserve the same five fields:
   execution positions, multi-instance progress,
   gateway executions, complex gateway states, and completion. Do not add fields
   to public DTOs or change HTTP routes, request shapes, response shapes, or
   error contracts.
3. Inject the existing definition/runtime repository ports, optional variable
   update audit repository, and workflow-variable store into the projection
   service. Stage 2 keeps those ports unchanged; retain the same underlying
   scoped repository instances established in Stage 1. The projection
   service must not depend on the engine, `IUnitOfWork`, a scope factory, or a
   service locator. Preserve the existing absent-audit/store behavior for test
   compositions that omit those optional dependencies.
4. Move detail loading, variable/history DTO assembly, execution projection,
   version-change audit loading, variable-update audit loading, and grouped
   multi-instance progress loading into this service. Preserve query order,
   grouped reads, ordering, null behavior, and existing exception messages.
   Move the local definition/node lookup needed by detail assembly without
   broadening this change into graph-helper restructuring.
5. Put the moved pure mappings in an internal `RuntimeProjectionMapper`: runtime
   workflow cloning/redaction, fault information, task work summaries,
   multi-instance progress, and version-change audit/version summary/direction.
   Extend the Stage 1 `RuntimeProjectionMapper`, which already owns fault and
   work-summary mappings; do not introduce a second copy.
   Update version-change preview/result mapping to use the same pure version
   helpers. Keep authentication constants and unrelated message helpers with
   their current owner.
6. Register the projection service as scoped in
   [service DI](../../Flowbit/src/Flowbit.Service/DependencyInjection/ServiceCollectionExtensions.cs)
   through its interface and inject that interface into the engine. Replace the
   old projection methods with thin
   delegations so command call sites retain their exact positions relative to
   `SaveChangesAsync` and `CommitAsync`. Update direct engine constructions in
   test fixtures to provide the projection service. Keep
   `IWorkflowEngineService.GetInstanceAsync` as a forwarding compatibility method;
   do not move the HTTP authorization boundary in this stage.

## Invariants and deliberately deferred work

- **Scope and transactions:** reuse the caller's scoped repositories/DbContext.
  Projection reads must see flushed state inside an existing ambient transaction.
  They must not create, commit, dispose, or suppress that transaction. Preserve
  post-commit reads where the existing command already commits before building
  its response. Do not parallelize DbContext queries.
- **Secrets and immutable definitions:** clone the cached workflow definition
  before redacting message `clientSecret`, message `headerValue`, and task
  distribution `clientSecret` to `[redacted]`. Neither the stored definition nor
  another caller's cached definition may change. Retain the existing serializer
  behavior and redaction extent.
- **Projection semantics:** preserve full-history versus current-only reads,
  exclusion of merged tokens, active/pending task preference, deterministic
  token/progress ordering, parent gateway linkage, and existing normal/terminate
  completion selection. Keep progress and work summaries as grouped database
  reads; do not load all child items to calculate counts.
- **Audit:** retain original history attribution, repeated actor claims,
  delegation, administrative batch correlations, variable-update audit links,
  version-change ordering, and null-versus-empty values. Reading a projection
  creates no history, claim, task, variable, or evidence rows.
- **Settings and authentication:** the projection service has no settings cache
  and does not evaluate actor capabilities. Leave `_settingsCache`,
  `LoadSettingsAsync`, `RefreshSettingsAsync`, captured request time, and shared
  variable lock selection in their current owners. Message start/catch
  authentication stays deferred: it refreshes settings on each authentication,
  reads shared values with different lock requirements, and constructs distinct
  authentication/validation contexts. Extracting it requires a separate plan
  covering rotation, replay proofs, idempotency, and verified actor attribution.
- **Other deferred work:** inbox queries and shared task authorization,
  service/script execution, pass-through routing, gateway state, conditional
  event coordination, and splitting the remainder of the engine interface are
  outside this stage. Add no database migration or new feature flag.

## Tests and acceptance

Start from the passing suite established in the earlier stages. Use existing
tests before adding cases; add focused regression tests only for an uncovered
extraction risk.

Existing coverage to retain includes
[InstanceDtoContractTests](../../Flowbit/tests/Flowbit.Tests/InstanceDtoContractTests.cs),
[MessageCatchApiTests](../../Flowbit/tests/Flowbit.Tests/MessageCatchApiTests.cs),
[InstanceVersionChangeApiTests](../../Flowbit/tests/Flowbit.Tests/InstanceVersionChangeApiTests.cs),
[InstanceReactivationApiTests](../../Flowbit/tests/Flowbit.Tests/InstanceReactivationApiTests.cs),
[GenericGatewayApiTests](../../Flowbit/tests/Flowbit.Tests/GenericGatewayApiTests.cs),
[MultiInstanceApiRegressionTests](../../Flowbit/tests/Flowbit.Tests/MultiInstanceApiRegressionTests.cs),
[InstanceVariableUpdateApiTests](../../Flowbit/tests/Flowbit.Tests/InstanceVariableUpdateApiTests.cs),
and [AdministrativeActionRuntimeModeApiTests](../../Flowbit/tests/Flowbit.Tests/AdministrativeActionRuntimeModeApiTests.cs).

Acceptance scenarios:

- Unknown instance returns the same null/404 result; running, completed,
  cancelled, faulted, and reactivated instances retain equivalent detail data.
- Full detail retains history, audit ordering, multi-instance summaries,
  gateway state, completion, `FinishedAt`, and `HistoryPrunedAt`; slim start,
  user-action, message, and administrative responses retain their current
  execution positions and completion values.
- Redacted responses contain no authored credential/header-value secrets, and
  projecting a cached definition leaves the original unchanged. Include task
  distribution redaction as well as message redaction.
- A database-backed test opens a transaction in one DI scope, writes and flushes
  representative instance/audit state, reads it through the projection service,
  then rolls back. A fresh scope sees no committed changes. This verifies shared
  scope/transaction visibility without changing command commit points.
- Projection-only reads add no audit or workflow rows. Existing administrative
  rollback and downstream-failure tests retain their behavior. Capture SQL reader
  counts for representative detail/current projections before extraction and
  verify that the refactor adds no per-child or per-definition queries.
- Message setting-rotation and secret-redaction tests still pass; inbox and
  role-policy tests remain in the full-suite gate because shared helper changes
  must not alter their behavior.

After restore and the documented Docker prerequisite, run the complete suite
from the repository root (the command works in PowerShell and Bash):

```text
dotnet test Flowbit/Flowbit.slnx --no-restore --nologo --verbosity quiet
git diff --check
```

Completion requires a successful full suite with no unexplained new skips, plus
the projection regression checks above. Record the actual command/results;
do not reuse historical pass counts. No rendered UI or interactions change, so
real-browser verification is not required for this stage.

## Documentation, commits, and rollback

Update the architecture/ownership description in
[Flowbit/README.md](../../Flowbit/README.md) and the affected runtime section of
[AGENTS.md](../../AGENTS.md) in the implementation change. Review
[API reference](../api-guide.md) and [developer guide](../developer-guide.md)
against actual behavior; edit those pages only if an existing implementation
description needs correction. Public integration examples remain HTTP-only.
Validate changed relative links and describe implemented behavior only after the
code exists.

Use two reviewable commits: first, any missing characterization tests and the
recorded query-count baseline; second, the complete extraction, DI/caller
updates, and architecture documentation. Each commit must build and pass its
relevant tests. Revert the second commit to roll back this stage while retaining
the characterization coverage. No data conversion or operational rollback is
needed. Stop the extraction if preserving the current scope, query counts, or
response semantics would require changes to routing or authorization; record the
specific dependency and revise this stage before expanding it.

At completion, update the [engine/interface inventory](gaps.md#1-engine-core-responsibilities)
with the remaining caller groups and any transaction/authorization dependencies
encountered. Retained compatibility forwards mean interface member counts do
not shrink in these stages. Use that evidence to choose one later command
responsibility; it is not authorization to add routing, message authentication,
or a broad interface rewrite to this stage.
