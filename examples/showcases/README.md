# Showcase workflows

Every other example in this catalog isolates one feature in a few nodes. These
two showcases model a complete business process instead, so you can see how
the features combine: lanes for the people involved, several outcomes, waits
for people and for other systems, and the error paths a production process
needs. Open one first when you evaluate Flowbit or demonstrate it; use the
[focused examples](../README.md) as the reference for each setting.

| Showcase | Process | Main features | Requirements |
| --- | --- | --- | --- |
| [Purchase request approval](01-purchase-request-approval.json) | A purchase request moves from the requester through manager review, an approval board for large amounts, and parallel finance and procurement checks to a purchase order. | Typed forms, business keys, assignment, claims, due dates in a business calendar, a working-day deadline, flow evidence, a quorum vote, parallel work, and a terminating end. | API and PostgreSQL. A Worker only for the deadline timer and administrative actions. |
| [Order fulfilment](02-order-fulfilment.json) | An order message starts the process; a credit service, a called approval workflow, a warehouse service, and a carrier callback move it to fulfilment. | Message start, idempotency, a validating script, REST status routing with retries and incidents, a call activity, per-line REST calls, a correlated message wait, and an expression timer. | API, PostgreSQL, Worker, a controlled REST service, and the published [manager approval child](../call-activities/01-manager-approval-child.json). |

Import and publish a showcase as described in
[getting started](../../docs/getting-started.md). Durations such as the order
showcase's carrier timeout are short local-test values; adjust them before
production use.

## 01 - Purchase request approval

[`01-purchase-request-approval.json`](01-purchase-request-approval.json),
workflow key `example-showcase-purchase-request`.

The [lifecycle load test](../../Flowbit/tools/LifecycleLoadTest/README.md) runs
this workflow's approval path to measure capacity. It finds the start event,
tasks, and flows it uses by their external IDs, so keep those IDs when you
change the definition.

### What it shows

| Feature | Where |
| --- | --- |
| Lanes, external IDs, and node `attributes` | Four lanes; every user task carries a `form` attribute an application can use to choose its screen. |
| Form labels, help text, fixed options, and validation | The start form (`category` offers three options) and every action form. |
| Business key with `active` uniqueness | `requestNumber`: a second open request with the same number is refused with `409`. |
| A `${sys.user}` default | `requestedBy` defaults to the person who starts the request. |
| Direct assignee expressions | **Review Purchase Request** goes to `managerUser`; **Revise Request** goes to `requestedBy`. |
| Due dates in a business calendar and priority expressions | Review is due two working days after it is created in the `examples.weekdays` calendar, with priority 80 from 25,000 and 50 below. |
| A working-day interrupting deadline | **Review Overdue** fires after three working days and moves the request to **Escalated Review**. |
| Claimed shared pools | **Escalated Review** (`DepartmentHead`) and **Confirm Budget** (`Finance`). |
| Flow evidence (`FlowInfo`) | **Send-Back Limit** counts traversals of **Send Back** (flow `202`); the third one closes the request. |
| Exclusive priority and default | **Approval Route**: 25,000 or more goes to the board, everything else straight to the checks. |
| Multi-instance quorum | **Board Vote**: three pooled items, one per board member, two matching votes decide (`CountFlow`), and the third item is cancelled. |
| Parallel split and join | **Purchase Checks** runs **Confirm Budget** and **Select Supplier** side by side. |
| Required assignment and assignment inheritance | **Select Supplier** stays hidden until a `ProcurementLead` assigns it; **Place Order** goes to the same buyer automatically (`assignmentMode: "fromNode"`). |
| Terminate end with live sibling work | **No Budget** ends the instance and cancels the open supplier selection. |

### Actors

