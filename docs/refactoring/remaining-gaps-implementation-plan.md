# Prioritized plan for the remaining refactoring gaps

[Plan index](README.md) · [Gap inventory](gaps.md)

**Status: In progress — Stage 5 and Stages 8–11 are locally accepted;
Stage 7's smoke and local Worker suites pass, with native editor-dialog
verification still open. Stage 11's final administrative acceptance and visual
review are complete. Explicitly deferred refactors remain open.** This roadmap
records the selected follow-up work and the
accepted C# compatibility break. Saving this document does not complete any
implementation or acceptance gate.

## Assessment

Reviewed the code at commit `fbe0721` against [gaps.md](gaps.md) on
2026-09-19. The review was read-only; tests were not run during that review.
The table below is maintained with subsequent stage outcomes; the dated stage
records preserve the original measurements and validation results.

| Gap | Current state | Decision |
| --- | --- | --- |
| 1. Engine responsibilities | Broader command responsibilities remain interleaved; Stage 9 extracted waiting-task role management | Keep routing, gateways, messages, and jobs deferred. |
| 2. Broad engine interface | Stage 9 reduces 37 methods to 33 | Waiting-task role operations now live on `IUserTaskRoleManagementService`. |
| 3. Definition validation | Implemented in stage 6; lifecycle service is now 309 lines | Correct the inventory; no additional extraction needed. |
| 4. Repository duplication | Stage 2 implemented; limited duplication remains | Defer further abstraction until a concrete maintenance need arises. |
| 5. Editor hotspots | Stage 10 extracted node-type transition handling | Keep parser and normalization rewrites deferred. |
| 6. Oversized UI units | Stage 5 and Stage 11 display extractions accepted | Keep other UI pages and the API-client split deferred. |
| 7. Browser automation | 30 smoke and 24 local Worker acceptance cases pass; existing CI observed passing | Native editor save-dialog success/cancel remains unavailable and open. |

## Implementation stages

### Preparation — refresh the inventory and establish a baseline

- Correct completed-stage statuses and outdated measurements in the gap inventory.
- Run the existing full test suite with Docker available.
- Preserve historical implementation records; add supersession notes where the new interface changes replace earlier compatibility decisions.

### Stage 7 — automated browser smoke suite and stage 5 acceptance

