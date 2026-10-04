# Deployment and operations

[Documentation home](index.md) · [Local setup](getting-started.md) · [Developer guide](developer-guide.md) · [API reference](api-guide.md)

Flowbit runs as a .NET 10 API backed by PostgreSQL. Deploy `Flowbit.Worker` when using durable tasks, timers, durable conditionals, administrative batches, or retention cleanup. `Flowbit.Ui` is optional for HTTP integrations and currently uses a shared development identity; production user authentication requires application changes.

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

Run replicas against the same authoritative database, with matching runtime configuration and compatible binaries. In-memory definition caches are rebuildable. PostgreSQL locking and durable fences coordinate mutations; extra API/Worker replicas do not replace a database availability and recovery strategy.

Terminate HTTPS at a configured hosting boundary or configure Kestrel certificates. For a reverse proxy, validate forwarded scheme/host behavior in that deployment: this checkout does not provide a complete reverse-proxy configuration. The API and UI invoke HTTPS redirection. A browser frontend that calls the API directly also needs an explicit origin/CORS design; the bundled UI calls from its server.

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

Startup waits for PostgreSQL's `pg_isready` health check before creating the API. The API applies migrations before accepting HTTP requests; a successful `/openapi/v1.json` check then allows the UI and Worker to start. Compose uses `depends_on` with `service_healthy` for this ordering, as described in the [Docker startup-order documentation](https://docs.docker.com/compose/how-tos/startup-order/). The Worker readiness check becomes healthy after its first successful durable-queue query and remains latched; it does not continuously verify database availability. Compose allows 45 seconds for Worker shutdown, covering its default 30-second drain and host shutdown allowance.

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

The UI Dockerfile copies Razor sources before restore so the SDK includes Blazor's framework assets. Its health check requests both the token page and the Blazor client script; server-rendered HTML alone does not establish that interactive controls can start.

