# Flowbit developer documentation

Flowbit is a **BPMN-aligned JSON workflow engine** with a visual editor, an HTTP API, and a PostgreSQL-backed runtime. These guides describe the current checkout: how to execute a first approval through HTTP or Flowbit.Ui, integrate an application, and operate durable workflows.

## Start here

1. [Getting started](getting-started.md) — run an isolated local database and complete an approval using HTTP.
2. [Flowbit.Ui guide](ui-guide.md) — import and publish definitions, complete an approval in the browser, and use management and operations screens.
3. [Developer guide](developer-guide.md) — understand definitions, identities, variables, authorization, concurrency, and integration patterns.
4. [API reference](api-guide.md) — look up every API operation, request, response, and permission contract.
5. [BPMN support](bpmn-support.md) — choose supported nodes and understand execution semantics, extensions, and limitations.
6. [Deployment and operations](deployment.md) — configure, publish, migrate, monitor, and recover the runtime.

## Choose your path

| Your goal | Recommended path |
| --- | --- |
| Embed workflow behavior in an application | Getting started → Developer guide → API reference. Your application calls HTTP endpoints; Flowbit.Ui is optional. |
| Use the operations UI | Getting started setup → [Flowbit.Ui guide](ui-guide.md) → Deployment for operational requirements. |
| Model a business process | BPMN support → [example catalog](../examples/README.md) → load a JSON definition in the [editor](../flowbit-editor.html). |
| Deploy or operate Flowbit | Deployment → API operational sections → [detailed runtime reference](../Flowbit/README.md). |
| Contribute to the engine | Developer guide → runtime reference → [repository architecture and contributor instructions](../AGENTS.md). |

## Components

| Component | Role | Needed for the tutorial? |
| --- | --- | --- |
| `flowbit-editor.html` | Standalone browser editor; saves native Flowbit JSON with diagram layout and executable configuration. | No; the tutorial reuses a checked-in example. |
| `Flowbit.Api` | Authenticated HTTP operations, definition validation, execution, and queries. | Yes. |
| PostgreSQL | Stores immutable definition versions, execution state, work items, jobs, and history in the `flowbit` schema. | Yes. |
| `Flowbit.Ui` | Blazor operations interface and development test-token page. Its current identity is shared by the whole UI process. | Used only to obtain the local development token. |
| `Flowbit.Worker` | Processes durable jobs, timers, retries, asynchronous conditional wakes, administrative batches, and retention cleanup. | No for the first approval; yes for examples requiring durable execution. |
| Service / Infrastructure / Shared projects | Engine logic, persistence and integrations, and shared contracts behind the API and Worker. | Built as dependencies; applications can integrate entirely through HTTP. |

Flowbit does not import or export BPMN XML, execute arbitrary BPMN diagrams, or provide production OIDC/per-user UI authentication out of the box. Read [BPMN support](bpmn-support.md) and [deployment](deployment.md) before choosing your integration and authentication boundaries.

## Reference ownership

The [example catalog](../examples/README.md) owns each sample's prerequisites, actors, inputs, and expected outcome. The [runtime README](../Flowbit/README.md) remains the detailed implementation reference for database behavior and individual features. These guides explain the developer and operator workflows and link to those sources for deeper details.

The [root README](../README.md) introduces Flowbit's features and includes editor and operations screenshots. This folder is ordinary GitHub-readable Markdown; no documentation website or Wiki setup is required.

[Documentation home](index.md) · [Getting started](getting-started.md) · [API reference](api-guide.md)
