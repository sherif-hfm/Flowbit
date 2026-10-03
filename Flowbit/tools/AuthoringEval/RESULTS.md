# Zen GLM Flash evaluation — 2026-10-03

The [targeted-adoption follow-up](#targeted-adoption-follow-up) also retained
defaults after testing all three remaining scenarios. The original strict
comparison below remains unchanged as historical evidence.

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
