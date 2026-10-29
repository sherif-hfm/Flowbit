# EVENT-01: Expression timers

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Done |
| Category | New event capability |
| Effort | M |
| Priority | Next tier: deadline and timer follow-up 1 |
| Depends on | None |
| Related | [UI-04](ui-04-task-due-date-and-priority.md), [EVENT-08](event-08-business-calendars.md), [EVENT-09](event-09-cron-schedules.md), [EVENT-03](event-03-event-based-gateway.md) |

## Summary

Timer catches and timer boundaries whose `timeDate` or `timeDuration` comes from persisted instance data, such as `timeDate: "${contractEnd}"` or `timeDuration: "if(urgent, 'PT4H', 'P2D')"`. The engine evaluates the expression when the timer activates and saves the resulting UTC due time. A duration can use an existing business calendar.

## Why it adds value

- Most real deadlines depend on data, such as a contract end date or an SLA chosen per request. Today a timer can only use a value fixed in the definition.

## First release acceptance criteria

The plan revises these criteria on 2026-10-28. It chooses NCalc, limits inputs to persisted instance variables, includes the already implemented business calendars, covers reactivation and schedule recovery, and distinguishes the editor's local checks from authoritative server validation. Full NCalc syntax and dependency validation in the standalone editor would require a separate parser; that is outside this release.

- [x] `timeDate` and `timeDuration` on `intermediateTimerCatchEvent` and `timerBoundaryEvent` accept fixed values or NCalc expressions. Raw expressions and an optional whole-expression `${...}` wrapper use the same evaluator.
- [x] Expressions read only declared persisted instance variables and return a nonblank string valid for the selected schedule property. A stored `datetime` qualifies only when its text fits that grammar, with a `Z` or `±hh:mm` offset. Shared aliases, runtime context, numeric seconds, and implicit date/time conversions are rejected.
- [x] Evaluation occurs once per successfully committed activation. The subscription retains the authored expression and resolved due time; variable/calendar edits, Worker restarts, firing retries, and compatible version changes do not recompute it.
- [x] A missing or unusable result fails scheduling atomically with a useful node/property diagnostic that contains no variable values. Synchronous transitions roll back and write a value-free warning log; durable execution uses its existing retry/incident policy. Past absolute dates become immediately eligible for Worker delivery.
- [x] Expression durations work with literal `timer.calendar` names and the current working-time rules; invalid resolved working durations fail activation.
- [x] Reactivation preview marks a target ineligible when one of its expression timer boundaries cannot be resolved, and warns when a timer boundary on it would already be due. Preview and commit agree.
- [x] The API checks syntax, functions, declared references, and permitted context before accepting a definition. The editor checks fixed forms, lengths, known forbidden references, and calendar combinations, and explains that full expression validation occurs on API save.
- [x] Active timer version-switch contracts compare authored configuration and retain saved timestamps. Incompatible changes are rejected even when two expressions currently resolve to the same due time.
- [x] Existing fixed timers, timer starts, recurrence, administrative timer actions, and old workflow documents keep their runtime behavior; fixed `timeDate` boundaries only gain the reactivation warning. No database migration or new public endpoint is required.
- [x] Documentation states the offset requirement for `datetime` inputs, how already-due boundaries behave, and how to recover from a failed or wrong schedule.
- [x] Automated runtime/authoring tests and real-browser editor and Worker-enabled scenarios cover the release, with evidence recorded before marking it Done.

## Later releases

- Expression-based `timeCycle`.
- Expression schedules on `timerStartEvent`, which has no instance-variable snapshot when its schedule is created.
- Recomputing a due time when a source variable changes.
- An administrative action that reschedules or fires an open timer, and showing a waiting timer's due time on instance detail.
- Capturing an already-due interrupting boundary at activation, before its host's external call or script body runs.
- A durable record, such as an incident, for synchronous scheduling failures.
- Shared-variable and `sys.*` / `config.*` / `setting.*` inputs, including the required shared access-plan and lock-order work.
- Calendar names selected by expressions, longer expressions, numeric duration units (until then, a JavaScript script task can write a duration string), and additional date arithmetic helpers.
- Full offline NCalc syntax/dependency validation in the standalone editor.

Working-time durations are already implemented by [EVENT-08](event-08-business-calendars.md); composing them with expression results belongs to this release. Cron remains [EVENT-09](event-09-cron-schedules.md), and event races remain [EVENT-03](event-03-event-based-gateway.md).

## Current state

Verified on 2026-10-28: done. The first release acceptance criteria are met, with no migration or new endpoint.