[Detailed implementation plan](stage-07-browser-smoke-and-stage-05-acceptance.md)
— harness ownership, test matrix, commands, CI evidence, and the complete
Stage 5 acceptance checklist. **Status: in progress.** The suite
(`Flowbit/tests/Flowbit.BrowserTests/`, 26 product cases E1–E8 and R1–R7 plus four
harness regression tests) is
implemented and passing locally. Both `test` and `browser-smoke` passed for
`3e70130` in [run 35713256658](https://github.com/sherif-hfm/Flowbit/actions/runs/35713256658);
that is the remote observation for the existing CI requirement. Stage 5's
runtime acceptance (Worker-driven batch links, delegation/claim attribution,
administrative action refresh, headed screenshots, and the pre-extraction
visual comparison) passed in the local acceptance follow-up. The
editor's native-picker success/cancel evidence is a separate Stage 7 gate and
remains unavailable. See the plan's
[implementation results](stage-07-browser-smoke-and-stage-05-acceptance.md#implementation-results-2026-09-20)
and the per-row checklist status.

- Add a standalone `net10.0` browser-test project outside the existing solutions. Match existing xUnit/Testcontainers versions and pin [Microsoft.Playwright 1.62.0](https://www.nuget.org/packages/Microsoft.Playwright/1.62.0), using Chromium.
- Start disposable PostgreSQL, actual API/UI processes on ephemeral localhost ports, and a test-only static editor host. Capture logs, bound startup timeouts, and clean up fixture-owned resources.
- Configure matching development JWT settings, the isolated database connection, and the UI API URL. Disable durable publication and use synchronous fixtures without a Worker.
- Run UI tests serially because development identity state is singleton. Reset identity through the token screen between tests and create fixtures through HTTP APIs.
- Cover editor load/edit/save/reload, node and lane dragging, validation feedback, and keyboard focus. Exercise the download fallback automatically; retain manual native-file-picker verification.
- Cover normal task claim/completion, instance navigation, identity changes, and normal/terminal/gateway/multi-instance detail sections.
- Add a separate CI job with browser installation, TRX output, and failure screenshots, traces, and host logs. Use behavioral assertions without screenshot comparison baselines.
- Complete every outstanding scenario in [stage 5's browser gate](stage-05-instance-detail-components.md#required-browser-verification), using the full isolated stack where necessary. Mark stage 5 implemented only after acceptance.

### Stage 8 — remove existing query/detail compatibility methods

[Detailed implementation plan](stage-08-remove-query-detail-compatibility.md)
— caller migration, detail endpoint identity and metadata checks, constructor
and test-host updates, projection invariants, validation, and rollback.
**Status: implemented and locally accepted.** Baseline 1,892/1,892, focused
102/102, final solution 1,900/1,900, and Chromium 20/20 passed with zero failures
or skips. The working tree is based on `6a14b1f` and remains uncommitted; see the
implementation record for commands and artifacts.

- Remove `ListInstancesAsync`, `SearchInstancesAsync`, and `GetInstanceAsync` from the engine interface and implementation: **40 → 37 methods**.
- Keep list/search endpoints on `IWorkflowInstanceQueryService`; move detail GET to `IWorkflowInstanceProjectionService.GetDetailAsync`.
- Preserve actor validation, authorization, response metadata, and 200/404 behavior.
- Remove the unused query-service constructor dependency. Retain the projection dependency and all command-response projection calls.
- Remove obsolete forwarding tests, migrate detail tests to the projection interface, and update direct engine construction sites.

### Stage 9 — extract waiting-task role management

[Detailed implementation plan](stage-09-waiting-task-role-management.md)
— service and helper ownership, direct endpoint migration, inherited lock order,
transaction/save boundaries, PostgreSQL rollback and concurrency coverage,
C# caller migration, validation, and documentation. **Status: implemented
and locally accepted.** Baseline 1,899/1,900 (HTTP timeout flake passed on
retry), focused 129/129, final solution 1,930/1,930, and Chromium 20/20
passed with zero failures or skips. The working tree is based on `67945f7`
and remains uncommitted; see the
[implementation record](stage-09-waiting-task-role-management.md#implementation-record--2026-09-20)
and [review follow-up](stage-09-waiting-task-role-management.md#review-follow-up--2026-09-20)
for commands and artifacts.

- Introduce scoped `IUserTaskRoleManagementService` and `UserTaskRoleManagementService`, owning the existing four task/MI role read/change operations.
- Inject the service directly into the four endpoint handlers and remove those operations from the engine: **37 → 33 methods**. Add no engine forwarding dependency.
- Move permission checks, scope loading, replacement validation, policy comparison, DTO mapping, and transaction ownership together.
- Preserve shared repository/UoW scope, instance → token → MI → task lock order, existing save/commit positions, atomic unfinished-child updates, and one audit event per actual change.
- Preserve stale-identical success, stale-different conflicts, claims/assignment ownership, completed-item history, actor claims, and immutable definitions.
- Keep management-list status parsing and dynamic role capture with their existing owners.

### Stage 10 — isolate editor node-type transitions

[Detailed implementation plan](stage-10-editor-node-type-transitions.md)
- transition-helper contract, retained inspector redraws, ordered graph cleanup,
characterization matrix, dialog cancellation coverage, real-browser acceptance,
documentation, and rollback. **Status: implemented and locally accepted.** The
transition body lives in `changeNodeTypeFromInspector(node, nextType)` with the
selector adapter owning both redraw paths; 20 characterization tests passed
before the extraction, the final suites passed (1,950 solution, 321 focused,
and a 27-case browser suite whose editor and harness cases all passed with one
unrelated R6 scroll-timing flake passing on retry), and headed inspection
evidence is recorded in the stage document's implementation record.

- Move the Type selector's transition callback into a named `changeNodeTypeFromInspector(node, nextType)` function. Keep rendering orchestration in the inspector.
- Preserve confirmation/rejection behavior, gateway metadata cleanup, role references, boundary deletion, message-start conversion, default-start reassignment, normalization order, redraws, and undo/redo.
- Leave `applyTypeInvariants`, the expression parser, save-validator markers, and other inspector sections unchanged.
- Preserve the single-file, dependency-free editor.

### Stage 11 — administrative-action display components

[Detailed implementation plan](stage-11-administrative-action-display-components.md)
— component contracts, retained page coordination, scoped CSS ownership,
characterization and identity/disposal coverage, no-Worker smoke and full-stack
browser acceptance, documentation, and rollback. **Status: implemented and
locally accepted.** The
extraction, tests, and no-Worker smoke scenario are implemented (2026-09-21,
with Stage 5's acceptance explicitly recorded as still open); the
2026-09-22 review follow-up adds the polling-render fix, passing Windows/Linux
regressions and Worker-enabled browser scenarios. The maintained 24-case suite
now passes, including cancelled-without-completion recovery and gated loading;
Stage 5's prerequisite is complete. The final focused administrative run passed
11/11 with expanded screenshots, exact loading-request counts and visual
review; Stage 11's remaining evidence is closed.

- Extract `AdministrativeBatchAudit`, `AdministrativeBatchItems`, and `AdministrativeBatchHistory`.
- Pass existing DTOs and presentation values; expose a typed batch-open callback.
- Keep fetching, filters, pagination, selection, confirmation/cancellation, identity checks, polling, and disposal in the page.
- Move applicable isolated CSS into the owning components, preserving appearance.
- Defer the other management pages and the API-client split.

Stages 8–9 can run alongside stage 7. Begin UI extractions after browser coverage
is available; stage 11 also requires stage 5 acceptance.

## Contracts and validation

The selected C# breaking change requires callers to migrate to the focused
interfaces. HTTP routes, DTOs, workflow JSON, authorization, and persisted data
remain unchanged. No migration or new production dependency is planned.

- **Backend:** retain query/projection, role-management, concurrency, authorization, and OpenAPI tests. Preserve projection query-count assertions. Add focused service-scope and rollback coverage, including failure after the repository's internal save.
- **Editor:** characterize accepted/rejected type changes and saved-model cleanup before extraction; run existing editor and parser-conformance tests plus real-browser interactions.
- **Blazor:** retain identity/disposal regressions; test escaping, ordering, empty states, parameter replacement, and unchanged request behavior. Keep delayed-response tests server-side because Blazor API calls do not originate in the browser.
- **Browser:** inspect editor at 1440×900 and 1024×768; inspect runtime UI additionally at 390×844. Record console results and screenshots.
- Run the existing full-suite command and `git diff --check` for each final stage candidate. Run browser tests separately after building their project and installing Chromium. Report unavailable checks explicitly.

## Documentation and delivery

Update the [gap inventory](gaps.md) and [refactoring index](README.md), and add
stage documents following the existing format. Update runtime/contributor
ownership descriptions, document the C# migration and browser setup commands,
and review affected public guides without unrelated edits.

Deliver independently reviewable, buildable stages. Validate changed links and
examples. Rollback consists of reverting the affected stage and rebuilding; no
data conversion is required. Keep deferred gaps explicitly open rather than
marking this roadmap as a complete engine or UI decomposition.
