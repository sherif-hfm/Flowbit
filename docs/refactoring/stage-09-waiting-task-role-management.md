# Stage 9 — Extract waiting-task role management

[Plan index](README.md) · [Remaining-gaps roadmap](remaining-gaps-implementation-plan.md#stage-9--extract-waiting-task-role-management) · [Engine interface gap](gaps.md#2-engine-interface-breadth)

**Status: Implemented and locally accepted.** Prepared on 2026-09-20 against
commit `67945f7`. The extraction, endpoint switch, tests, and documentation
updates are in the working tree based on that commit. See the implementation
record below.

## Objective and boundary

Extract the four existing waiting-task role-policy operations into a scoped
`IUserTaskRoleManagementService` implemented by `UserTaskRoleManagementService`.
Normal user-task and multi-instance (MI) role endpoints inject this interface
directly. Remove the four operations from both `IWorkflowEngineService` and
`WorkflowEngineService`, reducing the engine interface from **37 to 33 methods**.
The engine must neither implement the new interface nor depend on the new
service as a forwarding facade.

Move the complete responsibility: permission checking, waiting-scope loading,
replacement validation, policy comparison, DTO mapping, audit assembly, and
transaction ownership. This is the accepted C# compatibility break in the
roadmap. HTTP routes, request/response DTOs, status codes and error text,
authorization, workflow JSON, persisted data, and runtime behavior stay unchanged.
No database migration, data rewrite, new runtime package, feature flag, repository
redesign, or compatibility overload is required.

Start from the accepted [Stage 8](stage-08-remove-query-detail-compatibility.md)
baseline and a fresh passing solution suite. Stage 9 may proceed alongside
Stage 7; it does not depend on Stage 5's residual manual browser acceptance or
observation of Stage 7's remote CI job. Coordinate shared documentation and use
isolated test hosts. Stages 10 and 11 are separate work.

## Source inventory and target ownership

Paths below are relative to the repository root. Proposed files are named in
code until implementation creates them.

| File or responsibility | Current owner | Stage 9 action |
| --- | --- | --- |
| [Engine interface](../../Flowbit/src/Flowbit.Service/Abstractions/Services.cs) | Declares the four role operations among 37 members. | Remove exactly those four declarations. |
| `Flowbit/src/Flowbit.Service/Abstractions/UserTaskRoleManagementAbstractions.cs` (new) | No focused interface exists. | Add the public four-method interface in `Flowbit.Service.Abstractions`, with the existing signatures. |
| [Role-management service](../../Flowbit/src/Flowbit.Service/Services/UserTaskRoleManagementService.cs) | Formerly `WorkflowEngineService.RoleManagement.cs`: four public operations, `RoleManagementScope`, mutation/scope helpers, permission checking, validation, comparison, mapping, and management-list status parsing. | Moved all role-operation behavior here; retained the status parser with the engine and deleted the old partial. |
| [Main engine partial](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.cs) | Management list/search, shared identity/lookup helpers, and other runtime commands. | Place `NormalizeManagedTaskStatus` beside the retained management queries. Preserve shared helpers and constructor dependencies used by other operations. |
| [User-task endpoints](../../Flowbit/src/Flowbit.Api/Endpoints/UserTaskEndpoints.cs) | `GetRoles` and `ChangeRoles` inject the engine. | Inject the focused service in these two handlers only. |
| [MI endpoints](../../Flowbit/src/Flowbit.Api/Endpoints/MultiInstanceExecutionEndpoints.cs) | `GetRoles` and `ChangeRoles` inject the engine. | Inject the focused service in these two handlers only; interrupt discovery/execution still uses the engine. |
| [Service DI](../../Flowbit/src/Flowbit.Service/DependencyInjection/ServiceCollectionExtensions.cs) | Registers the engine and focused query/projection services. | Add one scoped interface-to-implementation registration. Retain existing engine aliases. |
| [Infrastructure DI](../../Flowbit/src/Flowbit.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs) | Shares a scoped runtime repository across its ports and a scoped `UnitOfWork`/`AppDbContext`. | Preserve registrations and identity; verify the new service uses this composition. |
| [Role-policy repository](../../Flowbit/src/Flowbit.Infrastructure/Repositories/WorkflowRuntimeRepository.RolePolicies.cs) | Persists immutable replacements and updates task projections under the caller's transaction. | Keep SQL, internal save, locks, timestamps, and replacement behavior unchanged. |
| [Role capture/effective roles](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.RolePolicies.cs) and [resolver](../../Flowbit/src/Flowbit.Service/Services/UserTaskRolePolicyResolver.cs) | Capture roles at task entry and use persisted roles during runtime actions. | Leave ownership unchanged; the new service reuses `NormalizeManagedRoles` for explicit replacements. |
| [Role DTOs](../../Flowbit/src/Flowbit.Shared/Dtos/RoleManagementDtos.cs), UI client, and task-management page | Existing HTTP contracts and consumers. | No signature, serialization, or UI changes. |

### Focused interface and dependencies

Keep the existing method names, argument order, nullability, and cancellation
tokens. The proposed public interface is:

```csharp
public interface IUserTaskRoleManagementService
{
    Task<UserTaskRolePolicyDto?> GetUserTaskRolesAsync(
        long taskId, ActorContext actor, CancellationToken cancellationToken);

    Task<UserTaskRolePolicyDto?> GetMultiInstanceRolesAsync(
        long executionId, ActorContext actor, CancellationToken cancellationToken);

    Task<UserTaskRolesChangeAckDto?> ChangeUserTaskRolesAsync(
        long taskId, ChangeUserTaskRolesRequest request,
        ActorContext actor, CancellationToken cancellationToken);

    Task<UserTaskRolesChangeAckDto?> ChangeMultiInstanceRolesAsync(
        long executionId, ChangeUserTaskRolesRequest request,
        ActorContext actor, CancellationToken cancellationToken);
}
```

Use an `internal sealed` implementation, following the existing focused-service
pattern, with only `IWorkflowDefinitionRepository`,
`IWorkflowRuntimeRepository`, and `IUnitOfWork` constructor dependencies.
Register `AddScoped<IUserTaskRoleManagementService,
UserTaskRoleManagementService>()`. Resolve tests and endpoint consumers through
the public interface. No dependency on the engine, projections, variable stores,
settings, HTTP identity resolver, or service provider is needed.

### Helper boundary

Move `RoleManagementScope`, `ChangeWaitingRolesAsync`,
`LoadRoleManagementScopeAsync`, `EnsureTaskRoleManager`, `IsOpenRoleTask`,
`ValidateManagedRoleReplacement`, `SameManagedRolePolicy`, and
`ToRoleManagementDto` into the new service as private implementation details.

`NormalizeManagedTaskStatus` is the exception: the engine's
`ListManageableUserTasksAsync` and `SearchManageableUserTasksAsync` use it.
Move it within the engine before deleting the role-management partial. Preserve
its active default, `pending`/`open` handling, normalization, and error text.

The new service also needs the behavior of four small engine helpers:

- Definition lookup: `definitions.GetAsync` and the same missing-definition
  `WorkflowDomainException` message.
- Node lookup: `SingleOrDefault` and the same missing-node error.
- User normalization: blank becomes `anonymous`; otherwise trim. Continue using
  the actual caller's `actor.User`, not `ActingFor`, for this management audit.
- Role-set normalization: omit blank values, trim, and use
  `StringComparer.OrdinalIgnoreCase`.

Keep narrow private equivalents in the new service; retain the engine helpers
for their many other consumers. Do not expose engine internals or broaden this
stage into a shared authorization utility extraction. Keep explicit replacement
bounds in the existing `UserTaskRolePolicyResolver.NormalizeManagedRoles`;
actor role normalization and replacement validation have different contracts.

## Behavior to preserve

### HTTP dispatch and authority

| Operation | Focused service call | Existing advertised responses |
| --- | --- | --- |
| `GET /api/user-tasks/{taskId:long}/roles` | `GetUserTaskRolesAsync` | 200 `UserTaskRolePolicyDto`; 401, 403, 404, 409. |
| `POST /api/user-tasks/{taskId:long}/roles` | `ChangeUserTaskRolesAsync` | 200 `UserTaskRolesChangeAckDto`; 400, 401, 403, 404, 409. |
| `GET /api/multi-instance-executions/{executionId:long}/roles` | `GetMultiInstanceRolesAsync` | 200 `UserTaskRolePolicyDto`; 401, 403, 404, 409. |
| `POST /api/multi-instance-executions/{executionId:long}/roles` | `ChangeMultiInstanceRolesAsync` | 200 `UserTaskRolesChangeAckDto`; 400, 401, 403, 404, 409. |

- Retain both route groups' `.RequireAuthorization()`, tags, operation IDs,
  summaries, metadata, JSON binding, and existing exception mapping. Preserve
  actor resolution before service invocation, including selected audit claims.
  Forward the exact ID, request, actor, and cancellation token; null stays 404.
- Authority is an authored `taskRoleManagementRoles` match. Empty disables it;
  assignment managers, cancellation roles, task/action roles, claims, and
  delegation do not implicitly grant it. Preserve the permission check before
  disclosing activation-state conflicts once the instance/definition is found.
- These are management operations: retain their existing independence from
  personal inbox visibility, assignment, and claim ownership. Do not route
  scope loading through personal-task/action authorization helpers.
- Preserve initial missing-record null results and later conflict behavior:
  stopped instance, non-waiting or mismatched activation, missing policy,
  non-user-task node, or no active/pending items. A normal-task request targeting
  an MI child still returns the existing conflict directing the caller to its
  parent execution.
- Read operations remain reads without a new transaction or row-locking scheme.
  Do not strengthen their consistency contract during extraction.

### Transaction, lock order, and persistence

Preserve the following mutation sequence exactly. Calls remain sequential on
one DI scope and one DbContext; do not open nested scopes, parallelize EF calls,
or add a second transaction in the repository.

1. Reject a null request, nonpositive `ExpectedRolePolicyId`, and an overlong
   trimmed reason before opening the transaction. Blank reason becomes null;
   its existing 1,000-character limit uses .NET string length, not rune count.
2. Begin `unitOfWork.BeginTransactionAsync` with `await using`. Read the initial
   task or MI row without a lock to discover its instance. Missing records
   return null and dispose the uncommitted transaction.
3. Keep `GetInstanceForUpdateAsync(instanceId, false, ct)`. Its existing
   repository implementation locks the instance, active gateway executions,
   complex gateway states, active gateway branches, active execution tokens,
   and active MI executions in its established order (ID ordering within the
   relevant groups). `false` defers task locks; it does not mean only the
   instance is locked. Do not replace this call with a narrower custom query.
4. Load the current immutable definition, authorize the role manager, and
   re-read/check the target token, MI execution when applicable, and normal task
   under their existing update locks. Verify instance/token/node relationships,
   status, persisted policy, and active/pending counts as today. The initial
   unlocked lookup is never sufficient authority to mutate.
5. Validate the complete replacement, then compare it with the current policy.
   An identical policy returns `Changed=false` before checking a stale positive
   expected ID. It creates no replacement, audit, timestamp update, final save,
   or commit. A different policy with a stale ID throws the existing conflict.
6. Call `ReplaceUserTaskRolePolicyAsync` in this transaction. For MI it locks
   unfinished children in task-ID order, creates one immutable policy, repoints
   the parent and every active/pending child, and updates task `Roles` inbox
   projections and existing timestamps. Completed/cancelled items stay intact.
   **This repository method calls `dbContext.SaveChangesAsync` internally to
   obtain the replacement ID; that flush must remain inside the service's
   still-uncommitted transaction.**
7. Add exactly one `taskRolesChanged` history row, call `TouchInstanceAsync`,
   call the final `unitOfWork.SaveChangesAsync`, then commit. Preserve the
   existing DTO construction after commit; do not move save, commit, or mapping
   positions as cleanup.
8. An exception before commit disposes the transaction and rolls back every
   flushed row. The existing [UnitOfWork](../../Flowbit/src/Flowbit.Infrastructure/Repositories/UnitOfWork.cs)
   also clears tracking on uncommitted disposal. Preserve that behavior so a
   later save in the same scope cannot re-persist rolled-back mutations.

The effective mutation hierarchy remains instance → gateway state/branches →
token → MI → tasks. Keep the repository's broader lock acquisition and the
service's targeted rechecks together. No new gateway locks or lock algorithm
are introduced by this stage.

### Replacement, DTO, and audit details

- Require non-null task roles and flow arrays. Each editable selectable,
  non-default outgoing flow must appear exactly once, with non-null roles;
  reject missing, duplicate, extra, default, or engine-only flow entries. Start
  from the existing captured flow-role map and preserve entries outside the
  editable set, as the current implementation does.
- Continue trimming/deduplicating roles case-insensitively, retaining first
  normalized spelling/order. Enforce at most 100 input entries and 300 Unicode
  scalars per trimmed role through the resolver. Explicit empty lists remain
  unrestricted. Never reevaluate `rolesVariable` during a management edit.
- Equality is case-insensitive set equality for task and all captured flow
  roles, independent of submitted order/casing. Preserve validation before
  equality and equality before optimistic-conflict checking.
- Preserve `ClaimedBy`, assignee, `RequiresClaim`, ownership timestamps,
  completed-item policy IDs, entry/execution snapshots, and prior policies.
  Assignment-only changes do not invalidate the role-policy concurrency token.
- Keep DTO identity fields, persisted policy ID/roles, active/pending counts,
  and editable flows ordered by flow ID. Preserve the null-name `Flow #<id>`
  fallback and missing-captured-flow conflict. Do not mutate cached definitions
  or authored node/flow role lists to build a response.
- Keep all audit payload fields: previous/new policy IDs, previous/new task
  roles, previous/new flow-role maps, nullable MI execution ID, affected active
  plus pending task count, normalized `performedByRoles` with its existing
  `.Order()` behavior, and normalized reason. Preserve null-versus-empty values.
- Normal edits use `AddUserTaskHistoryAsync`; MI edits use `AddTokenHistoryAsync`
  once for the parent, not once per child. Preserve token/task/node linkage,
  null action ID, actual normalized caller, and `actor.AuditClaims`. These rows
  must not become flow-action/claim events or alter execution claim snapshots.

Dynamic role capture, effective runtime role checks, SQL inbox paging/batch
policy loading, management list/search, assignment/claim/cancellation, MI
completion/interrupt routing, version compatibility, and Worker activation keep
their existing owners. No additional engine responsibilities move in Stage 9.

## Implementation sequence

### 1. Establish the baseline and characterize the seam

1. Record the checkout commit/status; confirm Stage 8's three removed methods
   have not returned and the engine declares 37 methods. Run the full
   Docker-backed solution suite before changing application code.
2. Search source, tests, tools, and test hosts for the four role methods,
   engine implementations/proxies, direct engine constructors, and endpoint
   group mappings. Classify each receiver: similarly named UI HTTP-client
   methods must stay unchanged. Recheck private helper references before moving.
3. Capture the four existing endpoint metadata/OpenAPI operations and their
   current exception behavior. Reuse the API tests below; add only missing
   extraction-boundary characterization, including selected-claim attribution.
4. Add PostgreSQL-backed failure coverage around the existing role mutation
   path before moving it. Tests initially calling the engine migrate to the
   new interface in the extraction change. Keep externally observable assertions.

### 2. Extract the implementation and switch callers atomically

1. Add the focused public interface and internal implementation. Move the
   helpers and operations with their current control flow and error messages.
   Add only the narrow private helper equivalents described above.
2. Move `NormalizeManagedTaskStatus` beside the engine's management-list code.
   Keep `WorkflowEngineService.RolePolicies.cs` and shared engine helpers.
3. Register the new scoped service. Keep the engine's definition/runtime/UoW
   dependencies: all are still used elsewhere, so this stage should not change
   its constructor or the retained direct-construction test arguments.
4. Change only the four role handlers to inject the new interface. Remove the
   four engine declarations/implementations and delete the vacated partial.
   Add no forwarder or temporary compatibility surface to the final candidate.
5. Build this as one coherent, reviewable change with caller/test migration.
   Confirm 33 engine methods and no remaining engine role-operation calls.

### 3. Verify dependency injection and endpoint hosts

Add `UserTaskRoleManagementServiceTests.cs` for composition/transaction coverage
and `UserTaskRoleManagementEndpointTests.cs` for focused dispatch coverage under
`Flowbit/tests/Flowbit.Tests/`, unless an existing class can own a case clearly.
These filenames are proposed, not current coverage.

- Resolve the service through production registration: repeated resolution in
  one scope is the same service; separate scopes are different. Confirm the
  runtime repository and UoW participate in the same transaction/DbContext using
  real transactional evidence, not only service-descriptor assertions.
- Use an isolated endpoint host with a recording role service. If the host maps
  complete task/MI groups, register an engine substitute for the remaining
  handlers and make unexpected engine use fail. Do not infer that all these
  groups can start with no engine registration.
- Verify each role route reaches its corresponding focused operation and
  retains actor resolution and null-to-404 mapping. Use the HTTP pipeline for
  bearer rejection and request binding; a direct service call cannot prove 401.
- Inspect custom hosts that map either group or all API endpoints. The existing
  `OpenApiContractTests.RegisterHandlerServiceParameters` automatically registers
  interfaces in `Flowbit.Service.Abstractions`; the new interface fits that
  convention. Explicit registration hosts still need individual review.
- Extend the production OpenAPI check to the four changed operations: same
  operation IDs/tags/security/statuses/schemas, GET without a body, POST with
  `ChangeUserTaskRolesRequest`, and no service interface inferred as request data.
  Use targeted assertions rather than an unrelated whole-document snapshot.

### 4. Prove atomicity after the internal repository save

Use the existing PostgreSQL fixture and test-local repository/UoW decorators
or proxies, following established failure-injection patterns. Register them in
an isolated scope without changing production contracts or shared fixture DI.
Forward to the real repository/UoW and preserve cancellation tokens.

1. Cover a normal task and an MI execution containing completed, active, and
   pending children. Snapshot original policy IDs/values, ownership, timestamps,
   histories, and counts from a separate context.
2. Inject a failure after the real `ReplaceUserTaskRolePolicyAsync` returns,
   or at the following audit append. Confirm the internal save actually ran;
   throwing before replacement does not test this risk.
3. Separately inject a failure in the final service-level `SaveChangesAsync`
   after the repository's successful flush and audit staging. Retain at least
   one case that flushes final changes and then throws before commit to prove
   the whole transaction, including audit, rolls back.
4. Read through a fresh scope/connection: no replacement policy row, parent/child
   pointer change, roles/timestamp/ownership change, or audit is committed.
   Verify all unfinished children together and completed children unchanged.
   PostgreSQL sequence gaps after rollback are acceptable; do not assert ID reuse.
5. After disposal, remove the one-shot fault and perform a benign save in the
   original scope. Confirm rolled-back changes cannot leak from EF tracking.
6. Retain successful normal/MI changes and repeat/no-op cases as controls.
   No-op, validation, authorization, missing-scope, and conflict paths must
   commit no changes. Do not introduce catch-and-retry behavior to satisfy tests.

### 5. Run acceptance and update ownership documentation

Run the focused suites, final solution gate, and standalone browser regression
below. Review the complete diff for changed transaction ordering, permissions,
DTOs, repository SQL, or unrelated engine cleanup. Update documentation in the
same implementation change and record actual evidence before changing status.

## Test and acceptance matrix

| Contract/risk | Existing coverage to retain | Stage 9 evidence required |
| --- | --- | --- |
| Authority, immediate inbox effect, identical retry, stale conflict, invalid replacement | [TaskRoleManagementApiTests](../../Flowbit/tests/Flowbit.Tests/TaskRoleManagementApiTests.cs) | Keep all HTTP assertions; add uncovered empty-manager/forbidden, missing-scope, MI retry/conflict, flow-list validation, and no-write cases where needed. |
| Claims, assignment independence, pending normal tasks, MI completed history | Same API suite; [UserTaskRolePolicyPersistenceTests](../../Flowbit/tests/Flowbit.Tests/UserTaskRolePolicyPersistenceTests.cs) | Preserve claim/assignment/`RequiresClaim`, current-policy concurrency after assignment, active/pending counts, one parent audit, and completed/cancelled policy snapshots. |
| Entry capture, shared/instance role sources, saved interrupt roles | [RuntimeRolePolicyTests](../../Flowbit/tests/Flowbit.Tests/RuntimeRolePolicyTests.cs); [UserTaskRolePolicyResolverTests](../../Flowbit/tests/Flowbit.Tests/UserTaskRolePolicyResolverTests.cs) | Existing tests remain green; edits neither recapture variables nor mutate cached definitions. Assert another instance using the same definition is unaffected. |
| Worker activation and concurrent lifecycle commands | [RolePolicyActivationTests](../../Flowbit/tests/Flowbit.Tests/RolePolicyActivationTests.cs); concurrent-role-change API test | Preserve durable pending and sequential-child policy activation; retain real PostgreSQL races with completion/cancellation and one winner for competing replacements. Add MI race coverage if absent, using independent scopes and bounded coordination. |
| Compatible version switching | [RolePolicyVersionSwitchApiTests](../../Flowbit/tests/Flowbit.Tests/RolePolicyVersionSwitchApiTests.cs); [WorkflowVersionCompatibilityEvaluatorTests](../../Flowbit/tests/Flowbit.Tests/WorkflowVersionCompatibilityEvaluatorTests.cs) | Manager-only enabling remains compatible; manual policies and ownership survive compatible switches; existing role-source/action-contract blockers stay unchanged. |
| Scope identity, save/commit ownership, rollback | Existing persistence suite and [UnitOfWork](../../Flowbit/src/Flowbit.Infrastructure/Repositories/UnitOfWork.cs); proposed service tests | Real post-flush/final-save failure checks above pass for normal and MI work. A successful change commits policy, inbox projection, timestamps, and exactly one audit atomically. |
| Audit fidelity and immutable reads | Existing history-count assertion plus proposed service/API cases | Assert both normal and MI payload/linkage, trimmed reason, role ordering, actual actor, selected `AuditClaims` (including null versus empty), one event per actual change, no event for retries, and read-only GET behavior. |
| Endpoint dispatch, identity, binding, metadata | [ActorContextResolverTests](../../Flowbit/tests/Flowbit.Tests/ActorContextResolverTests.cs), [OpenApiContractTests](../../Flowbit/tests/Flowbit.Tests/OpenApiContractTests.cs), proposed endpoint tests | All four handlers use the focused service; valid identity/data, missing/invalid canonical identity, anonymous requests, null results, and unchanged contract metadata are covered. |
| Inbox/projection costs and unrelated commands | [RolePolicyInboxPerformanceTests](../../Flowbit/tests/Flowbit.Tests/RolePolicyInboxPerformanceTests.cs), [UserTaskApiTests](../../Flowbit/tests/Flowbit.Tests/UserTaskApiTests.cs), [InstanceProjectionTests](../../Flowbit/tests/Flowbit.Tests/InstanceProjectionTests.cs), full solution | Retain existing bounded query-count assertions and SQL membership/order/page behavior without lowering thresholds; engine command responses remain unchanged. |

Do not replace database concurrency/rollback tests with mocks. Reuse existing
coverage rather than duplicate every feature test, and add no generic framework
or brittle source-text tests just to enforce the method count. This stage does
not optimize MI role-management reads or reduce child loading.

## Validation commands and evidence

Run from the repository root with .NET 10 and reachable Docker. These single-line
commands use the same arguments in PowerShell and Bash. Record real results;
writing these commands is not evidence that they passed.

Baseline, build, and final full-suite gate:

```text
git status --short
git rev-parse --short HEAD
docker info
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet --logger "trx;LogFileName=stage-09-baseline.trx" --results-directory artifacts/stage-09/baseline
dotnet build Flowbit/Flowbit.slnx --nologo
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet --logger "trx;LogFileName=stage-09-final.trx" --results-directory artifacts/stage-09/final
git diff --check
```

Run the baseline before edits and the build/final command after the implementation.
Run this focused filter during implementation; the first two classes are the
proposed new classes above, so align names if tests are placed elsewhere and
confirm that their cases are actually discovered:

```text
dotnet test Flowbit/tests/Flowbit.Tests/Flowbit.Tests.csproj --nologo --verbosity quiet --filter "FullyQualifiedName~UserTaskRoleManagementServiceTests|FullyQualifiedName~UserTaskRoleManagementEndpointTests|FullyQualifiedName~TaskRoleManagementApiTests|FullyQualifiedName~UserTaskRolePolicyPersistenceTests|FullyQualifiedName~RuntimeRolePolicyTests|FullyQualifiedName~UserTaskRolePolicyResolverTests|FullyQualifiedName~RolePolicyActivationTests|FullyQualifiedName~RolePolicyVersionSwitchApiTests|FullyQualifiedName~WorkflowVersionCompatibilityEvaluatorTests|FullyQualifiedName~RolePolicyInboxPerformanceTests|FullyQualifiedName~UserTaskApiTests|FullyQualifiedName~InstanceProjectionTests|FullyQualifiedName~ActorContextResolverTests|FullyQualifiedName~ActorContextApiTests|FullyQualifiedName~OpenApiContractTests" --logger "trx;LogFileName=stage-09-focused.trx" --results-directory artifacts/stage-09/focused
```

Run the separate Chromium regression using the existing
[browser harness instructions](../../Flowbit/tests/Flowbit.BrowserTests/README.md):

```text
dotnet publish Flowbit/src/Flowbit.Api/Flowbit.Api.csproj -c Release -o artifacts/browser/hosts/api /p:UseAppHost=false
dotnet publish Flowbit/src/Flowbit.Ui/Flowbit.Ui.csproj -c Release -o artifacts/browser/hosts/ui /p:UseAppHost=false
dotnet build Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release
pwsh Flowbit/tests/Flowbit.BrowserTests/bin/Release/net10.0/playwright.ps1 install chromium
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore --logger "trx;LogFileName=stage-09-browser-smoke.trx" --results-directory artifacts/stage-09/browser
```

The browser project is outside both solution files. On Linux, follow its README
for browser system dependencies. Record browser version, actual ephemeral
localhost URLs, E1–E5/R1–R6 interactions, editor viewports 1440×900/1024×768,
runtime viewports including 390×844, console/page errors, and artifact paths.
The smoke suite is regression coverage; it does not establish dedicated role-edit
UI coverage or close Stage 5/7's outstanding manual/remote gates. Stage 9 changes
no UI files or appearance. If implementation expands into HTML/CSS or
interactions, apply the separate
[real-browser verification requirements](../../AGENTS.md#mandatory-real-browser-ui-verification)
with scenario-specific clicks/typing and screenshots of visual changes.

Also check all changed Markdown links/anchors and perform a final receiver-aware
search for the removed engine operations. Keep failure injection isolated and
avoid leaving runnable pending jobs behind in shared fixtures. Record baseline
and final passed/failed/skipped counts, new test cases, warnings, unavailable
checks, and the candidate commit. A historical pass or an environment failure
does not satisfy this stage's acceptance gate.

## Documentation and C# migration

| Document | Implementation-time update or review |
| --- | --- |
| [Runtime reference](../../Flowbit/README.md) | Explain the focused role service, direct endpoint injection, transaction ownership, and in-process caller migration. Keep HTTP examples and role-policy behavior unchanged. |
| [Contributor instructions](../../AGENTS.md) | Update Service-layer ownership and the waiting-task role-policy section to identify the focused owner; preserve the lock/persistence invariants. |
| [Gap inventory](gaps.md), [roadmap](remaining-gaps-implementation-plan.md), [plan index](README.md), [documentation home](../index.md), and this plan | Record actual implementation/acceptance status and interface count; refresh affected engine measurements with commit/date. Keep broader deferred gaps and Stage 5/7 gates open. |
| [API reference](../api-guide.md) and [developer guide](../developer-guide.md) | Compare the four routes, DTOs, auth/error handling, tests, and generated development OpenAPI. Edit only if existing ownership wording becomes inaccurate; any public contract difference is a regression to resolve. |
| [UI guide](../ui-guide.md), [node reference](../node-reference.md), [deployment guide](../deployment.md), and [example catalog](../../examples/README.md) | Review affected role-management descriptions. No behavior/configuration/example/screenshot change is expected; record that assessment without cosmetic edits. |

For in-process callers, replace injected `IWorkflowEngineService` with
`IUserTaskRoleManagementService` for these four methods and keep the existing
arguments. Consumers of other engine methods can inject both focused and engine
interfaces. Existing direct engine construction remains unchanged; new role
tests resolve the focused service from DI. External HTTP clients and
`WorkflowApiClient` keep their routes/methods. Do not add C# service calls to
public HTTP integration examples.

Keep Stage 8's completed record intact and document this separate four-method
break here. No production migration or deployment configuration step is added.

## Acceptance checklist

- [x] Fresh baseline recorded; Stage 8's accepted removal remains intact.
- [x] One scoped role-management service owns all four operations and their
  permission, scope, validation, mapping, audit, and transaction behavior.
- [x] All four handlers inject it directly; engine interface is 33 methods,
  with no role-service dependency, forwarding overload, or constructor churn.
- [x] Management-list status parsing and dynamic role capture remain with their
  existing owners; all HTTP/JSON contracts and error messages are preserved.
- [x] Existing lock acquisition, internal flush, final save/commit, rollback
  cleanup, actor claims, ownership, and completed-item history are preserved.
- [x] Focused service/endpoint/OpenAPI, PostgreSQL rollback/concurrency, retained
  query-count, full solution, and standalone Chromium checks pass with evidence.
- [x] Runtime/contributor ownership and C# migration are documented; planning
  links/statuses are updated and validated; `git diff --check` passes.
- [x] No migration, runtime dependency, UI behavior change, or deferred engine
  extraction is included. Any unavailable acceptance check remains explicitly open.

## Implementation record — 2026-09-20

Implemented in the working tree based on `67945f7464ef0c75b85018cfa7146b72125dca2d`.
No implementation commit has been created. At entry, Stage 8's accepted
query/detail removal was intact (37 engine methods; no
`ListInstancesAsync` / `SearchInstancesAsync` / `GetInstanceAsync`). Planning
edits already present in the documentation home, plan index, gap inventory,
and remaining-gaps roadmap were preserved and extended with this record.

### Final implementation and review

- Added public `IUserTaskRoleManagementService` and internal sealed
  `UserTaskRoleManagementService` with the existing four signatures and only
  `IWorkflowDefinitionRepository`, `IWorkflowRuntimeRepository`, and
  `IUnitOfWork` constructor dependencies. Moved `RoleManagementScope`,
  mutation/scope helpers, permission checking, validation, comparison, mapping,
  and the four operations with their current control flow and error text.
- Registered `AddScoped<IUserTaskRoleManagementService,
  UserTaskRoleManagementService>()`. The four role handlers inject that
  interface after actor resolution; null remains 404. Interrupt discovery and
  execution still use the engine.
- Removed the four declarations from `IWorkflowEngineService` and deleted
  `WorkflowEngineService.RoleManagement.cs`. Measured interface count:
  **37 → 33**; every other member remains. The engine neither implements nor
  depends on the new service. Its constructor and retained direct-construction
  test arguments are unchanged.
- Moved `NormalizeManagedTaskStatus` beside the engine's management list/search
  methods, preserving the active default, `pending`/`open` handling, and error
  text. `WorkflowEngineService.RolePolicies.cs` and
  `UserTaskRolePolicyResolver.NormalizeManagedRoles` stay with their previous
  owners.
- Isolated PostgreSQL tests wrap the production runtime repository and UoW after
  `AddServiceLayer` / `AddInfrastructure`. `ReplaceUserTaskRolePolicyAsync`
  return IDs prove the internal flush ran; `afterReplace` / `onSave` /
  `onCommit` faults prove the still-uncommitted transaction rolls back normal
  and MI unfinished-child updates, then a later save in the same scope writes
  nothing. Successful changes commit policy, inbox projection, timestamps, and
  exactly one `taskRolesChanged` audit. Reads write nothing; null versus empty
  `AuditClaims` is preserved.
- `RoleApiFactory` replaces the focused service and throws if the engine is
  resolved. All four routes forward the exact ID, request, and resolved actor.
  Bearer rejection, invalid canonical identity, missing-scope 404, and POST
  binding without a body stay on the HTTP pipeline. Production OpenAPI for the
  four operations is generated through `IOpenApiDocumentProvider` on that host
  (operation IDs `get/postUserTasksByTaskIdRoles` and
  `get/postMultiInstanceExecutionsByExecutionIdRoles`).
- Extended `TaskRoleManagementApiTests` with empty-manager/missing-scope,
  MI retry/conflict/flow-list validation and sibling-instance isolation.
  The independent-scope MI replacement race now lives in the focused service
  suite with explicit database-lock coordination (see the review follow-up).
  Existing authority, inbox-effect,
  claim/assignment, persistence, activation, version-switch, and inbox
  query-count suites remain.

### Validation evidence

Docker Engine **29.6.2** and .NET SDK **10.0.301** were available. All test
runs used their own disposable PostgreSQL hosts.

| Gate | Command / evidence | Result |
| --- | --- | --- |
| Baseline | `dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet --logger "trx;LogFileName=stage-09-baseline.trx" --results-directory artifacts/stage-09/baseline` | First run **1,899 passed, 1 failed, 0 skipped**. The failure was the pre-existing flake `HttpServiceTaskInvokerTests.InvokeAsync_UsesNodeTimeoutAndDoesNotLeakDestinationInError` (4.76 s vs the 0.5–3 s bound). An immediate retry of that test passed. Stage 8's three removed methods were still absent. |
| Focused | The focused filter above, `--logger "trx;LogFileName=stage-09-focused.trx" --results-directory artifacts/stage-09/focused` | **128 passed, 0 failed, 0 skipped** |
| Final solution | `dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet --logger "trx;LogFileName=stage-09-final.trx" --results-directory artifacts/stage-09/final` | **1,929 passed, 0 failed, 0 skipped** |
| Documentation checks | Relative links/anchors in the changed Markdown files; `git diff --check` | Planning and ownership links resolve; `git diff --check` reports only existing CRLF warnings, no whitespace errors |
| Browser preparation | Both Release publishes and the standalone Release build using the commands above | Passed. `pwsh` is not on PATH in this environment, so `playwright.ps1 install chromium` could not run; the suite used the already-installed Chromium from the prior checkout. |
| Standalone browser | Exact browser test command above; `artifacts/stage-09/browser/stage-09-browser-smoke.trx` | **20 passed, 0 failed, 0 skipped** |

The initial implementation's final solution count was 1,929: 29 new cases (11 focused service, 14
endpoint dispatch, one production OpenAPI, and three API-suite additions)
on the Stage 8 baseline of 1,900. Existing NU1903 (SSH.NET 2025.1.0) and
xUnit collection-size analyzer warnings were observed in the
build/browser-project logs; this stage makes no dependency changes.

Browser evidence is under
`artifacts/browser/runs/20260920-174350-b31dae73/`, including the manifest,
per-scenario diagnostics, screenshots, and host logs. The run used headless
**Chromium 151.0.7922.34** with API **http://127.0.0.1:62564**, UI
**http://127.0.0.1:62566**, and editor **http://127.0.0.1:62568**. Those
fixture-owned hosts stopped after the run. E1–E5 exercised load/edit/save/reload,
node/lane dragging, validation, and keyboard/focus at **1440×900** and
**1024×768**. R1–R5 exercised task lifecycle, navigation, identity replacement,
gateway/MI detail, and polling/disposal at **1440×900**; R6 exercised responsive
navigation, scrolling, and focus at **1024×768** and **390×844**. All product
scenarios had zero page errors, console errors/warnings, failed requests, and
unexpected dialogs. The three harness regressions passed with their deliberately
injected diagnostics, which are not product failures.

### Documentation and boundaries

Updated the runtime ownership/migration reference, AGENTS service ownership,
gap inventory, roadmap, plan index, documentation home, and this record. The
API reference, developer guide, UI guide, node reference, deployment guide, and
example catalog were compared with the final handlers, DTOs, auth/error
handling, tests, and generated OpenAPI; their HTTP, authoring, and operations
descriptions remain accurate and required no edits. No workflow JSON, examples,
images, deployment configuration, or public walkthrough changed.

No UI code or visual appearance changed. The automated browser regression does
not close Stage 5's separate manual acceptance or Stage 7's remote CI
observation. Stages 10–11 remain deferred. Changes are left uncommitted for
review.

### Review follow-up — 2026-09-20

Closed the verification gaps identified during review without changing runtime
code or HTTP behavior:

- Normal and MI success tests compare the complete `taskRolesChanged` payload,
  including previous/new policy IDs, task/flow roles, affected count, normalized
  role ordering, and null/trimmed reasons. They verify workflow/token/node/task
  linkage, actual-caller attribution when `ActingFor` is populated, and exact
  selected-claims objects, including an empty object.
- All four endpoint dispatch cases enable the `department` audit allowlist and
  assert both repeated claim values while excluding an unselected claim. POST
  cases assert the complete replacement request; forwarded cancellation tokens
  must be cancellable and initially uncancelled.
- Every MI rollback fault case now performs a later save in the original scope,
  asserts that tracking is empty, and compares committed state again.
- The MI replacement race moved out of the sibling-instance HTTP test into a
  focused PostgreSQL test. It pauses the first transaction after its internal
  replacement flush and observes the second connection waiting on that exact
  backend's instance-row lock through `pg_stat_activity` and `pg_blocking_pids`.
  Only then does it release the winner. The loser must recheck the policy and
  return the existing conflict; one replacement and one audit are committed,
  all unfinished children receive the winning policy, and the completed child
  retains its original policy. Coordination has a 30-second deadline and
  releases/cancels/drains both operations before scope disposal.
- Production OpenAPI tests now assert both success response schemas and the
  required `int64` path parameter for all four operations. The documentation
  home now correctly lists only Stages 10–11 as planned.

The focused command above, with logger
`trx;LogFileName=stage-09-review-fixes-focused.trx` and results directory
`artifacts/stage-09/review-fixes-focused`, passed **129/129**, with no failures
or skips. The full solution command
`dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet --logger "trx;LogFileName=stage-09-review-fixes-final.trx" --results-directory artifacts/stage-09/review-fixes-final`
passed **1,930/1,930**, with no failures or skips. This is 30 new cases above
Stage 8 (12 focused service, 14 endpoint dispatch, one production OpenAPI, and
three API-suite additions). The original API case retains its sibling-instance
assertions. Relative links/anchors and `git diff --check` pass; existing NU1903
and collection-size analyzer warnings remain. No UI files or browser
scenarios changed, so the successful review browser run remains applicable:
`artifacts/stage-09/review-browser/stage-09-review-browser.trx` (**20/20**),
with diagnostics in `artifacts/browser/runs/20260920-180243-6b478988/`.

## Delivery and rollback

Keep preparatory characterization reviewable and the interface removal,
implementation move, DI registration, endpoint/caller migration, and documentation
in one buildable extraction candidate. Report code changes, tests, documentation,
remaining limitations, and commit identifiers; do not mark implementation complete
when a required gate is unexecuted.

Rollback is a code revert and normal API/Worker rebuild/redeployment, including
reverting dependent in-process callers together. Restore the engine operations
and handler injection as one unit; no stored-policy/data conversion is necessary.
Retain applicable behavior tests. Defer inbox/assignment authorization, routing,
gateways, messages, jobs, editor transitions, and administrative UI components
to their own stages.
