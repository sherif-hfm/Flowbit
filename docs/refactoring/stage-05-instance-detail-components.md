# Stage 5 — Extract instance detail display components

[Plan index](README.md)

[Next planned stage: definition validation](stage-06-definition-validation.md)

**Status: In progress — extraction implemented; real-browser verification
pending.** The seven display components are extracted and the automated suite
passes, but the required localhost browser checks
([below](#required-browser-verification)) have not been completed, so the
acceptance gate remains open. This stage is independent of backend
extractions and Stage 4 after a passing baseline.

**Stage 7 browser automation (2026-09-20).** The new
[Flowbit.BrowserTests](../../Flowbit/tests/Flowbit.BrowserTests/README.md)
suite now covers a large part of this gate automatically: R1/R2 (normal-task
lifecycle, detail states, section links), R3 (identity replacement),
R4 (gateway/complex/multi-instance sections and navigation), R5 (real
five-second polling after an out-of-band HTTP completion), R6 (responsive
navigation and keyboard focus at 1024x768 and 390x844), and a deterministic
server-side regression for an in-flight identity response with a
request-count characterization. The initial product scenarios passed (17/17 locally,
Chromium 151.0.7922.34). The gate is still open for the manual headed
inspection rows (per-state screenshots, native-picker success/cancel,
claim-capture and delegation attribution, Worker-driven batch links,
administrative action refresh, and the pre-extraction visual comparison).

The [Stage 7 review fixes](stage-07-browser-smoke-and-stage-05-acceptance.md#review-fixes-2026-09-20)
strengthen this evidence with real in-app navigation, separately gated identity
responses (including a failed replacement), complete-refresh polling counts,
gateway state values, and wheel-accessible submitted JSON. Those fixes do not
close the remaining manual acceptance rows.

## Implemented component boundaries

All components live under `Flowbit/src/Flowbit.Ui/Components/Shared/InstanceDetails/`
(namespace `Flowbit.Ui.Components.Shared.InstanceDetails`, wired through
`Components/_Imports.razor`). Each is display-only: no injected services, no
fetches, mutations, timers, or identity subscriptions. The page keeps instance
loading, the `refreshGate` semaphore, identity epochs, polling, cancellation,
disposal, input state, authorization, and every mutation. Display-only label and
history formatting moved with its owning section:

| Component | Inputs | Owns |
| --- | --- | --- |
| `InstanceGatewayState.razor` | `GatewayExecutions`, `ComplexGatewayStates`, `Nodes` | Both gateway sections, gateway labels, completion-reason labels, id-list formatting, per-section visibility. |
| `InstanceMultiInstanceResults.razor` | `ShowSection`, `CompletedItems`, `SequenceFlows` | The multi-instance result section, flow labels, item numbers, payload JSON. |
| `InstanceVersionChangeHistory.razor` | `InstanceId`, `Changes` | Version-change audit table, ordering, batch links. |
| `InstanceVariableUpdateHistory.razor` | `InstanceId`, `Updates` | Variable-update audit table, outcome JSON, batch links. |
| `InstanceSharedBindings.razor` | `InstanceId`, `Bindings` | Value-free shared-binding table and visibility rule. |
| `InstanceVariables.razor` | `InstanceId`, `Variables`, `EventCallback<string> OnNavigateToSection` | Latest variables, audit links (delegated to the page's `ScrollToSectionAsync`). |
| `InstanceHistory.razor` | `InstanceId`, `HistoryItems` | History table, details text, claims/delegation attribution, batch links. |

`InstanceDetailFormatting.cs` beside the components holds the pure
`FormatJson` helper shared by the variables and variable-update components.
The page keeps `CompletedMultiInstanceItems` derivation and all section
navigation (`ScrollToSectionAsync`); section IDs are unchanged. Component
rendering coverage lives in
[InstanceDetailDisplayComponentTests](../../Flowbit/tests/Flowbit.Tests/InstanceDetailDisplayComponentTests.cs).


## Objective and boundary

Reduce the markup and display-formatting responsibilities of
[InstanceDetail.razor](../../Flowbit/src/Flowbit.Ui/Components/Pages/InstanceDetail.razor)
by moving bounded display sections into child components. Preserve the page's
existing request coordination and ownership of operational actions.

The page retains instance loading, actor resolution, `refreshGate`,
`identityEpoch`, `identityRefreshPending`, polling, cancellation, disposal,
version-change/reactivation state, input state, authorization, and every mutation.
Do not introduce a new page state service or move these behaviors into children.

Keep existing shared components such as `ExecutionPositionList`,
`InstanceAdministrativeActions`, `ActorClaims`, and `ActingForBadge`. This stage
does not redesign the page, change routes, or change the API client.

## Proposed components and contracts

Add components under `Flowbit/src/Flowbit.Ui/Components/Shared/InstanceDetails/`.
Names below are proposed file names; use existing DTO types from
[WorkflowDtos.cs](../../Flowbit/src/Flowbit.Shared/Dtos/WorkflowDtos.cs) and related
DTO files, with read-only list parameters.

| Component | Inputs and responsibility |
| --- | --- |
| `InstanceGatewayState.razor` | Gateway executions, complex states, and workflow nodes for labels; render the existing two independently visible sections. |
| `InstanceMultiInstanceResults.razor` | Page-derived completed history items, the existing show-section flag, and sequence-flow definitions for labels. |
| `InstanceVersionChangeHistory.razor` | Instance ID and version-change audit DTOs; preserve source/target labels, direction, actor/roles, reason, and links. |
| `InstanceVariableUpdateHistory.razor` | Instance ID and administrative variable-update audit DTOs; preserve outcomes, JSON values, actor/roles, reason, and batch links. |
| `InstanceSharedBindings.razor` | Instance ID and shared-variable binding metadata; preserve value-free catalog presentation and empty/visibility rules. |
| `InstanceVariables.razor` | Instance ID, latest variable DTOs, and `EventCallback<string> OnNavigateToSection` for audit links. |
| `InstanceHistory.razor` | Instance ID and history DTOs; render existing details, attribution, delegation, claims, and administrative batch links. |

Pass only the collections/scalars a section uses. Children do not inject
`WorkflowApiClient`, `TokenState`, a repository, or a polling service. Parameters
are read-only by convention; children do not modify DTO collections or workflow
definitions. Do not pass the entire page instance or expose its mutable fields.

## Implementation sequence

1. Capture current rendered output and behavior for empty, normal, multi-instance,
   gateway, and audited instances. Run the existing UI contract tests below.
   Note section IDs, visibility conditions, ordering, links, and formatting.
2. Extract the variables and shared-binding sections first. Copy their markup
   without changing wrappers, classes, table semantics, text, or IDs. Wire
   `OnNavigateToSection` back to the page's `ScrollToSectionAsync` so the page
   retains JavaScript navigation. Keep the same prevent-default behavior.
3. Extract version-change and variable-update audit sections. Preserve existing
   `StatusBadge`, actor/role text, batch URLs, reasons, captions, and JSON escaping.
   Preview warnings remain in the page-owned forms. Selected claims and delegation
   badges remain in ordinary history where currently rendered. Run UI contracts
   after the move.
4. Extract the gateway/complex-state, multi-instance results, and history
   sections. Keep the page's existing completed-item derivation and visibility
   decisions; pass the resulting lists to the child. Move display-only label
   and history formatting into the components that own them.
5. For formatting needed by more than one component, add an internal
   `InstanceDetailFormatting` helper beside the components, containing only
   the existing pure formatting methods. Keep exact null/default and date/JSON
   behavior. Do not move mutation logic or actor evaluation into this helper.
6. Add the component namespace to imports where necessary. Review global and
   isolated CSS before moving markup. Preserve the rendered DOM hierarchy and
   CSS selector reach; do not compensate with a layout redesign or unnecessary
   broad selectors.
7. Recheck lifecycle and parameter updates. Children render the latest lists
   supplied by the page on each refresh; do not cache the initial parameters,
   retain stale DTOs in local state, or create per-child refresh requests.
8. Complete full tests and browser checks, update documentation where ownership
   descriptions change, and record the final component boundaries.

## Invariants

- Keep the single refresh semaphore, identity epochs, request sequencing,
  five-second polling, and disposal behavior in the page. Do not parallelize
  page requests or move subscription ownership while extracting markup.
- Preserve suppression of actions during identity refresh and rejection of stale
  responses. Child rendering must not revive data or controls from an older actor.
- Keep all existing mutation confirmations, pending states, validation, and error
  handling, including claim/actions, interrupts, reactivation, version changes,
  and administrative operations, with their current owners.
- Preserve section IDs including `gateway-scopes`, `complex-gateway-states`,
  `multi-instance-results`, `version-changes`, `variable-updates`, `variables`,
  and `history`. Keep shared-binding anchors and page navigation consistent.
- Preserve row order, empty states, heading hierarchy, table captions, responsive
  wrappers, labels, timestamp formatting, escaped JSON, audit attribution, and
  link destinations. Do not replace structured claims with lossy strings.
- Keep page-level and API-client request counts unchanged. Display components
  perform no fetches, mutations, timers, or identity subscriptions.

## Automated tests

Retain
[InstanceReactivationUiContractTests](../../Flowbit/tests/Flowbit.Tests/InstanceReactivationUiContractTests.cs)
and
[InstanceVariableUpdateUiContractTests](../../Flowbit/tests/Flowbit.Tests/InstanceVariableUpdateUiContractTests.cs),
including `IdentityChangeRefreshesDisplayedActorAndPersonalActions`,
`NavigatingAwayDuringInitialLoadDoesNotStartPollingAfterDisposal`, and the
newer `InFlightActionDiscoveryResponseCannotRevivePriorActorActions`
(delayed action-discovery response released after an identity change; only the
new actor's presentation survives; discovery request count characterized at
exactly two). Continue to
test the assembled page, so broken integration cannot pass solely because an
isolated component renders.

Use the existing `HtmlRenderer` pattern for uncovered display risks: empty
sections, audit links and escaping, gateway labels/order, multi-instance results,
and history attribution. Add a page regression for stale parameter/identity
updates only if the existing tests do not cover the affected scenario. Avoid
tests that assert private helper names or simply mirror component parameters.

From the repository root, in PowerShell or Bash:

```text
dotnet test Flowbit/Flowbit.slnx --filter "FullyQualifiedName~InstanceReactivationUiContractTests|FullyQualifiedName~InstanceVariableUpdateUiContractTests"
docker info
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet
git diff --check
```

Include any new component test class in the focused command when it is added.
Record the actual commands and counts. Component rendering tests do not replace
browser interaction checks.

## Required browser verification

Start the isolated local stack using the
[getting-started guide](../getting-started.md), then follow the
[UI guide](../ui-guide.md) for development identities and workflow setup. With
default Compose ports, use `http://127.0.0.1:15152/instances/{id}` with an actual
created instance ID. Record any different URL used.

Use real clicks, typing, keyboard navigation, and scrolling to verify:

1. An empty or newly started instance, a normal user-task instance, and a terminal
   instance retain their summary, sections, empty states, and visible actions.
2. Gateway/complex-state and multi-instance examples render labels, status,
   ordering, and submitted results as before. Use section navigation links and
   verify each scroll target, including variable-to-update-audit links.
3. Version and variable-update history retain reasons, actor/role text, JSON
   values, and working links to the correct batch screen. Ordinary history retains
   selected claims and delegation badges. Exercise
   an existing administrative action in the isolated test stack to refresh data.
4. Complete a normal action and observe a polling refresh. Change the development
   identity while data is refreshing and confirm prior-actor actions do not
   return. Navigate away during refresh and return; check for stale display,
   duplicate polling, or disposal errors. Use delayed responses in an automated
   test for deterministic stale-response coverage when manual timing is unreliable.
5. Inspect **1440 × 900**, **1024 × 768**, and **390 × 844** viewports. Compare
   table overflow, headings, links, controls, and focus order to the original.
   Preserve existing responsive behavior without broadening this refactor into
   unrelated mobile improvements.

Inspect the browser console and network activity. Record browser/version,
localhost URL, interactions, sizes, console result, and any unavailable scenario.
Capture before/after screenshots; include a screenshot in the implementation
report if visual appearance changed. This real-browser gate is required even
though the intended appearance is unchanged.

## Completion, documentation, and rollback

Acceptance requires all focused/full tests passing, completed browser checks,
unchanged user-visible behavior and request counts, and confirmation that
coordination still belongs to the page. Seven components are the bounded target;
there is no requirement to reach a particular page length.

Update component ownership in the applicable UI architecture section of
[AGENTS.md](../../AGENTS.md) or [Flowbit/README.md](../../Flowbit/README.md).
Review [docs/ui-guide.md](../ui-guide.md) against the final page. Its walkthroughs
and screenshots should remain accurate; update them only if the result actually
changes documented behavior or appearance. Validate affected links and images.

Deliver small buildable component extractions with tests, followed by the full
page verification. Mark the stage implemented only after acceptance. Roll back
by reverting the extraction commits and rebuilding/redeploying the UI. No API
upgrade, database migration, or persisted-state conversion is needed.
