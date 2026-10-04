# AI assistant improvement plan

Agreed 2026-10-04. Implements recommendations **1–4 and 7** from the
[eight-item list](ai-assistant-recommendations.md). Stage status is recorded below;
planned behavior must not be interpreted as already implemented.

## Decisions and boundaries

- One staged plan, implemented and verified one stage at a time.
- Parallelism initially covers analysis and review; construction has one writer.
- Unresolved requirements block applicable proposals. AI review is not a proof
  of business correctness; deterministic definition validation stays separate.
- Preserve selected provider/model, existing run budgets, redaction, editor
  snapshot protection, atomic Apply, and separate save/publication actions.
- No durable sessions, parallel branch generation, general specialist-agent
  infrastructure, framework dependency, or database migration in this scope.

## Stages

### 1. Measurement baseline and tracing (items 4 and 7)

Freeze the pre-change evaluation build and package. Extend the existing
`Flowbit.Ai.Authoring` metrics with correlated run, provider-attempt, read, edit,
validation, review, and retry-wait spans. Capture safe duration, purpose, usage,
retry, repair, and read metadata. Distinguish elapsed time from summed provider
time, and same-revision draft rereads from existing immutable duplicate reads.
Make evaluation recording safe for concurrency. Never export prompts, outputs,
source text, credentials, draft bodies, or raw exception messages.

Gate: scripted-provider tests verify accounting, correlation, cancellation,
missing usage, and secret-free metadata.

### 2. Draft context cache fix (item 1)

Key mutable excerpts by target, entity ID, and offset; retain workflow-wide
identity. Preserve different entities at equal offsets and replace only matching
reads. Preserve limits, redaction, immutable references, and cache invalidation
after accepted edits. Mutable excerpts stay out of checkpoints.

Gate: inspect observations directly for identity, replacement, invalidation,
bounded eviction, and sanitation regressions. Correct the owning documentation
without rewriting historical evaluation outcomes.

### 3. Blocking requirements review, initially serial (item 2)

Build a bounded source-grounded checklist from frozen inputs, tracking examined
ranges so unread tails cannot count as analysis. Supply the checklist to the
builder. Before accepting completion run requirement-coverage and
routing/roles/outcomes reviews on the exact candidate. Validate source/entity
references locally. Missing behavior triggers bounded targeted repairs;
essential ambiguity triggers clarification. A failed or incomplete review cannot
authorize a proposal. Edits invalidate verdicts. Show an expandable AI-review
checklist while preserving separate deterministic validation.

Gate: structurally valid omissions, wrong roles/thresholds, bypassed tasks,
fabricated evidence, ambiguity, and exhausted repair paths cannot bypass review.

### 4. Controlled parallel analysis and review (item 3)

Share provider dispatch, retries, timeouts, redaction, cancellation, and run
budgets across builder/analysis/review. Execute independent source batches and
the two reviewers with at most two active calls per run, immutable inputs, and
isolated worker context. The coordinator alone mutates the draft, checkpoints,
and ordered progress stream. Reserve calls/output before dispatch and account
conservatively for failed/unknown-usage attempts. Add a process provider-call cap
of four alongside the existing whole-run gate. Cancel workers together on
termination. Pause rather than skip review when budget is insufficient. Progress
must represent overlapping calls without misleading single-call timers.

Gate: barrier-controlled tests prove overlap, caps, shared accounting,
out-of-order completion handling, cancellation, and committed-state recovery.

### 5. Independent evaluation (item 7)

Compare pre-change optimized, cache-fixed optimized, reviewed serial, and
reviewed parallel configurations at identical model/provider/reasoning/settings.
Use one exploratory complex trial per first two configurations, three per
reviewed configuration, and candidate simple/modify/Cancel-Continue: 11 trials
within the existing 14-trial cap. Preserve failures and immutable provenance.
Report both existing checker policies; never use AI review as its own oracle.
Report correctness, latency, usage, calls, repairs, rereads, and actual overlap.

Gate: retain three fully correct fresh complex completions within 300 seconds,
then passing simple/modification/continuation. Longer diagnostics cannot satisfy
the five-minute gate. Defaults do not change automatically. Billed calls require
separate authorization after offline/browser verification.

## Interfaces and compatibility

- Preserve `current` and `optimized`. Add server options
  `RequirementsReviewEnabled=false`, `MaxParallelAnalysisCalls=2` (1–2), and
  `MaxConcurrentProviderCalls=4`.
- Add optional structured review results and additive active-call progress
  metadata. Preserve NDJSON ordering and one terminal result.
- Review-enabled runs use checkpoint v3 with orchestration-policy binding.
  Retained checklist information is untrusted; rebuild source excerpts and rerun
  final review on Continue. Preserve v1/v2 legacy behavior and reject incompatible
  continuation before transport.

## Verification and documentation

Run focused AI/provider/package/client tests, offline checker regressions, and
real API/UI Chromium scenarios. Browser coverage includes review, blocked Apply,
repair, overlapping calls, Cancel/Continue, stale editor, Apply and Undo at
1440×900, 1024×768, and 390×844; inspect keyboard behavior, screenshots, and console.

Update owning AI authoring, API, UI, deployment, runtime, evaluation, and browser
documentation with each affected stage. Validate links and regenerate packaged
authoring references when needed. Historical reports retain their original facts.

## Execution record

| Stage | Implementation | Verification |
| --- | --- | --- |
| 1 — measurement | Implemented | Frozen pre-change runner/package; correlated metadata and secret-canary tests passed |
| 2 — cache | Implemented | Entity/offset identity, eviction, sanitation and edit invalidation tests passed |
| 3 — requirements | Implemented, opt-in | Blocking findings, targeted repair, source/evidence validation, ambiguity, checkpoint and UI tests passed |
| 4 — parallelism | Implemented, opt-in | Overlap, global cap, reservations, cancellation, out-of-order results, Continue and real-browser checks passed |
| 5 — evaluation | Harness implemented; live gate pending | 15 complex and 12 simple/modify checker regressions passed; fixed 11-trial dry-run and cap guard verified; no billed calls |

The [verification record](../../Flowbit/tools/AuthoringEval/RESULTS.md#requirements-review-and-parallelism-offline-verification-2026-10-04)
records commands, browser evidence and remaining limits. Review stays disabled by
default. Live correctness and speed have **not** been established; running the billed
comparison and promoting defaults remain separate decisions.

[Documentation home](../index.md) · [AI authoring](../ai-authoring.md)
