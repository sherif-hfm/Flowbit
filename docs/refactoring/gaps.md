# Refactoring plan gaps

[Plan index](README.md)

**Status: Planning inventory — not implemented.** This page records work beyond
the initial five-stage scope and where that work belongs. Definition validation
has a separate Stage 6 plan, and browser coverage now has a detailed Stage 7
plan. Other candidates retain the decisions described below and in the
[remaining-gaps roadmap](remaining-gaps-implementation-plan.md). This inventory
contains targets and evidence, not implementation instructions. The
[shared implementation rules](README.md#shared-implementation-rules) apply to
any future stage created from this list.

Measurements were checked at commit `c654024`, the same baseline as the stage
plans. All file totals below count physical lines, including blank lines. The
earlier inventory mixed nonblank totals with physical source locations, which
understated sizes and produced incorrect `@code` lengths. Re-measure and inspect
the named symbols before implementation; size alone does not justify extraction.

## Summary

| # | Gap | Current evidence (at `c654024`) | Existing stage coverage |
| --- | --- | --- | --- |
| 1 | Engine core responsibilities still interleaved in `WorkflowEngineService` | 18,468 lines across 9 partial files; 19 constructor parameters; 5 implemented interfaces | [Stage 1](stage-01-instance-queries.md) (implemented) and [Stage 3](stage-03-engine-responsibilities.md) extract two read-side slices only |
| 2 | Engine interface breadth | `IWorkflowEngineService` declares 33 `Task`-returning members after Stages 8–9 | Stage 8 removed three query/detail forwards; Stage 9 moved four role operations to `IUserTaskRoleManagementService` |
| 3 | `WorkflowDefinitionService` | 3,795 lines combining validation and lifecycle operations | Added [Stage 6](stage-06-definition-validation.md) for validation extraction |
| 4 | Remaining query assembly duplication | `WorkflowRuntimeRepository` has 7,289 lines; basic filters and instance/inbox sort parsing are already shared | [Stage 2](stage-02-repository-query-helpers.md) extracts ownership predicates and inbox visibility CTEs; further candidates need separate evidence |
| 5 | Editor hotspots outside save validation | `renderNodeInspector` (~524 lines), `compileInboxVisibilityCondition` (~499 lines), `applyTypeInvariants` (~327 lines); flat script with no modules | [Stage 4](stage-04-editor-validation.md) covers `validateModelForSave` only |
| 6 | Secondary oversized UI units | Four management pages with approximately 332–835-line `@code` blocks; `WorkflowApiClient` 1,857 lines | [Stage 5](stage-05-instance-detail-components.md) covers `InstanceDetail.razor` only |
| 7 | Automated Chromium smoke suite exists; residual Stage 5 acceptance remains | Repeatable E1–E5/R1–R6 coverage plus a `browser-smoke` CI job; native-picker, Worker-driven batch links, and remote CI observation are still open | [Stage 7](stage-07-browser-smoke-and-stage-05-acceptance.md) implemented for the no-Worker smoke suite; Stage 5 acceptance still open |

## Decisions for the stage plans

| Area | Decision | Reason |
| --- | --- | --- |
| Definition validation | Add Stage 6, independently implementable after a passing baseline. | Authored checks and normalized validation have a concrete extraction boundary; lifecycle behavior stays in the existing service. |
| Remaining engine and interface work | Record remaining consumers and shared invariants when Stage 3 finishes; plan one command responsibility at a time afterward. | A single extra "split the engine" stage would conceal transaction, authorization, and lock-order decisions. |
| Repository assembly | Keep Stage 2 bounded; retain the specific remaining candidates in this inventory. | Much of the claimed duplication already has shared implementations, and query surfaces have different semantics. |
| Editor parser and other functions | Make existing grammar conformance checks explicit in Stage 4; defer inspector/type-invariant/parser decomposition. | The parser is an existing validator dependency, while the other functions have different mutation and rendering responsibilities. |
| Other UI pages/client | Reassess one page at a time after Stage 5 establishes a verified component pattern. | Size is evidence to inspect; it is not an automatic requirement to split every file. |
| Browser automation | Keep real-browser verification required; consider a separate test-infrastructure plan for a small repeatable smoke suite. | A new browser harness is useful but is not required to implement the bounded extractions with the existing verification gate. |

## 1. Engine core responsibilities

[WorkflowEngineService.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.cs)
is a `sealed partial class` whose remaining eight partial files exclude the
former `WorkflowEngineService.RoleManagement.cs` (219 lines at the Stage 3
measurement). Stage 9 (2026-09-20, working tree based on `67945f7`) moved
those four operations into `UserTaskRoleManagementService` and deleted the
partial. The other dated Stage 3 totals below are historical:

| Partial file | Lines |
| --- | --- |
| `WorkflowEngineService.cs` | 11,262 |
| `WorkflowEngineService.Jobs.cs` | 3,397 |
| `WorkflowEngineService.AdministrativeActions.cs` | 1,050 |
| `WorkflowEngineService.Reactivation.cs` | 874 |
| `WorkflowEngineService.VersionChange.cs` | 408 |
| `WorkflowEngineService.VersionChangeBatch.cs` | 212 |
| `WorkflowEngineService.InboxVisibility.cs` | 154 |
| `WorkflowEngineService.RolePolicies.cs` | 60 |

Stages 1 and 3 remove instance list/search orchestration and detail/execution
projection. Both are implemented. Stage 3 extracted `BuildDetailAsync`,
`BuildExecutionProjectionAsync`, grouped/single multi-instance progress, and
version-change/variable-update audit loading into the scoped
`WorkflowInstanceProjectionService` behind `IWorkflowInstanceProjectionService`,
and moved the pure runtime mappings (workflow cloning/redaction, fault info,
work summaries, multi-instance progress, version-change audit/summary/
direction) into the shared `RuntimeProjectionMapper`. The engine keeps thin
delegations so command call sites retain their `SaveChangesAsync`/`CommitAsync`
positions, and it retains routing, claim/action authorization, capability
assembly (`BuildUserTaskPresentationAsync`, `BuildUserTaskCapabilities`,
`GetEligibleUserTaskFlows`), settings caching, and message authentication.
Measured during Stage 3: one warm-definition detail projection issues 17
reader commands through the service — identical to the recorded pre-extraction
baseline — and the count does not grow with multi-instance child items or
version-change audit volumes. The
[plan index](README.md) explicitly defers the remaining
responsibility groups, which are interleaved in the main partial:

- Conditional wait and boundary triggering.
- Start and message-start entry with idempotency and business-key claims
  (`StartInstanceAsync`, `StartInstanceSlimAsync`, and `StartByMessageAsync`).
- Inbox paging, sort parsing, claim/unclaim, task assignment, and distribution
  authorization.
- Multi-instance interrupts and user-task flow execution, including
  `TakeFlowCoreAsync`.
- Message delivery: credential templating, idempotency receipts, and proof
  derivation (`DeliverMessageAsync` and its helpers).
- `ResolvePassThroughAsync` and the generic gateway machinery — fork, joins,
  inclusive enabling, complex state.
- Service and script task execution with the nested `EngineScriptContext`
  and its evaluation context.
- Variable load, write, context-map, default-resolution, and validation
  helpers.
- Durable job staging, leasing, and processing in
  [WorkflowEngineService.Jobs.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.Jobs.cs).

A future stage must first decide transaction and lock-order ownership per
group, which repository ports move with it, how command call sites keep their
commit points relative to `SaveChangesAsync`/`CommitAsync`, and how the
engine interface shrinks (gap 2). Message authentication additionally requires
a plan covering settings rotation, replay proofs, and verified actor
attribution, as noted by
[Stage 3](stage-03-engine-responsibilities.md).

## 2. Engine interface breadth

`IWorkflowEngineService`, declared in
[Services.cs](../../Flowbit/src/Flowbit.Service/Abstractions/Services.cs),
exposed 40 `Task`-returning members at the dated baseline above, spanning
queries, workflow/task commands, and lifecycle operations. Job processing is exposed through a separate
`IWorkflowJobProcessor` implemented by the same class. Stage 1 (implemented)
added a focused instance-query port and moved the two endpoint consumers to it,
while retaining both engine members as compatibility forwards. Stage 3 added a
projection port but kept `GetInstanceAsync` as a forwarding compatibility
method. Neither stage reduces the engine interface's member count.

The [detailed Stage 8 plan](stage-08-remove-query-detail-compatibility.md)
records the implemented removal of those three forwards and the unused
engine query-service dependency (40 → 37). The
[detailed Stage 9 plan](stage-09-waiting-task-role-management.md) records the
implemented extraction of the four waiting-task role operations onto
`IUserTaskRoleManagementService`. The working tree based on `67945f7` now
declares **33 methods**, with all other interface members unchanged. Role
GET/POST handlers inject the focused service directly; the engine neither
implements nor depends on it. HTTP routes, DTOs, authorization, and lock order
are unchanged. Broader
interface removal remains deferred; segmentation should follow the responsibility
extractions in gap 1, so each member group moves once.

## 3. WorkflowDefinitionService

[WorkflowDefinitionService.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowDefinitionService.cs)
is 3,795 physical lines. It mixes definition
validation (`ValidateDefinition` and its per-node-type rule groups),
create/version/publish/delete orchestration, and DTO assembly. The
compatibility evaluator is already separate
([WorkflowVersionCompatibilityEvaluator.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowVersionCompatibilityEvaluator.cs),
1,362 lines). [Stage 6](stage-06-definition-validation.md) now plans separation of
authored and normalized validation from lifecycle orchestration. It must preserve
the checks before and after normalization, first-error order/text, catalog and
publication gates, and cache warming. Version switching uses the separate
compatibility evaluator; do not conflate its runtime compatibility rules with
the definition validation pipeline or change either during extraction.

## 4. Repository filter/sort/paging duplication

[WorkflowRuntimeRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/WorkflowRuntimeRepository.cs)
is 7,289 physical lines. Stage 2 extracts the task ownership predicates shared by
`ListManageableUserTasksAsync`/`ListDistributableUserTasksAsync` and the
duplicated `evaluation_targets`/`visibility_results` CTEs. Basic filters already
share `AppendInstanceIdFilter`, `AppendWorkflowIdFilter`, `AppendWorkflowKeyFilter`,
`AppendBusinessKeyFilter`, `AppendNodeIdFilter`, and `AppendNodeExternalIdFilter`.
Variable-expression compilation uses the shared
[VariableFilterSqlCompiler](../../Flowbit/src/Flowbit.Infrastructure/Repositories/VariableFilterSqlCompiler.cs).
Instance and inbox sort validation already delegates to the engine's generic
`ParseSort`; management/distribution have fixed task ordering. Stage 1 moves
shared input parsing without creating another implementation.

Paging is intentionally different: instance queries count before their keyset
cursor predicate, while inbox queries use offset paging after visibility and
representative selection under repeatable-read isolation. Repeated helper
composition is not evidence that these queries should share a universal builder.

Concrete remaining candidates are management/distribution count-and-page
execution and task projection setup, plus predicates in `MaterializeAsync` in
[InstanceVersionChangeCandidateRepository](../../Flowbit/src/Flowbit.Infrastructure/Repositories/InstanceVersionChangeCandidateRepository.cs)
and
[InstanceVariableUpdateCandidateRepository](../../Flowbit/src/Flowbit.Infrastructure/Repositories/InstanceVariableUpdateCandidateRepository.cs).
Their `SearchAsync` methods already reuse runtime instance listing.
[AdministrativeActionCandidateRepository](../../Flowbit/src/Flowbit.Infrastructure/Repositories/AdministrativeActionCandidateRepository.cs)
already shares `BuildWhere`/`CandidateFromSql` internally and selects task or
multi-instance positions rather than ordinary instance rows.

Keep Stage 2's current scope. Consider further extraction when a demonstrated
change or maintenance need justifies it, preserving scope, selection unit,
bound parameters, ordering, paging, and transaction semantics. A generic query
framework is not presently justified.

## 5. Editor hotspots outside save validation

Stage 4 addressed `validateModelForSave` (now a dispatcher that builds a fresh
context with `createSaveValidationContext` and runs named `validateSave*`
phase functions; the validator region is 9555–12493) in
[flowbit-editor.html](../../flowbit-editor.html). Other
measured hotspots, in descending size:

| Function | Line | Approximate length |
| --- | --- | --- |
| `renderNodeInspector` | 5169 | 524 |
| `compileInboxVisibilityCondition` | 9706 | 499 |
| `applyTypeInvariants` | 3460 | 327 |
| `renderFlowInspector` | 7581 | 245 |

`compileInboxVisibilityCondition` is a hand-written NCalc-like parser that
duplicates grammar knowledge owned by the backend
`InboxVisibilityConditionCompiler`. Existing editor/server tests already check
a shared conformance corpus. Stage 4 must retain those checks and the parser's
availability inside the validator marker block; parser decomposition or a
shared grammar generator needs its own analysis. The inspector subsystem
(roughly lines 5024–8462) is factored into section builders. The script has 34
top-level `let`/`var` declarations; that count does not establish that every one
couples to the inspector. Structural items the stages defer: dual
DOM-building idioms (the `el()` helper versus raw `createElement`), five
inline `onclick=` handlers in the modal markup that depend on globals, and
`alert()`/`confirm()` usage beside the validation modal. The editor
module/build-system migration is explicitly out of scope and would need its
own plan that preserves the documented no-build, single-file constraint or
explicitly abandons it.

## 6. Secondary oversized UI units

Stage 5 decomposes `InstanceDetail.razor` only. Comparable units with no plan:

| File | Total lines | `@code` block |
| --- | --- | --- |
| [AdministrativeActions.razor](../../Flowbit/src/Flowbit.Ui/Components/Pages/AdministrativeActions.razor) | 1,379 | ~835 (from line 545) |
| [InstanceVersionChanges.razor](../../Flowbit/src/Flowbit.Ui/Components/Pages/InstanceVersionChanges.razor) | 1,003 | ~612 (from line 392) |
| [InstanceVariableUpdates.razor](../../Flowbit/src/Flowbit.Ui/Components/Pages/InstanceVariableUpdates.razor) | 988 | ~582 (from line 407) |
| [DelegationManagement.razor](../../Flowbit/src/Flowbit.Ui/Components/Pages/DelegationManagement.razor) | 632 | ~332 (from line 301) |

[WorkflowApiClient.cs](../../Flowbit/src/Flowbit.Ui/Clients/WorkflowApiClient.cs)
is 1,857 physical lines: one typed client spanning several endpoint groups with
shared request plumbing. Treat it as a lower priority; a split, if ever needed,
should follow endpoint groups.

## 7. Automated visual and interaction coverage

The standalone
[Flowbit.BrowserTests](../../Flowbit/tests/Flowbit.BrowserTests/README.md)
suite now provides repeatable Chromium coverage: E1–E5 drive the real copied
editor (load/save via the file chooser and forced download fallback, node and
lane drags with persisted-JSON assertions, validation dialog recovery, keyboard
menus/search/undo) and R1–R6 drive the real published API/UI (inbox claim and
action lifecycle, instance navigation, identity replacement, gateway/complex/
multi-instance detail sections, genuine five-second polling with an out-of-band
HTTP completion, and responsive/keyboard coverage at the narrower viewports).
The suite owns a disposable PostgreSQL container and published API/UI child
processes on ephemeral loopback ports, runs serially with per-scenario browser
contexts, and the `browser-smoke` CI job uploads TRX, traces, screenshots, and
host logs. The Jint and `HtmlRenderer` tests remain in place, including the new
deterministic in-flight identity-response regression.

What remains open: the manual native-file-picker success/cancel check on a
headed desktop Chromium; Stage 5's real version/variable batch links using a
separate Worker-driven stack; selected-claim and delegation attribution;
administrative action refresh (the immediate action needs no Worker); the pre-extraction visual
comparison, and observing the remote `browser-smoke` job pass. The
[real-browser gate](../../AGENTS.md#mandatory-real-browser-ui-verification)
remains required for UI changes beyond this suite's coverage.

## Converting a gap into a stage

1. Re-measure the target at the current commit and confirm the anchors above
   still apply; the stage plans were written against `c654024` and drift
   accumulates.
2. Perform the boundary analysis the [plan index](README.md) defers:
   ownership, transactions, lock order, interface seams, and the exact
   behaviors that must not change.
3. Write a stage document in this folder following the existing stage format:
   objective and scope, target ownership, implementation sequence,
   behaviors to preserve, tests and acceptance, documentation impact, and
   rollback.
4. Keep the [shared implementation rules](README.md#shared-implementation-rules):
   no behavior changes inside an extraction, one responsibility per stage,
   and no migrations or new runtime dependencies.
