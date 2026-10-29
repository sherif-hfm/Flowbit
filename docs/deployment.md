# Deployment and operations

[Documentation home](index.md) · [Local setup](getting-started.md) · [Developer guide](developer-guide.md) · [API reference](api-guide.md)

Flowbit runs as a .NET 10 API backed by PostgreSQL. Deploy `Flowbit.Worker` when using durable tasks, timers, durable conditionals, administrative batches, or retention cleanup. `Flowbit.Ui` is optional for HTTP integrations. It supports [OpenID Connect sign-in](openid-connect.md) with isolated per-login sessions on one UI instance; its shared test-identity utility is restricted to DevelopmentToken mode in Development.

For a disposable development database and your first working instance, follow [Getting started](getting-started.md). This guide covers deployed services, migration ordering, and operational behavior of the current checkout.

## Contents

- [Deployment layout](#deployment-layout)
- [Local Docker Compose stack](#local-docker-compose-stack)
- [Configuration](#configuration)
- [AI authoring and local OCR](#ai-authoring-and-local-ocr)
- [Authentication boundaries](#authentication-boundaries)
- [Build and publish](#build-and-publish)
- [Database migrations](#database-migrations)
- [Upgrade and compatibility rules](#upgrade-and-compatibility-rules)
- [Worker operation](#worker-operation)
- [Monitoring and recovery](#monitoring-and-recovery)
- [Retention and backup](#retention-and-backup)

## Deployment layout

| Component | Requirement | Responsibility |
| --- | --- | --- |
| PostgreSQL | PostgreSQL 17 is the development/test baseline. | Definitions, instances, tokens, work items, variables, shared state, audit, jobs, leases, and incidents. All Flowbit objects and EF migration history use the `flowbit` schema. |
| API | .NET 10 / ASP.NET Core runtime for the framework-dependent publish below. | Authenticated HTTP APIs, validation, synchronous transitions, and durable work creation. |
| Worker | Same runtime and database; needed for features listed above. | Leases and executes durable work, timer schedules, batches, metrics, and cleanup. |
| UI | Optional ASP.NET Core / Blazor Server application. | Management and demo client for the API; browser circuits use the server-side API client. |
| Editor | Shared static document and adjacent assets under `Flowbit.Ui/wwwroot/editor/`. | Authors JSON locally or inside Flowbit.Ui. Local file authoring needs neither the database nor a Worker; API saves require API/database access. |

Run API/Worker replicas against the same authoritative database, with matching runtime configuration and compatible binaries. In-memory definition caches are rebuildable. PostgreSQL locking and durable fences coordinate mutations; extra API/Worker replicas do not replace a database availability and recovery strategy. OIDC currently supports one UI instance: its session/token store is in memory and restarting it signs users out.

Terminate HTTPS at a configured hosting boundary or configure Kestrel certificates. For a reverse proxy, configure `ReverseProxy:KnownProxies` with the actual proxy IP addresses and verify the external HTTPS scheme/host and OIDC callbacks. Do not accept arbitrary forwarded headers. The API and UI invoke HTTPS redirection. A browser frontend that calls the API directly also needs an explicit origin/CORS design; the bundled UI calls from its server.

## Local Docker Compose stack

The root [compose.yaml](../compose.yaml) runs PostgreSQL 17, the API, UI, and Worker together. It is a local development setup: every published port binds to `127.0.0.1`, all application hosts use `Development`, and the UI retains its shared test identity. Docker with Linux containers and a current Docker Compose plugin is sufficient; the .NET SDK runs inside the image builds.

Run from the repository root. These commands work in PowerShell and Bash:

```text
docker compose version
docker compose up --build -d --wait
docker compose ps
docker compose logs --tail=100 api worker ui
```

| Service | Default host address | Container address |
| --- | --- | --- |
| UI | [Flowbit.Ui](http://127.0.0.1:15152) and [Test identity](http://127.0.0.1:15152/token) | `http://ui:8080` |
| API | [Swagger](http://127.0.0.1:15017/swagger) and [OpenAPI](http://127.0.0.1:15017/openapi/v1.json) | `http://api:8080` |
| Worker | [Readiness](http://127.0.0.1:18081/health/ready), [liveness](http://127.0.0.1:18081/health/live), and [metrics](http://127.0.0.1:18081/metrics) | `http://worker:8081` |
| PostgreSQL | `127.0.0.1:55439` | `postgres:5432` |

Startup waits for PostgreSQL's `pg_isready` health check before creating the API. The API applies migrations before accepting HTTP requests; a successful `/openapi/v1.json` check then allows the UI and Worker to start. Compose uses `depends_on` with `service_healthy` for this ordering, as described in the [Docker startup-order documentation](https://docs.docker.com/compose/how-tos/startup-order/). The Worker readiness check becomes healthy once no migration from its build is pending and its first durable-queue query succeeds, and remains latched; it does not continuously verify database availability. Compose allows 45 seconds for Worker shutdown, covering its default 30-second drain plus the 10-second allowance for cancelling and releasing unfinished jobs.

The UI calls the API through `http://api:8080`; application database connections use `postgres:5432`. Host port changes do not change these internal addresses. The API and UI receive matching JWT configuration. Both execution hosts allow the sample `depId` claim; supply any additional workflow context values consistently to API and Worker as described under [Configuration](#configuration).

Defaults work without a `.env` file. To customize them, copy [.env.example](../.env.example) to `.env` and edit its values before startup. PowerShell uses `Copy-Item .env.example .env`; Bash uses `cp .env.example .env`.

Use letters, digits, and underscores for the database and user names. Single-quote `.env` passwords containing `$` so Compose preserves them literally. API and Worker receive the password separately through [Npgsql's `PGPASSWORD` support](https://www.npgsql.org/doc/connection-string-parameters#environment-variables), so password quotes and semicolons do not become connection-string syntax.

| Variable | Default | Purpose |
| --- | --- | --- |
| `POSTGRES_DB` | `flowbit` | Initial database name. |
| `POSTGRES_USER` | `flowbit` | Initial PostgreSQL user. |
| `POSTGRES_PASSWORD` | `flowbit-local-only` | Public local-development database password. |
| `POSTGRES_PORT` | `55439` | Host database port. |
| `FLOWBIT_API_PORT` | `15017` | Host API port. |
| `FLOWBIT_UI_PORT` | `15152` | Host UI port. |
| `FLOWBIT_WORKER_PORT` | `18081` | Host Worker operational port. |
| `FLOWBIT_JWT_ISSUER` | `flowbit-local` | Shared API/UI issuer. |
| `FLOWBIT_JWT_AUDIENCE` | `flowbit-api` | Shared API/UI audience. |
| `FLOWBIT_JWT_KEY` | `flowbit-local-development-signing-key-change-me-2026` | Public local-development signing key. |

The PostgreSQL service stores data in the Compose-managed `postgres-data` named volume. Database initialization variables apply when the volume is empty; editing `.env` does not rename an existing database or rotate its user's password. Retain the matching values or update PostgreSQL deliberately. Stop the stack while retaining its database with:

```text
docker compose down
```

Run `docker compose up --build -d --wait` again to resume. For an intentional reset of this local stack, `docker compose down --volumes` **deletes its database volume and all saved workflows, instances, and history**. API/UI file logs and UI circuit state are container-local; use Compose logs while investigating failures. Workflow definitions are imported explicitly; startup does not load examples. The standalone [editor](../flowbit-editor.html) still opens directly in a browser with its complete asset folder. UI publishing and Docker builds include that folder automatically. Keep the editor document and adjacent CSS/JS from the same release; hosted integration mounts the shared editor directly into the UI and uses streamed JSON interop, with API requests made by the UI server. Restart a running development UI after adding assets so its static-asset manifest includes them; asset-load errors identify the failed URL.

Each application has a Dockerfile under its project directory. All three require the repository root as build context:

```text
docker build -f Flowbit/src/Flowbit.Api/Dockerfile -t flowbit-api:local .
docker build -f Flowbit/src/Flowbit.Ui/Dockerfile -t flowbit-ui:local .
docker build -f Flowbit/src/Flowbit.Worker/Dockerfile -t flowbit-worker:local .
```

Build stages use the [.NET SDK image](https://github.com/dotnet/dotnet-docker/blob/main/README.sdk.md); the final images run as a non-root user on the [.NET 10 ASP.NET Core runtime image](https://github.com/dotnet/dotnet-docker/blob/main/README.aspnet.md), including the Worker because it hosts HTTP health and metrics endpoints. The runtime variants use Ubuntu Noble.

The UI Dockerfile copies Razor sources before restore so the SDK includes Blazor's framework assets. Its health check requests anonymous `/health/live` and the Blazor client script; it works in either authentication mode. Liveness does not check API/provider/database availability, and the script probe detects missing interactive assets.

This Compose file explicitly selects DevelopmentToken and is not a production deployment template. Changing only `DOTNET_ENVIRONMENT` disables automatic migrations and the API's development OpenAPI health check, and DevelopmentToken outside Development fails startup. For deployment, apply [reviewed migrations](#database-migrations), provide a suitable API readiness probe, configure [OIDC](openid-connect.md) and HTTPS, and follow the upgrade procedure below. The optional [Keycloak Compose file](../compose.oidc.yaml) is a separate local provider for host-run HTTPS API/UI. Before rebuilding against an existing local database after schema changes, stop the stack so old API/Worker processes cannot run during migrations.

## Configuration

ASP.NET Core configuration accepts JSON and environment variables. Use `__` to separate nested environment keys. Supply connection strings and signing/client secrets from your deployment's protected configuration mechanism; checked-in values are development examples.

| Setting / environment key | Applies to | Purpose and current behavior |
| --- | --- | --- |
| `ConnectionStrings:Flowbit` / `ConnectionStrings__Flowbit` | API, Worker, EF tooling | Npgsql connection string. Set explicitly; do not inherit a developer machine's database. Legacy `ConnectionStrings:WorkflowEngine` remains a fallback. |
| `DOTNET_ENVIRONMENT` | All hosts | Use `Production` for a deployed service. Development API startup applies migrations automatically and exposes OpenAPI/Swagger. |
| `ASPNETCORE_URLS` | API, UI | Host listener URLs. Worker instead uses `FlowbitWorker:HealthListenUrl`. |
| `Authentication:Mode` | API, UI | Select `OpenIdConnect` for provider sign-in, or `Local` for database-backed login/shared-key tokens. `DevelopmentToken` is accepted only in Development. |
| `Authentication:Local:*` | API only | Local issuer, audience, signing key, password/session limits and initial administrator. See [Local authentication](local-authentication.md). Never copy the key or bootstrap password into UI configuration. |
| `Authentication:Oidc:*` | API, UI | Trusted authority, API audience/scope/claim mapping, and UI client/scopes/PKCE. See the complete [configuration table](openid-connect.md#modes-and-configuration). |
| `Authentication:Session:*`, `Authentication:DataProtection*` | UI | Absolute session lifetime, circuit revalidation, persisted key-ring path and key protection. One UI instance; see [session operations](openid-connect.md#session-behavior-and-operations). |
| `ReverseProxy:KnownProxies` | API, UI | Explicit proxy IP allowlist for forwarded headers, in addition to framework loopback defaults. Both hosts process these headers before HTTPS and authentication. |
| `AllowedHosts` | API, UI | Semicolon-separated direct and forwarded host allowlist. The UI requires explicit public hostnames outside Development in OIDC and Local modes. API configuration is optional and retains its `*` default. |
| `Jwt:Issuer`, `Jwt:Audience`, `Jwt:Key` | API; matching values for development UI | Symmetric test-token validation only in DevelopmentToken mode. No fallback in OpenIdConnect mode. |
| `WorkflowApi:BaseUrl` / `WorkflowApi__BaseUrl` | UI | Server-to-server API address, default `http://localhost:5017`. |
| `WorkflowContext:Config:<name>` / `WorkflowContext__Config__<name>` | API and Worker | Trusted `config.<name>` values for expressions and placeholders, including integration endpoints/credentials. Supply the same required values to both execution hosts. |
| `WorkflowContext:AllowedClaims` / `WorkflowContext__AllowedClaims__0` etc. | API and Worker | Allowlist for `sys.claim.*`. API sample configuration allows `depId`; Worker does not automatically inherit it. Durable jobs store only the caller claims these names resolve to on the enqueuing host, so a claim must be allowed on both hosts to reach Worker-side expressions. |
| `WorkflowAudit:AllowedClaims` / `WorkflowAudit__AllowedClaims__0` etc. | API and Worker | Selected JWT claims captured in history and node detail. Default `[]` disables capture; independent of expression access. See [selected-claim audit](#selected-claim-audit). |
| `WorkflowDurableProcessing:PublicationEnabled` | API | Blocks publication/default selection of definitions requiring durable processing when false. The checked-in API value is true; set false explicitly during a gated rollout. It is not a Worker pause switch. |
| `WorkflowMessageDelivery:MaxPayloadBytes` | API | Message request limit, default 1,048,576 bytes. |
| `WorkflowServiceTasks:MaxTimeoutSeconds`, `MaxResponseBodyBytes` | API and Worker | REST upper bounds, defaults 300 seconds and 1,048,576 bytes. Authored node limits still apply. |
| `WorkflowScript:*` | API and Worker | Jint limits: defaults include 5-second timeout, 100,000 statements, 8,000,000 memory bytes, recursion 128, stack 256, regex timeout 1,000 ms, array size 10,000, and output depth/items/bytes 32 / 10,000 / 1,048,576. |
| `FlowbitWorker:*` | Worker | Concurrency, lease, polling, operational listener, and cleanup options; see [Worker operation](#worker-operation). |
| `Serilog:*` / `Logging:*` | API/UI / Worker | API/UI use Serilog console and rolling file configuration. Worker uses .NET logging configuration. Route logs to the deployment's collector and protect their access. |

For example, these are configuration names and **public placeholders**, not usable credentials:

```text
ConnectionStrings__Flowbit=Host=DB_HOST;Port=5432;Database=FLOWBIT_DATABASE;Username=FLOWBIT_USER;Password=INJECTED_DATABASE_SECRET
Authentication__Mode=OpenIdConnect
Authentication__Oidc__Authority=https://YOUR_IDENTITY_PROVIDER/ISSUER_PATH
Authentication__Oidc__Audience=YOUR_FLOWBIT_API_AUDIENCE
Authentication__Oidc__ClientId=YOUR_CONFIDENTIAL_UI_CLIENT
Authentication__Oidc__ClientSecret=INJECTED_UI_CLIENT_SECRET
Authentication__DataProtectionKeysPath=/var/lib/flowbit/ui-keys
Authentication__DataProtectionCertificatePath=/run/secrets/flowbit-ui-key-protection.pfx
Authentication__DataProtectionCertificatePassword=INJECTED_CERTIFICATE_PASSWORD
AllowedHosts=YOUR_PUBLIC_UI_HOSTNAME;127.0.0.1
WorkflowContext__Config__exampleApiBaseUrl=https://YOUR_CONTROLLED_SERVICE
WorkflowContext__Config__exampleApiToken=INJECTED_SERVICE_SECRET
WorkflowContext__AllowedClaims__0=depId
WorkflowDurableProcessing__PublicationEnabled=false
FlowbitWorker__HealthListenUrl=http://127.0.0.1:8081
```

Apply each name only to the host identified in the table; the key-ring certificate
example is for a Linux UI deployment. DevelopmentToken load tools must target a
Development API. A custom `LoadTest` environment is not an exemption: it requires
OIDC configuration, and the existing HMAC-minting tools do not acquire OIDC tokens.

Engine and workflow **settings stored in PostgreSQL** are distinct from host configuration. They are managed through [settings APIs](api-guide.md#settings), not arbitrary environment variable names. The [built-in engine settings reference](engine-settings.md) owns the complete inventory, accepted values, runtime defaults, seed migrations, and change timing, including permissions, execution and batch limits, identity, and the reserved AI catalog. Missing/default management roles are generally `admin`; definition-owned permissions remain separate. The `AiAuthoring.Models` row holds the [AI model catalog](#ai-model-catalog) and is managed only through the AI models page and API.

`Authentication.UserIdentityClaim` is a persisted engine setting read at API startup. Configure the intended claim before starting replicas, then restart APIs together when changing it. A configured claim is authoritative and strict; a non-default OIDC `NameClaimType` is the next strict override. Otherwise all modes use `Username`, then the .NET `ClaimTypes.Name` URI, then `sub`/mapped `NameIdentifier`. Only absent claims fall through; an invalid present claim fails authentication. See the [authentication context API](api-guide.md).

### Selected-claim audit

`WorkflowAudit:AllowedClaims` is an opt-in list of JWT claim names. Its default `[]` records no claim snapshots. For example, configure `WorkflowAudit__AllowedClaims__0=depId` to retain the selected claim; this does not make `depId` available to workflow expressions. `WorkflowContext:AllowedClaims` controls expression access separately. Configure API and Worker consistently and restart the hosts when changing this configuration.

Configured names are trimmed and deduplicated case-insensitively; output keys retain the selected spelling. Whole claim names are matched case-insensitively, with support for known JWT/.NET inbound mappings, not arbitrary URI suffixes. Each matching claim contributes a string value in original order, including duplicates; choosing one name does not capture every token claim.

The runtime records selected claims as JSON objects of string arrays in `instance_history` and as independent starting/completing snapshots in `node_executions`. Repeated values are retained. The snapshot belongs to the authenticated caller, including when acting through delegation; `actingFor` identifies the represented owner separately. System-only activity has no JWT snapshot. Durable work carries its captured causal snapshot across Worker execution and retries. Configuration changes affect new captures and do not rewrite existing snapshots.

Select claims with their read audience in mind: instance history is available through the authenticated instance-detail route, while node detail uses node-activity authorization. The allowlist must not include credentials, bearer tokens, or claims whose disclosure is inappropriate for those readers. Snapshots are audit data, not a new source of authorization or workflow expressions.

Apply the additive claim-audit migration before upgrading writers, then deploy matching API, Worker, and UI versions together. Existing rows and older queued jobs remain without a snapshot; there is no historical backfill. Avoid mixed old/new writers when complete claim-event coverage is required. Verify a controlled claim/unclaim and a durable action before enabling capture for general use. `null` means not recorded, and `{}` means capture was enabled but no selected name was present.

Workflow-history retention deletes a row's actor claims with that history row; node-activity retention independently deletes visit snapshots. Database backups include captured claims. Existing retention protections and reactivation restrictions still apply; see [retention and backup](#retention-and-backup).

## AI authoring and local OCR

The API build exports and ships the portable [authoring package](ai-authoring.md).
At startup the API loads it once and verifies its manifest and resource hashes against the
current model. It also builds the assistant's documentation view from it: six guides plus
the canonical examples. Keep the published `authoring` directory with the API binaries; a
missing or incompatible package, or one without these guides, fails startup. Export
requires the repository's documentation and example catalog at build time; deployed
generation uses only the packaged files. No database migration or persistent
conversation store is introduced.

| API setting (`__` separates environment keys) | Default | Purpose |
| --- | --- | --- |
| `WorkflowAi:Enabled` | `true` | Enable model discovery and generation. When `false`, the assistant offers no models even if some are configured. |
| `WorkflowAi:MaxInputCharacters` | `200000` | Bound on the message plus attached document pages. |
| `WorkflowAi:MaxWorkflowCharacters` | `2097152` | Bound on the editor workflow and the proposal. |
| `WorkflowAi:RunTimeoutSeconds` | `1800` | One deadline for the whole run, follow-ups included (1–3600 seconds). There is no separate model-call timeout. Continue starts another run with fresh limits. |
| `WorkflowAi:MaxProviderCalls` | `40` | Model requests per run (1–100); the next request is refused before it is sent and the run pauses. |
| `WorkflowAi:MaxModelOutputTokens` | `32768` | `max_completion_tokens` per model call. A response that reaches it pauses the run. |
| `WorkflowAi:MaxNudges` | `2` | Times the API sends validation errors back to the model before a turn ends as `invalid` (0–5). |
| `WorkflowAi:MaxConcurrentRequests` | `4` | Process-local concurrent runs; more return `authoring_busy` (429). |
| `WorkflowAiDocuments:MaxBytes` / `MaxPages` / `MaxCharacters` | `20971520` / `100` / `200000` | PDF limits; excess input is rejected. |
| `WorkflowAiDocuments:TimeoutSeconds` / `ProcessTimeoutSeconds` | `300` / `45` | Total extraction and individual raster/OCR process deadlines. |
| `WorkflowAiDocuments:MaxRasterDimension` / `MaxConcurrentExtractions` | `4096` / `2` | Maximum raster dimension in pixels and process-local extraction concurrency. |
| `WorkflowAiDocuments:PdfToPpmPath` / `TesseractPath` | `pdftoppm` / `tesseract` | Executable paths; arguments are passed without a shell. |

**How the engine calls the provider.** Requests use OpenAI-compatible chat completions with native tool calls, through the
OpenAI .NET SDK under Microsoft Agent Framework:

- The SDK retry policy is zero, redirects are disabled, and the network timeout equals the run deadline.
- Nothing is retried. A throttled, failed, truncated, or refused call pauses the run with its code, and Continue resumes it.
- Single GLM calls with long reasoning can take several minutes. Configure reverse proxies between the UI and API to pass
  streamed responses without buffering and to keep idle connections open for at least the progress interval (60 seconds),
  with a total allowance of the run deadline plus overhead.
- The dedicated UI authoring HTTP client and stream reader allow 61 minutes, covering the maximum one-hour run; ordinary UI
  API requests retain their six-minute timeout.
- Both turn endpoints accept at most 12 MiB, because a turn carries the conversation state. Each NDJSON frame is at most 8 MiB.

**Conversation state.** It lives only in the open Blazor Server circuit and is sent back with each turn as untrusted,
compressed state:

- Any API replica can resume it; UI restart, circuit loss, close/reset, navigation, or identity/model changes discard it.
- The state is bound to the configured model's id, provider model name, endpoint and reasoning effort, not to its key.
  Changing one of them on the AI models page makes open conversations return `checkpoint_configuration_changed` (409), and
  users start a new conversation. Deleting the model makes the next turn return `unsupported_model` (400). Replacing only
  the key keeps open conversations.
- It contains the conversation, the documentation the model read, and the workflow with credential placeholders. It never
  contains credential values or the provider key, but do not log turn bodies or stream frames.

### AI model catalog

The models the assistant can use are runtime data, not host configuration. Settings administrators (`Settings.RequiredRole`)
add, edit and delete them on the UI's **AI models** page or through the [AI models API](api-guide.md#ai-models). Changes
apply to the next turn without a restart.

- **Storage.** All models are one JSON array in the engine setting `AiAuthoring.Models` (namespace `AiAuthoring`, key
  `Models`). Each entry holds an id, name, base URL, provider model name, optional reasoning effort, the API key, a revision
  and timestamps. A missing row means no models. The generic engine-settings API and page neither list nor change this row.
- **Keys at rest.** Keys are stored as plain text in that row, like other settings values, and are write-only through the
  application: no API, page or log returns them. Anyone who can read `flowbit.engine_settings` or its backups can read the
  keys, so restrict database and backup access. Rotate a key by entering a new one on the AI models page. Encryption at rest
  is not provided.
- **Who controls them.** Settings administrators choose where authoring requests and their content are sent and which
  provider account pays. Every user with `Workflow.RequiredRole` can use every configured model. Grant
  `Settings.RequiredRole` accordingly.
- **Endpoints.** A model's base URL must use HTTPS, except plain HTTP on a loopback address, and must not contain user
  information, a query, a fragment or a `chat/completions` suffix. The API checks the endpoint again before every run. Inside
  a container `localhost` is the container itself, so a model server on the Docker host needs an HTTPS address that the API
  container can reach.
- **Compatibility.** Requests use OpenAI-compatible chat completions with native tool calls and `max_completion_tokens`,
  plus `reasoning_effort` only when the model sets one. Only GLM-5.3-Flash on OpenCode Zen (`https://opencode.ai/zen/v1/`,
  reasoning `high`) has been verified with the agent's tool calls; OpenCode Go uses `https://opencode.ai/zen/go/v1/`.
  Verify any other provider with an authorized account before relying on it.
- **Key length.** A key needs 16 to 4,096 characters. The API matches the key as text to keep it out of conversation state,
  workflows and replies, so a short value would also match ordinary text. For a keyless local server, enter a unique
  placeholder of at least 16 characters.
- **Invalid stored list.** Only the AI models service writes the row. If it is edited directly in the database and no longer
  parses, the assistant returns `provider_configuration` (503) and the AI models API returns 409. Restore the row from a
  backup, or delete it and add the models again:

  ```sql
  DELETE FROM flowbit.engine_settings WHERE "Namespace" = 'AiAuthoring' AND "Key" = 'Models';
  ```

### Upgrading to administrator-managed AI models

Users no longer enter a provider key in the assistant:

- **No models after the upgrade.** The assistant offers only models configured on the AI models page and, until a settings
  administrator adds one, says that no AI models are available. To keep the previous behavior, add GLM-5.3-Flash with the
  endpoint `https://opencode.ai/zen/v1/` (or the Go endpoint), reasoning `high`, and an organization key.
- **Relocated settings.** `WorkflowAi:OpenCodeBaseUrl`, `WorkflowAi:OpenCodeModels` and `WorkflowAi:OpenCodeReasoningEfforts`
  are ignored with a startup warning. Remove them from configuration.
- **API changes.** `GET /api/workflows/ai/models` replaces `GET /api/workflows/ai/providers`. A turn's `modelId` is a
  configured model id and `providerId` is ignored. A turn with an `X-Flowbit-AI-Key` header returns
  `400 api_key_not_accepted`. Conversations open during the upgrade return `409 checkpoint_configuration_changed`.
- **Rollout.** Deploy the API and UI together. No database migration is needed: the model list uses the existing
  `engine_settings` table.

### Upgrading from the command-loop engine

The assistant's former engine has been replaced:

- **Removed:** the `current` and `optimized` execution variants, typed draft edits, requirements review, and parallel
  analysis.
- **Settings that stop AI requests:** `WorkflowAi:ExecutionVariant` (any value) and `WorkflowAi:RequirementsReviewEnabled=true`
  now make AI requests fail with `provider_configuration` (503) until the setting is removed, so a deployment never silently
  loses an engine or a review it relied on.
- **Settings that are ignored:** the other removed settings are ignored with a startup warning — `MaxContextCharacters`,
  `MaxOutputBytes`, `MaxHistoryMessages`, `MaxRepairAttempts`, `RequestTimeoutSeconds`, `MaxRunOutputTokens`,
  `MaxTransportRetries`, `MaxTruncationRecoveries`, `MaxOperationsPerBatch`, `InitialOutputTokens`, `ContextTokens`,
  `RetryBaseDelayMilliseconds`, `MaxParallelAnalysisCalls`, `MaxConcurrentProviderCalls` and `ModelProfiles`.
- **Old clients:** checkpoints and text history from older UIs are refused (`409 checkpoint_configuration_changed`,
  `400 history_unsupported`). Upgrade and restart API and UI together.

No database migration is needed.

**New dependencies.** The API, Worker and evaluation tool carry `Microsoft.Agents.AI` 1.23.0, `Microsoft.Agents.AI.OpenAI`
1.23.0, `Microsoft.Extensions.AI` 10.10, `OpenAI` 2.14.0, and `System.ClientModel` 1.15.0. `Flowbit.Service` references
`Microsoft.Extensions.DependencyInjection.Abstractions` and `Microsoft.Extensions.Logging.Abstractions` 10.0.12, which these
packages require.

**Telemetry.** The `Flowbit.Ai.Authoring` meter exposes:

- `flowbit.ai.provider.calls`, `flowbit.ai.provider.seconds` and `flowbit.ai.run.seconds`;
- `flowbit.ai.tool.calls`, tagged with the tool name and success;
- `flowbit.ai.nudges`.

The same-name `ActivitySource` emits run, provider-attempt, workflow-check and proposal-validation spans. Attach your
existing .NET diagnostics/OpenTelemetry listener to collect them; Flowbit adds no exporter. Tags contain outcomes, codes,
counts and durations, never prompts, outputs, source text, workflow contents, file paths or raw exceptions. Run responses
report model calls, input, cached and output tokens, follow-ups, and whether usage was missing from a response. These are
operational figures, not provider billing totals.

**Headers and privacy.** The provider client sends `Flowbit/1.0` as its user-agent and the conversation id as
`x-opencode-session` to every configured endpoint; the id is random and identifies only the conversation. For each run the
API reads the selected model's key from the model list and sends it only to that model's endpoint; application HTTP-client
header logging is redacted. Do not enable proxy/APM request-body or sensitive-header capture for these routes or for
`/api/ai-models`, whose create and update requests carry keys. The application stores provider keys only in the
`AiAuthoring.Models` engine setting and does not persist conversations. Selected workflow credential fields are masked in
everything the model receives, and their values are restored only in the final proposal. Review each provider's data
handling before sending requirements or PDFs. The hosted UI uses per-login sessions in OpenIdConnect mode and retains the
shared test identity only in DevelopmentToken mode. Conversations remain panel-local and are cleared on close, reset,
disposal, and identity replacement. Routine access-token refresh does not replace the actor or clear the conversation;
session or actor/role changes do. Authentication failures do not replay AI streams.

The API container installs Poppler, Tesseract, and English/Arabic language data.
Text extraction uses PdfPig locally. OCR uses bounded raster sizes, process
timeouts, two concurrent extractions by default, and a private temporary folder
removed on completion, failure, or cancellation. PdfPig's synchronous parsing observes cancellation between
parser calls; native raster/OCR processes are killed on cancellation or deadline.
Allow sufficient ephemeral disk
and restrict container memory/CPU at the deployment boundary. Configure reverse
proxy request limits to permit the PDF allowance plus multipart overhead, or
document any lower deployment limit. An unavailable OCR executable returns a
clear error; text-only extraction remains available. Handwriting and embedded
diagrams are not reliably interpreted.

For Windows development, install a Windows Poppler distribution and Tesseract
with both `eng.traineddata` and `ara.traineddata` in its `tessdata` folder. Add both
executables to PATH or set explicit paths. From the repository root, adapt these
PowerShell paths to your installation:

```powershell
$env:WorkflowAiDocuments__PdfToPpmPath = 'C:\tools\poppler\Library\bin\pdftoppm.exe'
$env:WorkflowAiDocuments__TesseractPath = 'C:\Program Files\Tesseract-OCR\tesseract.exe'
& $env:WorkflowAiDocuments__PdfToPpmPath -v
& $env:WorkflowAiDocuments__TesseractPath --list-langs
dotnet run --project Flowbit/src/Flowbit.Api
```

On Ubuntu (outside the supplied container), install the native dependencies and
check the same programs before starting the API. Bash:

```bash
sudo apt-get update
sudo apt-get install -y poppler-utils tesseract-ocr tesseract-ocr-eng tesseract-ocr-ara
export WorkflowAiDocuments__PdfToPpmPath=/usr/bin/pdftoppm
export WorkflowAiDocuments__TesseractPath=/usr/bin/tesseract
"$WorkflowAiDocuments__PdfToPpmPath" -v
"$WorkflowAiDocuments__TesseractPath" --list-langs
dotnet run --project Flowbit/src/Flowbit.Api
```

The package's provider-neutral rules are independent of live provider availability.
Automated tests use a local provider server and do not require billed requests.
A successful test-server run does not establish live OpenCode compatibility;
perform and record that check using an authorized account before making that claim.

The separate [native document verification](../Flowbit/tests/Flowbit.DocumentTests/README.md)
checks the container's real English/Arabic OCR and mixed-page extraction using synthetic PDFs.

## Authentication boundaries

### User-facing API

`Authentication:Mode=OpenIdConnect` validates JWT access tokens using the trusted provider's discovery/JWKS, expected API audience, and lifetime. Bearer access additionally requires the configured API scope. Roles and the canonical actor claim feed existing runtime authorization; allowlisted custom claims may be used in workflow expressions. Configure [OIDC registration, PKCE, and claim mappings](openid-connect.md) before starting deployed hosts.

`Authentication:Mode=Local` supports database-backed username/password login and external HS256 tokens. Apply `AddLocalIdentity` before switching all API/UI hosts; configure an explicit API-only signing key and bootstrap credentials for an empty directory. The default actor claim is exact-case `Username`. External tokens need no directory account, so deactivation blocks local login/renewal but does not revoke signed access tokens. Use HTTPS and the same protected UI key-ring/host/proxy boundary as OIDC. Bootstrap never resets existing accounts. See [Local operations and limits](local-authentication.md#sessions-and-operational-limits).

In OpenIdConnect mode there is **no API token-issuance endpoint**. The API never redirects callers to the provider or accepts an ID token for API access. The UI signs users in with confidential authorization code and PKCE by default; an explicit `UsePkce=false` option supports a configured confidential compatibility client. Tokens are stored server-side. In OIDC and Local modes, the authenticated UI [`/_info` diagnostic endpoint](openid-connect.md#view-your-current-access-token) explicitly returns the current access token to its owner; refresh and ID tokens are not exposed. Preserve its `no-store` response policy at proxies and exclude its body from response logging.

OIDC sessions are isolated per login, protected in memory, and bounded by an absolute lifetime. Only one UI instance is supported; restarts sign users out. The browser receives an HttpOnly session identifier cookie. Preserve a protected Data Protection key ring outside Development; configure Windows DPAPI or the required Linux certificate. Shared keys alone do not provide distributed sessions. API and Worker replicas remain supported.

`Authentication:Mode=DevelopmentToken` retains the process-wide `TokenState` and `/token` HMAC-SHA256 generator for trusted local testing, using matching API/UI `Jwt:*` settings. It is accepted only in Development; `/token` is unavailable in OIDC and Local modes. The production API never implicitly trusts the development signing key. Deployments that previously ran test-token authentication outside Development must configure OIDC or migrate to Local with its own explicit key before upgrading.

The [Keycloak walkthrough](openid-connect.md#local-keycloak-walkthrough) explicitly selects OpenIdConnect with the public local development client secret and loopback HTTP metadata exception, without changing the selected mode in `appsettings.Development.json`. Run both hosts with their HTTPS launch profiles. The local provider emits `Username` plus [standard profile and email claims](openid-connect.md#profile-and-email-claims) alongside flat roles; the UI requests the corresponding scopes. The ordinary Docker Compose stack and test-token tutorial explicitly override the mode to DevelopmentToken. These local settings are not production provider configuration.

Before switching providers or mode, compare `GET /api/auth/context` for existing users and preserve stored actor values. All modes default to `Username`, then the .NET name-claim URI, then `sub`/mapped `NameIdentifier`; explicit identity overrides remain strict. When upgrading from `flowbit_user`, update the provider mapper first, restart the upgraded API/UI together, and require fresh sign-in/reissued external tokens. An old token without `Username` can otherwise select its provider-generated `sub` as a different actor. No database migration changes assignments or old history. See the [in-place Keycloak update](openid-connect.md#update-an-existing-keycloak-identity-mapper). Machine integration credentials below retain their existing contracts.

All administrative-action catalog, candidate, batch/audit, and direct instance endpoints require a nonblank authenticated actor and `Workflow.RequiredRole`. This engine setting accepts comma-separated roles matched case-insensitively; missing/blank defaults to `admin`, and custom roles replace the default. The HTTP and service/engine entry points share the policy. An administrator override does not relax ordinary task, inbox, claim, assignment, delegation, or multi-instance interrupt authorization. See [administrative HTTP contracts](api-guide.md#instance-administrative-actions).

Queued administrative actions recheck the current role setting per item against the saved preparer roles during preparation and confirmer roles during execution. Unauthorized preparation becomes `ineligible`; unauthorized execution becomes `skipped` with `authentication_changed`. The Worker uses these stored snapshots and does not query an identity provider for live user-role revocation. Setting changes apply to later checks; they do not undo an already authorized transaction, committed successes, or already-committed asynchronous continuations.

### Machine integrations

- Message-start/catch endpoints authenticate node-configured client ID/secret plus required headers, independently of bearer JWTs. Keep their secrets in `config.*`, outside immutable workflow JSON.
- Task-distribution endpoints use workflow-family `X-Client-Id` / `X-Client-Secret` credentials.
- Shared-variable catalog/current-value routes accept either JWTs or managed scoped API clients. Client write scope only permits the value-write surface. Contract/lifecycle/history administration and client creation/rotation/revocation remain JWT-administrator operations. Supplying both credential modes is rejected.
- Worker health and metrics endpoints have no built-in authentication. Bind them to a controlled interface or protect them at the network/proxy boundary.

See the [API reference](api-guide.md) for endpoint-specific authorization and error contracts; `AllowAnonymous` on a machine endpoint does not mean its configured credential check is skipped.

## Build and publish

Run from the repository root with the .NET 10 SDK. The commands are identical in PowerShell and Bash and produce framework-dependent binaries; the destination host needs the matching .NET 10 ASP.NET Core runtime. Use a release-specific output directory in your deployment pipeline.

```text
dotnet publish Flowbit/src/Flowbit.Api/Flowbit.Api.csproj -c Release -o artifacts/publish/api
dotnet publish Flowbit/src/Flowbit.Worker/Flowbit.Worker.csproj -c Release -o artifacts/publish/worker
dotnet publish Flowbit/src/Flowbit.Ui/Flowbit.Ui.csproj -c Release -o artifacts/publish/ui
```

The UI publish is optional. Publish does not configure authentication, apply database migrations, install a Windows service/systemd unit, or start a Worker. Configure your process supervisor to restart failed hosts and allow Worker shutdown draining.

After the database migration and [upgrade checks](#upgrade-and-compatibility-rules), run each component from **its own publish directory** so appsettings and static assets resolve correctly. Set required secrets through the host's environment before launch.

PowerShell, API process:

```powershell
$env:DOTNET_ENVIRONMENT = 'Production'
$env:ASPNETCORE_URLS = 'http://127.0.0.1:5017'
$env:Authentication__Mode = 'OpenIdConnect'
# Database and trusted authority/audience are supplied by the deployment environment.
foreach ($flowbitKey in @('ConnectionStrings__Flowbit', 'Authentication__Oidc__Authority', 'Authentication__Oidc__Audience')) {
    if (-not [Environment]::GetEnvironmentVariable($flowbitKey)) {
        throw "Required deployment environment variable is missing: $flowbitKey"
    }
}
Set-Location artifacts/publish/api
dotnet Flowbit.Api.dll
```

Bash, API process:

```bash
export DOTNET_ENVIRONMENT=Production
export ASPNETCORE_URLS=http://127.0.0.1:5017
export Authentication__Mode=OpenIdConnect
# Database and trusted authority/audience are supplied by the deployment environment.
: "${ConnectionStrings__Flowbit:?Set the intended database connection}"
: "${Authentication__Oidc__Authority:?Set the trusted HTTPS authority}"
: "${Authentication__Oidc__Audience:?Set the API audience}"
cd artifacts/publish/api
dotnet Flowbit.Api.dll
```

Use a separate process rooted at the Worker publish directory with `dotnet Flowbit.Worker.dll`, and optionally one at the UI publish directory with `dotnet Flowbit.Ui.dll`. Assign distinct listener ports. Configure the UI's client secret, API origin, protected key ring, and provider callback URLs using the [OIDC deployment contract](openid-connect.md). These listener examples assume HTTPS is handled by the chosen hosting configuration and explicit trusted-proxy settings.

## Database migrations

**Apply reviewed migrations before starting upgraded API/Worker replicas.** Only Development API startup automatically migrates. Production API startup reads persisted settings, so an unmigrated database cannot be treated as an empty ready service. Worker startup does not apply migrations: while migrations included in its build are pending, the Worker logs them, reports not ready, and runs no durable work, timer-start reconciliation, or retention cleanup.

Use a migration identity with DDL permission for the `flowbit` schema and a separate runtime identity with the permissions required by runtime queries, sequences, triggers, and functions. Provision database connectivity before migration; migrations do not provision your database host, backups, or service credentials.

The repository's EF Core packages are **10.0.9**. These commands pin `dotnet-ef` to that version in an OS-temporary directory and use the Infrastructure project's `DesignTimeAppDbContextFactory`. Run them from the repository root, independently of the publish-directory examples above. The factory reads `ConnectionStrings__Flowbit` directly; it does not load your API appsettings file.

### Generate and review a migration script

This generates SQL without applying it to the target database. Supply an explicit intended connection in the environment rather than relying on the factory's development fallback. Reuse the tool directory on subsequent runs; install the tool only when absent.

PowerShell:

```powershell
if (-not $env:ConnectionStrings__Flowbit) {
    throw 'Set ConnectionStrings__Flowbit explicitly before using EF tooling.'
}
$flowbitEfTools = Join-Path ([System.IO.Path]::GetTempPath()) 'flowbit-docs-tools-10.0.9'
$flowbitEf = Join-Path $flowbitEfTools 'dotnet-ef.exe'
if (-not (Test-Path -LiteralPath $flowbitEf)) {
    dotnet tool install dotnet-ef --version 10.0.9 --tool-path $flowbitEfTools
    if ($LASTEXITCODE -ne 0) { throw 'EF tool installation failed.' }
}
$flowbitMigrationSql = Join-Path ([System.IO.Path]::GetTempPath()) 'flowbit-migrations.sql'
& $flowbitEf migrations script --idempotent `
    --project Flowbit/src/Flowbit.Infrastructure/Flowbit.Infrastructure.csproj `
    --startup-project Flowbit/src/Flowbit.Infrastructure/Flowbit.Infrastructure.csproj `
    --context AppDbContext --output $flowbitMigrationSql
if ($LASTEXITCODE -ne 0) { throw 'Migration script generation failed.' }
Write-Output $flowbitMigrationSql
```

Bash:

```bash
set -euo pipefail
: "${ConnectionStrings__Flowbit:?Set the intended connection explicitly before using EF tooling}"
flowbit_ef_tools="${TMPDIR:-/tmp}/flowbit-docs-tools-10.0.9"
flowbit_ef="$flowbit_ef_tools/dotnet-ef"
if [ ! -x "$flowbit_ef" ]; then
  dotnet tool install dotnet-ef --version 10.0.9 --tool-path "$flowbit_ef_tools"
fi
flowbit_migration_sql="${TMPDIR:-/tmp}/flowbit-migrations.sql"
"$flowbit_ef" migrations script --idempotent \
  --project Flowbit/src/Flowbit.Infrastructure/Flowbit.Infrastructure.csproj \
  --startup-project Flowbit/src/Flowbit.Infrastructure/Flowbit.Infrastructure.csproj \
  --context AppDbContext --output "$flowbit_migration_sql"
printf '%s\n' "$flowbit_migration_sql"
```

Review the generated SQL against the deployed migration history and your upgrade path. An idempotent script checks which migrations have run; that **does not make destructive schema transitions safe** or bypass compatibility guards. Test the full upgrade against a restored copy first.

### Apply to the intended database

After reviewing the SQL, backups, and compatibility requirements, apply the reviewed script through your controlled PostgreSQL deployment process, **or** use the same pinned EF tool and explicit connection:

PowerShell:

```powershell
& $flowbitEf database update `
    --project Flowbit/src/Flowbit.Infrastructure/Flowbit.Infrastructure.csproj `
    --startup-project Flowbit/src/Flowbit.Infrastructure/Flowbit.Infrastructure.csproj `
    --context AppDbContext
if ($LASTEXITCODE -ne 0) { throw 'Database migration failed; do not start upgraded replicas.' }
```

Bash:

```bash
"$flowbit_ef" database update \
  --project Flowbit/src/Flowbit.Infrastructure/Flowbit.Infrastructure.csproj \
  --startup-project Flowbit/src/Flowbit.Infrastructure/Flowbit.Infrastructure.csproj \
  --context AppDbContext
```

The `SeedDefaultSettings` migration inserts missing baseline settings without overwriting administrator values. Development startup does not automatically import the example catalog. Import and publish definitions through the API.

## Upgrade and compatibility rules

Classify the **actual source database and release path** before deployment. The migration history includes additive changes, backfills, removed columns/tables, and guarded cutovers.

| Upgrade path | Required procedure |
| --- | --- |
| Fresh database | Apply the complete migration chain, configure the deployment, start matching binaries, and import current definitions. |
| Compatible additive feature rollout | Apply its migration first, then upgrade every required API/Worker/editor component before publishing or enabling that feature. Follow the feature's compatibility requirements; an additive table alone does not prove old writers are safe. |
| Pre-generic-gateway runtime | The retired parallel-specific runtime/JSON vocabulary is not migrated into live generic scopes. Stop all writers, back up retained data, reset/recreate the Flowbit database/schema under a separate approved runbook, apply current migrations, then import canonical definitions. Do not upgrade old in-flight scopes in place. See [gateway rollout](../Flowbit/README.md#gateway-migration-rollout). |
| Legacy shared-variable conditional wakes | `RemoveSharedVariableConditionalWakes` aborts if dependency, wake, delivery, or incident rows remain. Use a safe-drain release/runbook to replace affected definitions and finish or migrate their instances. Do not delete rows to bypass the guard. Stop all API/Worker replicas for the cutover and deploy API/UI/Worker together. |
| Shared-variable REST and locking contracts | Run [shared-variable-rest-inventory.sql](../Flowbit/tools/shared-variable-rest-inventory.sql). Unsafe REST use must be empty: tasks accessing shared bindings require `asyncBefore`. Review every reported published multi-key definition, then save/republish through the monotonic lock-order validator or drain it. Existing unsafe definitions fail closed at runtime. |
| Shared revision allocator transition | Deploy the compatibility allocator/migration, upgrade all writer replicas, then invoke the fenced sequence cutover. Legacy writers are rejected after cutover. Keep the exact staged procedure in [shared-variable deployment notes](../Flowbit/README.md#shared-variables). |
| Conditional boundaries, role policies, retention | Apply their migrations before upgraded writers/features. Mixed legacy replicas are unsupported for these contracts. Retention rollback is additionally blocked after history has actually been pruned. |
| Reactivation run boundary | `AddInstanceLastReactivationToken` adds the nullable `workflow_instances.LastReactivationTokenId` and fills it from the `instanceReactivated` history still present. Apply it before upgrading the API, and upgrade every API replica together: an older replica would reactivate without recording the new root token. History that retention already removed cannot be recovered, so the instance diagram of such a reactivated instance may also mark where an earlier run ended. |
| FlowInfo async-after evidence | No migration. Upgrade every API and Worker replica: an older Worker still records an ordinary async-after user-task traversal twice, and an older API still rejects the deciding vote of an `asyncAfter` multi-instance task in a definition using `FlowInfo`. Evidence already recorded is not rewritten, so affected instances keep the extra traversal in that flow's `FlowInfo` count. See [flow evidence](../Flowbit/README.md#instance-wide-flow-evidence-flowinfo). |
| Task due date and priority | `AddUserTaskDueDateAndPriority` adds the nullable `user_tasks."DueAt"` and `"Priority"` columns and a `(Status, DueAt, Id)` index without backfill, and seeds the `UserTasks.DueSoonThreshold` engine setting (`P1D`) when it is missing. The columns are added at once, but the index is built with a normal `CREATE INDEX`, which blocks inserts and updates on `user_tasks` until it finishes; on a large table, apply it when task traffic is low, because running replicas wait on those writes. Apply it first, then upgrade every API, UI, and Worker replica before authoring `dueDate` or `priority`: an older replica creates work items without the values, an older API drops the properties when it saves a definition, and its strict validation and AI authoring reject them as unknown. Existing tasks keep no due date or priority, and definitions without the properties behave as before. |
| Business calendars | `AddBusinessCalendars` adds the `business_calendars` and `workflow_definition_business_calendars` tables and the nullable `timer_subscriptions."ScheduleCalendar"` column, and seeds the `examples.weekdays` calendar when it is missing. Apply it first, then upgrade every API, UI, and Worker replica before a definition names a calendar: an older API drops `calendar` and `dueDateCalendar` when it saves a definition, an older replica counts those durations in wall-clock time, and its strict validation and AI authoring reject them as unknown properties. Every API and Worker host must resolve IANA time-zone ids: the provided `noble` images include the IANA data, and Windows hosts map the ids to their own time zones through ICU. Keep the hosts on one operating-system image, because each computes due times with its own time-zone rules. Definitions without calendars behave as before. See [working-time durations](node-reference.md#working-time-durations). |
| Expression timers | No migration: the authored text fits `timer_subscriptions."ScheduleExpression"`, and the resolved due time uses the existing columns. Upgrade every API, UI, and Worker replica before saving or publishing a definition whose timer catch or boundary uses an [expression schedule](node-reference.md#expression-schedules): an older API rejects the expression as an invalid fixed value, and an older API or Worker fails that timer's activation as an invalid fixed schedule. Before rolling binaries back, drain, cancel, or otherwise retire the definitions and instances that can still activate an expression timer, through supported operations; a saved due time does not make an older runtime safe for the rest of the definition, and persisted timer rows must not be rewritten. Fixed timers, timer starts, and scheduled jobs need no change. |
| Padded names in conditions | No migration. Saving a definition now rejects a conditional catch or boundary condition with spaces inside a bracketed name, such as `[ approved ]`: NCalc keeps the spaces in the name, so the condition could never become true. An API replica that is not yet upgraded still accepts one. Versions saved earlier are not rewritten, can still be published, and run as before, so such a condition still never becomes true; save a new version that writes `[approved]`. An instance waiting at that event, or whose task has that boundary open, cannot change to the new version in place, because the condition differs. |
| NCalc function casing | No migration. NCalc resolves each built-in function only in its own spelling, such as `if` or `Max`. Saving a definition now rejects a conditional event condition, a timer expression, or a shared process-variable validation that writes one in another casing, such as `If` or `max`, and creating a shared variable rejects such a validation; the message names the spelling to use. These expressions never worked: such a condition never became true, and such a shared-variable validation rejected every non-null value. Definitions and shared variables saved earlier are not rewritten and run as before, and writes to such a shared variable are checked as before. A new definition version cannot bind a shared variable whose validation misspells a function, because the binding repeats that validation; create a corrected shared variable under a new key. An API replica that is not yet upgraded still accepts these spellings. |
| Variable form presentation | No migration. Upgrade every API, UI, and Worker replica before authoring variable `label`, `helpText`, or `options`: an older API drops these properties when it saves a definition, and its strict validation and AI authoring reject them as unknown properties. Definitions saved without them are unchanged. The instance-detail action and interrupt forms now parse entries like the start and task forms, so their `json` and array inputs are submitted as typed JSON instead of text. |
| Secure administrative actions | This release adds a breaking `Workflow.RequiredRole` gate to previously bearer-only administrative-action catalogs, candidates, audits, and batch mutations, plus direct instance actions. Pause administrative submissions and Workers; upgrade every API/Worker replica before resuming. Older replicas would retain the broader access. Existing audit tables/settings are reused; no new migration is needed for this change. |

Before resuming administrative work, confirm the intended `Workflow.RequiredRole` value and update affected clients to send an authorized actor token. Non-admin batch clients now receive `403`; previously queued items whose stored roles no longer meet the setting become terminal `ineligible`/`skipped` outcomes. Review batch results after resuming the Worker; successful items remain committed. Deploy the updated UI to expose immediate instance actions and role-denial handling. Direct instance actions execute without administrative Worker jobs, while authored asynchronous steps keep their usual Worker requirement.

After **all** shared-variable writer replicas have been upgraded and the shared-variable cutover prerequisites above are satisfied, the fenced allocator cutover is:

```sql
SELECT flowbit.cutover_shared_variable_revision_sequence();
```

Repeated calls are harmless, but calling early intentionally prevents legacy writers from allocating revisions. Treat the original database backup and matching application release as the rollback unit when an upgrade removes state. A reverse EF migration cannot reconstruct deleted workflow or audit data.

### Enabling durable features

1. Apply the compatible schema migration and deploy the matching API with `WorkflowDurableProcessing__PublicationEnabled=false`.
2. Start at least one matching Worker with the same database and required workflow context configuration.
3. Check `/health/ready`, verify queue/incident metrics, and run a controlled durable workflow.
4. Set the API publication gate to true and restart/redeploy APIs with that configuration; publish or select durable definitions as default only after this check.

The gate rejects publication/default selection for async tasks, timers, and durable conditionals. Draft saves remain possible. It does not stop already-published instances or queued work, so it is not a maintenance lock or substitute for stopping writers during an incompatible migration.

## Worker operation

The Worker claims available queue rows using PostgreSQL `FOR UPDATE SKIP LOCKED`: concurrent workers skip rows another claiming transaction has locked. Those short row locks establish persisted leases; the Worker then uses lease generations and activation fences throughout execution. Multiple replicas can process independent work while instance transitions remain serialized where required.

| `FlowbitWorker` option | Default | Operational meaning |
| --- | ---: | --- |
| `MaxConcurrency` | 8 | Total dispatcher slots. |
| `ActivityConcurrency` | 6 | Activity slots; defaults leave two slots for timer/control work. |
| `BatchSize` | 32 | Maximum queue acquisition batch. |
| `MaxPerInstance` | 4 | Per-instance activity concurrency bound. |
| `LeaseSeconds` | 60 | Job ownership duration. |
| `HeartbeatSeconds` | 20 | Renewal interval; must be less than half the lease. |
| `LeaseCheckMilliseconds` | 1000 | Local lease check frequency. |
| `HeartbeatCommandTimeoutMilliseconds` | 2000 | Bounded renewal command timeout; validation reserves time for retry before expiry. |
| `PollMilliseconds` | 1000 | Authoritative queue polling interval. PostgreSQL notifications are wake-up hints, and a finished job refills its slot without waiting for the next poll. |
| `IdleBackoffMilliseconds` | 5000 | Idle backoff bound; persisted due/lease deadlines can shorten it. |
| `TimerStartReconcileSeconds` / `TimerStartReconcileBatchSize` | 1 / 100 | Reconcile bounded batches of workflow families whose published default has a timer start, or that still own an open timer-start subscription, under a PostgreSQL advisory leader. Each Worker checks an immutable default version for timer starts once; later passes read only key columns. |
| `ShutdownDrainSeconds` | 30 | Graceful drain: stopping ends acquisition while running jobs keep their leases and may finish. Jobs still running afterward are cancelled and their leases released for immediate recovery. The host shutdown timeout adds ten seconds; allow the process supervisor at least that much stop time. |
| `HealthListenUrl` | `http://0.0.0.0:8081` | Unauthenticated operational listener. Bind deliberately. |

Worker option validation rejects inconsistent or out-of-range values at startup. Its runtime connection pool is capped at **the larger of 16 and `MaxConcurrency` + 8 connections per process**, and never exceeds the configured `Maximum Pool Size`. A configured maximum below `MaxConcurrency` + 8 logs a startup warning, because lease checks can then time out under load. Retention uses a separate pool capped at two. Size PostgreSQL connection capacity for every API/Worker replica and administrative connection, not just the dispatcher slot count.

External HTTP effects have **at-least-once** semantics. Retries may repeat a call after failure or lease loss; use the stable job ID as the remote service's idempotency key. Variable-version conflicts prevent silent overwrite and can open incidents. A timed-out caller, cancelled host, or database rollback cannot undo a remote side effect.

A captured result is the invocation boundary. If finalizing it fails unexpectedly, the Worker keeps the result and retries finalization after a five-second, database-timed delay without repeating the external call. `flowbit_worker_finalization_retries_total` counts these retries; a steadily rising value indicates a persistent finalization defect to investigate in the Worker logs.

## Monitoring and recovery

### Operational endpoints

These endpoints belong to the **Worker listener**, not the main API:

| Endpoint | Result | Meaning and limits |
| --- | --- | --- |
| `GET /health/live` | 200 `Healthy` while the host responds. | Process self-check; no database check. |
| `GET /health/ready` | 503 `Unhealthy` while migrations included in this Worker build are pending, and until the first successful queue acquisition query; then 200 `Healthy`. | Readiness is latched once both hold. It does not revert after later database outages and does not prove continuing queue progress. |
| `GET /metrics` | 200 Prometheus text. | Queue/lease/attempt/incident and runtime measurements; restrict exposure. |

PowerShell:

```powershell
Invoke-WebRequest -Uri 'http://127.0.0.1:8081/health/live' -UseBasicParsing
Invoke-WebRequest -Uri 'http://127.0.0.1:8081/health/ready' -UseBasicParsing
Invoke-RestMethod -Uri 'http://127.0.0.1:8081/metrics'
```

Bash:

```bash
curl --fail-with-body http://127.0.0.1:8081/health/live
curl --fail-with-body http://127.0.0.1:8081/health/ready
curl --fail-with-body http://127.0.0.1:8081/metrics
```

The API has no dedicated production health endpoint in this checkout. `/` redirects to `/swagger`, while `/swagger` and `/openapi/v1.json` are Development-only; they are not production readiness probes. Monitor host responsiveness plus a controlled authenticated API/database operation appropriate to your monitoring identity.

### Signals and actions

Observe queue depth and oldest age, timer lateness, acquisition/instance-lock latency, lease loss, retries, finalization retries, output conflicts, open incidents, and retention work. .NET meters are named `Flowbit.Worker` and `Flowbit.Runtime.Jobs`. A successful startup readiness response alone cannot detect a stuck queue; compare these signals over time and inspect Worker logs.

Use the UI Operations page or the role-protected jobs/incidents APIs. `WorkflowJobs.RequiredRole` defaults to `admin`. Collection endpoints use opaque keyset cursors; use detail/attempt endpoints to inspect evidence rather than expecting large execution snapshots in every list row.

Practice against a controlled local service with the
[durable-job examples](../examples/durable-jobs/README.md). They correlate
`sys.jobId` and `sys.jobAttempt` with remote calls and show both automatic
recovery and a fenced incident retry. The retry request has no JSON body and
queues work; inspect subsequent Worker attempts before treating recovery as
complete.

| Symptom | Recovery approach |
| --- | --- |
| Durable tasks or timers stay queued | Check matching Worker processes, target database, configuration, due times, lease expiry, queue metrics, and logs. Turning the publication gate on does not start a Worker. |
| REST failure or exhausted retries | Inspect the incident/attempt, correct the integration, then use the fenced retry endpoint. Account for any remote effect already performed. |
| Output-version conflict | Inspect intervening variable/shared-value changes and the failed attempt. Resolve the business state before requesting a new attempt. |
| `automatic_loop_limit` incident | Fix or review the automatic loop. A manual retry grants a fresh bounded allowance to the same stable job; it is not an unlimited retry exemption. |
| `timer_schedule_failed` incident | A timer start's first due time could not be computed: its business calendar is missing, the Worker host does not know the calendar's time zone, or the calendar cannot count the duration within 36,525 days. The timer start stays paused. Fix the calendar or the host's time-zone data, then retry the incident; the next timer-start reconciliation schedules it again or opens a new incident. |
| `... timer.timeDuration cannot be scheduled: ...` (or `timer.timeDate`) | A timer expression could not be scheduled from the instance's data: a variable it reads has no value, its result is not a valid date or duration (often a `datetime` without an offset), or its calendar cannot count the result. A request that reached the timer was rejected with `400` and left no incident; the API logs one warning without values, naming the instance, token, node, and property. In a Worker job, the job retries and then opens an incident. **Operations** shows that incident only as `Workflow job failed at node #N.` with type `job_execution_failed`; the message is the job's `lastFailureDescription` from `GET /api/jobs/{id}`. Correct the variable, for example with an administrative variable update, then repeat the request or retry the incident. A due time that was saved cannot be changed in place; see [correcting a schedule](developer-guide.md#expression-timers). |
| Worker stops during execution | A graceful stop lets running jobs finish within `ShutdownDrainSeconds`, then releases unfinished leases so a healthy replica recovers them at once; the interrupted attempt is recorded as `leaseLost` with failure code `workerShutdown`. After a crash or forced kill, recovery waits for lease expiry. Do not clear job or lease rows manually; execution fences determine whether a result is still valid. |
| Stale task/action/version operation returns 409 | Reload current state and follow compatibility/action discovery; do not force IDs or rewrite database state. |

See the [complete API reference](api-guide.md) for retry request fields and endpoint-specific 400/401/403/404/409 responses. Workflow-version changes have preview/compatibility surfaces, and terminal reactivation has explicit blockers; neither is a general repair command for arbitrary database state.

## Retention and backup

Retention is persisted deployment policy, managed through `/api/retention`. The five history/audit categories initially keep data forever. The first upgraded Worker initializes completed-job and resolved-incident retention from `CompletedJobRetentionDays=30` and `ResolvedIncidentRetentionDays=90`; subsequent restarts do not overwrite saved policies. Use matching bootstrap values on every replica.

Cleanup is performed by the Worker, including manually requested runs. It yields to a busy queue, uses bounded transactions, preserves referenced/current records, and resumes durable progress. Defaults are batches of 250, a 1,000 ms batch delay, 5,000 ms idle delay, a busy threshold of 100 runnable jobs, and 30-second queue lag. Tune with database I/O and workflow latency measurements.

**Pruning instance-owned history permanently blocks that instance's reactivation.** Disabling retention later does not restore deleted data or remove the blocker. Job/incident cleanup alone does not create that history-pruned state. Preview policies first and read [retention guarantees](../Flowbit/README.md#history-and-audit-retention) before changing them.

Back up the PostgreSQL database coherently, including `flowbit` data, sequences, functions/triggers, and migration history, together with the matching application release and protected configuration recovery process. Exported workflow JSON is useful for definitions but does not include running instances, claims, variable history, idempotency ownership, timers, or jobs. Test restores into an isolated environment with Workers and external integrations disabled until you have assessed pending timers and possible repeated external effects.

Next: [API reference](api-guide.md) · [BPMN support](bpmn-support.md) · [Documentation home](index.md)
