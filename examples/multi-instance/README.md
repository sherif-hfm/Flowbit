# Multi-instance examples

Examples 01 to 07 are multi-instance **user tasks**: the numbered sequence isolates
each behavior, then combines it with aggregate routing, parent interruption, timers,
and durable jobs. All of them keep the required engine-only default outcome visible
in the diagram. Examples 08 to 12 are
[automatic multi-instance activities](#automatic-multi-instance-activities): REST
service tasks and script tasks that run once per collection item. The
[multi-instance call example](../call-activities/04-multi-instance-approval-call.json)
is with the call activity examples.

## Multi-instance user tasks

| Example | Start values | Actors and actions | Expected result | API, Worker, and configuration |
| --- | --- | --- | --- | --- |
| [`01-parallel-collection-assignees.json`](01-parallel-collection-assignees.json) | No input. Process defaults assign `alice`, `bob`, and `carol`. | Start as `ReviewCoordinator`. Each named user also needs `Reviewer` and takes flow `201` with `reviewComment`. | Three active, directly assigned items are created together. After all three complete, `parallelReviewResults` contains three item rows and the instance ends. | Published definition and API; no Worker or custom setting. |
| [`02-sequential-collection-assignees.json`](02-sequential-collection-assignees.json) | No input. Process defaults order `analyst`, `manager`, then `director`. | Start as `ReviewCoordinator`. Each named user also needs `Reviewer` and takes flow `201` with `stageComment` when their item becomes active. | Only one assigned item is active at a time, in collection order. `sequentialReviewResults` preserves item order and the instance ends after the director. | Published definition and API; no Worker or custom setting. |
| [`03-cardinality-one-per-actor.json`](03-cardinality-one-per-actor.json) | No input. `panelSize` defaults to `3`. | Start as `ReviewCoordinator`. Three distinct `Panelist` users take flow `201` with `score` and optional `comment`. | Three pooled items are completed by three distinct usernames. `panelReviewResults` records action values and action-time roles. | Published definition and API; no Worker. `Workflow.MultiInstance.MaxInstances` must be at least `3` (the default is sufficient). |
| [`04-early-quorum-and-priorities.json`](04-early-quorum-and-priorities.json) | No input. `quorumSize` defaults to `5`. | Start as `ApprovalCoordinator`. Distinct `Approver` users take `Approve` (`201`) or `Object` (`202`). `ApprovalManager` handles flow `401` when required. | Conditions run `afterEach`. Three approvals end early unless an objection already exists; then priority `1` routes to manual resolution ahead of priority `2`. Unfinished items are cancelled. If no condition wins after all items, fallback `203` is used. | Published definition and API; no Worker. Max instances must be at least `5`. |
| [`05-parent-interrupt.json`](05-parent-interrupt.json) | No input. Process defaults assign four reviewers. | Start as `ReviewCoordinator`. Named `Reviewer` users may take flow `201`. A `ReviewManager` may take parent flow `202` with `interruptReason`, without owning a child item. | Normal completion writes four item rows. A parent interrupt cancels unfinished items, appends one `parentInterrupt` result row, and rests at **Inspect Interrupt Results**. | Published definition and API; no Worker. Use the multi-instance execution endpoints for the parent action. |
| [`06-multi-instance-reminder.json`](06-multi-instance-reminder.json) | No input. Cardinality is fixed at `3`. | Complete each visible `Approve` action (`201`). The non-interrupting boundary timer requires no user action. | Aggregate conditions run `afterAll`; recurring timer branches increment `remindersSent` without completing the parent. All approvals route to the normal end. | API plus `Flowbit.Worker`. Durable job processing and timers must be enabled; the authored cycle is `R3/PT15S`. |
| [`07-role-management-active-and-pending.json`](07-role-management-active-and-pending.json) | No input. Defaults assign `analyst`, `manager`, and `director`, with `reviewRoles=["Reviewer"]` and `approvalRoles=["Approver"]`. | `ReviewCoordinator` starts; each assigned actor takes flow `201`. After analyst completion, `RoleManager` replaces task/action roles for unfinished work. | The completed item retains its evidence; active/pending items keep assignees and share the replacement policy. Identical stale retry is unchanged success; a different stale edit is `409`. | Published definition, API, database, and the role-policy migration; no Worker or external setting. |

## Shared contract

A multi-instance user task has one parent execution token and many child work
items. Every example declares a scalar `json` process variable initialized to
`[]` for the ordered result collection. Each child result has `kind: "item"`,
its selected flow and submitted values, the completing user, and that user's
normalized action-time roles. Cancelled or otherwise non-action rows have
`userRoles: null`; an authenticated actor with no roles has `userRoles: []`.

Every definition also has exactly one pure fallback with:

```json
{
  "isDefault": true,
  "isSelectable": false,
  "completionCondition": null,
  "completionPriority": null,
  "cancelRemainingInstances": false
}
```

That route is engine-only. It does not appear in available actions and cannot be
taken through a user action endpoint. It wins only after the configured
evaluation timing when no non-default condition matches.

Collection mode snapshots a declared `string[]` and assigns one child directly
to each username. It must use `requiresClaim=false` and `claimMode="fresh"`.
Parallel mode activates every item together; sequential mode activates only the
next index. Cardinality mode evaluates an NCalc count and creates pooled role-
eligible work. With `onePerActor=true`, one authenticated username may complete
at most one item; concurrent users can temporarily see the same representative,
so a stale loser receives `409` and should refresh.

## Completing child items

Use task-specific endpoints for multi-instance work:

1. Read the actor's inbox and select a child `taskId`.
2. Read `GET /api/user-tasks/{taskId}/flows`.
3. Take an authored selectable flow with
   `POST /api/user-tasks/{taskId}/flows/{flowId}` and a `variables` object.
4. Refresh the inbox or instance detail after each completion.

Instance-addressed actions are intentionally ambiguous when several child items
are active and return `409`.

## Early quorum and flow evidence

Example 04 uses `completionEvaluation: "afterEach"`, so aggregate conditions
are evaluated after every item action. Its conditions demonstrate:

- `CountFlow(id)` and `PercentFlow(id)`, scoped to this multi-instance execution.
- `[mi.total]`, `[mi.completed]`, and `[mi.remaining]`.
- `FlowInfo(id, 'actions.count')`, the instance-lifetime action summary, including
  evidence staged by the current action in the same transaction.
- Unique positive `completionPriority` values, where the lowest matching value
  wins.

For a visible priority test, submit at least one `Object` before the third
`Approve`. When the approval quorum arrives, flows `201` and `202` both match;
flow `202` has priority `1`, so the instance rests at **Resolve Mixed Quorum**.
Without a prior objection, flow `201` ends the instance as approved as soon as
the third approval is committed.

## Parent interrupt

The manager action in example 05 is a parent-level interrupt, not a child vote.
Discover and take it with:

```text
GET  /api/multi-instance-executions/{executionId}/flows
POST /api/multi-instance-executions/{executionId}/flows/202
```

Use this request body for the POST:

```json
{
  "variables": {
    "interruptReason": "Policy changed during review"
  }
}
```

The caller must satisfy both the node role and flow role (`ReviewManager`) but
does not need an assigned child item or claim. The locked parent advances only
once; later or concurrent losing attempts return `409`. The engine cancels all
unfinished children, writes the index-ordered child rows, and appends one final
`kind: "parentInterrupt"` row containing flow `202`, the manager, action-time
roles, timestamp, and `interruptReason`.

## Role management across active and pending items

Start `example-multi-instance-role-management` as `ReviewCoordinator`. The
process defaults provide all values. Each child is directly assigned; role
management cannot transfer that assignment or grant action authority to the
manager performing the edit.

1. Sign in as `analyst` with both `Reviewer` and `Approver`. Complete flow `201`
   on the assigned child with `{"variables":{}}`. `analyst` is now completed,
   `manager` is active, and `director` is pending.
2. As `RoleManager`, find the returned `multiInstanceExecutionId` in task
   management or instance detail, then read
   `GET /api/multi-instance-executions/{executionId}/roles`. Save its actual
   `rolePolicyId`; the sample `501` below is only a placeholder.
3. Replace the complete policy through
   `POST /api/multi-instance-executions/{executionId}/roles`:

```json
{
  "expectedRolePolicyId": 501,
  "roles": ["SeniorReviewer"],
  "flows": [{ "flowId": 201, "roles": ["SeniorApprover"] }],
  "reason": "Move unfinished review to the senior team"
}
```

The acknowledgement has `changed=true` and a new policy ID. Both unfinished
children receive that policy atomically. The completed analyst's action-time
roles and history, and all three assignees, stay unchanged. The `reviewRoles`
and `approvalRoles` source variables are unchanged too: the edit replaces the
captured policy, not its authored source.

Resend the **same body with the old policy ID**: this is unchanged success
(`changed=false`) and creates no additional role-change audit event. Resend
that stale ID with different task or action roles: the server returns `409`.
Refresh the policy before making another intended edit.

Give `manager` and `director` both `SeniorReviewer` and `SeniorApprover` in
their test identities. Complete the manager's flow `201`, then the director's.
The pending director activates under the replacement policy; the old
`Reviewer`/`Approver` combination alone no longer authorizes unfinished work.
Final `reviewResults` preserves all three items and their action-time roles.

For the UI path, sign in as `RoleManager`, open **Task management**, filter to
the running instance, and choose **Edit roles** on its MI work. Enter
`SeniorReviewer` under **Task roles** and `SeniorApprover` under **Complete
current review**, then **Save roles**. Reopen the policy to inspect it, then
use the assigned actors' inboxes to finish. See
[management UI](../../docs/ui-guide.md#manage-assignments-and-roles) and
[roles API](../../docs/api-guide.md#post-apimulti-instance-executionsexecutionidroles).

## Worker boundary

Examples 01 through 05 and 07 are synchronous and require only `Flowbit.Api`
and PostgreSQL. Example
06 authors `asyncBefore`, `asyncAfter`, and a recurring non-interrupting timer
boundary, so run `Flowbit.Worker` as well. Its 15-second local timings are for
demonstration; increase them before adapting the definition for production.
Examples 08 through 12 always need the Worker, which runs every item.

## Automatic multi-instance activities

A `serviceTask`, `scriptTask`, or `callActivity` with `multiInstance` runs its
activity once per element of a typed collection, as durable Worker jobs, and
publishes one index-ordered result. Items are never user tasks: nothing appears in
an inbox until the body completes. See the
[node reference](../../docs/node-reference.md#automatic-multi-instance-properties)
for the JSON and the
[developer guide](../../docs/developer-guide.md#process-a-collection-with-automatic-multi-instance)
for snapshots, idempotency, and recovery.

| Example | Start values | Actors and actions | Expected result | API, Worker, and configuration |
| --- | --- | --- | --- | --- |
| [`08-rest-parallel-notifications.json`](08-rest-parallel-notifications.json) | Required `batchId`, such as `batch-2026-11-14`. `supplierIds` defaults to four suppliers. Any authenticated actor can start it. | None until `ExampleReviewer` takes **Finish review** on **Review Notifications**. | Four `POST` requests, at most two at a time, each with `Idempotency-Key: flowbit-<job id>`. `notificationResults` holds four `completed` rows in supplier order, each with `receiptId` and `httpStatus`, whatever order the responses arrive in. | API, PostgreSQL, Worker, the [notification mock](#rest-mock-contracts), and `${config.exampleApiBaseUrl}` and `${config.exampleApiToken}` on the Worker. |
| [`09-rest-sequential-reservations.json`](09-rest-sequential-reservations.json) | Required `orderId`, such as `ORD-1001`. `orderLines` defaults to three `json` objects. | `ExampleReviewer` takes **Confirm** on **Confirm Reservations**. | One reservation request at a time, in line order; each sends its whole line object. `reservationResults` holds three rows with `reservationId`, `reservedQuantity`, and `warehouse`, which is `null` when the response omits it. | As example 08, with the [reservation mock](#rest-mock-contracts). |
| [`10-ncalc-script-per-item.json`](10-ncalc-script-per-item.json) | None. `amounts` defaults to `[10, 20, 30]` and `multiplier` to `2`. | None; the instance completes. | `calculationResults` rows have `scaledAmount` and `adjustedAmount` of `20`/`20`, `40`/`41`, and `60`/`62`. `maxConcurrency` is omitted, so `Workflow.MultiInstance.DefaultConcurrency` (default `4`) sets the window. | API, PostgreSQL, and Worker; no configuration. See the [walkthrough](#walkthrough-ncalc-body). |
| [`11-javascript-script-per-item.json`](11-javascript-script-per-item.json) | None. `orderLines` defaults to three `json` objects and `multiplier` to `3`. | None; the instance completes. | `lineResults` rows have `lineAmount` `30`, `21`, and `9`, and `lineSummary` such as `{ "line": 1, "lineCount": 3, "sku": "A", "amount": 30 }`. A line without numeric `quantity` and `unitPrice` fails with `script_task_failed` and, with no boundary, waits in an incident after its retries. | API, PostgreSQL, and Worker; no configuration. |
| [`12-rest-error-and-deadline-boundaries.json`](12-rest-error-and-deadline-boundaries.json) | None. `invoiceIds` defaults to three invoices. | `ExampleReviewer` confirms normal postings; `ExampleOperator` handles **Review Failed Posting** or **Investigate Slow Posting**. | All three succeed within 30 seconds: **Confirm Postings** with three rows. One returns a non-2xx status: the catch-all boundary stops the body at once, `postingFailure` is `service_task_failed`, and `postingResults` holds `completed`, `failed`, and `cancelled` rows. Responses held for more than 30 seconds: the interrupting timer cancels unfinished items, `postingResults` stays `[]`, and late responses are discarded. | As example 08, with the [posting mock](#rest-mock-contracts). |

### Shared contract of automatic bodies

- `multiInstance.collectionVariable` names a declared typed array. The body copies it
  once, when its Worker job initializes the body, so later changes do not affect the
  running body. An empty array completes at once with `[]`.
- Items read `mi.item`, zero-based `mi.index`, and `mi.total`, and the instance
  variables frozen with the collection. They write only the declared local
  `outputVariables` (or `readWrite` shared aliases), never ordinary instance variables.
- `completionEvaluation: "afterAll"` is required, and the body has exactly one
  unconditional outgoing flow: there are no votes, outcomes, or engine-only default.
- Every row of `resultVariable` has `kind: "item"`, `index`, the copied `item`,
  `status`, and the item's local `variables`:

```json
{ "kind": "item", "index": 0, "item": 10, "status": "completed", "variables": { "scaledAmount": 20, "adjustedAmount": 20 } }
```

- A service or script body requires `asyncBefore: true`. With no applicable error
  boundary, a failed item retries under the node's `job` policy (10 seconds, 1 minute,
  and 5 minutes by default) and then waits in an incident; the body admits no further
  items until an operator retries it. Items cannot be skipped, so model a catch-all
  boundary or an interrupting deadline, as example 12 does, when a batch must finish.

### REST mock contracts

These definitions contain no public URL or secret. Point
`${config.exampleApiBaseUrl}` at a mock you control, as described in the
[service-task guide](../service-tasks/README.md#required-trusted-configuration), and
configure both values on every Worker, which sends the requests. Every request carries
`Authorization: Bearer <token>`, `Content-Type: application/json`, and an
`Idempotency-Key` that stays the same for every retry of one item: deduplicate on it,
because a request can be sent again after a Worker restart.

| Example | Request | Successful response |
| --- | --- | --- |
| 08 | `POST /suppliers/supplier-a/notifications` with `{"batchId":"batch-2026-11-14","supplierId":"supplier-a","index":0,"total":4}` | Any 2xx with `{ "id": "notice-supplier-a" }`. The status is saved as the item's `httpStatus`. |
| 09 | `POST /orders/ORD-1001/reservations` with `{"orderId":"ORD-1001","lineNumber":0,"line":{"sku":"A-100","quantity":2}}` | 2xx with `{ "reservationId": "R-1", "quantity": 2, "warehouse": "north" }`. `warehouse` is optional; a `quantity` of `0` fails the item's local validation with `multi_instance_result_invalid`. |
| 12 | `POST /invoices/INV-1001/postings` with `{"invoiceId":"INV-1001"}` | 2xx with `{ "postingId": "P-INV-1001" }`. Return `422` for one invoice to take the error path, or hold every response for 35 seconds, under the 60-second request timeout, to take the deadline path. |

### Walkthrough: NCalc body

These commands import and publish example 10, start it, wait for the Worker to finish
the body, and print its progress and result. `jq` and cURL are required for Bash.
Change `$api`/`api` to your local API URL, and use a development JWT holding the
`admin` role. The API must allow durable publication and a Worker must be running.

#### PowerShell 7

```powershell
$api = 'http://127.0.0.1:15017'
$token = Read-Host 'Paste the development JWT' -MaskInput
$headers = @{ Authorization = "Bearer $token" }
$definition = Get-Content -Raw 'examples/multi-instance/10-ncalc-script-per-item.json' | ConvertFrom-Json
$body = @{ definition = $definition; publish = $true } | ConvertTo-Json -Depth 100
$workflow = Invoke-RestMethod "$api/api/workflows" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
$body = @{ workflowId = $workflow.id; startEventId = 1; variables = @{} } | ConvertTo-Json
$instance = Invoke-RestMethod "$api/api/instances" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
$deadline = [DateTime]::UtcNow.AddMinutes(2)
do {
  Start-Sleep -Seconds 1
  $detail = Invoke-RestMethod "$api/api/instances/$($instance.id)" -Headers $headers
} while ($detail.status -eq 'running' -and [DateTime]::UtcNow -lt $deadline)
$detail.automaticMultiInstances | Format-List executionId, status, totalCount, completedCount, effectiveConcurrency
$result = @($detail.variables | Where-Object variableName -eq 'calculationResults' | Sort-Object id)[-1]
$result.value | ConvertTo-Json -Depth 10
```

#### Bash

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
body=$(jq -n --slurpfile definition examples/multi-instance/10-ncalc-script-per-item.json '{definition: $definition[0], publish: true}')
workflow_id=$(http POST /api/workflows --header 'Content-Type: application/json' --data-binary "$body" | jq -er '.id')
body=$(jq -n --argjson id "$workflow_id" '{workflowId: $id, startEventId: 1, variables: {}}')
instance_id=$(http POST /api/instances --header 'Content-Type: application/json' --data-binary "$body" | jq -er '.id')
deadline=$((SECONDS + 120))
while :; do
  sleep 1
  detail=$(http GET "/api/instances/$instance_id")
  status=$(jq -er '.status' <<<"$detail")
  [[ "$status" != 'running' || "$SECONDS" -ge "$deadline" ]] && break
done
jq '.automaticMultiInstances[] | {executionId, status, totalCount, completedCount, effectiveConcurrency}' <<<"$detail"
jq '[.variables[] | select(.variableName == "calculationResults")] | max_by(.id) | .value' <<<"$detail"
```

The instance status is `completed`, the body reports `totalCount` and
`completedCount` of `3`, and the result has the three rows in the table above. To
see the items themselves, read
`GET /api/multi-instance-executions/{executionId}/items` with the printed execution
ID.
