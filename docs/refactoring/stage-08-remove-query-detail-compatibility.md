# Stage 8 — Remove query and detail compatibility methods

[Plan index](README.md) · [Remaining-gaps roadmap](remaining-gaps-implementation-plan.md#stage-8--remove-existing-querydetail-compatibility-methods) · [Engine interface gap](gaps.md#2-engine-interface-breadth)

**Status: Implemented and locally accepted — all Stage 8 gates passed.** The planning baseline below was prepared
against commit `6a14b1f` on 2026-09-20 by inspecting the current implementation,
callers, tests, and documentation. No application tests were run to prepare this
plan; earlier stages' passing results are historical evidence only.

## Objective and boundary

Complete the read-service separation established by
[Stage 1](stage-01-instance-queries.md) and
[Stage 3](stage-03-engine-responsibilities.md). Remove exactly these three
members from both `IWorkflowEngineService` and `WorkflowEngineService`:

- `ListInstancesAsync`.
- `SearchInstancesAsync`.
- `GetInstanceAsync`.

The planning baseline engine interface declared **40 methods**; this stage reduces that
count to **37**. The four waiting-task role-management operations remain on the
engine until Stage 9, whose separate target is 33 methods. Do not remove other
read operations merely because they return queries or detail DTOs.

This is the C# compatibility break explicitly accepted in the remaining-gaps
roadmap. It changes the public engine constructor and requires in-process callers
to use the existing focused interfaces. HTTP routes, DTOs, status codes,
authorization, workflow JSON, persisted data, and command behavior stay unchanged.
Add no replacement facade, obsolete forwarding overload, feature flag, database
migration, package, or new production service.

Stages 1 and 3 must be present and the baseline must pass. Stage 8 can proceed
alongside Stage 7; it does not require completion of Stage 5's residual manual
browser acceptance or observation of Stage 7's remote CI job. Coordinate edits
to shared planning documents and run isolated test hosts. Stage 9 should start
from this stage's accepted 37-method interface baseline.

## Current implementation and target ownership

| Source | Behavior at the planning baseline | Stage 8 change |
| --- | --- | --- |
| [Services.cs](../../Flowbit/src/Flowbit.Service/Abstractions/Services.cs) | `IWorkflowEngineService` declares all three compatibility methods among 40 members. | Delete only those declarations; retain inbox, task, command, and lifecycle members. |
| [WorkflowEngineService.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.cs) | List/search forward to the `instanceQueries` constructor dependency; detail forwards to `projections`. `instanceQueries` has no other uses across the engine partials. | Delete the three public forwards and the `IWorkflowInstanceQueryService instanceQueries` constructor parameter. Keep `projections`. |
| [WorkflowInstanceEndpoints.cs](../../Flowbit/src/Flowbit.Api/Endpoints/WorkflowInstanceEndpoints.cs) | List/search already inject the query service. `GetInstance` validates the actor, calls the engine, and maps null to 404. | Change only the detail handler's service type, invocation, and service XML description. Preserve its actor resolution and result mapping. |
| [InstanceQueryAbstractions.cs](../../Flowbit/src/Flowbit.Service/Abstractions/InstanceQueryAbstractions.cs) | Existing public query interface and narrow repository port own list/search. | Keep both interfaces and their method signatures. |
| [InstanceProjectionAbstractions.cs](../../Flowbit/src/Flowbit.Service/Abstractions/InstanceProjectionAbstractions.cs) | Existing public projection interface exposes detail, execution, and grouped/single progress reads. | Use `GetDetailAsync` directly for detail GET; retain all four interface methods. |
| [WorkflowInstanceQueryService.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowInstanceQueryService.cs) and [WorkflowInstanceProjectionService.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowInstanceProjectionService.cs) | Already own the extracted behavior. The projection implementation is internal. | No algorithm or visibility change is needed; API callers inject the public interface. |
| [Service DI](../../Flowbit/src/Flowbit.Service/DependencyInjection/ServiceCollectionExtensions.cs) and [Infrastructure DI](../../Flowbit/src/Flowbit.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs) | Register both read services as scoped and share one runtime repository across its ports per scope. | Retain registrations, lifetimes, and aliases. Removing the engine dependency must not remove the query service registration. |

### Caller migration

| Former C# call or construction | Replacement |
| --- | --- |
| `engine.ListInstancesAsync(...)` | Inject `IWorkflowInstanceQueryService`; call `ListInstancesAsync(...)` with the same arguments and token. |
| `engine.SearchInstancesAsync(actor, request, ct)` | Inject `IWorkflowInstanceQueryService`; call `SearchInstancesAsync(actor, request, ct)`. |
| `engine.GetInstanceAsync(id, ct)` | Inject `IWorkflowInstanceProjectionService`; call `GetDetailAsync(id, ct)`. |
| `new WorkflowEngineService(..., logger, instanceQueries, projections, ...)` | Remove the query-service argument and retain `projections` and every remaining argument in its correct position. |

These are contributor/in-process migration instructions, not a new public
integration surface. Existing HTTP clients, including Flowbit.Ui and the live
API tools, keep their routes and method names. Repository
`IWorkflowRuntimeRepository.GetInstanceAsync` and local HTTP-client helpers named
`GetInstanceAsync` are unrelated and must remain. Search matches need receiver
and interface inspection; do not perform a repository-wide name replacement.

The target detail handler retains this structure:

```csharp
public static async Task<IResult> GetInstance(
    long id,
    ClaimsPrincipal principal,
    IActorContextResolver actorResolver,
    IWorkflowInstanceProjectionService service,
    CancellationToken cancellationToken)
{
    _ = actorResolver.Resolve(principal);
    var instance = await service.GetDetailAsync(id, cancellationToken);
    return instance is null ? Results.NotFound() : Results.Ok(instance);
}
```

## Behavior and composition invariants

### HTTP and identity

- Preserve `/api/instances` GET, `/api/instances/search` POST, and
  `/api/instances/{id:long}` GET, including tags, operation IDs, parameter
  binding, request-size metadata, and response schemas.
- Preserve the instance route group's `.RequireAuthorization()`. Detail GET
  retains `Produces<InstanceDetailDto>(200)`, `Produces(401)`, and
  `Produces(404)`, with no body parameter introduced by the service change.
- Run `actorResolver.Resolve(principal)` before detail projection even though
  the returned actor is discarded. Configured actor-claim rejection and its
  existing error mapping still apply. Preserve the resolver's default identity
  behavior too; this stage adds no new identity requirements.
- Detail remains the existing deployment-level authenticated read. It does not
  apply list/search's workflow-version visibility predicate or personal-task
  inbox visibility. The projection interface takes no actor and performs no
  action authorization. See the [API authentication boundary](../api-guide.md#http-conventions-and-authentication).
- Forward the exact instance ID and cancellation token; keep null-to-404 and
  non-null-to-200 mapping and existing exception handling. Do not add fallback
  engine calls, retries, new errors, or route-specific exception translation.
- List/search retain the focused query service's dynamic
  `WorkflowInstances.RequiredRole` handling, actor roles, filters, cursors,
  ordering, counts, paging, variable enrichment, and error behavior.

### Projections and command responses

- Keep the engine's `IWorkflowInstanceProjectionService projections` dependency
  and every command call to `GetDetailAsync`, `BuildExecutionAsync`, and progress
  reads. Do not move calls across `SaveChangesAsync` or `CommitAsync`.
- Review the main engine partial plus the VersionChange, Reactivation, and
  AdministrativeActions partials when checking retained full-detail responses;
  also preserve slim start, message, and task-action response projections.
- Projection reads continue using the caller's scoped repositories/DbContext.
  They see flushed state in an existing transaction without starting,
  committing, suppressing, or disposing it. Do not create scopes or parallelize
  reads on a shared DbContext.
- Preserve full-history/current-only selection, merged-token exclusion,
  ordering, grouped work/progress counts, gateway and completion projections,
  shared-variable metadata, fault fields, and pruning timestamps.
- Preserve actor/delegation claims and audit links, null-versus-empty values,
  and definition cloning/redaction. Reads must neither mutate cached definitions
  nor write workflow, variable, task, history, or audit rows.

### Explicitly deferred work

Waiting-task role management belongs to Stage 9. Routing, gateway behavior,
message authentication, jobs, inbox capabilities, repository SQL, parser/editor
changes, UI extraction, and broad engine-interface redesign remain outside this
stage. The repository ports, projection implementation, and mapping helpers need
no restructuring to remove these compatibility methods.

## Implementation sequence

### 1. Refresh the baseline and caller inventory

1. Record the checkout commit and working-tree status. Reconfirm the three
   forwarding bodies, the 40-method interface count, and all uses of
   `instanceQueries` across engine partials.
2. Run the Docker-backed solution suite before editing code. Record actual
   passed/failed/skipped counts and distinguish existing failures from changes
   introduced by this stage.
3. Search `Flowbit/src`, `Flowbit/tests`, and `Flowbit/tools` for the three
   method names, engine implementations/proxies, and direct constructor calls.
   Classify engine consumers separately from repository and HTTP helpers.
4. Inspect custom endpoint-test hosts as well as production DI.
   `AdvancedVariableSearchEndpointTests.CreateKestrelHarnessAsync` maps all
   instance routes but currently registers only engine/query/actor services.
   Add a projection substitute there: an unregistered interface parameter can
   be inferred as a GET body and break startup even when a test calls only
   search. Repeat this check for any new custom hosts.

### 2. Characterize the detail endpoint boundary

Reuse existing fixtures and coverage. Add a small
`WorkflowInstanceDetailEndpointTests.cs` under `Flowbit/tests/Flowbit.Tests/`
only for the missing boundary assertions listed below. Prefer an authenticated
API test host for authorization/status coverage and a focused handler test for
exact ID/token forwarding. Do not use a direct handler call as proof that bearer
middleware rejects an anonymous request.

Before the switch, any new characterization can provide an engine substitute;
adapt it to a projection substitute in the removal commit. Configure identity
through the existing isolated test-host mechanisms. Use a recording projection
to prove invalid identity is rejected before reads, and make unexpected engine
resolution/use fail in the focused host after the switch.

Check the current development OpenAPI operation and endpoint metadata before
and after the switch. Existing generic OpenAPI tests do not assert the complete
detail response/status contract. Add focused assertions for the changed seam,
not a full-document snapshot of unrelated operations.

### 3. Switch the detail handler and remove the compatibility surface

1. Change `WorkflowInstanceEndpoints.GetInstance` to inject
   `IWorkflowInstanceProjectionService` and call `GetDetailAsync`.
2. Preserve actor resolution, argument order, result mapping, route registration,
   and response metadata. Update the handler's service parameter documentation.
3. Delete all three declarations from `IWorkflowEngineService` and their exact
   forwarding implementations from `WorkflowEngineService`.
4. Delete only the `instanceQueries` constructor parameter. Keep all optional
   runtime dependencies, `projections`, service registrations, and repository
   aliases. Do not add a compatibility constructor or service locator.
5. Build with all caller/test edits below included so the removal is one
   independently buildable change.

### 4. Migrate tests and direct constructions

| Current file | Required action |
| --- | --- |
| `WorkflowEngineInstanceQueryForwardingTests.cs` (removed in Stage 8) | Remove the entire obsolete class/file: its three tests assert forwards being deliberately removed. Its private engine-construction helper disappears with it. The filename is retained here as historical context. |
| [InstanceProjectionTests.cs](../../Flowbit/tests/Flowbit.Tests/InstanceProjectionTests.cs) | In the three full-detail tests, resolve `IWorkflowInstanceProjectionService` in the existing scope and replace engine detail calls with `GetDetailAsync`. Preserve warmup/reset placement, seeds, count assertions, and no-write checks. Keep the current-only and transaction/rollback tests. |
| [ServiceTaskSharedStatusTests.cs](../../Flowbit/tests/Flowbit.Tests/ServiceTaskSharedStatusTests.cs) | Remove the query-service argument from the retained direct engine construction. Preserve the existing runtime/definition proxies, projection service, and shared-variable setup. Remove query-only setup only if it becomes unused. |
| [WorkflowInstanceVersionChangeServiceTests.cs](../../Flowbit/tests/Flowbit.Tests/WorkflowInstanceVersionChangeServiceTests.cs) | Remove the query-service constructor argument; retain the projection using the same proxied repositories so failure injection and rollback coverage still exercise the intended paths. |
| [WorkflowInstanceQueryServiceTests.cs](../../Flowbit/tests/Flowbit.Tests/WorkflowInstanceQueryServiceTests.cs) | Retain query behavior and production DI composition coverage; confirm engine resolution succeeds with the new constructor and both focused services remain scoped and resolvable. |
| [AdvancedVariableSearchEndpointTests.cs](../../Flowbit/tests/Flowbit.Tests/AdvancedVariableSearchEndpointTests.cs) | Register a projection substitute in `CreateKestrelHarnessAsync` before mapping endpoints. Retain query-service substitutes, list/search dispatch, body limits, authentication, selectors, and GET/POST parity, including the database-backed `AdvancedVariableSearchGetPostParityApiTests` class in this file. The `ContractApiFactory` uses production registrations, which already include projections. |

`OpenApiContractTests.RegisterHandlerServiceParameters` already registers all
interfaces in `Flowbit.Service.Abstractions`, including the projection port.
It needs no new DI registration for this switch; add only the missing contract
assertions.

At the inspected commit there are two retained direct engine construction sites,
plus the helper in the forwarding-test file being removed. Repeat the search
after edits; this inventory is a baseline, not permission to ignore new callers.

### 5. Validate the completed change and update documentation

Run the focused suites, the full solution gate, and the standalone browser smoke
regression described below. Inspect the complete diff for unintended query,
projection, authorization, transaction, and DTO edits. Update ownership and
compatibility documentation in the same implementation change, validate links,
and record evidence before changing Stage 8's status.

## Test and acceptance matrix

| Risk or contract | Coverage and required result |
| --- | --- |
| Detail endpoint dispatch | Focused detail endpoint tests: authenticated valid identity and a non-null detail give 200 with the same DTO; a null result gives 404. ID and cancellation token reach `GetDetailAsync`. Detail GET no longer depends on the engine. |
| Authentication and actor validation | Anonymous request gives 401 before projection. With a configured canonical actor claim, missing, blank, or conflicting values give the existing 401 before projection. Retain `ActorContextResolverTests` and `ActorContextApiTests`, both in [ActorContextResolverTests.cs](../../Flowbit/tests/Flowbit.Tests/ActorContextResolverTests.cs); their existing coverage alone does not prove the detail handler kept its resolver call. |
| Existing detail read scope | A valid authenticated actor without list-visibility/admin/task roles can still read an existing instance under the current deployment-level detail contract. Use a real seeded instance or equivalent existing API coverage; do not accidentally introduce the list/inbox predicate. |
| HTTP metadata and schemas | Retain [OpenApiContractTests](../../Flowbit/tests/Flowbit.Tests/OpenApiContractTests.cs) and add a targeted detail assertion: same GET path, operation ID, bearer requirement, 200 `InstanceDetailDto`, 401 and 404, no request body or DI type exposed as a parameter/schema. Preserve existing list/search metadata. |
| List/search behavior and SQL authorization | Retain query-service tests, advanced-search endpoint and PostgreSQL parity tests, [RepositoryProjectionTests](../../Flowbit/tests/Flowbit.Tests/RepositoryProjectionTests.cs), and [AdvancedVariableFilterAuthorizationPostgresTests](../../Flowbit/tests/Flowbit.Tests/AdvancedVariableFilterAuthorizationPostgresTests.cs). Fresh settings, role normalization, SQL membership/count/order/page, and page-bounded enrichment stay unchanged. |
| Projection cost and read-only behavior | Migrate the existing `InstanceProjectionTests` without weakening their recorded fixture baselines: 17 warm-detail readers, 18 cold-detail readers, and 7 current-only readers. Keep independence from child/audit/definition volume and no-write assertions. These are fixture-specific expectations, not universal request counts. |
| Scope, transaction, and immutable definitions | Retain the projection test proving flushed uncommitted state is visible and rollback leaves no committed rows, production repository identity checks, and [RuntimeProjectionMapperTests](../../Flowbit/tests/Flowbit.Tests/RuntimeProjectionMapperTests.cs) for secret redaction without source mutation. Resolve through existing scopes; do not replace these checks with mocks alone. |
| Command consumers | Retain [InstanceDtoContractTests](../../Flowbit/tests/Flowbit.Tests/InstanceDtoContractTests.cs), the two direct-construction suites, and existing message, version-change, reactivation, variable-update, administrative-action, gateway, and MI API regressions through the full suite. Full and slim command responses must keep existing data and commit behavior. |

Deleting the forwarding class removes three test cases intentionally. Record
the actual final count and the reason for that reduction; do not retain obsolete
tests or invent new tests merely to maintain the previous total. Add no generic
architecture framework or brittle source-text test solely to enforce the count.

## Validation commands and evidence

Run from the repository root. These single-line commands use the same arguments
in PowerShell and Bash. The .NET 10 SDK and reachable Docker are prerequisites
for the normal PostgreSQL-backed suite.

Baseline and final full-suite gate:

```text
git status --short
git rev-parse --short HEAD
docker info
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet
git diff --check
```

Focused checks after caller migration (the detail endpoint class is the proposed
new class above; adjust the filter if coverage is added to an existing class):

```text
dotnet test Flowbit/tests/Flowbit.Tests/Flowbit.Tests.csproj --nologo --verbosity quiet --filter "FullyQualifiedName~WorkflowInstanceDetailEndpointTests|FullyQualifiedName~InstanceProjectionTests|FullyQualifiedName~WorkflowInstanceQueryServiceTests|FullyQualifiedName~AdvancedVariableSearchEndpointTests|FullyQualifiedName~AdvancedVariableSearchGetPostParityApiTests|FullyQualifiedName~RepositoryProjectionTests|FullyQualifiedName~AdvancedVariableFilterAuthorizationPostgresTests|FullyQualifiedName~ActorContextResolverTests|FullyQualifiedName~ActorContextApiTests|FullyQualifiedName~OpenApiContractTests|FullyQualifiedName~InstanceDtoContractTests|FullyQualifiedName~ServiceTaskSharedStatusTests|FullyQualifiedName~WorkflowInstanceVersionChangeServiceTests"
```

Supplement the compiler with a semantic review of these search results:

```text
rg -n 'ListInstancesAsync|SearchInstancesAsync|GetInstanceAsync' Flowbit/src Flowbit/tests Flowbit/tools
rg -n 'instanceQueries|IWorkflowInstanceQueryService' Flowbit/src/Flowbit.Service/Services -g 'WorkflowEngineService*.cs'
rg -n 'new WorkflowEngineService|IWorkflowEngineService' Flowbit/src Flowbit/tests Flowbit/tools
```

The second search should have no engine matches after removal; `rg` exit code 1
means no matches. The first still has legitimate query-service, repository, and
HTTP-client uses. Count only methods declared in `IWorkflowEngineService` to
confirm 37; counting the entire `Services.cs` file would include other interfaces.

### Browser regression and UI boundary

No HTML, CSS, rendered layout, or interaction code is scheduled to change, so
this stage does not introduce a new manual visual acceptance checklist. Run
the existing standalone Chromium suite as an integration regression for the
rewired detail route. It remains outside both solution files and needs its own
build, browser installation, and freshly published API/UI hosts:

```text
dotnet publish Flowbit/src/Flowbit.Api/Flowbit.Api.csproj -c Release -o artifacts/browser/hosts/api /p:UseAppHost=false
dotnet publish Flowbit/src/Flowbit.Ui/Flowbit.Ui.csproj -c Release -o artifacts/browser/hosts/ui /p:UseAppHost=false
dotnet build Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release
pwsh Flowbit/tests/Flowbit.BrowserTests/bin/Release/net10.0/playwright.ps1 install chromium
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore --logger "trx;LogFileName=stage-08-browser-smoke.trx" --results-directory artifacts/browser/test-results
```

On Linux replace the installation command with
`pwsh Flowbit/tests/Flowbit.BrowserTests/bin/Release/net10.0/playwright.ps1 install --with-deps chromium`.
See the [browser-test README](../../Flowbit/tests/Flowbit.BrowserTests/README.md)
for isolated-stack ownership and diagnostics. The no-Worker suite exercises
E1–E5/R1–R6 and does not close Stage 5's separate manual gate.

Record actual commands, counts, and failures/skips separately for solution and
browser suites. For the browser run, retain the manifest's Chromium version and
localhost URLs, scenario diagnostics/viewports, console results, and relevant
artifacts; do not describe automated results as manual verification. If scope
expands to UI changes, revise this plan and apply the
[mandatory real-browser verification](../../AGENTS.md#mandatory-real-browser-ui-verification),
including screenshots when appearance changes. Report unavailable checks and
leave the corresponding acceptance item open.

## Documentation changes during implementation

| Document | Required update or review |
| --- | --- |
| [Runtime reference](../../Flowbit/README.md#instance-query-ownership) | Remove current-tense claims that list/search and `GetInstanceAsync` remain engine forwards. Explain direct API injection and the retained engine dependency for command projections. Add the C# migration mapping and constructor break to contributor-facing ownership text. |
| [AGENTS.md](../../AGENTS.md#runtime-engine-flowbit) | Update the Service-project ownership description to the implemented read-service boundaries; preserve command, transaction, and authorization invariants. |
| [Stage 1](stage-01-instance-queries.md) and [Stage 3](stage-03-engine-responsibilities.md) | Preserve historical implementation records and passing results. Add an explicit supersession note linking to this stage once removal is implemented; their compatibility decisions applied at those stages' completion. Repair historical links to any test file removed here. |
| [Gap inventory](gaps.md#2-engine-interface-breadth) | Record the measured 37-method interface and removal of the three forwards, with the implementation commit. Keep the other engine responsibilities and Stage 9's four methods open; preserve explicitly dated measurements. |
| [Plan index](README.md), [remaining-gaps roadmap](remaining-gaps-implementation-plan.md), [documentation home](../index.md), and this plan | Update Stage 8 status and links together after acceptance, retain other stages' independent statuses, and record actual validation evidence. |
| [API reference](../api-guide.md) and [developer guide](../developer-guide.md) | Compare detail/list/search contracts with final handlers, DTOs, authentication/error handling, tests, and development OpenAPI. Their public behavior is expected to remain accurate; edit only if an existing ownership explanation becomes inaccurate. Keep public integration examples HTTP-only. |

Validate every changed relative link and anchor, including references to the
deleted forwarding-test file. No workflow JSON, example, screenshot, deployment
configuration, or public walkthrough change is expected. Do not update unrelated
content just to include it in the diff.

## Delivery, rollback, and completion checklist

If missing characterization is needed, land it as a passing preparatory commit.
Then deliver one buildable removal commit containing the endpoint switch,
interface/constructor cleanup, caller/test migrations, and documentation updates.
There must be no intermediate delivered commit with dangling engine calls.

Rollback is to revert the removal commit and rebuild/redeploy the affected
application artifacts together. Keep characterization tests only where they
remain compatible. There is no data conversion. Revert dependent Stage 9 work
first if it has already landed; in-process consumers and their compiled
assemblies must be coordinated with whichever constructor/interface is deployed.

- [x] Baseline commit, full-suite result, and caller inventory recorded.
- [x] Exactly three methods removed from both engine interface and implementation;
  interface count is 37 and all other members remain.
- [x] Detail GET injects projections, validates the actor first, and preserves
  existing HTTP metadata, response mapping, and deployment-level read scope.
- [x] List/search endpoints still inject the query service; its DI registration
  and shared repository identity remain intact.
- [x] Engine query-service dependency removed; command projection dependency,
  call positions, lifetimes, and transaction semantics preserved.
- [x] Forwarding tests removed; projection tests and all direct constructions
  migrated without weakening cost, redaction, scope, or rollback assertions.
- [x] Focused tests and final full suite pass with explained count changes and
  no unexplained new skips; standalone browser regression recorded separately.
- [x] Changed endpoint metadata/development OpenAPI checked; documentation and
  historical supersession notes updated; links and `git diff --check` pass.
- [x] Implementation revision (commit or working-tree base), actual evidence, remaining limitations, and
  explicitly deferred Stage 9–11 work recorded here and in the plan index.

The implementation and acceptance evidence is recorded below. The design and
baseline inventory above are retained to explain the scope and migration.

## Implementation record — 2026-09-20

Implemented in the working tree based on `6a14b1f013eb47c03c445b297c61042d5aa3c6f2`.
No implementation commit has been created. At entry, the documentation home,
plan index, gap inventory, and remaining-gaps roadmap had uncommitted planning
edits, and this Stage 8 plan was untracked; those changes were preserved and
extended with this record.

### Final implementation and review

- Removed exactly `ListInstancesAsync`, `SearchInstancesAsync`, and
  `GetInstanceAsync` from both the engine interface and implementation.
  Measured interface count: **40 → 37**; every other member remains.
- Removed only the engine's `instanceQueries` constructor parameter. The two
  retained direct constructor sites were migrated; the forwarding-test helper
  disappeared with its obsolete class. No engine partial references the query
  service now. Repository and HTTP-client methods with similar names remain.
- Detail GET injects `IWorkflowInstanceProjectionService`, resolves the actor
  first, forwards the ID/token to `GetDetailAsync`, and preserves null/404 and
  DTO/200 mapping. List/search still inject the query service. Route metadata,
  DTOs, DI lifetimes/aliases, and exception middleware are unchanged.
- Retained all command projection calls in the main, VersionChange,
  Reactivation, and AdministrativeActions partials at their existing
  save/commit positions. Query/projection algorithms and persistence are unchanged.
- Migrated the three full-detail projection tests without changing warmup,
  counter-reset, no-write, rollback, or volume-independence assertions. Warm,
  cold, and current-only reader baselines remain **17 / 18 / 7**. Composition
  coverage now also checks that projection services differ across scopes.
- Added nine focused detail boundary cases covering real bearer rejection,
  missing/blank/conflicting canonical identity before any projection read,
  default/configured valid identity, DTO/404 mapping, no engine resolution,
  and exact handler ID/token forwarding. Added one database-backed test proving
  an actor excluded from instance listing can still read detail, and one
  production OpenAPI contract test for the same path, operation ID, bearer
  requirement, 200 detail schema, 401/404, path-only input, and no request body
  or injected service schemas. It generates the document through Program's
  actual registered transformers; Testing does not expose the development URL.
- Registered the missing projection service in the custom Kestrel search host.
  The new production-factory tests use isolated JWT validation settings and a
  nonparallel collection because Program owns a process-wide Serilog logger.
  Initial focused runs exposed test-fixture JWT/admin defaults and that logger
  collision; these test setup issues were corrected before the passing run.

### Validation evidence

Docker Engine **29.6.2** and .NET SDK **10.0.301** were available. All test
runs used their own disposable PostgreSQL hosts.

| Gate | Command / evidence | Result |
| --- | --- | --- |
| Baseline | `dotnet test Flowbit/Flowbit.sln --logger "trx;LogFileName=stage-08-baseline.trx" --results-directory artifacts/stage-08/baseline` | **1,892 passed, 0 failed, 0 skipped** |
| Focused | The focused filter above, with `--logger "trx;LogFileName=stage-08-focused.trx" --results-directory artifacts/stage-08/focused` | **102 passed, 0 failed, 0 skipped** |
| Final solution | `dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet --logger "trx;LogFileName=stage-08-final.trx" --results-directory artifacts/stage-08/final` | **1,900 passed, 0 failed, 0 skipped** |
| Documentation checks | Relative links/anchors/images in all nine changed Markdown files; `git diff --check` | **209 references valid; no whitespace errors** |
| Browser preparation | Both Release publishes, standalone Release build, and Chromium installation using the commands above | Passed |
| Standalone browser | Exact browser test command above; `artifacts/browser/test-results/stage-08-browser-smoke.trx` | **20 passed, 0 failed, 0 skipped** |

The final solution count is 1,900: three obsolete forwarding cases
removed and eleven boundary/read-scope/OpenAPI cases added. Existing NU1903
(SSH.NET 2025.1.0) and xUnit collection-size analyzer warnings were observed in
the baseline/build logs; this stage makes no dependency changes.

Browser evidence is under
`artifacts/browser/runs/20260920-164946-5e57f05a/`, including the manifest,
per-scenario diagnostics, screenshots, and host logs. The run used headless
**Chromium 151.0.7922.34** with API **http://127.0.0.1:60824**, UI
**http://127.0.0.1:60826**, and editor **http://127.0.0.1:60828**. Those
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
Stage 1/3 supersession notes, gap inventory, roadmap, plan index, documentation
home, and this record. The API reference and developer guide were compared with
the final handlers, DTOs, auth/error handling, tests, and generated OpenAPI;
their HTTP descriptions remain accurate and required no edits. No workflow JSON,
examples, images, deployment configuration, or public walkthrough changed.

No UI code or visual appearance changed. The automated browser regression does
not close Stage 5's separate manual acceptance or Stage 7's remote CI observation.
Stage 9's four role-management methods and Stages 10–11 remain deferred. Changes
are left uncommitted for review.
