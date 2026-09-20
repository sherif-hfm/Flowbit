# Stage 1 — Extract instance list and search

[Plan index](README.md) · [Next: repository helpers](stage-02-repository-query-helpers.md)

**Status: Implemented.** Instance list/search orchestration now lives in
`WorkflowInstanceQueryService` behind `IWorkflowInstanceQueryService` with the
narrow `IWorkflowInstanceQueryRepository` port. At this stage's completion the
two engine members remained as compatibility forwards. See
[Implementation record](#implementation-record).

**Superseded compatibility decision:** [Stage 8](stage-08-remove-query-detail-compatibility.md)
removes those forwards and the engine's query-service constructor dependency.
The historical implementation and validation results below describe Stage 1.

## Objective and scope

Move instance list/search orchestration out of `WorkflowEngineService` into a
focused query service. Preserve existing callers through forwarding methods.
This stage changes ownership of read behavior without changing SQL or public
HTTP contracts.

Implementation anchors at planning time (the extracted members moved to the
new query service; the engine file now retains only the forwards):

- [WorkflowEngineService.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.cs):
  `ListInstancesAsync`, `SearchInstancesAsync`, `ListInstancesCoreAsync`,
  `ResolveInstanceListAuthorizationAsync`, instance sort parsing, and summary mapping.
- [Services.cs](../../Flowbit/src/Flowbit.Service/Abstractions/Services.cs) and
  [Repositories.cs](../../Flowbit/src/Flowbit.Service/Abstractions/Repositories.cs):
  existing engine and repository contracts.
- [WorkflowInstanceEndpoints.cs](../../Flowbit/src/Flowbit.Api/Endpoints/WorkflowInstanceEndpoints.cs):
  the GET instance-list and POST instance-search handlers.
- [NodeExecutionQueryService.cs](../../Flowbit/src/Flowbit.Service/Services/NodeExecutionQueryService.cs):
  an existing example of a focused query service.

Inbox, management/distribution queries, instance detail, routing, commands,
role policies, and repository SQL restructuring are outside this stage.

## Target ownership

| Proposed type | Responsibility |
| --- | --- |
| `IWorkflowInstanceQueryService` | Existing `ListInstancesAsync` and `SearchInstancesAsync` signatures, including actor and cancellation token. |
| `WorkflowInstanceQueryService` | Input parsing, dynamic instance-list authorization, repository call, page enrichment, and summary DTO construction. |
| `IWorkflowInstanceQueryRepository` | The existing repository `ListInstancesAsync` signature only. |
| `WorkflowQueryInputParser` (internal) | Existing legacy variable-filter parsing, search-sort conversion, and common generic sort-clause parsing used by both instance and inbox paths. |
| `RuntimeProjectionMapper` (internal) | Existing pure fault and user-task work-summary mapping shared with engine responses. |

Place new query interfaces in a focused `InstanceQueryAbstractions.cs` file in
Service/Abstractions. The existing `IWorkflowRuntimeRepository` inherits the new
query port and removes its duplicate method declaration. Keep
`WorkflowRuntimeRepository` as the implementation of both interfaces, with its
query body unchanged.

The query service depends on the narrow query repository, workflow definitions,
workflow jobs, engine settings, and the workflow-variable store. Preserve the
current optional store behavior used by reduced test compositions. It must not
depend on `IWorkflowEngineService`, create an engine, or open its own DI scope.

## Implementation sequence

1. Run the focused tests below and inspect current callers of the two methods.
   Record current results, including any query-count assertions. Add a regression
   only where a listed acceptance scenario is not already covered.
2. Introduce the query contracts. Extract `ParseVariableFilters`, `ToLegacySort`,
   and generic `ParseSort` into the internal input parser unchanged. Update both
   instance and existing inbox/management/distribution callers as applicable.
   Keep instance-specific field selection in the new query service and
   inbox-specific field selection with its current owner.
3. Move `ToFault` and `ToUserTaskWorkSummary` into `RuntimeProjectionMapper`,
   updating their existing engine callers. Keep the instance-list `ToSummary`
   mapping private to the new query service. Do not copy shared helpers or
   expand this into detail projection yet.
4. Move the two entry methods, core query, authorization resolver, and their
   instance-role setting constants into `WorkflowInstanceQueryService`.
   Preserve validation order, exceptions, repository arguments, cancellation,
   and enrichment order. Copy behavior before simplifying it.
5. Register the query service as scoped in
   [service DI](../../Flowbit/src/Flowbit.Service/DependencyInjection/ServiceCollectionExtensions.cs).
   In [infrastructure DI](../../Flowbit/src/Flowbit.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs),
   register `WorkflowRuntimeRepository` once as scoped and resolve both repository
   interfaces through that same scoped concrete instance. Preserve its DbContext
   and per-scope bookkeeping; do not instantiate two repository objects.
6. Inject the query service into `WorkflowEngineService`. Retain the two old
   interface members as simple forwards with their original signatures. Existing
   engine aliases in DI must still resolve the same scoped engine object. Update
   direct engine constructions in tests; avoid fallback construction or a service
   locator to make old constructors compile.
7. Change only the GET instance-list and POST instance-search handlers to inject
   the new query interface. Preserve actor resolution, paging normalization,
   endpoint authorization, metadata, and response handling. Adapt the instance
   routes in `AdvancedVariableSearchEndpointTests` to stub the new interface;
   other endpoint stubs continue to target their existing services.
8. Run focused and full tests, review the source/contract diff, and update the
   affected architecture documentation in the same change.

## Behaviors to preserve

- Read `WorkflowInstances.RequiredRole` on each query. Keep the `admin` fallback,
  comma-separated setting normalization, and case-insensitive caller-role handling.
  Do not introduce settings caching. SQL applies visibility before count and page.
- Preserve legacy `name:value` parsing, advanced filter AST validation, sort
  validation, defaults, duplicate detection, and the three-clause limit.
- Normalize and validate cursors as today, including rejecting a missing cursor
  for pages after the first. Keep cursor/sort compatibility, deterministic ID
  tie-breaks, total-count semantics, and returned `NextCursor` unchanged.
- Keep all membership, filtering, ordering, and paging in PostgreSQL. Existing
  node filtering and current-node display projections retain their distinct
  semantics; do not replace them with in-memory filtering.
- Load job summaries for the selected page as a batch. With `includeVariables`,
  load distinct workflow definitions as a batch and describe shared bindings
  once per distinct workflow. Preserve that bound rather than add per-instance
  lookups. Do not claim the number of binding reads is constant across all
  numbers of distinct workflows.
- Preserve variables, shared binding metadata, work summaries, fault information,
  execution positions, completion, null handling, and result ordering. Mapping
  must not mutate records or cached definitions.

## Tests and acceptance

From the repository root, in PowerShell or Bash:

```text
docker info
dotnet test Flowbit/Flowbit.slnx --filter "FullyQualifiedName~WorkflowInstanceCursorTests|FullyQualifiedName~InstanceSortingApiTests|FullyQualifiedName~InstanceListVariablesApiTests|FullyQualifiedName~RepositoryProjectionTests|FullyQualifiedName~AdvancedVariableSearchEndpointTests|FullyQualifiedName~AdvancedVariableFilterPostgresTests|FullyQualifiedName~AdvancedVariableFilterAuthorizationPostgresTests|FullyQualifiedName~WorkflowApiClientAdvancedSearchTests|FullyQualifiedName~OpenApiContractTests|FullyQualifiedName~WorkflowEngineInstanceQueryForwardingTests|FullyQualifiedName~WorkflowInstanceQueryServiceTests"
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet
git diff --check
```

Acceptance scenarios:

- Authorized and unauthorized workflow versions produce the same membership and
  counts. Changing the required-role setting is reflected in the next query.
- First and later cursor pages, tied sort values, all supported sort directions,
  invalid cursors, invalid filters, and invalid sort clauses retain their results
  and errors. Legacy and advanced query paths retain equivalent supported cases.
- Empty pages and populated pages preserve DTO shape, job summaries, variables,
  shared metadata, and query bounds. Retain existing repository projection and
  parallel-token filter tests in the full suite.
- Existing engine callers still succeed through forwarding methods. The HTTP
  handlers resolve the focused service and their OpenAPI contract is unchanged.
- Normal application composition resolves the query service without a cycle and
  shares repository scope with the engine. The full suite covers shared parsing
  and mapper effects on inbox and command responses.

All focused and full-suite checks must pass without unexplained new skips. Record
the actual results. Browser checks are not required because no UI changes are
part of this stage.

## Documentation, delivery, and rollback

Update query ownership and DI descriptions in
[Flowbit/README.md](../../Flowbit/README.md) and affected architecture text in
[AGENTS.md](../../AGENTS.md). Compare the
[API guide](../api-guide.md) and [developer guide](../developer-guide.md) with the
final behavior; preserve their HTTP integration contracts. Validate changed links.

Deliver a buildable extraction with its caller/test/DI updates together. Any
necessary characterization tests may be a preceding commit. Mark the stage
implemented only after acceptance. Revert the extraction to roll back; no data
or deployment configuration changes are required. Retained forwarding members
can be considered for removal in a separate compatibility review.

## Implementation record

Delivered as one buildable change:

- `InstanceQueryAbstractions.cs` (Service/Abstractions) declares
  `IWorkflowInstanceQueryService` and `IWorkflowInstanceQueryRepository`.
  `IWorkflowRuntimeRepository` inherits the query port and no longer
  re-declares `ListInstancesAsync`.
- `WorkflowInstanceQueryService` owns the two entry methods, the core query,
  the dynamic instance-list authorization resolver, instance sort parsing, and
  the private instance-list `ToSummary` mapping, with the
  `WorkflowInstances.RequiredRole` constants.
- `WorkflowQueryInputParser` (internal) owns the legacy `name:value` variable
  filter parsing, structured-sort conversion, and the generic sort-clause
  grammar; the engine's inbox, management, and distribution paths call the same
  parser. `RuntimeProjectionMapper` (internal) owns the pure `ToFault` and
  `ToUserTaskWorkSummary` mappings shared by engine responses and the query
  service.
- `WorkflowEngineService` takes `IWorkflowInstanceQueryService` as a required
  constructor parameter and retains the two interface members as simple
  forwards. The GET instance-list and POST instance-search handlers inject the
  query service; all other endpoints keep their existing services.
- The service DI registers the query service as scoped. Infrastructure DI
  registers one scoped `WorkflowRuntimeRepository` and resolves both repository
  interfaces through that same instance.
- `AdvancedVariableSearchEndpointTests` stubs the new interface for the
  instance routes; a new `WorkflowEngineInstanceQueryForwardingTests`
  characterizes the forwards.
- `WorkflowInstanceQueryServiceTests` covers setting freshness and role
  normalization for both list and search, repository identity within and
  across production DI scopes, and page-bounded job/definition enrichment.
  Empty, single-item, and 40-item pages exercise binding reads once per distinct
  workflow, including multiple workflows, omitted variables, and the optional
  variable-store composition. Assertions retain result order, paging metadata,
  job/shared metadata, cancellation tokens, and immutable source records.

Validation (2026-09-17): the focused set passed 73/73 (56 baseline-matching
tests, two forwarding characterizations, and 15 query-service acceptance
cases), and the full suite passed 1,852 passed / 0 failed / 0 skipped
(baseline 1,835 plus 17 new cases) with Docker PostgreSQL tests. The focused
command above includes both new test classes. `git diff --check` is clean,
and all 30 relative links and anchors in this stage and the plan index resolve.
No HTTP route, DTO, JSON, error,
authorization, ordering, paging, or cursor contract changed; no UI changes, so
no browser verification was required. Documentation updates:
[Flowbit/README.md](../../Flowbit/README.md) query ownership note,
[AGENTS.md](../../AGENTS.md) service-layer bullet, this record, and the
[plan index](README.md) status. The API and developer guides needed no change
because their HTTP integration contracts are unaffected.
