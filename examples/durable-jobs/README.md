# Durable job examples

These examples require the API, PostgreSQL, and `Flowbit.Worker`, with durable
publication enabled. Import and publish them as described in
[getting started](../../docs/getting-started.md). Short retry delays are local
demonstration values; scheduling and Worker load may delay an attempt.

| Example | Inputs and actors | Observable behavior |
| --- | --- | --- |
| [Async before and after](01-async-before-after.json) | No input; complete **Inspect Result** after Worker processing. | Async-before executes the script once; async-after persists completion before routing onward. |
| [Retries and incident recovery](02-retries-and-incident-recovery.json) | `ExampleRequester` starts with `scenario` set to `fail-once` or `recover`; `ExampleOperator` inspects the recovered result. A job operator (default `admin`) inspects attempts and retries incidents. | `retryFirst` uses `PT2S`, `PT2S`: one initial attempt plus two automatic retries. Fail-once succeeds on the second call; persistent failure opens an incident after three attempts. Correcting the mock and manually retrying the incident resumes the same job. |
| [Error-boundary precedence](03-error-boundary-precedence.json) | No inputs; `ExampleRequester` starts and `ExampleOperator` inspects both error values. | Two async JavaScript tasks throw deliberately. `boundaryFirst` catches on the first attempt; `retryFirst` catches after the initial attempt and two retries. Both failures are handled, so neither creates an incident. |

## Controlled REST mock contract

Before running example 02, start a local service you control and supply matching
trusted context values to **both API and Worker**:

| Configuration environment name | Value |
| --- | --- |
| `WorkflowContext__Config__exampleApiBaseUrl` | The mock base URL, for example `http://127.0.0.1:5099`, without a trailing slash. |
| `WorkflowContext__Config__exampleApiToken` | A local test credential kept outside the workflow JSON. |

The REST task sends:

```http
POST /retry
Content-Type: application/json
X-Example-Credential: <configured local credential>
X-Flowbit-Job-Id: <stable sys.jobId>
X-Flowbit-Job-Attempt: <sys.jobAttempt>

{"scenario":"recover"}
```

Implement two modes, isolated by job ID:

- `fail-once`: return `503` for the first call, then `200` with `{"ok":true}`.
- `recover`: return `503` until the operator explicitly enables success for that
  job; then return `200` with `{"ok":true}`.

Reject an invalid credential and record the job ID and attempt header for each
accepted call. The success mapping writes process variable `recovered=true`.
The acceptance fixture owns an implementation on an ephemeral loopback port;
its control method releases a single recovery job. That helper is test-only,
not a Flowbit HTTP endpoint. For a manual walkthrough, use your controlled
mock's configuration interface to enable success at the indicated step.

