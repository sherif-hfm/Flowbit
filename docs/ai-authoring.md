# AI workflow authoring and portable skill

[Documentation home](index.md) · [UI guide](ui-guide.md) · [API reference](api-guide.md) · [Node reference](node-reference.md)

The Flowbit.Ui workflow editor's **AI assistant** creates and modifies complete workflow definitions from requirements. It supports the same 21 node types and canonical properties as the editor/runtime, including JavaScript and NCalc, REST services, messages, timers, conditional and boundary events, advanced gateways, shared variables, roles, assignments, and multi-instance outcomes. It does not restrict workflows to approvals.

The assistant returns a proposal for review. Applying a proposal changes the editable diagram; saving creates an unpublished version using the normal editor action. Generation does not save, publish, start an instance, run generated scripts, or call authored service endpoints. Business correctness and external integration contracts still need review even when Flowbit validation succeeds.

## Use the assistant

1. Open a new or existing workflow in Flowbit.Ui and select **AI assistant**.
2. Select the configured provider/model and enter your own provider API key. OpenCode Zen is the default provider endpoint. Keys are held only for the active editor session and sent for the specific generation request; they are not saved in workflow JSON or the engine settings. Closing the assistant/session clears the key.
3. Describe the process or requested modification. You can supply BRD/SRS text or upload a PDF. Inspect extracted text, especially scanned pages where OCR can misread identifiers, numeric thresholds, or negation.
4. Follow progress as the assistant reads relevant references, builds the draft in small steps, validates it, and recovers from temporary provider failures. A run lasts up to 30 minutes by default and stays on the selected model. **Cancel** stops it; **Continue** resumes the last complete draft step while this panel stays open. The panel shows retained draft steps, nodes and connections; only the per-run timer and call count reset on Continue. Answer missing-business-rule questions by sending a new message.
5. Review the proposed changes, assumptions, validation results, and external prerequisites. Partial draft steps are private working state and cannot be applied.
6. Apply the proposal to the current draft and review the diagram. Existing-workflow edits retain the family key and unchanged IDs/coordinates and apply as one undoable change. If the diagram changed while the model was working, regenerate against the current revision.
7. Save the unpublished version when ready, then use the existing explicit publication flow.

The assistant uses a bounded loop of reference/source reads, atomic workflow edits, and validation. Text execution accepts one complete JSON command surrounded by ordinary explanatory prose, but rejects multiple adjacent command objects and fields that do not belong to the selected command. If an output reaches the model limit, the incomplete response is discarded and the assistant requests a smaller complete edit; it does not concatenate broken JSON. Temporary throttling and connection/server failures receive bounded retries with backoff and provider wait hints. Authentication failures, exhausted account quota, refusals, and response-size violations require attention instead of repeated retries. Validation failures produce focused repair feedback; repeated failures stop the run.

Reference reads advertise exact schema names and a combined excerpt budget. Guide searches prefer matching section headings over contents links. The assistant sees the remaining run time and call/output budgets and can build known portions while reading rules for later features; original requirements remain available throughout. A timed-out model call reduces the next edit-batch limit. Successful edits also adjust that limit: batches shrink when reported output usage approaches the allowance and grow toward the configured limit when there is room. Checkpoints retain accepted read plans and bounded output-recovery settings, without advancing the draft revision for planning alone. If transient retries are exhausted, the run pauses with its checkpoint for a later attempt. Exhausted model-call timeouts report the request timeout separately from temporary provider outages. The shipped GLM Flash configuration uses max reasoning, a 180-second call timeout, and a 16,384-token initial allowance. Each run is bounded by 30 minutes, 50 model calls, and 262,144 output tokens; reaching any limit can still require Continue.

