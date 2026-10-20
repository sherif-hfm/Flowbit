# AI workflow authoring and portable skill

[Documentation home](index.md) · [UI guide](ui-guide.md) · [API reference](api-guide.md) · [Node reference](node-reference.md)

The Flowbit.Ui workflow editor's **AI assistant** creates and modifies complete workflow definitions from requirements. It supports the same 21 node types and canonical properties as the editor/runtime, including JavaScript and NCalc, REST services, messages, timers, conditional and boundary events, advanced gateways, shared variables, roles, assignments, and multi-instance outcomes. It does not restrict workflows to approvals.

The assistant returns a proposal for review. Applying a proposal changes the editable diagram; saving creates an unpublished version using the normal editor action. Generation does not save, publish, start an instance, run generated scripts, or call authored service endpoints. Business correctness and external integration contracts still need review even when Flowbit validation succeeds.

## Use the assistant

1. Open a new or existing workflow in Flowbit.Ui and select **AI assistant**.
2. Select one of the AI models a settings administrator [configured](#configure-ai-models). The first model is selected by default. You do not enter a key: the assistant receives only each model's id and name, and the API calls the model's endpoint with the key stored for it. If no model is configured, the panel says so and links to the **AI models** page.
3. Describe the process or requested modification. You can supply BRD/SRS text or upload a PDF. Inspect extracted text, especially scanned pages where OCR can misread identifiers, numeric thresholds, or negation.
4. Follow progress while the assistant reads Flowbit's guides and examples, writes the workflow, checks it and corrects it. The panel shows what the assistant read, saved or checked since its previous model call, the wait for the current call, the elapsed time, and the calls used. A run lasts up to 30 minutes and 40 model calls by default. **Cancel** stops it; **Continue** resumes a stopped run from its last completed model call while this panel stays open.
5. Review the proposal: the assistant's message, its open questions and assumptions, validation results, save and publication readiness, and the workflow JSON. Answer the open questions or ask for changes by sending another message. The assistant keeps the conversation, including what it read and the workflow it wrote.
6. Apply the proposal to the current draft and review the diagram. Existing-workflow edits retain the family key and unchanged IDs/coordinates and apply as one undoable change. If the diagram changed while the model was working, send another message so the assistant works from the current diagram.
7. Save the unpublished version when ready, then use the existing explicit publication flow.

## Configure AI models

Settings administrators (any role in `Settings.RequiredRole`) manage the models on **Manage → AI models** (`/ai-models`, see the [UI guide](ui-guide.md#ai-models)) or through the [AI models API](api-guide.md#ai-models). Each model has a display name, the base URL of an OpenAI-compatible chat-completions API, the provider's model name, an optional reasoning effort (`low`, `medium`, `high`, or not sent) and an API key. The first model in the list is the assistant's default.

- **Endpoint.** The base URL must use HTTPS, except plain HTTP on a loopback address for a local server, and must not contain user information, a query or a fragment. Enter the base URL, such as `https://opencode.ai/zen/v1/`, not the `…/chat/completions` address.
- **Key.** Keys are write-only: they are stored in the `AiAuthoring.Models` engine setting and no page or API returns them. Leave the key blank when editing to keep the stored one. A key needs 16 to 4,096 characters; for an endpoint without keys, enter a unique placeholder of at least 16 characters.
- **Access.** Every author who can open the assistant can use every configured model, and runs are billed to the configured provider account.
- **Compatibility.** The shipped evaluation covers GLM-5.3-Flash on OpenCode Zen with `high` reasoning. Another provider must accept OpenAI chat-completions requests with native tool calls and `max_completion_tokens`, plus `reasoning_effort` when one is configured.

[Deployment and operations](deployment.md#ai-model-catalog) covers storage, upgrades and recovery.

## How the assistant works

One model agent controls authoring itself, the approach measured in the Flowbit AI Lab v3 experiments. Flowbit keeps only what must not depend on the model: limits, credentials, validation, layout, and the editor's apply rules.

- **Message.** The model receives three lines of instructions and your message exactly as typed. Attached PDF pages are appended once each as `--- <file>, page <n> ---` blocks, and selected shared-variable contracts as a `--- Selected shared variables ---` block. Literal credential values of the current workflow are replaced by `[credential removed]` wherever they appear.
- **Tools.** The model has six tools: `get_files_list` and `get_file` read the documentation, the canonical examples, or its own files; `save_append_file` writes a file in parts; `edit_file` replaces exact text in a saved file; `validate_workflow` and `validate_item` check the saved workflow or single nodes, flows, variables and lanes. The tools cannot reach anything else.
- **Documentation it reads.** [Getting started](getting-started.md), the [documentation home](index.md), [BPMN support](bpmn-support.md), the [node and property reference](node-reference.md), the [developer guide](developer-guide.md), [deployment](deployment.md), and the [example catalog](../examples/README.md) with every canonical example. They come from the verified authoring package; other packaged files, such as the API guide and the skill's own instructions, are not visible to the model.
- **Files.** The model writes `workflow.json` and `open-questions.md` in a private, in-memory workspace. For an existing diagram, Flowbit first saves the current diagram there as `workflow.json`, with credentials replaced by placeholders, and the model changes it.
- **Check after each turn.** When the model ends its turn, Flowbit checks `workflow.json`: the strict canonical structure (every problem is reported), Flowbit's definition validation (the first problem it finds), credential placeholders, and whether newly added or disconnected work can be reached from an entry event. Each error names its code, JSON path, the node, flow, variable or lane it is in, and what is wrong. The same errors go back to the model at most twice per run (`MaxNudges`).
- **Proposal.** A valid workflow becomes a proposal: credentials are restored, the diagram is laid out (a new diagram gets a compact lane layout; an edit keeps existing positions and places additions to the right), and Flowbit validates it again, including save and publication readiness.
- **Other outcomes.** If the model leaves an existing workflow unchanged, the result is an **answer** with no proposal. A workflow that is still invalid or unsaved after the follow-ups ends as **invalid**; the conversation continues with your next message.

The engine runs on Microsoft Agent Framework 1.23.0 with Microsoft.Extensions.AI tool calling and the OpenAI .NET SDK against the selected model's OpenAI-compatible chat-completions endpoint. Compatible reasoning, such as GLM's, is replayed on later tool requests, and tool results are sent as compact JSON.

### Limits and stops

The shipped limits are 40 model requests per run (request 41 is refused before it is sent), one 1,800-second deadline for the whole run, 32,768 output tokens per model call, and two validation follow-ups. Each configured model sets its own reasoning effort. A single model call has no separate timeout, because long reasoning calls can take several minutes.

Nothing is retried automatically. A provider error, a response truncated at the output limit, the request limit, or the deadline **pauses** the run with its reason; **Continue** resumes it. Authentication and quota errors need a settings administrator to check the model's key or provider account on the AI models page first; a context-size error needs a new conversation.

### Conversation, Cancel and Continue

- **Where the conversation lives.** The conversation state (the agent session, the assistant's files and its read positions) is kept only in the open panel's Blazor Server circuit. It travels with each request as compressed, opaque state, and the API checks it whenever it returns: format, its binding to the selected model, message roles and content types, complete tool exchanges, size limits, and the absence of the provider key. It contains credential placeholders, never credential values. Any API replica can resume it.
- **Model changes.** A conversation belongs to its configured model, provider model name, endpoint and reasoning effort. If an administrator changes one of them, the next message fails with `checkpoint_configuration_changed` and you start a new conversation; if the model is deleted, the panel reloads the list and starts over. Replacing only the key keeps the conversation.
- **Checkpoints.** Each model call starts with a checkpoint. **Cancel**, a lost connection, or a stopped run keeps the last checkpoint, and **Continue** resumes with the input that call had not yet stored. Continue requires the editor to be unchanged since the run started.
- **Diagram changes.** When you edit or apply the diagram between messages, your next message replaces `workflow.json` with the editor's diagram, and the model is told so in one line: `workflow.json now contains the current diagram from the editor.` An unapplied proposal, or an editor unchanged since the last turn, keeps the assistant's own file, including an unfinished one.
- **Discarded.** A new conversation, selecting another model, an identity change, closing the panel, navigation, or losing the UI circuit discards the conversation. Reloading the page cannot restore it.

### Credentials and privacy

The provider of the selected model receives your messages, document text, selected catalog contracts, the conversation, and the documentation and workflow files the model reads. Recognized workflow credential fields are replaced by location-bound placeholders before anything is sent, and stay placeholders in the conversation state. Their values come back only in the final proposal. Avoid putting unrelated confidential material in requirements; redaction cannot identify every sensitive business value inside arbitrary text or JSON.

The model's API key stays on the server: it is never sent to the browser or stored in the conversation state or workflow JSON, and the API removes it from replies and refuses a returned state that contains it. Because settings administrators choose where requests are sent and which account pays, restrict `Settings.RequiredRole` accordingly.

Text extraction occurs on the Flowbit server. Scanned PDF pages use the configured local OCR tools, so scanned input does not depend on a provider-specific file/vision API. Encrypted, malformed, over-limit, or unreadable documents produce an error; review any extraction warnings before generation. See [deployment configuration](deployment.md) for OCR dependencies and limits.

The `Flowbit.Ai.Authoring` activity source and meter record metadata only: run outcome, request and follow-up counts, provider-call outcomes and durations, tool names with success, and validation error counts. They never contain prompts, responses, source text, workflow contents, file paths, credentials, or raw exception messages. No external telemetry exporter is installed automatically.

## Validation and readiness

The generated JSON Schema checks the structural canonical contract. It includes all current serialized model properties, rejects unknown configuration fields, and preserves arbitrary business JSON inside JSON-valued defaults. It excludes legacy document shims; ordinary editor legacy import behavior is unchanged.

Schema matching alone cannot check graph semantics, expression rules, catalog existence, deployment settings, or script behavior. When you have Flowbit authorization, optional `POST /api/workflows/validate` accepts the complete workflow JSON and performs the same read-only authored/normalized validation and readiness checks used for the assistant's proposals. The endpoint requires the normal workflow-author role and calls no AI model. Because that role can also create, publish, and delete definitions, give an external agent a token only for this call; the skill tells the agent to use it for nothing else and never print or store it. It does not save, publish, execute JavaScript, or call external services; the [API reference](api-guide.md) owns its exact response and status contract.

Without a server, the external agent can still produce a workflow file, but must report semantic/deployment validation as pending. Import it into Flowbit for authoritative review and save validation. Missing shared-catalog contracts can block saving; Worker publication configuration can block publication. Generated scripts require behavior review even after syntax validation, and REST endpoints/configuration references must be supplied and verified by the workflow owner.

## Use the same skill in another agent

Download the `flowbit-authoring.zip` package with **Download Flowbit AI skill** under **Use with another AI agent** at the end of the assistant panel, or with authenticated `GET /api/workflows/ai/skill`. Extract the **entire `flowbit-authoring` folder**, including `SKILL.md`, `references/`, `examples/`, and `manifest.json`, into the native skill location supported by your agent. Do not copy only `SKILL.md`: its schema, references, and examples are required for full coverage.

The entrypoint uses ordinary skill frontmatter (`name` and `description`) and relative file references. These project-local installation directories are supported by the agents' documentation; the final directory must contain `SKILL.md` directly, without another nested `flowbit-authoring` folder:

| Agent | Extracted skill directory in your project | Installation reference |
| --- | --- | --- |
| Claude Code | `.claude/skills/flowbit-authoring/` | [Claude Code skills](https://code.claude.com/docs/en/skills) |
| Cursor | `.cursor/skills/flowbit-authoring/` | [Cursor skills](https://cursor.com/docs/skills) |
| OpenCode | `.opencode/skills/flowbit-authoring/` | [OpenCode skills](https://opencode.ai/docs/skills/) |
| Codex | `.agents/skills/flowbit-authoring/` | [Codex skills](https://learn.chatgpt.com/docs/build-skills) |

Cursor and OpenCode also read `.agents/skills/` and `.claude/skills/`, so a copy in `.agents/skills/` serves Codex, Cursor, and OpenCode, and a copy in `.claude/skills/` serves Claude Code, Cursor, and OpenCode.

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

Generation with the portable package needs no Flowbit repository, running Flowbit server, or Flowbit AI-provider key. The external agent uses its own model/account and document tools. The skill does not install a model, obtain credentials, provide OCR executables, or automatically connect to OpenCode.

Import the result in the workflow editor with **File → Load JSON**, review the diagram, and select **Save new version**. The editor does not lay out an imported diagram, so the skill gives the agent the editor's shape sizes and spacing to place nodes itself.

## Package maintenance and export

The source entrypoint and authoring procedure live in `authoring/flowbit-authoring/`. The .NET exporter derives the schema from the actual serialized workflow model, obtains enum values from the model's constant catalogs, and copies the Markdown and JSON documentation under `docs/`, the [runtime reference](../Flowbit/README.md), and the canonical examples into a self-contained package. Internal planning records under `docs/plans/` are excluded, because they record decisions and superseded engines rather than supported behavior. Source-only links outside the package, including links to those records, become plain labels. Documentation and examples remain maintained in their existing owning files; there is no separately hand-maintained AI schema or example catalog.

The API build and publish produce the folder and ZIP under the application output's `authoring/` directory. At startup the API verifies their schema/resource hashes against the running model, caches the same immutable content for download, and builds the assistant's documentation view from it, failing fast if one of the six guides or the examples is missing. ZIP entry order, timestamps, and text line endings are deterministic. `manifest.json` includes `FormatVersion`, `SchemaHash`, `ContractHash`, the hash of every resource, and `Compatibility` with the model type and supported node catalog. Use the skill package from your target Flowbit release; offline packages cannot automatically detect that a distant server has upgraded.

To export manually from the repository root, use the same command in PowerShell and Bash:

```powershell
dotnet run --project Flowbit/tools/AuthoringExport/AuthoringExport.csproj -- . artifacts/authoring
```

```bash
dotnet run --project Flowbit/tools/AuthoringExport/AuthoringExport.csproj -- . artifacts/authoring
```

The output contains `artifacts/authoring/flowbit-authoring/` and `artifacts/authoring/flowbit-authoring.zip`. This command needs the .NET 10 SDK only for building/exporting; reading the exported skill in another agent needs neither the SDK nor the repository.

Schema/property coverage, all canonical examples, strict decoder behavior, deterministic export, integrity checks, and layout are covered by `WorkflowAuthoringPackageTests`. UI behavior additionally requires the repository's real-browser verification, including apply/undo, stale-response protection, document extraction, and narrow/wide layouts.

<a id="execution-variants-and-evaluation"></a>

## Evaluation

The [authoring evaluation tool](../Flowbit/tools/AuthoringEval/README.md) runs the assistant against fixed creation, modification and complex procurement fixtures with independent checkers. Live runs use a real provider account and are made only with explicit authorization; failures are kept. The [evaluation report](../Flowbit/tools/AuthoringEval/RESULTS.md) keeps earlier results unchanged, including those of the replaced command-loop engine and the Microsoft Agent Framework variant removed on 2026-10-04. The [agent engine plan](plans/ai-assistant-agent-engine-plan.md) records why the assistant was rebuilt this way.
