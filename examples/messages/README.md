# Message workflow examples

These examples cover the two inbound-message entry points supported by Flowbit.
They use only local API calls and server-resolved settings; none of the workflows
contacts an external service.

| Example | Start values | Actors and actions | Expected result | Runtime requirements |
| --- | --- | --- | --- | --- |
| [`01-message-start-typed-mapping.json`](01-message-start-typed-mapping.json) | The JSON message supplies `orderId`, `customerEmail`, `totalAmount`, `requestedDate`, and optional typed fields. The `Idempotency-Key` header supplies the separate implicit `messageRequestId`. | A machine client starts the instance. An `OrderReviewer` takes flow `201` and may accept the default `reviewNote`. | A valid message creates an instance at **Review Imported Order**. The review action completes it. Duplicate transport keys are rejected permanently; duplicate active `orderId` business keys are rejected while their owning instance is active. | Published default definition and `Flowbit.Api`; no Worker. Supply the message-client secret and optionally override the seeded example settings below. |
| [`02-message-catch-delivery-idempotency.json`](02-message-catch-delivery-idempotency.json) | Start node `1` with a required `purchaseOrderId`, for example `PO-1001`. | A `Purchasing` actor takes flow `201`; the machine client delivers the receipt; a `Purchasing` actor takes flow `401`. | The instance waits at **Wait for Provider Receipt**. One authenticated, valid delivery writes all six mapped values atomically and advances it. Reusing its delivery key returns `409`. | Published definition and `Flowbit.Api`; no Worker. Supply the message-client secret and optionally override the seeded client id. |
| [`03-message-catch-with-timeout.json`](03-message-catch-with-timeout.json) | `ExampleRequester` starts node `1` with `caseKey`, default `CASE-EXAMPLE`. | The machine client delivers before expiry; `ExampleReviewer` takes flow `301`. Alternatively `ExampleOperator` takes escalation flow `501` after expiry. | Receipt cancels the competing deadline; the 30-second deadline cancels the message wait. Late delivery changes no mapped values. | Published definition, API, and Worker; use the same configured message credentials. |

## Required configuration

The settings migration supplies the two non-secret `setting.*` example values
shown below when they are missing. Supply the `config.*` secret through the API
deployment configuration before running these examples, and override the seeded
values when a different local identity or correlation value is required:

| Context key | Example local value | Purpose |
| --- | --- | --- |
| `setting.examples.messageClientId` | `example-message-client` | Expected `X-Client-Id`. |
| `config.exampleMessageClientSecret` | A local secret supplied through server configuration | Expected `X-Client-Secret`; never put the value in a definition or request example. |
| `setting.examples.messageCorrelation` | `orders:inbound` | Required `X-Flowbit-Correlation` value for example 01. |

Use TLS and gateway rate limiting when adapting these anonymous, credential-
authenticated endpoints for a deployed environment.

## Example 01: typed message start

`messageStartEvent` is system-only. It is not selected through
`POST /api/instances`; deliver the message to the published default workflow
version instead:

```http
POST /api/workflows/example-message-start-typed-mapping/message-start
Content-Type: application/json
X-Client-Id: example-message-client
X-Client-Secret: <configured server-side secret>
X-Flowbit-Correlation: orders:inbound
Idempotency-Key: example-order-delivery-0001
```

```json
{
  "order": {
    "id": "ORD-1001",
    "total": 149.95,
    "expedited": true,
    "requestedDate": "2026-08-15",
    "tags": [
      "new-customer",
      "priority"
    ]
  },
  "customer": {
    "email": "buyer@example.test"
  },
  "metadata": {
    "source": "local-mock",
    "attempt": 1
  }
}
```

The mappings demonstrate scalar `string`, `number`, `boolean`, and `date`, a
`string[]`, and scalar `json`. Missing optional `expedited`, `tags`, or
`metadata` paths use their typed defaults. A mapping, required-field, type, or
validation failure creates no instance.

`orderId` is the domain business key with `active` uniqueness. Transport
idempotency is independent: `messageRequestId` comes only from the configured
header and is not a body mapping. A committed idempotency key can never create a
second instance, even after the first instance reaches a terminal state.

## Example 02: message catch and delivery idempotency

Start the published workflow with a user carrying `Purchasing`:

```json
{
  "workflowKey": "example-message-catch-delivery-idempotency",
  "startEventId": 1,
  "variables": {
    "purchaseOrderId": "PO-1001"
  }
}
```

Take flow `201` to place the instance at node `3`, then deliver this local mock
receipt to `POST /api/instances/{instanceId}/message`:

```http
Content-Type: application/json
X-Client-Id: example-message-client
X-Client-Secret: <configured server-side secret>
X-Purchase-Order: PO-1001
X-Flowbit-Delivery-Key: provider-receipt-0001
```

```json
{
  "receipt": {
    "accepted": true,
    "code": "RCPT-1001",
    "amount": 149.95,
    "receivedAt": "2026-08-01T14:30:00Z",
    "warnings": [
      "local-mock-only"
    ],
    "provider": "example-provider"
  }
}
```

The correlation header and delivery-idempotency header are deliberately
different. The custom header must equal the stored `purchaseOrderId` and pass
its NCalc validation. The first committed key is permanently reserved for that
instance; an authenticated duplicate returns `409`. Invalid credentials return
`401`, while a bad header, payload, mapping, validation, or wait state returns
`400` without partial variable writes.

## Example 03: receipt versus timeout

Publish `example-message-catch-with-timeout` and start two separate runs through
`POST /api/instances` as `ExampleRequester`:

```json
{
  "workflowKey": "example-message-catch-with-timeout",
  "startEventId": 1,
  "variables": { "caseKey": "CASE-1001" }
}
```

For the timely run, deliver to its returned instance ID while **Wait for
Receipt** (node `2`) is active:

```http
POST /api/instances/{instanceId}/message
Content-Type: application/json
X-Client-Id: example-message-client
X-Client-Secret: <configured server-side secret>
X-Correlation: CASE-1001
X-Flowbit-Delivery-Key: deadline-receipt-0001

{"receipt":{"accepted":true,"code":"RCPT-1001"}}
```

The receipt writes `acknowledged=true` and `receiptCode="RCPT-1001"`, cancels
the timer job, and opens **Review Timely Receipt**. As `ExampleReviewer`, take
flow `301` to finish. An authenticated duplicate of the committed delivery key
returns `409`.

For the expiry run, send nothing, keep the Worker running, and wait until
**Escalate Missing Receipt** appears. The timer is due after 30 seconds; use
observed state rather than assuming it fires at an exact wall-clock instant.
While escalation is still active, send the same valid receipt shape using a
new delivery key. With no active message wait it returns `400`;
`acknowledged=false` and `receiptCode="PENDING"` remain unchanged. The late
message cannot restore the cancelled wait. As `ExampleOperator`, take flow
`501` to resolve escalation.

These are distinct conflicts: a late message with a new key is invalid wait
state (`400`), while reuse of a committed delivery key is a duplicate (`409`).
See [message HTTP contracts](../../docs/api-guide.md#instances-and-messages).

No example embeds a working secret or invokes a public endpoint. Examples 01
and 02 need only the API and database; example 03 also requires a Worker to
fire its deadline.