- **Rules.** `TimerDefinitionRules.ClassifyScheduleValue` in [WorkflowModel.cs](../../Flowbit/src/Flowbit.Shared/Models/WorkflowModel.cs) sorts a value into fixed, expression, or invalid, using intended-fixed and quoted-literal rules that `UserTaskDueRules` now shares. `TryInterpretExpressionResult` reads a result with the property's fixed grammar. Timer starts and `timeCycle` stay fixed only.
- **Validation.** `TimerExpressionDefinitionAnalyzer` checks syntax, functions, and references against the declared persisted variables (`ConditionalEventDefinitionAnalyzer.DeclaredStoredVariableNames`), rejects runtime context and shared aliases, rejects ambiguous casing only for referenced names, rejects an NCalc built-in in a casing NCalc does not resolve (such as `If` or `max`, through the spelling list in `SequenceFlowConditionEvaluator` that `NCalcFunctionSpellingTests` pins), rejects a bracketed name with spaces inside the brackets (such as `[ slaDuration ]`, which NCalc would never match), and evaluates expressions without references at save. `WorkflowDefinitionValidator` runs it only for values classified as expressions, after the existing rules, and its FlowInfo and gateway-function checks now include timer fields.
- **Activation.** `ResolveTimerScheduleAsync` in [WorkflowEngineService.Jobs.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.Jobs.cs) evaluates the expression against the transition's stored-variable snapshot, resolves the result with `WorkflowTimerSchedule.TryResolveExpressionResult`, and saves the authored text in `ScheduleExpression` and the result in `NextDueAt`. A catch and its boundaries share one activation instant, and staged service and script hosts load the instance variables before their boundaries. A failure throws a value-free `WorkflowDomainException` and writes one value-free warning log.
- **Reactivation.** The preview evaluates a target's timer boundaries: `timer_boundary_unresolvable` blocks the target, and `timer_boundary_already_due` warns about an expression or fixed `timeDate` boundary that is already due. The commit repeats the assessment, so it refuses a target the preview refused with `409`.
- **Editor.** The timer inspector's **Schedule** selector picks a date or a duration, and its **Value** selector picks **Fixed** or **Expression**. The save validator's `classifyTimerScheduleValue` and `timerScheduleValueProblems` mirror the shared rules, and the standalone editor says that saving through the API checks NCalc syntax and variable names. Without a parser, `misspelledBuiltInCall` reports a built-in in another casing with the server's message; `EditorValidatorTests` compares its spelling list with the server's and its findings with NCalc's own parse.
- **Examples and documentation.** [timers/07](../../examples/timers/07-contract-end-wait.json), [timers/08](../../examples/timers/08-urgency-review-deadline.json), and [timers/09](../../examples/timers/09-working-day-sla-from-number.json) cover a stored deadline, a chosen duration, and a working-day count built from a number. The owning pages in [section 10](#10-documentation-and-examples-to-ship-with-implementation) are updated, including both browser suite READMEs.
- **Tests.** New: `TimerExpressionRulesTests`, `TimerExpressionDefinitionTests`, and `ExpressionTimerRuntimeTests`. The runtime tests use PostgreSQL in 14 cases: activation snapshots, one activation instant for a wait set under a clock that advances on every reading, kept due times, rollback without values, a single value-free warning log for a failure whose NCalc message would repeat the value, offset-free values, calendars, conditional capture before timers, staged hosts and retries, an already-due boundary that interrupts a staged service task while its call runs and discards the late result, version switches, both reactivation outcomes, and the catalog examples. `EditorValidatorTests`, `EditorRuntimeSmokeTests`, `ExampleWorkflowCatalogTests`, and `WorkflowAiWorkspaceTests` gained cases or updated counts.
- **Browser scenarios.** In the [smoke suite](../../Flowbit/tests/Flowbit.BrowserTests/README.md#what-is-covered), E12 (1440×900 and 1024×768) drives the standalone timer inspector: both selectors, live errors for inserted text and `[sys.now]`, the save they block, a wrapped expression with a calendar, the fixed-mode note for expression text, a schedule change undone and redone from the keyboard, and save and reload. H11 (1440×900 and 1024×768, then 390×844) saves through the hosted editor: the API refuses an undeclared variable and the draft stays, the corrected expression saves and reopens in Expression mode, and the narrow layout keeps the field in view. In the [Worker-enabled suite](../../Flowbit/tests/Flowbit.BrowserAcceptanceTests/README.md#coverage), `ExpressionTimerAcceptanceTests` (1440×900 and 390×844) checks that the contract-end example keeps its saved due time on instance detail and **Operations** through a variable update and a Worker restart, then fires on time. It runs a non-interrupting reminder and an interrupting deadline from start data, with both due times on **Administrative actions**, and shows the reactivation preview's already-due warning. Finally, a staged script whose boundary input is missing retries, opens an incident, and recovers through **Operations** after an API variable update. The full smoke suite passed all 81 cases, and the six acceptance cases passed with no browser console errors or warnings. The suite's other 53 cases passed except three administrative batch cases. A2 and A4 passed when that class was rerun. A5 and A8 at 1024×768 failed there and also against hosts published from the base commit, in the same 10-second scroll wait in that suite's screenshot helper, so that failure predates this release. The same day the helper and those cases were made reliable on a busy machine (see the [suite README](../../Flowbit/tests/Flowbit.BrowserAcceptanceTests/README.md#coverage)), and the administrative class then passed in two consecutive runs.
- **Manual verification** on a local stack also covered the standalone and hosted editors, and an urgency instance whose boundary the Worker fired 45 seconds after it started. The native save picker's success and cancel paths were not exercised.

## Builds on

- [WorkflowTimerSchedule.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowTimerSchedule.cs): fixed-value parsing, UTC arithmetic, working-time calculation, and recurrence handling.
- [SequenceFlowConditionEvaluator.cs](../../Flowbit/src/Flowbit.Service/Services/SequenceFlowConditionEvaluator.cs): NCalc parsing/evaluation, case-insensitive parameter lookup, the optional `${...}` wrapper, and typed results.
- `UserTaskDueRules` in [WorkflowModel.cs](../../Flowbit/src/Flowbit.Shared/Models/WorkflowModel.cs): `IntendedFixedValuePattern`, `QuotedLiteral`, `DueDateProblem`, `DueDateCalendarProblem`, `TryInterpretDueDateResult`, and `TryApplyCalendar` already classify and interpret values over the same timer grammar. The editor mirrors them in `USER_TASK_INTENDED_FIXED_DUE_VALUE`, `quotedUserTaskLiteral`, `userTaskDueValueProblem`, and `userTaskDueDateCalendarProblem`. `TryEvaluateUserTaskValue` in [WorkflowEngineService.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.cs) catches every evaluation failure except cancellation.
- [ConditionalEventDefinitionAnalyzer.cs](../../Flowbit/src/Flowbit.Service/Services/ConditionalEventDefinitionAnalyzer.cs): AST-based parameter/function inspection and the vocabulary of declared persisted variables. Timers reuse this analysis pattern, not conditional wake subscriptions.
- [JobModels.cs](../../Flowbit/src/Flowbit.Service/Models/JobModels.cs) and [TimerSubscriptionRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/TimerSubscriptionRepository.cs): persisted schedules, activation identity, and occurrence fencing.
- [WorkflowVersionCompatibilityEvaluator.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowVersionCompatibilityEvaluator.cs): open-timer and attached-boundary contract comparison.
- `EvaluateReactivationConditionalBoundaries` in [WorkflowEngineService.Reactivation.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.Reactivation.cs): the reactivation preview's existing check of a target's boundaries against current variables.

## Open questions

The plan resolves the first-release questions:

- **Language:** NCalc, with the existing optional whole-expression `${...}` wrapper. `${contractEnd}` is one expression, not arbitrary text interpolation.
- **Results:** strings only, parsed with the selected property's fixed grammar. A `timeDate` result must include a time and offset; a `timeDuration` result must be a supported positive ISO-8601 duration. A stored `datetime` qualifies only when its text also fits that grammar, which is stricter than the `datetime` type (see the result rules in [section 2](#2-workflow-json-grammar-and-compatibility)). A date-only value or number never qualifies; append a fixed time and offset to a `date` input instead (`endDate + 'T17:00:00Z'`).
- **Context:** declared persisted instance variables only. Copy shared or contextual data into an instance variable through existing workflow mechanisms before activating the timer when needed.
- **Errors:** scheduling is required for correct process execution, so invalid results fail activation. Do not reuse the optional user-task due-date behavior that logs a warning and leaves a value empty. A synchronous failure rolls back, so only the caller's `400` and a value-free warning log record it; documentation steers authors to validate inputs where they enter. A durable record of synchronous failures is a later release.
- **Calendars:** support expression durations with an existing literal calendar name immediately.
- **Validation:** full NCalc checks on the server; bounded local checks and explicit limitations in the editor.
- **Storage:** retain the authored expression and existing due-time fields; retain the 128-character limit. No new table, column, or migration.
- **Reactivation:** the preview evaluates a target's expression timer boundaries, as it already evaluates conditional boundaries. An unresolvable boundary makes the target ineligible; an already-due boundary adds a warning.
- **Recovery:** a committed due time cannot be changed in this release. Correct data before activation; afterwards, use the existing administrative actions or cancel the instance (see [section 5](#5-persistence-worker-behavior-and-version-changes)).
- **Already-due boundaries:** keep fixed-timer semantics. The host still starts, so a staged service task's HTTP call may run before the timer interrupts it. Document this rather than change it in this release.

## Plan

Written on 2026-10-28, following [Writing a plan](README.md#writing-a-plan), and revised the same day after a review against the code. Implementation started the same day; [Current state](#current-state) records what is done. Effort remains **M**: the existing timer lifecycle and persistence are reused, but engine, validation, editor, and durable acceptance coverage must change together.

### 1. Scope and execution contract

The feature applies to one-shot dates and durations on intermediate timer catches and both interrupting and non-interrupting timer boundaries. All currently supported boundary hosts remain supported, including multi-instance user tasks and durable automatic activities. A boundary on a multi-instance task belongs to the existing host-token activation, not separately to each child work item.

| Situation | Planned behavior |
| --- | --- |
| Fixed `timeDate`, `timeDuration`, or `timeCycle` | Existing parsing, scheduling, and recurrence behavior. |
| Expression `timeDate` or `timeDuration` on a catch/boundary | Evaluate against the activation's persisted instance values, validate the result, and persist one due time. |
| Expression on a timer start or in `timeCycle` | Reject during definition validation; no fallback to a different schedule type. |
| Due date at or before activation time | Persist that instant; queue the existing timer job for the Worker. Do not route inline, so the host still starts; see already-due boundaries in [section 4](#4-runtime-activation-transactions-and-failures). |
| Variable/calendar edited after activation | Keep the saved due time. No watcher, rescheduling job, or dependency trigger; see correcting a schedule in [section 5](#5-persistence-worker-behavior-and-version-changes). |
| Host revisited with a new activation ID | Evaluate again using that activation's current values and time. |
| Reactivation of a completed or cancelled instance | The preview evaluates the target's expression boundaries; the commit evaluates them again at activation. |
| Existing subscription revisited during recovery | Keep its saved schedule, including terminal one-shot subscriptions that already own the activation identity. |
| Activation transaction rolls back | Nothing has been scheduled durably. A retry may evaluate again and use a new activation timestamp. |

"Evaluated once" means once for the committed schedule, not that a database rollback prevents another attempt. Worker delivery can occur later than the due time; there is no new exact-delivery-time guarantee.

### 2. Workflow JSON, grammar, and compatibility

Keep the existing `timer` object and exactly-one-schedule rule. Do not introduce `timeDateExpression`, `timeDurationExpression`, a mode discriminator, or a document schema-version change.

This page calls the text of the selected property its *schedule value*: a fixed value or an NCalc *expression*. Existing names predate that distinction: `ScheduleExpression`, `MaxExpressionLength`, and `CountConfiguredExpressions` in code, the editor's **Timer expression** selector, and the node reference's statement that "every expression is limited to 128 characters". Keep the code names, but change user-facing text so that "expression" means NCalc only.

| Property | Planned accepted form |
| --- | --- |
| `timeDate` | Existing fixed offset date-time, or NCalc returning an offset date-time string. |
| `timeDuration` | Existing fixed duration, or NCalc returning a positive supported duration string. |
| `timeCycle` | Existing fixed `R/duration` or `R<count>/duration` only. |
| `calendar` | Existing literal calendar name, allowed only with `timeDuration`; never evaluated. |

Interpretation order:

1. Trim outer whitespace and enforce at most 128 UTF-16 code units, matching the current C# and JavaScript length convention. The wrapper, if present, counts toward this bound. Exactly one schedule member must remain nonblank.
2. Try the selected property's fixed-value parser first. Preserve accepted literal formats and existing fixed-date UTC normalization.
3. Reject unmistakably malformed fixed values and fixed values for the wrong property. Generalize UI-04's rule (`UserTaskDueRules.IntendedFixedValuePattern` and the editor's `USER_TASK_INTENDED_FIXED_DUE_VALUE`) rather than writing a second one; it already catches text such as `p2d`, `P1Y`, `P1M`, `PT0S`, offset-free timestamps, and date-only strings (NCalc evaluates `2026-11-01` as the number 2014). Timers add cases a due date does not need: a valid literal for the other property, recurrence text in a date/duration field (NCalc reads `R/P1D` as `R / P1D`), and negative durations (NCalc reads `-P1D` as minus a variable). Due-date classification must not change. Do not assume that every failed literal parse is a valid expression.
4. Otherwise treat the whole value as NCalc, stripping at most the existing optional `${...}` wrapper for parsing/evaluation. No substitution inside surrounding text, nested templates, or second evaluation of a returned string. Elsewhere in Flowbit, `${...}` inserts text into service URLs and variable defaults, so give a value that contains `${` other than as the whole-value wrapper, such as `P${days}D`, a specific error saying that schedules do not insert text, instead of NCalc's generic parse error. The API and the editor use the same message.
5. A quoted string expression, as `UserTaskDueRules.QuotedLiteral` recognizes one, is valid only if its contents pass the selected schedule parser. Expressions that merely look like fixed values can use an explicit parameter reference, such as `[PT1H]`, to name a variable instead of specifying a duration.

Retain authored expression spelling after outer trimming, including any wrapper; do not rewrite expressions or their string literals. Define classification fixtures shared by the C# and editor tests so malformed literals and valid bracketed/wrapped references do not drift apart.

These are node fragments, not complete runnable workflow definitions. `contractEnd` must be declared as stored `datetime` or string data whose value has an explicit offset, and `urgent` as stored Boolean data.

```json
{
  "id": 3,
  "type": "intermediateTimerCatchEvent",
  "name": "Wait until contract end",
  "timer": { "timeDate": "${contractEnd}" }
}
```

```json
{
  "id": 4,
  "type": "timerBoundaryEvent",
  "name": "Review deadline",
  "attachedToRef": 2,
  "cancelActivity": true,
  "timer": {
    "timeDuration": "if(urgent, 'PT4H', 'P2D')",
    "calendar": "examples.weekdays"
  }
}
```

Result rules are property-specific:

- Accept only a string; trim it and enforce the same 128-character bound before parsing. Do not stringify numbers, Booleans, arrays, objects, or other result types. NCalc's `#...#` date literals do not parse with Flowbit's options, so no native date result arises.
- `timeDate` accepts the current ISO grammar with `Z` or an explicit numeric offset and persists UTC. It does not accept a bare `date`, local timestamp, or duration.
- Stored `datetime` values are checked more loosely than this grammar. `TypedOutputValueValidator` accepts any text containing `T` that `TryGetDateTimeOffset` parses, so `2026-11-01T10:00:00`, `2026-11-01T10:00`, values with more than seven fractional digits, and `+02` offsets are valid variables that fail activation. Flowbit.Ui's typed date-time controls always send an offset; HTTP callers, mapped service/message outputs, and scripts may not. Keep one grammar: widening it for results alone would make results and fixed values disagree, and an offset-free value has no defined time zone. Document the requirement, as UI-04 does for due dates, and show a `validation` rule on the source input that requires an offset (for example with `IsMatch`), so bad data is rejected where it enters.
- `timeDuration` accepts the existing fixed-unit grammar and must resolve to a positive representable duration. It does not accept an absolute date or numeric seconds. Months and years remain unsupported.
- With `calendar`, additionally enforce current working-duration restrictions: whole days, no weeks or fractional days, and the existing search/range limit. The calendar does not change an absolute-date rule.
- Null, blank, an unavailable evaluated parameter, malformed text, overflow, or evaluation failure is an activation error. Declaration alone does not guarantee that a producer has supplied a value before this activation. Normal NCalc lazy branch evaluation remains intact; do not require unused branches to produce a value.
- String operations follow NCalc: two strings concatenate (`endDate + 'T17:00:00Z'` turns a `date` input into a valid `timeDate`), but a string plus a number fails (`'P' + days + 'D'` throws). A duration from a numeric input therefore needs a JavaScript script task that writes a duration string to a process variable first. Document both patterns.

Update comments/rules in [WorkflowModel.cs](../../Flowbit/src/Flowbit.Shared/Models/WorkflowModel.cs). [WorkflowModelMigrator.cs](../../Flowbit/src/Flowbit.Shared/Models/WorkflowModelMigrator.cs) already trims timer members and rewrites only values that parse as fixed dates, so it preserves expression text; pin that with round-trip tests rather than changing it. Prove old JSON still round-trips; never convert an expression into a fixed value during save, load, or migration. `job.retryDelays` and user-task due-date rules keep their own existing contracts.

### 3. Definition validation and expression analysis

Add a focused timer-expression analyzer in the service layer. Keep `TimerDefinitionRules` responsible for literal/result shape and classification; it must remain usable without adding NCalc to the shared model or browser. Move the classification and quoted-literal helpers that UI-04 put on `UserTaskDueRules` into shared timer-grammar helpers that both use.

In [WorkflowDefinitionValidator.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowDefinitionValidator.cs):

- Extend `ValidateTimerDefinition` to distinguish literal schedules from eligible expressions, with node-specific rejection of expressions on starts and cycles.
- Parse using the same NCalc options and wrapper normalization as runtime. `IsValid` alone is insufficient: inspect `GetParameterNames()` and `GetFunctionNames()` from the parsed AST.
- Reuse the declared persisted-variable vocabulary from `ConditionalEventDefinitionAnalyzer`: process, node, and sequence-flow variables, plus validated service/message output targets, status/error targets, and entry idempotency targets. Reject undeclared references; references otherwise resolve case-insensitively. Reject ambiguous declaration casing only for names the expression references. The conditional analyzer's `BuildCanonicalVariableMap` rejects it anywhere in the definition, which would make unrelated declarations fail as soon as one expression timer is added. Run the analysis only for values classified as expressions, so definitions with only fixed timers validate exactly as today.
- Reject shared aliases before accepting those names as stored dependencies. Reject `sys.*`, `config.*`, `setting.*`, `mi.*`, `gateway.*`, `FlowInfo`, and gateway/multi-instance aggregate helpers. Do not pass runtime context to compensate for a missing stored value.
- Permit the evaluator's ordinary NCalc built-ins and registered string/collection helpers, using the existing conditional analyzer's function-list pattern. Unknown functions are definition errors. Parsing, function checks, and runtime must agree on options; do not add new date functions for this item.
- Allow expressions with zero variable dependencies, such as `'PT30S'`. Do not inherit the conditional analyzer's minimum-one-dependency rule or create an inverse dependency index.
- Validate fixed and directly quoted values, obvious nonstring constants, and constant calendar-duration violations at authoring time. Do not claim to statically infer every conditional result type; dynamic branches are checked when used.
- Keep existing boundary-host, topology, durable-publication, and calendar-readiness gates. Extend the existing FlowInfo/gateway usage checks to timer expression fields without changing unrelated validation order.

The analyzer should return classification and referenced variable names for runtime use. Prefer a small shared declaration-enumeration helper if needed; do not refactor the conditional event subsystem as part of this feature. Validate again defensively at runtime boundaries so a cached/legacy definition cannot bypass the restricted context.

Use the same definition validation through normal save/publication and AI authoring checks. Verify AI errors identify the node, JSON property path, existing diagnostic code, and cause. Update diagnostic path mapping if necessary; do not alter AI instructions, tool names, tool descriptions, or introduce a new tool.

### 4. Runtime activation, transactions, and failures

Extend `EnsureTimerCatchWaitAsync`, `EnsureAttachedTimerBoundaryWaitsAsync`, and `ResolveTimerScheduleAsync` in [WorkflowEngineService.Jobs.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.Jobs.cs), together with their callers in the engine's pass-through and durable paths.

1. Obtain the current transaction's instance-variable overlay under the established instance/token/work locks. Synchronous traversal already has `storedOverlay`; pass its stored instance values, including earlier accepted writes in that transaction. Do not independently reload an older snapshot or pass a map containing shared/context overlays.
2. In staged service/script job paths, read the latest instance variables (`LoadLatestVariableVersionsAsync`) before activating attached boundaries; these paths currently activate boundaries first. Move only that read. `MergeEffectiveValuesAsync` can lock shared rows, so it stays after boundary activation; timer expressions never need shared values. Keep the read within the same transaction and reuse it for later staging instead of adding a read per boundary.
3. Capture one `TimeProvider.GetUtcNow()` instant for the logical activation being scheduled. Reuse it for the catch and its newly created attached timers, or for a host's set of boundaries, so scheduling work between boundaries does not change their duration bases.
4. Evaluate each new expression with `SequenceFlowConditionEvaluator.EvaluateValue(..., preserveComplexTypes: true)` over the allowed stored snapshot. JSON `datetime` values remain strings, while objects and arrays cannot accidentally pass as text.
5. Validate the result and construct a temporary resolved `TimerDefinitionModel`. Feed it and the captured instant into the existing `WorkflowTimerSchedule.Resolve`, loading an existing calendar through the scoped resolver when named. Never mutate the cached authored model or replace its expression with the result.
6. Persist the subscription, job, and wait state in the existing transaction. If any timer in an activation set fails, roll back the whole transition, including any subscriptions/jobs already inserted in that transaction.

A boundary sees values available when its host activates. It may see outputs of an earlier completed activity in the same transition, but cannot depend on the future output of the service/script activity it is guarding. An `asyncBefore` or staged automatic host must use its existing activation point; do not move the timeout to after its external call or body. Multi-instance, parallel, message-delivery, and loop re-entry paths must reach the same resolution helper.

Conditional boundaries are entered before timers (`EnsureAttachedConditionalBoundarySubscriptionsAsync` in [WorkflowEngineService.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.cs)). When an initially true interrupting conditional boundary captures the host, its timer catch and boundaries are never evaluated, so an unusable timer expression cannot fail that transition.

Already-due boundaries keep fixed-timer semantics because they are queued, not routed inline. A user task's work item is created and then interrupted when the Worker runs. A staged service or script commits its boundaries in the staging transaction before its body runs, so a service task's HTTP call may run while the timer interrupts the host; the fenced late result is discarded, but the external side effect has happened. A very short fixed duration behaves this way today; dates from data make it common. Document it in the node reference and pin it with a test. Capturing an already-due interrupting boundary at activation is a later release: doing it only for expressions would make them differ from fixed timers, and doing it for both would change existing behavior.

Reactivation commits route through `ResolvePassThroughAsync`, so they evaluate the target task's timer boundaries, but the preview checks only conditional boundaries (`EvaluateReactivationConditionalBoundaries`). Extend the preview with the same resolver, the current variables, and the preview time:

- A boundary that cannot be resolved makes its target ineligible with blocker code `timer_boundary_unresolvable`, reported like `conditional_boundary_true`.
- A boundary due at or before the preview time adds a `timer_boundary_already_due` warning naming the target and boundary. An absolute date from an old instance's data is usually past, and an interrupting boundary would end the reactivated task as soon as the Worker runs. Give fixed `timeDate` boundaries the same warning; it adds information without changing their behavior.
- The commit repeats the assessment, so it refuses a target the preview refused with `409`. A failure during activation itself returns the normal `400`. Either way nothing changes.

Failure handling:

- Return a bounded domain diagnostic naming the timer node ID, schedule property, and cause, for example `Timer boundary event #4 timer.timeDuration must resolve to a positive supported ISO-8601 duration string.` Do not include a resolved value or dump the variable map.
- Catch every exception from evaluation and schedule arithmetic except cancellation, as `TryEvaluateUserTaskValue` does. `EvaluateValue` wraps only NCalc, format, and invalid-operation exceptions: `n % 0` throws `DivideByZeroException`, and adding an out-of-range duration throws `ArgumentOutOfRangeException`. Unwrapped, they give a `500` on synchronous paths and, in the Worker, bypass the retry/incident handling for the dispatcher's unhandled-failure recovery.
- Never copy an inner message into the diagnostic or a log. NCalc format errors contain variable values (`s * 2` gives `The input string '<value>' was not in a correct format.`), `EvaluateValue` copies that text into its exception, and the Worker stores exception messages in job failures and incidents. `WorkflowTimerSchedule.Resolve` messages contain the schedule text, which for the temporary resolved model is the evaluated value. Log the exception type and cause, not the message.
- Throw only `WorkflowDomainException`. The Worker completes a job that throws `WorkflowConflictException` as stale, which would silently drop the wait, and a `WorkflowJobInvariantException` opens an invariant incident without retrying.
- Write one value-free warning log per failure, naming the instance, token, node, property, and cause. A synchronous failure rolls back and leaves no incident or history row, and its `400` reaches whoever caused the transition (the actor completing the previous task, a message sender, or the starter), who usually cannot correct the data.
- Preserve transaction rollback for synchronous API transitions and the existing durable job failure/retry/incident path for Worker execution. Do not create a second incident mechanism, silently skip the timer, or create a wait without its required job.
- A timer-expression failure is a scheduling failure, not automatically a service/script business error. Preserve existing failure classification and test attached error-boundary interactions so only the intended failures are caught.
- An incident retry before successful activation can evaluate corrected instance data. A retry after a timer subscription committed must use the saved schedule. Document this distinction for recovery.

No new shared-variable read locks are introduced. Keep the existing lock order and do not hold shared locks over an external call. Shared inputs remain rejected unless a later change extends `SharedVariableAccessPlanner` and its readiness proof explicitly.

### 5. Persistence, Worker behavior, and version changes

No database migration is planned. Keep the roles of the existing fields explicit:

| Field | Meaning for an expression timer |
| --- | --- |
| `ScheduleKind` | Existing `timeDate` or `timeDuration`; no new job/schedule kind. |
| `ScheduleExpression` | Original authored schedule text after outer trimming, never the evaluated value. |
| `ScheduleCalendar` | Literal calendar name, when a duration uses one. |
| `NextDueAt` | Resolved UTC due time for the subscription's occurrence. |
| Job `ScheduledOccurrenceAt` | Original scheduled occurrence, retained for identity/misfire/fencing semantics. |
| Job `DueAt` | Initial due time, which the existing retry machinery may subsequently move for another attempt. |

`BuildTimerSubscription` must receive the authored node; only the arithmetic resolver receives the temporary resolved timer. Keep activation IDs, occurrence numbers, unique constraints, lease fences, and duplicate-delivery protections unchanged. Do not persist a second expression result, variable snapshot, or calendar revision solely for this feature.

Worker firing, restart recovery, incident retry of an already scheduled timer, and administrative timer-boundary execution must not call the expression resolver. Preserve the existing terminal-subscription guard in `EnsureAttachedTimerBoundaryWaitsAsync`; a completed one-shot boundary must not be recreated on manual retry. One-shot non-interrupting boundaries create one sibling continuation per activation; only unchanged fixed `timeCycle` timers recur.

**Correcting a schedule.** Before a schedule commits, correct the data (for example with an administrative variable update) and retry the user action, message delivery, or incident. After it commits, the saved due time stands in this release: variable edits, calendar edits, and compatible version switches keep it, and recovery is limited to existing operations. For a timer boundary on a user task, an administrator can take the boundary action now, which always interrupts the host, or move the task on with a direct-flow action. A timer catch, or a boundary on another kind of host, can only wait or have its instance cancelled. Document these options in the UI and deployment guides. An action that reschedules or fires an open timer is a later release.

In [WorkflowVersionCompatibilityEvaluator.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowVersionCompatibilityEvaluator.cs), retain `ValidateOpenTimers`, `TimerDescriptorMatches`, `TimerNodeContract`, attached-boundary checks, and open-job checks as the authority:

- A switch may keep an open timer only when its authored node/flow contract and persisted schedule descriptor remain compatible. Preserve current calendar comparison and canonicalization rules.
- Changing expression text, literal versus expression, schedule kind, calendar, attachment, interruption mode, or required continuation contract blocks an affected open timer. Compare configuration, not the result of evaluating target values.
- Keep conservative authored-text equality: `slaDuration` and `${slaDuration}` can be semantically equivalent but still change an active timer contract. Outer trimming remains the existing normalization; do not invent expression equivalence. Conditional events differ deliberately: `WorkflowModelMigrator` strips their wrapper and their contracts compare the normalized condition. Say so in a code comment so the two are not aligned by accident.
- Compatible switches retain resolved due times and occurrence identity. Existing variable-contract compatibility checks still apply. Future activations use the target version normally.
- Test both preview and committed switch paths, including pending jobs and attached boundaries. Keep unrelated cosmetic compatibility and ordinary fixed timers unchanged.

### 6. HTTP API, authorization, and AI authoring

No route, role, request DTO property, response DTO property, or timer-specific permission is added. The behavior change is in workflow-definition JSON and transition validation, plus two issue codes in the existing [reactivation preview](../api-guide.md#get-apiinstancesidreactivation) lists: `timer_boundary_unresolvable` (blocker) and `timer_boundary_already_due` (warning).

- Definition save validates expression syntax/references without requiring sample runtime values. Existing publication/default-version readiness checks still enforce durable processing and calendar availability.
- Invalid definitions and synchronous activation failures use the existing `WorkflowDomainException` mapping to HTTP `400` with `{ "error": "..." }`. Preserve existing authentication, authorization, not-found, and conflict behavior; do not introduce a parallel error envelope.
- Jobs/incidents and administrative timer-boundary responses retain their existing fields and authorization. No response exposes `ScheduleExpression`; job `DueAt` and administrative timer `NextDueAt` describe the resolved schedule. Instance detail shows a timer wait without its due time.
- The model-derived authoring schema keeps the same JSON shape. Update descriptions and capability guidance where they still say fixed values only, and prove the generated authoring package and AI checks accept the same valid definitions as normal saves.
- AI `validate_item` cannot establish workflow-wide declarations without its surrounding workflow; preserve its existing limits and let complete-workflow validation perform dependency checks. Do not report unperformed checks as passed.
- Use mocked provider HTTP for all AI tests. No billed model run is needed.

### 7. Editor and Flowbit.Ui

Change only the canonical assets under [the shared editor folder](../../Flowbit/src/Flowbit.Ui/wwwroot/editor/). Hosted and standalone editors must share the same serialization and local validation.

- Keep the existing timer schedule selector, but rename its **Timer expression** label (for example to **Schedule**) and reword the malformed-configuration note that asks the user to choose a timer expression; next to an **Expression** input choice, the old wording is ambiguous. Add a **Fixed value / Expression** input choice for catch/boundary dates and durations. Fixed input retains the current date/time and friendly duration controls in `renderAbsoluteTimerEditor` and `renderDurationEditor`; expression input uses a labelled text field with examples such as `${contractEnd}` and `if(urgent, 'PT4H', 'P2D')` and the 128-character limit. The fixed controls' **Advanced ISO** fields accept any text, and text there that classifies as an expression reloads in Expression mode, so show the classification's message there rather than only the fixed-format hint.
- This input choice is UI state only, inferred from the authored value on load; it adds no JSON discriminator. Update `renderTimerSection` so selecting or rendering a node never rewrites an expression through a fixed-value control. An explicit value edit replaces the schedule; changing input mode must not silently substitute a default or lose invalid imported text. Preserve the calendar when switching duration input mode, and retain existing calendar cleanup when changing schedule kind.
- Link the expression field to its help/error text, expose invalid state accessibly, and support keyboard selection/editing without interfering with editor shortcuts.
- Show a concise hint: values are evaluated at activation and saved, and a `datetime` input needs an explicit offset; full expression syntax and variable-reference validation occurs when saving through the API. In standalone mode, make the API-validation limitation explicit without claiming a successful export proves the expression valid.
- Retain the **Business calendar** field for duration expressions. Its live validation checks the name and schedule combination immediately; a dynamic duration's working-time validity is checked on activation.
- Preserve expression text through `normalizeTimerDefinition`, import/load, export canonicalization, inspector edits, and type changes. Normalization already keeps text it cannot parse (`normalizeTimerTimeDate` returns it unchanged), so that part needs round-trip tests, not new code. A switch to `timerStartEvent` must not silently erase or turn an expression into a literal: retain it as invalid until corrected. Changing to a nontimer type follows existing timer cleanup behavior.
- Extend the async/job/timer save-validation phase with fixed/expression classification and bounds, generalizing the editor's UI-04 helpers (`USER_TASK_INTENDED_FIXED_DUE_VALUE`, `quotedUserTaskLiteral`) as the server generalizes `UserTaskDueRules`, and report embedded `${` with the server's message. Include timer fields in existing forbidden FlowInfo/gateway scans and conservative known-shared/context checks.
- Existing `declaredVariablesReferencedByExpression` helpers recognize known names; they cannot discover every unknown name or validate NCalc grammar. Use them only for bounded checks/advisory dependencies. Do not present regex scanning as a full parser or reject valid syntax based on uncertain scanning.
- Keep existing ordered errors for unchanged fixed models and add explicit characterization cases for new validation branches. Share the live-field and save-validation problem helpers.
- Verify hosted API validation leaves the edited document intact and reports the server's node/property error. Preserve undo/redo, dirty state, saved baseline, load failure behavior, and direct-file operation. No new Flowbit.Ui operational screen is needed.

### 8. Implementation sequence and review gates

| Step | Work | Exit condition |
| --- | --- | --- |
| 1. Contract and characterization | Generalize UI-04's classification helpers; add literal/expression classification fixtures, result rules (including stored `datetime` forms), old-model round trips, and negative start/cycle cases; decide each changed message for a pinned invalid literal. | The grammar, limits, and backward compatibility are pinned before runtime changes, and due-date results are unchanged. |
| 2. Server validation | Implement the timer analyzer, declared-variable/function checks, calendar composition, and AI diagnostic integration. | Invalid definitions fail before persistence; valid dynamic definitions save without sample values. |
| 3. Activation resolution | Thread stored snapshots/time through synchronous and staged paths; resolve temporary timer values; normalize failures and log them without values; extend the reactivation preview. | Catch/boundary schedules persist atomically from the correct activation snapshot, and the reactivation preview agrees with commit. |
| 4. Durability and compatibility | Exercise retries/restarts, completed subscriptions, administrative override, calendar edits, and version-switch guards. | Saved schedules never reevaluate; occurrence and activation fences remain intact. |
| 5. Editor and examples | Update shared fields/validation/round trips and add canonical examples using real start inputs. | Both editor hosts author and reload expressions; server errors preserve drafts. |
| 6. Documentation and release verification | Update owning guides, run automated suites and browser scenarios, and review evidence. | All first-release criteria are met; only then mark this page and backlog Done. |

Keep each implementation slice reviewable, but ship expression authoring only with the matching runtime and validators. Do not publish examples advertising usable expression timers while any deployed API or Worker still lacks support.

### 9. Tests and verification

Extend the existing suites rather than replacing their fixed-timer coverage. New test-class names below are proposed, not existing files.

| Area | Required cases |
| --- | --- |
| Shared rules and schedule arithmetic | Fixed values unchanged; raw/wrapped/bracketed expressions; outer whitespace; quoted literals; wrong-property literal; `R/P1D` and `-P1D`; malformed wrapper and embedded `${`; 128/129 limits; non-BMP length parity; offset/UTC dates; date-only/local-time rejection; stored `datetime` forms the type accepts but the grammar rejects (no offset, minutes only, more than seven fractional digits, `+02`); zero/negative/overflow duration; null/blank/number/Boolean/array/object results; string concatenation versus string plus number; no recursive evaluation; due-date classification unchanged. |
| Definition analyzer | Declared process/node/flow/producer names; case-insensitive references; ambiguous casing rejected only for referenced names; fixed-only definitions skip the analysis; unknown parameters/functions; shared/context/FlowInfo/gateway/MI rejection; zero-dependency expressions; lazy conditional branches; no acceptance of expressions on starts or cycles; pinned invalid-literal messages, such as `P١D` in `DefinitionValidationTests` (NCalc rejects it as a parse error), change only by decision. |
| Calendar composition | Same result as a fixed working duration; weekend/holiday and daylight-saving cases using existing calendar fixtures; missing calendar; forbidden weeks/fractional days; uncountable/overflow result; calendar edits affect only later activations. |
| Activation and snapshots | Catch and both boundary modes; boundary attached to a timer catch; normal and MI hosts; same-transition script/flow/message writes visible; future host output unavailable; staged service/script pre-body snapshot; shared/common activation instant for a wait set; an initially true interrupting conditional boundary on the same host skips timer evaluation; an already-due interrupting boundary on a staged service task (the call may run, its late result is discarded, and the boundary path is taken). |
| Atomic failure and recovery | A failing second boundary rolls back the first subscription/job and triggering writes; synchronous HTTP failure; durable retry/exhaustion/incident and repaired-value retry; error-boundary classification; `DivideByZeroException` and value-bearing format errors give the bounded diagnostic; no expression result, variable value, or inner message in diagnostics, job failures, incidents, or logs; one warning log per failure; Worker failures retry or open incidents and are never completed as stale. |
| Durable persistence | Authored expression retained; exact UTC `NextDueAt`/initial job times; variable edit then Worker restart; delivery retry preserves `ScheduledOccurrenceAt`; new activation reevaluates; terminal boundary not recreated; cancellation, duplicate delivery, lease recovery, and administrative override retain fences. |
| Version changes | Identical contract allowed with saved times preserved; different expressions producing the same instant still blocked; wrapper/literal changes; calendar/attachment/cancel/outgoing changes; open jobs; completed versus open activations; preview and apply agree. |
| Reactivation | An unresolvable expression boundary makes its target ineligible; already-due expression and fixed `timeDate` boundaries warn; future boundaries add nothing; preview and commit agree; a failing commit changes nothing. |
| Model/editor/AI | Legacy round trips; raw/wrapped values survive import/export/undo/redo/type changes; fixed error arrays unchanged; local validation limitations stated; API rejects what local checks cannot prove; generated package/schema and full-workflow AI validation agree. |

Relevant existing tests include [WorkflowTimerScheduleTests](../../Flowbit/tests/Flowbit.Tests/WorkflowTimerScheduleTests.cs), [DurableWorkflowProcessorTests](../../Flowbit/tests/Flowbit.Tests/DurableWorkflowProcessorTests.cs), [BusinessCalendarRuntimeTests](../../Flowbit/tests/Flowbit.Tests/BusinessCalendarRuntimeTests.cs), [WorkflowVersionCompatibilityEvaluatorTests](../../Flowbit/tests/Flowbit.Tests/WorkflowVersionCompatibilityEvaluatorTests.cs), [InstanceVersionChangeApiTests](../../Flowbit/tests/Flowbit.Tests/InstanceVersionChangeApiTests.cs), [AdministrativeTimerBoundaryOverrideApiTests](../../Flowbit/tests/Flowbit.Tests/AdministrativeTimerBoundaryOverrideApiTests.cs), [EditorValidatorCharacterizationTests](../../Flowbit/tests/Flowbit.Tests/EditorValidatorCharacterizationTests.cs), and [ExampleWorkflowCatalogTests](../../Flowbit/tests/Flowbit.Tests/ExampleWorkflowCatalogTests.cs). Add focused `TimerExpressionRulesTests`, `TimerExpressionDefinitionTests`, and `ExpressionTimerRuntimeTests` if that keeps concerns clearer.

Use an injected `TimeProvider` for deterministic unit/integration calculations. Use real PostgreSQL for transaction rollback, schedule persistence, concurrency, administrative actions, version switches, and reactivation; in-memory fakes cannot establish these guarantees. Job leasing compares `DueAt` with the database clock (`clock_timestamp()`) while the engine computes due times from the host clock, so tests with near-term or already-due timers poll with a bounded timeout and never assert immediate eligibility.

For implementation verification, run from the repository root (the command is identical in PowerShell and Bash):

```text
dotnet test Flowbit/tests/Flowbit.Tests/Flowbit.Tests.csproj
```

Then publish fresh hosts and build/install Chromium using the [browser smoke setup](../../Flowbit/tests/Flowbit.BrowserTests/README.md#commands) and the [Worker-enabled acceptance setup](../../Flowbit/tests/Flowbit.BrowserAcceptanceTests/README.md). After those prerequisites, run these commands in PowerShell or Bash:

```text
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore
dotnet test Flowbit/tests/Flowbit.BrowserAcceptanceTests/Flowbit.BrowserAcceptanceTests.csproj -c Release --no-build --no-restore
git diff --check
```

Browser work must include:

- **Standalone and hosted editor, 1440×900 and 1024×768:** select timer nodes, type fixed/raw/wrapped schedules, choose calendar durations, inspect live errors, exercise keyboard undo/redo, change schedule/node types, save and reload, and confirm malformed server-only expressions retain the draft after API rejection. Exercise native save-picker success/cancel manually where supported.
- **Hosted layout, 390×844:** inspect the timer fields/help/error messages and drawer behavior for clipping or inaccessible controls.
- **Durable runtime, 1440×900 and 390×844:** start examples through real UI/HTTP inputs, see the timer wait on instance detail and its resolved due time on **Operations** (and, for a boundary on a user task, **Administrative actions**), update a source variable through the supported API, restart the fixture-owned Worker, and observe the original schedule's continuation. Exercise interrupting/non-interrupting boundaries, incident recovery, and a reactivation preview that warns about an already-due boundary, with bounded polling.
- Keep the ordinary smoke fixture Worker-free. Add firing/restart/recovery cases to `Flowbit.BrowserAcceptanceTests`, using its disposable durable stack. Do not edit database rows, inject UI state, rewrite real responses, or add timer sleeps to the smoke fixture to manufacture success.
- Inspect screenshots and browser console errors/warnings at every relevant viewport. Record browser, actual localhost URLs, interactions, viewport sizes, screenshots, and console results separately from automated test commands/results. State unavailable checks explicitly.

### 10. Documentation and examples to ship with implementation

The implementation updates the following owning pages where the final behavior affects them. The browser suite READMEs change when the browser scenarios are added.

| Owner | Required update |
| --- | --- |
| [Node and property reference](../node-reference.md#timer-configuration) | Allowed nodes, fixed/expression distinction and terminology, wrappers and the difference from `${...}` text insertion, bounds, result grammar, the explicit-offset requirement for `datetime` inputs, concatenation and the script-task pattern for numeric durations, permitted variables, activation semantics including already-due boundaries, and calendar composition. |
| [BPMN support](../bpmn-support.md#timers) | Replace the blanket expression-timer limitation with the precise supported subset and remaining start/cycle/context restrictions. |
| [Developer guide](../developer-guide.md) | HTTP-only definition/start examples, ordering of input/producer values, `validation` rules that keep offset-free `datetime` inputs out, stored-schedule behavior, errors, and version-switch/retry semantics. |
| [API reference](../api-guide.md) | Workflow JSON description, domain-error examples, the two reactivation preview issue codes, and interpretation of existing operational timer timestamps; no invented route or DTO fields. |
| [Flowbit.Ui guide](../ui-guide.md) | Timer inspector examples and the renamed schedule selector, calendar use, API validation versus standalone checks, where due times appear, reactivation preview warnings, and recovery using existing jobs/incidents pages and administrative actions. |
| [Deployment and operations](../deployment.md) | Coordinated API/UI/Worker upgrade, Worker requirement, scheduling failures and their warning log, recovery options, restart behavior, and rollback constraints. |
| [Database schema reference](../database-schema.md) | Clarify authored `ScheduleExpression` versus resolved `NextDueAt` and job occurrence/retry times; no object-count or migration change. |
| [Runtime reference](../../Flowbit/README.md) and [AGENTS.md](../../AGENTS.md) | Activation snapshot, immutable definition, failure, persistence, reactivation preview, and compatibility invariants. |
| [Authoring guide](../../authoring/flowbit-authoring/references/authoring-guide.md) and generated package | Supported expression context and examples, with the offset requirement already stated for due dates; keep AI guidance consistent with authoritative validation. |
| [Example catalog](../../examples/README.md) and `examples/timers/` | Add complete workflows for an input-backed absolute catch whose `datetime` input has an offset-checking `validation` rule, a conditional duration boundary, and a calendar-backed duration, with roles/inputs, Worker prerequisite, expected outcomes, and safe near-term test inputs. |
| Browser suite READMEs | New editor and durable acceptance scenarios, viewport coverage, and evidence locations. |
| [Enhancement backlog](README.md) and this page | Record implementation evidence and later-release work when done; keep statuses synchronized. |

Review the [root README](../../README.md) for capability statements or screenshots made inaccurate by the implementation, updating only affected material. Validate changed relative links/anchors and all full example JSONs through the existing example catalog tests. The node fragments above require structural JSON validation only; the eventual runnable examples require full definition and runtime verification.

### 11. Rollout and completion checklist

- [x] Confirm the final implementation still fits existing columns and needs no migration. If expression limits/storage change, revise this plan and add migration-first deployment instructions before shipping.
- [x] Existing fixed definitions and already scheduled jobs require no rewrite or backfill. Verify this on a database containing representative fixed timer subscriptions.
- [x] Complete the validation matrix, browser evidence, owning documentation, and canonical examples. Record commands/results and limitations with the implementation change.
- [x] Mark the item Done only after the release criteria pass. The work under [Later releases](#later-releases) is not yet filed as separate backlog items; file each piece that is still wanted under the backlog's completion rules.

Deployment and rollback:

- Deploy compatible API, UI/editor assets, and Worker binaries before saving or publishing expression-timer definitions. Mixed old/new execution is unsupported: an older runtime interprets the new text as an invalid fixed schedule.
- Before rolling binaries back, inventory and drain, cancel, or otherwise remove all exposure to expression-bearing definitions and future activations through supported operations. Saved one-shot timestamps alone do not make an older runtime safe for the rest of the definition. Do not rewrite persisted timer rows manually.