Each complete draft step can be retained as a checkpoint in the current Blazor Server panel circuit. **Continue** sends that checkpoint with the original frozen request and selected provider/model for another bounded run. It does not include unsent changes to requirements, source text, or catalog selection; use **Send** for changed instructions. Editing the diagram makes the checkpoint stale. Closing/resetting the panel, changing identity or model, navigating away, or losing the UI circuit clears continuation state. Checkpoints are not database records or durable saved workflow versions, and are not shown as proposals. Reloading the page cannot restore them.

Continue also restores bounded reference and source excerpts already read by the
assistant. The checkpoint keeps only read positions; the server validates them
and rebuilds the text from the verified package and original inputs with fresh
redaction. Mutable draft excerpts are read again when needed. This reduces repeated
research after a pause, although the model can still request additional reads.
Compact prompts keep the same 8,000-character requirements excerpt and retain
recent reference batches within the working excerpt budget. Compaction reduces
that budget when necessary instead of discarding every batch except the latest.
In the `current` variant, drafts up to 14,000 JSON characters remain complete during normal compaction;
larger drafts use an entity index with bounded reads for omitted details. If the
context still exceeds a configured limit or the provider rejects its size, even
a small draft falls back to that index.

Empty or unreadable successful provider responses use the same bounded transport
retries without changing the draft. Repeated failures pause with
`provider_invalid_response`. Authentication, billing, refusals and unsupported
completion formats remain terminal; their last emitted checkpoint stays available
in the open panel.

The provider receives the submitted requirements, document text, conversation context, and the workflow needed to make an edit. Recognized workflow credential fields are redacted before model calls. Request-derived context is scrubbed without rewriting internal command names or JSON member names. Public packaged references and the protocol instructions retain their exact text, so a short credential such as `node` cannot corrupt the schema or command vocabulary. Avoid putting unrelated confidential material in requirements; redaction cannot identify every sensitive business value inside arbitrary text or JSON.

Text extraction occurs on the Flowbit server. Scanned PDF pages use the configured local OCR tools, so scanned input does not depend on a provider-specific file/vision API. Encrypted, malformed, over-limit, or unreadable documents produce an error; review any extraction warnings before generation. See [deployment configuration](deployment.md) for OCR dependencies and limits.

## Execution variants and evaluation

`WorkflowAi:ExecutionVariant` selects `current` (the default), `optimized`, or
`agent-framework`. The latter two are experimental until the live acceptance
gates pass; installing a framework does not guarantee faster or correct results.
The selected variant and reasoning effort stay fixed for the run. The provider
and model remain OpenCode Zen and the configured model; a variant does not switch
models or obtain different provider capacity.

Both experimental variants supply compact contracts derived from the verified
schema and relevant guide excerpts before the first call. Immutable reads are
deduplicated separately from mutable draft reads; accepted edits discard stale
draft excerpts. Draft context preserves business fields and removes layout first,
using the remaining context budget instead of the current runner's fixed
14,000-character cutoff. If the semantic draft still cannot fit, a disclosed
entity index and bounded reads remain available. Following a timeout or truncation,
batches shrink. They grow by two operations only after two accepted batches each
use less than half the call deadline and reported output allowance, up to the
configured maximum. Missing usage never triggers growth.

The framework prototype pins `Microsoft.Agents.AI` 1.23.0 in Infrastructure and uses
one `ChatClientAgent` per bounded model step. Its four native tools are
`read_authoring_context`, `apply_draft_batch`, `finish_proposal`, and
`request_clarification`. Flowbit dispatches the complete tool command through the
same authoring kernel used by text execution. SDK automatic tool loops, retries,
and retained chat history are disabled; Flowbit owns budgets, context rebuilding,
checkpoints, and serial edits. Finishing validates locally and stops immediately
on success; errors return as focused repair feedback. There is no fallback from
native tools to text, and no execution, save, publish, shell, or network tool.

New checkpoints use version 2, binding the execution variant, reasoning effort,
model profile, and provider endpoint as well as the original inputs and package.
Changing those server settings rejects continuation before any provider call;
start a new request. Version 1 checkpoints continue through `current`. Neither
format survives loss of the open UI circuit. Progress now shows the wait time for
the current model call separately from elapsed run time and retained draft counts.