This Compose file is not a production deployment template. Changing only `DOTNET_ENVIRONMENT` disables automatic migrations and the API's development OpenAPI health check; it also leaves the UI's shared token generator intact. For deployment, apply [reviewed migrations](#database-migrations), provide a suitable API readiness probe, configure authentication and HTTPS, and follow the upgrade procedure below. Before rebuilding against an existing local database after schema changes, stop the stack so old API/Worker processes cannot run during migrations.

## Configuration

ASP.NET Core configuration accepts JSON and environment variables. Use `__` to separate nested environment keys. Supply connection strings and signing/client secrets from your deployment's protected configuration mechanism; checked-in values are development examples.

| Setting / environment key | Applies to | Purpose and current behavior |
| --- | --- | --- |
| `ConnectionStrings:Flowbit` / `ConnectionStrings__Flowbit` | API, Worker, EF tooling | Npgsql connection string. Set explicitly; do not inherit a developer machine's database. Legacy `ConnectionStrings:WorkflowEngine` remains a fallback. |
| `DOTNET_ENVIRONMENT` | All hosts | Use `Production` for a deployed service. Development API startup applies migrations automatically and exposes OpenAPI/Swagger. |
| `ASPNETCORE_URLS` | API, UI | Host listener URLs. Worker instead uses `FlowbitWorker:HealthListenUrl`. |
| `Jwt:Issuer`, `Jwt:Audience`, `Jwt:Key` | API; matching values for development UI | Current bearer validation uses the shared symmetric key, issuer, audience, and lifetime. Replace all sample values. |
| `WorkflowApi:BaseUrl` / `WorkflowApi__BaseUrl` | UI | Server-to-server API address, default `http://localhost:5017`. |
| `WorkflowContext:Config:<name>` / `WorkflowContext__Config__<name>` | API and Worker | Trusted `config.<name>` values for expressions and placeholders, including integration endpoints/credentials. Supply the same required values to both execution hosts. |
| `WorkflowContext:AllowedClaims` / `WorkflowContext__AllowedClaims__0` etc. | API and Worker | Allowlist for `sys.claim.*`. API sample configuration allows `depId`; Worker does not automatically inherit it. |
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
Jwt__Issuer=YOUR_TRUSTED_ISSUER
Jwt__Audience=YOUR_FLOWBIT_AUDIENCE
Jwt__Key=INJECTED_HIGH_ENTROPY_SIGNING_SECRET
WorkflowContext__Config__exampleApiBaseUrl=https://YOUR_CONTROLLED_SERVICE
WorkflowContext__Config__exampleApiToken=INJECTED_SERVICE_SECRET
WorkflowContext__AllowedClaims__0=depId
WorkflowDurableProcessing__PublicationEnabled=false
FlowbitWorker__HealthListenUrl=http://127.0.0.1:8081
```

Engine and workflow **settings stored in PostgreSQL** are distinct from host configuration. They are managed through [settings APIs](api-guide.md), not arbitrary environment variable names. Examples include `Workflow.RequiredRole`, `Settings.RequiredRole`, `WorkflowJobs.RequiredRole`, `NodeExecution.RequiredRole`, `Workflow.MultiInstance.MaxInstances`, and `Workflow.Async.MaxConsecutiveAutomaticActivations`. Missing/default management roles are generally `admin`; definition-owned permissions remain separate.

`Authentication.UserIdentityClaim` is a persisted engine setting read at API startup. Configure the intended claim before starting replicas, then restart APIs together when changing it. A nonblank configured claim is authoritative; do not assume `sub` automatically replaces the current identity selection. See the [authentication context API](api-guide.md).

### Selected-claim audit

`WorkflowAudit:AllowedClaims` is an opt-in list of JWT claim names. Its default `[]` records no claim snapshots. For example, configure `WorkflowAudit__AllowedClaims__0=depId` to retain the selected claim; this does not make `depId` available to workflow expressions. `WorkflowContext:AllowedClaims` controls expression access separately. Configure API and Worker consistently and restart the hosts when changing this configuration.

Configured names are trimmed and deduplicated case-insensitively; output keys retain the selected spelling. Whole claim names are matched case-insensitively, with support for known JWT/.NET inbound mappings, not arbitrary URI suffixes. Each matching claim contributes a string value in original order, including duplicates; choosing one name does not capture every token claim.

The runtime records selected claims as JSON objects of string arrays in `instance_history` and as independent starting/completing snapshots in `node_executions`. Repeated values are retained. The snapshot belongs to the authenticated caller, including when acting through delegation; `actingFor` identifies the represented owner separately. System-only activity has no JWT snapshot. Durable work carries its captured causal snapshot across Worker execution and retries. Configuration changes affect new captures and do not rewrite existing snapshots.

Select claims with their read audience in mind: instance history is available through the authenticated instance-detail route, while node detail uses node-activity authorization. The allowlist must not include credentials, bearer tokens, or claims whose disclosure is inappropriate for those readers. Snapshots are audit data, not a new source of authorization or workflow expressions.

Apply the additive claim-audit migration before upgrading writers, then deploy matching API, Worker, and UI versions together. Existing rows and older queued jobs remain without a snapshot; there is no historical backfill. Avoid mixed old/new writers when complete claim-event coverage is required. Verify a controlled claim/unclaim and a durable action before enabling capture for general use. `null` means not recorded, and `{}` means capture was enabled but no selected name was present.

Workflow-history retention deletes a row's actor claims with that history row; node-activity retention independently deletes visit snapshots. Database backups include captured claims. Existing retention protections and reactivation restrictions still apply; see [retention and backup](#retention-and-backup).

## AI authoring and local OCR

The API build exports and ships the portable [authoring package](ai-authoring.md).
Startup loads it once and verifies its manifest/resource hashes against the current
model. Keep the published `authoring` directory with the API binaries; a missing or
incompatible package fails startup. Export requires the repository's documentation
and example catalog at build time; deployed generation uses only the packaged files.
No database migration or persistent conversation store is introduced.

| API setting (`__` separates environment keys) | Default | Purpose |
| --- | --- | --- |
| `WorkflowAi:Enabled` | `true` | Enable provider discovery/generation. |
| `WorkflowAi:ExecutionVariant` | `current` | `current` or `optimized`, both using the custom authoring loop. Optimized remains experimental. Removed values fail with `provider_configuration` before any provider call. See [execution variants](ai-authoring.md#execution-variants-and-evaluation) and the [evaluation tool](../Flowbit/tools/AuthoringEval/README.md) before changing defaults. |
| `WorkflowAi:OpenCodeBaseUrl` | `https://opencode.ai/zen/v1/` | Server-owned OpenCode Zen endpoint. HTTPS required except loopback test servers; callers cannot supply a URL. |
| `WorkflowAi:OpenCodeModels` | Shipped configuration: `glm-5.3-flash`; unconfigured fallback: `kimi-k2.7-code`, `glm-5.3`, `glm-5.3-flash` | An explicit configured list replaces fallback models; its first entry is the default. Verify live account/model compatibility separately. |
| `WorkflowAi:OpenCodeReasoningEfforts` | Shipped configuration: `{"glm-5.3-flash":"max"}`; unconfigured fallback: `{}` | Model-id-to-effort mapping. A configured selected model sends `reasoning_effort` as exactly `low`, `high`, or `max`; any other value rejects the request with `provider_configuration` (503) before provider transport. Unmapped models omit the field and retain provider defaults. |
| `WorkflowAi:MaxInputCharacters` / `MaxHistoryMessages` | `200000` / `30` | Combined input/history bounds. |
| `WorkflowAi:MaxContextCharacters` | `750000` | Assembled prompt bound, in addition to the model token budget. Original requirements and source pages remain available through bounded reads. |
| `WorkflowAi:MaxWorkflowCharacters` / `MaxOutputBytes` | `2097152` / `2097152` | Workflow/context and provider response bounds. |
| `WorkflowAi:RunTimeoutSeconds` / `RequestTimeoutSeconds` | `1800` / `180` | Total run deadline (1–3600 seconds) and individual provider-call deadline. Every call is also bounded by the remaining run time. Continue starts another bounded run. |
| `WorkflowAi:MaxProviderCalls` / `MaxRunOutputTokens` | `50` / `262144` | Total calls and output-token budget per run, including recovery. Missing usage and retryable failed attempts are conservatively charged their requested allowance. |
| `WorkflowAi:MaxTransportRetries` / `RetryBaseDelayMilliseconds` | `2` / `1000` | Bounded transient retries with exponential delay, jitter, and provider `Retry-After`; waits must fit the remaining run deadline. |
| `WorkflowAi:MaxTruncationRecoveries` / `MaxOperationsPerBatch` | `3` / `20` | Output-limit recoveries before pausing, and initial/maximum atomic edit-batch size. Truncation and timed-out calls shrink batches; accepted edits adjust batch size according to reported output usage. Truncation recovery can raise the output allowance once for a single operation. |
| `WorkflowAi:MaxRepairAttempts` / `MaxConcurrentRequests` | `2` / `4` | Consecutive validation/command repairs and process-local concurrent runs. Accepted edits reset repair/truncation counters; global run limits still apply. |
| `WorkflowAi:ContextTokens` / `InitialOutputTokens` / `MaxModelOutputTokens` | `65536` / `8192` / `32768` | Conservative fallback model profile; input estimation uses rounded-up UTF-8 bytes divided by two plus 32 tokens per message, reserving the requested output allowance and a 10% context safety margin. This is a heuristic, not the provider's tokenizer or detected model capacity. |
| `WorkflowAi:ModelProfiles` | Shipped Flash profile: context `65536`, initial output `16384`, maximum output `32768`; unconfigured fallback: `{}` | Optional model-id map with `ContextTokens`, `InitialOutputTokens`, and `MaxOutputTokens`; overrides the fallback profile for that model. The model must still be enabled. |
| `WorkflowAiDocuments:MaxBytes` / `MaxPages` / `MaxCharacters` | `20971520` / `100` / `200000` | PDF limits; excess input is rejected. |
| `WorkflowAiDocuments:TimeoutSeconds` / `ProcessTimeoutSeconds` | `300` / `45` | Total extraction and individual raster/OCR process deadlines. |
| `WorkflowAiDocuments:MaxRasterDimension` / `MaxConcurrentExtractions` | `4096` / `2` | Maximum raster dimension in pixels and process-local extraction concurrency. |
| `WorkflowAiDocuments:PdfToPpmPath` / `TesseractPath` | `pdftoppm` / `tesseract` | Executable paths; arguments are passed without a shell. |

The default endpoint is now OpenCode Zen. Use a key/account with access to the
selected Zen model. Existing deployments that explicitly configure an endpoint
keep that override; to continue using Go, set `WorkflowAi:OpenCodeBaseUrl` to
`https://opencode.ai/zen/go/v1/`. The provider label is OpenCode Zen by default and
OpenCode Go for that Go endpoint. The API provider id remains `opencode-go` for
compatibility with existing callers. Restart the API after changing configuration.

The shipped configuration explicitly selects `max` reasoning for GLM-5.3-Flash,
allows 180 seconds per model call, and starts with a 16,384-token output allowance.
These settings accommodate long reasoning responses; they do not guarantee completion
within one 30-minute run. Repeated individual call timeouts pause with
`provider_timeout`, while temporary server/connectivity failures retain
`provider_unavailable`. Check the configured endpoint and model when diagnosing retries;
three roughly 90-second calls indicate an older 90-second request timeout.
Empty or unreadable successful responses receive bounded transport retries, then
pause with `provider_invalid_response`. Provider `Retry-After` hints cover the
maximum one-hour run; a wait beyond the remaining run time pauses immediately.
Continuation retains up to 18 validated reference/source read positions within a
32,000-character excerpt budget, rebuilding text from the original inputs rather
than trusting cached text. Upgrade and restart both API and UI to carry this
optional checkpoint field through the Blazor session.

For models without an explicit effort mapping, reasoning defaults to the provider's choice. The official
[GLM-5.3-Flash model card](https://huggingface.co/zai-org/GLM-5.3-Flash#note) documents
`low`, `high`, and `max`, with `max` when omitted. Administrators can set an explicit
effort after checking the latency and result quality for their requirements:

```json
{
  "WorkflowAi": {
    "OpenCodeReasoningEfforts": {
      "glm-5.3-flash": "max"
    }
  }
}
```

This reasoning mapping does not enable models or change the default model, output-token
budgets, or request deadlines. It is server-owned configuration, not a caller-supplied
request option. Configure an effort only after verifying that the selected model
and configured OpenCode endpoint support it; accepted values in Flowbit do not establish
live provider compatibility. Record an authorized live check for each configured
model/provider combination. Models absent from the mapping omit `reasoning_effort`;
configuring Flash's effort does not change another model's requests.

Set model profiles from the limits supported by your provider/account. Positive bounds,
bounded retry/call limits, and `InitialOutputTokens <= MaxOutputTokens < ContextTokens`
are validated before generation; invalid configuration returns `provider_configuration`
without a provider call. Increasing output allowance does not fix insufficient input
context. The adapter distinguishes temporary throttling/server/connectivity failures
from terminal authentication, account quota, refusal, context, and response-size errors;
the runner owns retry decisions and never silently switches models.

Both turn endpoints accept at most 8 MiB so a continuation can carry its original
request and checkpoint. Each workflow/draft remains separately bounded to 2 MiB by
default. The stream uses `application/x-ndjson`, explicit flushes, and no-store/no-buffer
headers. Configure reverse proxies to pass streamed responses without buffering and
allow the configured run plus response overhead. The dedicated UI authoring HTTP
client and stream reader allow 61 minutes, covering the maximum one-hour server
run plus overhead; ordinary UI API requests retain their six-minute timeout.
Administrators can shorten the default 30-minute run or extend it up to one hour.
Explicit older overrides such as `WorkflowAi__RunTimeoutSeconds=300` still apply;
restart both API and UI after upgrading or changing these settings.

Continuations have no API session store or database migration. A checkpoint lives in
the current Blazor Server UI circuit and is sent back as untrusted bounded state with
the original input. Any API replica with the matching contract can resume it; changed
inputs, selected catalog contracts, or package hashes reject it as stale. UI restart,
circuit loss, close/reset, navigation, or identity/model changes discard panel state.
Do not log checkpoint bodies: they may contain restored workflow credentials.

New version 2 checkpoints also bind the execution variant, reasoning effort, model
profile, and provider endpoint. Replicas must agree on these settings for Continue;
changing them requires a new request. Version 1 checkpoints retain current-engine
compatibility. Both supported modes run inside Flowbit without an agent SDK or
persistent session store. To restore the default for new requests, set
`WorkflowAi:ExecutionVariant=current`; version 2 checkpoints for optimized require
their original settings or a new request.
Finish or discard open assistant drafts before changing engine/reasoning settings.
To restore the shipped behavior explicitly, use `ExecutionVariant=current` and
`OpenCodeReasoningEfforts:glm-5.3-flash=max`. Microsoft Agent Framework is removed.
Before upgrading a deployment configured with `ExecutionVariant=agent-framework`,
set it to `current` or `optimized` and start fresh authoring requests. The removed
setting returns `provider_configuration` (503); there is no silent fallback.
Old framework checkpoints return `checkpoint_configuration_changed` (409) under
either supported mode. No database migration or provider-key relocation is needed.
Historical evaluation results remain [available](../Flowbit/tools/AuthoringEval/RESULTS.md).

The `Flowbit.Ai.Authoring` meter exposes `flowbit.ai.provider.calls`,
`flowbit.ai.recoveries`, `flowbit.ai.provider.seconds`, and `flowbit.ai.run.seconds` without prompt/workflow contents.
Run responses report model calls, elapsed seconds, output-token accounting, and whether
usage includes estimates. These are operational bounds, not provider billing totals.
Summaries additionally expose reported input tokens, first-edit/last-call timing,
read/duplicate-read counts, retries, and accepted batches. Flowbit's custom runner
owns model/command iteration and recovery in both supported modes. Every HTTP
attempt counts, including failed retries. Continue restores the validated draft
and read positions with fresh per-run accounting; no provider conversation is
persisted. See [execution ownership](ai-authoring.md#execution-variants-and-evaluation).

The provider adapter sends `Flowbit/1.0` as its user-agent and a stable
`x-opencode-session` for the temporary conversation. It receives the key only for
the current request. Application HTTP-client header logging is redacted. Do not
enable proxy/APM request-body or sensitive-header capture for these routes; the
application does not persist provider keys or conversations. Selected workflow
credential fields are masked in model context and unchanged values restored on
edits. Review the provider's data handling before sending requirements or PDFs.
The hosted UI retains the existing shared test identity boundary; its AI keys and
conversation are panel-local and cleared on close, reset, disposal, and identity
replacement. Production per-user authentication remains separate work.

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

The current API validates bearer JWTs using `Jwt:Key` as a symmetric signing key. Issuer, audience, signature, and token lifetime are checked. Roles and the configured identity claim feed runtime authorization; allowlisted custom claims may be used in workflow expressions.

There is **no API token-issuance endpoint**. The UI's `/token` page is a development utility. It can mint an HMAC-SHA256 token from the configured shared key or accept a token, then sends that token through its typed API client.

**Flowbit.Ui is not a per-user authentication implementation.** `TokenState` and `DevTokenFactory` are registered as singletons; the selected identity applies to the entire UI process. `Production` does not convert this into user sessions or remove the token generator. Keep that utility within a trusted development environment. A production UI requires an authentication flow, per-user token/session storage, and corresponding authorization boundaries implemented in source.

Integrating OIDC is also a source integration: configure a trusted provider in `AddJwtBearer`, map identity/role claims to the engine's contracts, and obtain tokens through your application. Merely adding `Authority` to configuration does not replace the current symmetric-key validator. Do not treat the bundled development signing key as a production trust boundary.

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
# ConnectionStrings__Flowbit and Jwt__* are supplied by the deployment environment.
foreach ($flowbitKey in @('ConnectionStrings__Flowbit', 'Jwt__Issuer', 'Jwt__Audience', 'Jwt__Key')) {
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
# ConnectionStrings__Flowbit and Jwt__* are supplied by the deployment environment.
: "${ConnectionStrings__Flowbit:?Set the intended database connection}"
: "${Jwt__Issuer:?Set the trusted issuer}"
: "${Jwt__Audience:?Set the API audience}"
: "${Jwt__Key:?Inject the signing secret}"
cd artifacts/publish/api
dotnet Flowbit.Api.dll
```

Use a separate process rooted at the Worker publish directory with `dotnet Flowbit.Worker.dll`, and optionally one at the UI publish directory with `dotnet Flowbit.Ui.dll`. Assign distinct listener ports. These listener examples assume HTTPS is handled by the chosen hosting configuration.

## Database migrations

**Apply reviewed migrations before starting upgraded API/Worker replicas.** Only Development API startup automatically migrates. Production API startup reads persisted settings, so an unmigrated database cannot be treated as an empty ready service. Worker startup does not apply migrations.

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
| `PollMilliseconds` | 1000 | Authoritative queue polling interval. PostgreSQL notifications are wake-up hints. |
| `IdleBackoffMilliseconds` | 5000 | Idle backoff bound; persisted due/lease deadlines can shorten it. |
| `TimerStartReconcileSeconds` / `TimerStartReconcileBatchSize` | 1 / 100 | Reconcile bounded workflow-family schedule batches under a PostgreSQL advisory leader. |
| `ShutdownDrainSeconds` | 30 | Graceful drain; host shutdown timeout adds five seconds. |
| `HealthListenUrl` | `http://0.0.0.0:8081` | Unauthenticated operational listener. Bind deliberately. |

Worker option validation rejects inconsistent or out-of-range values at startup. Its runtime connection pool is capped at **16 connections per process**, including when a larger pool was configured. Retention uses a separate pool capped at two. Size PostgreSQL connection capacity for every API/Worker replica and administrative connection, not just the dispatcher slot count.

External HTTP effects have **at-least-once** semantics. Retries may repeat a call after failure or lease loss; use the stable job ID as the remote service's idempotency key. Variable-version conflicts prevent silent overwrite and can open incidents. A timed-out caller, cancelled host, or database rollback cannot undo a remote side effect.

## Monitoring and recovery

### Operational endpoints

These endpoints belong to the **Worker listener**, not the main API:

| Endpoint | Result | Meaning and limits |
| --- | --- | --- |
| `GET /health/live` | 200 `Healthy` while the host responds. | Process self-check; no database check. |
| `GET /health/ready` | 503 `Unhealthy` until the first successful queue acquisition query, then 200 `Healthy`. | Readiness is latched. It does not revert after later database outages and does not prove continuing queue progress. |
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

Observe queue depth and oldest age, timer lateness, acquisition/instance-lock latency, lease loss, retries, output conflicts, open incidents, and retention work. .NET meters are named `Flowbit.Worker` and `Flowbit.Runtime.Jobs`. A successful startup readiness response alone cannot detect a stuck queue; compare these signals over time and inspect Worker logs.

Use the UI Operations page or the role-protected jobs/incidents APIs. `WorkflowJobs.RequiredRole` defaults to `admin`. Collection endpoints use opaque keyset cursors; use detail/attempt endpoints to inspect evidence rather than expecting large execution snapshots in every list row.

| Symptom | Recovery approach |
| --- | --- |
| Durable tasks or timers stay queued | Check matching Worker processes, target database, configuration, due times, lease expiry, queue metrics, and logs. Turning the publication gate on does not start a Worker. |
| REST failure or exhausted retries | Inspect the incident/attempt, correct the integration, then use the fenced retry endpoint. Account for any remote effect already performed. |
| Output-version conflict | Inspect intervening variable/shared-value changes and the failed attempt. Resolve the business state before requesting a new attempt. |
| `automatic_loop_limit` incident | Fix or review the automatic loop. A manual retry grants a fresh bounded allowance to the same stable job; it is not an unlimited retry exemption. |
| Worker stops during execution | Allow lease expiry/recovery by a healthy replica. Do not clear job or lease rows manually; execution fences determine whether a result is still valid. |
| Stale task/action/version operation returns 409 | Reload current state and follow compatibility/action discovery; do not force IDs or rewrite database state. |

See the [complete API reference](api-guide.md) for retry request fields and endpoint-specific 400/401/403/404/409 responses. Workflow-version changes have preview/compatibility surfaces, and terminal reactivation has explicit blockers; neither is a general repair command for arbitrary database state.

## Retention and backup

Retention is persisted deployment policy, managed through `/api/retention`. The five history/audit categories initially keep data forever. The first upgraded Worker initializes completed-job and resolved-incident retention from `CompletedJobRetentionDays=30` and `ResolvedIncidentRetentionDays=90`; subsequent restarts do not overwrite saved policies. Use matching bootstrap values on every replica.

Cleanup is performed by the Worker, including manually requested runs. It yields to a busy queue, uses bounded transactions, preserves referenced/current records, and resumes durable progress. Defaults are batches of 250, a 1,000 ms batch delay, 5,000 ms idle delay, a busy threshold of 100 runnable jobs, and 30-second queue lag. Tune with database I/O and workflow latency measurements.

**Pruning instance-owned history permanently blocks that instance's reactivation.** Disabling retention later does not restore deleted data or remove the blocker. Job/incident cleanup alone does not create that history-pruned state. Preview policies first and read [retention guarantees](../Flowbit/README.md#history-and-audit-retention) before changing them.

Back up the PostgreSQL database coherently, including `flowbit` data, sequences, functions/triggers, and migration history, together with the matching application release and protected configuration recovery process. Exported workflow JSON is useful for definitions but does not include running instances, claims, variable history, idempotency ownership, timers, or jobs. Test restores into an isolated environment with Workers and external integrations disabled until you have assessed pending timers and possible repeated external effects.

Next: [API reference](api-guide.md) · [BPMN support](bpmn-support.md) · [Documentation home](index.md)
