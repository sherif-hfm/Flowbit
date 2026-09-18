# Stage 2 — Repository query helpers

**Status: Implemented.** The shared task-ownership helper and the
inbox-visibility CTE constant now live in
`WorkflowRuntimeRepository.QuerySql.cs`; each caller keeps its own candidate
projection, authorization, ranking, count/page execution, and transactions.
See [Implementation record](#implementation-record).

[Plan index](README.md) · [Next: engine projections](stage-03-engine-responsibilities.md)

## Purpose and scope

Reduce verified duplication in the repository's read queries while preserving
their database semantics. Stage 1 is the recommended predecessor because it
establishes clearer read-service boundaries. This stage can proceed independently
when its repository and test edits do not conflict with Stage 1.

The targets are the repeated ownership predicates in
`ListManageableUserTasksAsync` and `ListDistributableUserTasksAsync`, and the
identical `evaluation_targets` / `visibility_results` CTEs in `ListInboxAsync`
and `ListUserTasksPageAsync`. Their current implementation is in
[WorkflowRuntimeRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/WorkflowRuntimeRepository.cs).

The existing
[VariableFilterSqlCompiler](../../Flowbit/src/Flowbit.Infrastructure/Repositories/VariableFilterSqlCompiler.cs)
already shares parameterized variable-expression compilation across repositories.
Retain that focused responsibility. A generic query framework, repository-wide
interface split, new query specification type, SQL optimization, and changes to
runtime mutation methods are outside this stage.

The [gap review](gaps.md#4-repository-filtersortpaging-duplication) confirms that
basic runtime filters, variable-expression compilation, and instance/inbox sort
validation already share implementations. Remaining management/distribution page
assembly and candidate materialization are future candidates; do not expand this
stage to combine queries with different scope or paging semantics.

## Implementation sequence and interfaces

1. Record the current results of the focused test command below. Inspect the
   current query bodies before extraction, since surrounding work may have moved
   them. Compare the two visibility CTE bodies and extract only their identical
   portion; authorization and candidate projection remain owned by each query.
2. Add `WorkflowRuntimeRepository.QuerySql.cs` beside the main repository file,
   using the same partial class. Introduce the private static helper
   `AppendTaskOwnershipFilter(StringBuilder where,
   List<(string Name, object Value)> args, string? owner, string? ownership)`.
   Move the existing owner predicate and ownership switch into it unchanged and
   call it from the two management/distribution methods. Preserve the bound
   `owner` parameter name, trimming, case-insensitive comparison, assignee-first
   `COALESCE`, and assigned/claimed/unassigned definitions. Keep normalization and
   validation in their current service entry points.
3. In the same partial file, add the private constant
   `InboxVisibilityEvaluationCtes`. It contains the complete `evaluation_targets`
   and `visibility_results` definitions, including their separating comma but
   no leading or trailing comma. Interpolate this fixed SQL text between each
   caller's `base_candidates` and subsequent CTE. Both callers keep their
   existing aliases and bound parameters. Preserve effective shared-variable
   resolution, acting-for grouping, fixed context merge, and `IS TRUE` behavior.
4. Leave `base_candidates`, `visible_candidates`/`eligible`, representative
   ranking, count queries, page projections, ordering, and transaction ownership
   in their current methods. Keep the existing instance/node/workflow filter
   helpers and `BuildParameters`; this stage does not combine count/page
   execution or introduce another parameter abstraction.
5. Add the focused ownership regression described below, run the focused and
   full suites, and update the runtime documentation after the code passes.

These additions are private implementation details. HTTP routes, request/response
DTOs, repository interfaces, DI registration, database schemas, and migrations
require no changes.

## Behaviors that must remain intact

- Management uses workflow assignment/role-management authority and its existing
  active/pending/open task rules. Distribution uses its previously authenticated
  workflow key and active-task scope. Both continue to bypass personal inbox
  visibility. Do not share their authorization predicates.
- Inbox visibility runs before one-per-actor representative selection, exact
  count, sorting, and paging. Per-instance task pages retain their own personal
  authorization and return individual tasks without inbox representative
  selection. Delegation eligibility stays in each caller's candidate query.
- The visibility CTE groups by instance, condition, and acting-for identity;
  resolves shared aliases over instance values; and hides missing, null, invalid,
  or failed expressions according to the existing database evaluator.
- Inbox and per-instance task queries retain their existing repeatable-read
  transaction ownership and empty-page handling. Page enrichment and policy
  loading remain bounded to the selected page; no per-task queries are added.
- Instance lists retain cursor pagination and count-before-cursor semantics.
  Task pages retain offset pagination and their existing stable sort tie-breaks.
  All caller values remain bound parameters, and SQL identifiers remain fixed
  implementation text.

## Tests and acceptance

Run commands from the repository root with the supported .NET SDK and a reachable
Docker daemon, as documented in the [runtime test instructions](../../Flowbit/README.md).
The following commands are valid in PowerShell and Bash:

```text
docker info
dotnet test Flowbit/Flowbit.slnx --filter "FullyQualifiedName~AdvancedVariableFilterAuthorizationPostgresTests|FullyQualifiedName~InboxVisibilityPostgresApiTests|FullyQualifiedName~TaskAssignmentApiTests|FullyQualifiedName~TaskDistributionApiTests|FullyQualifiedName~TaskRoleManagementApiTests|FullyQualifiedName~UserTaskApiTests|FullyQualifiedName~RolePolicyInboxPerformanceTests|FullyQualifiedName~InstanceSortingApiTests|FullyQualifiedName~WorkflowInstanceCursorTests"
dotnet test Flowbit/Flowbit.slnx
git diff --check
```

Use these existing cases as specific acceptance checks:

- `AdvancedVariableFilterAuthorizationPostgresTests.LogicalFilterIsConjoinedWithEveryMandatoryRepositoryScope`:
  advanced filters cannot widen any mandatory repository scope.
- `InboxVisibilityPostgresApiTests.VisibilityRunsBeforeCountOrderingAndPagingWithoutHoles`
  and `VisibilityRunsBeforeOnePerActorRepresentativeSelection`: hidden rows
  cannot affect totals, fill pages, or become representatives.
- `InboxVisibilityPostgresApiTests.DelegationUsesDelegateClaimsAndRolesAndExposesRepresentedOwner`,
  `NullWrongTypeAndDivisionByZeroAllFailClosed`, and
  `ManagementAndDistributionBypassHiddenPersonalVisibility`: delegation context,
  failure behavior, and management bypass remain unchanged.
- `TaskRoleManagementApiTests.PendingNormalTasksAreOptInAndOnlyRoleManagersCanManageThem`:
  extraction preserves the distinct manager permissions for pending work.
- `UserTaskApiTests.InboxProjectionUsesOneFreshSettingsReadPlusTwoQueriesAndPreservesPagingAndLatestVariables`,
  `InstanceUserTaskQueryCountIsPageBoundedForOnePerActorExecution`, and
  `RolePolicyInboxPerformanceTests.InboxPolicyReadsRemainBatchBoundedAtPageSizes50And200`:
  extraction preserves established query-count bounds.
- `InstanceSortingApiTests` and `WorkflowInstanceCursorTests`: existing ordering,
  tie-breaks, and cursor compatibility remain intact.

Add one PostgreSQL regression theory named
`TaskOwnershipFiltersPreserveManagerAndDistributionMembership` to
[AdvancedVariableFilterAuthorizationPostgresTests](../../Flowbit/tests/Flowbit.Tests/AdvancedVariableFilterAuthorizationPostgresTests.cs).
Exercise both repository methods against assigned, claimed-only, and unowned
tasks in an authorized workflow, with a second unauthorized workflow present.
Cover each ownership filter with and without a trimmed, differently cased owner
filter, owner-only filtering, and an owner mismatch. Include a task with both
assignee and claimant to verify assignee precedence. Assert exact returned IDs
and `TotalCount`, including a page size of one across multiple matches. This
tests the contract affected by the extraction rather than helper internals or
the literal SQL string.

Acceptance requires all selected and full-suite tests to pass, no new database
round trips, and a reviewed diff showing the original query-specific behavior
around the shared fragments. A Docker or environment failure must be reported
as an unavailable check, not as a passing result. No browser validation is
required because this stage changes no UI rendering or interactions.

## Documentation, delivery, and rollback

After implementation, update [Flowbit/README.md](../../Flowbit/README.md) with the
private helper ownership and preserved query boundaries. Review the
[API reference](../api-guide.md), [developer guide](../developer-guide.md), and
[AGENTS.md](../../AGENTS.md) for consistency; update them only if an existing
architectural explanation becomes inaccurate. The public query contract should
remain unchanged. Mark this plan implemented only after the acceptance checks,
and validate changed relative links.

Deliver this as one focused change containing the two extractions, regression
test, and affected documentation. Review it after Stage 1 or rebase it onto that
stage before merging if both touched related code. Deploy through the existing
application process; there is no data migration, feature flag, or coordinated
API/Worker upgrade requirement. Roll back by reverting this change and deploying
the previous application version; no stored data needs conversion. Investigate
any changed membership, totals, ordering, or query counts before further query
refactoring.

## Implementation record

Delivered as one buildable change:

- `WorkflowRuntimeRepository.QuerySql.cs` (Infrastructure/Repositories) joins
  the existing partial class beside the main file and holds both private
  helpers. No repository interface, DI registration, DTO, schema, or migration
  changed.
- `AppendTaskOwnershipFilter(StringBuilder where, List<(string Name,
  object Value)> args, string? owner, string? ownership)` moved the owner
  predicate and ownership switch out of `ListManageableUserTasksAsync` and
  `ListDistributableUserTasksAsync` unchanged: the bound `owner` parameter,
  whitespace trimming, case-insensitive comparison against
  `COALESCE(Assignee, ClaimedBy)`, and the assigned/claimed/unassigned
  definitions. Normalization and validation stay in the service entry points;
  the two methods keep their distinct manager-role and workflow-key scopes.
- `InboxVisibilityEvaluationCtes` contains the complete `evaluation_targets`
  and `visibility_results` definitions with their separating comma and no
  leading or trailing comma. `ListInboxAsync` and `ListUserTasksPageAsync`
  interpolate it between their own `base_candidates` and their subsequent
  `visible_candidates` / `eligible` CTEs, keeping their existing aliases,
  bound `@user` / `@visibilityFixedValues` parameters, representative
  ranking, count/page queries, ordering, empty-page handling, and
  repeatable-read transaction ownership. The two CTE bodies were compared
  byte-for-byte before extraction, and the constant's value was verified
  against the original text afterwards.
- `TaskOwnershipFiltersPreserveManagerAndDistributionMembership` (new
  PostgreSQL theory in `AdvancedVariableFilterAuthorizationPostgresTests`,
  nine cases) exercises both repository methods over assigned, claimed-only,
  unowned, and both-owner tasks in an authorized workflow with a second,
  unauthorized workflow present. Cases cover each ownership filter bare and
  with a trimmed, differently cased owner filter, an owner mismatch, and an
  owner/unassigned contradiction; the both-owner task proves assignee
  precedence. Each case asserts exact returned IDs and `TotalCount` for both
  methods, and the multi-match cases page both methods through results at
  page size one against the `UpdatedAt DESC, Id DESC` order. A second call
  with the other workflow's manager role confirms per-workflow management
  authorization.

Validation (2026-09-17): Docker was reachable, and the focused command above
passed 86/86 (77 matching the pre-change baseline plus the nine new theory
cases) with no failures or skips. The full suite passed 1,861 passed / 0
failed / 0 skipped, including all named acceptance checks. `git diff --check`
is clean. No membership, totals, ordering, or query-count change was observed,
so the stage's "no new database round trips" requirement holds. No UI
rendering or interaction changed, so no browser validation was required.
Documentation updates: [Flowbit/README.md](../../Flowbit/README.md) records
the private helper ownership and preserved query boundaries;
[this plan index](README.md) marks the stage implemented.
[API reference](../api-guide.md), [developer guide](../developer-guide.md),
and [AGENTS.md](../../AGENTS.md) were reviewed and stay accurate because no
HTTP route, DTO, JSON, error, authorization, ordering, paging, or cursor
contract changed.
