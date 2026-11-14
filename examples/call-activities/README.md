# Call activity examples

A parent workflow calls a reusable child workflow and continues with its result. These examples need the API, PostgreSQL, and `Flowbit.Worker`, with durable publication enabled, because a Worker creates each child and delivers each result. All data is synthetic and nothing calls an external provider.

| File | What it demonstrates |
| --- | --- |
| [`01-manager-approval-child.json`](01-manager-approval-child.json) | The child: an unrestricted plain start with typed `amount` and `requestId` inputs, a `Manager` review, and a nullable process variable `approved` that the approval path sets. Rejecting ends in `errorEndEvent` `approval.rejected`. |
| [`02-purchase-request-call.json`](02-purchase-request-call.json) | A parent with no error boundary: `requestedAmount` and `requestId` go in, and `approved` comes back as `managerApproved`. A rejection faults the parent at the call. |
| [`03-purchase-request-call-failure.json`](03-purchase-request-call-failure.json) | The same call with a catch-all error boundary: a rejection records its failure in `callFailure` and routes to **Review Failed Approval**. |
| [`04-multi-instance-approval-call.json`](04-multi-instance-approval-call.json) | A multi-instance call: one child per amount in `lineAmounts`, two at a time, with `mi.item` mapped to `amount` and the child's `approved` collected in the item-local `lineApproved`. A catch-all boundary handles a rejection, and a non-interrupting 30-minute timer adds a reminder task while the body keeps running. |

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

## Multi-instance call

Example 04 starts with a required `requestId`, such as `REQ-2`; `lineAmounts` defaults to `[250, 1200, 75]`. When a Worker initializes the body, it pins the child's current published default for all three items, then starts two children; the third starts when one of them settles. Each child receives its line's amount and the shared `requestId`, so a `Manager` sees at most two **Review Request** tasks at a time.

| Path | Actions | Expected result |
| --- | --- | --- |
| Approve every line | As a `Manager`, take **Approve** on each child's task. Then, as `Purchasing`, take **Order Placed**. | `approvalResults` holds three `completed` rows in line order, each with `"variables": { "lineApproved": true }`, and the parent completes at **Ordered**. |
| Reject a line | As a `Manager`, take **Reject** on one child's task. Then, as `Purchasing`, take **Close**. | The child faults with `approval.rejected`. The body stops: unfinished children are cancelled through `callCancel` jobs and unstarted items never start. `approvalFailure` is `call_child_faulted`, `approvalResults` holds `completed`, `failed`, and `cancelled` rows, and the parent completes at **Closed Without Order**. |
| Wait 30 minutes | Leave a child's task open. | **Chase Approvers** appears for `Purchasing` while the body keeps running; it survives the body's later completion. |
| Change the default meanwhile | Publish and default a new child version while the body runs. | Items still start the pinned version, and the pinned version cannot be unpublished or deleted until the body settles. |

Instance detail shows the body under **Automatic multi-instance work** and each child under **Call activities**, with its item index and pinned version. Without the boundary, a rejection would fault the parent with `call_child_faulted`. See [multi-instance calls](../../docs/node-reference.md#multi-instance-calls) and [calling a workflow for every item](../../docs/developer-guide.md#call-a-workflow-for-every-item).

## Walkthrough

These commands import both definitions, start example 02, and list its call. For example 04, use its file name in place of `02-purchase-request-call.json`, start it with `requestId` only, and list every call instead of the first; the [variant](#multi-instance-variant) below shows the changed lines. `jq` and cURL are required for Bash. Change `$api`/`api` to your local API URL, and use a development JWT holding the `admin` role.

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

### Multi-instance variant

For example 04, import `01-manager-approval-child.json` and `04-multi-instance-approval-call.json` with the loop above, then replace the start and polling commands with these. They wait until the first two children exist and list every item's call.

PowerShell 7:

```powershell
$body = @{ workflowId = $workflow.id; startEventId = 1; variables = @{ requestId = 'REQ-2' } } | ConvertTo-Json
$instance = Invoke-RestMethod "$api/api/instances" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
$deadline = [DateTime]::UtcNow.AddMinutes(2)
do {
  Start-Sleep -Seconds 1
  $calls = @((Invoke-RestMethod "$api/api/instances/$($instance.id)/calls" -Headers $headers).items)
} while (@($calls | Where-Object childInstanceId).Count -lt 2 -and [DateTime]::UtcNow -lt $deadline)
$calls | Format-Table itemIndex, state, childInstanceId, pinnedChildDefinitionVersion
```

Bash:

```bash
body=$(jq -n --argjson id "$workflow_id" '{workflowId: $id, startEventId: 1, variables: {requestId: "REQ-2"}}')
instance_id=$(http POST /api/instances --header 'Content-Type: application/json' --data-binary "$body" | jq -er '.id')
deadline=$((SECONDS + 120))
while :; do
  sleep 1
  calls=$(http GET "/api/instances/$instance_id/calls")
  started=$(jq '[.items[] | select(.childInstanceId != null)] | length' <<<"$calls")
  [[ "$started" -ge 2 || "$SECONDS" -ge "$deadline" ]] && break
done
jq '.items[] | {itemIndex, state, childInstanceId, pinnedChildDefinitionVersion}' <<<"$calls"
```

Two calls are `running` with a child, and the third item has no call yet, because the body admits two at a time. Approve the children as a `Manager` to let the third start.

See the [node reference](../../docs/node-reference.md#callactivity), the [integration guide](../../docs/developer-guide.md#call-another-workflow), and [deployment](../../docs/deployment.md#call-activity-rollout).
