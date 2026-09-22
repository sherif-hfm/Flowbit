# Stage 11 — Administrative-action display components

[Plan index](README.md) · [Remaining-gaps roadmap](remaining-gaps-implementation-plan.md) · [Gap inventory](gaps.md)

**Status: In progress — the extraction and review fixes are implemented;
Windows/Linux tests, the no-Worker suite, and the Worker-enabled scenarios in
the [review follow-up](#review-follow-up--2026-09-22) passed locally.
Stage 5's browser acceptance and the residual visual/timing evidence listed
there remain open.** The initial plan was prepared on 2026-09-21
against commit `5403136`, with a clean working tree before this documentation
change. This plan is based on the current page, DTOs, CSS, tests, and Stage 5/7
acceptance records. No application tests or browser checks were run while
writing the initial plan; completed validation is recorded in the dated
implementation and review records below.

## Objective and scope

Extract three display components from
[AdministrativeActions.razor](../../Flowbit/src/Flowbit.Ui/Components/Pages/AdministrativeActions.razor):
`AdministrativeBatchAudit`, `AdministrativeBatchItems`, and
`AdministrativeBatchHistory`. Keep the existing `/administrative-actions`
screen, its appearance, and its operational behavior unchanged.

The page continues to own fetching, filters, pagination, population selection,
input validation, batch preparation/confirmation/cancellation, identity checks,
polling, and disposal. Children receive existing DTOs and presentation values;
the history component reports a clicked batch ID through `EventCallback<long>`.
No child acquires its own API client, identity, or request lifecycle.

This stage does not extract the discovery/search/action forms, redesign page
state, split `WorkflowApiClient`, change other management pages, or modify the
separate instance-detail `InstanceAdministrativeActions` command component.
Routes, HTTP DTOs, authorization, workflow JSON, persisted data, and Worker
semantics remain unchanged. No migration or new production/test package is
planned. Success is a clear ownership boundary with preserved behavior, not a
target page length.

## Prerequisites and implementation gate

Planning and read-only inspection can proceed now. Begin production extraction
only after both conditions from the remaining-gaps roadmap are satisfied:

1. The Stage 7 real-browser harness is available and its baseline passes on the
   implementation checkout. It currently exists as the standalone
   [Flowbit.BrowserTests project](../../Flowbit/tests/Flowbit.BrowserTests/README.md),
   outside both solutions. Its documented baseline contains 27 cases: 23 product
   scenarios across E1–E8/R1–R6 and four harness regressions. Reconfirm the actual
   count and results; this historical count is not an acceptance result.
2. Complete and record
   [Stage 5's browser acceptance](stage-05-instance-detail-components.md#required-browser-verification),
   using the detailed
   [Stage 7 evidence checklist](stage-07-browser-smoke-and-stage-05-acceptance.md#completing-the-outstanding-stage-5-gate).
   It remains open in this checkout, including headed visual evidence,
   before/after comparison, attribution, genuine Worker-driven batch links,
   and administrative-action refresh. Preserve that checklist as the owner of
   its exact requirements. Do not equate a smoke-suite pass with completion.

Record the prerequisite evidence before changing the page. Keep Stage 7's
unobserved remote CI result separate from Stage 5 acceptance; do not silently
mark either complete as part of this plan. If a required gate cannot run,
record what is unavailable and leave Stage 11 planned or in progress.

## Current implementation and extraction map

The page currently has 1,379 physical lines, with its `@code` block beginning
at line 545. These are inspection anchors at `5403136`; re-find the named
sections before implementation rather than relying on fixed line numbers.

| Current region | Proposed owner | Retained page boundary |
| --- | --- | --- |
| Current-batch section and heading, lines 375–389 | Page | Null guard, `summary` used by controls, section wrapper, `admin-current-batch-heading`, status badge, and Refresh button. |
| Summary grid, Frozen request, and optional preparation issues, lines 390–419 | `AdministrativeBatchAudit` | Page supplies the current detail DTO only when non-null. |
| Confirm and cancel controls, lines 420–430 | Page | Eligibility, count/timestamp concurrency fence, pending flag, cancellation input and request. |
| Paged item results heading and status filter, lines 432–439 | Page | Heading text, total count, selected status and change handler. |
| Item loading/empty/table branches, lines 440–485 | `AdministrativeBatchItems` | Page supplies the current items collection, preserving null versus empty. |
| Item pager, lines 486–493 | Page | Same visibility condition, total-page calculation, busy guards and handlers. |
| Recent batches section and filter, lines 497–503 | Page | Section wrapper, `admin-batches-heading`, filter options/state and handler. |
| Recent-batch loading/empty/table branches, lines 504–533 | `AdministrativeBatchHistory` | Page supplies rows and active batch ID; handles the typed Open callback. |
| Recent-batch pager, lines 534–541 | Page | Same visibility condition, totals, busy guards and handlers. |

The components do not need to own an entire panel. Moving only these display
regions keeps control placement, DOM nesting, and page coordination intact.
After splitting the current `else` blocks, render each page-owned pager only
when its corresponding `PagedResult` is non-null and `Items.Count > 0`, exactly
as today. Neither a loading nor an empty view gains a pager.

The contracts come from
[AdministrativeActionDtos.cs](../../Flowbit/src/Flowbit.Shared/Dtos/AdministrativeActionDtos.cs)
and `PagedResult<T>` in
[WorkflowDtos.cs](../../Flowbit/src/Flowbit.Shared/Dtos/WorkflowDtos.cs).
The current implementation renders batch/item rows in received order; it does
not apply the descending sort used by some Stage 5 history components.

## Target files and component contracts

Use `Flowbit/src/Flowbit.Ui/Components/Shared/AdministrativeBatches/`, with
namespace `Flowbit.Ui.Components.Shared.AdministrativeBatches`. Add its namespace
to [Components/_Imports.razor](../../Flowbit/src/Flowbit.Ui/Components/_Imports.razor).
Proposed contracts are intentionally small:

| File | Parameters | Responsibility |
| --- | --- | --- |
| `AdministrativeBatchAudit.razor` | Required `AdministrativeActionBatchDetailDto Batch` | Nine summary counts, frozen source/action/MI mode/reason, prepared/confirmed actor-role snapshots, common variables, selection JSON, and optional preparation issues. |
| `AdministrativeBatchItems.razor` | `IReadOnlyList<AdministrativeActionBatchItemDto>? Items` | Null loading branch, empty branch, and populated results table, including instance links, action fences, status and result/issue display. |
| `AdministrativeBatchHistory.razor` | `IReadOnlyList<AdministrativeActionBatchSummaryDto>? Batches`, `long? CurrentBatchId`, `EventCallback<long> OnOpenBatch` | Null loading branch, empty branch, recent-batch table, selected-row class, and Open buttons. |
| `AdministrativeBatchFormatting.cs` | Internal pure static methods only | Existing presentation formatting shared by the page and extracted sections. |
| `AdministrativeBatchAudit.razor.css` | None | Audit JSON styling formerly scoped to the page. |
| `AdministrativeBatchItems.razor.css` | None | Item result-cell and JSON styling formerly scoped to the page. |

`AdministrativeBatchHistory` currently needs no isolated stylesheet: its table,
buttons, badges and active-row class use existing global styles. Add a stylesheet
only if the final selector audit identifies an actual owned rule.

Use `[Parameter, EditorRequired]` for the non-null `Batch` input, with the
existing page null guard enforcing its use. Collection inputs deliberately
default to null, which means loading; `[]` means loaded with no rows. Do not
normalize null to an empty collection. DTOs and collections are read-only by
convention: children must not sort them in place, mutate them, or cache the first
parameter value in lifecycle methods.

The intended page wiring is:

```razor
<AdministrativeBatchAudit Batch="currentBatch" />

<AdministrativeBatchItems Items="@(batchItems?.Items)" />

<AdministrativeBatchHistory Batches="@(batchList?.Items)"
                            CurrentBatchId="@(currentBatch?.Summary.Id)"
                            OnOpenBatch="OpenBatchAsync" />
```

These are insertion examples within the retained guards and wrappers, not a
replacement for the page's surrounding markup. The history button should await
`OnOpenBatch.InvokeAsync(item.Id)` through its normal Blazor click handler.
Preserve the existing button text, classes, and enabled behavior. Do not replace
the callback with navigation, fire-and-forget work, or a direct API call.

## Formatting and CSS ownership

Copy the existing pure `ActionTitle`, `ActionLabel`, `ActionKindLabel`,
`PositionKindLabel`, `MultiInstanceModeLabel`, `RoleSnapshot`, and `FormatJson`
methods into `AdministrativeBatchFormatting`. Move their indented Web
`JsonSerializerOptions` with them. Use that helper from the children and the
remaining page presentation through a scoped static import or explicit calls;
retain one implementation of each shared rule.

Preserve exact fallbacks and comparisons. In particular, null MI mode means
`Not applicable` in the audit and `Standard position` in recent batches;
null confirmation means `Not confirmed`; absent roles render `no roles`;
whitespace reason renders `Not provided`. Action-name fallback still selects
the existing flow or timer-boundary name. Do not add friendly labels for new
statuses or change the existing comparison casing during extraction.

Keep `ToString("N0")`, `ToLocalTime().ToString("g")`, role order, JSON
indentation, and ordinary Razor encoding unchanged. Administrative JSON uses
serialization with indentation, whereas `InstanceDetailFormatting.FormatJson`
returns a string value or raw JSON. Reusing the Stage 5 helper would change
presentation and is outside this extraction. Keep `TryParseOptionalJson`,
`TryParseVariables`, `ActionKey`, `PositionKey`, selection helpers,
`TotalPages`, `IsLive`, and `IsCancellable` in the page, along with its remaining
action-card/timer presentation.

Audit
[AdministrativeActions.razor.css](../../Flowbit/src/Flowbit.Ui/Components/Pages/AdministrativeActions.razor.css)
as markup moves. Parent isolated selectors do not automatically style child
component elements.

| Selector or styling | Final owner and action |
| --- | --- |
| `.admin-json` | Copy the same declaration into both Audit and Items isolated stylesheets, then remove it from the page once neither remaining page region uses it. The small duplicated scoped rule preserves ownership without adding global styles or a fourth component. |
| `.admin-result-cell` | Move unchanged to Items. Preserve its 15rem minimum and 30rem maximum widths. |
| `.admin-status-filter` | Keep in the page: both status selects stay there. |
| `.admin-cancellation-input` | Keep in the page with the cancellation input. |
| Warning, definition/selection/action/MI/timer styles and 760px media query | Keep in the page; their markup is not extracted. |
| Global panel, grid, table, badge, button and responsive classes | Reuse as-is. Preserve existing wrappers, sibling order, and classes. |

Do not add wrapper elements just to attach a CSS scope, broaden selectors into
global styles, or introduce `::deep` to compensate for misplaced ownership.
The audit can render its existing sibling blocks as a fragment. Inspect the
generated CSS bundle and actual computed styles after building; compiling Razor
alone cannot establish that selector reach and appearance are preserved.

## Page behavior that must remain unchanged

### Requests, inputs, and identity

Retain every API call and its sequencing in the page. Preserve the exact
workflow family/version/node discovery and action selection; candidate page
size 50; batch item page size 50; recent batch page size 25; explicit selection
and all-matching exclusions; timer eligibility; common-variable parsing; reason
validation; and idempotency-key lifecycle. Children receive the results and
never recreate selection/search requests from visible rows.

`RequestedBatchId` remains a query-supplied parameter read by `LoadInitialAsync`.
This plan does not add query-change handling in `OnParametersSetAsync` or change
URL behavior. Preserve `OpenBatchAsync`, which resets item page to 1, clears its
status and cancellation reason, and then loads items for the selected batch.

Retain `Token.Changed`, `identityEpoch`, `GuardAdministrativeAsync`,
`EnsureIdentity`, `IsCurrentIdentity`, `HandleError`, and
`ClearAdministrativeData` together. The API determines administrator permission;
children do not infer it from role names. Token changes and 401/403 responses
clear the page's administrative state. Responses from an older identity or a
disposed page must not restore data, messages, controls, or loading flags.

Existing refreshes leave loaded rows visible until replacement data arrives.
The new components must not introduce an extra loading flash by interpreting
`refreshingBatch` or `loadingBatches` as an instruction to discard their inputs.
Do not add response ordering, cancellation, or concurrency redesigns within the
extraction. Record independently discovered pre-existing defects separately.

### Confirmation, cancellation, polling, and disposal

Keep `CanConfirmCurrentBatch`, `CanCancelCurrentBatch`, `batchOperation`, all
mutation handlers, and their success/error behavior in the page. Confirmation
uses the displayed summary's eligible count, affected-task count, and exact
`UpdatedAt`; children must not recalculate this concurrency fence. Cancellation
still trims an optional reason and targets the current batch. Rendering a child
must never initiate, duplicate, or retry a mutation.

There is one three-second `PeriodicTimer`, owned by `PollCurrentBatchAsync`.
`IsLive` currently includes `preparing`, `queued`, `running`, and `cancelled`
with null `CompletedAt`. `ready`, completed states, and `failed` do not trigger
poll refreshes. Preserve the `refreshingBatch` guard and dispatcher invocation.
Do not import InstanceDetail's five-second timing or refresh semaphore.

`DisposeAsync` continues to set `disposed`, increment the identity epoch,
unsubscribe from `Token.Changed`, cancel and await `pollTask`, and dispose its
cancellation source. The children need no timers, subscriptions, or disposal.

### Display behavior

- Keep all nine count labels and their current order: Positions, Affected tasks,
  Eligible, Ineligible, Succeeded, Queued, Skipped, Failed, Cancelled.
- Preserve the item's result precedence: nonblank `ErrorDescription`, else
  non-null `Issues`, else non-null `Result`, else an em dash. A present JSON
  null is distinct from an absent nullable `JsonElement` for these branches.
- Preserve supplied row order, IDs, workflow version text, node/flow/token
  references, optional timer subscription, instance link destinations, local
  timestamps, active recent-batch highlighting, and status badges.
- Render only fields currently shown. Job IDs, additional cancellation metadata,
  or other DTO properties do not become new audit fields merely because the
  child receives the detail DTO.
- Preserve heading IDs, `aria-labelledby`, filter labels, native `details` and
  `summary` behavior, loading/empty text, existing status roles, table wrappers,
  keyboard order and responsive scrolling. Add no raw `MarkupString` output.

## Implementation sequence

### 1. Establish evidence before moving markup

Confirm the prerequisite records, checkout, and working-tree changes. Run the
existing focused UI/client tests, complete solution suite, and standalone
browser suite. Capture the original page at 1440×900, 1024×768 and 390×844 with
a populated batch, expanded audit JSON, item results and recent history.
Also record loading/empty states and filter/pager behavior.

Add the assembled-page characterizations below against the inline implementation
first. Use deterministic HTTP gates for delayed requests rather than timing
assumptions. Preserve that baseline and compare post-extraction output and
request records. If an expected invariant already fails, distinguish the
baseline defect from an extraction regression before proceeding.

### 2. Extract audit and its formatting

Create the namespace, pure formatting helper, audit component and its isolated
CSS. Move the summary grid, frozen request, and optional issues verbatim into
the component. Wire it inside the current-batch guard; keep heading, refresh,
confirmation and cancellation outside. Check prepared/confirmed snapshots,
JSON encoding, count order, and ready/cancel controls on the assembled page.
Build and run focused tests before the next extraction.

### 3. Extract item display

Move only the null/empty/table branches and their CSS into Items. Keep the
heading/status select and conditional pager in the page. Pass `batchItems?.Items`
without sorting or copying into child state. Verify populated, empty and
replacement results, each result-precedence branch, exact instance links and
optional timer fences. Verify filtering and paging still issue the same requests.

### 4. Extract recent-batch display

Move its null/empty/table branches into History. Pass the current batch ID for
`table-active`, and bind `OnOpenBatch` to `OpenBatchAsync`. Keep list filters,
page numbers, totals and pager buttons in the page. Exercise an actual Open
button in the browser to prove the selected ID reaches the page and its detail
is loaded; calling a callback delegate directly is insufficient click coverage.

### 5. Verify integration and finish documentation

Remove only unused page formatting/CSS/imports left by the extraction. Review
the diff to confirm that operational methods and request order remain unchanged.
Run the final gates below, inspect CSS and browser evidence, and update ownership
documentation. Record actual commands/counts/artifacts and any unavailable
checks in this document before updating stage status.

Deliver these as buildable review units: characterization; audit/helper;
items; history/callback; final browser evidence and documentation. Each extraction
unit includes its relevant tests and styling, without unrelated behavior fixes.

## Automated verification

### Existing coverage to retain

[InstanceAdministrativeActionsUiTests](../../Flowbit/tests/Flowbit.Tests/InstanceAdministrativeActionsUiTests.cs)
already covers these assembled batch-page behaviors:

- `BatchPageShowsRoleDenialAndDoesNotExposeSelectors`.
- `BatchPageRendersVersionNumbersInDiscoveryCandidatesAuditAndRecentBatches`.
- `BatchIdentityChangeDiscardsOldCatalogAndSelectionData`.
- `OldBatchDiscoveryCannotClearCurrentIdentityActionsOrSelection`.
- `OldBatchDiscoveryCannotClearCurrentIdentityLoadingState`.
- `OldBatchSearchCannotEnableSearchWhileCurrentIdentitySearchIsPending`.

Its batch audit fixture has empty item results. The separate panel's permission
and submission tests do not establish batch-page mutation or polling behavior.
Keep them, but fill the specific page/display gaps below.

Retain
[WorkflowApiClientAdministrativeActionTests](../../Flowbit/tests/Flowbit.Tests/WorkflowApiClientAdministrativeActionTests.cs),
especially `MonitorConfirmCancelAndListUseAffectedCountTimestampAndPagedRoutes`.
Client route/body tests support, but do not replace, assembled-page request
characterization. Retain Stage 5 display, identity and disposal regressions in
the focused gate because its pattern and navigation remain prerequisites.

### Component rendering matrix

Add `AdministrativeBatchDisplayComponentTests.cs` under `Flowbit.Tests`, using
the existing `extern alias FlowbitUi` and `HtmlRenderer` pattern from
[InstanceDetailDisplayComponentTests](../../Flowbit/tests/Flowbit.Tests/InstanceDetailDisplayComponentTests.cs).
No new component-testing package is required. Assert meaningful output and
unchanged DTOs rather than private helper names or full generated HTML snapshots.

| Area | Required cases and assertions |
| --- | --- |
| Audit | Distinct values for all nine counts; normal and timer action labels; both MI modes and null mode; blank/absent reason; unconfirmed and confirmed actors; empty/nonempty role snapshots in supplied order; common variables and selection; optional preparation issues. |
| Items | Null loading, empty loaded, populated rows; deliberately non-sorted input order; ordinary/MI position labels; exact instance href; definition/node/flow/token IDs; timer subscription present/absent; status, affected count and date formatting. |
| Result precedence | Error plus issues/result shows only the error; whitespace error allows issues; issues suppress result; result-only shows Result; neither shows em dash; present JSON null retains its existing branch. |
| History | Null loading, empty loaded, received row order, version/action/MI labels, counts, prepared actor, local date, selected-row match/nonmatch, and one Open control per row. Actual click wiring is verified in the browser. |
| Escaping and formatting | HTML-looking names/reasons/errors/roles and JSON strings remain text; no executable markup. Test JSON indentation and string/null/object/array behavior without depending on machine-specific timezone output. Control test culture or derive expected local dates with the same environment. |
| Parameter replacement | Rerender the same child instance with batch A then B, updated counters/status/actor, replaced items, changed `CurrentBatchId`, and populated → empty → null collections. No previous batch's text, row highlight or JSON survives. |

For replacement coverage, use a small test-only parent that passes mutable test
parameters to a retained child and rerenders on the dispatcher. Rendering two
fresh children cannot prove this behavior. Plain component renders should work
without registering `WorkflowApiClient` or `TokenState`, demonstrating that
display needs no operational services.

### Assembled-page characterization and regressions

Add `AdministrativeActionsPageTests.cs`, reusing the existing recording HTTP
handler/component activation approach without coupling the tests to the proposed
helper layout. Run the initial characterizations before extraction and retain
the same behavior assertions afterward.

| Operation | Request and state contract to assert |
| --- | --- |
| Open a batch | One detail GET followed by one items GET; page 1, page size 50, cleared item status/cancellation reason; no extra recent-list fetch; correct audit and active row after render. |
| Refresh current batch | Detail → items → recent-list requests, once each for one completed refresh; the existing in-flight guard prevents a duplicate refresh. Preserve current filters/pages and list page size 25. |
| Filter or page items/history | One corresponding GET per handler; a changed status resets only its own page to 1; correct status/page/pageSize query; same pager disabled and visibility conditions. Rerender alone issues no requests. |
| Confirm or cancel | One POST and existing items/list refreshes on success. Confirm carries the exact displayed eligible count, affected-task count and `UpdatedAt`; cancellation preserves reason trimming. Re-entry while pending causes no second POST; ordinary failures do not introduce automatic retries. |
| Identity replacement and permission loss | Hold detail/items/list or mutation response, change or clear identity, then release it. Old success or 401/403 cannot restore or clear the replacement actor's current data/flags. Current-identity denial clears audit/items/history and controls. Use a bounded representative matrix across reads and writes, complementing existing discovery tests. |
| Polling | Observe a real three-second poll and complete its detail/items/list sequence. Verify eligible live states and the cancelled-without-completion case; ready/terminal states make no poll requests. Avoid exact counts over uncontrolled elapsed time; gate one cycle and record its requests. |
| Disposal | Start disposal with a detail/items request held, release it, then await disposal (which awaits an in-flight poll task). Verify no stale render, follow-up chain, new polling or token subscription work; inspect renderer exceptions. |

Use `TaskCompletionSource` gates and bounded waits for delayed-response cases.
Keep these tests server-side: Blazor's typed `HttpClient` requests originate in
the UI server, so browser request interception cannot reliably delay them.
Do not add production timing knobs or copy InstanceDetail coordination into
this page to make tests easier.

### Commands

Run from the repository root. These single-line commands work in PowerShell
and Bash. The focused command includes proposed test classes once added; record
the classes/counts actually discovered.

```text
docker info
dotnet test Flowbit/Flowbit.slnx --filter "FullyQualifiedName~InstanceAdministrativeActionsUiTests|FullyQualifiedName~WorkflowApiClientAdministrativeActionTests|FullyQualifiedName~AdministrativeBatchDisplayComponentTests|FullyQualifiedName~AdministrativeActionsPageTests|FullyQualifiedName~InstanceDetailDisplayComponentTests|FullyQualifiedName~InstanceReactivationUiContractTests|FullyQualifiedName~InstanceVariableUpdateUiContractTests" --nologo --verbosity quiet
dotnet build Flowbit/src/Flowbit.Ui/Flowbit.Ui.csproj --nologo
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet
git diff --check
```

Build/publish fresh browser hosts after the final UI/CSS changes; previously
published binaries are not evidence for the current source:

```text
dotnet publish Flowbit/src/Flowbit.Api/Flowbit.Api.csproj -c Release -o artifacts/browser/hosts/api /p:UseAppHost=false
dotnet publish Flowbit/src/Flowbit.Ui/Flowbit.Ui.csproj -c Release -o artifacts/browser/hosts/ui /p:UseAppHost=false
dotnet build Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release
pwsh Flowbit/tests/Flowbit.BrowserTests/bin/Release/net10.0/playwright.ps1 install chromium
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore --logger "trx;LogFileName=stage-11-browser.trx" --results-directory artifacts/browser/stage-11/test-results
```

On Linux, use the browser README's `install --with-deps chromium` form when
system dependencies are needed. Keep the project outside the solutions and
reuse its existing diagnostics, serial execution and identity cleanup. Record
failed/skipped tests and retries separately; do not substitute old pass counts.

## Real-browser acceptance

### Extend the existing no-Worker smoke suite

Add a bounded `AdministrativeActionsSmokeTests.cs` scenario (R7, subject to the
current matrix) and only the HTTP support/fixtures it needs. Keep
`WorkflowDurableProcessing:PublicationEnabled=false` and the existing fixture
lifecycle unchanged. A synchronous instance administrative action creates a
completed one-item audit batch, so this suite can exercise real batch display
without starting a Worker or fabricating a completed database row.

Use a safe synchronous workflow created through HTTP, establish identity through
the real `/token` screen, execute the immediate administrative action through
the UI, and follow its audit batch link. Verify its frozen request, affected
counts, instance result link, JSON details, recent-history row and current-row
highlight. Use two distinct completed audits to click the second row's actual
Open button and prove that audit/items/highlight change to that batch. Exercise
a status filter with zero matches and restore it. Verify the rendered content
against HTTP reads, rather than accepting only a heading or screenshot.

Use real keyboard activation of Open and `details` controls, inspect narrow
table scrolling, and collect console/host diagnostics. Cover 1440×900,
1024×768 and 390×844 with the relevant checks. Update the browser README's
matrix and measured count after adding cases. This scenario verifies display
and callback wiring; it does not satisfy the queued-batch acceptance below.

### Exercise queued batches in a separate full stack

Use the canonical
[isolated setup](../getting-started.md#run-everything-with-docker-compose)
and [UI guide](../ui-guide.md#use-administrative-batches), with API, UI, PostgreSQL and a
Worker sharing the isolated configuration and durable publication enabled.
Use a dedicated Compose project/volume and unused loopback ports, or the
documented host alternative, so acceptance does not operate on an existing
developer database. Record the exact setup and cleanup commands used; retain
needed evidence before removing only acceptance-owned resources.

The default guide's UI address is
`http://127.0.0.1:15152/administrative-actions`; include
`?batchId=<actual-id>` when testing the deep link. Record the actual localhost
URL if using different ports. Seed fixtures through HTTP and exercise page
controls with real clicks, typing, keyboard input and scrolling.

| Scenario | Required browser evidence |
| --- | --- |
| Frozen audit | Prepare ordinary and MI batches with distinguishable source/action, reason, roles, selection and variables. Inspect the frozen request, all counters, both MI labels and unconfirmed/confirmed states. Verify direct and timer-boundary audit labels; timer batches retain the existing no-submitted-variables behavior. |
| Items and pagination | Use more than 50 positions in a batch to exercise both item pages and status filters. Show real successful results and a controlled ineligible/skipped/failed case with issues/error text. Follow the exact instance link and return. Compare all visible columns and overflow with baseline. |
| Recent history | Provide more than 25 batches in the isolated fixture to exercise its own pager. Filter, Open another batch, verify the active row and item-page/status reset, and reopen through a fresh deep-link navigation. Confirm list and item pagination remain independent. |
| Confirm and refresh | Prepare → ready → confirm → queued/running → terminal, using the actual Worker. Verify confirmation counts and the three-second live refresh. Use a controlled Worker pause/start if needed to observe short-lived states; document it. No duplicate action or unexpected repeated refresh chain. |
| Cancellation | Cancel a separate ready batch with an optional reason and inspect its cancelled work. Separately cancel a partially executed batch and verify completed-item evidence is preserved while unstarted work is cancelled. Observe a live cancelled batch until `CompletedAt` is set when that state can be held deterministically. |
| Identity and disposal | Replace/clear identity while open, verify denial removes data/controls, reapply an authorized identity, navigate away during refresh, and return. Check for stale data, circuit/disposal errors and duplicate polling; deterministic response races remain covered server-side. |
| Empty and loading | Observe initial loading, no matching recent batches, no items under a selected status, and recovery after filter changes. Existing headings, text, controls and pager visibility match the original. |

Inspect the populated audit, items and recent-history sections at **1440×900**,
**1024×768** and **390×844**, including expanded JSON, buttons, focus order,
headings and horizontal table overflow. Capture before/after screenshots of
equivalent states. Preserve the original responsive behavior; unrelated mobile
improvements need a separate change.

Inspect browser console errors and warnings, uncaught errors, failed browser
requests and UI/API/Worker logs. Use server-side recording/tests or host logs
for typed-client request counts; browser network activity alone is insufficient.
Record browser/version, URL, viewport, scenario, fixture/batch IDs, screenshot
paths, results and limitations. The existing browser suite, `HtmlRenderer`,
Jint and DOM stubs do not replace this scenario-specific acceptance.

## Documentation and acceptance criteria

During implementation, update only affected ownership/coverage explanations:

| Document | Required review/update |
| --- | --- |
| This plan | Append final file/parameter ownership, implementation commit(s), prerequisite evidence, commands and actual results, browser artifacts and remaining limitations. |
| [Refactoring index](README.md), [roadmap](remaining-gaps-implementation-plan.md), [gaps](gaps.md), [documentation home](../index.md) | Keep Stage 11 links/status consistent. Record this bounded UI extraction while leaving the other management pages and client split deferred. Preserve historical measurements as dated evidence. |
| [AGENTS.md](../../AGENTS.md#blazor-ui-pages) and [runtime reference](../../Flowbit/README.md) | Add or update the applicable UI architecture description: three display components, typed Open callback, and operational ownership retained by the page. Do not claim the current page is extracted before implementation. |
| [Browser README](../../Flowbit/tests/Flowbit.BrowserTests/README.md) | Add the actual administrative display scenario, coverage boundaries and new measured suite count; keep no-Worker and full-stack evidence separate. |
| [UI guide](../ui-guide.md) | Re-exercise management actions/deep links and review text/screenshots. No public behavior change is intended; edit only an explanation/image made inaccurate by the final implementation. |

No API, node-authoring, migration, example-schema or deployment contract change
is expected. Review related guidance when validating the full stack, but avoid
unrelated edits. Validate changed relative links, anchors and image references;
if fixtures introduce JSON files, parse them and verify their actual API use.

Stage 11 is accepted only when all of these are true:

- [ ] Stage 5 acceptance and a current passing Stage 7 harness baseline are recorded.
- [x] All three components use the documented DTO/scalar/callback contracts and
  contain no API, identity, selection, mutation, polling or disposal ownership.
- [x] The page retains all operational state, request order/counts and control
  placement, including conditional pagers and confirmation concurrency values.
- [x] Baseline characterizations, new component/page tests, required existing
  regressions, full solution tests, UI build and standalone browser tests pass.
- [ ] Scoped CSS applies to the actual child markup, and all specified viewport,
  keyboard, callback, JSON, empty-state and queued-batch checks are complete.
- [x] Identity replacement and disposal cannot revive stale batch data; no
  additional API calls, mutations or timers originate from children.
- [x] Documentation, links, artifacts and `git diff --check` are verified.

The implementation report must distinguish automated results from real-browser
results, state the exact test commands/counts, and identify the documentation
updated. Include screenshots if appearance changed; even when appearance is
preserved, retain before/after evidence. Unavailable required checks leave the
acceptance gate open. Saving this plan completes planning only.

## Rollback

Revert the Stage 11 extraction commits and rebuild/republish the UI. Restore
markup, formatting and isolated CSS together so the former page receives its
styles again. Keep applicable behavioral characterizations and remove or adapt
tests/imports that directly require the reverted components. Revert dependent
Stage 11 commits in reverse order, without undoing unrelated Stage 5/7 work.
No data conversion, API change, Worker upgrade or database rollback is required.

## Implementation record (2026-09-21)

The extraction was implemented in the working tree on top of `5403136` together
with the plan document itself; the plan-writing documentation changes and the
implementation remain uncommitted working-tree changes. The user explicitly
approved proceeding with the extraction while Stage 5's browser acceptance
stays open, which this record keeps visible; Stage 11 is therefore **in
progress**, not implemented.

### Prerequisite evidence recorded before the extraction

- **Stage 7 harness baseline on this checkout (passed).** Fresh Release
  publishes of the API/UI hosts, then
  `dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj
  -c Release --no-build --no-restore` — **27 passed, 0 failed, 0 skipped**
  (1 m 14 s). TRX:
  `artifacts/browser/stage-11/baseline/test-results/stage-11-baseline-browser.trx`.
- **Focused UI/client baseline (passed).**
  `dotnet test Flowbit/Flowbit.slnx --filter "FullyQualifiedName~InstanceAdministrativeActionsUiTests|FullyQualifiedName~WorkflowApiClientAdministrativeActionTests|FullyQualifiedName~InstanceDetailDisplayComponentTests|FullyQualifiedName~InstanceReactivationUiContractTests|FullyQualifiedName~InstanceVariableUpdateUiContractTests"`
  — **57 passed, 0 failed, 0 skipped** (11 s).
- **Full solution baseline (passed).** `dotnet test Flowbit/Flowbit.slnx
  --nologo --verbosity quiet` — **1,950 passed, 0 failed, 0 skipped**
  (4 m 33 s).
- **Stage 5 browser acceptance: still open and not claimed.** Its residual
  rows (headed per-state screenshots, a real catalog-backed shared-binding
  walkthrough, Worker-driven version/variable-update batch links,
  claims/delegation attribution, administrative-action refresh with the real
  batch link, the pre-extraction visual comparison, and native-picker
  evidence) were not collected in this session. The working tree keeps
  `stage-05-instance-detail-components.md` and the Stage 7 checklist open.

### Original-page evidence before the extraction

The new `AdministrativeActionsSmokeTests` R7 scenario ran against the inline
implementation before the extraction and passed **3/3** after two
assertion-shaped iterations (the "Prepared by" label and value are separate
`dt`/`dd` elements; the recent-batch list is not workflow-filtered, so
scenario-local batches must be asserted by identity rather than exact totals).
It executes real immediate administrative actions through the instance-detail
panel, follows the real `View audit batch #N` link, and verifies the frozen
request, count order/values, item row, highlight, filters, and the Open
callback against HTTP reads. It captured the original page at 1440×900,
1024×768, and 390×844 (baseline run
`artifacts/browser/runs/20260921-170358-64e02857/`):
`administrative-batch-audit-1440x900.png`,
`administrative-batch-audit-1024x768.png`, and
`administrative-batch-audit-390x844.png`. Zero-match item and recent-batch
status filters plus restore are covered; loading states remain covered by the
HtmlRenderer tests because they are transient. Chromium 151.0.7922.34
(headless) drove the scenarios; console/host diagnostics came from the
harness (`diagnostics.json` in each scenario directory, zero page errors,
console errors, or failed requests on these scenarios).

### New automated coverage

- `AdministrativeActionsPageTests.cs` (14 cases) — assembled-page
  characterization against the inline implementation first, retained through
  the extraction: open-batch state reset and detail→items request order with
  no recent-list fetch plus the active-row highlight; refresh's detail→items→list sequence with the
  in-flight `refreshingBatch` guard; item/list filters resetting only their
  own page and issuing one request each; a rerender issuing no requests;
  pager visibility for populated views, the disabled Previous/Next guards,
  and the item pager hiding under a zero-match filter while the list pager
  stays; confirm carrying the displayed eligible count, affected-task count, and
  `UpdatedAt`; cancel trimming the optional reason (and sending null for
  whitespace); single-POST under re-entry; a failed confirm showing the error,
  issuing exactly one POST, and starting no automatic retry or follow-up
  refreshes; held detail/items/confirm responses
  after identity replacement restoring nothing; a denied detail clearing data
  and controls; real three-second polling for queued and cancelled-without-
  completion states with no polls for ready/terminal states; and disposal
  during a held detail stopping the follow-up chain.
- `AdministrativeBatchDisplayComponentTests.cs` (37 cases) — component
  rendering matrix per the plan: nine count labels in order with distinct
  values; direct/timer action-title fallbacks; null and both MI modes; blank,
  absent, and exact reason text; unconfirmed/confirmed actor snapshots;
  empty/ordered role snapshots; common variables, selection, and escaping
  (System.Text.Json's default encoder emits `\u003C`/`\u0026`, so escaping
  assertions target the raw HTML while content assertions decode entities);
  optional preparation issues; null/empty/populated item branches; supplied
  order preserved; ordinary/MI position labels; exact instance hrefs;
  definition/node/flow/token fences; optional timer subscriptions; status,
  affected count, and same-environment local dates; all six result-precedence
  branches including present JSON null; inert error text; history
  null/empty/populated branches, row order, labels, counts, prepared actor,
  local dates, highlight match/non-match, one Open control per row; and
  audit/items/history parameter replacement through test-only parents
  rerendering retained children.

### Extraction result

`Flowbit/src/Flowbit.Ui/Components/Shared/AdministrativeBatches/`
(namespace `Flowbit.Ui.Components.Shared.AdministrativeBatches`, wired through
`Components/_Imports.razor`):

| File | Contract |
| --- | --- |
| `AdministrativeBatchAudit.razor` + `.razor.css` | `[Parameter, EditorRequired] AdministrativeActionBatchDetailDto Batch`; nine counts, frozen request, common variables/selection JSON, optional issues; owns `.admin-json`. |
| `AdministrativeBatchItems.razor` + `.razor.css` | `IReadOnlyList<AdministrativeActionBatchItemDto>? Items`; null loading, empty, populated table; owns `.admin-result-cell` and `.admin-json`. |
| `AdministrativeBatchHistory.razor` | `IReadOnlyList<AdministrativeActionBatchSummaryDto>? Batches`, `long? CurrentBatchId`, `EventCallback<long> OnOpenBatch`; null loading, empty, table, `table-active` highlight, Open buttons calling `OnOpenBatch.InvokeAsync(item.Id)`; no isolated stylesheet. |
| `AdministrativeBatchFormatting.cs` | Internal pure `ActionTitle`, `ActionLabel`, `ActionKindLabel`, `PositionKindLabel`, `MultiInstanceModeLabel`, `RoleSnapshot`, `FormatJson`, and the indented Web `JsonSerializerOptions`; shared by the children and the page through a `@using static` import. |

The page keeps every operational method and request sequence unchanged
(1,379 → 1,273 physical lines). Retained page-owned helpers: `TryParseOptionalJson`,
`TryParseVariables`, `ActionKey`, `PositionKey`, `PositionReference`,
`ShortActivation`, `VariableContractLabel`, `TimerDefinitionLabel`,
`TimerStateDetail`, `TotalPages`, `IsCancellable`, `IsLive`, and all
action-card/timer presentation. Page CSS dropped only `.admin-json` and
`.admin-result-cell` (now owned by the children); `.admin-status-filter`,
`.admin-cancellation-input`, and the warning/definition/action/MI/timer
styles and the 760 px media query stay in the page. Pagers render only when
their `PagedResult` is non-null with `Items.Count > 0`, exactly as before;
neither the loading nor the empty view gained a pager.

### Post-extraction gates

- **Focused (passed).** The plan's focused command with the new classes —
  **108 passed, 0 failed, 0 skipped** (25 s).
- **Full solution (passed).** — **2,001 passed, 0 failed, 0 skipped**
  (4 m 29 s).
- **UI build (passed).** `dotnet build Flowbit/src/Flowbit.Ui/Flowbit.Ui.csproj
  --nologo` — 0 errors.
- **Standalone browser suite (passed with one pre-existing flake).** Fresh
  publishes, then the full 30-case suite — **29 passed, 1 failed** on the
  first run: `R6_ResponsiveControlsAndFocus(390×844)` hit the documented
  pre-existing R6 scroll-timing flake (instance-detail section scrolling,
  unrelated to this page) and passed **2/2 on an immediate retry**.
  TRX: `artifacts/browser/stage-11/final/test-results/stage-11-final-browser.trx`.
  R7's post-extraction screenshot
  (`artifacts/browser/runs/20260921-195116-e8a72efa/r7-administrative-batches/administrative-batch-audit-1440x900.png`)
  is visually identical to the pre-extraction baseline: the child-scoped
  `.admin-json` and `.admin-result-cell` styles apply to the child markup, the
  Open callback works through real clicks and keyboard activation, and the
  active-row highlight, status filters, and instance links are unchanged.

### Limitations recorded on 2026-09-21 (see review follow-up below)

- The Worker-driven full-stack acceptance below (queued batches with real
  Worker confirm/cancel/pagination states, frozen audits for ordinary and MI
  batches with more than 50 items, >25 recent batches exercising the history
  pager, controlled ineligible/skipped/failed evidence, live polling of a
  short-lived state, and identity/disposal checks on a durable stack) has not
  run.
- Stage 5's browser acceptance rows remain open and untouched by this change.
- The `browser-smoke` CI job has not been observed remotely.
- R7's zero-match status-filter cases assume no other failed batches exist in
  the run's disposable database; other smoke scenarios create none today, and
  the scenario asserts its own batches by identity for the populated cases.

No unrelated documentation edits are included. The updated ownership
descriptions live in [AGENTS.md](../../AGENTS.md#blazor-ui-pages),
[Flowbit/README.md](../../Flowbit/README.md#projects),
the [browser README](../../Flowbit/tests/Flowbit.BrowserTests/README.md),
the [refactoring index](README.md), the
[roadmap](remaining-gaps-implementation-plan.md), the
[gap inventory](gaps.md), the [documentation home](../index.md), and this
document. The [UI guide](../ui-guide.md) needed no change: the screen's
behavior, appearance, and walkthroughs are unchanged.

## Review follow-up — 2026-09-22

The review found a Windows-specific test assertion, insufficient polling and
identity/disposal characterizations, and a pre-existing page defect: timer
refreshes updated fields without rendering them. The follow-up fixes the
page-owned timer callback to check eligibility on the renderer dispatcher,
await the existing detail → items → history refresh, and request a render unless
the page was disposed. Permission loss also renders the cleared display.
No child takes on operational ownership, and request order, the three-second
interval, confirmation fences, and the retained-rows refresh behavior are unchanged.
This is an intentional bug fix after the otherwise behavior-preserving extraction;
the historical claim that operational methods were unchanged applies to the
2026-09-21 extraction only.

The component test now checks actual indentation using `Environment.NewLine`
and pairs all nine count labels with their distinct values. The assembled-page
coverage is now **36 cases** (plus **37 component cases**): gates are reset when
armed; each live state completes one gated detail/items/history cycle and must
update the existing rendered status, counts, results, and history without a
test-triggered render; ready/terminal states make no poll requests; 401/403
from the current poll clear the display. A 12-case matrix releases stale
200/401/403 detail/items/history/confirm responses while a replacement actor's
populated batch and real request remain pending. Disposal waits for a held
detail or items poll, then ignores later ticks and token changes. The separate
rerender assertion now calls `StateHasChanged` and verifies changed output.

R7 identifies recent batches by an exact first-cell ID instead of substring
matching, and its README now includes the fifth runtime fixture. The
[UI guide](../ui-guide.md#use-administrative-batches) documents automatic visible
refresh and its stopping conditions; [AGENTS.md](../../AGENTS.md#blazor-ui-pages)
records page-owned rendering and the disposal guard.

### Automated follow-up results

Run from the repository root:

```text
dotnet test Flowbit/Flowbit.slnx --no-restore --filter "FullyQualifiedName~AdministrativeActionsPageTests|FullyQualifiedName~AdministrativeBatchDisplayComponentTests" --nologo --verbosity quiet --logger "trx;LogFileName=stage-11-fixes-focused.trx" --results-directory artifacts/review/stage-11/fixes
dotnet test Flowbit/Flowbit.slnx --no-build --no-restore --nologo --verbosity quiet --logger "trx;LogFileName=stage-11-fixes-full.trx" --results-directory artifacts/review/stage-11/fixes/full
dotnet publish Flowbit/src/Flowbit.Ui/Flowbit.Ui.csproj -c Release --no-restore -o artifacts/browser/hosts/ui /p:UseAppHost=false --nologo --verbosity quiet
dotnet publish Flowbit/src/Flowbit.Worker/Flowbit.Worker.csproj -c Release --no-restore -o artifacts/browser/hosts/worker /p:UseAppHost=false --nologo --verbosity quiet
dotnet build Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-restore --nologo --verbosity quiet
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore --nologo --verbosity quiet --logger "trx;LogFileName=stage-11-fixes-browser.trx" --results-directory artifacts/review/stage-11/fixes/browser
```

- Focused: **73 passed, 0 failed, 0 skipped**, 45 seconds.
- Full solution: **2,023 passed, 0 failed, 0 skipped**, 4 m 59 s.
- UI/Worker publishes and browser build: passed. The API publish was unchanged
  from the fresh review build on the same checkout.
- No-Worker Chromium: **30 passed, 0 failed, 0 skipped**, 1 m 44 s; no retries.
  Chromium 151.0.7922.34 used `http://127.0.0.1:49927/administrative-actions`.
  R7 retained real clicks, keyboard Open/details, filters and table scrolling
  at 1440×900, 1024×768 and 390×844. This is separate from Worker acceptance.
- Linux reproduction: the same portable test assemblies ran in the cached
  `mcr.microsoft.com/dotnet/sdk:10.0-noble` container with networking disabled
  and a read-only test mount: **73 passed, 0 failed, 0 skipped**, 45 seconds.
  The previous CRLF failure is resolved. The exact Windows-hosted Docker command is:

```powershell
docker run --rm --network none --read-only --tmpfs /tmp --env DOTNET_CLI_HOME=/tmp/dotnet-home --env DOTNET_CLI_TELEMETRY_OPTOUT=1 --env DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1 --mount 'type=bind,source=D:\projects2026\business process workflow\Flowbit\tests\Flowbit.Tests\bin\Debug\net10.0,target=/tests,readonly' mcr.microsoft.com/dotnet/sdk:10.0-noble dotnet vstest /tests/Flowbit.Tests.dll '--TestCaseFilter:FullyQualifiedName~AdministrativeBatchDisplayComponentTests|FullyQualifiedName~AdministrativeActionsPageTests' '--ResultsDirectory:/tmp/test-results' '--logger:console;verbosity=minimal'
```

Existing SSH.NET dependency-audit and unrelated analyzer warnings remain; no
production or test package was added by the follow-up.

### Worker-enabled browser evidence

A separate local acceptance runner reused the browser project's published-host,
identity-screen, HTTP fixture client, Chromium and Testcontainers dependencies.
It did not change the no-Worker suite or its configuration. The runner created
its own PostgreSQL container/database with generated credentials, launched the
published API with durable publication enabled, and started/stopped only its
own Worker processes to expose Preparing and Queued states. Setup and fixture
changes used authenticated HTTP. A separate loopback HTTP service held the
second action of a four-position batch to exercise partial cancellation.
Owned hosts and the database were disposed at exit; no developer database or
Compose volume was used. Runner and evidence are local ignored artifacts.

```text
dotnet build artifacts/review/stage-11/fixes/WorkerAcceptance/WorkerAcceptance.csproj --no-restore --nologo --verbosity quiet
dotnet run --project artifacts/review/stage-11/fixes/WorkerAcceptance/WorkerAcceptance.csproj --no-build --no-restore --no-launch-profile
```

The final run passed all **11 scenario/assertion groups**, exit code 0, in
approximately 1 m 32 s. Chromium **151.0.7922.34**, headless, used
`http://127.0.0.1:50544/administrative-actions`; the isolated API was
`http://127.0.0.1:50542`. Final artifacts:
`artifacts/review/stage-11/fixes/worker-20260922-084020/`.
Its `evidence.json` records batches 1–34, passed groups, host URLs, and zero
browser page errors, console errors/warnings, or UI host error entries.

- **Batch 1:** real UI Prepare → Preparing → Worker Ready → UI Confirm →
  Queued → Worker Completed for 51 ordinary positions. No click/refresh was
  used to obtain Ready or Completed. Audit counts, item statuses and history
  progress updated visibly. Item pages 1/2, empty status filter, restore,
  exact instance link and browser Back from an explicit batch deep link passed.
- **Batches 2–27:** real prepared batches established a two-page history.
  History paging stayed independent of item paging; keyboard Open, ready
  cancellation with a reason, empty history filtering and restore passed.
- **Batches 28–29:** Force parent for 51 MI positions / 153 affected tasks,
  then Complete all unfinished children for one position / three tasks, both
  completed through the Worker with the expected labels and counts.
- **Batch 30:** timer-boundary execution displayed the authored action label,
  real timer-subscription fence, and empty submitted-variable audit.
- **Batches 31–33:** HTTP fixture actions made one position stale after Ready
  (Skipped), one stale before preparation (Ineligible with expanded Issues),
  and one fail in a controlled downstream script (Failed with error text).
  The skipped error correctly takes precedence over its issue JSON.
- **Batch 34:** the first action completed, the second was held by the local
  service, and the UI submitted cancellation. Releasing the second action
  allowed cancellation to finish: two succeeded, two cancelled, exactly two
  service calls. Completed evidence was preserved.
- Identity replacement and clearing through a second real browser page hid
  restricted data; reapplying the administrator and reopening the deep link
  recovered current data. Navigation away while work was preparing and return
  also passed. Deterministic disposal during a held refresh is covered by the
  server-side tests above.

Screenshots `ordinary-ready-{width}x{height}.png`,
`ordinary-completed-{width}x{height}.png`, and
`partial-cancel-completed-{width}x{height}.png` cover **1440×900**, **1024×768**
and **390×844**, with document-overflow checks. The successful ordinary audit
screens were inspected alongside the no-Worker R7 evidence. No CSS or layout
redesign was introduced.

Earlier runner attempts are retained separately. They exposed a disposed
readiness client in the temporary runner, a Back-navigation assertion made
without first establishing a batch deep link, and an incorrect expectation
that a skipped error would expose its suppressed issue JSON. Another attempt
held a synchronous action beyond the API command timeout: cancellation waited
for its batch lock and timed out. The final run released that action after
cancellation was submitted. This observed existing timing limitation is now
documented in the UI guide; the extraction does not change transaction locks.

### Remaining acceptance evidence

- Stage 5's prerequisite browser checklist remains open; this follow-up does
  not claim to complete that separate acceptance effort.
- A durable Cancelled state with null `CompletedAt` was not held visibly in
  the browser: the synchronous action's transaction blocked cancellation
  until release. Its polling/rendering behavior is covered by the gated page
  tests. Initial transient loading was not captured as a separate browser
  before/after visual state; loading/empty branches remain covered by component
  tests and empty filters were exercised in Chromium.
- Remote CI results have not been observed. Local Windows, Linux-runtime and
  browser results are recorded separately above.

The stage remains **in progress**, with implementation concerns fixed and the
unobserved evidence explicitly retained. Page length is now 1,280 physical
lines; 1,273 remains the dated measurement immediately after extraction.

Changed-guide local links/anchors, acceptance JSON files and screenshot paths
were checked. `git diff --check` passed; new untracked source/test files also
passed a separate trailing-whitespace check. The no-Worker R7 diagnostics in
`artifacts/browser/runs/20260922-082440-a42d6537/` contain no console warnings,
page/console errors or failed requests at any of the three viewports.
