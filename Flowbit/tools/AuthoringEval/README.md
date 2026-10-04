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
pwsh -File Flowbit/tools/AuthoringEval/Test-Checks.ps1 -ExampleResult artifacts/ai-sdk-max-20261004/03-complex-resume/result.json -Output artifacts/authoring-eval/checker-regressions
pwsh -File Flowbit/tools/AuthoringEval/Test-SimpleChecks.ps1 -Runner Flowbit/tools/AuthoringEval/bin/Release/net10.0/AuthoringEval.dll -Output artifacts/authoring-eval/preservation-regressions
```

The historical result file is optional local evidence, not a bundled fixture;
substitute another retained valid complex result if it is unavailable. These
checks inspect its JSON only and do not load or execute the historical runner.

See [AI authoring](../../../docs/ai-authoring.md#execution-variants-and-evaluation),
[configuration](../../../docs/deployment.md#ai-authoring-and-local-ocr), and the
[browser suite](../../tests/Flowbit.BrowserTests/README.md).
