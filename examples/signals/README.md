# Signal workflow examples

A signal is a broadcast: one throw reaches every signal event that is waiting for
its name when the throw runs, in every instance of the chosen workflow family or
of all families. A wait that starts later does not receive it, and nothing is
stored for later. The Worker delivers each recipient in its own transaction.
See [signals](../../docs/bpmn-support.md#signals) and the
[node reference](../../docs/node-reference.md#intermediatesignalcatchevent).

| Example | Start values | Actors and actions | Expected result | Runtime requirements |
| --- | --- | --- | --- | --- |
| [`01-signal-catch-policy-change.json`](01-signal-catch-policy-change.json) | Supply `claimNumber`, for example `CLM-1001`. | Throw `claims.policy-changed` with a `version` number and an optional `effectiveDate`; then complete **Recalculate under the new policy**. Alternatively let the one-hour timer expire and complete **Review under the current policy**. | The claim waits at **Wait for the new policy**. A delivered signal writes `policyVersion` and `effectiveDate` and continues to the recalculation task. The timer boundary cancels the wait, and its subscription, after an hour. | Published definition, API, and `Flowbit.Worker`. The thrower needs a role from `Signals.RequiredRole` (default `admin`) or a managed API client with the `signals.throw` scope. |
| [`02-signal-boundaries-recall.json`](02-signal-boundaries-recall.json) | Supply `orderNumber`, for example `ORD-2001`. | Throw `warehouse.price-update` with a `unitPrice` number, as often as you like; complete each **Update the invoice**. Then throw `warehouse.product-recall` with an optional `reason`, or complete **Prepare the shipment** with **Ship**. | A price update adds a branch to **Update the invoice** with `unitPrice` set and leaves the shipment task waiting for the next one. A recall interrupts **Prepare the shipment**, ends both boundary waits, and opens **Stop the shipment** with `recallReason`. Shipping first ends both waits, so later signals reach nothing. | Published definition, API, and `Flowbit.Worker`. The thrower needs a role from `Signals.RequiredRole` (default `admin`) or a managed API client with the `signals.throw` scope. |

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

A price update whose `unitPrice` is missing, not a number, or not above zero fails with
`signal_payload_invalid`, and the shipment task keeps waiting. A non-interrupting
boundary that would push the instance past `Workflow.Gateway.MaxActiveTokens` active
tokens fails with `signal_token_limit` and stays armed.
