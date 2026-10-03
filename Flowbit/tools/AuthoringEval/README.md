# Authoring evaluation

The [2026-10-03 evaluation report](RESULTS.md) records the initial live comparison:
no candidate met all promotion gates, so existing defaults were retained.
The later targeted-adoption round also retained defaults: editing returned an
invalid result and the live cancellation/resume trial exceeded its deadline.

## Targeted framework adoption

The explicitly selected adoption policy is separate from the original speed
comparison. `functional-v2` splits behavior from action wording; the approved
aliases are Approve/Approved, Reject/Rejected, Complete/Completed and
Submit/Submitted, plus Director approved on the Director approval route. A known
alias records a warning with expected/actual text. Unknown, blank or opposite
labels fail. Routing, permissions, selectability, conditions and quorum references
remain blocking. Existing content in modification fixtures must remain exact.
These are evaluation checks, not automatic production renaming or relaxed
workflow validation.

`FinalizeFramework` requires `-PriorTrials 11` and the retained trial folders
07, 10 and 11 from the first comparison. It rechecks their definitions into a new
output folder without rewriting history, then runs only trials 12–14: simple,
modify, and complex with actual HTTP Cancel/Continue. It does not apply the
fastest/20% selection rule or promote application settings. A blocking failure
retains the existing default. This round consumed all 14 authorized trials;
starting another billed round requires a newly agreed budget.

The command is the same in PowerShell and Bash (PowerShell 7 installed):

```text
pwsh -File Flowbit/tools/AuthoringEval/Run-Matrix.ps1 -Mode FinalizeFramework -PriorTrials 11 -RetainedResults artifacts/ai-variants/matrix-02 -KeyFile /private/zen.key -Package artifacts/ai-variants/knowledge/flowbit-authoring -Output artifacts/authoring-eval/finalist-new -Runner artifacts/authoring-eval/frozen/AuthoringEval.dll
```

This documents the completed round; do not rerun it to reset the trial counter.
Finalist evidence records the policy and runner hashes. Original strict 2/3
framework passes remain historical; the separate reassessment passed behavior
with one label warning. A passed `evidence.json` for complex creation must still
be combined with the independent checker verdict.

Offline checker regressions require no key or provider calls. Both commands are
identical in PowerShell and Bash; use fresh output directories:

```text
pwsh -File Flowbit/tools/AuthoringEval/Test-Checks.ps1 -ExampleResult artifacts/ai-variants/matrix-02/11-agent-framework-high-complex/result.json -Output artifacts/authoring-eval/checker-regressions
pwsh -File Flowbit/tools/AuthoringEval/Test-SimpleChecks.ps1 -Runner Flowbit/tools/AuthoringEval/bin/Release/net10.0/AuthoringEval.dll -Output artifacts/authoring-eval/preservation-regressions
```

The single-trial CLI accepts `--policy functional-v2`. Its `--check-only
result.json` mode checks a saved simple/modify result without reading a key or
creating an authoring service. It writes only offline acceptance evidence.

## Original strict comparison

This opt-in tool compares `current`, `optimized`, and `agent-framework` execution
on OpenCode Zen `glm-5.3-flash`, using `low`, `high`, and `max` reasoning. Live calls
use the authorized account and may consume credits. The key is read from a file
and never printed or placed in command arguments. Only synthetic fixtures, final
proposals and safe metrics are written. No generated code executes and no workflow
is saved, applied, or published.

Run from the repository root with .NET 10. These build/export commands are the
same in PowerShell and Bash:

```text
dotnet build Flowbit/tools/AuthoringEval/AuthoringEval.csproj -c Release
dotnet run --project Flowbit/tools/AuthoringExport -c Release -- . artifacts/authoring-eval/knowledge
```

Use a fresh output directory for each comparison. PowerShell (substitute your
private key-file location):

```powershell
pwsh -File Flowbit/tools/AuthoringEval/Run-Matrix.ps1 -KeyFile /private/zen.key -Package artifacts/authoring-eval/knowledge/flowbit-authoring -Output artifacts/authoring-eval/run-01
```

Bash, with PowerShell 7 installed for the matrix/checker:

```bash
pwsh -File Flowbit/tools/AuthoringEval/Run-Matrix.ps1 -KeyFile /private/zen.key -Package artifacts/authoring-eval/knowledge/flowbit-authoring -Output artifacts/authoring-eval/run-01
```

The matrix starts with current/max, alternates optimized/framework at low/high/max,
then repeats each variant's fastest fully correct candidate twice. Every fresh
workflow trial has a 300-second deadline, identical limits and a frozen knowledge
package. A paused/error/timeout run fails; no automatic Continue, recorded-response
replay, or human repair contributes to a passing fresh trial. `Check-Complex.ps1`
performs 36 checks over six lanes, 29 nodes, 33 connections, parallel reviews,
amount routing, multi-instance quorum/fallback, script output, timer and prerequisites.

Finalists require 3/3 fully correct fresh completions within 300 seconds. Framework
wins only if it is the sole passing finalist or its median is at least 20% faster
than optimized. The selected candidate must also pass simple creation, preservation
of an existing workflow, and a separate live Cancel/Continue trial. The initial
matrix cap is 14 workflow trials; `-PriorTrials` accounts for earlier preliminary
or interrupted trials, and `-SkipBaseline` avoids repeating an already recorded
baseline. If the cap leaves too little room for final validation, no candidate is
selected. Never reset the counter to conceal failed or interrupted trials.

`decision.json` records the outcome but never changes application defaults.
Automated and real-browser checks must also pass before promotion. Failure to meet
the gates leaves the existing configuration intact; a framework migration is not
justified by structural validation or one fast run alone.

For one trial, the following command is identical in both shells:

```text
dotnet Flowbit/tools/AuthoringEval/bin/Release/net10.0/AuthoringEval.dll --key-file /private/zen.key --package artifacts/authoring-eval/knowledge/flowbit-authoring --output artifacts/authoring-eval/single --variant optimized --effort low --fixture complex --fixtures Flowbit/tools/AuthoringEval/fixtures
```

Use `--fixture probe --variant agent-framework` for a small native-tool/usage
compatibility check before the matrix; it is not a workflow trial. Unit tests cover
session headers, reasoning settings, native framing and cancellation. Each trial
records elapsed/first-edit time, calls, tokens, retries, reads and accepted batches,
without provider prompts, keys or private reasoning. For repeatability, freeze the
built runner and package, record their hashes, and pass the copied DLL with
`-Runner`. Changing implementation during a comparison invalidates that comparison;
retain its evidence as preliminary instead of combining candidates across builds.

See [AI authoring](../../../docs/ai-authoring.md#execution-variants-and-evaluation),
[configuration](../../../docs/deployment.md#ai-authoring-and-local-ocr), and the
[browser suite](../../tests/Flowbit.BrowserTests/README.md).