The repeatable [authoring evaluation tool](../Flowbit/tools/AuthoringEval/README.md)
contains synthetic creation/modification fixtures and 36 complex-procurement
checks. It compares variants and reasoning efforts with a 300-second trial limit,
counts pauses/failures as failures, and never promotes defaults automatically.
Validated structure is necessary but does not establish coverage of every
business requirement. Review the evaluation evidence before changing defaults.
The [2026-10-03 evaluation](../Flowbit/tools/AuthoringEval/RESULTS.md) retained
the existing default: framework/high passed two of three fresh complex trials,
short of the required three; optimized candidates did not pass every requirement.
The subsequent targeted-adoption round treated approved action-label aliases as
warnings and separately passed all three retained definitions' functional checks.
It still retained `current`/max: simple creation passed, editing returned an
invalid result, and live Cancel/Continue preserved the checkpoint but did not
finish within five minutes. Neither the framework nor a structural validation
result guarantees full requirement completion. See the report for the unchanged
historical verdicts and new evidence.

## Use the same skill in another agent

Download the `flowbit-authoring.zip` package through the assistant's skill download or authenticated `GET /api/workflows/ai/skill`. Extract the **entire `flowbit-authoring` folder**, including `SKILL.md`, `references/`, `examples/`, and `manifest.json`, into the native skill location supported by your agent. Do not copy only `SKILL.md`: its schema, references, and examples are required for full coverage.

The entrypoint uses ordinary skill frontmatter (`name` and `description`) and relative file references. These project-local installation directories are supported by the agents' documentation; the final directory must contain `SKILL.md` directly, without another nested `flowbit-authoring` folder:

