# Authoring evaluation

This opt-in tool runs the editor assistant's agent engine (the Flowbit AI Lab v3 approach) against fixed
fixtures, with independent checkers. It uses the evaluated configuration:

- one AI model, held in memory as the AI models page would store it: OpenCode Zen (`https://opencode.ai/zen/v1/`) with
  `glm-5.3-flash`, `high` reasoning and the key from `--key-file`;
- the run limits of 40 model calls, 32,768 output tokens per call and two validation follow-ups;
- a run deadline of 1,800 seconds by default.

It builds the same `WorkflowAiAuthoringService` and `OpenAiCompatibleChatClientFactory` as the API, with a fixed
model store instead of the `AiAuthoring.Models` engine setting, so it needs no database. The former `current`/`optimized` variants, effort
choices and requirements review no longer exist; the tool rejects their old arguments. The
[historical evaluation report](RESULTS.md) keeps earlier results without rewriting their outcomes; its commands
apply only to historical builds.

Live calls use the authorized account and consume credits. A past evaluation does not authorize a new billed
round. The key is read from a private file and never printed or placed in command arguments. Only synthetic
fixtures, final proposals and allowlisted metrics are written. No workflow is applied, saved, published or
executed.

## Build and run

Run from the repository root with .NET 10. Commands are identical in PowerShell and Bash; the trial and checker
scripts also require PowerShell 7 (`pwsh`):

```text
dotnet build Flowbit/tools/AuthoringEval/AuthoringEval.csproj -c Release
dotnet run --project Flowbit/tools/AuthoringExport -c Release -- . artifacts/authoring-eval/knowledge
```

For one authorized trial, substitute your private key-file path and use a fresh output directory. Fixtures are
`simple`, `modify` and `complex`. Invalid values are rejected before the key is read or the provider contacted.

```text
dotnet Flowbit/tools/AuthoringEval/bin/Release/net10.0/AuthoringEval.dll --key-file /private/zen.key --package artifacts/authoring-eval/knowledge/flowbit-authoring --output artifacts/authoring-eval/single --fixture complex --fixtures Flowbit/tools/AuthoringEval/fixtures
pwsh -File Flowbit/tools/AuthoringEval/Check-Complex.ps1 -Directory artifacts/authoring-eval/single -Policy strict-v1
```

| Argument | Default | Meaning |
| --- | --- | --- |
| `--fixture` | `complex` | `simple`, `modify` or `complex` |
| `--timeout-seconds` | `1800` | The run deadline, 30–3600 seconds; also the trial's pass limit |
| `--resume-test` | `false` | Cancel a real in-flight model call after at least one completed call, then Continue |
| `--policy` | `strict-v1` | Checker policy for `simple`/`modify`; see below |
| `--diagnostics` | `false` | Record exception types and code locations, never messages |
| `--check-only` | — | Check a saved `result.json` offline, with no key or provider call |

Complex trials require the independent checker's verdict in addition to `evidence.json`; a structurally valid
proposal alone is insufficient.

**The resume test.** It cancels the model call that starts after the first completed call, 500 ms after its HTTP
request begins, as the panel's Cancel does. It then sends Continue with the last checkpoint, as the panel does.
The trial passes only if a real HTTP attempt was cancelled and the first resumed request carries exactly the
cancelled request's conversation. That comparison uses hashes of roles, text, tool calls, tool results and
reasoning. The overall deadline spans both runs.

## Evidence

Each trial directory holds:

- **`evidence.json`**: the outcome and its code, elapsed time, the per-call metadata and the last run summary
  (model calls, input, cached and output tokens, follow-ups, workflow size). It also records the prompt
  fingerprint, the docs fingerprint and file count, the limits and the resume verdict.
  - The per-call metadata is status, finish reason, token usage and the names of the tools called.
- **`progress.ndjson`**: the stream frames' type, stage, code and run summary.
- **`trace.json`**: the `Flowbit.Ai.Authoring` spans.
- **`result.json`**: the final result without its conversation state.

Prompts, messages, reasoning, tool arguments, provider bodies and keys are never written. The tool refuses to
save evidence that contains the key.

After an evaluation deadline, `evidence.json.run` may be the last progress snapshot, not settled accounting.
Usage figures are operational counts, not provider billing totals.

## Trial rounds

`Run-Trials.ps1` runs a round of trials with frozen inputs:

- **Default round:** three `complex`, one `simple`, one `modify` and one `complex-resume` trial, within a cap
  of six. `-Trials` chooses others (`complex`, `simple`, `modify`, `complex-resume`), `-Cap` raises the cap
  (at most 20) and `-PriorTrials` accounts for trials already used. Never reset the count to hide failures.
- **Frozen inputs:** it hashes the runner directory, the package and the fixtures, and checks them again before
  every trial.
- **Provenance:** it records each trial as started before transport, so an interrupted process still counts. It
  keeps every failure and reports both checker policies; a trial passes only with the strict policy.
- **Plan first:** planning is the default and writes only `plan.json`, with no key and no provider calls.

```text
pwsh -File Flowbit/tools/AuthoringEval/Run-Trials.ps1 -Runner artifacts/authoring-eval/frozen/runner/AuthoringEval.dll -Package artifacts/authoring-eval/frozen/knowledge/flowbit-authoring -Output artifacts/authoring-eval/round-01-plan
```

After separate authorization for billed calls, use a new output directory and add
`-Execute -KeyFile /private/zen.key`. The round writes `trials.json` and `summary.json`. Copy the complete
runner directory and package to a frozen location first. Changing the implementation during a round invalidates
the round; preserve its evidence and start a new one.

## Acceptance policies and offline checks

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

Validation checks structure, references, topology, routing and configuration; it does not prove business
correctness. The checkers cover the fixtures' expected behavior only.

See [AI authoring](../../../docs/ai-authoring.md#evaluation),
[configuration](../../../docs/deployment.md#ai-authoring-and-local-ocr), and the
[browser suite](../../tests/Flowbit.BrowserTests/README.md).
