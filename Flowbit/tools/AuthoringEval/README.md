# Authoring evaluation

This opt-in tool compares Flowbit's custom `current` and `optimized` execution
on OpenCode Zen `glm-5.3-flash`. Microsoft Agent Framework, its native transport
probe and the `FinalizeFramework` matrix mode have been removed. The
[historical evaluation report](RESULTS.md) retains earlier results without
rewriting their outcomes; its framework commands apply only to historical builds.

Live calls use the authorized account and may consume credits. A past evaluation
does not authorize a new billed round. The key is read from a private file and
never printed or placed in command arguments. Only synthetic fixtures, final
proposals and safe metrics are written. No workflow is applied, saved, published
or executed. Defaults remain current/Zen/Flash/max.

## Build and run

Run from the repository root with .NET 10. Commands are identical in PowerShell
and Bash; matrix/checker commands also require PowerShell 7:

```text
dotnet build Flowbit/tools/AuthoringEval/AuthoringEval.csproj -c Release
dotnet run --project Flowbit/tools/AuthoringExport -c Release -- . artifacts/authoring-eval/knowledge
```

For one authorized trial, substitute your private key-file path and use a fresh
output directory. Supported variants are `current` and `optimized`; supported
fixtures are `simple`, `modify` and `complex`. Invalid values are rejected before
reading the key or contacting the provider.

```text
dotnet Flowbit/tools/AuthoringEval/bin/Release/net10.0/AuthoringEval.dll --key-file /private/zen.key --package artifacts/authoring-eval/knowledge/flowbit-authoring --output artifacts/authoring-eval/single --variant optimized --effort max --fixture complex --fixtures Flowbit/tools/AuthoringEval/fixtures
pwsh -File Flowbit/tools/AuthoringEval/Check-Complex.ps1 -Directory artifacts/authoring-eval/single -Policy strict-v1
```

Complex trials require the independent checker's verdict in addition to
`evidence.json`; a structurally valid proposal alone is insufficient. Add
`--resume-test true` to a separate trial to cancel a real in-flight request after
an accepted edit and verify the exact checkpoint before continuing. The overall
trial deadline spans both runs; final per-run counters cover only the resumed run.

Each trial records elapsed/first-edit time, calls, tokens, retries, reads and
accepted batches without prompts, keys or private reasoning. `progress.ndjson`
provides safe live metrics; `evidence.json` records the final outcome.
`--diagnostics true` records exception types and code locations, never messages or
command values. The default deadline is 300 seconds; `--timeout-seconds 1800`
uses a separate 30-minute diagnostic budget (allowed range 30–3600). Never count
a longer run as passing the five-minute gate or overwrite prior evidence.

## Current/optimized comparison

For a separately authorized matrix, in either shell:

```text
pwsh -File Flowbit/tools/AuthoringEval/Run-Matrix.ps1 -KeyFile /private/zen.key -Package artifacts/authoring-eval/knowledge/flowbit-authoring -Output artifacts/authoring-eval/run-01
```

The matrix runs current/max as a baseline, then optimized at low/high/max. It
repeats the fastest fully correct optimized candidate twice. Qualification
requires three fully correct fresh completions within 300 seconds, followed by
simple creation, preservation of an existing workflow, and a separate complex
Cancel/Continue pass. This is an accuracy/completion gate, not a claim of a
particular speed improvement over current.

The matrix retains its 14-trial cap. `-PriorTrials` accounts for already consumed
trials, and `-SkipBaseline` avoids repeating a recorded baseline. If insufficient
trials remain for final validation, no candidate is selected. Never reset the
counter to hide failures or interrupted runs. `decision.json` records the result
but does not change application defaults. Automated and browser checks are also
required before promotion.

For repeatability, freeze the runner and package, record their hashes, and pass
the copied DLL with `-Runner`. Changing implementation during a comparison
invalidates that comparison; preserve its evidence as preliminary. Historical
framework runners are not supported application deliverables.

## Acceptance policies and offline checks

### Requirements and parallelism comparison

`Run-Improvements.ps1` keeps Zen/GLM-5.3-Flash/max, optimized execution, a common
frozen package, and a 600-second deadline fixed (the user raised this comparison's
limit from five to ten minutes on 2026-10-04). It compares one pre-change and one
cache-fixed complex trial, three reviewed-serial and three reviewed-parallel trials,
then the qualifying candidate's simple, modify and Cancel/Continue cases: at most
11 trials within the existing 14-trial cap. Pass `-PriorTrials` to account for already
consumed trials. `-TimeoutSeconds` sets the common deadline and qualification limit
(30–3600 seconds; default 600). The older matrix and single-trial default remain
300 seconds; their historical five-minute results are not reclassified.
Interrupted trials are recorded before transport and count toward
the cap; use a fresh output directory for each invocation.

