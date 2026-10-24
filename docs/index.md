# Flowbit developer documentation

Flowbit is a **BPMN-aligned JSON workflow engine** with a visual editor, an HTTP API, and a PostgreSQL-backed runtime. These guides describe the current checkout: how to execute a first approval through HTTP or Flowbit.Ui, integrate an application, and operate durable workflows.

## Start here

1. [Getting started](getting-started.md) — run an isolated local database and complete an approval using HTTP.
2. [Flowbit.Ui guide](ui-guide.md) — import and publish definitions, complete an approval in the browser, and use management and operations screens.
3. [Developer guide](developer-guide.md) — understand definitions, identities, variables, authorization, concurrency, and integration patterns.
4. [API reference](api-guide.md) — look up every API operation, request, response, and permission contract.
5. [BPMN support](bpmn-support.md) — choose supported nodes and understand execution semantics, extensions, and limitations.
6. [Node and property reference](node-reference.md) — look up every task, event, and gateway, including properties, defaults, validation rules, and JSON fragments.
7. [Deployment and operations](deployment.md) — configure, publish, migrate, monitor, and recover the runtime.
8. [AI authoring and portable skill](ai-authoring.md) — generate and modify Flowbit JSON in the UI or install the self-contained authoring package in an external agent.
9. [OpenID Connect](openid-connect.md) — configure provider sign-in, PKCE, API access tokens, actor continuity, and the local Keycloak walkthrough.
10. [Local users and shared-key authentication](local-authentication.md) — configure database-backed login, manage users and typed attributes, and integrate HS256 tokens using `Username` identity.
11. [Built-in engine settings reference](engine-settings.md) — look up every persisted built-in setting, its format, defaults, seed migration, and when changes take effect.

[Branding reference](branding.md) — shared logo assets, palette, themes, and contributor maintenance.

## Choose your path

| Your goal | Recommended path |
| --- | --- |
| Embed workflow behavior in an application | Getting started → Developer guide → API reference. Your application calls HTTP endpoints; Flowbit.Ui is optional. |
| Use the operations UI | Getting started setup → [Flowbit.Ui guide](ui-guide.md) → Deployment for operational requirements. |
| Model a business process | BPMN support → [node and property reference](node-reference.md) → [example catalog](../examples/README.md) → use the [standalone editor](../flowbit-editor.html) or [visual authoring in Flowbit.Ui](ui-guide.md#design-workflows-in-the-ui). |
| Deploy or operate Flowbit | Deployment → [built-in engine settings](engine-settings.md) → API operational sections → [detailed runtime reference](../Flowbit/README.md). |
| Contribute to the engine | Developer guide → runtime reference → [database schema reference](database-schema.md) → [repository architecture and contributor instructions](../AGENTS.md). |

## Components

| Component | Role | Needed for the tutorial? |
| --- | --- | --- |
| `flowbit-editor.html` | Standalone browser editor; saves native Flowbit JSON with diagram layout and executable configuration. | No; the tutorial reuses a checked-in example. |
| `Flowbit.Api` | Authenticated HTTP operations, definition validation, execution, and queries. | Yes. |
| PostgreSQL | Stores immutable definition versions, execution state, work items, jobs, and history in the `flowbit` schema; see the [database schema reference](database-schema.md). | Yes. |
| `Flowbit.Ui` | Blazor operations interface with per-login OIDC sign-in; DevelopmentToken mode provides a process-wide shared test identity. | Used only to obtain the local development token. |
| `Flowbit.Worker` | Processes durable jobs, timers, retries, asynchronous conditional wakes, administrative batches, and retention cleanup. | No for the first approval; yes for examples requiring durable execution. |
| Service / Infrastructure / Shared projects | Engine logic, persistence and integrations, and shared contracts behind the API and Worker. | Built as dependencies; applications can integrate entirely through HTTP. |

Flowbit does not import or export BPMN XML or execute arbitrary BPMN diagrams. OIDC requires an external provider and supports one UI instance with per-login in-memory sessions. Read [BPMN support](bpmn-support.md), [OpenID Connect](openid-connect.md), and [deployment](deployment.md) before choosing your integration and hosting boundaries.

## AI assistant engine

The [agent engine plan](plans/ai-assistant-agent-engine-plan.md) records why the assistant now runs one model agent
with file tools, and what was verified. The earlier [eight recommendations](plans/ai-assistant-recommendations.md)
and [staged improvement plan](plans/ai-assistant-improvement-plan.md) describe the replaced command-loop engine and
are kept as history. The [AI authoring guide](ai-authoring.md) describes supported behavior.

## Enhancement backlog

The [enhancement backlog](enhancements/README.md) lists candidate task types,
events, integrations, and Flowbit.Ui features that are not implemented yet.
Each item records its status, the evidence for that status, open questions, and
a plan section to complete before implementation starts.

## Refactoring history

The staged refactoring plans and gap inventory that guided earlier contributor
work were removed as outdated in commit `87a649c`; git history retains them.
Their completed outcomes are part of the current code and guides:

- Stages 5 and 11 moved the instance-detail and administrative-batch display
  sections into display-only components under
  `Flowbit.Ui/Components/Shared/InstanceDetails/` and
  `Flowbit.Ui/Components/Shared/AdministrativeBatches/`. The pages keep all
  requests, filters, mutations, identity checks, polling, and disposal.
- Stage 7 added the no-Worker Chromium smoke suite, which passes locally and in
  CI. The editor's native file-picker success/cancel path remains a manual
  verification step.
- Stage 8 removed three query/detail compatibility methods from the engine;
  list, search, and detail endpoints call their query and projection services
  directly, with HTTP and identity boundaries unchanged.
- Stage 9 moved waiting-task role management into
  `IUserTaskRoleManagementService`, which the four role endpoints use directly;
  routes, DTOs, and role-policy behavior are unchanged.
- Stage 10 extracted the editor's Type-selector transition into
  `changeNodeTypeFromInspector`, preserving graph cleanup and undo/redo
  behavior.

Contributor browser commands are documented separately for the
[no-Worker smoke suite](../Flowbit/tests/Flowbit.BrowserTests/README.md) and the
[local Worker acceptance suite](../Flowbit/tests/Flowbit.BrowserAcceptanceTests/README.md).
Both use disposable databases and localhost hosts; the acceptance suite is
run explicitly and does not add or change a GitHub CI job.

The [example coverage matrix](../examples/README.md#coverage-matrix) separates
node-type coverage from runtime scenarios. For an operational walkthrough, use
the [durable-job examples](../examples/durable-jobs/README.md) to observe retries,
error-boundary precedence, attempts, and incident recovery with a local mock.

## Reference ownership

The [node and property reference](node-reference.md) owns the consolidated authoring tables for nodes and their nested configuration. The [built-in engine settings reference](engine-settings.md) owns the complete persisted-setting inventory, value rules, runtime defaults, seed migrations, and change timing; [deployment configuration](deployment.md#configuration) owns host-level configuration. The [example catalog](../examples/README.md) owns each sample's prerequisites, actors, inputs, and expected outcome. The [runtime README](../Flowbit/README.md) remains the detailed implementation reference for database behavior and individual features. The [database schema reference](database-schema.md) owns the table-by-table description of the `flowbit` schema: each table's purpose, key columns, keys, and relationships. These guides explain the developer and operator workflows and link to those sources for deeper details.

The [root README](../README.md) introduces Flowbit's features and includes editor and operations screenshots. This folder is ordinary GitHub-readable Markdown; no documentation website or Wiki setup is required.

[Documentation home](index.md) · [Getting started](getting-started.md) · [API reference](api-guide.md)
