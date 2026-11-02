# Call activity examples

A parent workflow calls a reusable child workflow and continues with its result. These examples need the API, PostgreSQL, and `Flowbit.Worker`, with durable publication enabled, because a Worker creates each child and delivers each result. All data is synthetic and nothing calls an external provider.

| File | What it demonstrates |
| --- | --- |
| [`01-manager-approval-child.json`](01-manager-approval-child.json) | The child: an unrestricted plain start with typed `amount` and `requestId` inputs, a `Manager` review, and a nullable process variable `approved` that the approval path sets. Rejecting ends in `errorEndEvent` `approval.rejected`. |
| [`02-purchase-request-call.json`](02-purchase-request-call.json) | A parent with no error boundary: `requestedAmount` and `requestId` go in, and `approved` comes back as `managerApproved`. A rejection faults the parent at the call. |
| [`03-purchase-request-call-failure.json`](03-purchase-request-call-failure.json) | The same call with a catch-all error boundary: a rejection records its failure in `callFailure` and routes to **Review Failed Approval**. |

## Publish in this order

Publish the child first. Publishing a parent checks the call against the target's current published default, so a parent published first is refused, while drafts can be saved in either order. The parents reference the child's key `example-call-manager-approval`.

## Suggested inputs and actions

Start a parent with `requestedAmount` of `250` and any `requestId`. The start has no special role.

| Path | Actions | Expected result |
| --- | --- | --- |
| Approve | Open the child from the parent's **Call activities** section. As a `Manager`, take **Approve**. Then, as `Purchasing`, take **Order Placed** on the parent. | The child completes, a Worker delivers `managerApproved: true`, and the parent completes at **Ordered**. |
| Reject, example 02 | As a `Manager`, take **Reject**. | The child faults with `approval.rejected`. The parent faults at the call with code `call_child_faulted`. |
| Reject, example 03 | As a `Manager`, take **Reject**. Then, as `Purchasing`, take **Close**. | The child faults, the boundary writes `call_child_faulted: ...` to `callFailure`, and the parent completes at **Closed Without Order**. |
| Cancel the parent | Cancel the parent while the child's task is open. | The parent is `cancelled` at once and the child refuses actions with `409`. The child becomes `cancelled` when a Worker runs its `callCancel` job, and stays `running` until then. Cancellation does not undo external effects. |
| Worker stopped | Stop the Worker, start a parent, then start the Worker again. | The call stays **Queued** with no child, then the child starts. No work is lost or repeated. |

A call's version is chosen when the child is created, so publishing and defaulting a new child version affects only later visits. Before changing the default, check `GET /api/workflows/example-call-manager-approval/callers`, or use the confirmation on **Workflows**.

## Walkthrough

These commands import both definitions, start example 02, and list its call. `jq` and cURL are required for Bash. Change `$api`/`api` to your local API URL, and use a development JWT holding the `admin` role.

### PowerShell 7

```powershell
$api = 'http://127.0.0.1:15017'
$token = Read-Host 'Paste the development JWT' -MaskInput
$headers = @{ Authorization = "Bearer $token" }
foreach ($file in 'examples/call-activities/01-manager-approval-child.json', 'examples/call-activities/02-purchase-request-call.json') {
  $definition = Get-Content -Raw $file | ConvertFrom-Json
  $body = @{ definition = $definition; publish = $true } | ConvertTo-Json -Depth 100
  $workflow = Invoke-RestMethod "$api/api/workflows" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
}
$body = @{ workflowId = $workflow.id; startEventId = 1; variables = @{ requestedAmount = 250; requestId = 'REQ-1' } } | ConvertTo-Json
$instance = Invoke-RestMethod "$api/api/instances" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
$deadline = [DateTime]::UtcNow.AddMinutes(2)
do {
  $calls = Invoke-RestMethod "$api/api/instances/$($instance.id)/calls" -Headers $headers
  $call = @($calls.items)[0]
  if ($call.state -eq 'queued') { Start-Sleep -Seconds 1 }
} while ($call.state -eq 'queued' -and [DateTime]::UtcNow -lt $deadline)
$call | Format-List invocationId, state, childInstanceId, childDefinitionVersion
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
for file in examples/call-activities/01-manager-approval-child.json examples/call-activities/02-purchase-request-call.json; do
  body=$(jq -n --slurpfile definition "$file" '{definition: $definition[0], publish: true}')
  workflow=$(http POST /api/workflows --header 'Content-Type: application/json' --data-binary "$body")
done
workflow_id=$(jq -er '.id' <<<"$workflow")
body=$(jq -n --argjson id "$workflow_id" '{workflowId: $id, startEventId: 1, variables: {requestedAmount: 250, requestId: "REQ-1"}}')
instance=$(http POST /api/instances --header 'Content-Type: application/json' --data-binary "$body")
instance_id=$(jq -er '.id' <<<"$instance")
deadline=$((SECONDS + 120))
while :; do
  call=$(http GET "/api/instances/$instance_id/calls" | jq -e '.items[0]')
  state=$(jq -er '.state' <<<"$call")
  [[ "$state" != 'queued' || "$SECONDS" -ge "$deadline" ]] && break
  sleep 1
done
jq '{invocationId, state, childInstanceId, childDefinitionVersion}' <<<"$call"
```

See the [node reference](../../docs/node-reference.md#callactivity), the [integration guide](../../docs/developer-guide.md#call-another-workflow), and [deployment](../../docs/deployment.md#call-activity-rollout).
