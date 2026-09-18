# Stage 3 — Extract instance detail and execution projections

**Status: Implemented.** Instance detail assembly, execution-position
projection, grouped multi-instance progress, and version-change/variable-update
audit loading now live in a scoped
`WorkflowInstanceProjectionService` (`IWorkflowInstanceProjectionService`)
in the Service project. The engine forwards detail and slim-ack projections to
it; task capability assembly, routing, authorization, and settings caching
remain in the engine. See the implementation record at the bottom of this
document. Stage 1 is a prerequisite and was completed first; Stage 2 was also
completed before this stage.

[Plan index](README.md) · [Next: editor validation](stage-04-editor-validation.md)

## Objective and boundary

Move instance detail assembly, execution-position projection, and their pure
mapping helpers out of `WorkflowEngineService`. This is one bounded reduction of
engine responsibilities; completion does not mean that the whole engine has
been decomposed.

The pre-extraction implementation was in
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

## Implementation record

Implemented on top of Stage 1 (`e60d26d`) and Stage 2 (`b711328`). The
implementation matches the target ownership above with these concrete choices:

- `IWorkflowInstanceProjectionService` (`InstanceProjectionAbstractions.cs`)
  exposes `GetDetailAsync`, `BuildExecutionAsync(instance, includeHistory, ct)`,
  and grouped/single `GetMultiInstanceProgressAsync` overloads (the single-ID
  overload delegates to the grouped read). The internal
  `WorkflowInstanceProjectionService` takes the runtime and definition
  repository ports plus the optional variable-update audit repository and
  workflow-variable store; it has no engine, unit-of-work, or scope-factory
  dependency. The public `InstanceExecutionProjection` record (the same five
  fields) moved to `Flowbit.Service.Models`.
- `RuntimeProjectionMapper` gained the moved pure mappings: workflow
  cloning/redaction (`ToRuntimeWorkflowDetail`, `[redacted]` extent unchanged),
  multi-instance progress (`ToProgress`), and the version-change
  audit/summary/direction/issue helpers. The VersionChange and
  VersionChangeBatch partials now call those shared helpers; message
  authentication helpers and authentication constants stayed with the engine.
  The engine's variable-update serializer options moved with the audit loader.
- The engine constructor takes `IWorkflowInstanceProjectionService`, and the
  now-unread optional `variableUpdates` constructor parameter was removed (the
  audit repository moved to the projection service). Every former
  `BuildDetailAsync`/`BuildExecutionProjectionAsync` call site now
  calls the service; the two `BuildProgressAsync` overloads are thin
  delegations, and `IWorkflowEngineService.GetInstanceAsync` remains a
  compatibility forward. The projection service is registered scoped in
  `AddServiceLayer`; the shared scoped `WorkflowRuntimeRepository` preserves
  one DbContext per scope. No HTTP route, DTO, JSON shape, error text, commit
  point, or lock order changed.
- `Flowbit.Service` grants `InternalsVisibleTo Flowbit.Tests` (matching the
  Api/Ui projects) so test fixtures can compose the internal service with the
  same proxied repositories.

Validation (actual runs):

- Characterization first (`InstanceProjectionTests`): a database-backed detail
  projection records **17 reader commands** as the warm-definition baseline and
  **18** for the cold-cache first projection (the warm baseline plus the
  one-time immutable definition lookup), and proves projection-only reads
  write no history/variable/audit rows; a second test proves the reader count
  is independent of multi-instance child items (1 vs 6) and version-change
  audit/definition volumes (1 vs 5 audits across 3 definitions); a
  current-only (slim-ack) execution projection records **7 reader commands**
  (current tokens/tasks/multi-instance executions, grouped progress, current
  gateway executions, and complex states; no branch read without an active
  gateway execution) — that baseline was recorded post-extraction against the
  moved code, whose pre-extraction equivalence is covered by the full suite;
  a mapper test proves redaction of message clientSecret/headerValue and
  task-distribution clientSecret leaves the cached record's authored secrets
  intact; a DI-scope test proves projection reads see flushed uncommitted
  state inside the caller's transaction and a rollback leaves a fresh scope
  seeing nothing.
- After extraction the same characterization tests pass unchanged (still 17
  warm readers), the engine forwarding tests cover `GetInstanceAsync`, and the
  DI composition test asserts the projection service is scoped and resolvable.
- Focused suites passed during implementation. Final gate:
  `dotnet test Flowbit/Flowbit.slnx --no-restore --nologo --verbosity quiet`
  → **1,867 passed, 0 failed, 0 skipped**; `git diff --check` clean (CRLF
  normalization warnings only, no whitespace errors). Docker Desktop was
  started for the Testcontainer-backed PostgreSQL tests.
- Documentation updated: this file, the [plan index](README.md),
  [Flowbit/README.md](../../Flowbit/README.md), the service-layer ownership
  note in [AGENTS.md](../../AGENTS.md), and the [gap inventory](gaps.md)
  (remaining engine responsibilities, current partial-file line counts, and
  the measured reader baselines). A follow-up review caught and fixed two
  documentation/validation gaps: the cold-cache and current-projection reader
  baselines above were added after the initial record, and a Windows-1252
  round-trip had corrupted the gap inventory's en/em dashes; `gaps.md` was
  restored from the committed UTF-8 text and its Stage 3 edits reapplied.
  API and developer guides still describe behavior, not ownership, so they
  needed no edits.
