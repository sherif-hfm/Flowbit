# Getting started

[Documentation home](index.md)

Run Flowbit locally and complete a claimed approval through HTTP. This tutorial uses the existing [Roles, Claim, and Bypass](../examples/user-tasks/01-roles-claim-and-bypass.json) definition. No application client code is needed.

## Contents

- [Prerequisites](#prerequisites)
- [Start an isolated PostgreSQL database](#start-an-isolated-postgresql-database)
- [Run the API](#run-the-api)
- [Obtain a development bearer token](#obtain-a-development-bearer-token)
- [Understand the example](#understand-the-example)
- [PowerShell HTTP walkthrough](#powershell-http-walkthrough)
- [Bash HTTP walkthrough](#bash-http-walkthrough)
- [Inspect the result](#inspect-the-result)
- [When to run the Worker](#when-to-run-the-worker)
- [Troubleshooting and cleanup](#troubleshooting-and-cleanup)

## Prerequisites

- .NET **10 SDK** to build and run this checkout.
- Docker with Linux containers for the disposable PostgreSQL **17** setup below, or an explicitly configured isolated PostgreSQL database.
- Git and a modern browser for the development token page.
- Windows PowerShell 5.1+ or PowerShell 7 on Windows; alternatively Bash, cURL, and `jq` on Linux/macOS. Install `jq` with your operating system's package manager if needed.
- Available local ports `55439` (database), `15017` (API), `15152` (UI), and optionally `18081` (Worker probes).

Clone the repository, then run every command below from its root directory. Open additional terminals at that same directory when instructed.

```bash
git clone https://github.com/sherif-hfm/Flowbit.git
cd Flowbit
dotnet --version
docker version
```

Use either shell walkthrough, once the shared setup is complete. Both create a new definition version and a new instance when rerun. The example's `requestReference` is ordinary business data, not an automatic uniqueness constraint.

## Start an isolated PostgreSQL database

The following container is dedicated to this tutorial. Its port is bound to loopback. The password and JWT key shown on this page are public local-development values; supply independently managed secrets for deployment.

PowerShell:

```powershell
docker run --name flowbit-docs-local --detach `
  --publish 127.0.0.1:55439:5432 `
  --env POSTGRES_DB=flowbit_docs `
  --env POSTGRES_USER=flowbit `
  --env POSTGRES_PASSWORD=flowbit-docs-local-only `
  postgres:17-alpine
docker exec flowbit-docs-local pg_isready -U flowbit -d flowbit_docs
```

Bash:

```bash
docker run --name flowbit-docs-local --detach \
  --publish 127.0.0.1:55439:5432 \
  --env POSTGRES_DB=flowbit_docs \
  --env POSTGRES_USER=flowbit \
  --env POSTGRES_PASSWORD=flowbit-docs-local-only \
  postgres:17-alpine
docker exec flowbit-docs-local pg_isready -U flowbit -d flowbit_docs
```

Wait for `accepting connections`; repeat `pg_isready` if the server is still starting. There is no Docker Compose file in this checkout. The application processes below run on your host and connect to the mapped port.

## Run the API

In a dedicated terminal, explicitly override the database and JWT configuration. This prevents the tutorial from using a database address saved in an existing configuration file.

PowerShell:

```powershell
$env:ConnectionStrings__Flowbit = 'Host=127.0.0.1;Port=55439;Database=flowbit_docs;Username=flowbit;Password=flowbit-docs-local-only'
$env:Jwt__Issuer = 'flowbit-docs-dev'
$env:Jwt__Audience = 'flowbit-docs-api'
$env:Jwt__Key = 'flowbit-docs-demo-signing-key-only-2026-09-09'
dotnet run --no-launch-profile --project ./Flowbit/src/Flowbit.Api/Flowbit.Api.csproj -- --environment Development --urls http://127.0.0.1:15017
```

Bash:

```bash
export ConnectionStrings__Flowbit='Host=127.0.0.1;Port=55439;Database=flowbit_docs;Username=flowbit;Password=flowbit-docs-local-only'
export Jwt__Issuer='flowbit-docs-dev'
export Jwt__Audience='flowbit-docs-api'
export Jwt__Key='flowbit-docs-demo-signing-key-only-2026-09-09'
dotnet run --no-launch-profile --project ./Flowbit/src/Flowbit.Api/Flowbit.Api.csproj -- --environment Development --urls http://127.0.0.1:15017
```

Wait for the successful migration message and the listening URL. **Development startup applies database migrations.** For an existing or production database, use the explicit migration and upgrade procedure in [Deployment](deployment.md).

Development exposes [Swagger](http://127.0.0.1:15017/swagger) and the [OpenAPI document](http://127.0.0.1:15017/openapi/v1.json). These development endpoints are not production health probes.

## Obtain a development bearer token

In a second terminal, start Flowbit.Ui with the same issuer, audience, and signing key and the explicit API address.

PowerShell:

```powershell
$env:WorkflowApi__BaseUrl = 'http://127.0.0.1:15017'
$env:Jwt__Issuer = 'flowbit-docs-dev'
$env:Jwt__Audience = 'flowbit-docs-api'
$env:Jwt__Key = 'flowbit-docs-demo-signing-key-only-2026-09-09'
dotnet run --no-launch-profile --project ./Flowbit/src/Flowbit.Ui/Flowbit.Ui.csproj -- --environment Development --urls http://127.0.0.1:15152
```

Bash:

```bash
export WorkflowApi__BaseUrl='http://127.0.0.1:15017'
export Jwt__Issuer='flowbit-docs-dev'
export Jwt__Audience='flowbit-docs-api'
export Jwt__Key='flowbit-docs-demo-signing-key-only-2026-09-09'
dotnet run --no-launch-profile --project ./Flowbit/src/Flowbit.Ui/Flowbit.Ui.csproj -- --environment Development --urls http://127.0.0.1:15152
```

1. Open [Test identity](http://127.0.0.1:15152/token).
2. Enter `docs.reviewer` in **User**.
3. Enter `admin, Requester, Reviewer` in **Roles**. The default expiry is 60 minutes; increase it for a longer local session.
4. Leave custom claims empty and click **Generate and apply**.
5. Copy the value from **Raw JWT**. Paste only the token when the shell walkthrough prompts for it, without the `Bearer ` prefix.

There is **no token-issuance HTTP endpoint**. The local UI server generates this development JWT using its configured symmetric key. Its test identity is process-wide and shared by every UI user. It is not production sign-in or per-user session isolation. See [authentication boundaries](deployment.md) before exposing the UI or API beyond local development.

All remaining approval steps use HTTP. Once a token is copied, the UI need not remain running for those requests; the token remains usable until its expiry if the API configuration stays the same.

## Understand the example

| Configuration | Meaning |
| --- | --- |
| Workflow key `example-user-task-roles-claim-bypass` | The authored top-level JSON `id`; common to every version. |
| Start node `1` | Allows `Requester`; requires `requestReference`, whose length after trimming is at least three characters. |
| User task `2`, **Claimed Review** | Allows `Reviewer` or `Supervisor` and requires a claim. |
| **Approve after claim** | Requires a claim, a `Reviewer` or `Supervisor` role, and `reviewNote` of at least three trimmed characters. Leads to **Approved**. |
| **Escalate without claim** | An alternative for `Supervisor`, with a required `escalationNote` of at least five trimmed characters. Not used here. |
| Definition administration | The fresh database's `Workflow.RequiredRole` setting is `admin`; the token includes this role to create and publish the example. |

The example's `Trim` expressions validate length; they do not automatically trim the stored strings. `admin` does not automatically grant the workflow's `Requester`, `Reviewer`, or `Supervisor` roles. Workflow cancellation uses `WorkflowAdministrator`; recovery unclaim uses `Supervisor`.

The API assigns a **definition ID** when importing JSON, an **instance ID** when starting it, and a **task ID** when creating human work. These are different from authored node/flow IDs. Both walkthroughs retain the returned IDs and discover the approval by its authored `externalId`; do not replace runtime IDs with sample diagram IDs. See the [identifier glossary](developer-guide.md).

## PowerShell HTTP walkthrough

Run this in a third terminal at the repository root. `Invoke-RestMethod` handles HTTP and JSON responses; `ConvertTo-Json -Depth 100` preserves nested definition configuration.

### 1. Authenticate and create a definition

```powershell
$ErrorActionPreference = 'Stop'
$api = 'http://127.0.0.1:15017'
$token = (Read-Host 'Paste the development JWT').Trim()
$headers = @{ Authorization = "Bearer $token" }
$definition = Get-Content ./examples/user-tasks/01-roles-claim-and-bypass.json -Raw | ConvertFrom-Json
$body = @{ definition = $definition; publish = $false } | ConvertTo-Json -Depth 100
$workflow = Invoke-RestMethod "$api/api/workflows" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
$workflow.id
```

Creation returns **201** with the stored definition, including `id`, `workflowKey`, `version`, and `isPublished`. The envelope's `definition` is the authored document; `$workflow` is a runtime API response.

### 2. Publish and start that exact version

```powershell
Invoke-RestMethod "$api/api/workflows/$($workflow.id)/publish" -Method Post -Headers $headers | Out-Null
$body = @{
  workflowId = $workflow.id
  startEventId = 1
  variables = @{ requestReference = 'REQ-DEMO-001' }
} | ConvertTo-Json -Depth 10
$instance = Invoke-RestMethod "$api/api/instances" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
$instance.id
```

Publication returns **204** with no response body. Start returns **201**. `workflowId` pins the returned immutable definition version. Do not also send `workflowKey`; starting by key is a separate choice that resolves the published default version.

### 3. Find the inbox task and claim it

```powershell
$inbox = Invoke-RestMethod "$api/api/instances/inbox?instanceId=$($instance.id)&page=1&pageSize=50" -Headers $headers
$tasks = @($inbox.items | Where-Object instanceId -EQ $instance.id)
if ($tasks.Count -ne 1) { throw 'Expected one visible approval task; check roles and instance state.' }
$taskId = $tasks[0].userTaskId
$claimed = Invoke-RestMethod "$api/api/user-tasks/$taskId/claim" -Method Post -Headers $headers
$claimed
```

Before claiming, this actor sees `canClaim: true` and `canAct: false`. Claiming returns **200** with task details. An inbox entry's `userTaskId` is the address for task actions.

### 4. Discover and submit approval

```powershell
$flows = Invoke-RestMethod "$api/api/user-tasks/$taskId/flows" -Headers $headers
$approvals = @($flows | Where-Object externalId -EQ 'FLOW_APPROVE_AFTER_CLAIM')
if ($approvals.Count -ne 1) { throw 'Approval is unavailable; refresh the task and check claim ownership.' }
$flowId = $approvals[0].id
$body = @{ variables = @{ reviewNote = 'Reviewed and approved' } } | ConvertTo-Json
$ack = Invoke-RestMethod "$api/api/user-tasks/$taskId/flows/$flowId" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
$ack | Select-Object userTaskId, instanceId, selectedFlowId, taskStatus, instanceStatus
```

Discovery returns an array of currently available sequence-flow definitions, including their required `variables`. Approval returns **200** with an acknowledgement. Discovery is advisory: another actor or event may change work before submission, so refresh after a conflict.

### 5. Verify completion, values, and history

```powershell
$detail = Invoke-RestMethod "$api/api/instances/$($instance.id)" -Headers $headers
if ($detail.status -ne 'completed') { throw 'The instance has not completed.' }
$detail.variables | Select-Object variableName, value, sourceFlowId, setBy
$detail.history | Format-Table
if (@($detail.variables | Where-Object { $_.variableName -eq 'reviewNote' -and $_.value -eq 'Reviewed and approved' }).Count -ne 1) {
  throw 'The approval note was not recorded.'
}
```

## Bash HTTP walkthrough

Run this in a third terminal at the repository root. It needs Bash, cURL, and `jq`. The helper prints HTTP failures and stops instead of feeding an error object into subsequent ID lookups.

### 1. Authenticate and create a definition

```bash
set -euo pipefail
api='http://127.0.0.1:15017'
read -r -s -p 'Paste the development JWT: ' token
printf '\n'
http() {
  local method="$1" path="$2" response status body
  shift 2
  response=$(curl --silent --show-error --write-out $'\n%{http_code}' \
    --request "$method" --header "Authorization: Bearer $token" \
    --header 'Content-Type: application/json' "$api$path" "$@")
  status="${response##*$'\n'}"
  body="${response%$'\n'*}"
  if [[ "$status" != 2?? ]]; then
    printf 'HTTP %s: %s\n' "$status" "$body" >&2
    return 1
  fi
  printf '%s' "$body"
}
body=$(jq -n --slurpfile definition examples/user-tasks/01-roles-claim-and-bypass.json \
  '{definition: $definition[0], publish: false}')
workflow=$(http POST /api/workflows --data-binary "$body")
workflow_id=$(jq -er '.id' <<<"$workflow")
printf 'Definition ID: %s\n' "$workflow_id"
```

### 2. Publish and start that exact version

```bash
http POST "/api/workflows/$workflow_id/publish"
body=$(jq -n --argjson workflowId "$workflow_id" \
  '{workflowId: $workflowId, startEventId: 1, variables: {requestReference: "REQ-DEMO-001"}}')
instance=$(http POST /api/instances --data-binary "$body")
instance_id=$(jq -er '.id' <<<"$instance")
printf 'Instance ID: %s\n' "$instance_id"
```

### 3. Find the inbox task and claim it

```bash
inbox=$(http GET "/api/instances/inbox?instanceId=$instance_id&page=1&pageSize=50")
task_id=$(jq -er --argjson instanceId "$instance_id" \
  '[.items[] | select(.instanceId == $instanceId)] | if length == 1 then .[0].userTaskId else error("Expected one visible task") end' <<<"$inbox")
claimed=$(http POST "/api/user-tasks/$task_id/claim")
jq . <<<"$claimed"
```

### 4. Discover and submit approval

```bash
flows=$(http GET "/api/user-tasks/$task_id/flows")
flow_id=$(jq -er '[.[] | select(.externalId == "FLOW_APPROVE_AFTER_CLAIM")] | if length == 1 then .[0].id else error("Approval unavailable") end' <<<"$flows")
ack=$(http POST "/api/user-tasks/$task_id/flows/$flow_id" \
  --data-binary '{"variables":{"reviewNote":"Reviewed and approved"}}')
jq '{userTaskId, instanceId, selectedFlowId, taskStatus, instanceStatus}' <<<"$ack"
```

### 5. Verify completion, values, and history

```bash
detail=$(http GET "/api/instances/$instance_id")
jq -e '.status == "completed"' <<<"$detail"
jq '.variables, .history' <<<"$detail"
jq -e '[.variables[] | select(.variableName == "reviewNote" and .value == "Reviewed and approved")] | length == 1' <<<"$detail"
```

## Inspect the result

The acknowledgement reports `taskStatus: "completed"`, `instanceStatus: "completed"`, and the selected flow. Instance detail shows the **Approved** end position and a normal `completion`. The runtime's IDs and timestamps depend on your database.

`variables` is an array of named value records, not a JSON object indexed by variable name. Find `requestReference = "REQ-DEMO-001"` and `reviewNote = "Reviewed and approved"`; the latter identifies the selected source flow and actor. `history` records transitions and actions; node execution activity is a separate lifecycle ledger covered in the [API reference](api-guide.md).

For a larger integration, refresh paged inbox results, respect server capabilities, and use task-addressed actions. See the [developer guide](developer-guide.md) for concurrent work, versioning, and retry contracts. The sample does not configure a business key or start idempotency; those are explicit features, not implied by `requestReference`.

## When to run the Worker

This approval uses synchronous transitions and persisted human work, so it needs only the API and PostgreSQL. Run `Flowbit.Worker` for timers, `asyncBefore`/`asyncAfter`, durable retry processing, conditional events with `deliveryMode: "durableAsync"`, administrative batches, and retention cleanup.

In another terminal, use the same database connection and a private operational listener:

PowerShell:

```powershell
$env:ConnectionStrings__Flowbit = 'Host=127.0.0.1;Port=55439;Database=flowbit_docs;Username=flowbit;Password=flowbit-docs-local-only'
$env:FlowbitWorker__HealthListenUrl = 'http://127.0.0.1:18081'
$workerRoot = (Resolve-Path ./Flowbit/src/Flowbit.Worker).Path
dotnet run --no-launch-profile --project ./Flowbit/src/Flowbit.Worker/Flowbit.Worker.csproj -- --contentRoot $workerRoot
```

Bash:

```bash
export ConnectionStrings__Flowbit='Host=127.0.0.1;Port=55439;Database=flowbit_docs;Username=flowbit;Password=flowbit-docs-local-only'
export FlowbitWorker__HealthListenUrl='http://127.0.0.1:18081'
dotnet run --no-launch-profile --project ./Flowbit/src/Flowbit.Worker/Flowbit.Worker.csproj -- --contentRoot "$PWD/Flowbit/src/Flowbit.Worker"
```

The explicit `--contentRoot` loads the Worker's own appsettings when launching from the repository root. The Worker does not apply migrations. Start it after the API's local migration has completed. `/health/live`, `/health/ready`, and `/metrics` are on **port 18081**, not the API. Readiness becomes successful after a durable-queue query succeeds and stays latched; it does not detect later database outages, certify every dependency, or prove that no incident exists.

For a next exercise, import [Intermediate Timer Delay](../examples/timers/02-intermediate-timer-delay.json), publish it, and start with no input values. Leave the Worker running: after the authored 30-second delay, the inbox offers **Timer Fired**; take its available action to complete. Publication of timer/durable definitions requires `WorkflowDurableProcessing:PublicationEnabled=true`, which the API's checked-in settings enable. This configuration gate does not check Worker availability. See [deployment](deployment.md) for the publication gate and operational contracts.

## Troubleshooting and cleanup

| Symptom | Check |
| --- | --- |
| API does not start | Confirm `pg_isready`, the explicit connection string, port availability, and migration logs. |
| `401` | Supply a nonexpired bearer token with matching issuer, audience, and signing key. Generate another through `/token` if expired. |
| Definition administration denied | Confirm `admin` is in the token on a fresh database; role settings may differ in an existing database. |
| Start or approval returns `400` | Read the endpoint's actual error body. The required strings must satisfy the example's minimum lengths; `{variables:{}}` does not supply them. |
| Empty inbox or unavailable action | Use `Requester` to start and `Reviewer` to see the task. Claim with the same username before approval, then rediscover flows. |
| `404` or `409` after reading a task | Work may be hidden, completed, claimed by another actor, or otherwise stale. Refresh state; do not blindly repeat the action. |
| Timer definition cannot publish | Check `WorkflowDurableProcessing:PublicationEnabled`; operators should verify a compatible Worker before enabling the gate. |

Error bodies differ by endpoint. The [API reference](api-guide.md) documents empty authentication/not-found responses, validation envelopes, and the distinct conflict contracts.

Stop the API, UI, and optional Worker with **Ctrl+C** in their terminals. To retain the tutorial database, run `docker stop flowbit-docs-local`; resume it with `docker start flowbit-docs-local`. To discard **only this disposable tutorial container and its data**, run:

```bash
docker rm -f -v flowbit-docs-local
```

Do not use this cleanup command against a business database container. Continue with [Developer guide](developer-guide.md), [API reference](api-guide.md), [BPMN support](bpmn-support.md), or [Deployment](deployment.md).