Freeze each complete runner directory before a comparison. Both reviewed configurations
use the same new runner; `--review true --analysis-workers 1` selects serial review
and `--analysis-workers 2` allows overlap. Review defaults to false. The script hashes
all runner/package/fixture files and rechecks them before every trial. It preserves
all failures, reports both independent checker policies, and selects only a candidate
with three strict complex passes plus passing simple/modify/continuation. It never
changes application defaults. The historical one-trial baselines are exploratory,
not sufficient to establish a statistically reliable speed improvement.

The command below only records a plan and hashes, with no key and no provider calls;
it works in PowerShell and Bash. Adjust frozen paths for the local evidence directory.

```text
pwsh -File Flowbit/tools/AuthoringEval/Run-Improvements.ps1 -BaselineRunner artifacts/ai-improvements/baseline/runner/AuthoringEval.dll -CacheRunner artifacts/ai-improvements/cache-fixed/runner/AuthoringEval.dll -Runner artifacts/ai-improvements/reviewed-final/runner/AuthoringEval.dll -Package artifacts/ai-improvements/reviewed-final/knowledge/flowbit-authoring -Output artifacts/ai-improvements/ten-minute-plan -TimeoutSeconds 600
```

After separate authorization for billed calls, use a new output directory and add
`-Execute -KeyFile /private/zen.key`. Keep private keys outside the repository.
`trace.json` contains correlated metadata spans; `evidence.json` includes active/peak
calls, total provider duration, per-run accounting and builder rereads. Compare elapsed
time separately from summed provider duration, especially with overlap. Original
requests, provider response bodies, keys and reasoning are never trace tags.

After an evaluation deadline, `evidence.json.run` may be the last progress snapshot,
not settled terminal accounting. Use recorded call outcomes and trace spans to
identify canceled attempts and overlap. Report completed-response token usage
separately from unknown usage on canceled/failed calls; their reserved allowances
are conservative budget charges, not measured billing totals.

The [2026-10-04 live comparison](RESULTS.md#requirements-review-and-parallelism-live-evaluation-2026-10-04)
did not qualify either reviewed configuration: all six reviewed trials reached the
600-second deadline. Real-provider reviewer overlap was observed in one trial,
but review remains disabled by default. The report retains every trial and the
interrupted five-minute attempt without reclassifying it as a timing verdict.

### Checker policies

`strict-v1` checks exact action wording with 36 complex-procurement checks.
`functional-v2` uses 78 checks, separating behavior from the approved aliases
Approve/Approved, Reject/Rejected, Complete/Completed, Submit/Submitted, and
Director approved on the director approval route. Known aliases generate warnings;
unknown, blank or opposite labels fail. Routing, roles, selectability, conditions,
quorum references and existing-content preservation remain blocking. These
policies do not rename generated models or relax production validation.

Use `--policy functional-v2` for a single trial. The `--check-only result.json`
mode checks a saved simple/modify result without a key or provider call. Checker
regressions also run offline, using a retained valid complex proposal:

```text
pwsh -File Flowbit/tools/AuthoringEval/Test-Checks.ps1 -Output artifacts/authoring-eval/checker-regressions
pwsh -File Flowbit/tools/AuthoringEval/Test-SimpleChecks.ps1 -Runner Flowbit/tools/AuthoringEval/bin/Release/net10.0/AuthoringEval.dll -Output artifacts/authoring-eval/preservation-regressions
```

`fixtures/complex-valid.json` is a checked-in synthetic checker fixture derived from
the retained procurement definition with exact action wording and explicit Worker
prerequisites; it contains no live-run metrics or credentials. `-ExampleResult` can
instead inspect another retained valid result. Fifteen regressions cover labels,
roles, routing, selectability, quorum IDs, an omitted branch, a bypassed task and
an incorrect threshold. Threshold matching requires the complete `amount > 10000`
expression; the old substring check could incorrectly accept `100000`. Historical
reports are retained unchanged and must not be silently reclassified. These checks
inspect JSON only and never load a historical runner or call a provider.

See [AI authoring](../../../docs/ai-authoring.md#execution-variants-and-evaluation),
[configuration](../../../docs/deployment.md#ai-authoring-and-local-ocr), and the
[browser suite](../../tests/Flowbit.BrowserTests/README.md).
