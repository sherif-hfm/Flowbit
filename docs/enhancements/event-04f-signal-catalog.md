# EVENT-04F: Signal catalog

[Parent EVENT-04](event-04-signal-events.md) · [Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Done |
| Category | New events |
| Effort | M–L |
| Priority | Not ranked; sixth of eleven in the [EVENT-04 follow-up order](README.md#event-04-follow-up-subtasks) |
| Parent | [EVENT-04](event-04-signal-events.md) |
| Depends on | [EVENT-04](event-04-signal-events.md) (Done) |
| Related | [EVENT-04B](event-04b-signal-throw-and-end-events.md), [EVENT-04H](event-04h-expression-signal-names.md), [EVENT-04K](event-04k-rename-managed-api-clients.md) |

## Summary

A catalog of signal definitions. Each entry has a name, a description, a declared payload contract, and the roles allowed to throw it. Saved definitions and throws are checked against it.

## Why it adds value

- Today any name can be awaited and thrown. A mistyped name in a definition creates a wait that no broadcast reaches, and saving cannot detect it.
- A payload that does not fit a receiver's mappings fails delivery by delivery, after the throw. A contract rejects it at the throw.
- One setting, `Signals.RequiredRole`, authorizes every signal. With a catalog, a recall can need more authority than a price update.
- The editor and the **Signals** page can offer names instead of free text.

## First release acceptance criteria

These criteria are revised by the [plan](#plan) on 2026-11-24 and by its [review](#plan-review-of-2026-11-24) the same day. The revision names the routes, the page, and the setting, decides how entry roles combine with `Signals.RequiredRole` and managed clients, splits the checks between saving a draft and publishing, makes the check a guarantee for every live version, and makes archiving a refusal rather than a warning.

- [x] Settings administrators create, edit, archive, and reactivate entries through `/api/signal-catalog/entries` and a **Signal catalog** page, with `expectedUpdatedAt` concurrency. An entry has a name under the existing name rule, fixed at creation; a description; up to 100 typed payload fields; throw roles; and two switches that say whether managed API clients and workflows may throw it.
- [x] A throw through the API of a cataloged name checks the payload against the contract inside the throw's transaction, before the open-broadcast count and the freeze. A rejection returns `400`, names the field, and never quotes a value. An archived name cannot be thrown.
- [x] A user needs a role from `Signals.RequiredRole` and, when the entry lists throw roles, one of those as well, to throw or retry a broadcast of that name (`403` otherwise). Cancelling needs the same, except that the thrower of a broadcast may always cancel it. A managed client needs `signals.throw` and an entry that allows clients. Reading broadcasts is not narrowed.
- [x] Saving a definition, drafts included, refuses archived names, payload members and mapping paths that are not fields, and types that do not fit. Publishing also refuses an unsent required field, a receiver that requires an optional field without a default, a throw of a name that workflows may not throw, and, in `required` mode, a name without an entry. The error is the first problem; the AI assistant's proposal lists all of them.
- [x] Creating or changing an entry is refused with `409`, naming the versions, when a published version or a version with a running instance would no longer pass the check, and reactivating an instance on a version that no longer passes it is refused. Every live version therefore fits its entry, and no delivery reads the catalog.
- [x] `Signals.CatalogMode` is `optional` by default: names without an entry keep working, and existing definitions and throws behave as before. `required` refuses publishing a definition and throwing through the API with a name that has no entry; drafts can still be saved, and published definitions keep running.
- [x] Archiving is refused with `409` while a live version uses the name, an instance waits for it, a family starts on it, or a broadcast of it is delivering. A usage read shows each count, and the page lists names in use without an entry.
- [x] The hosted editor suggests cataloged names, shows an entry's description and fields in the **Signal** section, notes a name that the catalog lacks or has archived, and can add an entry's missing fields. The standalone editor works without the catalog, as today. The **Signals** page suggests cataloged names, shows the entry, and says whether the user may throw it. Catalog text is shown as text everywhere, never as markup.
- [x] Documentation, automated tests including PostgreSQL race tests, and real-browser verification of the catalog page, the **Signals** page, and the hosted editor are complete.

## Later releases

- Versioned contracts, so that an incompatible change can ship beside the old contract instead of waiting for the old users to drain.
- Entries limited to workflow families.
- Importing and exporting the catalog.
- Grants for one managed API client, which need a change to the client model; plan them with [EVENT-04K](event-04k-rename-managed-api-clients.md).
- Rules on a field's value (NCalc validation) and declared structure below a `json` field.
- Offering catalog entries to the AI assistant as a selectable block, as shared variables are offered.
- A form generated from the contract on the **Signals** page, in place of the JSON payload box.
- A history of entry changes with the previous contract of each revision.
- An expiry or a scope default stored on an entry.
- Entries for names built by an expression ([EVENT-04H](event-04h-expression-signal-names.md)), such as a prefix entry.

## Current state

Done on 2026-11-25: implemented on 2026-11-24 on top of `57dbfe6`, uncommitted at the time of writing, and verified as below; reviewed and corrected on 2026-11-25 ([review](#review-of-2026-11-25)). Planned the same day: the [plan](#plan) is written, reviewed, and agreed. Before this item there was no catalog: any name could be awaited and thrown, a payload was checked only by each receiver's mappings, and one role setting authorized every signal.

### Implementation of 2026-11-24

- **Contract.** [SignalCatalogEntryRules.cs](../../Flowbit/src/Flowbit.Shared/Models/SignalCatalogEntryRules.cs) (entry rules, type words, field lists, `SignalCatalogModes`), [SignalCatalogDtos.cs](../../Flowbit/src/Flowbit.Shared/Dtos/SignalCatalogDtos.cs), [SignalCatalogContract.cs](../../Flowbit/src/Flowbit.Service/Services/SignalCatalogContract.cs) (payload and definition checks with the three stages), and [SignalCatalogAccess.cs](../../Flowbit/src/Flowbit.Service/Services/SignalCatalogAccess.cs) (who may throw, cancel, or retry).
- **Persistence.** Migration `AddSignalCatalog` (two tables, checks, index, backfill, seeded `Signals.CatalogMode`, a down migration that refuses while entries exist), [SignalCatalogRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/SignalCatalogRepository.cs) with the name lock, and `EnforceSignalCatalogAsync` and `PersistSignalNamesAsync` in `WorkflowDefinitionRepository`.
- **Checks.** Readiness (`FindSignalCatalogProblemsAsync`, `ValidateSignalCatalogAsync`) in saves, publications, and set-default; the AI proposal's save and publication blockers; the reactivation blocker `signal_catalog_mismatch` with the name locks taken before the instance lock.
- **API.** The nine routes in [SignalCatalogEndpoints.cs](../../Flowbit/src/Flowbit.Api/Endpoints/SignalCatalogEndpoints.cs), the `SignalCatalog.Read` policy, and the catalog steps of throw, cancel, and retry in `SignalBroadcastService`.
- **Flowbit.Ui.** The **Signal catalog** page, the **Signals** page's catalog panel, template, review row, and refusal handling, and the hosted editor's `signalCatalog` capability with its block and **Add missing fields**.

Decisions made while implementing, beyond the plan's wording:

- **A catalog `403` has a code.** The body is `{"code":"signal_catalog_forbidden","error":"..."}`, so the **Signals** page tells a catalog refusal from a policy refusal (no body) by its code, never its text.
- **A `json` array mapping reads only an array field.** The plan's table allowed `json` with either array flag; a `json[]` mapping of a scalar field fails at runtime, so it is refused. A `json` mapping still reads any field.
- **Retrying an archived name is refused with `400`**, the throw's message. Cancel ignores the archived status.
- **Usage needs an entry.** `GET /entries/{name}/usage` returns `404` for a name without one, and lists every version that uses the name, drafts included.
- **Unchanged lifecycle requests.** Archiving an archived entry or reactivating an active one returns it unchanged, whatever `expectedUpdatedAt` says; changing an archived entry is `409`.
- **The editor's list reply** reaches about 290 KB at 1,000 entries with 100-character names, not about 150 KB. The editor also asks for a name outside the active list when the name field changes, so it can say "Archived in the signal catalog.".
- **The archive panel** labels the wait count "Open signal waits for it", because it counts waits, boundaries included, not instances.
- **The catalog form** moves focus to the name (new entry) or the description (edit) when it opens, and every field row's controls carry the row number in their accessible names. A problem the form finds before any request clears at the next edit; a server's refusal, such as a stale entry with **Reload latest**, stays until the next request.

### Review of 2026-11-25

A review of the implementation against this plan found one design gap, one unsafe API default, and gaps in tests and documentation. The user asked for all of them to be fixed; each fix below changes the plan's wording where it applied, and the sections that follow are corrected to match.

- **No field holds null.** The plan let an optional field be null, but a receiver's mapping refuses null unless its target variable is nullable, and a signal start's mapping always does. A null that the contract accepted could therefore fail a delivery, the failure the catalog exists to prevent. `PayloadProblem` now refuses a null field, optional ones included (`Signal '...' payload field 'note' is null; leave out an optional field instead of sending null.`), and a publication rule makes an optional payload entry whose variable can be null (`CallSourceDeclaration.MayBeNull`: a nullable process variable, or an optional scalar `json` input or mapping output) set `required`, so a null stops the throw instead. The rule covers `json` fields too, because a `json` mapping into a non-nullable process variable also refuses null. A null inside a `json` value is still allowed.
- **A change states the whole entry.** `PUT /entries/{name}` defaulted a left-out `allowClients` and `allowWorkflows` to true and `throwRoles` and `fields` to empty, so a script that sent only a description lifted the entry's restrictions. The four members are now required (`[property: JsonRequired]`), and a body without one fails binding with `Missing required properties: ...`. Create keeps its defaults.
- **The payload template fits.** **Insert payload template** wrote `null` for a `json` field, which the contract refuses for a required field and now for any field; it writes `{}`.
- **API clients read less.** A managed client now gets `createdBy` and `updatedBy` as null and an empty `signalRoles`: it needs the contract, not the administrators' identities or the user roles a throw needs.
- **Documented, not enforced.** A required mapping that reads part of a field (`skus.0`, `details.lot`) can still fail a delivery when that part is missing, because the inside of a `json` field and the elements of an array are not declared. Refusing such mappings would refuse Example 05's `product.name`, so the guides now name the limit and advise a `defaultValue`.
- **Kept as planned, confirmed by the user.** `SetDefaultAsync` runs the publication stage, so in `required` mode it refuses to make an already-published version with an uncataloged name the default again, which blocks rolling back to it until the name has an entry. The user confirmed on 2026-11-25 that this stays: making a version the default makes its signal starts listen, so it counts as publication.
- **Tests added.** Contract: a null field for each type, the nullable-variable rule for throw and end events, the archived message for all five event types at all three stages, the `required`-mode message for all five, the receiver default rule for catch, boundary, and start, and each archive count alone (`IsUnused`). PostgreSQL, in the new partial `SignalCatalogTests.Lifecycle.cs`: a published version alone and a running instance alone block archiving, which then succeeds; a delivering broadcast alone blocks it until its deliveries end; a stale archive is `409`; a change without each of the four members is `400` and changes nothing. In `Throws`: an optional null field is `400`; retrying an archived name is `400` while another user's cancel succeeds; the workflow throw in `required` mode is now captured. In `Races`: a `Signals.RequiredRole`-only user gets `403` on update, archive, and reactivate too, and a client reads no identities or roles. `SignalsPageTests` checks that the template fits its entry.
- **Documentation fixed.** The suite ranges in AGENTS.md and the runtime README name H19 and R20; the signal scopes' catalog access is stated on the client pages; a catalog write stops once it has found a sixth problem and lists five; an unchanged archive still needs `expectedUpdatedAt`; the example steps state the whole entry on change, cannot run twice, and need a new idempotency key to throw again; the counts of the 2026-11-24 verification below are corrected.
- **Verification of the review.** The catalog, Signals page, editor catalog, and schema classes passed 235 of 235, 20 more than before the fixes. `dotnet test Flowbit/Flowbit.slnx` passed 6,524 of 6,524 in 20 m 27 s. With freshly published hosts, smoke scenarios R19, R20, and H19 passed 6 of 6, and acceptance scenario S16, whose entry change now states the whole entry, passed with a Worker. The UI change is the template's `{}` for a `json` field, which `SignalsPageTests` checks; no layout changed, so no manual browser pass was repeated. `git diff --check` is clean.

### Verification of 2026-11-24

- **Automated tests.** New classes: `SignalCatalogEntryRulesTests`, `SignalCatalogContractTests` (61), `SignalCatalogTests` (38, PostgreSQL, partial across four files: saves, publications, references, backfill, catalog writes, throws, cancel and retry, runtime command capture, five race tests, reactivation, authorization, OpenAPI), `SignalCatalogMigrationTests` (own database), `SignalCatalogPageTests`, additions to `SignalsPageTests`, `EditorSignalCatalogTests` (10 methods, 14 cases, Jint), and `WorkflowEditorHostSignalCatalogTests` (4). The file and test counts of this item were corrected on 2026-11-25; the review of that day added tests. `DatabaseSchemaTests` now counts 74 tables. A regression slice over the signal, OpenAPI, migration, settings, editor, reactivation, definition, AI, shared-variable, and business-calendar classes passed 2,460 of 2,461; the one failure was the pinned table count, fixed and rerun.
- **Browser suites.** New smoke scenarios R19 (Signal catalog page at 1440×900, 1024×768, and 390×844), R20 (Signals page with the catalog at 1440×900 and 390×844), and H19 (hosted editor catalog block) in [SignalCatalogSmokeTests.cs](../../Flowbit/tests/Flowbit.BrowserTests/SignalCatalogSmokeTests.cs), and acceptance scenario S16 (live versions protect their contract, with a Worker) in [SignalCatalogAcceptanceTests.cs](../../Flowbit/tests/Flowbit.BrowserAcceptanceTests/SignalCatalogAcceptanceTests.cs). The full smoke suite passed 157 of 157 in 16 m 46 s, and the acceptance suite's signal scenarios S1 to S16 passed 22 of 22, the rerun that every throw and publication now taking a name lock calls for. H19 failed once on its own mistake (it clicked an undo button the hosted command bar does not show) and passed after the scenario was fixed. After the last catalog-page change the three new smoke scenarios passed again, 6 of 6.
- **Full solution.** `dotnet test Flowbit/Flowbit.slnx` passed 6,504 of 6,504 in 20 m 13 s on the final tree, with no order-dependent failures; `git diff --check` is clean.
- **Test table audit.** Every row of [section 10](#10-tests-and-verification) has tests. Covered as planned: entry rules, the payload check, the definition check (each message at each stage, both type tables, paths, sources, a variable declared twice, archived, `required` mode, node order), saves and publications, references and the backfill, catalog writes, reactivation, throws, cancel and retry, runtime command capture in `required` mode, the five races plus the reverse wait, authorization, the migration, OpenAPI, the editor, and Flowbit.Ui. Covered differently: "archive allowed after the blockers end" is shown by the counts falling to zero after delivery and by archiving a name only a draft uses, because a published default cannot be unpublished in a test without replacing it; "the hash of a recorded request is unchanged" is shown by the replay of a broadcast thrown before its entry existed, since the hash code did not change. `MigrationDowngradeCannotRemovePermanentPruningMarkers` passed in the full run.
- **Real browser, by hand**, in the built-in browser at `http://127.0.0.1:25152` against published API, UI, and Worker hosts on a fresh database: the plan's seven manual steps. An entry with two fields and a throw role was created on the **Signal catalog** page with the keyboard through the field table (the pane's key action cannot type `D` or `-`, so those two values were corrected by click and type), both warnings shown, reviewed, and saved. A change Example 01 does not fit was listed by the review, with no **Save**, and refused by the API naming `example-signal-catch-policy-change v1 (#1)`. On **Signals**: the name list offered the entry with its description, the panel showed its fields and "You can throw this signal.", **Insert payload template** filled the box, the review showed the Catalog row, and the throw reached both waits; a misspelled member showed the `400`; a user without the role saw the panel's refusal and the `403` without the page clearing; another user's cancel was refused while the thrower, without the new role, cancelled. In the hosted editor the name suggestion carried the description, the block listed the fields, **Add missing fields** added two mapping rows on a catch (focus on the first) and two payload entries on a throw, one undo removed the rows, and the draft saved. With `Signals.CatalogMode=required` a draft with an uncataloged name saved, its publication was refused, and the page listed the live uncataloged name with the mode. A completed instance whose retired version no longer fit showed `signal_catalog_mismatch` in the preview with **Confirm reactivation** disabled, and the API commit returned `409`. The catalog page, the **Signals** page, and the editor had no horizontal overflow at 390×844, 1024×768, and 1440×900, and the console had no errors.

### Plan review of 2026-11-24

A review of the plan against the code, on the day it was written, found these problems. Each is corrected in the sections below.

- **A business-key payload source can be absent.** The plan said a `source` entry is always present. `ThrowSignalFromNodeAsync` leaves out a `businessKey` member when the instance has no key and the entry is not `required`, and an instance moved by a version change from an unkeyed version has none. A required field now needs `required: true` on its entry, `businessKey` sources included; only `instanceId` is always present.
- **A migration does seed a signal setting.** `AddSignalThrowEvents` inserts `Signals.MaxOpenWorkflowBroadcasts` with a description when it is missing. `AddSignalCatalog` now seeds `Signals.CatalogMode` the same way.
- **The AI model never sees catalog problems.** The agent's own checks run no readiness check (`WorkflowAiWorkflowCheck.NotChecked`); only the proposal's check does. The plan now says so, and the messages that the model cannot otherwise act on list the entry's fields.
- **A catalog write checked whole definitions.** It now checks only the nodes that use the name, so another name's problem in the same version cannot block it.
- **Reactivation broke invariant 1**, which the plan had listed as a limit. Reactivation now refuses a version that no longer fits, with the name locks taken before the instance lock.
- **Catalog text reached `innerHTML`.** The editor's `selectField` and `field()` build markup from unescaped template strings. The plan now requires catalog text to be set as text or element properties, with a test.
- **The editor's catalog reply could reach megabytes** over the Blazor circuit. The list now carries names, short descriptions, and flags; fields come per entry.
- **Two role settings could lock everyone out.** With `Signals.RequiredRole` at its default `admin`, an entry role such as `recall-manager` lets nobody throw unless someone has both. The page now warns, and the guides say to add the role to `Signals.RequiredRole`.
- **Tests could break each other.** Entries and the mode apply to the whole deployment. The test plan now requires unique names, a restored mode, and versions created through the API, because directly inserted definitions get no reference rows.
- **A catalog write could hold the name lock for long.** It now loads live versions in pages and stops once it has found more than five problems; the unlocked preview gives the full list.
- **Smaller gaps.** The throw's catalog step is placed before `CountOpenAsync`; the readiness checker's construction with `new` is named; the documentation rows for the `Settings.RequiredRole` and `Workflow.RequiredRole` entries and the AGENTS.md settings bullet are added; a name ending in `.` is noted for proxies; **Add missing fields** leaves a variable empty when the field name is not a plain identifier.

The user accepted three changes to the design on 2026-11-24, recorded under [Open questions](#open-questions): drafts are checked only for the problems that are wrong at any stage, the thrower of a broadcast may always cancel it, and a `string` reader accepts `date` and `datetime` values.

### Facts the plan rests on

Verified in the code on 2026-11-24, at `57dbfe6`.

- **Throw order.** `ThrowAsync` in [SignalBroadcastService.cs](../../Flowbit/src/Flowbit.Service/Services/SignalBroadcastService.cs) checks the name rule, the scope, and `expiresAfter`, normalizes the payload (a JSON object of at most 256 KiB), hashes the request, and replays an earlier broadcast for a repeated idempotency key. Only then does it resolve the scope against the database and read the two limits. Its transaction locks the idempotency key, looks for a replay again, counts open broadcasts, and calls `SignalBroadcastWriter`. The hash covers the name, the scope, the payload, and the expiry.
- **Callers.** `SignalCallerResolver` turns a managed client into a `SignalCaller` with its client ID and no roles, and a JWT user into one with its roles and audit claims. Client scopes are checked only by the authorization policy and do not reach the service.
- **Policies.** `SignalAuthorizationPolicies` in [SharedVariableAuthorization.cs](../../Flowbit/src/Flowbit.Api/Auth/SharedVariableAuthorization.cs) defines `Signals.Throw` (throw, cancel, retry), `Signals.Read` (list, detail, deliveries), and `Signals.Summary` (users only). Each uses `SharedVariableAccessRequirement`, which takes one role setting key, so no policy today accepts roles from two settings.
- **Visibility.** A client sees and cancels only its own broadcasts (`IsVisible`). A user sees every broadcast.
- **Receivers.** A wait maps the payload through `ResolveTypedOutputs`, whose failure becomes `SignalPayloadException` and the result `signal_payload_invalid` ([WorkflowEngineService.Signals.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.Signals.cs)). A start uses `ResolveStartMappings`. `TypedOutputValueValidator` in Flowbit.Service owns the strict value check for the six data types, arrays included; its `string` check accepts any JSON string, a date or a datetime included. A mapping path is read with case-sensitive property lookups.
- **Workflow throws.** `ThrowSignalFromNodeAsync` in [WorkflowEngineService.SignalThrows.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.SignalThrows.cs) copies stored instance variables whole into the payload, refuses a missing or null value of a `required` entry, and throws with no roles. A `businessKey` source is left out when the instance has no key and the entry is not `required`, and refused when it is. `SignalPayloadEntryModel` is strict: one of `variable` or `source` (`instanceId`, a number; `businessKey`, a string).
- **Definition rules.** `SignalEventRules` owns the name rule, `NodeProblems`, `StartProblems`, `ThrowProblems`, and `BoundaryNameProblems`; `ThrowProblems` reads declarations through `CallActivityRules.SourceDeclarations`, which can list one name with several declared types. `MessageOutputMappingModel` has `variable`, `path`, `dataType`, `isArray`, `required`, `defaultValue`, and `validation`. The data types are `string`, `number`, `boolean`, `date`, `datetime`, and `json`.
- **Readiness.** `WorkflowDefinitionService.CreateAsync` and `CreateNewVersionAsync` run the shared-catalog and business-calendar checks for every save, drafts included, and the call-target and signal-target checks only when publishing, so that drafts can be written in either order. `PublishAsync` and `SetDefaultAsync` repeat all of them. `WorkflowDefinitionService` and `WorkflowAiAuthoringService` each construct the `WorkflowDefinitionReadinessChecker` with `new`, passing its repositories.
- **AI checks.** `WorkflowAiAuthoringService` collects the save checks as save blockers and the target checks as publication blockers of a proposal. The agent's `validate_workflow`, `validate_item`, and end-of-turn gate run `WorkflowAiWorkflowCheck`, whose `NotChecked` list names "save and publication readiness": the model never sees those problems.
- **Persistence of references.** `WorkflowDefinitionRepository.AddAsync` takes the exclusive family advisory lock (`pg_advisory_xact_lock(hashtext('workflow-family'), hashtext(key))`), then writes the shared-variable bindings (catalog rows locked `FOR UPDATE`, archived keys refused), the business-calendar references (rows locked `FOR KEY SHARE` in name order), and the call targets (no lock). `SetPublishedAsync` writes the calendar references again and checks the shared keys are active. `SetDefaultAsync` refuses an unpublished version. A definition JSON never changes after it is saved.
- **Resource lifecycles.** A shared variable's contract is fixed at creation, and archiving it is refused while published versions, running instances, or open jobs bind it (`GetBlockersAsync`). Deleting a business calendar locks it `FOR UPDATE` and is refused on the same three blockers. Both pages and APIs belong to settings or shared-variable administrators.
- **No record of names per version.** No table records which signal names a version uses. `signal_subscriptions` holds the active waits, `signal_start_subscriptions` the starts of each family's published default, and `signal_broadcasts."SignalName"` the broadcasts. Definitions that tests insert directly into the database get no reference rows of any kind.
- **Reactivation.** The preview (`GET /api/instances/{id}/reactivation`) reports blockers by code, such as `unresolved_durable_work`, and the commit refuses them under the instance's locks.
- **Settings and errors.** `SettingsAuthorizationFilter` guards the business-calendar, AI-model, and retention groups with `Settings.RequiredRole`; `WorkflowAdministratorPolicy` reads `Workflow.RequiredRole`. The API maps `WorkflowDomainException` to `400`, `WorkflowConflictException` to `409`, and `WorkflowForbiddenException` to `403`, each with an `{"error": ...}` body; a refusal by an authorization policy has no body. `AddSignalThrowEvents` seeds `Signals.MaxOpenWorkflowBroadcasts` with a description when the row is missing; a missing `Signals.*` value means the default.
- **Editor.** `signalNamesInDiagram` and `renderSignalNameField` in [flowbit-editor.js](../../Flowbit/src/Flowbit.Ui/wwwroot/editor/flowbit-editor.js) build the name datalist from the diagram only. `selectField` and `field()` build their label and options as template strings assigned to `innerHTML`, without escaping. `timerSchedules` is the editor's only optional host capability: [workflow-editor-host.js](../../Flowbit/src/Flowbit.Ui/wwwroot/workflow-editor-host.js) routes it through `askHost`, which answers `{ status: "unavailable" }` when a call fails, and [WorkflowEditor.razor](../../Flowbit/src/Flowbit.Ui/Components/Pages/WorkflowEditor.razor) replies `ok`, `invalid`, `forbidden`, or `unavailable` (`TimerScheduleReply`). The save validator between the `// BEGIN/END WORKFLOW SAVE VALIDATOR` markers contains the signal phases.
- **Signals page.** [Signals.razor](../../Flowbit/src/Flowbit.Ui/Components/Pages/Signals.razor) takes the name in `signal-name` with the datalist `signal-name-options`, filled from the subscription summary, and the payload as JSON in `signal-payload`. Every call goes through `GuardAsync`, which clears the page's signal data on `401` or `403`.
- **Browser suites.** The smoke suite ends at E25, H18, and R18 and runs no Worker; the acceptance suite's signal scenarios are S1 to S15. Both suites share one stack and database across their scenarios.

## Builds on

- The shared-variable catalog: a contract that a definition must match when it is saved and published, and archiving refused while something uses the key.
- Business calendars: a named resource that definitions reference, with a references table, locked rows, and refused deletion.
- `WorkflowDefinitionReadinessChecker`, which already checks named resources at save and publication, and feeds the AI assistant's proposal check.
- The family advisory lock, as the model of a lock per name that definition writes, catalog writes, API throws, and reactivation take.
- The reactivation preview's blocker list, which gains one code.
- The hosted editor's optional `timerSchedules` capability, as the model of a `signalCatalog` capability.
- The typed mapping shape of message and signal mappings, and `TypedOutputValueValidator`, as the form and the check of a payload field.

## Open questions

No question remains open. The plan decides the five original questions and the ones it and its review raised. The decisions marked **Confirmed** change what callers or administrators see; the user confirmed them on 2026-11-24, when the status became Planned. The user also accepted the review's three design changes the same day: the rows on drafts, cancelling, and compatible types.

| Question | Decision |
| --- | --- |
| Is the catalog optional or required, and is that a deployment setting? | **Confirmed.** A dynamic engine setting, `Signals.CatalogMode`: `optional` (also missing, blank, or any other value) or `required` (trimmed, case-insensitive). It is read when a definition with signal events is published and when a name without an entry is thrown, never latched. `required` does not touch drafts, published definitions, waits, or workflows' throws: the [invariants](#1-scope-and-invariants) keep the catalog out of runtime. An unreadable value falls back to `optional`, because a typo must not make every publication fail; the catalog page shows the mode in effect. |
| Is a contract a list of typed fields, as mappings are, or a JSON Schema? | A list of typed fields with the mapping vocabulary: `name`, `dataType`, `isArray`, `required`, and `description`. Authors know these types, the same strict check serves mappings and contracts, the editor can show a field list, and comparing a receiver with a contract is a lookup. A `json` field accepts any value and leaves its inside unchecked. JSON Schema would need a validator library, a second type system, and a subset decision; nested structure is a [later release](#later-releases). |
| Is a contract open or closed? | **Confirmed.** Closed: a throw may send only the declared fields. An open contract would let a mistyped optional member through, which is the mistake the catalog exists to catch. A field of type `json` is the escape hatch for free-form data. |
| Must a mapping or a payload variable have exactly the field's type? | No; accepted after the review. The reading side may be wider than the writing side, as the runtime check is: a `json` mapping reads any field, a `string` mapping reads a `date` or `datetime` field, a `date` or `datetime` variable fills a `string` field, and any variable fills a `json` field. Everything else must match in type and array flag. The table is in [section 3](#definitions). |
| Which checks run when a draft is saved? | Accepted after the review: only the problems that are wrong at any stage, namely archived names, members and paths that are not fields, and types that do not fit. Completeness waits for publication: a required field not yet sent, a receiver that requires an optional field without a default, a throw that workflows may not make, and a missing entry in `required` mode. Call and signal targets are publication checks for the same reason: an author can save a half-finished throw, or a draft for a signal the administrator has not cataloged yet. Invariant 1 concerns live versions only, so it is unaffected. |
| Do an entry's roles replace `Signals.RequiredRole` or add to it? | **Confirmed.** They add to it. `Signals.RequiredRole` stays the gate to the signal routes and the **Signals** page; an entry's `throwRoles` narrow who may throw, cancel, or retry a broadcast of that name. An empty list narrows nothing. A user therefore needs a role from each, so the catalog page warns when no entry role appears in `Signals.RequiredRole`, and the guides say to add an entry's roles there. Reading broadcasts is not narrowed, because the list and detail routes already show every name to every signal user. |
| Who may cancel a broadcast of a narrowed name? | Accepted after the review: its thrower always, and anyone else who may throw the name now. Cancelling is how a mistake is stopped, so a thrower who threw by mistake, or a client whose entry stopped allowing clients, can still stop its own broadcast. Retry stays narrowed for everyone, because it delivers the payload again. |
| Grants for one client need a change to the client model. Does that belong here or to EVENT-04K? | **Confirmed.** To a [later release](#later-releases), planned with [EVENT-04K](event-04k-rename-managed-api-clients.md). This item adds no scope and no per-client grant, so EVENT-04K stays separate. An entry has `allowClients` instead: a client with `signals.throw` may throw or retry the name only when it is true, which is the default. An entry with throw roles that leaves `allowClients` on is legal, and the page warns about it. |
| What happens to open subscriptions and saved definitions when a contract changes? | **Confirmed.** A change, and the creation of an entry for a name in use, is refused while a live version would no longer fit: a published version, or one with a running instance. Open subscriptions and family starts belong to live versions, so they always fit. Reactivating an instance on a version that no longer fits is refused. Drafts and unpublished versions without running instances are not checked by a change; they are checked when saved again or published. A broadcast already thrown keeps its payload. Changing a contract that live versions rely on therefore means changing those versions first, or waiting for versioned contracts. |
| Do entry roles apply to workflows' throws? | No. A workflow throws with no roles. An entry's `allowWorkflows` switch decides whether workflows may throw the name at all, checked when a definition is published. Publishing needs `Workflow.RequiredRole`, which is the authority behind a workflow's throw today. |
| Is a workflow's throw, or a delivery, checked against the catalog at runtime? | No. Every live version fits, so a workflow's payload already has compatible types, and a required field comes from a `required` payload entry or `instanceId`, which the throw already refuses or always sends. Keeping the catalog out of runtime means no transition reads it and a catalog outage cannot stop an instance. |
| Archive, delete, or both? | Archive and reactivate. Nothing is deleted in this release, so a name with a history stays reserved. An archived name is refused when a definition is saved or published and when it is thrown through the API, in both modes. |
| Who manages the catalog, and who reads it? | Settings administrators (`Settings.RequiredRole`) manage it, as they manage business calendars and AI models. Users with a role from `Settings.RequiredRole`, `Signals.RequiredRole`, or `Workflow.RequiredRole` read it, so that the catalog page, the **Signals** page, and the hosted editor can all show it; managed clients with `signals.read` or `signals.throw` read it to learn the contracts. Names, contracts, and roles are not secrets. Catalog text is data written by administrators and is never rendered as markup. |
| How is a name compared? | Exactly, with the `C` collation, as subscriptions compare it. The name rule already allows lowercase only. |
| Does a repeated idempotency key replay before or after the catalog check? | Before, as scope resolution does: a retried request gets its broadcast whatever happened to the catalog since. The request hash does not change, so stored keys replay. |
| May a receiver map an optional field as required? | Only with a `defaultValue`, checked at publication. Otherwise a throw that leaves the field out, which the contract allows, fails the delivery, which is the failure the catalog exists to prevent. |
| How does the AI assistant learn about the catalog? | Through the proposal's readiness check only. The agent's tools, instructions, and checks do not change, as AGENTS.md requires, so the model never sees catalog problems; the person sees them as the proposal's save or publication blockers. A message about an unknown field or a type lists the entry's fields, so that the person, or their next message to the assistant, can fix it. A catalog block for the assistant is a [later release](#later-releases). |
| Does a catalog change raise a webhook event or write audit history? | No webhook event. Each entry keeps who created and last changed it; a history of contracts is a [later release](#later-releases). Every change logs one information line with the name and the caller. |
| How does a deployment move to `required`? | The page lists the names that live versions use without an entry, with their version counts, and offers to create an entry for each. Names thrown only through the API leave no trace in definitions; the recent broadcasts list shows them. |

## Plan

Written on 2026-11-24 following [Writing a plan](README.md#writing-a-plan), and corrected by its [review](#plan-review-of-2026-11-24) the same day. Effort stays **M–L**: the item adds a catalog resource with a migration and a backfill, a lock per name shared by definition writes, catalog writes, API throws, and reactivation, nine routes, a page, a hosted editor capability, and changes to the **Signals** page. It changes no workflow JSON, no engine lifecycle, no Worker, no version-change rule, and no webhook, so the plan has no section for those.

### 1. Scope and invariants

The catalog is a deployment-wide list of signal names with their contracts and permissions. It is consulted when people write things down or bring work back: when a definition is saved or published, when an instance is reactivated, when someone throws, cancels, or retries through the API, and when the catalog itself changes. It is never consulted while instances run.

The invariants of the [parent plan](event-04-signal-events.md#1-scope-and-invariants) hold unchanged. These are new:

1. **Every live version fits its entries.** A live version is a published version or one with a running instance. Publishing and reactivating check a version against the current entries; creating, changing, or reactivating an entry checks every live version that uses the name. Each side takes the lock of each name involved, so neither can commit against a state the other is changing. Drafts are checked too, for the problems that are wrong at any stage, but they are not live.
2. **Runtime never reads the catalog.** No delivery, start, workflow throw, timer, job, or Worker pass reads it. Only definition saves and publications, reactivation, API throws, cancels, and retries, and the catalog's own routes do.
3. **One lock per name, before any runtime row.** Definition saves and publications take the shared advisory lock of each signal name they use, in ordinal order, after the family lock; reactivation takes the shared locks of its version's names, in ordinal order, before it locks the instance; API throws take the shared lock of their one name inside their transaction; catalog writes take the exclusive lock of their one name. No transaction takes a name lock while it holds a runtime row.
4. **Without entries and in `optional` mode, behavior is unchanged.** Saves of definitions without signal events run no new statement. A save with signal events runs one lock per name, one read of their entries, and one insert of its references; a publication also reads the mode when a name has no entry. An API throw runs one lock and one entry read, and reads the mode only for a name without an entry; otherwise it behaves as today.
5. **A refusal names the field, never a value.** Payload values can be personal data. Every message names the signal, the field or the node, and the expected type.
6. **Names are compared exactly**, everywhere, as subscriptions compare them.
7. **Catalog text is data.** Names, descriptions, field names, and roles are shown with text properties in the editor and with Razor's encoding in Flowbit.Ui, never through `innerHTML` or the editor helpers that build markup from strings.

This release does not add per-client grants, family-limited entries, versioned contracts, value rules on fields, a webhook event, a history of contracts, AI catalog blocks, or any change to delivery, scopes, expiry, version changes, or retention.

Two limits follow:

- **A broadcast thrown before a change keeps its payload.** A delivery that runs after a compatible change can still fail on a receiver saved after it, as it can today.
- **Anyone who can publish workflows can make one throw a name**, unless its entry turns off `allowWorkflows`. Entry roles govern people and clients only.
- **Parts of a field are not promised** (added by the [review of 2026-11-25](#review-of-2026-11-25)). The elements of an array field and the inside of a `json` field are not declared, so a required mapping that reads `skus.0` or `details.lot` without a `defaultValue` can still fail a delivery when that part is missing.

### 2. Catalog entries

An entry as the API returns it:

```json
{
  "name": "warehouse.product-recall",
  "description": "A supplier recalled a product. Open orders for it stop.",
  "status": "active",
  "fields": [
    { "name": "reason", "dataType": "string", "isArray": false, "required": true, "description": "Why the product was recalled." },
    { "name": "skus", "dataType": "string", "isArray": true, "required": true, "description": null },
    { "name": "details", "dataType": "json", "isArray": false, "required": false, "description": "Supplier data, passed on unchanged." }
  ],
  "throwRoles": ["recall-manager"],
  "allowClients": false,
  "allowWorkflows": true,
  "canThrow": false,
  "createdAt": "2026-11-24T09:00:00Z",
  "createdBy": "alice",
  "updatedAt": "2026-11-24T09:00:00Z",
  "updatedBy": "alice",
  "archivedAt": null
}
```

| Member | Rule |
| --- | --- |
| `name` | Required; `SignalEventRules.IsValidName`. Fixed at creation: it is the route key and is not in the update body. Unique among active and archived entries. |
| `description` | Optional; trimmed, blank means null; at most 1,000 Unicode scalars. |
| `fields` | Optional, at most 100 (`SignalEventRules.MaxPayloadEntries`), in the order given. |
| `fields[].name` | `SignalEventRules.IsValidPayloadName`, unique ignoring case, as payload entry names are. |
| `fields[].dataType` | One of `WorkflowVariableTypes`: `string`, `number`, `boolean`, `date`, `datetime`, `json`. |
| `fields[].isArray`, `fields[].required` | Booleans, default false. |
| `fields[].description` | Optional; at most 300 Unicode scalars. |
| `throwRoles` | Optional; at most 100 roles, each trimmed, nonblank, at most 300 Unicode scalars, deduplicated ignoring case, as waiting-task role lists are. Empty narrows nothing. |
| `allowClients`, `allowWorkflows` | Booleans, default true on create. A change must state them, with `fields` and `throwRoles` (review of 2026-11-25). |
| `canThrow` | Response only: whether the caller could throw the name now (see [section 6](#6-api-contracts-and-authorization)). |

The request bodies are strict (`JsonUnmappedMemberHandling.Disallow`, `JsonNumberHandling.Strict`), as `SignalPayloadEntryModel` is, so a misspelled `alowClients` is a `400` rather than a silent default. The first problem is reported:

| Problem | Message |
| --- | --- |
| Name missing or invalid | The two messages of `SignalEventRules.ThrowNameProblem`. |
| Name taken | `409`: `Signal catalog entry 'warehouse.product-recall' already exists.` |
| Description too long | `description can have at most 1000 characters.` |
| Too many fields | `fields can have at most 100 entries.` |
| Field name | `Field 3 name must be 1 to 100 letters, digits, '_', or '-', starting with a letter or '_'.` |
| Repeated field | `Field 'reason' appears more than once.` |
| Field type | `Field 'reason' dataType must be string, number, boolean, date, datetime, or json.` |
| Field description | `Field 'reason' description can have at most 300 characters.` |
| Roles | `throwRoles can have at most 100 roles.`, `A throw role cannot be blank.`, `A throw role can have at most 300 characters.` |
| A field is null (implemented after the table) | `Field 2 cannot be null.`, before the field's name is checked. |

Code:

- **`SignalCatalogEntryRules`** in `Flowbit.Shared/Models`: the limits, the field and role normalization, and the problems above, so the page can check a form before any request.
- **`SignalCatalogContract`** in `Flowbit.Service/Services`: the payload check, the definition check, and the type compatibility of [section 3](#3-checks-against-the-catalog), as pure functions over an entry and a definition or a payload. It uses `TypedOutputValueValidator`, so a field and a mapping accept exactly the same values.
- **DTOs** in a new `SignalCatalogDtos.cs`: `SignalCatalogEntryDto`, `SignalCatalogFieldDto`, `SignalCatalogListDto`, `SignalCatalogEntrySummaryDto` (for the editor), `CreateSignalCatalogEntryRequest`, `UpdateSignalCatalogEntryRequest` (everything but the name, plus `expectedUpdatedAt`), `SignalCatalogLifecycleRequest` (`expectedUpdatedAt`), `SignalCatalogImpactDto` with `SignalCatalogProblemDto`, `SignalCatalogUsageDto` with `SignalCatalogVersionUseDto`, and `UncatalogedSignalNameDto` inside `UncatalogedSignalNameListDto`, each with `<param>` comments for the OpenAPI schema.
- **`SignalCatalogModes`**, beside `SignalCatalogEntryRules` in `SignalCatalogEntryRules.cs`: the key `Signals.CatalogMode`, the two values, and `ParseMode`.

### 3. Checks against the catalog

#### Payload of an API throw

For an entry with fields, in this order, the first problem returns `400`:

1. For each field in contract order: absent or JSON null when required gives `Signal 'warehouse.product-recall' payload is missing the required field 'reason'.`; JSON null when optional gives `Signal 'warehouse.product-recall' payload field 'details' is null; leave out an optional field instead of sending null.` (review of 2026-11-25); present, not null, and not of the field's type gives `Signal 'warehouse.product-recall' payload field 'skus' must be an array of strings.`
2. For each payload member in payload order that is not a field: `Signal 'warehouse.product-recall' payload member 'reson' is not a field of its catalog entry. Its fields: reason (string), skus (string[]), details (json).`

An entry without fields accepts only an absent payload or an empty object, because the contract is closed. No field may be null, optional ones included, because a receiver's mapping refuses null unless its target variable is nullable; the plan first let an optional field be null, and the [review of 2026-11-25](#review-of-2026-11-25) changed that. A date must be `YYYY-MM-DD` and a datetime ISO-8601 with an offset, exactly as a typed mapping requires, and the type words come from one helper (`a string`, `a number`, `true or false`, `a date (YYYY-MM-DD)`, `a date and time (ISO-8601)`, `any JSON value`, and their plural array forms). A field list in a message names at most 20 fields and then the number of the others.

#### Definitions

`SignalCatalogContract.FindDefinitionProblems(definition, entries, mode, stage)` visits the signal events in node order. `stage` is `draft`, `publication`, or `reactivation`. Save rules apply at every stage; publication rules apply at publication and reactivation, and to every catalog write, because live versions are published or running. The `required`-mode rule applies at publication only: reactivation brings back a version that was published, and published definitions keep running in that mode.

| Node | Problem | Stage | Message |
| --- | --- | --- | --- |
| Any signal event | Archived entry | Save | `Signal catch event #4 uses signal 'claims.policy-changed', whose catalog entry is archived.` |
| Throw or end event | Member not a field | Save | `Signal throw event #7 payload sends 'reson', which is not a field of the catalog entry of 'warehouse.product-recall'. Its fields: reason (string), skus (string[]), details (json).` |
| Throw or end event | Type not compatible | Save | `Signal throw event #7 payload entry 'skus' reads 'skuList', declared as string, but the catalog entry of 'warehouse.product-recall' defines it as string[].` For a source: `Signal end event #7 payload entry 'orderInstance' sends source 'businessKey', which is string, but the catalog entry of 'warehouse.product-recall' defines it as number.` |
| Catch, boundary, or start | Path not a field | Save | `Signal catch event #4 mapping to 'policyVersion' reads 'verison', which is not a field of the catalog entry of 'claims.policy-changed'. Its fields: version (number), note (string).` For a longer path: `... reads 'detail.lot', whose first part 'detail' is not a field of the catalog entry of ...` |
| Catch, boundary, or start | Path below a scalar field | Save | `Signal catch event #4 mapping to 'major' reads 'version.major' below field 'version', which is a number; only a json field or an array element has parts.` |
| Catch, boundary, or start | Type not compatible | Save | `Signal catch event #4 mapping to 'policyVersion' is string, but the catalog entry of 'claims.policy-changed' defines 'version' as number.` For an array element: `... mapping to 'first' is number, but the catalog entry of 'warehouse.product-recall' defines the items of 'skus' as string.` |
| Any signal event | No entry in `required` mode | Publication only | `Signal catch event #4 uses signal 'claims.policy-changed', which has no entry in the signal catalog; this deployment requires one before publication.` |
| Throw or end event | `allowWorkflows` off | Publication | `Signal throw event #7 throws 'warehouse.product-recall', which its catalog entry does not let workflows throw.` |
| Throw or end event | Required field not sent | Publication | `Signal throw event #7 payload does not send 'skus', which the catalog entry of 'warehouse.product-recall' requires.` |
| Throw or end event | Required field from an optional entry | Publication | `Signal throw event #7 payload entry 'skus' must set required, because the catalog entry of 'warehouse.product-recall' requires it.` |
| Throw or end event | Optional field from an optional entry whose variable can be null (review of 2026-11-25) | Publication | `Signal throw event #7 payload entry 'note' must set required, because variable 'remark' can be null and the catalog entry of 'warehouse.product-recall' accepts no null field.` |
| Catch, boundary, or start | Required without default on an optional field | Publication | `Signal catch event #4 mapping to 'note' is required without a defaultValue, but 'note' is optional in the catalog entry of 'claims.policy-changed'.` |

Compatible types, for the field and the side that reads or writes it:

| Field | A receiver's mapping may be | A throw's variable may be |
| --- | --- | --- |
| Any type | The same type and array flag, or `json` with either array flag. | The same type and array flag. |
| `date` or `datetime` | Also `string` with the same array flag. | Only the same type. |
| `string` | Only `string` or `json`. | Also `date` or `datetime` with the same array flag. |
| `json` | Only `json`. | Any type and array flag. |

The other rules behind the messages:

- **Types of a throw.** A payload entry's type is its variable's declared type and array flag; every declaration that `CallActivityRules.SourceDeclarations` lists must be compatible with the field. A `source` entry is `number` for `instanceId` and `string` for `businessKey`.
- **Required of a throw.** A required field must come from `source: "instanceId"`, which is always present, or from a `variable` or `source: "businessKey"` entry with `required: true`, because only those are refused at runtime when the value is missing. A `businessKey` source is left out for an instance without a key.
- **Paths of a receiver.** A blank path (a default-only mapping) is not checked. A path's first segment names the field, compared exactly, as the runtime reads it. A path equal to the field compares the mapping with the field. A path of the field and a numeric index reads one element of an array field and compares with the element type. Any longer path needs a `json` field, or an array of `json`, and its type is not checked.
- **Order.** The node order and, within a node, the order of the table. The error is the first problem; the AI proposal and the catalog's impact preview use the whole list.

The check runs in three places with the same function and messages:

- **`WorkflowDefinitionReadinessChecker.FindSignalCatalogProblemsAsync`** and `ValidateSignalCatalogAsync`, without locks. `CreateAsync` and `CreateNewVersionAsync` call it after the business-calendar check with the `draft` stage, or `publication` when the version is saved as published; `PublishAsync` and `SetDefaultAsync` call it with `publication`. The checker gains `ISignalCatalogRepository` and `IEngineSettingsRepository` as constructor parameters, passed by `WorkflowDefinitionService` and `WorkflowAiAuthoringService`, which construct it with `new`.
- **Inside the repository's transaction**, under the name locks, in `AddAsync` (with the stage its `isPublished` argument gives) and in `SetPublishedAsync(true)` (`publication`): the authoritative check of invariant 1. `SetDefaultAsync` keeps only the readiness check, because it refuses an unpublished version and a published version already passed the locked check.
- **In reactivation**, as described [below](#reactivation).

A definition without signal events skips all of them, with no statement. In the AI assistant, `WorkflowAiAuthoringService` adds the `draft`-stage problems to a proposal's save blockers and the rest to its publication blockers. The agent's own checks are not changed and never see them.

#### Catalog writes

Creating, changing, or reactivating an entry runs `FindDefinitionProblems` with the candidate entry, at the `publication` stage, over the nodes that use the name in every live version, found through the references table of [section 4](#4-persistence-and-migration). Other names in the same versions are not checked. Under the exclusive name lock the write loads the live versions in pages of 50 and stops once it has found a sixth problem, so the lock is held briefly; any problem refuses the write with `409`:

```text
Signal catalog entry 'claims.policy-changed' does not fit live workflow versions: claims-review v3 (#41): Signal catch event #4 mapping to 'policyVersion' is string, but the catalog entry of 'claims.policy-changed' defines 'version' as number. …
```

The message names at most five problems and says when there may be more. `POST /api/signal-catalog/preview` runs the same check without a lock and without writing and returns the complete list, at most 200 problems with `truncated`, so the page can show it before saving. Changes that touch only the description, the field descriptions, `throwRoles`, or `allowClients` cannot fail it; the check still runs, because it is cheap and the rule stays simple.

Archiving is refused with `409` while any of these exists, each counted by the usage read:

| Blocker | Source |
| --- | --- |
| Published versions that use the name | References joined to `workflow_definitions."IsPublished"`. |
| Versions with a running instance that use the name | References joined to running `workflow_instances`. |
| Active waits | `signal_subscriptions` with the name and the status `active`. |
| Families that start on it | `signal_start_subscriptions` with the name. |
| Broadcasts still delivering | `signal_broadcasts` with the name and the status `delivering`. |

`Signal catalog entry 'claims.policy-changed' cannot be archived: 3 published workflow versions use it; 12 signal waits are open for it.`

#### Reactivation

Reactivation makes a version live again, so it is checked:

- **Preview.** `GET /api/instances/{id}/reactivation` adds the blocker `signal_catalog_mismatch` when the instance's version has a problem at the `reactivation` stage, with the first problem as its message. The check reads entries without a lock and is skipped for a version without signal events.
- **Commit.** For a version with signal events, the commit takes the shared name locks of the version's names, in ordinal order, before it locks the instance, then repeats the check and refuses with the same blocker, as it refuses every other blocker.

### 4. Persistence and migration

One migration, `AddSignalCatalog`, in a transaction.

| Object | Definition |
| --- | --- |
| `signal_catalog_entries` | `Id` (identity), `Name` (`varchar(100)`, `C` collation, unique), `Description` (`varchar(1000)`, null), `Fields` (`jsonb`, an array), `ThrowRoles` (`jsonb`, an array of strings), `AllowClients` and `AllowWorkflows` (`boolean`), `Status` (`varchar(16)`), `CreatedAt`, `CreatedBy` (`varchar(300)`), `UpdatedAt`, `UpdatedBy` (`varchar(300)`), `ArchivedAt` (null). |
| `CK_signal_catalog_entries_name` | `"Name" ~ '^[a-z0-9][a-z0-9._-]{0,99}$'`, the name rule. |
| `CK_signal_catalog_entries_shape` | `Status` is `active` or `archived`; `ArchivedAt` is null exactly when active; `jsonb_typeof` of `Fields` and `ThrowRoles` is `array`. |
| `workflow_definition_signal_names` | `WorkflowDefinitionId` (foreign key to `workflow_definitions`, cascade), `NodeId` (`integer`), `SignalName` (`varchar(100)`, `C`), `Usage` (`varchar(16)`: `catch`, `boundary`, `start`, `throw`, `end`); primary key (`WorkflowDefinitionId`, `NodeId`); index on `SignalName`. |
| `engine_settings` row `Signals.CatalogMode` | Inserted with the value `optional` and a description when missing, as `AddSignalThrowEvents` inserts `Signals.MaxOpenWorkflowBroadcasts`; an existing row is left as it is. |

- **No foreign key from references to entries.** Names without an entry are recorded too: they are what the page lists for a move to `required`, and an entry created later finds its users without a scan.
- **Writing references.** `AddAsync` writes one row per signal event with a valid name, as `PersistCallTargetsAsync` writes call targets. A definition never changes after it is saved, so publication writes nothing.
- **Backfill.** The migration inserts the rows of every existing version from `workflow_definitions."Definition"`, with `jsonb_array_elements(... -> 'flowNodes')` as [AddUserTaskRolePolicies](../../Flowbit/src/Flowbit.Infrastructure/Data/Migrations/20260905143652_AddUserTaskRolePolicies.cs) reads nodes, the five signal types, and names that match the rule. A test compares the backfill with the rows `AddAsync` writes for the same definitions.
- **Cost.** One scan of `workflow_definitions` in the migration; the new tables are small. No runtime table changes.
- **Down migration.** It refuses while an entry exists, archived ones included: dropping the table would silently remove the roles that narrow who may throw. An operator deletes the entries with SQL first, knowing that their restrictions end. It then drops both tables and deletes the seeded setting row when its value is still `optional`.

Repository, a new `SignalCatalogRepository` behind `ISignalCatalogRepository`:

- `ListAsync`, `GetAsync`, `GetManyAsync(names)`; `CreateAsync`, `UpdateAsync`, `ChangeStatusAsync`, each in its own transaction that takes the exclusive name lock first, then locks the row `FOR UPDATE`, compares `expectedUpdatedAt`, runs the live-version check or the blocker counts, and writes. `UpdatedAt` is truncated to microseconds and moves forward on every write, as business calendars do.
- `LockNamesAsync(names, exclusive)`: `pg_advisory_xact_lock_shared(hashtext('signal-name'), hashtext(name))` or the exclusive form, in ordinal order, inside the ambient transaction.
- `GetUsageCountsAsync(name)`, `ListVersionUsesAsync(name, page, pageSize)`, `PreviewAsync(candidate, limit)`, and `ListUncatalogedNamesAsync(limit)` over the references; the writes find live versions through the same references.

`WorkflowDefinitionRepository` gains `EnforceSignalCatalogAsync(definition, stage)`, called in `AddAsync` and `SetPublishedAsync(true)` right after the family lock and before `LockSignalStartsAsync`. For a definition with signal events it takes the shared name locks, reads the entries and, at the `publication` stage when a name has no entry, `Signals.CatalogMode`, and throws the first problem of `FindDefinitionProblems` as a `WorkflowDomainException`.

### 5. Locks and races

| Transaction | Locks, in order |
| --- | --- |
| Save (`AddAsync`) | Family lock (exclusive), name locks (shared, ordinal), signal start rows (`FOR UPDATE`, only when the new version becomes the published default), then the existing shared-variable and calendar row locks. |
| Publish (`SetPublishedAsync(true)`) | Family lock, name locks (shared), signal start rows when default, then the existing locks. |
| Set default | Unchanged: family lock, then signal start rows. It takes no name lock. |
| Reactivation commit | Name locks (shared, ordinal) of the version's signal names, then the existing instance hierarchy locks. |
| API throw | Idempotency key lock (when a key is given), name lock (shared), then the writer's `FOR SHARE` read of start rows. |
| Catalog write | Name lock (exclusive), entry row (`FOR UPDATE`). Reads definitions, instances, subscriptions, and broadcasts without locks. |
| Cancel, retry | Unchanged. The entry is read without a lock to authorize. |

A catalog write holds one name lock and waits for nothing that another transaction holds after its name locks, so no cycle exists. A save or a reactivation that uses two names takes them in ordinal order and may wait for a catalog write on the second while holding the first shared; the catalog write never asks for the first. A catalog write holds its lock only while it loads at most the pages it needs to find five problems, so throws and saves of that name wait briefly.

| Race | Result |
| --- | --- |
| A publication and a change of an entry it uses. | Whichever takes the name lock first commits first. A publication after the change checks the new entry; a change after the publication sees the new version among the live users. |
| A save and the creation of an entry for a name it uses. | The same. |
| A save and an archive. | A save after the archive is refused; an archive after the publication of a version that uses the name is refused. |
| A throw and an archive or change. | The throw reads the entry under its shared lock, so it is checked against the entry that is committed. A broadcast still delivering blocks the archive. |
| A reactivation and a change that its version no longer fits. | The commit checks under the lock and is refused; a change after the commit sees the version among the live users. |
| A publish of an old draft and a change that it no longer fits. | Publication checks under the lock and is refused with the current problem. |
| Two catalog writes of one entry. | The name lock and `expectedUpdatedAt` order them; the second returns `409` and the page offers to reload. |

PostgreSQL race tests for the first, third, fourth, and fifth rows are a release gate.

### 6. API contracts and authorization

New routes, tagged **Signal catalog**, with descriptions in `FlowbitOperationCatalog`. The collection routes sit beside `entries`, so no signal name can shadow them.

| Route | Who | Result |
| --- | --- | --- |
| `GET /api/signal-catalog/entries?name=&status=&page=&pageSize=` | Catalog readers | `SignalCatalogListDto`: `items`, `page`, `pageSize`, and `totalCount`, as `PagedResult` has them, plus `mode`, the value of `Signals.CatalogMode` in effect, and `signalRoles`, the roles of `Signals.RequiredRole`. Ordered by name; `name` is a prefix, `status` is `active` or `archived`; `pageSize` from 1 to 200, default 50. |
| `GET /api/signal-catalog/entries/{name}` | Catalog readers | The entry, or `404`. |
| `POST /api/signal-catalog/entries` | Settings administrators | `201` with `Location`; `400`; `409` for a taken name or live versions that do not fit. |
| `PUT /api/signal-catalog/entries/{name}` | Settings administrators | `200`; `400`, also for a body without `fields`, `throwRoles`, `allowClients`, or `allowWorkflows` (review of 2026-11-25); `404`; `409` for a stale `expectedUpdatedAt`, an archived entry, or live versions that do not fit. |
| `POST /api/signal-catalog/entries/{name}/archive` | Settings administrators | `200`; `404`; `409` for a stale timestamp or blockers. Archiving an archived entry returns it unchanged. |
| `POST /api/signal-catalog/entries/{name}/reactivate` | Settings administrators | `200`; `404`; `409` for a stale timestamp or live versions that do not fit. |
| `GET /api/signal-catalog/entries/{name}/usage` | Settings administrators | `SignalCatalogUsageDto`: the five counts of [section 3](#catalog-writes) and a page of the versions that use the name, with key, version, published flag, and node IDs. |
| `POST /api/signal-catalog/preview` | Settings administrators | `SignalCatalogImpactDto` for a candidate entry: the live versions, and up to 200 problems with `truncated`. Writes nothing. |
| `GET /api/signal-catalog/uncataloged-names` | Settings administrators | Names that live versions use without an entry, with the number of versions each, at most 200, with `truncated` and the `mode` in effect. |

A signal name may end with `.`, and some reverse proxies remove a trailing dot from a path segment. The API reference says so; such names work through Kestrel and through proxies that keep the path as sent.

Authorization:

- **Settings administrators.** `SettingsAuthorizationFilter`, as business calendars use it.
- **Catalog readers.** A new policy, `SignalCatalog.Read`, with its own requirement, `SignalCatalogReadRequirement`: a JWT user with a role from any of `Settings.RequiredRole`, `Signals.RequiredRole`, or `Workflow.RequiredRole`, or a managed client with `signals.read` or `signals.throw`. `SharedVariableAccessRequirement` and the existing policies do not change.
- **`canThrow`** is computed per caller: an active entry, and for a user a role from `Signals.RequiredRole` plus, when `throwRoles` is not empty, one of them; for a client the `signals.throw` scope and `allowClients`. The endpoint reads the scopes from the client identity's claims.

Changes to the signal routes:

| Route | Change |
| --- | --- |
| `POST /api/signal-broadcasts` | After the replay check inside the transaction, and before the open-broadcast count: the shared name lock and the entry read. `400` for no entry in `required` mode, an archived entry, or a payload that does not fit; `403` when the caller may not throw the entry. A payload that does not fit is therefore never reported as the open-broadcast limit. The hash and the response do not change. |
| `POST /api/signal-broadcasts/{id}/retry-failed` | `403` when the broadcast's name has an entry that the caller may not throw, checked after the broadcast is found visible and before its state is read. |
| `POST /api/signal-broadcasts/{id}/cancel` | The same, except that the broadcast's thrower may always cancel it: a user whose actor identity is its `thrownBy` with kind `user`, or the client whose ID it is with kind `client`. |
| `GET` routes and the summary | Unchanged. |

Messages:

| Case | Status | Message |
| --- | --- | --- |
| No entry, `required` mode | `400` | `Signal 'warehouse.product-recall' has no entry in the signal catalog; this deployment requires one.` |
| Archived | `400` | `Signal 'warehouse.product-recall' is archived in the signal catalog and cannot be thrown.` |
| User lacks the entry's roles | `403` | `Throwing signal 'warehouse.product-recall' needs one of the roles its catalog entry lists: recall-manager.` (cancel and retry say "Cancelling" and "Retrying") |
| Client not allowed | `403` | `Signal 'warehouse.product-recall' cannot be thrown by an API client; its catalog entry does not allow clients.` |

- **Compatibility.** In `optional` mode without entries, every existing request gets the same response. A client that throws a name which gains an entry with `allowClients` off starts to get `403` on throws and retries; the API reference and the page's help text say so, and the recent broadcasts list, filtered to **People and API clients**, shows which clients threw a name.
- **Replicas.** An API replica without this release publishes definitions, reactivates instances, and accepts throws without the catalog, which breaks invariant 1. Mixed replicas are unsupported.
- **OpenAPI.** The new routes declare the `Bearer` scheme, and the reading routes also the two client schemes, as the signal routes do.

### 7. Hosted editor

A second optional capability, `signalCatalog`, built like `timerSchedules`:

- **Host.** `workflow-editor-host.js` passes `signalCatalog: { entries: () => askHost("GetSignalCatalog"), entry: name => askHost("GetSignalCatalogEntry", name) }`. `WorkflowEditor.razor` gains two `[JSInvokable]` methods under the same disposal and identity checks as `PreviewTimerSchedule`, replying `ok`, `forbidden`, or `unavailable`:
  - `GetSignalCatalog` reads active entries in pages of 200 up to 1,000 and replies `SignalCatalogReply(Status, Entries, Mode, Truncated)`, where each entry is a `SignalCatalogEntrySummaryDto`: the name, the first 120 characters of its description, `allowWorkflows`, and its field count. At most about 150 KB crosses the Blazor circuit.
  - `GetSignalCatalogEntry(name)` replies with one entry and its fields, or `missing`.
- **Loading.** The editor asks for the list once per mount, when the first signal section renders, and for an entry when a selected node's name matches one, caching each reply. Replies live in per-mount state outside the model, history, the saved baseline, and AI snapshots, as `cronPreview` does. A failed or forbidden reply leaves the editor as it is today.
- **Text only.** The suggestions are `<option>` elements created with `createElement` whose `value` and `label` properties are set; the catalog block sets `textContent`. Catalog text never passes through `selectField`, `field()`, a template string, or `innerHTML`.
- **Name field.** `renderSignalNameField` merges cataloged names into its datalist after the diagram's names, with the short description as the option label. A throw or end event lists only entries that allow workflows.
- **Catalog block.** Under the name, a `role="status"` block (`signal-catalog-{id}`) shows the entry's description and a compact field list (name, type, required), or one note: "Not in the signal catalog." (with "This deployment requires an entry before publication." in `required` mode), "Archived in the signal catalog.", or "Workflows may not throw this signal." Notes are hints; the server decides when the workflow is saved or published.
- **Add missing fields.** A button in the block adds, for a throw or end event, one payload entry per field not yet sent (name set, `required` from the field), and for a catch, boundary, or start event, one mapping row per field not yet mapped (path set to the field name; type, array flag, and `required` from the field). The variable is set to the field name only when that name is a plain identifier (`^[A-Za-z_][A-Za-z0-9_]*$`) and no declared variable of that name has another type or is a shared alias; a mapping also leaves it empty when another row of the event already writes that name, and a start's mapping when a process variable has it. Otherwise it stays empty for the author. One click is one undoable change, recorded by the editor-root listeners; focus moves to the first new row.
- **Not changed.** The save validator, the viewer, and the standalone editor. The catalog rules need server data, so they stay server-side, and a hosted save shows the API's message as it does for every save error.

### 8. Flowbit.Ui

**Signal catalog page.** A new page `/signal-catalog` (`SignalCatalog.razor`) in the **Manage** group of `NavMenu.razor`, after **Signals**, built like `BusinessCalendars.razor`:

- **List.** Name, description, field count, throw roles, the two switches, status, and last change, with an **Active**/**Archived** filter and a name search. Each row has **Edit** and **Archive** or **Reactivate**.
- **Form.** Name (read-only when editing), description, a field table with one row per field (name, type list, **Array**, **Required**, description, **Remove**) and **+ Add field**, **Throw roles** as one comma-separated text box, **API clients may throw it**, and **Workflows may throw it**. `SignalCatalogEntryRules` checks the form before any request.
- **Warnings.** When roles are set and clients are still allowed: "API clients with signals.throw can still throw this signal; their roles are not checked." When no throw role appears in the list's `signalRoles`: "Users need a role from Signals.RequiredRole as well as one of these roles. None of these roles is in Signals.RequiredRole, so only users who hold both can throw this signal; add the role there to let its holders throw it."
- **Review.** **Review changes** calls the preview and shows "The live workflow versions that use this name all fit." or the problems with links to the versions. **Save** is offered only after a clean review of the current form; any edit ends the review. A `409` for a stale entry keeps the form and offers **Reload latest**.
- **Archive.** Opens the usage panel with the five counts; **Archive** is offered only when all are zero, after a confirmation.
- **Names in use without an entry.** A panel from `uncataloged-names`, with **Create entry** prefilled with the name, and the mode in effect.
- **Identity.** An `identityVersion` guard drops late replies after an identity change, and a `403` shows the settings-administrator message, as on the calendars page.

**Signals page.**

- The datalist `signal-name-options` adds the active entries, with their descriptions, to the names from the subscription summary.
- When the name has an entry, a **Catalog** panel under the name shows the description and fields, and either "You can throw this signal." or why not, from `canThrow`. **Insert payload template** writes a JSON object with the fields, required ones first, into an empty payload box. An archived or missing entry shows its note, with the mode.
- The review gains a **Catalog** row: the entry's name, or "No entry".
- **Cancel pending deliveries** stays offered to the broadcast's thrower whatever the entry says.
- A `400` from the throw is shown as today. A `403` from a catalog rule must not clear the page, as `GuardAsync` does today for every `403`. For the three commands (throw, cancel, retry), a `403` that carries an `error` body is shown as the refusal of that request, and the page then reads the broadcast list once: only when that read also returns `401` or `403` is the identity treated as lost and the page cleared. A policy refusal has no body and keeps today's handling.

Typed date controls are not involved, so the browser time-zone rules do not apply.

### 9. Documentation and examples

| Owning page | Update |
| --- | --- |
| [API reference](../api-guide.md#signals) | A new section **Signal catalog** with the nine routes, the DTOs, the validation messages of [section 2](#2-catalog-entries), the `409` bodies, and the note on names that end with `.`; under **Signals**, the catalog checks of the throw, the `403` of throw, cancel, and retry with the thrower's exception for cancel, and the order of checks; under reactivation, the `signal_catalog_mismatch` blocker. |
| [Developer guide](../developer-guide.md#broadcast-signals) | A part **Use the signal catalog**: what an entry declares, the compatible types, how a throw is checked, how roles and `allowClients` combine with `Signals.RequiredRole` and the scopes, which checks wait for publication, what `required` mode changes, and an HTTP example that creates an entry and shows a refused throw. [Signals and version changes](../developer-guide.md#signals-and-version-changes) says that a contract change waits for live versions. |
| [Node reference](../node-reference.md#signal-configuration) | A subsection **Signal catalog checks** with the definition messages and stages of [section 3](#definitions), the type table, and the path rules, linked from each signal event's section. |
| [BPMN support](../bpmn-support.md#signals) | A catalog entry corresponds to the BPMN `signal` root element with its `structureRef`; Flowbit keeps it outside the definition and checks definitions against it. |
| [Flowbit.Ui guide](../ui-guide.md#manage-settings-and-shared-variables) | A part **Signal catalog** for the page and its two warnings; [Throw signals](../ui-guide.md#throw-signals) gains the catalog panel, the template, the refusals, and who may cancel; the editor part on signal events gains the catalog block and **Add missing fields**; the reactivation part names the new blocker. |
| [Engine settings](../engine-settings.md#signal-settings) | `Signals.CatalogMode` with its values, fallback, and seeded row. [Signals.RequiredRole](../engine-settings.md#signalsrequiredrole) says that an entry can narrow it and that an entry's roles must also appear in it. The `Settings.RequiredRole` entry adds managing the signal catalog, and the `Workflow.RequiredRole` entry adds reading it. |
| [Deployment](../deployment.md#signal-events) | A rollout part with the order of [section 11](#11-delivery-sequence-rollout-and-risks); a row for `AddSignalCatalog` in the [upgrade table](../deployment.md#upgrade-and-compatibility-rules); the warning that rolling back removes the entry roles; the note on trailing dots behind proxies; troubleshooting rows under [signals and actions](../deployment.md#signals-and-actions) for a refused publication, a refused entry change, a refused archive, and an entry nobody can throw. |
| [Database schema](../database-schema.md#signals) | The two tables, their checks, and the index. |
| [AI authoring](../ai-authoring.md#validation-and-readiness) | Catalog problems appear as a proposal's save or publication blockers; the assistant itself does not see the catalog. |
| [Runtime reference](../../Flowbit/README.md#signal-events) and [AGENTS.md](../../AGENTS.md) | The invariants of [section 1](#1-scope-and-invariants), the name lock and its order, the references table, `EnforceSignalCatalogAsync`, the reactivation check, the `signalCatalog` capability beside `timerSchedules` in the architecture part, the HTTP API list, the **Signal catalog** page in the Blazor page list, the settings-management bullet that lists what settings administrators manage, and the BPMN concept row. |
| [Portable authoring guide](../../authoring/flowbit-authoring/references/authoring-guide.md) | A deployment can keep a signal catalog: ask for its entries, and make names, mappings, and payloads fit them. |
| [Signal examples](../../examples/signals/README.md) | An optional step that creates an entry fitting Example 01's mappings, shows a refused throw with a misspelled member, and a refused change, with PowerShell and Bash commands. No example JSON changes. |
| [Root README](../../README.md) | The signal bullet names the catalog, if it lists signal capabilities. Confirm when implementing. |
| Browser suite READMEs | R19, R20, and H19 in the smoke matrix; S16 in the acceptance coverage. |
| This page, the [backlog](README.md), the [parent](event-04-signal-events.md#later-releases), [EVENT-04K](event-04k-rename-managed-api-clients.md), and [EVENT-04H](event-04h-expression-signal-names.md) | Evidence and status. EVENT-04K records that per-client grants wait for it; EVENT-04H's catalog question points to the later release. |

Public examples stay HTTP-only. Validate changed relative links, anchors, and JSON examples, and compare the new and changed operations with their registrations, the DTOs, the tests, and the development OpenAPI document.

### 10. Tests and verification

PostgreSQL tests go in a new class `SignalCatalogTests` in the `PostgresApiCollection`. Entries and `Signals.CatalogMode` apply to the whole database that the collection shares, so:

- Every test uses signal names of its own, built from a per-test prefix, and never a name that an example or another test uses.
- A test that sets `required` mode restores the setting in `finally`; the tests that need it run in one class that no other class runs beside.
- Live versions are created through the API. Definitions inserted directly into the database get no reference rows, so the live-version check cannot see them.

| Layer | Cases |
| --- | --- |
| Entry rules | `SignalCatalogEntryRulesTests`: every message of [section 2](#2-catalog-entries) in order; trimming and deduplication of roles; 100 fields accepted, 101 refused; a field name repeated in another case refused. Strict binding refuses an unknown member and a number for a boolean. |
| Payload check | `SignalCatalogContractTests`: each data type accepted and refused, scalar and array; null for a required and an optional field; an absent payload against required fields and against none; an unknown member, with the field list capped at 20; the first problem in contract order, then payload order; no message contains a payload value. |
| Definition check | Each message of the definitions table for a catch, boundary, start, throw, and end event, at each stage it belongs to and absent at the stages it does not; every row of the type table, both ways; paths equal to a field, with an index, below a `json` field, and below a scalar; required with and without a default; `instanceId` and `businessKey` sources, with and without `required`; a variable declared twice with different types; `allowWorkflows`; archived; `required` mode at publication with and without an entry, and absent at the draft and reactivation stages; node order and the first problem. |
| Save and publish | A draft with a missing required field saves and its publication is refused; a draft with an unknown field is refused; in `required` mode a draft with an uncataloged name saves and its publication is refused; a definition without signal events runs no catalog statement (command count); the AI proposal lists draft-stage problems as save blockers and the rest as publication blockers; `SetDefaultAsync` repeats the readiness check. |
| References | `AddAsync` writes one row per signal event; a deleted version cascades; the migration's backfill equals the rows `AddAsync` writes for every example definition with signal events. |
| Catalog writes | Create, update, archive, reactivate, stale `409`, taken name `409`; a change refused with the live versions named and allowed when only a draft uses the name; a change not blocked by another name's problem in the same version; creation for a name in use; the write stops at the fifth problem while the preview lists all; archive refused by each of the five blockers and allowed after they end; usage counts; uncataloged names. |
| Reactivation | The preview reports `signal_catalog_mismatch` for a completed instance whose version no longer fits, and the commit refuses it; a version that fits reactivates as today; a version without signal events runs no catalog statement. |
| Throws | A payload that does not fit returns `400` and writes no broadcast or delivery; with the open-broadcast limit reached, a payload that does not fit still returns its `400`; `403` for a user without the entry's role and for a client when clients are not allowed; archived; `required` mode refuses a name without an entry and `optional` throws it as today; a repeated key replays a broadcast thrown before its entry existed; the hash of a recorded request is unchanged. |
| Cancel and retry | Retry returns `403` under the throw rules. Cancel returns `403` to another user without the role, and succeeds for the thrower without it and for a client whose entry turned off `allowClients`. |
| Runtime | Deliveries to waits and starts, and workflows' throws, run no `signal_catalog` statement (command capture), also in `required` mode. |
| Races | A catalog write holding the exclusive name lock makes a publication wait, and the publication then checks the new entry; an archive and a publication; a throw and an archive; a throw and a change; a reactivation and a change. |
| Authorization | Readers through each of the three role settings and both client scopes; `401` without a token; `403` for a user with only `Signals.RequiredRole` on the write routes; `canThrow` for each caller kind; the existing shared-variable and signal policies unchanged. |
| Migration | Up, then down, then up; down is refused while an entry exists; the seeded setting is inserted when missing and left alone when present; `MigrationDowngradeCannotRemovePermanentPruningMarkers` still passes. |
| API | The OpenAPI document lists the nine routes with their schemes and DTOs. |
| Editor | `EditorSignalCatalogTests` (Jint): without the capability nothing changes; merged suggestions with labels; throw events list only entries that allow workflows; each note; a description and a field name containing `<img src=x onerror=alert(1)>` appear as text and create no element; **Add missing fields** for each mode as one undoable change that marks the document dirty once, with the variable left empty for a field name with `-`; a reply never dirties the document; the entry is fetched once per name. `WorkflowEditorHostSignalCatalogTests`: a failed call answers `unavailable` for both methods. |
| Flowbit.Ui | `SignalCatalogPageTests`: form checks, both warnings, review required before save, an edit ends the review, `409` keeps the form, the archive panel, the identity guard, and catalog text with markup rendered encoded. `SignalsPageTests`: the catalog panel, the template, the review row, cancel offered to the thrower, and that a catalog `403` does not clear the page while a lost identity still does. `WorkflowEditor` replies for each status and the size of the list reply. |

Browser smoke suite, which has no Worker and publishes no signal definition. Each scenario uses signal names of its own, because the stack and its catalog are shared:

- **R19, Signal catalog page**, at 1440×900, 1024×768, and 390×844. An administrator creates an entry with three fields and a role with real typing and clicks, sees both warnings, reviews, saves, edits a description, archives it, filters to **Archived**, and reactivates it. An invalid field name is refused before any request. The page does not overflow horizontally.
- **R20, Signals page with the catalog.** The name list offers the entry; the panel shows its fields; **Insert payload template** fills the box; a throw with a misspelled member shows the `400` message; with an entry whose role the administrator lacks, the throw shows the `403` message and the page keeps its data.
- **H19, hosted editor catalog block.** The name field offers the entry; the block shows its fields; **Add missing fields** adds the rows and undo removes them; saving a draft that uses an archived name shows the API's message; a draft with a missing required field saves.

Worker acceptance suite:

- **S16, live versions protect their contract**, at 1440×900. [Example 01](../../examples/signals/01-signal-catch-policy-change.json) published under its own family with waiting instances. An entry that fits is created from the page; changing the mapped field's type is refused with the version named; archiving is refused with the wait count; a throw from the **Signals** page that fits reaches every wait.

Then follow [mandatory real-browser verification](../../AGENTS.md#mandatory-real-browser-ui-verification) by hand on a local stack with published API, UI, and Worker hosts:

1. Create, review, and save an entry on the **Signal catalog** page with the keyboard only, including the field table.
2. Publish Example 01 and confirm that a change it does not fit is refused and the preview lists it.
3. On **Signals**, throw with a template payload, then with a misspelled member, then as a user without the entry's role, and cancel a broadcast as its thrower after the entry's roles change.
4. In the hosted editor, use the name suggestions and **Add missing fields** on a catch and a throw, and save.
5. Set `Signals.CatalogMode` to `required` and confirm that a draft with an uncataloged name saves, its publication is refused, and the page lists the name.
6. Complete an instance, publish a newer default, unpublish the instance's version so that it is no longer live, change the entry so that version no longer fits, and confirm that the reactivation preview shows the blocker and the commit refuses it.
7. Check the three sizes and the console on each page.

Keep screenshots of the catalog form with its warnings, the refused change, the **Signals** catalog panel, and the editor block.

Regression commands, run from the repository root with the same arguments in PowerShell 7 and Bash:

```text
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet
dotnet publish Flowbit/src/Flowbit.Api/Flowbit.Api.csproj -c Release -o artifacts/browser/hosts/api /p:UseAppHost=false
dotnet publish Flowbit/src/Flowbit.Ui/Flowbit.Ui.csproj -c Release -o artifacts/browser/hosts/ui /p:UseAppHost=false
dotnet build Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore
git diff --check
```

Full solution runs have had order-dependent failures at the base commit, so compare failures against a base run before attributing one to this change. Rerun the signal acceptance scenarios S1 to S15 once on the final build, because every throw and publication now takes a name lock. Before setting the status to Done, walk this table case by case against the tests that exist.

### 11. Delivery sequence, rollout, and risks

1. **Contract.** `SignalCatalogEntryRules`, `SignalCatalogContract` with its stages and type table, the DTOs, and the mode. Exit with the rules and contract tests green.
2. **Persistence.** The migration with its backfill and seeded setting, the entities, `SignalCatalogRepository`, the name locks, and the references written by `AddAsync`. Exit with the schema, backfill, and migration tests green.
3. **Definition checks.** The readiness methods and their wiring, `EnforceSignalCatalogAsync`, the reactivation check, and the AI blockers. Exit with the save, publish, and reactivation tests green and every existing signal test unchanged.
4. **Catalog API.** The routes, the policy, the live-version check, archive, preview, usage, and uncataloged names. Exit with the catalog-write, race, and authorization tests green.
5. **Throws.** The catalog step in `ThrowAsync` and the checks in cancel and retry. Exit with the throw, cancel, and runtime tests green.
6. **Flowbit.Ui.** The catalog page and the **Signals** page. Exit with the component tests green.
7. **Hosted editor.** The capability, the block, and **Add missing fields**. Exit with the editor and host tests green.
8. **Documentation.** The owning pages of [section 9](#9-documentation-and-examples).
9. **Verification.** R19, R20, H19, S16, the reruns, and the manual checks. Record the evidence under [Current state](#current-state), then set the status to Done here, in the [backlog](README.md#event-04-follow-up-subtasks), and in the [parent](event-04-signal-events.md#later-releases).

Rollout:

1. Apply `AddSignalCatalog`. It scans `workflow_definitions` once for the backfill and seeds `Signals.CatalogMode`. Nothing behaves differently yet.
2. Upgrade every API, UI, and Worker replica together. Mixed API replicas are unsupported: an old one publishes, reactivates, and throws without the catalog.
3. Create entries, starting with the names the page lists as in use. Each creation is checked against the live versions that use the name. Add every entry role to `Signals.RequiredRole` as well.
4. Optionally set `Signals.CatalogMode` to `required` once the list of uncataloged names is empty.
5. To roll the binaries back, first accept that entry roles and `allowClients` stop applying. Either keep the schema and roll back the binaries only, or delete the entries with SQL so the down migration can run.

| Risk | Guard |
| --- | --- |
| A catalog entry and a definition change at once, and a live version ends up not fitting. | Both sides take the name lock and check under it; race tests hold the lock across the other write. |
| A reactivated instance brings back a version that no longer fits. | The preview and the commit refuse it with `signal_catalog_mismatch`, under the name locks. |
| Creating an entry for a name that many versions use is refused, so adoption stalls. | The preview and the refusal name the versions and their problems; the page lists the uncataloged names first, so administrators can see the work before they start. A field of type `json` and an optional field accept what a strict field would not. |
| Authors cannot save work in progress. | Drafts are refused only for problems that are wrong at any stage; completeness and the `required` mode wait for publication. |
| `required` mode stops publications by authors who did not know about the catalog. | It is off by default; the editor's block says when a name needs an entry; the publication message names the requirement. |
| An entry's roles lock everyone out, because users need a role from `Signals.RequiredRole` as well. | The page warns when no entry role is in `Signals.RequiredRole`; the guides and the rollout say to add the roles there. |
| Entry roles are bypassed through a client or a workflow. | `allowClients` and `allowWorkflows` close both doors; the page warns when roles are set and clients are still allowed; the guides say that publishing workflows is the authority behind a workflow's throw. |
| A thrower cannot stop a mistaken broadcast after the entry changes. | The thrower may always cancel its own broadcast. |
| An administrator's catalog text runs as script in the editor or a page. | Catalog text is set as text or element properties, never through `innerHTML`; Razor encodes it; tests put markup in a description and a field name. |
| The editor's catalog reply grows to megabytes on a large catalog. | The list carries names, short descriptions, and flags, about 150 KB at most; fields come per entry, when a node uses it. |
| A catalog write holds the name lock while it loads many versions, and throws or saves of that name wait. | The write loads pages of 50 and stops once it has found more than five problems; the full list comes from the unlocked preview. |
| A catalog read slows throws or saves. | One advisory lock and one indexed read per throw, and per signal name per save; nothing at runtime. A command-count test pins it. |
| The catalog's own failure stops running instances. | Invariant 2: no runtime path reads it, and a test captures the commands of deliveries and workflows' throws. |
| A refusal message leaks a payload value. | Messages are built from names and types only, and a test scans them for the values it sent. |
| The **Signals** page clears itself on a catalog `403`, as if the identity were lost. | The page tells a refused request from a lost identity, and a component test covers both. |
| Tests or browser scenarios break each other through the shared catalog or mode. | Unique names per test and scenario, the mode restored in `finally`, and live versions created through the API. |
| A name that ends with `.` cannot be addressed behind a proxy that trims trailing dots. | The API reference and the deployment guide say so. Such names are legal today and stay so. |
| Rolling back silently removes the roles that narrow a throw. | The down migration refuses while entries exist, and the deployment guide states the consequence. |
| Hash collisions of the advisory lock key make unrelated names wait for each other. | A collision only adds waiting, never a wrong result, as with the family lock. |