| Agent | Extracted skill directory in your project | Installation reference |
| --- | --- | --- |
| Claude Code | `.claude/skills/flowbit-authoring/` | [Claude Code skills](https://code.claude.com/docs/en/skills) |
| Cursor | `.cursor/skills/flowbit-authoring/` | [Cursor skills](https://cursor.com/docs/skills) |
| OpenCode | `.opencode/skills/flowbit-authoring/` | [OpenCode skills](https://opencode.ai/docs/skills/) |
| Codex | `.agents/skills/flowbit-authoring/` | [Codex skills](https://learn.chatgpt.com/docs/build-skills) |

For a downloaded archive in your current project, this example installs the full package for Codex. Substitute the parent skill directory from the table for another agent. Review or remove an older package before extracting an update; do not combine releases.

```powershell
Expand-Archive -LiteralPath ./flowbit-authoring.zip -DestinationPath ./.agents/skills
```

```bash
unzip flowbit-authoring.zip -d .agents/skills
```

Restart or reload the agent's skill discovery if needed. You can also explicitly ask any file-capable agent to read the extracted `SKILL.md` and follow its local references, without installing it globally.

Example requests:

> Use the flowbit-authoring skill to create a workflow from this SRS. Include the described message callback, timeout escalation, parallel reviews, and quorum rules. Ask about missing business rules and return the complete JSON plus prerequisites.

> Use the flowbit-authoring skill to update this existing workflow: add a non-interrupting reminder boundary to the review task. Preserve unrelated configuration, IDs, permissions, and layout.

Generation with the portable package needs no Flowbit repository, running Flowbit server, or Flowbit AI-provider key. The external agent uses its own model/account and document tools. The skill does not install a model, obtain credentials, provide OCR executables, or automatically connect to OpenCode. A raw provider API also does not discover local skill files: Flowbit's internal context assembler explicitly supplies the same packaged instructions and relevant resources.

## Validation and readiness

The generated JSON Schema checks the structural canonical contract. It includes all current serialized model properties, rejects unknown configuration fields, and preserves arbitrary business JSON inside JSON-valued defaults. It excludes legacy document shims; ordinary editor legacy import behavior is unchanged.

Schema matching alone cannot check graph semantics, expression rules, catalog existence, deployment settings, or script behavior. When you have Flowbit authorization, optional `POST /api/workflows/validate` accepts the complete workflow JSON and performs the same read-only authored/normalized validation and readiness checks used by the assistant. The endpoint requires the normal workflow-author role and no AI-provider key. It does not save, publish, execute JavaScript, or call external services; the [API reference](api-guide.md) owns its exact response and status contract.

Without a server, the external agent can still produce a workflow file, but must report semantic/deployment validation as pending. Import it into Flowbit for authoritative review and save validation. Missing shared-catalog contracts can block saving; Worker publication configuration can block publication. Generated scripts require behavior review even after syntax validation, and REST endpoints/configuration references must be supplied and verified by the workflow owner.

## Package maintenance and export

The source entrypoint and authoring procedure live in `authoring/flowbit-authoring/`. The .NET exporter derives the schema from the actual serialized workflow model, obtains enum values from the model's constant catalogs, and copies owning developer/node/BPMN/API documentation and canonical examples into a self-contained package. Source-only links outside the package become plain labels. Historical refactoring plans are excluded. Documentation and examples remain maintained in their existing owning files; there is no separately hand-maintained AI schema or example catalog.

The API build and publish produce the folder and ZIP under the application output's `authoring/` directory. The API verifies their schema/resource hashes against the running model and caches the same immutable content for internal prompts and download. The internal assistant starts with the capability catalog and authoring procedure, then reads exact packaged schema sections, guide excerpts, and examples through bounded internal read commands. The full resource index remains available regardless of the request language. Requirements, source pages, and private draft entities are also available through bounded reads; prompt excerpts identify their extent, while original request text remains available. Context compaction first uses smaller indexes/excerpts, then drops older working observations if needed, retaining access to original source requirements. A conservative byte-based token estimate reserves room for output and a safety margin against the configured model context window. If required context still does not fit, the run pauses or rejects oversized input. The verified core package retains its 1,000,000-character startup limit; request character, byte, call, token, and time limits are separate deployment controls. ZIP entry order, timestamps, and text line endings are deterministic. `manifest.json` includes `FormatVersion`, `SchemaHash`, `ContractHash`, the hash of every resource, and `Compatibility` with the model type and supported node catalog. Use the skill package from your target Flowbit release; offline packages cannot automatically detect that a distant server has upgraded.

To export manually from the repository root, use the same command in PowerShell and Bash:

```powershell
dotnet run --project Flowbit/tools/AuthoringExport/AuthoringExport.csproj -- . artifacts/authoring
```

```bash
dotnet run --project Flowbit/tools/AuthoringExport/AuthoringExport.csproj -- . artifacts/authoring
```

The output contains `artifacts/authoring/flowbit-authoring/` and `artifacts/authoring/flowbit-authoring.zip`. This command needs the .NET 10 SDK only for building/exporting; reading the exported skill in another agent needs neither the SDK nor the repository.

Schema/property coverage, all canonical examples, strict decoder behavior, deterministic export, integrity checks, and layout are covered by `WorkflowAuthoringPackageTests`. UI behavior additionally requires the repository's real-browser verification, including apply/undo, stale-response protection, document extraction, and narrow/wide layouts.

An independent package-only evaluation generated a unanimous parallel collection review with a post-approval script, then added a non-interrupting 24-hour timer reminder while preserving existing objects. Both outputs passed the exported schema and Flowbit's strict parser, authored validation, normalization, normalized validation, and read-only readiness checks with durable publication enabled. The evaluation did not execute either workflow or prove arbitrary requirements correct; the package still requires semantic validation for each generated result. Its feedback also corrected the collection examples' input descriptions and clarified how start input feeds a process-declared MI collection.
