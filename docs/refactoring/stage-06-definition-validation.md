# Stage 6 — Extract definition validation

[Plan index](README.md) · [Gap inventory](gaps.md#3-workflowdefinitionservice)

**Status: Implemented.** Authored and normalized definition validation now
lives in a scoped `WorkflowDefinitionValidator` behind
`IWorkflowDefinitionValidator`; `WorkflowDefinitionService` retains lifecycle
orchestration. The implementation record at the end of this document reports
scope and test results. Added after reviewing the gap inventory. This stage
can proceed independently after a passing baseline. Stage 4 offers related
editor regression evidence, but the two validators remain separate.

## Objective and boundary

Separate authored and normalized definition validation from lifecycle operations
in [WorkflowDefinitionService.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowDefinitionService.cs).
The current service contains 3,795 physical lines at `c654024`, but the reason
for extraction is its mix of validation rules and persistence orchestration.

Keep create/version/publish/unpublish/default/delete operations, repository and
catalog access, model normalization, publication gates, cache warming, logging,
and DTO mapping in `WorkflowDefinitionService`. Leave the existing
[WorkflowVersionCompatibilityEvaluator](../../Flowbit/src/Flowbit.Service/Services/WorkflowVersionCompatibilityEvaluator.cs)
and runtime version-switch behavior unchanged.

This is a validation ownership change. It does not unify editor/server grammar,
add rules, alter accepted legacy models, change API error responses, or introduce
an aggregate-error response in place of the current first exception.

## Target ownership

Add `IWorkflowDefinitionValidator` under Service/Abstractions, implemented by a
scoped `WorkflowDefinitionValidator` under Service/Services. The public interface
can be accepted by the existing public service constructor and offers:

- `void ValidateAuthored(WorkflowModel definition)`: the current checks before
  `WorkflowModelMigrator.Normalize`, in their existing order.
- `void ValidateNormalized(WorkflowModel definition)`: the current internal
  `ValidateDefinition` body and its supporting rules. The caller must already
  have normalized the model.

These operations validate structure and configuration; a successful result
does not establish catalog existence, publication readiness, or version-switch
compatibility. They do not normalize, persist, or warm caches.

The validator uses `IScriptEvaluator`, `ServiceTaskOptions`, and
`IConditionalEventDefinitionAnalyzer`. It has no repository, logger, engine,
unit of work, scope factory, or dependency on `WorkflowDefinitionService`.
Keep the conditional analyzer in the lifecycle service too: publication and
shared-variable durability/lock checks still use it. Preserve current optional
configuration behavior when adapting reduced test compositions.

Move validation-only constants and helpers, including `SharedValidationFunctions`
and `ReservedIdempotencyHeaders`, with their rules. Keep `NormalizeContractRule`
with shared catalog comparison and retain `ToSummary`/`ToDetail` with their
current owner because other services call those mappings.

## Pipeline to preserve

| Operation | Existing order to retain |
| --- | --- |
| Create | Authored checks → normalize → normalized validation → shared catalog validation → shared service durability → shared lock-order validation → publication gate → repository add → cache warming → log/map. |
| Create new version | Look up source and return null if missing → replace incoming workflow key with source key → the same create validation/persistence sequence, with existing name fallback. |
| Publish / set default | Look up stored definition → conditional analysis → catalog/durability/lock-order/publication checks → repository update → existing result/logging. Do not insert full authored/normalized validation here. |
| Other lifecycle operations | Preserve their current calls and results; the extraction adds no validation to them. |

Authored validation calls the existing gateway, claim-bypass, script, exclusive
gateway, message-start, async/timer, inbox-visibility, conditional-event,
shared-variable metadata checks, then `ValidateRoleSources`. The normalized
pipeline checks unique identifiers before its own `ValidateRoleSources` call.
Both role-source checks are intentional and must remain.

Normalized validation ends with `InboxVisibilityConditionCompiler.CompileAll`
followed by conditional analysis. Preserve this order, all earlier traversals,
and the exact exception type/message for the first failure. Do not eagerly build
lookup dictionaries before duplicate-ID validation or reorder checks while
extracting helpers.

## Implementation sequence

1. Run existing definition and conditional/compiler tests. Capture missing
   characterization cases for validation order and lifecycle effects before
   moving code. Verify source anchors against the implementation checkout.
2. Introduce the validator port and implementation. Move the existing normalized
   entry body with its private rule dependencies. Preserve conditions, iteration
   order, null handling, expression validation, and parse-only script checking.
3. Move authored checks into `ValidateAuthored` in the order listed above. Keep
   the migration call between the two validator operations in the lifecycle
   service. Avoid a generic rule registry or a new pipeline framework.
4. Inject the validator into `WorkflowDefinitionService` and register it as
   scoped in [service DI](../../Flowbit/src/Flowbit.Service/DependencyInjection/ServiceCollectionExtensions.cs).
   Remove dependencies from the lifecycle constructor only when it no longer
   uses them. Retain its analyzer and publication/catalog dependencies.
5. Update direct test constructors. Replace the reflection helper that calls
   internal `ValidateDefinition` in `DefinitionValidationTests` with direct
   `ValidateNormalized` calls for validation-only cases. Keep create/version/
   publication tests exercising `WorkflowDefinitionService` so orchestration
   remains covered. Do not add fallback construction or service-location code
   merely to preserve old test constructors.
6. Group the moved rules by responsibility in the validator using existing
   helper boundaries. If file organization needs several partial files, keep
   ownership explicit and avoid a simultaneous rewrite into many rule services.
   The first deliverable is separation from lifecycle operations.
7. Run focused/full tests, review API errors and normalized saved models against
   the baseline, and update architecture documentation in the same change.

## Tests and acceptance

Reuse [DefinitionValidationTests](../../Flowbit/tests/Flowbit.Tests/DefinitionValidationTests.cs),
[ConditionalEventDefinitionTests](../../Flowbit/tests/Flowbit.Tests/ConditionalEventDefinitionTests.cs),
[InboxVisibilityConditionCompilerTests](../../Flowbit/tests/Flowbit.Tests/InboxVisibilityConditionCompilerTests.cs),
[SharedVariableAccessPlanTests](../../Flowbit/tests/Flowbit.Tests/SharedVariableAccessPlanTests.cs),
and [WorkflowDefinitionRepositoryTests](../../Flowbit/tests/Flowbit.Tests/WorkflowDefinitionRepositoryTests.cs).
Retain compatibility and API contracts in the full suite.

Acceptance scenarios:

- Existing curated definitions retain acceptance and the same normalized saved
  JSON; invalid authored metadata remains rejected before migration can erase it.
- Representative inputs with multiple violations produce the same first
  exception and message. Include duplicate IDs, role-source metadata, and
  normalized expression or shared-variable errors.
- Missing source versions return the same result before validation. Valid new
  versions retain the source workflow key and existing name fallback.
- Rejected create/version requests make no repository write or cache-warming
  call. Successful requests preserve those effects and their order.
- Draft and published behavior remains covered by
  `CreateAsync_PublicationGateAllowsDraftButRejectsDurablePublish`,
  `SetDefaultAsync_RejectsLegacyPublishedUnsafeSharedRestDefinition`, and
  `PublishAndSetDefaultRejectStoredSharedConditionalDefinition`.
- Shared catalog requirements, service durability, and lock-order checks remain
  on create/version/publish/default paths. Validator-only tests do not stand in
  for those lifecycle tests.
- Script syntax is checked without executing the script; expression grammar,
  inbox compiler conformance, conditional analysis, and cache identity remain
  unchanged. No additional database queries are introduced by the validator.

Add tests only for uncovered risks above. From the repository root in PowerShell
or Bash:

```text
docker info
dotnet test Flowbit/Flowbit.slnx --filter "FullyQualifiedName~DefinitionValidationTests|FullyQualifiedName~ConditionalEventDefinitionTests|FullyQualifiedName~InboxVisibilityConditionCompilerTests|FullyQualifiedName~SharedVariableAccessPlanTests|FullyQualifiedName~WorkflowDefinitionRepositoryTests|FullyQualifiedName~WorkflowVersionCompatibilityEvaluatorTests|FullyQualifiedName~OpenApiContractTests"
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet
git diff --check
```

Record actual results, including unavailable checks. Completion requires the
focused and full suite to pass with no unexplained new skips. No UI rendering
or interactions change, so browser verification is not required for this stage.

## Documentation, delivery, and rollback

Update validation/lifecycle ownership in [Flowbit/README.md](../../Flowbit/README.md)
and the relevant [AGENTS.md](../../AGENTS.md) architecture explanation. Compare
[BPMN support](../bpmn-support.md), [node reference](../node-reference.md),
[developer guide](../developer-guide.md), and [API reference](../api-guide.md)
with the final implementation. Public rules and HTTP examples should remain
unchanged; update descriptions only where this move makes them inaccurate.
Validate changed relative links and examples.

Deliver missing characterization tests first if needed, then the complete
extraction, DI/test-caller changes, and documentation in a buildable change.
Mark this stage and the index implemented only after acceptance. Roll back by
reverting the extraction and deploying the previous application version. No
schema migration, saved-definition conversion, or coordinated API/Worker
upgrade is required.

## Implementation record

Implemented (2026-09-19). The new
[WorkflowDefinitionValidator.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowDefinitionValidator.cs)
owns `ValidateAuthored` (the nine authored-metadata checks plus
`ValidateRoleSources` in the create/version order) and `ValidateNormalized`
(the former internal `ValidateDefinition` body ending with
`InboxVisibilityConditionCompiler.CompileAll` followed by conditional
analysis), together with the `SharedValidationFunctions` and
`ReservedIdempotencyHeaders` constants and all supporting static rules. Its
dependencies are `IScriptEvaluator`, `ServiceTaskOptions`, and an optional
`IConditionalEventDefinitionAnalyzer` (defaulting to
`ConditionalEventDefinitionAnalyzer`); it has no repository, logger, engine,
unit of work, or scope factory, performs no database access, and neither
normalizes nor persists.

[WorkflowDefinitionService.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowDefinitionService.cs)
now injects `IWorkflowDefinitionValidator` in place of
`IScriptEvaluator`/`ServiceTaskOptions`; create and create-new-version call
`ValidateAuthored` → `WorkflowModelMigrator.Normalize` → `ValidateNormalized`
→ shared-catalog, durability, lock-order, and publication checks in the
original order. Publish/set-default keep conditional analysis plus the
shared/durability/lock-order/publication checks and intentionally do not run
full authored/normalized validation. `NormalizeContractRule`,
`ToSummary`, and `ToDetail` stayed with the lifecycle service because the
shared-catalog comparison and other services use them. The validator is
registered scoped in the service DI extension. `WorkflowVersionCompatibilityEvaluator`
and runtime version-switch behavior are unchanged; no rule, order, message,
status code, or accepted legacy shape changed, and no aggregate-error
response was introduced.

Test and tool callers updated: `DefinitionValidationTests` builds the
validator through a new `CreateValidator` helper and replaced the reflection
helper with direct `ValidateNormalized` calls for its validation-only cases;
create/version/publication tests still exercise `WorkflowDefinitionService`.
`ConditionalEventDefinitionTests` and `InboxVisibilityConditionCompilerTests`
compose the validator with the same script evaluator/analyzer substitutes
their service compositions used, and `MultiInstanceVerifier` calls
`ValidateNormalized` directly without reflection. Five characterization tests
cover previously unverified lifecycle effects: rejected create and missing
source-version create make no repository write or cache-warming call, a
successful create writes the repository and warms both plan caches, and a
rejected new-version request makes no write.

Validation (2026-09-19): Docker PostgreSQL reachable. The focused set passed
471/471 (467 baseline tests plus five new characterization cases), and the
full suite passed 1,889 / 0 failed / 0 skipped. `git diff --check` is clean.
`MultiInstanceVerifier` compiles against the new validator but its
`votes*.json` samples are not present in this checkout, so it could not be
executed; that limitation is recorded here rather than claimed as a pass. No
HTTP route, DTO, JSON, error, authorization, or saved-model contract changed,
so no browser verification was required. Documentation updates:
[Flowbit/README.md](../../Flowbit/README.md) definition validation ownership
section, [AGENTS.md](../../AGENTS.md) service-layer bullet, the
[node reference](../node-reference.md) validation link, this record, and the
[plan index](README.md) status. The API, developer, BPMN support, and node
property pages needed no change because their public rules and HTTP examples
are unaffected.
