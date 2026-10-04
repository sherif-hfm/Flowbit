# Zen GLM Flash evaluation — 2026-10-03

> Historical evidence: Microsoft Agent Framework was subsequently removed from
> Flowbit. Only `current` and `optimized` are now supported. Framework commands,
> settings, package versions and adoption procedures below describe the tested
> historical builds, not the current product or runnable tooling. Frozen historical
> artifacts are not application deliverables. See the [current evaluation guide](README.md)
> and [removal verification](#framework-removal).

The [targeted-adoption follow-up](#targeted-adoption-follow-up) also retained
defaults after testing all three remaining scenarios. The original strict
comparison below remains unchanged as historical evidence.
The separately authorized [recovery retest](#recovery-retest) records subsequent
fixes and diagnostic runs without rewriting those original trial results.

No candidate qualified for promotion. Keep the existing `current` execution
variant, OpenCode Zen endpoint, and `glm-5.3-flash` with `max` reasoning. The
optimized and Microsoft Agent Framework variants remain opt-in experiments.
This decision does **not** establish that the existing default meets the speed
target: its baseline also exceeded five minutes.

## Live results

The [complex fixture](fixtures/complex.txt) requests six lanes, 29 nodes, 33 flows,
parallel department reviews, conditional routing, a multi-instance vote, script
output and a timer. A pass requires all 36 requirement checks and a validated
proposal in at most 300 seconds, without Continue. Structural validation alone
does not count as a pass.

| Trial | Variant / reasoning | Seconds | Provider attempts | Requirements | Outcome |
| --- | --- | ---: | ---: | --- | --- |
| 1 | current / max | 300.00 | 4 | No final proposal | Deadline; baseline |
| 2 | optimized / low, preliminary build | 120.20 | 10 | No final proposal | Context-budget pause; excluded from candidate comparison |
| 3 | framework / low, preliminary build | Interrupted | At least 3 started | Not evaluated | Stopped after identifying the same context-budget defect; excluded |
| 4 | optimized / low | 84.87 | 9 | 34/36 | Incorrect Submit and Director Approve action labels |
| 5 | framework / low | 263.46 | 15 | 31/36 | Five requirement checks failed |
| 6 | optimized / high | 183.73 | 12 | 32/36 | Four required action-label checks failed |
| 7 | framework / high | 230.98 | 12 | 36/36 | Pass |
| 8 | optimized / max | 300.00 | 3 | No final proposal | Deadline |
| 9 | framework / max | 300.01 | 4 | No final proposal | Deadline |
| 10 | framework / high, repeat | 140.07 | 8 | 36/36 | Pass |
| 11 | framework / high, repeat | 206.93 | 9 | 35/36 | Director action was `Director approved` instead of required `Approve` |

Framework/high was the only candidate with a fully correct screening result.
Its two additional fresh trials produced **2/3 passes overall**, below the
required 3/3. Its last proposal passed Flowbit validation and claimed the action
was Approve, but the actual definition had a different label. This demonstrates
why the evaluation checks the definition separately from the model's summary.

There were 11 workflow trials, including the two excluded preliminary trials and
the baseline, within the initial cap of 14. The three reserved winner checks
(simple creation, existing-workflow modification, and live Cancel/Continue) were
not run because no candidate met the prerequisite reliability gate. Unit and
browser cancellation tests passed; live provider Cancel/Continue acceptance
remains unverified. A small native-tool/usage compatibility probe passed and is
not counted as a workflow trial. No default was promoted or deployment performed.

## Build and evidence provenance

All candidates used the same Zen endpoint `https://opencode.ai/zen/v1/`, model,
synthetic requirements, frozen authoring package, 300-second run deadline,
180-second call deadline, 50-call budget and 262,144-output-token budget. Model
profile: 16,384 initial output tokens, 32,768 maximum output tokens and 65,536
context tokens. Only variant and reasoning effort differed.

Trials 4–11 used the same frozen Service and Infrastructure binaries. Trial 1
used the earlier build with the same legacy execution behavior. Trials 2–3
preceded a context-budget fix and are retained as preliminary evidence, not
combined with the candidate comparison. The evaluation-only harness was updated
before trial 9 to cancel an actual HTTP attempt in the reserved resume test;
the frozen Service/Infrastructure binaries were unchanged. That resume test was
not reached.

| Frozen artifact | SHA-256 |
| --- | --- |
| Authoring package contract | `be62f092cc2306ad66590b8d180b97c3e7d4183675b521b4b989c2b8acc23a9f` |
| Flowbit.Service.dll | `5d01c80d50de9c1f6b19480ac64e504c1b258069b0799d1798ca231b4cf8d365` |
| Flowbit.Infrastructure.dll | `a63f39108f1e33ead2adce16ca2c970f2202d1d0b43357bc5599c147556285b8` |
| Initial frozen AuthoringEval.dll | `1303c1d4863cbcb53b51859cc17f2a49ce859383e44d9fb1c4d0131e7521cc9f` |
| Resume-test harness AuthoringEval.dll | `48e9a5e0d631c614976c5a3e82ce77fb2398d262df451545e492cb8d9fbe4bb4` |

Subsequent production fixes retain read counters on timeout, exclude backoff from
call duration, reject null native-tool arguments, and halve optimized batches on
each repeated truncation. They are covered by automated tests; the live timings
above belong to the frozen binaries, not a fresh benchmark of these later fixes.

Local evidence is retained under `artifacts/ai-variants/matrix-01/` and
`artifacts/ai-variants/matrix-02/`, including per-call metrics, final synthetic
definitions and requirement-check results. The matrix verdict combines the
harness structural/deadline result with the independent requirement checker;
`evidence.json` alone is not the acceptance verdict. No keys, prompts or private
reasoning are recorded. Ignored local artifacts are not part of a repository
checkout; this report records their result and provenance.

## Automated verification

The final focused suite passed **349/349**:

```text
dotnet test Flowbit/tests/Flowbit.Tests/Flowbit.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~WorkflowAi|FullyQualifiedName~OpenCodeGoProviderRecovery|FullyQualifiedName~WorkflowAuthoringPackage|FullyQualifiedName~WorkflowApiClientAi" --logger "trx;LogFileName=ai-variants-final.trx" --results-directory artifacts/ai-tests
```

Coverage includes native-tool framing and exact provider-call counts, terminal
errors, cancellation, truncated-output discard, atomic edits, duplicate batches,
targeted validation repair, checkpoint version/configuration compatibility,
semantic context, read caching and batch pacing. The evaluation tool builds.
The 36-check PowerShell checker passed its known-valid definition regression and
rejected a broken entry flow, under both Windows PowerShell 5 and PowerShell 7.
These checker regressions are not additional live trials.
An offline matrix regression also passed on both shells: seven simulated failures
produce `selected: null` and no extra trials, without reading a key or using the
network. Changed documentation links and anchors passed validation.

## Real-browser verification

After publishing the actual API and UI Release hosts, Chromium **151.0.7922.34**
ran the [AI browser scenarios](../../tests/Flowbit.BrowserTests/README.md).
The full AI suite passed **8/8** with current execution; the H8 recovery scenarios
passed **3/3** each with optimized and framework execution, **14/14 total**.
The browser suite uses a controlled loopback provider to exercise recovery;
the live Zen evaluation above is separate.

```text
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~AiAuthoringSmokeTests
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~H8
```

The first command used `FLOWBIT_BROWSER_AI_VARIANT=current`; the second ran once
with `optimized` and once with `agent-framework`, as documented in the suite's
PowerShell/Bash instructions. The actual runs also saved TRX logs named
`ai-variants-current.trx`, `ai-variants-optimized.trx`, and
`ai-variants-framework.trx` under `artifacts/browser/test-results/`.

Local UI URLs were `http://127.0.0.1:62363` for the current suite and
`http://127.0.0.1:62587` / `http://127.0.0.1:62590` for the candidate suites.
Their disposable hosts have stopped. Viewports were **1440×900, 1024×768 and
390×844**. Real browser clicks and typing exercised generation, truncation and
retry, accepted draft steps, Cancel, Continue with frozen requirements, the
per-call wait timer, Apply and Undo. The current suite also checked stale-editor
and identity/model lifecycle behavior.

All 17 setup/scenario diagnostic files recorded **zero page errors, console
errors, warnings or failed requests**. Retained progress screenshots were
visually inspected at all three widths. Screenshot evidence is under
`artifacts/browser/runs/20261003-155201-1c23436c/h8-ai-recovery-1024/ai-resumed-progress.png`
and the corresponding 1440/390 scenario directories. Candidate run directories
are `20261003-155740-a39e74ef` and `20261003-155740-462410b1`.

See [execution behavior](../../../docs/ai-authoring.md#execution-variants-and-evaluation),
[configuration and rollback](../../../docs/deployment.md#ai-authoring-and-local-ocr),
and [evaluation policy](README.md).

## Targeted adoption follow-up

The user selected targeted framework/high adoption instead of reopening the
speed comparison, and accepted harmless equivalent labels as warnings.
`functional-v2` now separates behavior and display wording, using the explicit
aliases documented in the evaluation guide. This is a revised acceptance policy,
not a claim that framework is faster than optimized execution.

Retained trials 7, 10 and 11 each passed all **78 functional/label checks** under
that policy. Trial 11 has one warning (`Approve` versus `Director approved`).
The original 36-check reports and strict **2/3** outcome were not overwritten.
Reassessment records include source-result hashes and reference the originals.

| Trial | Framework/high scenario | Seconds | HTTP attempts | Outcome |
| --- | --- | ---: | ---: | --- |
| 12 | Simple creation | 13.05 | 2 | Pass; no label warnings |
| 13 | Existing-workflow modification | 23.47 | 4 | Invalid final result: “The given key was not present in the dictionary.” |
| 14 | Complex creation with Cancel/Continue | 300.02 | 12 | Deadline; no final proposal |

Trial 13 retained a draft with the new Rejected end and Reject flow, but final
generation failed. An offline test using the same minimal edit and a valid finish
command passes for all three execution variants; the live failure is **not
reproduced or resolved**. Raw provider responses were deliberately not logged,
so these artifacts do not identify the exact failing command or code location.

Trial 14 cancelled a real HTTP attempt after revision 1 and resumed the identical
draft, revision, input/package bindings, edit receipts and retained reads.
`resumed` and `resumeVerified` were true. Its two cancelled HTTP attempts were the
intentional cancellation and the final deadline cancellation. More edits were
accepted after resuming, but no complete proposal arrived within the overall
300-second budget. Resume preservation passed; scenario acceptance did not.

All **14 authorized workflow trials** have now been consumed. No extra billed
trial, manual proposal repair, automatic fresh retry or promotion was performed.
Shipped settings remain `current`, Zen, Flash/max. The new engine is not qualified
for default adoption by this round, even with label warnings allowed.

Fresh live evidence is under `artifacts/ai-adoption/finalist-01/`. It used the same
frozen reference contract as the initial comparison and a new frozen runner:

| Artifact | SHA-256 |
| --- | --- |
| AuthoringEval.dll | `3dd860c767cbd92d195b54faf355d0d6222c771895fe89935412168a52837a5b` |
| Flowbit.Service.dll | `b679800a0f03b6cd473db622c64a7b92767b45605bc9e64738bcbacd426eaad6` |
| Flowbit.Infrastructure.dll | `518e5c2f5d46645a4cd21f36daf8505531da0abc13e083285080e626866a3367` |

After these trials, an offline-only checker regression caught and corrected the
modification checker's removal of PascalCase rather than serialized camelCase
collection keys. That harness defect did not cause trial 13's failure: it had
already returned an invalid service result, before preservation checking could
succeed. Historical trial verdicts are unchanged.

The focused automated command above passed **353/353** in this follow-up, saved
as `artifacts/ai-tests/ai-adoption-final.trx`. Twelve complex-checker regressions
passed on Windows PowerShell 5 and PowerShell 7, covering alias warnings and
blocking route, role, selectability, condition, meaning and quorum errors.
Twelve offline creation/preservation checks passed, including existing coordinates,
IDs, labels and role preservation. These are offline tests, not new live passes.

Follow-up browser verification passed **16/16** in Chromium **151.0.7922.34**:
H8/H9 passed 4/4 with published execution/reasoning settings (`shipped`) and 4/4
with explicit framework/high; the full AI suite passed 8/8 with `current`.
Shipped mode leaves the published execution/reasoning values intact and asserts
them on the real adapter's initial and resumed requests. Candidate mode asserts
four native tools and high reasoning. Both use the controlled loopback provider,
so these results do not replace live Zen acceptance.

```text
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~H8|FullyQualifiedName~H9"
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore --filter FullyQualifiedName~AiAuthoringSmokeTests
```

The first command ran with `FLOWBIT_BROWSER_AI_VARIANT=shipped` and then
`agent-framework`; the second used `current`. The final TRX files are
`ai-adoption-shipped-final.trx`, `ai-adoption-framework-final.trx`, and
`ai-adoption-current-final.trx` in `artifacts/browser/test-results/`.
An initial setup attempt failed because a no-build API publish lacked its
generated authoring manifest. Republishing the API with its build enabled
resolved setup before all final runs; those initial failures remain recorded.

The UI URLs were `http://127.0.0.1:64483` (shipped),
`http://127.0.0.1:64489` (framework/high), and
`http://127.0.0.1:64485` (current); the disposable hosts have stopped.
Real clicks and typing exercised truncation/retry, draft progress, Cancel,
Continue with frozen requirements, Apply, Undo, and checkpoint invalidation.
The current suite additionally exercised creation/editing and identity lifecycle.
Viewports were **1440×900, 1024×768, and 390×844**. All 19 setup/scenario diagnostic
files recorded zero page errors, console errors, warnings, failed requests or
unexpected dialogs. Resumed-progress screenshots were visually inspected at all
three widths under `artifacts/browser/runs/20261003-173812-87103427/`.
The shipped/current run folders are `20261003-173812-3b859aae` and
`20261003-173813-4a0d30ca`. No product layout changed in this follow-up.

Final documentation validation checked 20 changed/new relative links and anchors
without failures; `git diff --check` passed. The authorized test key was absent
from all 46 changed/new source files and 58 retained live/browser evidence files
scanned by the final audit.

## Recovery retest

The user's subsequent request to try Agent Framework again and fix the failures
authorized a new diagnostic round. These runs are separate from the completed
14-trial comparison; its cap, evidence and verdicts remain unchanged. All new
runs used Microsoft Agent Framework, OpenCode Zen `glm-5.3-flash`, high reasoning,
and the same frozen authoring contract. Complex diagnostics used a 1,800-second
overall limit, including time before and after intentional Cancel/Continue.
This matches the shipped run duration, not the earlier 300-second acceptance gate.

Implemented changes:

- Missing or mistyped command/read fields now produce named repair instructions
  rather than unchecked JSON lookup errors. Native schemas describe read selectors,
  typed edit fields, uploaded-page citations, and the current operation limit.
- Missing/text-only/multiple native calls are discarded and receive bounded
  retries, explicit one-tool guidance and a smaller batch. Exhaustion pauses with
  the same checkpoint; unknown tool names remain terminal. SDK loops stay disabled.
- Completed proposals cannot introduce unreachable work or disconnect previously
  reachable work. Starts and attached boundaries are included; existing diagram
  islands remain untouched. Runtime save/validation rules are unchanged.
- Instructions preserve requested action labels even on the final task of a
  branch, and ask for a default when creating a single-manual-start workflow.
  These instructions do not automatically rename model output or relax checks.

| Local run | Build | Scenario | Seconds | Result |
| --- | --- | --- | ---: | --- |
| 01 | Diagnostic, before fixes | Modify existing approval | 9.35 | Pass, preserved existing content |
| 02 | Diagnostic, before fixes | Repeat modification | 14.08 | Pass, preserved existing content |
| 03 | Diagnostic, before fixes | Complex Cancel/Continue | 174.67 | Terminal native-tool protocol failure after retained progress; resume verified |
| 04 | Tool fixes | Modify existing approval | 9.60 | Pass, preserved existing content |
| 05 | Tool fixes | Complex Cancel/Continue | 197.52 | Valid proposal, but only 75/78 checks: both amount routes bypassed Committee vote |
| 06 | Reachability guard | Complex Cancel/Continue | 232.67 | Valid proposal, 74/78: missing default start and three changed Complete labels |
| 07 | Reachability guard | Fresh complex generation | 303.18 | Valid proposal, same 74/78 misses; beyond the old five-minute limit |
| 08 | Final label/default guidance | Complex Cancel/Continue | 171.05 | Valid proposal, 77/78; Submit action was named Fork department reviews |

Run 08 made eight HTTP attempts including one intentional cancellation. It
restored the identical checkpoint, completed a validated proposal and passed all
remaining behavioral checks. The wrong action label is not in the approved alias
list, so it remains a failed acceptance check. This is improvement in completion
and recovery, not a 78/78 pass or a new default-adoption qualification.

Eight new diagnostic workflow trials were performed in this round. No generated
proposal was manually repaired, applied, saved or published. Shipped defaults
remain `current` / Zen / `glm-5.3-flash` / max; framework/high remains opt-in.

The historical dictionary-key failure did not recur in the three live editing
runs. Its exact original trigger remains unknown because the old raw tool calls
were not recorded. New regressions reproduce missing-field failures and verify
repair feedback across all three engines. It would be inaccurate to claim a
proven root cause for the original trial from these later passes alone.

Run 03 revealed repeated malformed edit arguments and an oversized batch before
the terminal native response failure. Run 05 exposed an unreachable Committee
step that ordinary structural validation allowed. Runs 06/07 motivated the final
label/default-start guidance. All failed functional checks remain failures;
structural success alone is not an acceptance pass.

Evidence is retained in `artifacts/ai-recovery/`, with separate output folders
and frozen runners at each stage. Allowlisted command shapes and exception
types/code locations are recorded without raw command values, prompts, exception
messages, provider keys or model reasoning. Final runner hashes:

| Artifact | SHA-256 |
| --- | --- |
| AuthoringEval.dll | `ff80ecc90bfdd4b6f3e22a7d8a1ae58722de968ffe1aa346d28e3d09d80db6ed` |
| Flowbit.Service.dll | `7e50fa4d748bed3ef29e6f7c13f9d42ac8e062a44cd5794729d0c26a7a3e6799` |
| Flowbit.Infrastructure.dll | `980cc5ed9d354a289fff6dd9c1520b386bf6a158f15006fc7e795f44606e7559` |

Automated verification covers field-specific repair, untouched drafts, bounded
native retries/accounting, safe pause/resume, dynamic operation limits, reachability
repair, existing islands, message/timer starts and attached boundary paths.
The focused suite passed **390/390**. Its command is the same as above, with TRX output
`artifacts/ai-tests/ai-recovery-complete.trx`.

Chromium 151.0.7922.34 H8/H9 passed **8/8** against published hosts, 4/4 with
framework/high and 4/4 with shipped current/max. Native H8 additionally injected
multiple tool calls after an accepted edit and asserted a smaller retry at the
same revision, followed by real Cancel/Continue/Apply/Undo interactions.
The command was the H8/H9 command above with `FLOWBIT_BROWSER_AI_VARIANT` set
to `agent-framework` and `shipped`; final TRX names are
`ai-recovery-connected-framework.trx` and `ai-recovery-connected-shipped.trx`.
UI URLs were `http://127.0.0.1:51612` and `http://127.0.0.1:51615` respectively;
the disposable hosts have stopped. All ten diagnostic files reported zero page
errors, console errors/warnings, failed requests or unexpected dialogs.
Screenshots at **1440×900, 1024×768 and 390×844** were visually inspected under
`artifacts/browser/runs/20261003-201404-4a93e85c/`; the shipped run is
`20261003-201404-9e427ede`. No product layout changed.

Documentation links/anchors and `git diff --check` passed. A final scan of changed
source files and retained diagnostic/browser evidence found no copy of the
authorized provider key.

## SDK-owned loop verification — 2026-10-04

This change replaces experimental per-step SDK calls with one
`ChatClientAgent.RunAsync` and executable `FunctionInvokingChatClient` tools.
The SDK dispatches tools and schedules subsequent model turns; Flowbit callbacks
retain atomic edits, validation, checkpoint reconstruction and bounded transport
recovery/accounting. The default remains `current` with Zen/Flash/max.

Earlier live results above used the single-step adapter. **No new live Zen calls
were made during this implementation verification**, so those results do not
establish the new loop's speed or business-requirement accuracy. This section
records deterministic integration and browser verification. The separately
authorized [live max round](#sdk-max-live) below tests the SDK-owned loop.

The focused automated suite passed **402/402**:

```text
dotnet test Flowbit/tests/Flowbit.Tests/Flowbit.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~WorkflowAi|FullyQualifiedName~OpenCodeGoProviderRecovery|FullyQualifiedName~WorkflowAuthoringPackage|FullyQualifiedName~WorkflowApiClientAi" --logger "trx;LogFileName=ai-sdk-loop.trx" --results-directory artifacts/ai-tests-sdk-loop
```

New regressions prove the service selects the whole-run interface instead of the
custom per-step loop, native read/edit results reach the next model request,
45 edits finish without a hidden SDK final call, validation feedback returns
through tools, clarification/finish stop immediately, budgets stop before another
HTTP attempt, and Cancel/Continue retains the draft with fresh run accounting.
History tests cover complete-pair omission above 8,000 bytes, credentials with
quotes/newlines, short credentials matching structural tokens, and opaque GLM
reasoning retained unchanged only within a safe bounded native pair. Neither
reasoning nor native metadata enters serialized completion/progress/checkpoints.
The evaluation observer now records each SDK transport attempt through the same
allowlisted metrics wrapper. Its Release build passed, and all **12 offline
creation/preservation checker cases** passed without a key or network access.

Real-browser H8/H9 passed **8/8** (4 framework/high and 4 shipped current/max).
After the documented API/UI publish and browser-test build commands, run this
command with `FLOWBIT_BROWSER_AI_VARIANT` set to `agent-framework` and `shipped`:

```text
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~H8|FullyQualifiedName~H9"
```

Chromium **151.0.7922.34** exercised Generate, output-limit/transient/native-envelope
recovery, real in-flight Cancel, Continue, Apply, Undo, and stale-checkpoint/model
invalidation. Framework UI: `http://127.0.0.1:59803`; shipped UI:
`http://127.0.0.1:59795` (disposable hosts have stopped). Viewports were
**1440×900, 1024×768 and 390×844**. All ten diagnostics files contained zero page
errors, console errors/warnings, failed requests or unexpected dialogs.
Progress screenshots at all three widths were inspected; no product layout changed.
Final evidence lives in `artifacts/browser/runs/20261004-062532-e2dcff61`
(framework) and `20261004-062532-ab5ab9e1` (shipped), with TRX files
`framework-sdk-loop-verified.trx` and `shipped-sdk-loop-verified.trx` under
`artifacts/browser/test-results-sdk-loop/`.

The initial browser attempt could not start because a `--no-build` republish
omitted the generated authoring manifest; a full publish restored the package,
then both engines passed. Source changes were not required for that setup error.
The test build still reports pre-existing SSH.NET advisory and unrelated analyzer
warnings. Changed documentation links/anchors and `git diff --check` passed.

<a id="sdk-max-live"></a>
## SDK-owned loop live max evaluation — 2026-10-04

The user explicitly requested the new SDK-controlled experimental engine with
OpenCode **Zen → `glm-5.3-flash` → max**. Three live trials used one frozen
Release runner and authoring package. `ChatClientAgent.RunAsync` and executable
`FunctionInvokingChatClient` tools controlled model/tool iteration; Flowbit
callbacks retained draft, validation, budget and recovery responsibilities.
This is a separate round from the earlier 14-trial comparison and eight-trial
recovery diagnostic. All three new trials are included, with no replacement runs.

Each trial had a **1,800-second total deadline**, a 180-second request timeout,
50 calls and 262,144 output tokens per run. The cancellation/resume trial shared
one total deadline across both runs. The profile used an initial 16,384 output
tokens, a 32,768 maximum and a 65,536 context window. Functional acceptance used
the existing `functional-v2` policy; completed proposals were also checked with
`strict-v1`. Nothing was manually repaired, applied, saved, published or executed.

| Trial | Total elapsed | HTTP attempts | Outcome |
| --- | ---: | ---: | --- |
| Fresh complex procurement | 1,800.01 s | 21 | Failed: evaluation deadline; no final proposal |
| Edit existing workflow | 45.09 s | 2 | Passed functional and strict preservation checks |
| Complex procurement with in-flight Cancel/Continue | 412.76 s | 8 | Passed 78/78 functional and 36/36 strict checks; identical checkpoint restored |

The editing trial preserved the existing workflow and added the requested Reject
route and Rejected end event. The completed complex proposal contained all six
lanes, 29 nodes and 33 connections: three parallel review branches, amount-based
director review, multi-instance voting/quorum/fallback, the approval script,
one-hour timer, fulfilment and closure. No action-label aliases or requirement
warnings were needed. Authoring validation reported only its normal generated
script review warning; the proposal documented the timer's Worker prerequisite.
These checks inspect the proposal; they do not execute its business process.

Cancellation happened during a real HTTP request after the first committed batch
at 93.98 seconds. Continue restored the exact draft, revision, input/contract
hashes, execution/reasoning selection, batch receipts and read positions. It then
completed in another 318.20 seconds. The eight attempts include the intentionally
cancelled attempt; the final response's six-call counter covers only the resumed
run. Four draft batches were accepted across both runs. This demonstrates
checkpoint preservation for this trial, not general reliability after arbitrary
interruptions or UI-circuit loss.

The failed fresh trial accepted seven batches containing 74 edit operations but
never requested final validation. It spent approximately 886 seconds in 11 read
calls (47 read items), 516 seconds in seven edit calls, 360 seconds in two
180-second request timeouts and 35 seconds in the final deadline-cancelled call.
Its first edit arrived at 166.72 seconds. No truncated-output response was
observed. A structurally complete draft cannot be inferred from these counters;
without a returned proposal, business-requirement acceptance was not evaluated.

An offline audit reproduced a context-cache defect in `WorkflowAiContext`: draft
read resources omit entity IDs, while the optimized cache replaces entries by
resource and offset. Reading nodes 1 and 2 together returned both, but retained
only node 2 in cached observations. This affects the shared experimental context
and may contribute to repeated reads. Live selectors were deliberately not
recorded, so it is **not a proven sole cause** of the timeout. Draft reads are
excluded from the duplicate-read metric; zero there does not rule out repeated
draft reads. The defect was not changed during this frozen test round.

**Verdict:** the new engine completed editing and one complex resumed workflow,
but fresh complex creation failed even with 30 minutes. It is not qualified for
default promotion. Defaults remain `current` / Zen / `glm-5.3-flash` / max. This
round neither compares engines under matching conditions nor establishes that
max is faster or more accurate than high. The successful resumed trial also
exceeded the original five-minute promotion gate.

Evidence is under `artifacts/ai-sdk-max-20261004/`: `plan.json`, `summary.json`,
frozen `runner/` and `knowledge/`, per-trial progress/evidence, returned proposals,
independent acceptance files and `cache-audit.json`. Allowlisted metrics exclude
provider keys, prompts, raw tool arguments and private reasoning. Token sums in
the summary cover reported responses only, not unknown usage on cancelled or
timed-out requests. Frozen binary and fixture hashes were rechecked after all
three trials. Package contract hash:
`5357aa5b8a140750ad70df37d34eae93b75da6b992e20b23e0553dd2f992c307`.

| Frozen artifact | SHA-256 |
| --- | --- |
| AuthoringEval.dll | `37e7c8c64b44f6debf91dcff74dd89660f6e6b3bc9a5e0ddfdecd8c75c726e1e` |
| Flowbit.Service.dll | `a1c33dbc2f42ef2c52ee5860c88d3edbf5d118128952b4ea78c11b089604ee01` |
| Flowbit.Infrastructure.dll | `632772b57b76c8842fabfebc2b346992e8beba2d3279ec069b3924573d8d7191` |

Release build and package export passed. Offline acceptance commands (identical
in PowerShell and Bash, with PowerShell 7 installed) were:

```text
dotnet artifacts/ai-sdk-max-20261004/runner/AuthoringEval.dll --check-only artifacts/ai-sdk-max-20261004/02-modify/result.json --fixture modify --policy strict-v1 --output artifacts/ai-sdk-max-20261004/02-modify/strict
pwsh -File Flowbit/tools/AuthoringEval/Check-Complex.ps1 -Directory artifacts/ai-sdk-max-20261004/03-complex-resume -Policy functional-v2
pwsh -File Flowbit/tools/AuthoringEval/Check-Complex.ps1 -Directory artifacts/ai-sdk-max-20261004/03-complex-resume -Policy strict-v1 -OutputDirectory artifacts/ai-sdk-max-20261004/03-complex-resume/strict
dotnet run --project artifacts/ai-sdk-max-20261004/cache-audit/CacheAudit.csproj -c Release
```

The first three checks passed; the last reproduced the cache defect without a
provider call. This turn changed test-result documentation only; no product UI
changed and no new browser run was performed. Earlier automated/browser results
remain in the preceding section. Changed documentation links/anchors and
`git diff --check` passed; a source/evidence scan found no copy of the provider key.

<a id="framework-removal"></a>
## Framework removal verification — 2026-10-04

Microsoft Agent Framework was removed at the user's request. Current and optimized
remain custom Flowbit execution modes, with current/Zen/Flash/max still shipped.
The SDK package, runtime adapters, native-tool transport/history, agent interfaces,
framework-only tests, transport probe and framework matrix branches are removed.
Shared retry/output recovery, validation, redaction, draft receipts, progress and
checkpoint behavior remain. The separate optimized draft-read cache defect is
unchanged and documented; this removal makes no new latency or live-accuracy claim.

Removed execution configuration fails with `provider_configuration` (503) before
transport. Framework version 2 checkpoints fail `checkpoint_configuration_changed`
(409) under either supported mode. Current/optimized checkpoints retain version 2;
version 1 remains current-only. No database migration or provider-key move is needed.

Validation for the removal:

| Check | Result |
| --- | --- |
| Release solution and evaluation tool builds | Passed |
| Focused AI unit/integration tests | 370/370 passed |
| Offline complex-checker regressions | 12/12 passed |
| Offline creation/preservation regressions | 12/12 passed |
| Matrix control flow with simulated runner outcomes | 4/4 cases passed: qualification, all failures, final failure, insufficient remaining cap |
| Evaluation CLI removed variant/probe | Both rejected before key-file access |
| Chromium with shipped current/max | 8/8 passed |
| Chromium H8/H9 with optimized/max | 4/4 passed |
| Fresh API, UI, Worker and evaluation-tool publishes | No `Microsoft.Agents.AI*` or `Microsoft.Extensions.AI*` packages/assemblies |

The focused test command was:

```text
dotnet test Flowbit/tests/Flowbit.Tests/Flowbit.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~WorkflowAi|FullyQualifiedName~OpenCodeGoProviderRecovery|FullyQualifiedName~WorkflowAuthoringPackage|FullyQualifiedName~WorkflowApiClientAi" --logger "trx;LogFileName=framework-removal.trx" --results-directory artifacts/remove-framework/tests
```

After the documented API/UI publish and browser-test build, Chromium
**151.0.7922.34** ran `AiAuthoringSmokeTests` with `FLOWBIT_BROWSER_AI_VARIANT=shipped`
and `H8|H9` with `FLOWBIT_BROWSER_AI_VARIANT=optimized`, using the suite's real
localhost API/UI and loopback synthetic provider. Shipped UI:
`http://127.0.0.1:61840`; optimized UI: `http://127.0.0.1:61973`. Both disposable
hosts have stopped. Tests exercised Generate, output-limit/transient recovery,
real in-flight Cancel, exact draft continuation, Apply, Undo, PDF/skill flows,
and stale-document/model invalidation. Viewports were **1440×900, 1024×768 and
390×844**; progress/continuation screenshots were inspected. No product layout
changed. Console, page, request and dialog diagnostics were checked separately.
All 14 diagnostic files reported zero page errors, console errors/warnings,
failed requests or unexpected dialogs.

Evidence is under `artifacts/remove-framework/`, with TRX files, dependency audit,
build/checker logs and explicitly simulated matrix evidence. Browser runs are
`artifacts/browser/runs/20261004-081517-45649045` (shipped) and
`artifacts/browser/runs/20261004-081628-c7a63c85` (optimized). No live Zen call,
workflow execution or default promotion was performed. Existing SSH.NET advisory
and unrelated test-analyzer warnings remain. Historical frozen experiment outputs
were preserved outside the new application deliverables.

## Requirements review and parallelism offline verification (2026-10-04)

This records implementation/offline verification only. No Zen requests were made,
no live quality or speed gain is established, and defaults were not promoted.
The [staged plan](../../../docs/plans/ai-assistant-improvement-plan.md) covers
recommendations 1–4 and 7. The reviewed path remains disabled by default.

The pre-change build was frozen from commit
`c4217603503e81d7a58d0d751bffb57b0e27e3d6` under
`artifacts/ai-improvements/baseline/`; cache-fixed and reviewed builds are retained
separately. `Run-Improvements.ps1` produced a key-free 11-trial plan with file hashes
under `artifacts/ai-improvements/dry-run-final/`; its cap guard rejected four prior trials
before creating output. The live comparison remains pending separate authorization.

Focused automated verification passed **397 tests**, including endpoint authorization,
the production OpenAPI schema, transport recovery, source/checklist validation,
blocking review, exact-candidate review after repairs, v3 policy binding, cancellation,
reservations, global provider caps, overlapping/reversed completions, metadata tracing
and mutable-cache behavior. Commands, runnable in PowerShell or Bash:

```text
dotnet test Flowbit/tests/Flowbit.Tests/Flowbit.Tests.csproj -c Release --no-restore --filter "FullyQualifiedName~WorkflowAi|FullyQualifiedName~OpenCodeGoProviderRecovery|FullyQualifiedName~WorkflowAuthoringPackage|FullyQualifiedName~WorkflowApiClientAi|FullyQualifiedName~OpenApiContractTests" --logger "trx;LogFileName=verified-ai.trx" --results-directory artifacts/ai-improvements/tests --nologo
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore --filter "FullyQualifiedName~AiAuthoringSmokeTests|FullyQualifiedName~AiRequirementsSmokeTests" --logger "trx;LogFileName=final-browser.trx" --results-directory artifacts/ai-improvements/tests --nologo
pwsh -File Flowbit/tools/AuthoringEval/Test-Checks.ps1 -Output artifacts/ai-improvements/checker-regressions-final
pwsh -File Flowbit/tools/AuthoringEval/Test-SimpleChecks.ps1 -Runner Flowbit/tools/AuthoringEval/bin/Release/net10.0/AuthoringEval.dll -Output artifacts/ai-improvements/simple-regressions
```

Offline checker verification passed **15 complex** and **12 creation/preservation**
regressions. The new wrong-threshold case found that the previous substring regex
accepted `amount > 100000`; the checker now matches the complete requested threshold.
The checked-in synthetic fixture supplies a repeatable independent oracle. These
results do not retroactively change historical live reports above.

The separately published API/UI passed **12 Chromium 151.0.7922.34 browser cases**:
legacy H6–H9 and reviewed H10/H11. Reviewed UI: `http://127.0.0.1:64496`;
legacy UI: `http://127.0.0.1:64576`. Scenarios cover blocked Apply, targeted repair,
overlapping calls, cancellation of both workers, Continue with fresh analysis/review,
reversed reviewer completion, stale editor/model guards, Apply and Undo. Viewports:
1440×900, 1024×768 and 390×844. Fourteen diagnostic files show zero page errors,
console errors/warnings, failed requests or unexpected dialogs. One initial H11
cleanup failure from a dirty editor was fixed by undoing its edit; the complete
12-case rerun passed.

Scenario-specific manual verification used the Chromium-based Codex in-app browser
at `http://127.0.0.1:64698/workflows/1/edit`, with a separate disposable real API/UI
stack and synthetic upstream. Real clicks/typing exercised generation, overlapping
progress, Enter to expand the checklist, Apply, Escape on the mobile drawer, and
one-step Undo restoring the saved workflow. Desktop/tablet/mobile layouts were
inspected at the same three widths. Console warnings/errors were empty. Temporary
tabs and all verification hosts were closed afterward; nothing was saved or published
through the assistant.

Evidence: `artifacts/browser/runs/20261004-104008-ffffcd99` (reviewed),
`20261004-104040-9108e829` (legacy), `20261004-104111-7a9b0d5b` (manual stack).
Reviewed screenshots include `parallel-review.png`, `requirements-review.png`
and `requirements-blocked.png`; desktop and mobile screenshots were inspected.
Builds and portable package export passed. Local documentation validation checked
884 links/anchors/image references and parsed the new fixture/configuration JSON;
12 pre-existing references to absent refactoring guides are recorded separately in
`artifacts/ai-improvements/pre-existing-doc-link-issues.txt`. No new broken link was
introduced. Existing SSH.NET advisory warnings and
unrelated test-analyzer warnings remain. Live model accuracy, token cost and latency
are still unverified; only deterministic/synthetic and real-browser behavior is covered.

## Requirements review and parallelism live evaluation (2026-10-04)

The user authorized live testing and promotion only if the checks passed, then
raised this comparison's per-trial deadline from 300 to **600 seconds**. The first
300-second trial was interrupted and preserved as one consumed trial, without a
correctness or timing verdict. The replacement comparison carries `PriorTrials=1`.
Earlier five-minute reports above retain their original results.

Neither reviewed configuration met the requirement of three correct fresh complex
completions within ten minutes. Requirements review remains disabled, optimized
execution remains experimental, and application defaults are unchanged. Actual
reviewer overlap was observed, but it did not establish reliable completion or a
speed improvement. Candidate simple/modification/Cancel-Continue tests were not
reached because no configuration qualified.

The comparison fixes OpenCode Zen, `glm-5.3-flash`, max reasoning and optimized
execution. All configurations share the frozen 68-file authoring package and
fixtures. `plan.json` retains the hashes of every runner/package/fixture file;
the script rechecks them before each trial. Both reviewed configurations use the
same frozen binary, changing only the per-run concurrency setting from one to two.
No prompts, implementation, fixtures or model settings changed during the round.

| Trial | Configuration | Result | Elapsed seconds | HTTP calls | Observed input / output tokens | First edit seconds |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| 2 | Pre-change optimized | Both policies passed | 427.94 | 11 | 213,161 / 57,229 | 139.52 |
| 3 | Cache-fixed optimized | Deadline; no proposal | 600.03 | 8 | 121,322 / 47,387 | 189.81 |
| 4 | Reviewed serial 1 | Deadline; no proposal | 600.02 | 7 | 92,436 / 44,274 | 343.32 |
| 5 | Reviewed serial 2 | Deadline; no proposal | 600.01 | 11 | 161,368 / 59,766 | 228.41 |
| 6 | Reviewed serial 3 | Deadline; no proposal | 600.03 | 12 | 203,842 / 45,401 | 160.78 |
| 7 | Reviewed parallel 1 | Deadline; no proposal | 600.02 | 13 | 200,124 / 82,843 | 243.85 |
| 8 | Reviewed parallel 2 | Deadline; no proposal | 600.02 | 9 | 141,485 / 51,295 | 188.76 |
| 9 | Reviewed parallel 3 | Deadline; no proposal | 600.02 | 11 | 173,048 / 54,688 | 226.16 |

Trial 2 passed all 36 `strict-v1` and 78 `functional-v2` checks with zero errors
or warnings. The seven deadline failures returned no final proposal, so their
independent checkers could not run; they count as failed completions, not passes
based on a partial draft. All six reviewed trials failed to qualify. One baseline
trial and one cache-only trial cannot establish a statistically reliable speed
effect or show that the cache fix caused a regression.

| Trial | Accepted edits | Builder context reads / immutable duplicates | Same-revision draft rereads | Recorded validation repairs | Review HTTP calls | Peak traced calls | Summed provider seconds |
| --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 2 | 4 | 15 / 1 | Not instrumented | Not instrumented | 0 | Not instrumented | 427.33 |
| 3 | 5 | 3 / 0 | Not instrumented | 0 | 0 | 1 | 599.51 |
| 4 | 3 | 3 / 0 | 0 | 1 | 0 | 1 | 599.77 |
| 5 | 5 | 5 / 0 | 0 | 0 | 2 | 1 | 599.41 |
| 6 | 4 | 3 / 0 | 0 | 0 | 1 | 1 | 599.42 |
| 7 | 5 | 4 / 0 | 0 | 0 | 3 | 2 | 663.09 |
| 8 | 4 | 3 / 0 | 0 | 1 | 0 | 1 | 599.46 |
| 9 | 5 | 3 / 0 | 0 | 1 | 0 | 1 | 599.47 |

These edit/read counters are the last observed progress snapshots; reviewer reads
belong to isolated worker contexts and are not included in builder counters. All
reviewed runs used one source-analysis batch. Trial 7 reached the two reviewers at
512.10 seconds and recorded **63.71 seconds of actual overlapping provider calls**.
Coverage returned a review; routing requested four draft reads and was canceled
during its following call. Trials 5 and 6 reached coverage review serially, while
trials 4, 8 and 9 remained in construction. No traced transport, truncation or
requirements-repair recovery occurred; validation repairs were recorded in trials
4, 8 and 9. Reducing builder round trips and avoidable reviewer reads is a concrete
area to investigate before another evaluation; this round does not demonstrate
that such a change would pass.

Across the eight 600-second trials, 82 HTTP attempts include seven canceled
attempts. Completed responses reported 1,306,786 input and 442,883 output tokens.
Each canceled call has unknown usage and a 16,384-token reserved allowance:
114,688 additional conservatively charged output tokens across those calls.
These are budget allowances, not measured billed usage; neither these totals nor
the table include unknown consumption from interrupted trial 1. After cancellation,
`evidence.json.run` is the last progress snapshot rather than settled terminal
accounting. Call outcomes and trace durations provide the cancellation/overlap
evidence separately.

The matrix completed normally and `decision.json` selected no candidate. It
consumed **9 of 14 trials**, including the interrupted first trial. No additional
billed round, workflow application/save/publication/execution, or default change
was performed. The production authoring deadline remains 1,800 seconds; only this
comparison's per-trial deadline and qualification limit changed to 600 seconds.

Evidence directories are `artifacts/ai-improvements/live-20261004-01/`
(interruption and partial progress), `live-20261004-02/` (plan, matrix, decision,
per-trial evidence, traces and checker reports), and `ten-minute-plan/` (key-free
600-second dry-run). All three are beneath `artifacts/ai-improvements/`.
The completed command used these arguments, with the private key path substituted
locally; use a fresh output directory for any separately authorized future run:

```text
pwsh -NoProfile -File Flowbit/tools/AuthoringEval/Run-Improvements.ps1 -BaselineRunner artifacts/ai-improvements/baseline/runner/AuthoringEval.dll -CacheRunner artifacts/ai-improvements/cache-fixed/runner/AuthoringEval.dll -Runner artifacts/ai-improvements/reviewed-final/runner/AuthoringEval.dll -Package artifacts/ai-improvements/reviewed-final/knowledge/flowbit-authoring -Output artifacts/ai-improvements/live-20261004-02 -TimeoutSeconds 600 -PriorTrials 1 -KeyFile /private/zen.key -Execute
```

The changed PowerShell script parses successfully and its dry-run recorded the
600-second limit and carried-forward trial count without provider calls. This turn
changes the evaluation script and documentation, with no application/UI change.
The preceding 397 automated tests and 12 Chromium cases remain historical checks;
they were not rerun or represented as live-provider correctness evidence here.
Changed-document validation passed 32 local links/anchors/image references and
parsed the fixture/configuration JSON, with no broken references in the four
changed Markdown files. `git diff --check` passed. No new browser run was needed
for the evaluation-only change.