The walkthrough uses these development identities; create each one on the UI's
[test identity page](../../docs/getting-started.md#obtain-a-development-bearer-token).
The Flowbit.Ui start page reads the definition, which needs the workflow
administrator role (`admin` by default), so give `pr-requester` both
`Requester` and `admin` to start from the UI, or start with
`POST /api/instances`, which needs only `Requester`.

| User name | Roles | Acts on |
| --- | --- | --- |
| `admin` | `admin` | Imports and publishes the definition; runs administrative actions. |
| `pr-requester` | `Requester` | Starts the request; revises or withdraws it. |
| `pr-manager` | `Manager` | **Review Purchase Request**, when named as `managerUser`. |
| `board-one`, `board-two`, `board-three` | `BoardMember` | **Board Vote**, one item each. |
| `pr-head` | `DepartmentHead` | **Escalated Review**. |
| `pr-finance` | `Finance` | **Confirm Budget**. |
| `pr-lead` | `ProcurementLead` | Assigns **Select Supplier** on **Task management**. |
| `pr-buyer` | `Buyer` | **Select Supplier** and **Place Order**. |

### Walkthrough

Each step names the Flowbit.Ui page and the matching HTTP call.

| Step | Actor | Action | Result |
| --- | --- | --- | --- |
| 1 | `pr-requester` | **Workflows → Start** (`POST /api/instances`) with `requestNumber` `PR-2026-0042`, `title` `Laptops for the design team`, `category` `equipment`, `amount` `18000`, and `managerUser` `pr-manager`. Leave **Requester** empty. | **Review Purchase Request** is assigned to `pr-manager` with priority 50 and a due time two working days away. `requestedBy` is `pr-requester`. Starting again with `PR-2026-0042` returns `409` with `business_key_conflict` while this request is open. |
| 2 | `pr-manager` | **My work** → **Send Back** (flow `202`) with a `revisionNote`. | **Send-Back Limit** sees the first send-back and returns the request to **Revise Request**, assigned to `pr-requester`. The review deadline is cancelled. |
| 3 | `pr-requester` | **Resubmit** (flow `601`) with `amount` `32000` and a `changeSummary`. | A new review item for `pr-manager`, now with priority 80. |
| 4 | `pr-manager` | **Approve** (flow `201`), optionally with a comment. | **Approval Route** sends 32,000 to **Board Vote**: three items for the `BoardMember` pool. |
| 5 | `board-one`, then `board-two` | **Approve** (flow `901`) on the item **My work** shows each of them. | After the second approval the vote is decided: the third item is cancelled and `boardVotes` holds three result rows. **Confirm Budget** and **Select Supplier** open together. |
| 6 | `pr-finance` | Claim **Confirm Budget**, then **Budget Confirmed** (flow `1201`) with `budgetCode` `IT-2026`. | The budget branch waits at **Checks Complete**. |
| 7 | `pr-lead` | **Task management** → assign **Select Supplier** to `pr-buyer` (`POST /api/user-tasks/{taskId}/assign`). | Until now no buyer could see the task. |
| 8 | `pr-buyer` | **Supplier Selected** (flow `1301`) with `supplierName` and `quotedAmount`. | **Checks Complete** joins both branches. **Place Order** is assigned to `pr-buyer` without another assignment step. |
| 9 | `pr-buyer` | **Order Placed** (flow `1501`) with `purchaseOrderNumber` `PO-1001`. | The instance completes at **Purchase Ordered**. Its diagram on the instance page shows the path taken. |

### Other endings

| What happens | Ending |
| --- | --- |
| The manager or the escalated reviewer chooses **Reject** (flow `203` or `402`) with a reason. | **Rejected** |
| Two board members choose **Reject** (flow `902`). | **Rejected by Board** |
| The manager chooses **Send Back** a third time. `FlowInfo(202, 'traversals.count') >= 3` is true. | **Closed After Three Send-Backs** |
| The requester chooses **Withdraw** (flow `602`) on **Revise Request**. | **Withdrawn** |
| Finance chooses **No Budget** (flow `1202`). The terminate end cancels **Select Supplier** even if it is still open. | **Stopped: No Budget**, completion kind `terminate` |
| The review is still open three working days after it was created. **Review Overdue** cancels it and opens **Escalated Review** for the `DepartmentHead` pool, priority 90. | Continues as an approval or a rejection |

Every multi-instance user task needs an engine-only default outcome. Here it
leads to **No Board Majority**, but three voters with two choices always give
one choice two votes, so the vote never ends there.

To see the escalation without waiting three working days, start the Worker and,
as `admin`, open **Administrative actions**. Select this workflow family, its
exact version, and **Review Purchase Request** as the waiting user-task node,
search positions (enter the instance ID to narrow them), check the request,
choose the **Escalate** timer boundary action, prepare the batch, and confirm it.
The review is cancelled and **Escalated Review** opens as if the deadline had
passed. See
[administrative batches](../../docs/ui-guide.md#use-administrative-batches).

## 02 - Order fulfilment

[`02-order-fulfilment.json`](02-order-fulfilment.json), workflow key
`example-showcase-order-fulfilment`.

### What it shows

| Feature | Where |
| --- | --- |
| Message start with typed mappings, transport idempotency, and a business key | **Order Received**: `orderId` is the active business key, and the `Idempotency-Key` header is stored separately as `orderRequestKey`. |
| A JavaScript script that validates and normalises data, with an error boundary and an error end | **Normalise Order Lines** trims and capitalises each SKU and refuses a line without a whole positive quantity; the failure ends at **Order Lines Invalid** with fault code `ORDER.INVALID_LINES`. |
| Durable REST with HTTP status routing, retries, and incidents | **Check Credit** runs on the Worker. A `4xx` answer takes **Credit Refused** to **Order Refused** (`ORDER.CREDIT_REFUSED`). A `5xx` answer, a timeout, or an unusable `2xx` answer matches no boundary, so it is retried after 5 and 15 seconds and then opens an incident an operator can retry. |
| Exclusive decision on a REST result | **Credit Decision** routes `approved` straight on and everything else to a manual override. |
| Reuse through a call activity | **Approve Credit Override** calls the [manager approval child](../call-activities/01-manager-approval-child.json); its rejection is caught by **Override Declined** and ends at **Order Declined**. |
| Multi-instance REST with bounded concurrency | **Reserve Stock** reserves each order line, two at a time, with a stable `Idempotency-Key` per item, and collects `reservations`. Any failed line takes **Stock Unavailable** to **Resolve Stock Shortage**, which can retry or cancel. |
| An NCalc script | **Prepare Shipment** builds `shipmentReference` (`SHP-` and the order number). |
| A message wait correlated by a stored value, with delivery idempotency | **Await Shipment Confirmation** accepts only a delivery whose `X-Shipment-Reference` header equals `shipmentReference`, and each `X-Flowbit-Delivery-Key` once. |
| An expression timer boundary | **Carrier Overdue** waits `if(expedited, 'PT1M', 'PT5M')`, then **Chase Carrier** asks a `ShippingClerk` for the tracking number. |
| Due dates, priority expressions, and form options on human fallbacks | **Resolve Stock Shortage** and **Chase Carrier**; the carrier field offers DHL, FedEx, and UPS. |

### Configuration and services

Publish the [manager approval child](../call-activities/01-manager-approval-child.json)
before this definition: publication checks the called workflow. Supply these
values to **both API and Worker**:

| Value | Purpose |
| --- | --- |
| `WorkflowContext__Config__exampleApiBaseUrl` | Base URL of a credit and warehouse service you control, without a trailing slash. |
| `WorkflowContext__Config__exampleApiToken` | Token the workflow sends as `Authorization: Bearer <token>`. |
| `WorkflowContext__Config__exampleMessageClientSecret` | Secret the order and carrier systems send as `X-Client-Secret`. |

The settings migration seeds `setting.examples.messageClientId`
(`example-message-client`) and `setting.examples.messageCorrelation`
(`orders:inbound`); see the [message examples](../messages/README.md#required-configuration).

Your controlled service answers two routes:

| Route and request body | Answer |
| --- | --- |
| `POST /customers/{customerId}/credit-checks` with `{"orderId":"ORD-1001","amount":1250.5}` | `200` with `{"decision":"approved"}` or `{"decision":"review"}`. A `4xx` refuses the order. A `5xx` is retried, then becomes an incident. Any other decision fails the process variable's validation and is treated like a `5xx`. |
| `POST /orders/{orderId}/reservations` with `{"orderId":"ORD-1001","line":{"sku":"KB-100","quantity":2}}`, once per line | `2xx` with `{"reservationId":"RES-..."}`; the identifier must start with `RES-`. Any other answer stops all lines and opens **Resolve Stock Shortage**. |

Both requests carry `Idempotency-Key: flowbit-<job id>`, which stays the same
when a job retries. **Retry Reservation** reserves every line again under new
jobs, so the warehouse service must also treat a repeated line of the same order
as the same reservation.

### Walkthrough

Run from the repository root with the API, Worker, and your controlled service
running. The first commands import and publish both definitions with a
development JWT holding `admin`, then send an order as the anonymous order
system. Supply the message secret your hosts are configured with.

#### PowerShell

```powershell
$api = 'http://127.0.0.1:15017'
$token = (Read-Host 'Paste the development JWT').Trim()
$secret = (Read-Host 'Message client secret').Trim()
$headers = @{ Authorization = "Bearer $token" }
foreach ($file in 'examples/call-activities/01-manager-approval-child.json', 'examples/showcases/02-order-fulfilment.json') {
  $definition = Get-Content -Raw $file | ConvertFrom-Json
  $body = @{ definition = $definition; publish = $true } | ConvertTo-Json -Depth 100
  Invoke-RestMethod "$api/api/workflows" -Method Post -Headers $headers -ContentType 'application/json' -Body $body | Format-List id, workflowKey, isPublished
}
$order = '{"order":{"id":"ORD-1001","total":1250.5,"expedited":false,"lines":[{"sku":" kb-100 ","quantity":2},{"sku":"MS-200","quantity":1}]},"customer":{"id":"C-42"}}'
$machine = @{ 'X-Client-Id' = 'example-message-client'; 'X-Client-Secret' = $secret; 'X-Flowbit-Correlation' = 'orders:inbound'; 'Idempotency-Key' = 'order-ORD-1001-1' }
$ack = Invoke-RestMethod "$api/api/workflows/example-showcase-order-fulfilment/message-start" -Method Post -Headers $machine -ContentType 'application/json' -Body $order
$ack | Format-List instanceId, status, currentNodeName
```

Wait until the Worker has checked credit and reserved both lines; the instance
then rests on **Await Shipment Confirmation**:

```powershell
$detail = Invoke-RestMethod "$api/api/instances/$($ack.instanceId)" -Headers $headers
$detail.executionPositions | Format-Table nodeId, nodeName, tokenStatus
$carrier = @{ 'X-Client-Id' = 'example-message-client'; 'X-Client-Secret' = $secret; 'X-Shipment-Reference' = 'SHP-ORD-1001'; 'X-Flowbit-Delivery-Key' = 'shipment-ORD-1001-1' }
$shipment = '{"shipment":{"trackingNumber":"1Z999AA10123456784","carrier":"UPS"}}'
Invoke-RestMethod "$api/api/instances/$($ack.instanceId)/message" -Method Post -Headers $carrier -ContentType 'application/json' -Body $shipment | Format-List status, currentNodeName
```

#### Bash

```bash
api='http://127.0.0.1:15017'
read -r -p 'Paste the development JWT: ' token
read -r -p 'Message client secret: ' secret
for file in examples/call-activities/01-manager-approval-child.json examples/showcases/02-order-fulfilment.json; do
  { printf '{"publish":true,"definition":'; cat "$file"; printf '}'; } |
    curl --fail-with-body -sS -X POST "$api/api/workflows" -H "Authorization: Bearer $token" \
      -H 'Content-Type: application/json' --data-binary @-
  printf '\n'
done
curl --fail-with-body -sS -X POST "$api/api/workflows/example-showcase-order-fulfilment/message-start" \
  -H 'X-Client-Id: example-message-client' -H "X-Client-Secret: $secret" \
  -H 'X-Flowbit-Correlation: orders:inbound' -H 'Idempotency-Key: order-ORD-1001-1' \
  -H 'Content-Type: application/json' \
  --data '{"order":{"id":"ORD-1001","total":1250.5,"expedited":false,"lines":[{"sku":" kb-100 ","quantity":2},{"sku":"MS-200","quantity":1}]},"customer":{"id":"C-42"}}'
printf '\n'
```

Set `instance_id` to the `instanceId` printed above. Once the instance rests on
**Await Shipment Confirmation**, deliver the carrier's confirmation:

```bash
instance_id=1
curl --fail-with-body -sS "$api/api/instances/$instance_id" -H "Authorization: Bearer $token"
printf '\n'
curl --fail-with-body -sS -X POST "$api/api/instances/$instance_id/message" \
  -H 'X-Client-Id: example-message-client' -H "X-Client-Secret: $secret" \
  -H 'X-Shipment-Reference: SHP-ORD-1001' -H 'X-Flowbit-Delivery-Key: shipment-ORD-1001-1' \
  -H 'Content-Type: application/json' \
  --data '{"shipment":{"trackingNumber":"1Z999AA10123456784","carrier":"UPS"}}'
printf '\n'
```

The instance completes at **Order Fulfilled**. On the way, `stockLines` holds
`KB-100` and `MS-200`, `shippingService` is `standard`, `creditDecision` is
`approved`, and `reservations` has one `completed` row per line with its
`reservationId`, in line order. Sending the order again returns `409` with
`idempotency_conflict`; sending it with a new `Idempotency-Key` while the order
is open returns `409` with `business_key_conflict`. Repeating the confirmation's
`X-Flowbit-Delivery-Key` returns `409`, and a delivery with another
`X-Shipment-Reference` returns `400`.

### Other paths

| Trigger | What happens |
| --- | --- |
| A line without a SKU or with a quantity that is not a whole positive number. | The message start still creates the instance, which ends at once at **Order Lines Invalid**: its response has status `faulted` and fault code `ORDER.INVALID_LINES`, and `orderProblem` names the line. |
| The credit service answers `{"decision":"review"}`. | **Approve Credit Override** starts the manager approval child; a `Manager` decides in **My work**. Approval continues to **Reserve Stock** with `overrideApproved` true; rejection ends at **Order Declined**, with `overrideFailure` starting `call_child_faulted`. |
| The credit service answers `4xx`. | **Order Refused**, status `faulted`, fault code `ORDER.CREDIT_REFUSED`; `creditStatus` holds the status. |
| The credit service answers `5xx`. | Three attempts, 5 and 15 seconds apart, then an incident on **Jobs & incidents**. Fix the service and retry the incident (`POST /api/incidents/{id}/retry`); the order continues. |
| A reservation fails. | Every line stops, `stockFailure` is `service_task_failed`, and **Resolve Stock Shortage** opens for a `WarehouseLead` (priority 90 for an expedited order). **Retry Reservation** reserves all lines again; **Cancel Order** ends at **Order Cancelled**. |
| No confirmation arrives in time: one minute for an expedited order, five otherwise. | **Carrier Overdue** cancels the wait, and a later confirmation returns `400`. A `ShippingClerk` enters the tracking number and carrier on **Chase Carrier**, and the order completes at **Order Fulfilled**. |

The [acceptance suite](../../Flowbit/tests/Flowbit.BrowserAcceptanceTests/README.md)
runs both showcases through these paths against real API and Worker processes
and a controlled service, firing the purchase deadline through an administrative
action rather than waiting three working days.