Keep actual side effects idempotent by stable job ID. Attempts can repeat after
lease loss, so the attempt header is diagnostic evidence, not an exactly-once
guarantee. See [Worker operation](../../docs/deployment.md#worker-operation).

## Example 02: retries and recovery through HTTP

Run from the repository root after starting the configured hosts and mock.
Use a development JWT with `admin`, `ExampleRequester`, and `ExampleOperator`
for this isolated walkthrough. `admin` is the default workflow/job operator
role; use your deployment's configured operator roles if different. The script
imports and publishes the definition, starts `recover`, waits up to two minutes
for its incident, and prints the three failed attempts. `jq` and cURL are
required for Bash. Change `$api`/`api` to your local API URL.

### PowerShell 7

```powershell
$api = 'http://127.0.0.1:15017'
$token = Read-Host 'Paste the development JWT' -MaskInput
$headers = @{ Authorization = "Bearer $token" }
$definition = Get-Content -Raw 'examples/durable-jobs/02-retries-and-incident-recovery.json' | ConvertFrom-Json
$body = @{ definition = $definition; publish = $false } | ConvertTo-Json -Depth 100
$workflow = Invoke-RestMethod "$api/api/workflows" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
Invoke-RestMethod "$api/api/workflows/$($workflow.id)/publish" -Method Post -Headers $headers
$body = @{ workflowId = $workflow.id; startEventId = 1; variables = @{ scenario = 'recover' } } | ConvertTo-Json
$instance = Invoke-RestMethod "$api/api/instances" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
$deadline = [DateTime]::UtcNow.AddMinutes(2)
do {
  $page = Invoke-RestMethod "$api/api/incidents?instanceId=$($instance.id)&status=open&pageSize=50" -Headers $headers
  $incidents = @($page.items)
  if ($incidents.Count -eq 0) { Start-Sleep -Seconds 1 }
} while ($incidents.Count -eq 0 -and [DateTime]::UtcNow -lt $deadline)
if ($incidents.Count -ne 1) { throw 'Expected one open incident; inspect Worker logs and job state.' }
$incident = $incidents[0]
$attempts = Invoke-RestMethod "$api/api/jobs/$($incident.jobId)/attempts?pageSize=50" -Headers $headers
$attempts.items | Format-Table attemptNumber, status, failureCode
if (@($attempts.items).Count -ne 3) { throw 'Expected three attempts before recovery.' }
```

Enable success in the mock for `$incident.jobId`, then continue in the same
session. This POST deliberately has **no body**:

```powershell
$retry = Invoke-RestMethod "$api/api/incidents/$($incident.id)/retry" -Method Post -Headers $headers
$retry | Format-List incidentId, jobId, incidentStatus, jobStatus
$deadline = [DateTime]::UtcNow.AddMinutes(2)
do {
  $job = Invoke-RestMethod "$api/api/jobs/$($incident.jobId)" -Headers $headers
  if ($job.summary.status -ne 'completed') { Start-Sleep -Seconds 1 }
} while ($job.summary.status -ne 'completed' -and [DateTime]::UtcNow -lt $deadline)
if ($job.summary.status -ne 'completed') { throw 'Recovery has not completed; inspect the job and Worker.' }
$detail = Invoke-RestMethod "$api/api/instances/$($instance.id)" -Headers $headers
$detail.variables | Where-Object variableName -EQ 'recovered' | Sort-Object id -Descending | Select-Object -First 1 variableName, value | Format-List
$attempts = Invoke-RestMethod "$api/api/jobs/$($incident.jobId)/attempts?pageSize=50" -Headers $headers
$attempts.items | Format-Table attemptNumber, status, failureCode
```

### Bash

```bash
set -euo pipefail
api='http://127.0.0.1:15017'
read -r -s -p 'Paste the development JWT: ' token
printf '\n'
http() {
  local method="$1" path="$2"
  shift 2
  curl --fail-with-body --silent --show-error --request "$method" \
    --header "Authorization: Bearer $token" "$api$path" "$@"
}
body=$(jq -n --slurpfile definition examples/durable-jobs/02-retries-and-incident-recovery.json \
  '{definition: $definition[0], publish: false}')
workflow=$(http POST /api/workflows --header 'Content-Type: application/json' --data-binary "$body")
workflow_id=$(jq -er '.id' <<<"$workflow")
http POST "/api/workflows/$workflow_id/publish"
body=$(jq -n --argjson id "$workflow_id" '{workflowId: $id, startEventId: 1, variables: {scenario: "recover"}}')
instance=$(http POST /api/instances --header 'Content-Type: application/json' --data-binary "$body")
instance_id=$(jq -er '.id' <<<"$instance")
deadline=$((SECONDS + 120))
while :; do
  incidents=$(http GET "/api/incidents?instanceId=$instance_id&status=open&pageSize=50")
  count=$(jq -er '.items | length' <<<"$incidents")
  [[ "$count" -gt 0 || "$SECONDS" -ge "$deadline" ]] && break
  sleep 1
done
[[ "$count" -eq 1 ]] || { printf 'Expected one open incident; inspect Worker logs and job state.\n' >&2; exit 1; }
incident_id=$(jq -er '.items[0].id' <<<"$incidents")
job_id=$(jq -er '.items[0].jobId' <<<"$incidents")
attempts=$(http GET "/api/jobs/$job_id/attempts?pageSize=50")
jq '.items[] | {attemptNumber, status, failureCode}' <<<"$attempts"
[[ $(jq '.items | length' <<<"$attempts") -eq 3 ]]
```

Enable success in the mock for `$job_id`, then continue in the same session:

```bash
http POST "/api/incidents/$incident_id/retry"
deadline=$((SECONDS + 120))
while :; do
  job=$(http GET "/api/jobs/$job_id")
  status=$(jq -er '.summary.status' <<<"$job")
  [[ "$status" == completed || "$SECONDS" -ge "$deadline" ]] && break
  sleep 1
done
[[ "$status" == completed ]] || { printf 'Recovery has not completed; inspect the job and Worker.\n' >&2; exit 1; }
http GET "/api/instances/$instance_id" | jq '[.variables[] | select(.variableName == "recovered")] | max_by(.id) | {variableName, value}'
http GET "/api/jobs/$job_id/attempts?pageSize=50" | jq '.items'
```

The retry response acknowledges queueing and incident resolution; it does not
mean execution has completed. The variable query selects the greatest history
ID because instance detail includes earlier values as well. After the Worker
succeeds, **Inspect Recovered Result**
is available and `recovered` is true. Complete flow `301` as `ExampleOperator`.
For automatic recovery, start another instance of the published version with
`scenario: "fail-once"`: it reaches the inspection task after two calls, with
no open incident. Compare mock headers with the job's attempts endpoint.

## Example 03: error-boundary precedence

Start the published definition, let the Worker execute both throwing scripts,
and inspect `boundaryFirstError` and `retryFirstError` at **Inspect Both Errors**. Query
`GET /api/jobs?instanceId={instanceId}` and each job's `/attempts` endpoint:
the first host has one attempt, the second has three, and the instance has no
open incidents. The boundary continuation routes to the next task in each case;
the hosts' normal-success routes are not followed. Complete the inspection
action `601` after reviewing that evidence.

## Operations UI and acceptance

On **Jobs & incidents**, filter by incident/job status and locate the row by its
workflow and instance ID. Inspect detailed attempts through the HTTP endpoint
shown above; the operations list does not provide an instance filter or a job
detail screen. Correct the mock before selecting the incident's **Retry**
action. Refresh until the job completes, then inspect the instance's
variables and finish its review task. See [operations UI](../../docs/ui-guide.md#monitor-jobs-and-incidents)
and the [jobs/incident API](../../docs/api-guide.md#jobs-and-incidents).

The [local acceptance suite](../../Flowbit/tests/Flowbit.BrowserAcceptanceTests/README.md)
uses these canonical JSON files and real API/Worker attempts. Its test results
and captured diagnostics establish a particular run's outcome; the existence
of an example or screenshot alone does not establish runtime verification.
