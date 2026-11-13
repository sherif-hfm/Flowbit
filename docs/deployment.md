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
- [Call activity rollout](#call-activity-rollout)
- [Cron timer starts](#cron-timer-starts)
- [Lifecycle webhooks](#lifecycle-webhooks)
- [Worker operation](#worker-operation)
- [Monitoring and recovery](#monitoring-and-recovery)
- [Retention and backup](#retention-and-backup)

## Deployment layout

| Component | Requirement | Responsibility |
| --- | --- | --- |
| PostgreSQL | PostgreSQL 17 is the development/test baseline. | Definitions, instances, tokens, work items, variables, shared state, audit, jobs, leases, and incidents. All Flowbit objects and EF migration history use the `flowbit` schema. |
| API | .NET 10 / ASP.NET Core runtime for the framework-dependent publish below. | Authenticated HTTP APIs, validation, synchronous transitions, and durable work creation. |
| Worker | Same runtime and database; needed for features listed above. | Leases and executes durable work, timer schedules, batches, metrics, and cleanup. |
| UI | Optional ASP.NET Core / Blazor Server application. Its host must resolve IANA time-zone ids: the provided `noble` image includes the data, and Windows hosts map the ids through ICU. | Management and demo client for the API; browser circuits use the server-side API client. Each circuit shows and reads times in its browser's time zone, and uses UTC, labelled on every time, for a zone the host cannot find. |
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
| `FLOWBIT_WEBHOOK_CERT_DIR` | `./dev/webhooks` | Host folder mounted read-only into the API and Worker at `/run/flowbit/webhooks`; put `webhook-keys.pfx` there to use [lifecycle webhooks](#lifecycle-webhooks). Without it, webhook secret routes return `503` and everything else works. |
| `FLOWBIT_WEBHOOK_CERT_PASSWORD` | `flowbit-local-webhooks` | Password of that local certificate. |
| `FLOWBIT_WEBHOOK_DELIVERY_ENABLED` | `false` | Lets the Worker send webhook requests. |

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
| `WorkflowCallActivities:MaxDepth`, `WorkflowCallActivities:MaxOpenInvocationsPerRoot` | API and Worker | Limits checked when a call is admitted: nesting depth (default 8, range 1–32), exact because depth is immutable, and a soft threshold on a tree's open calls (default 256, range 1–4096). Startup fails on a value outside its range. Use identical values on every replica. See [call activity rollout](#call-activity-rollout). |
| `WorkflowMessageDelivery:MaxPayloadBytes` | API | Message request limit, default 1,048,576 bytes. |
| `WorkflowServiceTasks:MaxTimeoutSeconds`, `MaxResponseBodyBytes` | API and Worker | REST upper bounds, defaults 300 seconds and 1,048,576 bytes. Authored node limits still apply. |
| `WorkflowScript:*` | API and Worker | Jint limits: defaults include 5-second timeout, 100,000 statements, 8,000,000 memory bytes, recursion 128, stack 256, regex timeout 1,000 ms, array size 10,000, and output depth/items/bytes 32 / 10,000 / 1,048,576. |
| `FlowbitWorker:*` | Worker | Concurrency, lease, polling, operational listener, and cleanup options; see [Worker operation](#worker-operation). |
| `Webhooks:*` | API and Worker | Lifecycle webhook delivery, its own PostgreSQL pool, the operator's destination policy, and the certificate that wraps the shared webhook key ring; see [lifecycle webhooks](#lifecycle-webhooks). Delivery is off by default. |
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

Engine and workflow **settings stored in PostgreSQL** are distinct from host configuration. They are managed through [settings APIs](api-guide.md#settings), not arbitrary environment variable names. The [built-in engine settings reference](engine-settings.md) owns the complete inventory, accepted values, runtime defaults, seed migrations, and change timing, including permissions, execution and batch limits, identity, lifecycle capture, and the reserved AI catalog. Engine settings carry `CreatedBy`/`UpdatedBy` from authenticated writes (unknown historical actors are null), including writes through AI model management. Missing/default management roles are generally `admin`; definition-owned permissions remain separate. The `AiAuthoring.Models` row holds the [AI model catalog](#ai-model-catalog) and is managed only through the AI models page and API.

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

`HashInstanceVariableStringIndex` replaces the case-insensitive string index on `instance_variable_current_values`, which made any write of a string variable longer than about 2.7 KB fail. It builds the new index, then drops the old one, in one transaction that blocks writes to that table while the index builds, so apply it with API and Worker replicas stopped, as for other migrations. Rolling it back fails while any stored string is too long for the old index.

For instance diagram history, stop every Worker before applying `AddInstanceNodeActivityCoverage` and `ClassifyInstanceNodeActivityCoverage`, then restart only upgraded Workers. An older Worker can prune node activity without its new permanent marker; mixed Worker versions are unsupported. Apply both migrations before the upgraded API, and upgrade the API before the UI. Existing API binaries can continue inserting instances during these migrations: the database default records new instances as covered from creation. Finish classification before exposing the new summary route.

The first migration adds the coverage flag conservatively as `false` for existing rows and switches its insert default to `true`. The second classifies existing instances from surviving unseeded plain/message/timer start visits, excluding any instance with `HistoryPrunedAt`. It commits windows of at most 1,000 instance IDs outside a surrounding transaction, captures an upper ID bound, and safely repeats after interruption. Do not wrap this migration's generated SQL in an external transaction. Measure its execution time and database load on a production-sized restored copy before rollout. The local automated test exercises 2,009 instances across three batches; it is not a production capacity measurement.

Legacy instances without sufficient surviving evidence retain unknown coverage. Seeded visits and actual node-activity pruning produce partial coverage. Other-category pruning alone does not lower coverage for instances recorded from creation. Retention never removes the coverage columns; a downgrade that would erase a non-null `NodeActivityPrunedAt` is rejected. Prefer rolling back application binaries while retaining the additive schema, and keep old Workers stopped to preserve the marker contract. See the [coverage columns](database-schema.md#workflow_instances) and [diagram scope](ui-guide.md#read-the-instance-diagram).

The repository's EF Core packages are **10.0.9**. These commands pin `dotnet-ef` to that version in an OS-temporary directory and use the Infrastructure project's `DesignTimeAppDbContextFactory`. Run them from the repository root, independently of the publish-directory examples above. The factory reads `ConnectionStrings__Flowbit` directly; it does not load your API appsettings file.

### Automatic multi-instance foundation

The automatic multi-instance schema and shared contract are staged behind the
TASK-05 release boundary. `WorkflowAutomaticMultiInstance:AllowInDevelopment`
opens automatic multi-instance only on a host whose environment is `Development`;
other environments ignore it. Set it on both the API and the Worker: each host
decides for itself. On the API it opens authoring validation, publication, and
starting an instance that reaches a body. The hosted editor reads the API's
effective value from `GET /api/workflows/authoring-capabilities` and shows its
controls only when that is true, so the UI host's own environment and settings
never decide it; the standalone editor remains closed. On the Worker it opens a
body's entry and item jobs. With the option in effect on both, a Development API
can publish a multi-instance REST service or script task, and a Development Worker
runs its items. A host where it is not in effect refuses a published body anyway:
the API refuses to start an instance that reaches one, and the Worker's entry and
item jobs fail, retry, and then wait in an incident that a retry on an open Worker
resumes. Call bodies stay refused at publication, entry, and item processing until
their adapter is installed. Do not use this option as a production feature switch.
A service or script body needs a running Worker even for an empty collection, and
each JavaScript item stores its own copy of every `config.*` and `setting.*` value
within its 1 MiB staged-input limit.

A REST item reads the `config.*` and `setting.*` values its templates and
expressions name when it first runs, and every retry of that item reuses them, as an
ordinary durable service job reuses its snapshot. Rotating a credential therefore
reaches only items that start afterwards: an item that failed with an expired token
keeps failing until an error boundary, an interrupting boundary, or cancellation ends
the body, because items cannot be skipped. The staged values stay in the item row
until the body settles. REST effects are at least once: a request can reach the
receiver again after a lost lease or a Worker restart before its result was saved,
so receivers should deduplicate on a header such as
`Idempotency-Key: flowbit-${sys.jobId}`, which stays the same for every retry of an
item. Once an item's mapped result is saved, recovery never sends its request again.
There is no receiver-specific throttling: a `429` or `Retry-After` response is an
ordinary failed attempt under the node's retry delays, so limit the load on a
receiver with the body's `maxConcurrency` or `Workflow.MultiInstance.MaxConcurrency`.

Every automatic multi-instance size limit (the 1 MiB body snapshot, an item's staged
input and candidate together, its result, and the aggregate) counts the bytes
PostgreSQL prints for the stored JSON, which is what the tables' size checks
measure. That text has a space after each comma and colon and writes numbers in
full, so it can be much larger than compact JSON: an array of zeros takes three
bytes per element, and `1E+300` takes 301. A value over a limit is refused before
it is written.

Apply `20261111144523_AddAutomaticMultiInstanceCore`, then
`20261111144824_ValidateAutomaticMultiInstanceConstraints`, then
`20261111192010_HardenAutomaticMultiInstanceCore`. The second migration
validates widened constraints outside a transaction. The third restores the
general job index on the body column and adds the call-pin and invocation
item-pair checks; it fails if a database already holds a nonempty call body
without a pin, which no released build can create. Existing human execution
rows keep activity kind `userTask`; ordinary call activation uniqueness excludes
only rows with an automatic item identity. The additions include body-owned
snapshots, persisted item candidates/results, activation and control-generation
fences, reserved control capacity, and call-item/pin fields for adapter integration.
See the [database schema](database-schema.md) and the inactive
[concurrency settings](engine-settings.md#automatic-multi-instance-concurrency-foundation).

Completed-job retention keeps snapshots referenced by bodies. Item results live
on their item rows and survive job and node-activity pruning. A body's staged item
inputs and candidates are cleared when it settles, and workflow-history retention
empties a settled body's input snapshot when it prunes the finished instance, so
item detail then shows no copied element. Publication recovery
preserves automatic candidates and input snapshots; ordinary job recovery keeps
its existing behavior. Only retrying an `output_version_conflict` explicitly
accepts the saved candidate against current output versions and can replace a
more recent value without repeating the activity. Retrying other incidents keeps
the original version fences, so a subsequent output conflict still requires
explicit recovery. This applies to item and aggregate publication, including a
call item's `callResume`. A body's `asyncAfter` continuation runs after the body
has settled and retries as an ordinary job. Catalog lifecycle, access, and
contract checks still apply. Automatic progress reports the current
dispatch/completion job's open incident separately from item counts; a completed
set of items can still need aggregate-publication recovery. While an item's own
job (or a call item's `callStart`) has an open incident, that item counts as
needing attention and the body admits no further items; retrying the incident
resumes admission.

The release integration must upgrade every API/Worker/UI replica before enabling
automatic execution. Legacy processors do not understand the new wait/job/item
shapes. Once automatic bodies exist, reverting binaries requires draining or
explicitly cancelling/reconciling that work; retain the additive schema and audit
evidence. The current foundation does not claim Worker or adapter acceptance.

### Instance diagram history deployment checks

UI-14 feature acceptance is complete. The following deployment checks remain
outstanding; the [implementation validation record](enhancements/ui-14-instance-diagram-history.md#implementation-validation-record--2026-11-10)
records the automated and browser coverage already completed.

- [ ] Before upgrading a large existing database, measure coverage migration
  duration and database load on a production-sized restored copy, and use the
  result to plan the upgrade window.
- [ ] In a staging environment with authentically aged, retention-eligible node
  activity, let the upgraded Worker perform cleanup and verify that refreshing
  the instance diagram updates retained history counts and the partial-coverage
  notice. Keep screenshot and console evidence. Neither fresh-stack browser
  suite currently covers this walkthrough.

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
| Shared revision allocator removal | Stop all API/Worker writers, apply `RemoveSharedVariableRevisionState`, deploy matching binaries, then restart. The migration preserves shared-variable data and makes sequence allocation permanent; mixed old/new writers are unsupported. See [shared-variable deployment notes](../Flowbit/README.md#shared-variables). |
| Conditional boundaries, role policies, retention | Apply their migrations before upgraded writers/features. Mixed legacy replicas are unsupported for these contracts. Retention rollback is additionally blocked after history has actually been pruned. |
| Reactivation run boundary | `AddInstanceLastReactivationToken` adds the nullable `workflow_instances.LastReactivationTokenId` and fills it from the `instanceReactivated` history still present. Apply it before upgrading the API, and upgrade every API replica together: an older replica would reactivate without recording the new root token. History that retention already removed cannot be recovered, so the instance diagram of such a reactivated instance may also mark where an earlier run ended. |
| FlowInfo async-after evidence | No migration. Upgrade every API and Worker replica: an older Worker still records an ordinary async-after user-task traversal twice, and an older API still rejects the deciding vote of an `asyncAfter` multi-instance task in a definition using `FlowInfo`. Evidence already recorded is not rewritten, so affected instances keep the extra traversal in that flow's `FlowInfo` count. See [flow evidence](../Flowbit/README.md#instance-wide-flow-evidence-flowinfo). |
| Task due date and priority | `AddUserTaskDueDateAndPriority` adds the nullable `user_tasks."DueAt"` and `"Priority"` columns and a `(Status, DueAt, Id)` index without backfill, and seeds the `UserTasks.DueSoonThreshold` engine setting (`P1D`) when it is missing. The columns are added at once, but the index is built with a normal `CREATE INDEX`, which blocks inserts and updates on `user_tasks` until it finishes; on a large table, apply it when task traffic is low, because running replicas wait on those writes. Apply it first, then upgrade every API, UI, and Worker replica before authoring `dueDate` or `priority`: an older replica creates work items without the values, an older API drops the properties when it saves a definition, and its strict validation and AI authoring reject them as unknown. Existing tasks keep no due date or priority, and definitions without the properties behave as before. |
| Business calendars | `AddBusinessCalendars` adds the `business_calendars` and `workflow_definition_business_calendars` tables and the nullable `timer_subscriptions."ScheduleCalendar"` column, and seeds the `examples.weekdays` calendar when it is missing. Apply it first, then upgrade every API, UI, and Worker replica before a definition names a calendar: an older API drops `calendar` and `dueDateCalendar` when it saves a definition, an older replica counts those durations in wall-clock time, and its strict validation and AI authoring reject them as unknown properties. Every API and Worker host must resolve IANA time-zone ids: the provided `noble` images include the IANA data, and Windows hosts map the ids to their own time zones through ICU. Keep the hosts on one operating-system image, because each computes due times with its own time-zone rules. Definitions without calendars behave as before. See [working-time durations](node-reference.md#working-time-durations). |
| Expression timers | No migration: the authored text fits `timer_subscriptions."ScheduleExpression"`, and the resolved due time uses the existing columns. Upgrade every API, UI, and Worker replica before saving or publishing a definition whose timer catch or boundary uses an [expression schedule](node-reference.md#expression-schedules): an older API rejects the expression as an invalid fixed value, and an older API or Worker fails that timer's activation as an invalid fixed schedule. Before rolling binaries back, drain, cancel, or otherwise retire the definitions and instances that can still activate an expression timer, through supported operations; a saved due time does not make an older runtime safe for the rest of the definition, and persisted timer rows must not be rewritten. Fixed timers, timer starts, and scheduled jobs need no change. |
| Padded names in conditions | No migration. Saving a definition now rejects a conditional catch or boundary condition with spaces inside a bracketed name, such as `[ approved ]`: NCalc keeps the spaces in the name, so the condition could never become true. An API replica that is not yet upgraded still accepts one. Versions saved earlier are not rewritten, can still be published, and run as before, so such a condition still never becomes true; save a new version that writes `[approved]`. An instance waiting at that event, or whose task has that boundary open, cannot change to the new version in place, because the condition differs. |
| NCalc function casing | No migration. NCalc resolves each built-in function only in its own spelling, such as `if` or `Max`. Saving a definition now rejects a conditional event condition, a timer expression, or a shared process-variable validation that writes one in another casing, such as `If` or `max`, and creating a shared variable rejects such a validation; the message names the spelling to use. These expressions never worked: such a condition never became true, and such a shared-variable validation rejected every non-null value. Definitions and shared variables saved earlier are not rewritten and run as before, and writes to such a shared variable are checked as before. A new definition version cannot bind a shared variable whose validation misspells a function, because the binding repeats that validation; create a corrected shared variable under a new key. An API replica that is not yet upgraded still accepts these spellings. |
| Error-boundary HTTP status matching | No migration. Upgrade every API, UI, and Worker replica before saving or publishing a definition with `errorMatch` or several boundaries on one host. An older runtime can ignore the matcher or use the first boundary, and a stale Flowbit.Ui replica drops `errorMatch` when it saves, which turns a filtered boundary into a catch-all. Before rolling back, inventory definitions and live work that use matchers or several boundaries, and replace, drain, or cancel them through supported operations; never edit stored JSON or job rows. This release also changes existing definitions in one way: a connection lost while reading a REST response is now a task failure that keeps the received status. A synchronous task with a boundary routes through it, one without rolls back with `400` instead of failing with `500`, and a durable job records the received status instead of `0` and the raw exception message. A durable service job fences the error variable of every boundary on its host, so a concurrent write to any of them opens `output_version_conflict` even if another boundary would have been selected. A durable script job fences its boundary's error variable the same way when the script fails, or when a write is refused at finalization; retrying the incident works from a new snapshot. A corrupt, ambiguous matcher opens `job_invariant_violation` without a retry. |
| Variable form presentation | No migration. Upgrade every API, UI, and Worker replica before authoring variable `label`, `helpText`, or `options`: an older API drops these properties when it saves a definition, and its strict validation and AI authoring reject them as unknown properties. Definitions saved without them are unchanged. The instance-detail action and interrupt forms now parse entries like the start and task forms, so their `json` and array inputs are submitted as typed JSON instead of text. |
| Call activities | `AddCallActivityExecution` adds the `call_activity_executions`, `call_activity_results`, and `workflow_definition_call_targets` tables, `workflow_instances."CallRootInstanceId"` and `"CallDepth"`, and `workflow_jobs."CallInvocationId"`. Existing instances get root defaults and no history. Follow [call activity rollout](#call-activity-rollout). Its down migration refuses while call instances, invocations, or call jobs remain. |
| Cron timer starts | `AddCronTimerSchedules` adds the nullable `timer_subscriptions."ScheduleTimeZone"` column, allows schedule kind `cron`, and adds two constraints: a cron subscription has a nonblank zone, no calendar, and a timer-start shape, and is never `completed`. `ValidateCronTimerScheduleConstraints` then validates the constraints outside a transaction; if that run stops, apply the migrations again. Neither backfills anything or activates a schedule. Follow [cron timer starts](#cron-timer-starts): apply both, upgrade every API, UI, and Worker replica, and only then save or publish a cron definition. The down migration of `AddCronTimerSchedules` refuses while any cron subscription row remains. |
| API request log status | No migration. The API's request log now records the status a mapped error returns, such as `400` or `409`, at Information, and the `503` of an unavailable webhook key ring at Error. Before, it recorded each of these as `500` at Error with a stack trace, while the client received the mapped status. Review log alerts and dashboards that counted those entries as server failures. See [signals and actions](#signals-and-actions). |
| Browser time zones in Flowbit.Ui | No migration, and stored times do not change. Flowbit.Ui now shows times, and reads the date filters on **Activity** and **Webhooks** and the shared-variable client expiry, in each browser's time zone instead of the UI server's, so users in another zone see and enter different wall-clock times than before. Delegation windows stay in UTC. The UI host needs IANA time-zone data, as the [deployment layout](#deployment-layout) describes; without it every time falls back to UTC and says so. See [Times and time zones](ui-guide.md#times-and-time-zones). |
| Secure administrative actions | This release adds a breaking `Workflow.RequiredRole` gate to previously bearer-only administrative-action catalogs, candidates, audits, and batch mutations, plus direct instance actions. Pause administrative submissions and Workers; upgrade every API/Worker replica before resuming. Older replicas would retain the broader access. Existing audit tables/settings are reused; no new migration is needed for this change. |

Before resuming administrative work, confirm the intended `Workflow.RequiredRole` value and update affected clients to send an authorized actor token. Non-admin batch clients now receive `403`; previously queued items whose stored roles no longer meet the setting become terminal `ineligible`/`skipped` outcomes. Review batch results after resuming the Worker; successful items remain committed. Deploy the updated UI to expose immediate instance actions and role-denial handling. Direct instance actions execute without administrative Worker jobs, while authored asynchronous steps keep their usual Worker requirement.

For shared-variable allocator removal, stop every API and Worker writer before
applying migrations, deploy the matching binaries, then restart them. The
`RemoveSharedVariableRevisionState` migration preserves existing shared-variable
data and advances the sequence above both stored revisions and previously
allocated sequence values. Fresh installations use sequence allocation
automatically after the full migration chain; there is no manual allocator
cutover or mode setting.

Rolling back this migration also requires stopped writers and matching previous
binaries. Its `Down` restores the previous allocator schema in `sequence` mode,
preserving the sequence and shared-variable data. For upgrades that remove other
state, treat the original database backup and matching application release as the
rollback unit: a reverse EF migration cannot reconstruct deleted workflow or
audit data.

### Enabling durable features

1. Apply the compatible schema migration and deploy the matching API with `WorkflowDurableProcessing__PublicationEnabled=false`.
2. Start at least one matching Worker with the same database and required workflow context configuration.
3. Check `/health/ready`, verify queue/incident metrics, and run a controlled durable workflow.
4. Set the API publication gate to true and restart/redeploy APIs with that configuration; publish or select durable definitions as default only after this check.

The gate rejects publication/default selection for async tasks, timers, durable conditionals, call activities, and call-only starts, including an otherwise synchronous call-only target. Deleting a default is refused when its successor would need the closed gate, rather than promoting a different version. Draft saves remain possible. It does not stop already-published instances or queued work, so it is not a maintenance lock or substitute for stopping writers during an incompatible migration.

## Call activity rollout

A call crosses instances through durable jobs, so it needs Workers and matching binaries everywhere. A legacy API ignores the `callOnly` start restriction, the call fence, and the abandon hook, and a legacy Worker treats an unknown job kind as an async continuation. Mixed call-aware and legacy replicas are therefore unsupported once a call node or `callOnly: true` has been published. Definitions without these features remain valid.

1. Back up the database.
2. Freeze API and UI authoring, import, publication, and default selection of definitions that use `callActivity` or `callOnly: true`. Enforce this operationally until every replica is upgraded: an old API can drop `callOnly` before validating, so the new gate alone cannot protect requests routed to it.
3. Set `WorkflowDurableProcessing:PublicationEnabled` to false on the API, because the checked-in value is true. This also holds back other durable definitions during the rollout, while already-published work keeps running.
4. Apply the additive migration.
5. Upgrade every API and Worker replica and the matching editor assets, with identical `WorkflowCallActivities` values. Refresh open authoring tabs so an old editor cannot strip the new fields.
6. Verify Worker health, then confirm the gate refuses both a call node and a call-only start while allowing drafts.
7. Lift the freeze, set the gate to true, restart the APIs, publish and default the child, then publish and start the parent.

**Admission and capacity.** A call that passes its input, depth, and capacity checks reserves one slot until it settles. A call blocked before admission holds no slot and has an open incident; retrying repeats admission, so it can succeed once capacity frees up. An admitted retry reuses its slot. `MaxOpenInvocationsPerRoot` is a soft threshold, not an exact quota: at most `C × M` reservations can exist with `C` simultaneous admission transactions for a root and a limit of `M`, and when all `C` observe the same count `N` the bound is `N + C × (M − N)`. For example, `M = 4`, `N = 3`, and `C = 3` can reach six. Lowering a limit blocks new admissions above it without revoking reservations or cancelling work. It never shortens the ancestry check on existing descendants, which follows persisted depth up to the supported maximum of 32.

**Cancellation lag.** Cancelling a parent settles its calls immediately and fences every descendant, so commands on them return `409`. The descendants become terminal only when their `callCancel` jobs run: normally moments later, longer with a backlog, and not at all while Workers are stopped. Until then their tasks stay visible. A job that exhausts its retries opens an incident, and the child stays fenced until an operator retries it or cancels the child, which also closes the incident. Cancellation does not undo external effects, and an in-flight REST request cannot be recalled, though its late result is fenced out.

**Rollback.** Before any call data exists, the down migration can run. Afterwards it refuses, and rollback means a forward repair or a database restore, because dropping these tables would cut live parents from their children.

**Retention.** The invocation row stays for the instance's lifetime as the relationship. Captured values become eligible under the existing `workflowHistory` policy after settlement, aged from the parent's terminal time, so there is no new category. Open call jobs, undelivered results, and referenced definitions are not removed.

## Cron timer starts

A timer start can run on a [cron schedule](node-reference.md#cron-schedules) in an IANA time zone. The API validates the expression and zone when a definition is saved; the Worker calculates each occurrence with the pinned Cronos 0.13.0 package and its host's time-zone data, saves it as a UTC instant, and fires it through the existing timer-start subscription and queue. No new job kind, table, or external scheduler is involved.

**Rollout.** Workers released before this feature cannot read cron, and the schema gate only warns a Worker about newer migrations, so this order is the guard:

1. Apply `AddCronTimerSchedules` and `ValidateCronTimerScheduleConstraints`. The first adds `timer_subscriptions."ScheduleTimeZone"` and the constraints `NOT VALID` in one transaction, which needs no table scan. The second validates each constraint in its own statement outside a transaction. Validation takes a lock that lets reads and writes continue, so running Workers keep firing timers while it scans; existing rows satisfy the constraints. If validation stops partway, for example at a `statement_timeout` or a killed deployment, apply the migrations again: validating a constraint that is already valid changes nothing, and the second migration is recorded only when all three are valid.
2. Upgrade every API, UI, and Worker replica to the same release. Refresh open authoring tabs, because an older editor drops `cron` and `timeZone`.
3. Confirm that every API and Worker host resolves the zones you will use, with compatible time-zone data, as described below.
4. Only then save, publish, or set as default a definition with a cron timer start.

**If the order is broken.** An older Worker that leads timer-start reconciliation fails the whole pass while a cron default awaits its first activation, which stalls timer-start activation and missing-job repair for every family on the passes it leads; upgrading that Worker clears it. An older Worker that leases a cron occurrence starts the instance and then completes the subscription, which the database refuses (`CK_timer_subscriptions_cron_not_completed`): the start rolls back, the job retries, and then it opens an incident. Retry that incident once every Worker is upgraded. The constraint equally stops a defect from ending a cron schedule after one firing.

**Time-zone data and the calculator.** Each API host checks zones against its own data when saving, and each Worker calculates occurrences with its own. Keep them on one operating-system image: the provided `noble` images include the IANA data, and Windows hosts map IANA ids through ICU, where the suggestion list can be incomplete. A due time already saved never moves when zone rules change; occurrences calculated afterwards use the Worker's new data. A zone a Worker cannot resolve fails visibly; nothing falls back to UTC. Previews use the API's data and promise nothing about Worker timing.

**Misfires and downtime.** With `* * * * *`, an occurrence stored for 08:00 still runs when the Worker reaches it at exactly 08:01:00; at 08:01:00.001 it is a misfire that starts nothing, and the schedule moves to 08:02, skipping the 08:01 occurrence. A Worker that was stopped for days resumes with the first occurrence after its current time, in one lookup; missed runs are never replayed. Each run starts a new instance even if earlier ones are open.

**Failures.** A cron timer start whose first occurrence cannot be calculated, because the Worker cannot resolve its zone or finds no occurrence within 36,525 days, keeps a paused subscription with its expression and zone and a `timer_schedule_failed` incident. A failure to calculate the next occurrence while firing rolls back the instance, the successor, and the job completion together, so no start commits without its successor; the job retries and then opens a `job_execution_failed` incident. A later retry checks lateness again and may skip that occurrence.

**Rollback.** Before rolling back binaries, make a version without a cron timer start the default of each affected family, or unpublish or delete the cron versions, so their schedules and queued jobs are retired through supported operations. The down migration also refuses while any cron subscription row remains, cancelled ones included, and never deletes history or strips zones to pass. Retiring a schedule cancels its row but keeps it; only deleting the version removes the row, and a version that has started instances cannot be deleted. To roll the schema back past such a version, restore a compatible backup.

## Lifecycle webhooks

Lifecycle webhooks send committed instance, task, delegation, and incident events to external receivers. Settings administrators manage subscriptions in the [UI](ui-guide.md#lifecycle-webhooks) or [API](api-guide.md#lifecycle-webhooks); receivers follow the [developer guide](developer-guide.md#lifecycle-webhooks). Three independent controls govern it:

| Control | Where | Effect |
| --- | --- | --- |
| Capture switch | Protected `Webhooks.CaptureEnabled` row in `flowbit.engine_settings`; edit through Engine settings/API or `flowbit.set_lifecycle_event_capture` | Whether database triggers record events. Off on fresh installs. Only canonical `true`/`false` values are stored; deletion is refused. Changes affect subsequent committed transitions without a process restart. |
| `Webhooks:DeliveryEnabled` | Each Worker | Whether that Worker sends requests. Off by default. Fanout, lease recovery, and archive settlement keep running while it is off. |
| Subscription state | Webhooks page or API | Whether a subscription matches new events. |

The webhook catalog reports the capture row's latest edit time/actor as `changedAt`/`changedBy`. API same-value or description-only saves update that metadata; only Boolean toggles create management audit entries. The SQL helper keeps unchanged calls as no-ops and advances timestamps monotonically on a toggle. For direct database operations, use this helper with the actual operator identity.

### How capture works

Deferred constraint triggers on `workflow_instances`, `user_tasks`, `workflow_incidents`, and `user_delegations` run at commit, in the order the rows changed, and record each event that matches at least one active subscription in `lifecycle_events`. A failure there fails the source commit; a receiver failure happens later in the Worker and can never fail it. A transition with no matching subscription writes nothing, so a deployment without subscriptions pays one switch read and one subscription query per qualifying change. Capture takes no row locks and reads subscriptions without waiting on management or dispatch.

Data-repair scripts that change those four tables and must not announce their changes run `SET LOCAL flowbit.lifecycle_events = 'off';` inside their transaction. Repair migrations do the same unless they deliberately announce their changes.

### Configuration

| `Webhooks` option | Default | Meaning |
| --- | ---: | --- |
| `DeliveryEnabled` | `false` | Worker only: send requests. |
| `MaxConcurrentSends` | 16 | Worker HTTP sends in flight, separate from workflow-job slots. Each subscription is further limited by its own concurrency (default 4, at most 16), enforced in PostgreSQL across Workers. |
| `FanoutBatchSize` | 100 | Events turned into deliveries per transaction. |
| `PollSeconds` | 5 | Authoritative polling interval; `flowbit_lifecycle_events` notifications are wakeup hints. |
| `HttpTimeoutSeconds` | 15 | Total time for DNS, connection, TLS, and response headers. |
| `LeaseSeconds` / `HeartbeatSeconds` | 60 / 15 | Delivery lease and its renewal. A lost lease cancels the local request. |
| `MaxResponseHeaderKilobytes` | 16 | Larger response headers fail the attempt. Response bodies are never read. |
| `ConnectionLifetimeSeconds` | 120 | Pooled HTTP connection lifetime; addresses are re-checked on every new connection. |
| `ConnectionPoolSize` | 20 (API: 4) | The webhook queue's own PostgreSQL pool, at least 4. A sending Worker needs one connection per concurrent send for its short prepare, heartbeat, and finalize transactions, plus four for the notification listener, fanout, acquisition, and key-ring reads; below `MaxConcurrentSends` + 4 it logs a startup warning, because a result that cannot be saved in time is sent again. Sends never hold a connection during HTTP. Add the pool to each API and Worker replica's connection budget. |
| `Network:AllowedHosts` | `[]` | `host` or `host:port` entries the operator trusts: they may use a port other than 443 and resolve to private addresses. Subscription administrators cannot change this policy. |
| `Network:AllowedCidrs` | `[]` | Address ranges any receiver may use, for internal HTTPS receivers. |
| `Network:AllowLoopbackHttp` | `false` | Lets local fixtures use their own `http://127.0.0.1` receiver. Startup refuses it outside Development. |
| `KeyProtection:CertificatePath` / `CertificatePassword` | none | PKCS#12 certificate with a private key that wraps the webhook key ring. Required on the API and every Worker. |
| `KeyProtection:PreviousCertificates` | `[]` | `{ "Path", "Password" }` entries for earlier certificates, so keys wrapped before a certificate change still unwrap. |

**Destinations.** Subscriptions save only absolute HTTPS URLs on port 443 (or an allowlisted host and port) without user information or fragments. Every new connection resolves the host itself, refuses loopback, private, link-local, metadata, multicast, and other non-public addresses unless allowlisted, including IPv4-mapped and other alternate IPv6 forms, and connects to a checked address while keeping the hostname for TLS and SNI. Redirects, proxies, cookies, and credentials are never used. Logs, metrics, and audit entries carry only the destination's scheme, host, and port: the path and query can contain a receiver token.

### Signing keys and the key ring

Subscriber secrets are protected with ASP.NET Data Protection under the application name `Flowbit.Webhooks`. Its key ring is stored in `flowbit.webhook_data_protection_keys`, so the API (which creates and rotates secrets) and the Workers (which sign) share it without a shared volume. Every ring key is wrapped with the configured certificate, so database backups contain only wrapped keys. There is no fallback to machine-local or ephemeral keys. Key protection is unavailable on a host without a usable certificate, and on a host whose certificates cannot unwrap every key in the ring: there create, rotate, test, resume, and retry return `503`, and the Worker sends nothing and, when delivery is enabled, reports `Degraded` readiness with the reason. A delivery whose own secret cannot be decrypted even after the ring is reloaded fails with `signing_unavailable`; rotate that subscription's secret, then retry it.

Create a certificate for each environment and give only the API and Worker read access to it. PowerShell:

```powershell
New-Item -ItemType Directory -Force dev/webhooks | Out-Null
$certificate = New-SelfSignedCertificate -Subject "CN=Flowbit webhook key ring" -KeyAlgorithm RSA -KeyLength 3072 -KeyExportPolicy Exportable -CertStoreLocation Cert:\CurrentUser\My -NotAfter (Get-Date).AddYears(5)
Export-PfxCertificate -Cert $certificate -FilePath dev/webhooks/webhook-keys.pfx -Password (ConvertTo-SecureString "flowbit-local-webhooks" -AsPlainText -Force) | Out-Null
Remove-Item "Cert:\CurrentUser\My\$($certificate.Thumbprint)"
```

Bash:

```bash
mkdir -p dev/webhooks
openssl req -x509 -newkey rsa:3072 -sha256 -days 1825 -nodes -subj "/CN=Flowbit webhook key ring" -keyout dev/webhooks/webhook-keys.key -out dev/webhooks/webhook-keys.crt
openssl pkcs12 -export -inkey dev/webhooks/webhook-keys.key -in dev/webhooks/webhook-keys.crt -out dev/webhooks/webhook-keys.pfx -passout pass:flowbit-local-webhooks
rm dev/webhooks/webhook-keys.key dev/webhooks/webhook-keys.crt
```

`.pfx` files are ignored by Git, and the repository includes the `dev/webhooks` folder, so Docker never creates it with another owner. **Back up the certificate with the database and keep it across restores and upgrades.** To change certificates, configure the new one as `CertificatePath` and the old one under `PreviousCertificates` on every API and Worker. Data Protection rotates its own ring keys automatically; that is separate from rotating a subscriber's secret.

**If the certificate is lost**, every subscriber secret is unrecoverable, and Flowbit never generates replacement secrets. Hosts stay unavailable while the ring holds keys that no configured certificate can unwrap, so:

1. Configure a new certificate as `CertificatePath` on every API and Worker, keeping any earlier certificate you still have under `PreviousCertificates`.
2. Back up the database, then delete the ring keys the lost certificate wrapped. When it was the only certificate, that is every row: `DELETE FROM flowbit.webhook_data_protection_keys;`
3. Restart the API and Workers, rotate every subscription's secret, and reconfigure each receiver with its new secret.
4. Retry the deliveries that failed with `signing_unavailable` in bulk.

### Rollout

1. For a fresh installation, apply the migration chain, including `AddOutboundLifecycleWebhooks` and `ConsolidateLifecycleCaptureSettings`, with capture and delivery off. It creates the tables, functions, and triggers; `CREATE TRIGGER` briefly blocks writes to the four source tables, so use the normal migration window. There is no historical backfill.
2. Configure the certificate and network policy on every API and Worker; update Compose, environment, or mounts. Back up the database and the certificate.
3. Deploy matching API, Worker, and UI versions. The Worker's schema gate waits for required migrations. Do not run older binaries after capture consolidation; they query the removed table.
4. Create a paused subscription for a test receiver, send a test event with delivery enabled on one Worker, and activate the subscription. Then choose **Enabled** (`true`) for `Webhooks.CaptureEnabled` in Engine settings or use the SQL helper below. Capture applies to transitions committed after the setting transaction commits:

   ```sql
   SELECT flowbit.set_lifecycle_event_capture(true, 'operator@example.com');
   ```

5. Verify a real lifecycle event, signatures, deduplication, retries, backoff, bulk recovery, backlog, and the retention preview before production subscriptions are activated.

**Existing-database capture consolidation.** `ConsolidateLifecycleCaptureSettings` adds nullable `CreatedBy`/`UpdatedBy` to `engine_settings`, imports the capture row, replaces its SQL functions, and drops `lifecycle_event_capture_state` without a compatibility view. Back up first, stop all API and Worker writers, apply the migration, deploy matching API/UI/Worker versions, then restart and verify `Webhooks.CaptureEnabled`, the webhook catalog, and Worker metrics before resuming traffic. Mixed old/new versions are unsupported; an older Worker accepting a newer schema does not make its removed-table queries compatible. The migration rejects an existing canonical or equivalent capture key with a diagnostic; resolve that collision deliberately before retrying rather than overwriting it.

The import preserves the old Boolean and last operator/time. Both initial timestamps use the old `ChangedAt` because the original creation time is unknown; `CreatedBy` remains null. Other existing engine rows also retain null actors. New authenticated writes populate attribution.

**Rollback.** Disabling delivery (`Webhooks:DeliveryEnabled=false`) keeps captured work and keys. An application rollback across consolidation requires stopping writers and coordinating the schema downgrade: its Down migration reconstructs the current capture switch and original functions, but removes all engine-setting actor columns and their data. Restore older binaries only after that downgrade. Do not use the original webhook feature's destructive Down migration as routine rollback for retained or pending data.

**Emergency stop.** For a receiver outage, pause the subscription or disable delivery: captured work is preserved. Turn capture off only for a trigger defect, because transitions committed while it is off are never recorded:

```sql
SELECT flowbit.set_lifecycle_event_capture(false, 'operator@example.com');
```

### Monitoring and runbooks

The Worker's `/metrics` adds `flowbit_webhooks_capture_enabled`, `flowbit_webhooks_fanout_pending`, `flowbit_webhooks_fanout_oldest_age_seconds`, `flowbit_webhooks_queue_depth{state}`, `flowbit_webhooks_queue_oldest_due_age_seconds`, `flowbit_webhooks_subscriptions_backing_off`, `flowbit_webhooks_subscriptions_paused`, `flowbit_webhooks_sends_in_flight`, `flowbit_webhooks_attempts_total{outcome}`, `flowbit_webhooks_automatic_pauses_total{reason}`, `flowbit_webhooks_leases_recovered_total`, `flowbit_webhooks_archive_discarded_total`, `flowbit_webhooks_loop_failures_total`, and the `flowbit_webhooks_send_duration_seconds` summary; the `Flowbit.Webhooks` meter carries the same measurements. Labels are bounded codes, never ids, URLs, or actors. Logs name event and delivery ids, never payloads, URLs beyond their origin, signatures, or keys.

| Symptom | Action |
| --- | --- |
| Deliveries stay pending | Check `capture_enabled`, a Worker with delivery enabled, `fanout_pending`, and readiness. A paused subscription holds its work; only tests are sent while paused. |
| Growing backlog for one subscription | Check `subscriptions_backing_off` and the subscription's health on the Webhooks page. Backoff holds its queue without spending attempts; fix the receiver, or pause the subscription. Lower its concurrency if the receiver is overloaded. |
| A subscription paused itself | `receiverGone` means the receiver answered `410`; `receiverUnavailable` means 72 hours of continuous backoff whose probes kept failing. A backoff that went more than an hour without a probe, for example because its queue emptied, starts again at the next failure instead. Fix the receiver, resume, then retry failed deliveries in bulk. Nothing is backfilled for the paused period. |
| Many failed deliveries | Filter by failure code. Correct the cause, then retry in bulk, or discard what the receiver no longer needs. Failed deliveries are kept until retried or discarded. |
| Readiness `Degraded` with key protection | Restore the certificate configuration on that host. When the reason says the ring cannot be unwrapped, add the earlier certificate under `PreviousCertificates`. If the certificate is lost, follow the recovery steps above. |
| A trigger defect blocks commits | Turn capture off (emergency stop above), fix forward, then turn it back on. Transitions committed meanwhile are not recorded. |
| `lifecycle_events` or deliveries grow | Configure the `webhookDeliveries` and `lifecycleEvents` [retention](#retention-and-backup) categories. Audit entries, subscription revisions, and the key ring are kept indefinitely in this release; include them in sizing. |

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

Worker option validation rejects inconsistent or out-of-range values at startup. Its runtime connection pool is capped at **the larger of 16 and `MaxConcurrency` + 8 connections per process**, and never exceeds the configured `Maximum Pool Size`. A configured maximum below `MaxConcurrency` + 8 logs a startup warning, because lease checks can then time out under load. Retention uses a separate pool capped at two, and [webhooks](#lifecycle-webhooks) a separate pool of `Webhooks:ConnectionPoolSize` (default 20). Size PostgreSQL connection capacity for every API/Worker replica and administrative connection, not just the dispatcher slot count.

External HTTP effects have **at-least-once** semantics. Retries may repeat a call after failure or lease loss; use the stable job ID as the remote service's idempotency key. Variable-version conflicts prevent silent overwrite and can open incidents. A timed-out caller, cancelled host, or database rollback cannot undo a remote side effect.

A captured result is the invocation boundary. If finalizing it fails unexpectedly, the Worker keeps the result and retries finalization after a five-second, database-timed delay without repeating the external call. `flowbit_worker_finalization_retries_total` counts these retries; a steadily rising value indicates a persistent finalization defect to investigate in the Worker logs.

## Monitoring and recovery

### Operational endpoints

These endpoints belong to the **Worker listener**, not the main API:

| Endpoint | Result | Meaning and limits |
| --- | --- | --- |
| `GET /health/live` | 200 `Healthy` while the host responds. | Process self-check; no database check. |
| `GET /health/ready` | 503 `Unhealthy` while migrations included in this Worker build are pending, and until the first successful queue acquisition query; then 200 `Healthy`. 200 `Degraded` reports a webhook problem without failing job processing. | Readiness is latched once both hold. It does not revert after later database outages and does not prove continuing queue progress. Webhook receiver outages never affect it. |
| `GET /metrics` | 200 Prometheus text. | Queue/lease/attempt/incident, runtime, and call activity measurements; restrict exposure. |

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

The `Flowbit.Runtime.CallActivities` meter exposes `flowbit.call.events`, tagged with the history note and failure code; `flowbit.call.fence_stops`; and the `flowbit.call.start_queue_age` and `flowbit.call.cancel_age` histograms. The Worker's `/metrics` endpoint exports them as `flowbit_call_events_total{event,failure_code}`, `flowbit_call_fence_stops_total`, and the `flowbit_call_start_queue_age_seconds` and `flowbit_call_cancel_age_seconds` summaries. It counts only what that Worker process records, so calls abandoned through the API, such as a parent cancelled over HTTP, appear only on a .NET diagnostics or OpenTelemetry listener in the API process. Metric tags are stable notes and codes, logs add invocation, root, and child identifiers and states, and neither carries mapped values. To see cancellations still in progress, run these read-only queries. The second lists instances whose own call was cancelled; a deeper descendant joins it when the cascade reaches its parent, and until then its detail reports `parentCall.cancellationInProgress`:

```sql
-- The age of open call-cancel jobs.
SELECT "Id", "InstanceId", "CallInvocationId", "Status", now() - "CreatedAt" AS age
FROM flowbit.workflow_jobs
WHERE "Kind" = 'callCancel' AND "Status" NOT IN ('completed', 'cancelled', 'skipped')
ORDER BY "CreatedAt";

-- Fenced children still running: their own incoming call was already cancelled.
SELECT c."ChildInstanceId", c."Id" AS invocation_id, now() - c."SettledAt" AS since_cancelled
FROM flowbit.call_activity_executions c
JOIN flowbit.workflow_instances i ON i."Id" = c."ChildInstanceId"
WHERE c."State" = 'cancelled' AND i."Status" = 'running'
ORDER BY c."SettledAt";
```

Observe queue depth and oldest age, timer lateness, acquisition/instance-lock latency, lease loss, retries, finalization retries, output conflicts, open incidents, and retention work. .NET meters are named `Flowbit.Worker`, `Flowbit.Runtime.Jobs`, `Flowbit.Runtime.CallActivities`, and `Flowbit.Webhooks`. A successful startup readiness response alone cannot detect a stuck queue; compare these signals over time and inspect Worker logs.

The API writes one request-log entry per request, `HTTP {method} {path} responded {status} in {elapsed} ms`, giving the status the client received. A client error the API maps, such as `400`, `401`, `403`, or `409`, is logged at Information after a warning that names its cause. `5xx` responses are logged at Error: `503` while the webhook key ring is unavailable, and `500`, with its exception, when nothing maps a failure. Alert on Error entries, not on every unsuccessful status.

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
| `job_conflict` incident | A durable job hit a conflict while its token still waited on it, or while its timer boundary still had that occurrence to fire, so it stopped rather than strand that work. **Operations** shows `Workflow job conflict at node #N.`, and `details` from `GET /api/incidents/{id}` gives the cause. One cause is a write to an archived shared key, which can happen when an older instance is reactivated after its key was retired. Correct the cause, for example by reactivating the key, then retry. The retry runs the job again, including its REST call. If the cause cannot be corrected, cancel the instance. |
| Call stays **Blocked** or **Queued** | Read the incident's `call_*` code. `call_target_unavailable` or `call_target_incompatible`: publish a fitting default. `call_depth_exceeded` or `call_capacity_exceeded`: let open calls settle or review the limits. Then retry. A retry cannot create a second child. |
| Parent waits with **Result ready** and an incident | Delivery failed and the frozen result is kept. Correct the parent's state, then retry; if the result itself is invalid, a retry cannot succeed, so cancel the parent. **Cancel instance #N** on that incident does so, and the incident closes because nothing is left to deliver. |
| Descendant stays `running` after a parent's cancel | Check Workers and the `callCancel` query above. Retry an incident on that job; its owner may already be terminal, which is allowed. **Cancel instance #N** on that incident cancels the child directly instead, which also closes the incident. |
| `job_invariant_violation` naming a call chain | An instance's persisted call ancestry is inconsistent, so nothing transitions on it. Investigate the rows; do not edit them to force progress. |
| `automatic_loop_limit` incident | Fix or review the automatic loop. A manual retry grants a fresh bounded allowance to the same stable job; it is not an unlimited retry exemption. |
| `timer_schedule_failed` incident | A timer start's first due time could not be computed: its business calendar is missing, the Worker host does not know the calendar's or the cron schedule's time zone, the calendar cannot count the duration within 36,525 days, or the cron schedule has no occurrence within 36,525 days. The timer start stays paused. Fix the calendar or the host's time-zone data, then retry the incident; the next timer-start reconciliation schedules it again or opens a new incident. |
| A cron timer start's job fails with `... has no occurrence within 36,525 days ...` or `... cannot be scheduled: timeZone ...` | Calculating the next occurrence failed while the job fired, so the instance it started was rolled back with it. The job retries and then opens a `job_execution_failed` incident. Fix the Worker host's time-zone data, then retry; the retry checks lateness again and may skip that occurrence. See [cron timer starts](#cron-timer-starts). |
| A timer-start job fails with `CK_timer_subscriptions_cron_not_completed` | A Worker that cannot read cron, released before cron timer starts, tried to end the schedule after one firing. The start rolled back. Upgrade every Worker, then retry the incident. |
| `... timer.timeDuration cannot be scheduled: ...` (or `timer.timeDate`) | A timer expression could not be scheduled from the instance's data: a variable it reads has no value, its result is not a valid date or duration (often a `datetime` without an offset), or its calendar cannot count the result. A request that reached the timer was rejected with `400` and left no incident; the API logs one warning without values, naming the instance, token, node, and property. In a Worker job, the job retries and then opens an incident. **Operations** shows that incident only as `Workflow job failed at node #N.` with type `job_execution_failed`; the message is the job's `lastFailureDescription` from `GET /api/jobs/{id}`. Correct the variable, for example with an administrative variable update, then repeat the request or retry the incident. A due time that was saved cannot be changed in place; see [correcting a schedule](developer-guide.md#expression-timers). |
| Worker stops during execution | A graceful stop lets running jobs finish within `ShutdownDrainSeconds`, then releases unfinished leases so a healthy replica recovers them at once; the interrupted attempt is recorded as `leaseLost` with failure code `workerShutdown`. After a crash or forced kill, recovery waits for lease expiry. Do not clear job or lease rows manually; execution fences determine whether a result is still valid. |
| Stale task/action/version operation returns 409 | Reload current state and follow compatibility/action discovery; do not force IDs or rewrite database state. |

See the [complete API reference](api-guide.md) for retry request fields and endpoint-specific 400/401/403/404/409 responses. Workflow-version changes have preview/compatibility surfaces, and terminal reactivation has explicit blockers; neither is a general repair command for arbitrary database state.

## Retention and backup

Retention is persisted deployment policy, managed through `/api/retention`. The five history/audit categories and the two webhook categories initially keep data forever. `webhookDeliveries` removes succeeded and discarded deliveries, attempts first, aged from settlement; pending, retrying, leased, and failed deliveries stay protected until they settle or are discarded. `lifecycleEvents` removes events, aged from the event time, only after fanout finished and no delivery references them. Webhook cleanup never sets `HistoryPrunedAt` or changes reactivation eligibility. The first upgraded Worker initializes completed-job and resolved-incident retention from `CompletedJobRetentionDays=30` and `ResolvedIncidentRetentionDays=90`; subsequent restarts do not overwrite saved policies. Use matching bootstrap values on every replica.

Cleanup is performed by the Worker, including manually requested runs. It yields to a busy queue, uses bounded transactions, preserves referenced/current records, and resumes durable progress. Defaults are batches of 250, a 1,000 ms batch delay, 5,000 ms idle delay, a busy threshold of 100 runnable jobs, and 30-second queue lag. Tune with database I/O and workflow latency measurements.

**Pruning instance-owned history permanently blocks that instance's reactivation.** Disabling retention later does not restore deleted data or remove the blocker. Job/incident cleanup alone does not create that history-pruned state. Preview policies first and read [retention guarantees](../Flowbit/README.md#history-and-audit-retention) before changing them.

Node-activity deletion additionally sets the instance's first `NodeActivityPrunedAt` in the same transaction as deletion and `UpdatedAt`. Protected or running-instance visits are retained and do not set that marker. The diagram labels remaining evidence as partial; retained rows are a skewed sample because references and open work protect some visits. Workflow-history retention may remove History-table rows while the separately retained diagram evidence remains visible.

Back up the PostgreSQL database coherently, including `flowbit` data, sequences, functions/triggers, and migration history, together with the matching application release and protected configuration recovery process. Exported workflow JSON is useful for definitions but does not include running instances, claims, variable history, idempotency ownership, timers, or jobs. Test restores into an isolated environment with Workers and external integrations disabled until you have assessed pending timers and possible repeated external effects.

Next: [API reference](api-guide.md) · [BPMN support](bpmn-support.md) · [Documentation home](index.md)
