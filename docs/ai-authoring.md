# AI workflow authoring and portable skill

[Documentation home](index.md) · [UI guide](ui-guide.md) · [API reference](api-guide.md) · [Node reference](node-reference.md)

The Flowbit.Ui workflow editor's **AI assistant** creates and modifies complete workflow definitions from requirements. It supports the same 21 node types and canonical properties as the editor/runtime, including JavaScript and NCalc, REST services, messages, timers, conditional and boundary events, advanced gateways, shared variables, roles, assignments, and multi-instance outcomes. It does not restrict workflows to approvals.

The assistant returns a proposal for review. Applying a proposal changes the editable diagram; saving creates an unpublished version using the normal editor action. Generation does not save, publish, start an instance, run generated scripts, or call authored service endpoints. Business correctness and external integration contracts still need review even when Flowbit validation succeeds.

## Use the assistant

1. Open a new or existing workflow in Flowbit.Ui and select **AI assistant**.
2. Select the configured provider/model and enter your own provider API key. OpenCode Go is the first provider. Keys are held only for the active editor session and sent for the specific generation request; they are not saved in workflow JSON or the engine settings. Closing the assistant/session clears the key.
3. Describe the process or requested modification. You can supply BRD/SRS text or upload a PDF. Inspect extracted text, especially scanned pages where OCR can misread identifiers, numeric thresholds, or negation.
4. Answer any questions about missing business rules and review the proposed changes, assumptions, validation results, and external prerequisites.
5. Apply the proposal to the current draft and review the diagram. Existing-workflow edits retain the family key and unchanged IDs/coordinates and apply as one undoable change. If the diagram changed while the model was working, regenerate against the current revision.
6. Save the unpublished version when ready, then use the existing explicit publication flow.

The provider receives the submitted requirements, document text, conversation context, and the workflow needed to make an edit. Recognized workflow credential fields are redacted before model calls. Avoid putting unrelated confidential material in requirements; redaction cannot identify every sensitive business value inside arbitrary text or JSON.

Text extraction occurs on the Flowbit server. Scanned PDF pages use the configured local OCR tools, so scanned input does not depend on a provider-specific file/vision API. Encrypted, malformed, over-limit, or unreadable documents produce an error; review any extraction warnings before generation. See [deployment configuration](deployment.md) for OCR dependencies and limits.

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

Generation with the portable package needs no Flowbit repository, running Flowbit server, or Flowbit AI-provider key. The external agent uses its own model/account and document tools. The skill does not install a model, obtain credentials, provide OCR executables, or automatically connect to OpenCode Go. A raw provider API also does not discover local skill files: Flowbit's internal context assembler explicitly supplies the same packaged instructions and relevant resources.

## Validation and readiness

The generated JSON Schema checks the structural canonical contract. It includes all current serialized model properties, rejects unknown configuration fields, and preserves arbitrary business JSON inside JSON-valued defaults. It excludes legacy document shims; ordinary editor legacy import behavior is unchanged.

Schema matching alone cannot check graph semantics, expression rules, catalog existence, deployment settings, or script behavior. When you have Flowbit authorization, optional `POST /api/workflows/validate` accepts the complete workflow JSON and performs the same read-only authored/normalized validation and readiness checks used by the assistant. The endpoint requires the normal workflow-author role and no AI-provider key. It does not save, publish, execute JavaScript, or call external services; the [API reference](api-guide.md) owns its exact response and status contract.

Without a server, the external agent can still produce a workflow file, but must report semantic/deployment validation as pending. Import it into Flowbit for authoritative review and save validation. Missing shared-catalog contracts can block saving; Worker publication configuration can block publication. Generated scripts require behavior review even after syntax validation, and REST endpoints/configuration references must be supplied and verified by the workflow owner.

## Package maintenance and export

The source entrypoint and authoring procedure live in `authoring/flowbit-authoring/`. The .NET exporter derives the schema from the actual serialized workflow model, obtains enum values from the model's constant catalogs, and copies owning developer/node/BPMN/API documentation and canonical examples into a self-contained package. Source-only links outside the package become plain labels. Historical refactoring plans are excluded. Documentation and examples remain maintained in their existing owning files; there is no separately hand-maintained AI schema or example catalog.

The API build and publish produce the folder and ZIP under the application output's `authoring/` directory. The API verifies their schema/resource hashes against the running model and caches the same immutable content for internal prompts and download. Each prompt contains the complete node, BPMN, and developer behavior references as well as the schema, so requests written in Arabic retain the full feature vocabulary; up to two examples are chosen by relevance. The core package has a 1,000,000-character startup limit and selected examples have a 40,000-character budget; the configured total request-context limit applies separately. Oversized context is rejected rather than silently truncated. ZIP entry order, timestamps, and text line endings are deterministic. `manifest.json` includes `FormatVersion`, `SchemaHash`, `ContractHash`, the hash of every resource, and `Compatibility` with the model type and supported node catalog. Use the skill package from your target Flowbit release; offline packages cannot automatically detect that a distant server has upgraded.

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
