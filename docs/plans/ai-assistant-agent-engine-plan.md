# AI assistant agent engine

Agreed 2026-10-16/17. The editor's AI assistant replaces its command-loop engine with one model
agent that controls authoring through file tools, the approach measured in the Flowbit AI Lab v3
experiments. Stage status is recorded below; planned behavior must not be interpreted as
already implemented. The [AI authoring guide](../ai-authoring.md) describes supported behavior.

## Why

The command-loop engine had the model answer with one JSON command per response (read, edit,
validate, finish, clarification), applied as typed draft edits, with Flowbit-owned context
compaction, transport retries, checkpoints and an optional requirements review. Its live
evaluations did not pass the promotion gates: no qualifying complex run within 600 seconds, and
a fresh complex run failed even at 30 minutes (see the [evaluation report](../../Flowbit/tools/AuthoringEval/RESULTS.md)).

Lab v3 gave one GLM agent a short prompt and six file tools. The agent reads the documentation and
examples itself, writes the workflow itself and checks it; the host only gates with validation,
sends errors back at most twice, and lays out the result. Lab v3 recorded these live results:

| Requirements | Duration | Requests |
| --- | --- | --- |
| Procurement | 139–243 seconds | 8–12 |
| Supplier onboarding | 800–1,750 seconds | 19–26, no validation follow-up in the last six runs |

These results came from a lab, not from this repository's evaluation fixtures.

## Decisions

| # | Decision |
| --- | --- |
| 1 | Replace the command-loop engine, its `current`/`optimized` variants, typed draft edits, requirements review and parallel analysis. |
| 2 | The model reads the lab's doc set, served from the verified authoring package under the lab's paths: `index.md`, `getting-started.md`, `bpmn-support.md`, `node-reference.md`, `developer-guide.md`, `deployment.md`, and the examples with their READMEs (63 files). |
| 3 | The lab's two-line prompt, verbatim, plus one line for editing an existing diagram. |
| 4 | New diagrams get the lab's compact lane layout; edits keep positions with Flowbit's edit layout. |
| 5 | All structural errors are reported; definition rules report Flowbit's first error. Each error has a code, JSON path, element, and message. |
| 6 | A diagram the user changed between messages replaces `workflow.json`, announced in one fixed line. |
| 7 | Tool names and descriptions are the lab's, verbatim, except one clause of `validate_item` that does not apply to Flowbit's single node schema. |
| 8 | Lab limits: 40 model requests, a 1,800-second run deadline, 32,768 output tokens per call, two follow-ups, `high` reasoning, no per-call timeout, no automatic retries. |
| 9 | Use Microsoft Agent Framework 1.23.0, as the lab does. This reverses the framework removal of 2026-10-04. |

Settled during design:

- **Sync only real edits.** Sync replaces the agent's file only when the editor differs from both that file and the diagram the agent last saw.
- **Labelled data blocks.** PDF pages and selected catalog contracts are appended to the message as labelled blocks.
- **Cancel and Continue.** Cancel aborts the stream and keeps the last checkpoint taken at the start of a model call. Continue resumes with the input that call had not yet stored, so any API replica can continue.
- **Credentials in the message.** Literal credential values from the current workflow are replaced in the message and in document pages, as before.

## Stages

| Stage | Content | Status |
| --- | --- | --- |
| 0 | Packages; GLM reasoning replay and compact tool results; Agent Framework resume behaviour checked with mocked HTTP | Implemented |
| 1 | Collecting structural check, located errors, per-node reachability, stable credential placeholders, compact layout | Implemented |
| 2 | The engine: agent, tools, docs view, end-of-turn check, checkpoints, conversation state, provider client | Implemented |
| 3 | Contracts, service, endpoints, settings, panel; old engine and its tests removed; documentation | Implemented |
| 4 | Browser tests with a tool-calling test provider | Implemented |
| 5 | Evaluation tool for the new engine | Implemented; verified offline, then one authorized live round |
| 6 | Full verification and real-browser check | Implemented |

Live evaluation with a real provider account is not part of these stages. It needs separate authorization.

## Verification (2026-10-19, mocked providers only)

- **Build.** `dotnet build Flowbit/Flowbit.slnx -c Release` succeeded with no NU1605, NU1608 or MSB3277 warnings. The
  only warnings are the existing NU1903 advisories for `SSH.NET`, which Testcontainers brings into the test project.
- **Tests.** `dotnet test Flowbit/Flowbit.slnx -c Release`: 2,348 passed, 0 failed.
- **Sensitivity checks.** Two checks confirmed that the tests detect a disabled feature:
  - Without the start-of-call checkpoint layer, the request-cap resume test failed.
  - With the collecting structural check reduced to its first error, the two structural-error tests and the
    validation-tool test failed (3 of 186).

  Both changes were reverted and the solution rebuilt with `--no-incremental`.
- **Browser.** The complete Chromium suite passed: 53 of 53 against the published API and UI. It includes:
  - the AI scenarios H6–H9 at 1440×900, 1024×768 and 390×844, driven by a loopback provider that answers with tool
    calls;
  - the OIDC scenario O3.

  Their screenshots show the proposal with open questions, progress during a model call, a stopped run with
  Continue, and the compact layout of a new diagram. No console or page errors were recorded.
- **Evaluation tool.** The rewritten tool and `Run-Trials.ps1` were verified offline; see the
  [evaluation report](../../Flowbit/tools/AuthoringEval/RESULTS.md#agent-engine-tooling-2026-10-19).
- **Documentation.** The changed Markdown files have no new broken links.
  - 12 links point into `docs/refactoring/`, which this checkout does not contain. These links predate this work.
  - The skill's 11 package-relative links resolve in the exported package.
- No live provider call was part of this verification. Validation checks structure, references, topology, routing
  and configuration; it does not prove business correctness.

## Live round (2026-10-19, authorized by the user)

One `Run-Trials.ps1` round with the default six trials ran against OpenCode Zen `glm-5.3-flash` with `high`
reasoning. Every trial ended with a proposal that passed Flowbit's validation, save readiness and publication
checks within 21–99 seconds, with no validation follow-up.

**Three of six passed their checkers:**

- one of the three fresh complex trials;
- the complex trial with Cancel and Continue, whose resend of the stopped request was verified;
- the modify trial.

**What failed:**

- Two complex trials added a merging gateway before Committee vote, which is equivalent routing but one more node
  and flow; one of them also mislabelled Submit and used the role `Requester`.
- The simple trial named its start event `start` instead of `Start`.

See the [evaluation report](../../Flowbit/tools/AuthoringEval/RESULTS.md#agent-engine-live-round-2026-10-19).
One round of six trials is not a speed or accuracy distribution.

[Documentation home](../index.md) · [AI authoring](../ai-authoring.md)
