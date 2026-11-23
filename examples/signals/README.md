# Signal workflow examples

A signal is a broadcast: one throw reaches every signal event that is waiting for
its name when the throw runs, in every instance of the chosen workflow family or
of all families, or only in the instance with one business key or ID. A wait
that starts later does not receive it, and nothing is stored for later. The
Worker delivers each recipient in its own transaction. Examples 01 and 02 throw
their signals through the Signals page or the API; examples 03 and 04 throw them
from a workflow, with a signal throw event and a signal end event. Example 05
starts a workflow with a signal: a throw to its family or to all workflows starts
one instance of each workflow whose published default starts on the name.
See [signals](../../docs/bpmn-support.md#signals) and the node reference for the
[catch](../../docs/node-reference.md#intermediatesignalcatchevent), the
[throw](../../docs/node-reference.md#intermediatesignalthrowevent), and the
[start](../../docs/node-reference.md#signalstartevent).

| Example | Start values | Actors and actions | Expected result | Runtime requirements |
| --- | --- | --- | --- | --- |
| [`01-signal-catch-policy-change.json`](01-signal-catch-policy-change.json) | Supply `claimNumber`, for example `CLM-1001`. | Throw `claims.policy-changed` with a `version` number and an optional `effectiveDate`; then complete **Recalculate under the new policy**. Alternatively let the one-hour timer expire and complete **Review under the current policy**. | The claim waits at **Wait for the new policy**. A delivered signal writes `policyVersion` and `effectiveDate` and continues to the recalculation task. The timer boundary cancels the wait, and its subscription, after an hour. | Published definition, API, and `Flowbit.Worker`. The thrower needs a role from `Signals.RequiredRole` (default `admin`) or a managed API client with the `signals.throw` scope. |
| [`02-signal-boundaries-recall.json`](02-signal-boundaries-recall.json) | Supply `orderNumber`, for example `ORD-2001`. It is the family's business key, so a second running order with the same number is refused. | Throw `warehouse.price-update` with a `unitPrice` number, as often as you like; complete each **Update the invoice**. Then throw `warehouse.product-recall` with an optional `reason`, or complete **Prepare the shipment** with **Ship**. | A price update adds a branch to **Update the invoice** with `unitPrice` set and leaves the shipment task waiting for the next one. A recall interrupts **Prepare the shipment**, ends both boundary waits, and opens **Stop the shipment** with `recallReason`. Shipping first ends both waits, so later signals reach nothing. | Published definition, API, and `Flowbit.Worker`. The thrower needs a role from `Signals.RequiredRole` (default `admin`) or a managed API client with the `signals.throw` scope. |
| [`03-signal-throw-order-cancellation.json`](03-signal-throw-order-cancellation.json) | Supply `orderNumber`, for example `ORD-3001`. | Any authenticated user completes **Review the order** with **Cancel the order**, entering the required `cancelReason`, or with **Keep the order**; then completes **Confirm the cancellation** or the open shipment tasks. No one throws a signal by hand. | The order splits into **Prepare the shipment**, which has an interrupting boundary for `orders.cancelled`, and **Review the order**. **Cancel the order** passes the throw event, which throws `orders.cancelled` to this instance with the payload `reason`, and opens **Confirm the cancellation** at once. The Worker then interrupts this order's shipment task and opens **Stop the shipment** with `stopReason`; other orders keep their shipment tasks. **Keep the order** ends the review and leaves the shipment task open. | Published definition, API, and `Flowbit.Worker`; durable publication enabled. No signal role is needed to take the action. |
| [`04-signal-end-recall-decision.json`](04-signal-end-recall-decision.json) | Supply `orderNumber`, the order of [Example 02](02-signal-boundaries-recall.json) to recall, for example `ORD-4001`. | Any authenticated user completes **Decide on the recall** with **Recall**, entering the required `recallReason`, or with **No recall**. | **Recall** ends the decision at a signal end event that throws `warehouse.product-recall` to the Example 02 instance whose business key is `orderNumber`, with the payload `reason`; the decision completes. The Worker interrupts that order's **Prepare the shipment** and opens **Stop the shipment** with `recallReason`. Other orders are not affected. **No recall** ends without a signal. | Example 02 imported under its own key, `example-signal-boundaries-recall`, and published before this one; API and `Flowbit.Worker`; durable publication enabled. |
| [`05-signal-start-recall-notice.json`](05-signal-start-recall-notice.json) | None: a signal starts it. Throw `warehouse.recall-announced` with `recallNumber` and `product.name`, and an optional `reason`. | The thrower needs a role from `Signals.RequiredRole` or a managed client with `signals.throw`. Any authenticated user completes **Write the customer notice** with **Send the notice**. | A throw to the family or to all workflows starts one instance, which writes `recallNumber`, `product`, and `reason` (default `Not stated`) and opens **Write the customer notice**. `recallNumber` is the business key with uniqueness `all`, so a second throw with the same number starts nothing and its delivery is skipped. A payload without `product` fails its delivery and starts nothing. A throw to one business key or one instance starts nothing. | Published as the family's default, with durable publication enabled; API and `Flowbit.Worker`. |

## Example 01: throw a policy change

1. Publish the definition and start two or three instances with different claim numbers.
   Each one waits at **Wait for the new policy**; instance detail lists the wait under
   **Waiting for signals**.
2. Throw the signal from **Manage → Signals** in Flowbit.Ui, or over HTTP. A bearer
   token for a user with the signal role:

   ```powershell
   $body = @{
     name = "claims.policy-changed"
     workflowKey = "example-signal-catch-policy-change"
     payload = @{ version = 7; effectiveDate = "2027-01-01" }
     idempotencyKey = "policy-7"
   } | ConvertTo-Json
   Invoke-RestMethod -Method Post -Uri "http://localhost:5017/api/signal-broadcasts" `
     -Headers @{ Authorization = "Bearer $env:FLOWBIT_TOKEN" } `
     -ContentType "application/json" -Body $body
   ```

   ```bash
   curl -s -X POST "http://localhost:5017/api/signal-broadcasts" \
     -H "Authorization: Bearer $FLOWBIT_TOKEN" \
     -H "Content-Type: application/json" \
     -d '{"name":"claims.policy-changed","workflowKey":"example-signal-catch-policy-change","payload":{"version":7,"effectiveDate":"2027-01-01"},"idempotencyKey":"policy-7"}'
   ```

   The response is `202 Accepted` with the broadcast and `totalDeliveryCount`, the
   number of waits it froze. Sending the same request again returns the same
   broadcast with `200`.
3. With the Worker running, follow the broadcast on the Signals page or at
   `GET /api/signal-broadcasts/{id}/deliveries`. Each delivery ends `delivered` with
   result `caught`, and each instance moves to **Recalculate under the new policy**
   with `policyVersion` set to 7.
4. Start another instance now: it waits again, because the earlier signal is not kept.

A payload without `version`, or with a version that is not a number, fails that
delivery with `signal_payload_invalid`; the instance keeps waiting, and **Retry failed**
on the Signals page delivers it again after the cause is fixed.

## Example 02: boundaries on a task

1. Publish the definition and start an instance with an `orderNumber`. Instance detail
   lists two waits under **Waiting for signals**: the interrupting
   `warehouse.product-recall` boundary and the non-interrupting
   `warehouse.price-update` boundary, both on **Prepare the shipment**.
2. Throw `warehouse.price-update` with a payload such as `{ "unitPrice": 12.5 }`, from
   **Manage → Signals** or over HTTP with the same bearer token as Example 01:

   ```powershell
   $body = @{
     name = "warehouse.price-update"
     workflowKey = "example-signal-boundaries-recall"
     payload = @{ unitPrice = 12.5 }
   } | ConvertTo-Json
   Invoke-RestMethod -Method Post -Uri "http://localhost:5017/api/signal-broadcasts" `
     -Headers @{ Authorization = "Bearer $env:FLOWBIT_TOKEN" } `
     -ContentType "application/json" -Body $body
   ```

   ```bash
   curl -s -X POST "http://localhost:5017/api/signal-broadcasts" \
     -H "Authorization: Bearer $FLOWBIT_TOKEN" \
     -H "Content-Type: application/json" \
     -d '{"name":"warehouse.price-update","workflowKey":"example-signal-boundaries-recall","payload":{"unitPrice":12.5}}'
   ```

   With the Worker running, the delivery ends with result `branched`: a new **Update the
   invoice** task appears, the shipment task stays open, and the price-update wait stays
   armed. Throw it again to add another branch.
3. Throw `warehouse.product-recall` with a reason:

   ```powershell
   $body = @{
     name = "warehouse.product-recall"
     workflowKey = "example-signal-boundaries-recall"
     payload = @{ reason = "Supplier recall" }
   } | ConvertTo-Json
   Invoke-RestMethod -Method Post -Uri "http://localhost:5017/api/signal-broadcasts" `
     -Headers @{ Authorization = "Bearer $env:FLOWBIT_TOKEN" } `
     -ContentType "application/json" -Body $body
   ```

   ```bash
   curl -s -X POST "http://localhost:5017/api/signal-broadcasts" \
     -H "Authorization: Bearer $FLOWBIT_TOKEN" \
     -H "Content-Type: application/json" \
     -d '{"name":"warehouse.product-recall","workflowKey":"example-signal-boundaries-recall","payload":{"reason":"Supplier recall"}}'
   ```

   The delivery ends with result `interrupted`: **Prepare the shipment** is cancelled,
   both waits end, and **Stop the shipment** opens with `recallReason` set. Open invoice
   tasks are separate branches and stay open.

4. Signal one order only. Start a second order, such as `ORD-2002`, and throw
   `warehouse.price-update` with the first order's business key:

   ```powershell
   $body = @{
     name = "warehouse.price-update"
     workflowKey = "example-signal-boundaries-recall"
     businessKey = "ORD-2001"
     payload = @{ unitPrice = 13 }
   } | ConvertTo-Json
   Invoke-RestMethod -Method Post -Uri "http://localhost:5017/api/signal-broadcasts" `
     -Headers @{ Authorization = "Bearer $env:FLOWBIT_TOKEN" } `
     -ContentType "application/json" -Body $body
   ```

   ```bash
   curl -s -X POST "http://localhost:5017/api/signal-broadcasts" \
     -H "Authorization: Bearer $FLOWBIT_TOKEN" \
     -H "Content-Type: application/json" \
     -d '{"name":"warehouse.price-update","workflowKey":"example-signal-boundaries-recall","businessKey":"ORD-2001","payload":{"unitPrice":13}}'
   ```

   The response shows `"businessKey": "ORD-2001"` and one recipient, and only
   `ORD-2001` gets a new **Update the invoice** task. To address an order by its
   instance ID instead, send `instanceId` alone, for example for instance 42:

   ```powershell
   $body = @{
     name = "warehouse.price-update"
     instanceId = 42
     payload = @{ unitPrice = 13 }
   } | ConvertTo-Json
   Invoke-RestMethod -Method Post -Uri "http://localhost:5017/api/signal-broadcasts" `
     -Headers @{ Authorization = "Bearer $env:FLOWBIT_TOKEN" } `
     -ContentType "application/json" -Body $body
   ```

   ```bash
   curl -s -X POST "http://localhost:5017/api/signal-broadcasts" \
     -H "Authorization: Bearer $FLOWBIT_TOKEN" \
     -H "Content-Type: application/json" \
     -d '{"name":"warehouse.price-update","instanceId":42,"payload":{"unitPrice":13}}'
   ```

   A business key that no running order has, or an order that has shipped,
   completes with no deliveries. On the Signals page, choose **One workflow
   family** with a **Business key**, or **One instance**.

The business key was added to this example with signal scopes. Business keys
cannot be turned off for a workflow key once enabled. If your database already
holds an earlier version of this example, the imported version starts only once
it is the published default, and from then on the earlier version without a
business key cannot start.

A price update whose `unitPrice` is missing, not a number, or not above zero fails with
`signal_payload_invalid`, and the shipment task keeps waiting. A non-interrupting
boundary that would push the instance past `Workflow.Gateway.MaxActiveTokens` active
tokens fails with `signal_token_limit` and stays armed.

## Example 03: cancel an order from its own review

The throw event `#9` states `{ "kind": "thisInstance" }`, so the signal reaches only
the waits of the order that throws it, and sends `cancelReason` as the payload member
`reason`. The shipment task's boundary has waited since the order started, in an
earlier transaction, so the throw always reaches it. See the
[throw event](../../docs/node-reference.md#intermediatesignalthrowevent).

1. Publish the definition with durable publication enabled, and start two orders, such
   as `ORD-3001` and `ORD-3002`. Each one has **Prepare the shipment** and **Review the
   order** open, and instance detail lists the `orders.cancelled` boundary under
   **Waiting for signals**.
2. In Flowbit.Ui, open **My work**, open **Review the order** of the first order, and
   choose **Cancel the order** with a reason such as `Customer request`. Or, instead
   of step 1 and this step, run the commands below with a bearer token for any user
   who may publish definitions, such as a development token with the `admin` role.
   They import and publish the definition, start both orders, cancel the first, and
   wait up to a minute for the Worker.

   ```powershell
   $api = 'http://localhost:5017'
   $headers = @{ Authorization = "Bearer $env:FLOWBIT_TOKEN" }
   $definition = Get-Content -Raw 'examples/signals/03-signal-throw-order-cancellation.json' | ConvertFrom-Json
   $body = @{ definition = $definition; publish = $true } | ConvertTo-Json -Depth 100
   $workflow = Invoke-RestMethod "$api/api/workflows" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
   $orders = foreach ($number in 'ORD-3001', 'ORD-3002') {
     $body = @{ workflowId = $workflow.id; startEventId = 1; variables = @{ orderNumber = $number } } | ConvertTo-Json
     Invoke-RestMethod "$api/api/instances" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
   }
   $review = @($orders[0].executionPositions | Where-Object nodeId -eq 8)[0]
   $body = @{ variables = @{ cancelReason = 'Customer request' } } | ConvertTo-Json
   Invoke-RestMethod "$api/api/user-tasks/$($review.userTaskId)/flows/801" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
   $deadline = [DateTime]::UtcNow.AddMinutes(1)
   do {
     Start-Sleep -Seconds 1
     $detail = Invoke-RestMethod "$api/api/instances/$($orders[0].id)" -Headers $headers
     $stopped = @($detail.executionPositions | Where-Object { $_.nodeId -eq 6 -and $_.tokenStatus -eq 'active' })
   } while ($stopped.Count -eq 0 -and [DateTime]::UtcNow -lt $deadline)
   $detail.executionPositions | Format-Table nodeId, nodeName, tokenStatus
   @($detail.history | Where-Object note -eq 'signalThrown')[0].payload | ConvertTo-Json
   (Invoke-RestMethod "$api/api/instances/$($orders[1].id)" -Headers $headers).executionPositions | Format-Table nodeId, nodeName, tokenStatus
   ```

   ```bash
   set -euo pipefail
   api='http://localhost:5017'
   http() {
     local method="$1" path="$2"
     shift 2
     curl --fail-with-body --silent --show-error --request "$method" \
       --header "Authorization: Bearer $FLOWBIT_TOKEN" "$api$path" "$@"
   }
   body=$(jq -n --slurpfile definition examples/signals/03-signal-throw-order-cancellation.json '{definition: $definition[0], publish: true}')
   workflow_id=$(http POST /api/workflows --header 'Content-Type: application/json' --data-binary "$body" | jq -er '.id')
   ids=()
   reviews=()
   for number in ORD-3001 ORD-3002; do
     body=$(jq -n --argjson id "$workflow_id" --arg number "$number" '{workflowId: $id, startEventId: 1, variables: {orderNumber: $number}}')
     started=$(http POST /api/instances --header 'Content-Type: application/json' --data-binary "$body")
     ids+=("$(jq -er '.id' <<<"$started")")
     reviews+=("$(jq -er '[.executionPositions[] | select(.nodeId == 8)][0].userTaskId' <<<"$started")")
   done
   http POST "/api/user-tasks/${reviews[0]}/flows/801" --header 'Content-Type: application/json' \
     --data-binary '{"variables":{"cancelReason":"Customer request"}}' >/dev/null
   deadline=$((SECONDS + 60))
   while :; do
     sleep 1
     detail=$(http GET "/api/instances/${ids[0]}")
     stopped=$(jq '[.executionPositions[] | select(.nodeId == 6 and .tokenStatus == "active")] | length' <<<"$detail")
     [[ "$stopped" -ge 1 || "$SECONDS" -ge "$deadline" ]] && break
   done
   jq '.executionPositions[] | {nodeId, nodeName, tokenStatus}' <<<"$detail"
   jq '[.history[] | select(.note == "signalThrown")][0].payload' <<<"$detail"
   http GET "/api/instances/${ids[1]}" | jq '.executionPositions[] | {nodeId, nodeName, tokenStatus}'
   ```

3. The action returns at once and **Confirm the cancellation** opens in the same step;
   the throw never waits for its delivery. The `signalThrown` history row has
   `scopeKind` `thisInstance` and `recipientCount` 1, and instance detail shows it as
   *Threw signal 'orders.cancelled' to this instance: 1 recipient* with a link to the
   broadcast. When the Worker has delivered it, **Prepare the shipment** is cancelled,
   **Stop the shipment** is active, and `stopReason` holds `Customer request`. The
   second order still has **Prepare the shipment** and **Review the order** open.
4. On **Manage → Signals**, set **Thrown by** to **Workflows**: the broadcast is listed
   with `Instance #<id> · Announce the cancellation` as its thrower, linked to the first
   order. Under **People and API clients**, the default, it is not listed. Over HTTP,
   `GET /api/signal-broadcasts?thrownByKind=instance` lists it.
5. Complete **Confirm the cancellation** and **Stop the shipment**; the order completes.
   Choosing **Keep the order** on the second order ends its review and leaves its
   shipment task open, to be completed with **Ship**.

With the Worker stopped, the cancellation still succeeds and the broadcast stays
**Delivering**; the shipment task is interrupted when a Worker returns. Each broadcast
that a workflow threw counts toward `Signals.MaxOpenWorkflowBroadcasts` until it is
delivered, and a cancellation beyond that budget is refused with
`Signal throw event #9 was not thrown: ... signal broadcasts thrown by workflows are still delivering; see Signals.MaxOpenWorkflowBroadcasts.`;
the action changes nothing and can be repeated once broadcasts have finished.

## Example 04: end a recall decision with a signal

The signal end event `#3` throws `warehouse.product-recall` with the scope
`{ "kind": "businessKey", "workflowKey": "example-signal-boundaries-recall", "businessKeyVariable": "orderNumber" }`,
so it reaches only the [Example 02](02-signal-boundaries-recall.json) order whose
business key is the decision's `orderNumber`, and sends `recallReason` as the payload
member `reason`, which Example 02 maps into its own `recallReason`. See the
[signal end event](../../docs/node-reference.md#signalendevent).

The scope names Example 02 by its literal key, so import Example 02 under that key,
which is the `id` in its file, and publish it before this example: publishing a
definition whose scope names a family without a definition is refused with
`Signal end event #3 throws to workflow 'example-signal-boundaries-recall', which has no definition.`
If you imported Example 02 under another key, change `scope.workflowKey` in this file
to that key before importing it.

1. Publish Example 02, then this example, with durable publication enabled. Start an
   order of Example 02 with `orderNumber` `ORD-4001`, then a recall decision of this
   example with the same `orderNumber`.
2. In Flowbit.Ui, open **Decide on the recall** and choose **Recall** with a reason such
   as `Supplier recall`. Or, instead of step 1 and this step, run these commands with
   the same token as Example 03. They import and publish both examples, start the order
   and the decision, and choose **Recall**. Example 02 keeps each business key for one
   running order, so after step 1 they would stop with `409 business_key_conflict`;
   use another order number, or complete or cancel the first order, to run them again.

   ```powershell
   $api = 'http://localhost:5017'
   $headers = @{ Authorization = "Bearer $env:FLOWBIT_TOKEN" }
   $workflows = foreach ($file in 'examples/signals/02-signal-boundaries-recall.json', 'examples/signals/04-signal-end-recall-decision.json') {
     $definition = Get-Content -Raw $file | ConvertFrom-Json
     $body = @{ definition = $definition; publish = $true } | ConvertTo-Json -Depth 100
     Invoke-RestMethod "$api/api/workflows" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
   }
   $body = @{ workflowId = $workflows[0].id; startEventId = 1; variables = @{ orderNumber = 'ORD-4001' } } | ConvertTo-Json
   $order = Invoke-RestMethod "$api/api/instances" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
   $body = @{ workflowId = $workflows[1].id; startEventId = 1; variables = @{ orderNumber = 'ORD-4001' } } | ConvertTo-Json
   $decision = Invoke-RestMethod "$api/api/instances" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
   $task = @($decision.executionPositions | Where-Object nodeId -eq 2)[0]
   $body = @{ variables = @{ recallReason = 'Supplier recall' } } | ConvertTo-Json
   Invoke-RestMethod "$api/api/user-tasks/$($task.userTaskId)/flows/201" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
   $deadline = [DateTime]::UtcNow.AddMinutes(1)
   do {
     Start-Sleep -Seconds 1
     $detail = Invoke-RestMethod "$api/api/instances/$($order.id)" -Headers $headers
     $stopped = @($detail.executionPositions | Where-Object { $_.nodeId -eq 6 -and $_.tokenStatus -eq 'active' })
   } while ($stopped.Count -eq 0 -and [DateTime]::UtcNow -lt $deadline)
   $detail.executionPositions | Format-Table nodeId, nodeName, tokenStatus
   $ended = Invoke-RestMethod "$api/api/instances/$($decision.id)" -Headers $headers
   $ended.status
   @($ended.history | Where-Object note -eq 'signalThrown')[0].payload | ConvertTo-Json
   ```

   ```bash
   set -euo pipefail
   api='http://localhost:5017'
   http() {
     local method="$1" path="$2"
     shift 2
     curl --fail-with-body --silent --show-error --request "$method" \
       --header "Authorization: Bearer $FLOWBIT_TOKEN" "$api$path" "$@"
   }
   workflow_ids=()
   for file in examples/signals/02-signal-boundaries-recall.json examples/signals/04-signal-end-recall-decision.json; do
     body=$(jq -n --slurpfile definition "$file" '{definition: $definition[0], publish: true}')
     workflow_ids+=("$(http POST /api/workflows --header 'Content-Type: application/json' --data-binary "$body" | jq -er '.id')")
   done
   body=$(jq -n --argjson id "${workflow_ids[0]}" '{workflowId: $id, startEventId: 1, variables: {orderNumber: "ORD-4001"}}')
   order_id=$(http POST /api/instances --header 'Content-Type: application/json' --data-binary "$body" | jq -er '.id')
   body=$(jq -n --argjson id "${workflow_ids[1]}" '{workflowId: $id, startEventId: 1, variables: {orderNumber: "ORD-4001"}}')
   decision=$(http POST /api/instances --header 'Content-Type: application/json' --data-binary "$body")
   decision_id=$(jq -er '.id' <<<"$decision")
   task_id=$(jq -er '[.executionPositions[] | select(.nodeId == 2)][0].userTaskId' <<<"$decision")
   http POST "/api/user-tasks/$task_id/flows/201" --header 'Content-Type: application/json' \
     --data-binary '{"variables":{"recallReason":"Supplier recall"}}' >/dev/null
   deadline=$((SECONDS + 60))
   while :; do
     sleep 1
     detail=$(http GET "/api/instances/$order_id")
     stopped=$(jq '[.executionPositions[] | select(.nodeId == 6 and .tokenStatus == "active")] | length' <<<"$detail")
     [[ "$stopped" -ge 1 || "$SECONDS" -ge "$deadline" ]] && break
   done
   jq '.executionPositions[] | {nodeId, nodeName, tokenStatus}' <<<"$detail"
   http GET "/api/instances/$decision_id" | jq '{status, thrown: ([.history[] | select(.note == "signalThrown")][0].payload)}'
   ```

3. The decision is `completed`. Its `signalThrown` row, from the end event to itself,
   has `eventKind` `end`, `scopeKind` `businessKey`, `workflowKey`
   `example-signal-boundaries-recall`, and `recipientCount` 1: of the order's two
   armed boundaries, only the recall boundary waits for `warehouse.product-recall`.
   The row holds no business key. When the Worker has delivered the signal, the
   order's **Prepare the shipment** is cancelled, **Stop the shipment** is active, and
   `recallReason` holds `Supplier recall`.
4. A decision for an order number that no running Example 02 order has, or for an
   order that has already shipped, completes with `recipientCount` 0 and changes
   nothing. **No recall** ends at a plain end event and throws nothing.

## Example 05: start a recall notice with a signal

The signal start event `#1` starts on `warehouse.recall-announced`. Its mappings
declare the start variables `recallNumber` and `product`, both required, and
`reason`, with the default `Not stated`, and `recallNumber` is the business key
with uniqueness `all`. See the [signal start event](../../docs/node-reference.md#signalstartevent).

1. Import and publish the example with durable publication enabled. Its rows on
   **Manage → Signals** then list `example-signal-start-recall-notice` with 1 start
   for `warehouse.recall-announced`.
2. Throw the signal to the family, from the Signals page or over HTTP with the token
   of Example 01. The review reads **1 new instance**. When the Worker has delivered
   it, the delivery reads **Started an instance** and links the instance, whose history
   starts with **Started by signal**, and **Write the customer notice** is open.

   ```powershell
   $api = 'http://localhost:5017'
   $headers = @{ Authorization = "Bearer $env:FLOWBIT_TOKEN" }
   $definition = Get-Content -Raw 'examples/signals/05-signal-start-recall-notice.json' | ConvertFrom-Json
   $body = @{ definition = $definition; publish = $true } | ConvertTo-Json -Depth 100
   Invoke-RestMethod "$api/api/workflows" -Method Post -Headers $headers -ContentType 'application/json' -Body $body | Out-Null
   function Send-Recall($payload, $scope) {
     $body = (@{ name = 'warehouse.recall-announced'; payload = $payload } + $scope) | ConvertTo-Json -Depth 10
     $broadcast = Invoke-RestMethod "$api/api/signal-broadcasts" -Method Post -Headers $headers -ContentType 'application/json' -Body $body
     $deadline = [DateTime]::UtcNow.AddMinutes(1)
     do {
       Start-Sleep -Seconds 1
       $broadcast = Invoke-RestMethod "$api/api/signal-broadcasts/$($broadcast.id)" -Headers $headers
     } while ($broadcast.status -eq 'delivering' -and [DateTime]::UtcNow -lt $deadline)
     (Invoke-RestMethod "$api/api/signal-broadcasts/$($broadcast.id)/deliveries" -Headers $headers).items |
       Select-Object kind, status, resultCode, instanceId, resultDescription
   }
   $family = @{ workflowKey = 'example-signal-start-recall-notice' }
   Send-Recall @{ recallNumber = 'R-1001'; product = @{ name = 'Kettle' } } $family
   Send-Recall @{ recallNumber = 'R-1002'; product = @{ name = 'Toaster' }; reason = 'Overheating' } @{ allWorkflows = $true }
   Send-Recall @{ recallNumber = 'R-1001'; product = @{ name = 'Kettle' } } $family
   Send-Recall @{ recallNumber = 'R-1003' } $family
   ```

   ```bash
   set -euo pipefail
   api='http://localhost:5017'
   http() {
     local method="$1" path="$2"
     shift 2
     curl --fail-with-body --silent --show-error --request "$method"        --header "Authorization: Bearer $FLOWBIT_TOKEN" "$api$path" "$@"
   }
   body=$(jq -n --slurpfile definition examples/signals/05-signal-start-recall-notice.json '{definition: $definition[0], publish: true}')
   http POST /api/workflows --header 'Content-Type: application/json' --data-binary "$body" >/dev/null
   send_recall() {
     local payload="$1" scope="$2" id status
     body=$(jq -n --argjson payload "$payload" --argjson scope "$scope" '{name: "warehouse.recall-announced", payload: $payload} + $scope')
     id=$(http POST /api/signal-broadcasts --header 'Content-Type: application/json' --data-binary "$body" | jq -er '.id')
     deadline=$((SECONDS + 60))
     while :; do
       sleep 1
       status=$(http GET "/api/signal-broadcasts/$id" | jq -r '.status')
       [[ "$status" != "delivering" || "$SECONDS" -ge "$deadline" ]] && break
     done
     http GET "/api/signal-broadcasts/$id/deliveries" | jq '.items[] | {kind, status, resultCode, instanceId, resultDescription}'
   }
   family='{"workflowKey":"example-signal-start-recall-notice"}'
   send_recall '{"recallNumber":"R-1001","product":{"name":"Kettle"}}' "$family"
   send_recall '{"recallNumber":"R-1002","product":{"name":"Toaster"},"reason":"Overheating"}' '{"allWorkflows":true}'
   send_recall '{"recallNumber":"R-1001","product":{"name":"Kettle"}}' "$family"
   send_recall '{"recallNumber":"R-1003"}' "$family"
   ```

3. The four broadcasts end like this:
   - `R-1001` to the family: one `start` delivery, `delivered` with `started` and an
     `instanceId`. The instance has `reason` `Not stated`.
   - `R-1002` to all workflows: one more instance, with `reason` `Overheating`,
     besides any waits for the name in other workflows.
   - `R-1001` again: `skipped` with `business_key_conflict` and the description
     `Instance #<id> already has this start's business key.`; no instance starts.
   - `R-1003` without `product`: `failed` with `signal_payload_invalid`; no instance
     starts, and the broadcast is completed with issues.
4. A throw to one business key or one instance of this family starts nothing. Set
   another version without the signal start as the default, and the family stops
   listening at once: a start that was frozen but not yet delivered is then skipped
   with `subscription_ended`.
