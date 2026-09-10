# Flowbit HTTP API reference

[Documentation home](index.md) · [First workflow](getting-started.md) · [Integration guide](developer-guide.md)

Use this reference to build an HTTP client for the current Flowbit checkout. The API stores versioned workflow definitions, runs instances, exposes actor task inboxes, and provides administrative and operational interfaces. Follow [Getting started](getting-started.md) for an executable PowerShell or Bash tutorial.

All JSON property names below are the wire names. Database identifiers are integers; authored node and sequence-flow IDs are integers inside a definition. Workflow keys are strings. An execution ID identifies one activation or ledger record, not a workflow version. See the [identifier glossary](developer-guide.md).

## Contents

- [HTTP conventions and authentication](#http-conventions-and-authentication)
- [Errors and concurrency](#errors-and-concurrency)
- [Filtering and pagination](#filtering-and-pagination)
- [Authentication context](#authentication-context)
- [Workflow definitions](#workflow-definitions)
- [Instances and messages](#instances-and-messages)
- [User tasks](#user-tasks)
- [Multi-instance executions](#multi-instance-executions)
- [Task distribution](#task-distribution)
- [Node execution activity](#node-execution-activity)
- [Jobs and incidents](#jobs-and-incidents)
- [Instance administrative actions](#instance-administrative-actions)
- [Administrative action batches](#administrative-action-batches)
- [Version-change batches](#version-change-batches)
- [Variable-update batches](#variable-update-batches)
- [Delegation](#delegation)
- [Settings](#settings)
- [Shared variables](#shared-variables)
- [Shared-variable clients](#shared-variable-clients)
- [Retention](#retention)
- [Shared JSON schemas](#shared-json-schemas)
- [Worker operational endpoints](#worker-operational-endpoints)
- [API development endpoints](#api-development-endpoints)

## HTTP conventions and authentication

The examples use `Host: localhost:5017`; replace it with your API origin. Send `Content-Type: application/json` when sending a JSON body. `TOKEN`, client credentials, IDs, timestamps, and sample results are illustrative. Copy IDs, revision numbers, capability flags, and timestamps from actual preceding responses. A successful request may finish automatic routing before it responds, so its representative current node can already be an end event.

The API currently validates bearer JWTs with the deployment's symmetric `Jwt:Key`, issuer, audience, and lifetime. It does not expose a login or token-issuance endpoint. The optional UI's `/token` page provides its development token; see [deployment authentication](deployment.md). The canonical actor claim is selected at API startup by `Authentication.UserIdentityClaim`; roles are normalized server-side. Call `GET /api/auth/context` to see the resulting identity.

| Permission name in this reference | Actual authorization |
| --- | --- |
| Bearer | A valid bearer JWT. Additional actor/task checks are described at the endpoint. |
| Workflow administrator | Bearer plus any role in the comma-separated engine setting `Workflow.RequiredRole`, matched case-insensitively; missing/blank defaults to `admin`. A custom value replaces the default. Administrative-action routes also require a nonblank actor. |
| Settings administrator | Bearer plus `Settings.RequiredRole`, default `admin`. |
| Job operator | Bearer plus `WorkflowJobs.RequiredRole`, default `admin`. |
| Delegation administrator | Bearer plus `Delegation.AdminRoles`, default `admin`, and a nonblank actor identity. |
| Shared-variable administrator | Bearer plus `SharedVariables.RequiredRole`, default `admin`. This is required for JWT reads as well as writes. |
| Task actor | Bearer plus the persisted task/action roles, applicable assignment/claim/delegation rules, and the task's database inbox-visibility predicate. |
| Task assignment manager | Bearer plus an authored workflow `taskAssignmentRoles` match. This does not grant task-action rights. |
| Task role manager | Bearer plus an authored workflow `taskRoleManagementRoles` match. An empty list disables role management. |
| Instance-list reader | Bearer; lists/search restrict workflow versions through `taskAssignmentRoles` or `WorkflowInstances.RequiredRole` (default `admin`). |
| Node-activity reader | Bearer; each exact workflow version is visible through its `taskAssignmentRoles` or `NodeExecution.RequiredRole` (default `admin`). |
| Message client | The target node's configured `X-Client-Id` and `X-Client-Secret`, plus its authored correlation header. These routes do not use bearer authorization. |
| Distributor | Workflow-family `X-Client-Id` and `X-Client-Secret`; this is separate from message-client and shared-variable-client authentication. |
| Shared-variable client | A managed deployment-wide API client with `shared-variables.read` or `shared-variables.write` as required. Send exactly one of each client header. |

Shared-variable routes reject combining bearer and client credentials. A write scope permits only `PUT /api/shared-variables/{key}/value`; catalog reads require the read scope. Contract creation, metadata/lifecycle changes, history, blockers, and client management require the JWT administrator.

**Integration boundary:** this checkout does not apply a universal tenant/owner filter to every bearer endpoint. Instance detail is a deployment-level read after authentication; instance lists/search separately enforce the workflow-version visibility rule above. Every administrative-action catalog, candidate, batch, and direct instance endpoint requires a nonblank authenticated actor and workflow-administrator permission. Those explicit overrides bypass ordinary task authorization; having the administrator role alone does not bypass personal task APIs or inbox filtering. Actor-scoped inbox and personal task routes retain their own visibility rules; filtering an API request is not an authorization mechanism.

## Errors and concurrency

The API does not have one universal error envelope. Clients should branch on HTTP status and, when present, the stable `code` field. Treat human-readable `error` text as diagnostics.

| Status | Contract and client behavior |
| --- | --- |
| `200`, `201`, `202`, `204` | Successful read/mutation, created resource, accepted durable work, or success without a response body. `201` and `202` endpoints described below set `Location`. A batch `202` means preparation is queued, not that its items have executed. |
| `400` | Domain validation normally returns `{"error":"description"}`. Malformed transport input may instead use framework binding errors or Problem Details. Task role/claim/flow mismatches are often domain errors, not `403`. |
| `401` | Missing/invalid JWT or machine credentials. JWT middleware may return an empty body; service authentication errors can return `{"error":"description"}`. |
| `403` | Required administrator/manager permission missing. Endpoint filters may return an empty body; service-level forbiddance returns an `error` object. |
| `404` | Missing resource, or an intentionally hidden personal-task/node-execution resource. Do not use detail probing to infer task availability. |
| `409` | Stale work, incompatible version/reactivation, optimistic revision mismatch, or a conflict described below. Refresh the resource before deciding whether to retry. |
| `413`, `415` | Request exceeds the endpoint limit, or JSON media type is required. The server/reverse proxy may impose a lower limit. |

Start idempotency is configured on the selected entry node, including its HTTP header name. It is permanently scoped to the stable workflow key across versions and across user/message starts. A committed duplicate returns:

```http
HTTP/1.1 409 Conflict
Location: /api/instances/101
Content-Type: application/json

{"code":"idempotency_conflict","instanceId":101}
```

Message-start business-key conflicts use the same slim shape with `code: "business_key_conflict"`. A user-start business-key conflict uses the middleware shape `{"code":"business_key_conflict","error":"...","existingInstanceId":101}`. Both set `Location`. Business-key uniqueness is independently configured as active-only or permanent; it is not transport idempotency.

For a message catch with `message.deliveryIdempotency: true`, supply its configured delivery header (default `Idempotency-Key`). Authenticated reuse of a committed instance-scoped key returns `409` with `{"code":"...","instanceId":101,"sourceNodeId":4}` and a `Location` header. Do not treat every conflict as a new successful delivery.

Optimistic commands use the exact returned `updatedAt`, `revision`, or `rolePolicyId`, not a client clock. Normal task actions instead re-check active state under database locks: a concurrently completed or cancelled item returns `409`. Role replacement has a specific retry rule: a stale identical replacement is unchanged success; a stale different replacement is `409`. Batch creation and administrative variable updates have their own request idempotency keys; those are distinct from start idempotency.

## Filtering and pagination

Paged JSON responses use `items`, `page`, `pageSize`, `totalCount`, and, on cursor-based surfaces, `nextCursor`. The cursor field is omitted when null; calculate page counts from the count and size if needed. Defaults are page 1 and page size 50, with a maximum of 200. Do not assume all endpoints normalize nonpositive values identically: instance/inbox GET treats a nonpositive page size as 50; most management endpoints clamp it to 1.

Instance lists/search, job/incident lists, and variable-update candidate searches use opaque keyset cursors; pages after the first require the previous response's `nextCursor`. Do not decode or fabricate cursors. Preserve the query and sort while paging. Inbox, task management, distribution, node activity, delegation, and batch audit lists use numbered pages. Job attempts use `cursor` and `pageSize` without a page query parameter.

Repeated GET `var=name:value` filters perform exact case-insensitive comparison against each instance variable's latest scalar value. Multiple filters are AND-combined; object/array values do not match. They are not execution-time variable snapshots.

POST search requests use a typed `variableFilter` over the same current-value projection:

```json
{
  "workflowKey": "purchase-approval",
  "variableFilter": {
    "$and": [
      {"request.region": {"$eqIgnoreCase": "north"}},
      {"amount": {"$gte": 1000}},
      {"tags": {"$contains": "urgent"}}
    ]
  },
  "sort": [{"field": "updatedAt", "direction": "desc"}],
  "includeVariables": true,
  "pageSize": 50
}
```

Logical operators: `$and`, `$or`, `$not`. Comparisons: `$eq`, `$eqIgnoreCase`, `$ne`, `$in`, `$nin`, `$gt`, `$gte`, `$lt`, `$lte`, `$exists`, `$contains`, `$containsAny`, `$containsAll`, `$elemMatch`. Ordinary fields and multiple operators on a field imply AND. Keep logical and ordinary field members in separate nodes. Dotted paths start with the variable name; for literal dots use `{"$field":{"$var":"request.name","$path":["id"],"$eq":"A1"}}`. Array indexes are unsupported; `$elemMatch` correlates predicates within a single array element.

`$eq` is typed and case-sensitive; numeric ranges accept numbers only. Explicit JSON null differs from a missing path; missing values do not match `$ne` or `$nin`. Use `$exists:false` for absence. Filters allow five logical levels, 20 comparison predicates, 100 membership values, and 16 path segments. The five main advanced-search bodies are limited to 64 KiB; batch requests use the separate limits shown below. Unknown operators, regex, raw JSONPath, `$where`, and executable expressions return `400`. Full semantics and examples are in the [runtime search reference](../Flowbit/README.md#advanced-variable-search).

GET sorting uses repeated `sort=field:asc` or `sort=field:desc`; POST uses objects with `field` and `direction`. At most three criteria are accepted. Instance sorts: `id`, `createdAt`, `updatedAt`, default `updatedAt DESC, id DESC`. Inbox sorts include task and instance ID/creation/update fields, default `taskUpdatedAt DESC, taskId DESC`. Node-activity sorts and range semantics are listed in its section. An ID tie-breaker makes ordering deterministic; sorting and filters are applied in PostgreSQL before paging.

Object response examples below are **excerpts** when a DTO is large; the linked schema tables enumerate the complete response. Empty-list examples represent a valid search with no matches. Request examples are complete illustrative bodies, but their IDs, credentials, timestamps and authored inputs must match your deployment.

## Authentication context

Use the server-resolved context to diagnose identity/role mapping; this endpoint does not mint or refresh a token.

### GET /api/auth/context

Get the server-resolved actor identity and normalized roles.

**Access:** Bearer. See [authentication](#http-conventions-and-authentication).

**Parameters:** none.

**Body:** none.

**Success:** `200` [ActorContextDto](#schema-actorcontextdto).
**Errors:** `401` Unauthorized. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/auth/context HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "user": "reviewer@example.com",
  "roles": [
    "Requester",
    "Reviewer"
  ]
}
```

## Workflow definitions

All operations here require workflow-administrator access except `message-start`, which authenticates the configured message client. Definition JSON is a versioned immutable snapshot. POST with an existing model `id` and PUT both create new version rows; neither mutates a running version. Multiple versions can be published, but only one is the default for key-based starts. Publication of durable features requires the configured Worker deployment capability. Definition deletion returns `409` when runtime state or retained audit references the version; it does not detach existing instances from their definition.

### GET /api/workflows

Lists the latest versions of all workflow definitions.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

**Parameters:** none.

**Body:** none.

**Success:** `200` array of [WorkflowSummaryDto](#schema-workflowsummarydto).
**Errors:** `401` Unauthorized; `403` Forbidden. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/workflows HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

[]
```

### POST /api/workflows

Creates the next version in the workflow-key family.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

**Parameters:** none.

**JSON body:** [CreateWorkflowRequest](#schema-createworkflowrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `201` [WorkflowDetailDto](#schema-workflowdetaildto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/workflows HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{"definition":{"id":"example-api-approval","name":"API approval example","initialEventId":1,"lanes":[{"id":1,"name":"Operations","x":30,"y":30,"w":1000,"h":350}],"variables":[],"flowNodes":[{"id":1,"type":"startEvent","name":"Start","laneId":1,"x":100,"y":180,"roles":["Requester"],"variables":[{"id":1,"name":"requestReference","dataType":"string","required":true}]},{"id":2,"type":"userTask","name":"Review","laneId":1,"x":350,"y":180,"roles":["Reviewer"],"requiresClaim":true},{"id":3,"type":"endEvent","name":"Approved","laneId":1,"x":750,"y":180}],"sequenceFlows":[{"id":101,"name":"Open review","sourceRef":1,"targetRef":2},{"id":201,"name":"Approve","sourceRef":2,"targetRef":3,"roles":["Reviewer"],"variables":[{"id":2,"name":"reviewNote","dataType":"string","required":true}]}]},"publish":false}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 201 Created
Location: /api/workflows/11
Content-Type: application/json

{
  "id": 11,
  "name": "API approval example",
  "workflowKey": "example-api-approval",
  "version": 1,
  "isPublished": false,
  "isDefault": false,
  "createdAt": "2026-09-09T10:00:00Z"
}
```

### GET /api/workflows/{workflowKey}/versions

Lists all versions of a workflow definition identified by its workflow key.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `workflowKey` | path | string | Yes | The stable cross-version key identifying the workflow. |

**Body:** none.

**Success:** `200` array of [WorkflowSummaryDto](#schema-workflowsummarydto).
**Errors:** `401` Unauthorized; `403` Forbidden. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/workflows/example-api-approval/versions HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

[]
```

### GET /api/workflows/{id}

Retrieves a specific workflow definition version by its database ID.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | The database ID of the workflow definition version. |

**Body:** none.

**Success:** `200` [WorkflowDetailDto](#schema-workflowdetaildto).
**Errors:** `401` Unauthorized; `403` Forbidden; `404` Not Found. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/workflows/11 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "id": 11,
  "name": "API approval example",
  "workflowKey": "example-api-approval",
  "version": 1,
  "isPublished": false,
  "isDefault": false,
  "createdAt": "2026-09-09T10:00:00Z"
}
```

### PUT /api/workflows/{id}

Creates a new version of an existing workflow definition.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | The database ID of the workflow version to base the update on. |

**JSON body:** [UpdateWorkflowRequest](#schema-updateworkflowrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [WorkflowDetailDto](#schema-workflowdetaildto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
PUT /api/workflows/11 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{"definition":{"id":"example-api-approval","name":"API approval example","initialEventId":1,"lanes":[{"id":1,"name":"Operations","x":30,"y":30,"w":1000,"h":350}],"variables":[],"flowNodes":[{"id":1,"type":"startEvent","name":"Start","laneId":1,"x":100,"y":180,"roles":["Requester"],"variables":[{"id":1,"name":"requestReference","dataType":"string","required":true}]},{"id":2,"type":"userTask","name":"Review","laneId":1,"x":350,"y":180,"roles":["Reviewer"],"requiresClaim":true},{"id":3,"type":"endEvent","name":"Approved","laneId":1,"x":750,"y":180}],"sequenceFlows":[{"id":101,"name":"Open review","sourceRef":1,"targetRef":2},{"id":201,"name":"Approve","sourceRef":2,"targetRef":3,"roles":["Reviewer"],"variables":[{"id":2,"name":"reviewNote","dataType":"string","required":true}]}]},"publish":false}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "id": 12,
  "name": "API approval example",
  "workflowKey": "example-api-approval",
  "version": 2,
  "isPublished": false,
  "isDefault": false,
  "createdAt": "2026-09-09T10:00:00Z"
}
```

### DELETE /api/workflows/{id}

Deletes a specific version of a workflow definition.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | The database ID of the workflow version to delete. |

**Body:** none.

**Success:** `204` without a response body.
**Errors:** `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Retained runtime or audit references prevent deletion. See [error envelopes](#errors-and-concurrency) and the family rules above.

No request body. Retained runtime/audit references block deletion with 409; choose unpublish when the intent is to stop future starts.

```http
DELETE /api/workflows/11 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response:

```http
HTTP/1.1 204 No Content
```

### POST /api/workflows/{id}/publish

Publishes a specific version of a workflow definition, making it available for starting new instances.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | The database ID of the workflow version to publish. |

**Body:** none.

**Success:** `204` without a response body.
**Errors:** `400` Publication/domain validation failure; `401` Unauthorized; `403` Forbidden; `404` Not Found. See [error envelopes](#errors-and-concurrency) and the family rules above.

No request body. Publication validates configured deployment support; domain failures can return 400 beyond the declared response metadata.

```http
POST /api/workflows/11/publish HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response:

```http
HTTP/1.1 204 No Content
```

### POST /api/workflows/{id}/unpublish

Unpublishes a specific version of a workflow definition, making it unavailable for starting new instances.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | The database ID of the workflow version to unpublish. |

**Body:** none.

**Success:** `204` without a response body.
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found. See [error envelopes](#errors-and-concurrency) and the family rules above.

No request body. A default version cannot be unpublished; select another published default first.

```http
POST /api/workflows/11/unpublish HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response:

```http
HTTP/1.1 204 No Content
```

### POST /api/workflows/{id}/set-default

Sets a specific version as the default for its workflow key.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | The database ID of the workflow version to set as default. |

**Body:** none.

**Success:** `204` without a response body.
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found. See [error envelopes](#errors-and-concurrency) and the family rules above.

No request body. The target must already be published.

```http
POST /api/workflows/11/set-default HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response:

```http
HTTP/1.1 204 No Content
```

### POST /api/workflows/{workflowKey}/message-start

Starts a new workflow instance by delivering an initial message payload to a system-only message start event.

**Access:** Message client. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `workflowKey` | path | string | Yes | The stable cross-version key identifying the workflow. |
| `startEvent` | query | string | No | Optional exact message-start external ID; repeated values rejected. |
| `X-Client-Id` | header | string | Yes | Configured target-node credential. |
| `X-Client-Secret` | header | string | Yes | Configured target-node credential. |
| `authored correlation header` | header | string | Yes | Use message.headerName with the required value/headerValidation; add the configured idempotency header when enabled. |

**JSON body:** [JsonElement](#schema-jsonelement) (raw message payload; an empty body is also accepted before mapping validation). 

**Success:** `200` [MessageStartAckDto](#schema-messagestartackdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `409` Conflict; `413` Payload Too Large; `415` Unsupported Media Type. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/workflows/example-api-approval/message-start?startEvent=START_FROM_MESSAGE HTTP/1.1
Host: localhost:5017
X-Client-Id: example-client
X-Client-Secret: CLIENT_SECRET
X-Correlation-Id: REQ-DEMO-001
Content-Type: application/json

{
  "requestReference": "REQ-DEMO-001",
  "approved": true
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "instanceId": 101,
  "currentNodeId": 2,
  "currentNodeName": "Review",
  "currentNodeExternalId": null,
  "status": "running",
  "createdAt": "2026-09-09T10:00:00Z"
}
```

## Instances and messages

Start with exactly one of `workflowId` or `workflowKey`. By-key starts resolve the published default; `startEventId` is an authored node ID. Initial input must match the selected start contract. Use `executionPositions` for parallel work rather than assuming `currentNodeId` is the only execution position. Instance list/search is filtered by the immutable definition’s `taskAssignmentRoles` or `WorkflowInstances.RequiredRole` (default `admin`); instance detail is a deployment-level bearer read without that list predicate. Inbox/personal task operations apply actor-specific visibility. Use task-addressed actions in new clients. The instance-addressed flows/claim/unclaim/action routes are compatibility surfaces and return `409` when multiple active user tasks make the address ambiguous.

Message bodies are raw external JSON, not a `{variables:...}` wrapper. `message-start` accepts optional `startEvent`, the exact external ID of a message-start node; repeated selectors fail with `400`. Catch delivery accepts optional case-sensitive `catchEvent`, required when multiple active message waits are ambiguous. Each node defines its own correlation header and typed output mappings. Missing/mismatched correlation or invalid mapping yields `400`; bad client secrets yield `401`. Message payloads default to a 1 MiB deployment limit; empty bodies and JSON null are accepted subject to mappings.

Reactivation and in-place version change require workflow-administrator access. Always preview, inspect blockers/warnings, and submit the returned expected fields plus a reason. Reactivation targets eligible prior top-level ordinary user tasks of completed/cancelled instances; it preserves history, rechecks business-key ownership, and is blocked permanently after instance-owned history has been pruned. A version change requires a compatible published version in the same family and a currently running instance.

`PATCH .../variables` is an administrative raw-JSON operation: it can add instance variables and deliberately bypasses authored types/nullability/NCalc validation. JSON null is a stored value, not deletion. Shared aliases and protected identifiers remain restricted. Writes are atomic, audited, and passed through conditional-event coordination; a variable update can therefore wake or interrupt waiting work.

### PATCH /api/instances/{id}/variables

Administratively add or update variables on one running instance.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**JSON body:** [UpdateInstanceVariablesRequest](#schema-updateinstancevariablesrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [UpdateInstanceVariablesResultDto](#schema-updateinstancevariablesresultdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict; `413` Payload Too Large; `415` Unsupported Media Type. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
PATCH /api/instances/101/variables HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "variables": [
    {
      "name": "reviewRequired",
      "value": true
    }
  ],
  "reason": "Correct operational state",
  "idempotencyKey": "variable-update-001"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "operationId": 1101,
  "instanceId": 101,
  "workflowDefinitionId": 11,
  "updatedAt": "2026-09-09T10:00:00Z",
  "variables": [
    {
      "name": "reviewRequired",
      "outcome": "added",
      "variableId": 1201,
      "value": true
    }
  ],
  "warnings": []
}
```

### POST /api/instances

Starts a new workflow instance based on a workflow definition ID or key.

**Access:** Bearer. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `detail` | query | string | No | Optional. If set to 'full', returns the detailed instance DTO instead of the slim version. |

**JSON body:** [StartInstanceRequest](#schema-startinstancerequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `201` [StartInstanceResultDto](#schema-startinstanceresultdto); `detail=full` returns [InstanceDetailDto](#schema-instancedetaildto).
**Errors:** `400` Bad Request; `401` Unauthorized; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

Default response is StartInstanceResultDto; detail=full selects InstanceDetailDto. StartEvent roles are enforced. Configured idempotency headers cannot be supplied through variables.

```http
POST /api/instances HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "workflowId": 11,
  "workflowKey": null,
  "startEventId": null,
  "variables": {
    "requestReference": "REQ-DEMO-001"
  }
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 201 Created
Location: /api/instances/101
Content-Type: application/json

{
  "id": 101,
  "currentNodeId": 2,
  "currentNodeName": "Review",
  "currentNodeExternalId": null,
  "status": "running",
  "businessKey": null,
  "businessKeyUniqueness": null,
  "startedBy": "reviewer@example.com",
  "createdAt": "2026-09-09T10:00:00Z",
  "updatedAt": "2026-09-09T10:00:00Z",
  "executionPositions": [
    {
      "tokenId": 201,
      "nodeId": 2,
      "nodeName": "Review",
      "nodeExternalId": null,
      "nodeType": "userTask",
      "tokenStatus": "active",
      "arrivedViaFlowId": 101,
      "terminationReason": null,
      "userTaskId": 301,
      "multiInstanceExecutionId": null
    }
  ]
}
```

### GET /api/instances

Lists workflow instances matching filter criteria.

**Access:** Instance-list reader. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `status` | query | string | No | Optional. Filter by execution status (`running`, `completed`, `faulted`, `cancelled`). |
| `instanceId` | query | integer (int64) | No | Optional. Filter by unique instance ID. |
| `workflowId` | query | integer (int64) | No | Optional. Filter by specific workflow version ID. |
| `workflowKey` | query | string | No | Optional. Filter by stable cross-version workflow key (spans all versions). |
| `businessKey` | query | string | No | Optional. Exact, case-sensitive business-key match after trimming. |
| `nodeId` | query | integer (int32) | No | Optional. Filter by the ID of the current resting flow node. |
| `nodeExternalId` | query | string | No | Optional. Filter by the external ID of the current resting flow node (case-insensitive). |
| `var` | query | array of string | No | Repeated `var=name:value` filter. Exact, case-insensitive match on an instance variable's latest scalar value. Multiple entries are AND-combined. Array/object variables never match. |
| `sort` | query | array of string | No | Optional. Up to three repeated `sort=field:direction` clauses. Fields: `id`, `createdAt`, `updatedAt`; directions: `asc`, `desc`. |
| `cursor` | query | string | No | Optional. Opaque keyset cursor returned as `nextCursor` by the preceding page. The cursor is bound to the requested sort order. |
| `includeVariables` | query | boolean | No | When true, each returned item includes a `variables` object containing the latest value for every instance variable. Defaults to false. |
| `page` | query | integer (int32) | No | Optional display index (default 1). Pages after the first also require cursor. |
| `pageSize` | query | integer (int32) | No | Optional. The number of items per page (default 50, max 200). |

**Body:** none.

**Success:** `200` [PagedResultOfInstanceSummaryDto](#schema-pagedresultofinstancesummarydto).
**Errors:** `400` Bad Request; `401` Unauthorized. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/instances?pageSize=50 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalCount": 0
}
```

### POST /api/instances/search

Search workflow instances with advanced variable predicates.

**Access:** Instance-list reader. See [authentication](#http-conventions-and-authentication).

**Parameters:** none.

**JSON body:** [InstanceSearchRequest](#schema-instancesearchrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [PagedResultOfInstanceSummaryDto](#schema-pagedresultofinstancesummarydto).
**Errors:** `400` Bad Request; `401` Unauthorized; `413` Payload Too Large; `415` Unsupported Media Type. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/instances/search HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "workflowKey": "example-api-approval",
  "variableFilter": {
    "requestReference": {
      "$eq": "REQ-DEMO-001"
    }
  },
  "sort": [
    {
      "field": "updatedAt",
      "direction": "desc"
    }
  ],
  "includeVariables": true,
  "pageSize": 50
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalCount": 0
}
```

### GET /api/instances/inbox

Retrieves user tasks pending claim or action for the current authenticated actor.

**Access:** Bearer. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `instanceId` | query | integer (int64) | No | Optional. Filter by unique instance ID. |
| `workflowId` | query | integer (int64) | No | Optional. Filter by workflow definition version ID. |
| `workflowKey` | query | string | No | Optional. Filter by stable workflow key (spans all versions). |
| `businessKey` | query | string | No | Optional. Exact, case-sensitive business-key match after trimming. |
| `nodeId` | query | integer (int32) | No | Optional. Filter by flow node ID. |
| `nodeExternalId` | query | string | No | Optional. Filter by flow node external ID (case-insensitive). |
| `var` | query | array of string | No | Repeated `var=name:value` filter. Exact, case-insensitive match on an instance variable's latest scalar value. Multiple entries are AND-combined. Array/object variables never match. |
| `sort` | query | array of string | No | Optional. Up to three repeated `sort=field:direction` clauses over task/instance IDs and creation/update timestamps. |
| `includeVariables` | query | boolean | No | When true, each returned item includes a `variables` object containing the latest value for every instance variable. Defaults to false. |
| `page` | query | integer (int32) | No | Optional. The 1-based page index (default 1). |
| `pageSize` | query | integer (int32) | No | Optional. The number of items per page (default 50, max 200). |

**Body:** none.

**Success:** `200` [PagedResultOfInboxItemDto](#schema-pagedresultofinboxitemdto).
**Errors:** `400` Bad Request; `401` Unauthorized. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/instances/inbox?pageSize=50 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalCount": 0
}
```

### POST /api/instances/inbox/search

Search the actor inbox with advanced variable predicates.

**Access:** Bearer. See [authentication](#http-conventions-and-authentication).

**Parameters:** none.

**JSON body:** [InboxSearchRequest](#schema-inboxsearchrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [PagedResultOfInboxItemDto](#schema-pagedresultofinboxitemdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `413` Payload Too Large; `415` Unsupported Media Type. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/instances/inbox/search HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "workflowKey": "example-api-approval",
  "variableFilter": {
    "requestReference": {
      "$eq": "REQ-DEMO-001"
    }
  },
  "sort": [
    {
      "field": "taskUpdatedAt",
      "direction": "desc"
    }
  ],
  "includeVariables": true,
  "page": 1,
  "pageSize": 50
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalCount": 0
}
```

### GET /api/instances/{id}

Retrieves full structural and execution details of a specific workflow instance.

**Access:** Bearer. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | The database ID of the workflow instance. |

**Body:** none.

**Success:** `200` [InstanceDetailDto](#schema-instancedetaildto).
**Errors:** `401` Unauthorized; `404` Not Found. See [error envelopes](#errors-and-concurrency) and the family rules above.

This is an authenticated deployment-level detail read, not a personal-task visibility check.

Each history row may include `actorClaims`, a snapshot of the actual actor's selected JWT claims, separate from the business `payload`. Claim names map to arrays of strings, including repeated values. `null` or an absent property means not recorded; `{}` means capture was enabled but no selected claim was present. These values have the same access boundary as instance detail; select suitable claims through [audit configuration](deployment.md#selected-claim-audit).

```http
GET /api/instances/101 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "id": 101,
  "workflow": {
    "id": 11,
    "workflowKey": "example-api-approval",
    "name": "API approval example",
    "version": 1,
    "isPublished": true,
    "isDefault": true
  },
  "status": "running",
  "currentNodeId": 2,
  "currentNodeName": "Review",
  "businessKey": null,
  "createdAt": "2026-09-09T10:00:00Z",
  "updatedAt": "2026-09-09T10:00:00Z"
}
```

### GET /api/instances/{id}/reactivation

Checks whether a completed or cancelled instance can be reactivated and returns its eligible previously visited user-task targets.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**Body:** none.

**Success:** `200` [InstanceReactivationPreviewDto](#schema-instancereactivationpreviewdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/instances/101/reactivation HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "instanceId": 101,
  "workflowId": 11,
  "status": "completed",
  "canReactivate": true,
  "targets": [
    {
      "nodeId": 2,
      "nodeName": "Review",
      "nodeExternalId": null,
      "lastVisitedAt": "2026-09-09T10:00:00Z"
    }
  ],
  "blockers": [],
  "warnings": [],
  "expectedUpdatedAt": "2026-09-09T10:00:00Z"
}
```

### POST /api/instances/{id}/reactivation

Atomically creates a fresh user-task activation for an eligible completed or cancelled instance while preserving its prior runtime history.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**JSON body:** [ReactivateInstanceRequest](#schema-reactivateinstancerequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [InstanceDetailDto](#schema-instancedetaildto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/instances/101/reactivation HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "targetNodeId": 2,
  "expectedWorkflowId": 11,
  "expectedUpdatedAt": "2026-09-09T10:00:00Z",
  "reason": "Review corrected request"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "id": 101,
  "workflow": {
    "id": 11,
    "workflowKey": "example-api-approval",
    "name": "API approval example",
    "version": 1,
    "isPublished": true,
    "isDefault": true
  },
  "status": "running",
  "currentNodeId": 2,
  "currentNodeName": "Review",
  "businessKey": null,
  "createdAt": "2026-09-09T10:00:00Z",
  "updatedAt": "2026-09-09T10:00:00Z"
}
```

### POST /api/instances/{id}/version-change/preview

Checks whether a running instance can move to a published version in the same workflow family.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**JSON body:** [PreviewInstanceVersionChangeRequest](#schema-previewinstanceversionchangerequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [InstanceVersionChangePreviewDto](#schema-instanceversionchangepreviewdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/instances/101/version-change/preview HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "targetWorkflowId": 12
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "instanceId": 101,
  "direction": "upgrade",
  "compatible": true,
  "blockers": [],
  "warnings": [],
  "expectedSourceWorkflowId": 11,
  "expectedUpdatedAt": "2026-09-09T10:00:00Z"
}
```

### POST /api/instances/{id}/version-change

Atomically moves a compatible running instance to another published workflow version.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**JSON body:** [ChangeInstanceVersionRequest](#schema-changeinstanceversionrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [ChangeInstanceVersionResultDto](#schema-changeinstanceversionresultdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/instances/101/version-change HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "targetWorkflowId": 12,
  "expectedSourceWorkflowId": 11,
  "expectedUpdatedAt": "2026-09-09T10:00:00Z",
  "reason": "Adopt compatible definition version"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "instance": {
    "id": 101,
    "workflow": {
      "id": 11,
      "workflowKey": "example-api-approval",
      "name": "API approval example",
      "version": 1,
      "isPublished": true,
      "isDefault": true
    },
    "status": "running",
    "currentNodeId": 2,
    "currentNodeName": "Review",
    "businessKey": null,
    "createdAt": "2026-09-09T10:00:00Z",
    "updatedAt": "2026-09-09T10:00:00Z"
  },
  "versionChange": {
    "id": 1,
    "instanceId": 1,
    "sourceWorkflow": {
      "id": 11,
      "name": "API approval example",
      "workflowKey": "example-api-approval",
      "version": 1,
      "isPublished": false,
      "isDefault": false,
      "createdAt": "2026-09-09T10:00:00Z"
    },
    "targetWorkflow": {
      "id": 11,
      "name": "API approval example",
      "workflowKey": "example-api-approval",
      "version": 1,
      "isPublished": false,
      "isDefault": false,
      "createdAt": "2026-09-09T10:00:00Z"
    },
    "direction": "upgrade",
    "changedBy": null,
    "changedByRoles": [],
    "reason": "example",
    "changedAt": "2026-09-09T10:00:00Z"
  }
}
```

### GET /api/instances/{id}/user-tasks

List user-task work items for an instance, filtered by task status.

**Access:** Task actor; personal task visibility is applied before paging. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |
| `status` | query | string | No | Optional task status (pending, active, completed, cancelled); omission lists all statuses. |
| `page` | query | integer (int32) | No | Default 1; see the endpoint family for numbered versus cursor paging. |
| `pageSize` | query | integer (int32) | No | Default 50; maximum 200. |

**Body:** none.

**Success:** `200` [PagedResultOfUserTaskDto](#schema-pagedresultofusertaskdto).
**Errors:** standard authentication/transport failures. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/instances/101/user-tasks?pageSize=50 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalCount": 0
}
```

### GET /api/instances/{id}/flows

Lists all sequence flows currently available to be taken from the resting node of a workflow instance.

**Access:** Task actor through legacy instance addressing. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | The database ID of the workflow instance. |

**Body:** none.

**Success:** `200` array of [SequenceFlowModel](#schema-sequenceflowmodel).
**Errors:** `401` Unauthorized; `404` Not Found; `409` Stale work or ambiguous legacy work address. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/instances/101/flows HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

[]
```

### POST /api/instances/{id}/claim

Claims a userTask within a workflow instance for the current authenticated actor.

A changed claim writes the same `taskClaim` history event as the [task-addressed claim](#post-apiuser-taskstaskidclaim). An unchanged retry writes no additional event.

**Access:** Task actor through legacy instance addressing. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | The database ID of the workflow instance. |

**Body:** none.

**Success:** `200` [InstanceDetailDto](#schema-instancedetaildto).
**Errors:** `400` Bad Request; `401` Unauthorized; `404` Not Found; `409` Stale work or ambiguous legacy work address. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/instances/101/claim HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "id": 101,
  "workflow": {
    "id": 11,
    "workflowKey": "example-api-approval",
    "name": "API approval example",
    "version": 1,
    "isPublished": true,
    "isDefault": true
  },
  "status": "running",
  "currentNodeId": 2,
  "currentNodeName": "Review",
  "businessKey": null,
  "createdAt": "2026-09-09T10:00:00Z",
  "updatedAt": "2026-09-09T10:00:00Z"
}
```

### POST /api/instances/{id}/unclaim

Releases a previously claimed userTask, returning it to the pool of unclaimed tasks.

A changed claim writes the same `taskClaim` history event as the [task-addressed unclaim](#post-apiuser-taskstaskidunclaim). An unchanged retry writes no additional event.

**Access:** Task actor through legacy instance addressing. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | The database ID of the workflow instance. |

**Body:** none.

**Success:** `200` [InstanceDetailDto](#schema-instancedetaildto).
**Errors:** `400` Bad Request; `401` Unauthorized; `404` Not Found; `409` Stale work or ambiguous legacy work address. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/instances/101/unclaim HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "id": 101,
  "workflow": {
    "id": 11,
    "workflowKey": "example-api-approval",
    "name": "API approval example",
    "version": 1,
    "isPublished": true,
    "isDefault": true
  },
  "status": "running",
  "currentNodeId": 2,
  "currentNodeName": "Review",
  "businessKey": null,
  "createdAt": "2026-09-09T10:00:00Z",
  "updatedAt": "2026-09-09T10:00:00Z"
}
```

### POST /api/instances/{id}/flows/{flowId}

Executes a transition flow from the current resting node to the next step.

**Access:** Task actor through legacy instance addressing. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | The database ID of the workflow instance. |
| `flowId` | path | integer (int32) | Yes | The unique integer ID of the sequence flow to transition along. |

**JSON body:** [TakeFlowRequest](#schema-takeflowrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [InstanceDetailDto](#schema-instancedetaildto).
**Errors:** `400` Bad Request; `401` Unauthorized; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/instances/101/flows/201 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "variables": {
    "reviewNote": "Approved after review"
  }
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "id": 101,
  "workflow": {
    "id": 11,
    "workflowKey": "example-api-approval",
    "name": "API approval example",
    "version": 1,
    "isPublished": true,
    "isDefault": true
  },
  "status": "completed",
  "currentNodeId": 3,
  "currentNodeName": "Approved",
  "businessKey": null,
  "createdAt": "2026-09-09T10:00:00Z",
  "updatedAt": "2026-09-09T10:00:00Z"
}
```

### POST /api/instances/{id}/cancel

Cancels an active workflow instance.

**Access:** Bearer plus an authored workflow cancelRoles match. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | The database ID of the workflow instance to cancel. |

**Body:** none.

**Success:** `204` without a response body.
**Errors:** `400` Bad Request; `401` Unauthorized; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

No request body. Cancels all active tokens, open work and waits atomically; an empty cancelRoles list does not grant cancellation.

```http
POST /api/instances/101/cancel HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response:

```http
HTTP/1.1 204 No Content
```

### POST /api/instances/{id}/message

Delivers an external system message payload to a workflow instance resting on an intermediate message catch event.

**Access:** Message client. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | The database ID of the workflow instance waiting for the message. |
| `catchEvent` | query | string | No | Optional exact, case-sensitive catch-event external ID. It is required when more than one active branch is waiting for a message on the instance. |
| `X-Client-Id` | header | string | Yes | Configured target-node credential. |
| `X-Client-Secret` | header | string | Yes | Configured target-node credential. |
| `authored correlation header` | header | string | Yes | Use message.headerName with the required value/headerValidation; add the configured idempotency header when enabled. |

**JSON body:** [JsonElement](#schema-jsonelement) (raw message payload; an empty body is also accepted before mapping validation). 

**Success:** `200` [MessageDeliveryAckDto](#schema-messagedeliveryackdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `404` Not Found; `409` Conflict; `413` Payload Too Large; `415` Unsupported Media Type. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/instances/101/message?catchEvent=WAIT_FOR_APPROVAL HTTP/1.1
Host: localhost:5017
X-Client-Id: example-client
X-Client-Secret: CLIENT_SECRET
X-Correlation-Id: REQ-DEMO-001
Content-Type: application/json

{
  "requestReference": "REQ-DEMO-001",
  "approved": true
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "id": 101,
  "currentNodeId": 3,
  "currentNodeName": "Approved",
  "currentNodeExternalId": null,
  "status": "completed",
  "updatedAt": "2026-09-09T10:00:00Z"
}
```

## User tasks

The inbox exposes exact `userTaskId` values. Fetch the personal task and honor `capabilities`; discover actions with `/flows`, then submit only the chosen flow inputs. An item with no available action can remain in its SQL inbox page with disabled capabilities. Visibility conditions are rechecked under the normal operation locks, and hidden personal-task operations return `404`. Claiming is separate from assignment: claim/unclaim have no JSON body; assignment uses the task’s exact `updatedAt`.

`manage` reads are visible to assignment managers or role managers for the relevant immutable definition; use returned `canManageAssignment` and `canManageRoles`. `status` accepts `active` (default), `pending`, or `open` (active and pending); `ownership` accepts `assigned`, `claimed`, or `unassigned`. An empty manager result is valid. Assignment endpoints require assignment-manager permission, while `/roles` requires role-manager permission independently.

Role replacement supplies the complete task role list and every editable selectable non-default action role list, with `expectedRolePolicyId`. Lists are limited to 100 entries and 300 Unicode scalars per trimmed role, normalized and deduplicated case-insensitively. Explicit empty lists are unrestricted. A role edit preserves ownership and completion history. For a multi-instance child, use the execution-level role endpoint rather than the normal-task endpoint.

### GET /api/user-tasks/manage

List open tasks visible to assignment or role managers.

**Access:** Task assignment manager or task role manager (results filtered by definition). See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `taskId` | query | integer (int64) | No | Optional exact selector; omission does not add this filter. |
| `instanceId` | query | integer (int64) | No | Optional exact selector; omission does not add this filter. |
| `workflowId` | query | integer (int64) | No | Optional exact selector; omission does not add this filter. |
| `workflowKey` | query | string | No | Stable authored workflow-family key; URL-encode. |
| `businessKey` | query | string | No | Optional exact selector; omission does not add this filter. |
| `nodeId` | query | integer (int32) | No | Optional exact selector; omission does not add this filter. |
| `nodeExternalId` | query | string | No | Optional exact selector; omission does not add this filter. |
| `owner` | query | string | No | Optional owner username filter. |
| `ownership` | query | string | No | assigned, claimed, or unassigned. |
| `status` | query | string | No | active (default), pending, or open. |
| `var` | query | array of string | No | Repeated `var=name:value` filter. Exact, case-insensitive match on an instance variable's latest scalar value. Multiple entries are AND-combined. Array/object variables never match. |
| `page` | query | integer (int32) | No | Default 1; see the endpoint family for numbered versus cursor paging. |
| `pageSize` | query | integer (int32) | No | Default 50; maximum 200. |

**Body:** none.

**Success:** `200` [PagedResultOfManagedUserTaskDto](#schema-pagedresultofmanagedusertaskdto).
**Errors:** `400` Bad Request; `401` Unauthorized. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/user-tasks/manage?pageSize=50 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalCount": 0
}
```

### POST /api/user-tasks/manage/search

Search manageable user tasks with advanced variable predicates.

**Access:** Task assignment manager or task role manager (results filtered by definition). See [authentication](#http-conventions-and-authentication).

**Parameters:** none.

**JSON body:** [ManageableUserTaskSearchRequest](#schema-manageableusertasksearchrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [PagedResultOfManagedUserTaskDto](#schema-pagedresultofmanagedusertaskdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `413` Payload Too Large; `415` Unsupported Media Type. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/user-tasks/manage/search HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "workflowKey": "example-api-approval",
  "status": "open",
  "ownership": "unassigned",
  "variableFilter": {
    "requestReference": {
      "$exists": true
    }
  },
  "page": 1,
  "pageSize": 50
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalCount": 0
}
```

### GET /api/user-tasks/{taskId}

Read one personal user task and the caller’s capabilities.

**Access:** Task actor. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `taskId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**Body:** none.

**Success:** `200` [UserTaskDto](#schema-usertaskdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `404` Not Found. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/user-tasks/301 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "id": 301,
  "instanceId": 101,
  "tokenId": 201,
  "nodeId": 2,
  "nodeName": "Review",
  "roles": [
    "Reviewer"
  ],
  "requiresClaim": true,
  "status": "active",
  "claimedBy": "reviewer@example.com",
  "capabilities": {
    "claimedByMe": true,
    "canClaim": false,
    "canUnclaim": true,
    "canAct": true
  },
  "updatedAt": "2026-09-09T10:00:00Z"
}
```

### GET /api/user-tasks/{taskId}/flows

Discover currently available selectable actions for a personal task.

**Access:** Task actor. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `taskId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**Body:** none.

**Success:** `200` array of [SequenceFlowModel](#schema-sequenceflowmodel).
**Errors:** `401` Unauthorized; `404` Not Found. See [error envelopes](#errors-and-concurrency) and the family rules above.

Only actionable selectable flows are returned; engine-only multi-instance fallback/outcome flows are omitted. Discover again after claiming or refreshing state.

```http
GET /api/user-tasks/301/flows HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

[
  {
    "id": 201,
    "name": "Approve",
    "sourceRef": 2,
    "targetRef": 3,
    "roles": [
      "Reviewer"
    ],
    "variables": [
      {
        "id": 2,
        "name": "reviewNote",
        "dataType": "string",
        "isArray": false,
        "nullable": false,
        "required": true
      }
    ],
    "condition": null,
    "isDefault": false,
    "isSelectable": true
  }
]
```

### POST /api/user-tasks/{taskId}/claim

Claim an active pooled task for the authenticated actor.

A successful ownership change appends one `taskClaim` instance-history row in the same transaction. Its payload includes `operation: "claimed"`, `previousClaimedBy`, `newClaimedBy`, and `authority: "user"`. The row has no sequence-flow ID and its source and target are the task node. An already-owned retry, denied request, or losing concurrent claim does not create a history row.

**Access:** Task actor. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `taskId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**Body:** none.

**Success:** `200` [UserTaskDto](#schema-usertaskdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/user-tasks/301/claim HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "id": 301,
  "instanceId": 101,
  "tokenId": 201,
  "nodeId": 2,
  "nodeName": "Review",
  "roles": [
    "Reviewer"
  ],
  "requiresClaim": true,
  "status": "active",
  "claimedBy": "reviewer@example.com",
  "capabilities": {
    "claimedByMe": true,
    "canClaim": false,
    "canUnclaim": true,
    "canAct": true
  },
  "updatedAt": "2026-09-09T10:00:00Z"
}
```

### POST /api/user-tasks/{taskId}/unclaim

Release a claim as its owner or an authorized recovery actor.

A successful release appends one `taskClaim` history row with `operation: "unclaimed"`, `previousClaimedBy`, `newClaimedBy: null`, and authority `user`, `unclaimOverride`, or `userDelegation`. Delegated releases retain the actual caller in `performedBy` and `actorClaims`, with the represented owner in `actingFor` and grant in `delegationId`. Releasing an already unclaimed task writes no additional row. Assignment-related claim clearing remains part of the existing `taskAssignment` event.

**Access:** Task actor. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `taskId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**Body:** none.

**Success:** `200` [UserTaskDto](#schema-usertaskdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/user-tasks/301/unclaim HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "id": 301,
  "instanceId": 101,
  "tokenId": 201,
  "nodeId": 2,
  "nodeName": "Review",
  "roles": [
    "Reviewer"
  ],
  "requiresClaim": true,
  "status": "active",
  "claimedBy": null,
  "capabilities": {
    "claimedByMe": false,
    "canClaim": true,
    "canUnclaim": false,
    "canAct": false
  },
  "updatedAt": "2026-09-09T10:00:00Z"
}
```

### POST /api/user-tasks/{taskId}/assign

Assign or reassign waiting work to the requested actor.

**Access:** Task assignment manager. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `taskId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**JSON body:** [AssignUserTaskRequest](#schema-assignusertaskrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [UserTaskAssignmentAckDto](#schema-usertaskassignmentackdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/user-tasks/301/assign HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "actorId": "reviewer@example.com",
  "expectedUpdatedAt": "2026-09-09T10:00:00Z",
  "reason": "Assign regional reviewer"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "userTaskId": 301,
  "instanceId": 101,
  "operation": "assigned",
  "previousOwnership": "unassigned",
  "previousOwner": null,
  "currentOwnership": "assigned",
  "currentOwner": "reviewer@example.com",
  "requiresClaim": true,
  "requiresAssignment": false,
  "changed": true,
  "updatedAt": "2026-09-09T10:00:00Z"
}
```

### POST /api/user-tasks/{taskId}/unassign

Remove a waiting task’s explicit owner through assignment management.

**Access:** Task assignment manager. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `taskId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**JSON body:** [UnassignUserTaskRequest](#schema-unassignusertaskrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [UserTaskAssignmentAckDto](#schema-usertaskassignmentackdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/user-tasks/301/unassign HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "expectedUpdatedAt": "2026-09-09T10:00:00Z",
  "reason": "Return to assignment pool"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "userTaskId": 301,
  "instanceId": 101,
  "operation": "unassigned",
  "previousOwnership": "assigned",
  "previousOwner": "reviewer@example.com",
  "currentOwnership": "unassigned",
  "currentOwner": null,
  "requiresClaim": true,
  "requiresAssignment": false,
  "changed": true,
  "updatedAt": "2026-09-09T10:00:00Z"
}
```

### GET /api/user-tasks/{taskId}/roles

Read the immutable effective role policy of a normal waiting task.

**Access:** Task role manager. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `taskId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**Body:** none.

**Success:** `200` [UserTaskRolePolicyDto](#schema-usertaskrolepolicydto).
**Errors:** `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/user-tasks/301/roles HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "instanceId": 101,
  "tokenId": 201,
  "nodeId": 2,
  "nodeName": "Review",
  "userTaskId": 301,
  "multiInstanceExecutionId": null,
  "rolePolicyId": 501,
  "roles": [
    "Reviewer"
  ],
  "flows": [
    {
      "flowId": 201,
      "name": "Approve",
      "roles": [
        "Reviewer"
      ]
    }
  ],
  "activeTaskCount": 1,
  "pendingTaskCount": 0
}
```

### POST /api/user-tasks/{taskId}/roles

Replace the role snapshot of a waiting normal user task.

**Access:** Task role manager. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `taskId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**JSON body:** [ChangeUserTaskRolesRequest](#schema-changeusertaskrolesrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [UserTaskRolesChangeAckDto](#schema-usertaskroleschangeackdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/user-tasks/301/roles HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "expectedRolePolicyId": 501,
  "roles": [
    "Reviewer"
  ],
  "flows": [
    {
      "flowId": 201,
      "roles": [
        "Reviewer"
      ]
    }
  ],
  "reason": "Update review team"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "changed": true,
  "policy": {
    "instanceId": 101,
    "tokenId": 201,
    "nodeId": 2,
    "nodeName": "Review",
    "userTaskId": 301,
    "multiInstanceExecutionId": null,
    "rolePolicyId": 502,
    "roles": [
      "Reviewer"
    ],
    "flows": [
      {
        "flowId": 201,
        "name": "Approve",
        "roles": [
          "Reviewer"
        ]
      }
    ],
    "activeTaskCount": 1,
    "pendingTaskCount": 0
  }
}
```

### POST /api/user-tasks/{taskId}/flows/{flowId}

Complete this exact work item by taking a discovered flow.

**Access:** Task actor. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `taskId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |
| `flowId` | path | integer (int32) | Yes | Required route identifier; use the actual returned ID. |

**JSON body:** [TakeFlowRequest](#schema-takeflowrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [UserTaskActionAckDto](#schema-usertaskactionackdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/user-tasks/301/flows/201 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "variables": {
    "reviewNote": "Approved after review"
  }
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "userTaskId": 301,
  "instanceId": 101,
  "taskStatus": "completed",
  "instanceStatus": "completed",
  "selectedFlowId": 201,
  "currentNodeId": 3,
  "currentNodeName": "Approved",
  "currentNodeExternalId": null,
  "multiInstance": null,
  "updatedAt": "2026-09-09T10:00:00Z"
}
```

## Multi-instance executions

These are parent-execution endpoints. `/flows` discovers authorized immediate interrupts and taking one requires both captured node and action roles but no child assignment or claim. An interrupt cancels unfinished children and advances the parent once; stale competing actions return `409`. Ordinary votes use `/api/user-tasks/{taskId}/flows/{flowId}`. Role reads/edits require task-role-manager permission and replace the parent plus every unfinished active/pending child atomically; completed child history is preserved. The same complete-list and optimistic-policy rules apply as for normal tasks.

### GET /api/multi-instance-executions/{executionId}/flows

Discover parent-level interrupt actions for an active multi-instance execution.

**Access:** Task actor; parent interrupts do not require child ownership/claim. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `executionId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**Body:** none.

**Success:** `200` array of [SequenceFlowModel](#schema-sequenceflowmodel).
**Errors:** `401` Unauthorized; `404` Not Found. See [error envelopes](#errors-and-concurrency) and the family rules above.

Requires both node and interrupt-flow roles; no active assigned child or claim is needed.

```http
GET /api/multi-instance-executions/401/flows HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

[]
```

### POST /api/multi-instance-executions/{executionId}/flows/{flowId}

Take an immediate parent interrupt and cancel unfinished children.

**Access:** Task actor; parent interrupts do not require child ownership/claim. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `executionId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |
| `flowId` | path | integer (int32) | Yes | Required route identifier; use the actual returned ID. |

**JSON body:** [TakeFlowRequest](#schema-takeflowrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [InstanceDetailDto](#schema-instancedetaildto).
**Errors:** `400` Bad Request; `401` Unauthorized; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

Success returns the updated instance detail. This interrupts the parent, unlike a child vote.

```http
POST /api/multi-instance-executions/401/flows/201 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "variables": {
    "reviewNote": "Approved after review"
  }
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "id": 101,
  "workflow": {
    "id": 11,
    "workflowKey": "example-api-approval",
    "name": "API approval example",
    "version": 1,
    "isPublished": true,
    "isDefault": true
  },
  "status": "completed",
  "currentNodeId": 3,
  "currentNodeName": "Approved",
  "businessKey": null,
  "createdAt": "2026-09-09T10:00:00Z",
  "updatedAt": "2026-09-09T10:00:00Z"
}
```

### GET /api/multi-instance-executions/{executionId}/roles

Read the effective role policy shared by unfinished multi-instance work.

**Access:** Task role manager. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `executionId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**Body:** none.

**Success:** `200` [UserTaskRolePolicyDto](#schema-usertaskrolepolicydto).
**Errors:** `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/multi-instance-executions/401/roles HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "instanceId": 101,
  "tokenId": 201,
  "nodeId": 2,
  "nodeName": "Review",
  "userTaskId": 301,
  "multiInstanceExecutionId": null,
  "rolePolicyId": 501,
  "roles": [
    "Reviewer"
  ],
  "flows": [
    {
      "flowId": 201,
      "name": "Approve",
      "roles": [
        "Reviewer"
      ]
    }
  ],
  "activeTaskCount": 1,
  "pendingTaskCount": 0
}
```

### POST /api/multi-instance-executions/{executionId}/roles

Replace roles for a waiting multi-instance execution and all unfinished work items.

**Access:** Task role manager. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `executionId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**JSON body:** [ChangeUserTaskRolesRequest](#schema-changeusertaskrolesrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [UserTaskRolesChangeAckDto](#schema-usertaskroleschangeackdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/multi-instance-executions/401/roles HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "expectedRolePolicyId": 501,
  "roles": [
    "Reviewer"
  ],
  "flows": [
    {
      "flowId": 201,
      "roles": [
        "Reviewer"
      ]
    }
  ],
  "reason": "Update review team"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "changed": true,
  "policy": {
    "instanceId": 101,
    "tokenId": 201,
    "nodeId": 2,
    "nodeName": "Review",
    "userTaskId": 301,
    "multiInstanceExecutionId": null,
    "rolePolicyId": 502,
    "roles": [
      "Reviewer"
    ],
    "flows": [
      {
        "flowId": 201,
        "name": "Approve",
        "roles": [
          "Reviewer"
        ]
      }
    ],
    "activeTaskCount": 1,
    "pendingTaskCount": 0
  }
}
```

## Task distribution

Authenticate using the workflow family’s configured distribution credentials. The `workflowKey` route boundary cannot be overridden by body/query data; malformed or unauthorized credentials return `401`, and unavailable family/task results can return `404`. These operations bypass personal inbox visibility and do not impersonate a task actor. Lists cover distributable active tasks; selectors include exact task/instance/version, owner and ownership (`assigned`, `claimed`, `unassigned`). Assignment/unassignment require the last returned `updatedAt` and never complete work. Optional `includeVariables` defaults to false.

### GET /api/task-distribution/workflows/{workflowKey}/tasks

List tasks available to the authenticated workflow-family distributor.

**Access:** Distributor. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `workflowKey` | path | string | Yes | Stable authored workflow-family key; URL-encode. |
| `X-Client-Id` | header | string | Yes | Required configured workflow-family distributor credential. |
| `X-Client-Secret` | header | string | Yes | Required configured workflow-family distributor credential. |
| `taskId` | query | integer (int64) | No | Optional exact selector; omission does not add this filter. |
| `instanceId` | query | integer (int64) | No | Optional exact selector; omission does not add this filter. |
| `workflowId` | query | integer (int64) | No | Optional exact selector; omission does not add this filter. |
| `businessKey` | query | string | No | Optional exact selector; omission does not add this filter. |
| `nodeId` | query | integer (int32) | No | Optional exact selector; omission does not add this filter. |
| `nodeExternalId` | query | string | No | Optional exact selector; omission does not add this filter. |
| `owner` | query | string | No | Optional owner username filter. |
| `ownership` | query | string | No | assigned, claimed, or unassigned. |
| `var` | query | array of string | No | Repeated `var=name:value` filter. Exact, case-insensitive match on an instance variable's latest scalar value. Multiple entries are AND-combined. Array/object variables never match. |
| `includeVariables` | query | boolean | No | When true, each returned item includes a `variables` object containing the latest value for every instance variable. Defaults to false. |
| `page` | query | integer (int32) | No | Default 1; see the endpoint family for numbered versus cursor paging. |
| `pageSize` | query | integer (int32) | No | Default 50; maximum 200. |

**Body:** none.

**Success:** `200` [PagedResultOfManagedUserTaskDto](#schema-pagedresultofmanagedusertaskdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `404` Not Found. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/task-distribution/workflows/example-api-approval/tasks?pageSize=50 HTTP/1.1
Host: localhost:5017
X-Client-Id: example-client
X-Client-Secret: CLIENT_SECRET
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalCount": 0
}
```

### POST /api/task-distribution/workflows/{workflowKey}/tasks/search

Search distributable tasks with advanced variable predicates.

**Access:** Distributor. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `workflowKey` | path | string | Yes | Stable authored workflow-family key; URL-encode. |
| `X-Client-Id` | header | string | Yes | Required configured workflow-family distributor credential. |
| `X-Client-Secret` | header | string | Yes | Required configured workflow-family distributor credential. |

**JSON body:** [DistributableUserTaskSearchRequest](#schema-distributableusertasksearchrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [PagedResultOfManagedUserTaskDto](#schema-pagedresultofmanagedusertaskdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `404` Not Found; `413` Payload Too Large; `415` Unsupported Media Type. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/task-distribution/workflows/example-api-approval/tasks/search HTTP/1.1
Host: localhost:5017
X-Client-Id: example-client
X-Client-Secret: CLIENT_SECRET
Content-Type: application/json

{
  "ownership": "unassigned",
  "variableFilter": {
    "requestReference": {
      "$exists": true
    }
  },
  "includeVariables": true,
  "page": 1,
  "pageSize": 50
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalCount": 0
}
```

### POST /api/task-distribution/workflows/{workflowKey}/tasks/{taskId}/assign

Assign or reassign a task within the authenticated distribution family.

**Access:** Distributor. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `workflowKey` | path | string | Yes | Stable authored workflow-family key; URL-encode. |
| `taskId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |
| `X-Client-Id` | header | string | Yes | Required configured workflow-family distributor credential. |
| `X-Client-Secret` | header | string | Yes | Required configured workflow-family distributor credential. |

**JSON body:** [AssignUserTaskRequest](#schema-assignusertaskrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [UserTaskAssignmentAckDto](#schema-usertaskassignmentackdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/task-distribution/workflows/example-api-approval/tasks/301/assign HTTP/1.1
Host: localhost:5017
X-Client-Id: example-client
X-Client-Secret: CLIENT_SECRET
Content-Type: application/json

{
  "actorId": "reviewer@example.com",
  "expectedUpdatedAt": "2026-09-09T10:00:00Z",
  "reason": "Assign regional reviewer"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "userTaskId": 301,
  "instanceId": 101,
  "operation": "assigned",
  "previousOwnership": "unassigned",
  "previousOwner": null,
  "currentOwnership": "assigned",
  "currentOwner": "reviewer@example.com",
  "requiresClaim": true,
  "requiresAssignment": false,
  "changed": true,
  "updatedAt": "2026-09-09T10:00:00Z"
}
```

### POST /api/task-distribution/workflows/{workflowKey}/tasks/{taskId}/unassign

Unassign a task within the authenticated distribution family.

**Access:** Distributor. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `workflowKey` | path | string | Yes | Stable authored workflow-family key; URL-encode. |
| `taskId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |
| `X-Client-Id` | header | string | Yes | Required configured workflow-family distributor credential. |
| `X-Client-Secret` | header | string | Yes | Required configured workflow-family distributor credential. |

**JSON body:** [UnassignUserTaskRequest](#schema-unassignusertaskrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [UserTaskAssignmentAckDto](#schema-usertaskassignmentackdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/task-distribution/workflows/example-api-approval/tasks/301/unassign HTTP/1.1
Host: localhost:5017
X-Client-Id: example-client
X-Client-Secret: CLIENT_SECRET
Content-Type: application/json

{
  "expectedUpdatedAt": "2026-09-09T10:00:00Z",
  "reason": "Return to assignment pool"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "userTaskId": 301,
  "instanceId": 101,
  "operation": "unassigned",
  "previousOwnership": "assigned",
  "previousOwner": "reviewer@example.com",
  "currentOwnership": "unassigned",
  "currentOwner": null,
  "requiresClaim": true,
  "requiresAssignment": false,
  "changed": true,
  "updatedAt": "2026-09-09T10:00:00Z"
}
```

## Node execution activity

Visibility is SQL-filtered by immutable workflow version before count/order/page: a caller needs its assignment-manager role or the deployment node-activity role. No visible versions means an empty page; an out-of-scope detail is `404`. This grants no mutation rights. Search the committed lifecycle ledger from its deployment cutover, not a reconstructed history of all earlier visits.

Statuses: `pending`, `active`, `completed`, `cancelled`, `faulted`, `merged`. Execution kinds: `node`, `userTaskItem`. Repeated node types, statuses, instance statuses, and completion reasons are OR-combined within their group; different groups are AND-combined. Date `From` bounds are inclusive and `To` bounds exclusive; duration bounds are milliseconds and nonnegative. IDs must be positive; `itemIndex` may be zero. Sort fields: `id`, `instanceId`, `workflowId`, `nodeId`, `createdAt`, `startedAt`, `updatedAt`, `completedAt`, `duration`/`durationMilliseconds`; default `updatedAt DESC, id DESC`, nulls last. Detail variable changes are attributed to that execution, while search filters use the instance’s latest values. Completion-reason vocabulary is listed in the schema notes below.

### GET /api/node-executions

Search node executions across workflow versions and instances.

**Access:** Node-activity reader. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `executionId` | query | integer (int64) | No | Optional exact selector; omission does not add this filter. |
| `instanceId` | query | integer (int64) | No | Optional exact selector; omission does not add this filter. |
| `workflowId` | query | integer (int64) | No | Optional exact selector; omission does not add this filter. |
| `workflowKey` | query | string | No | Stable authored workflow-family key; URL-encode. |
| `workflowVersion` | query | integer (int32) | No | Optional exact selector; omission does not add this filter. |
| `businessKey` | query | string | No | Optional exact selector; omission does not add this filter. |
| `tokenId` | query | integer (int64) | No | Optional exact selector; omission does not add this filter. |
| `userTaskId` | query | integer (int64) | No | Optional exact selector; omission does not add this filter. |
| `multiInstanceExecutionId` | query | integer (int64) | No | Optional exact selector; omission does not add this filter. |
| `gatewayBranchId` | query | integer (int64) | No | Optional exact selector; omission does not add this filter. |
| `itemIndex` | query | integer (int32) | No | Optional exact selector; omission does not add this filter. |
| `executionKind` | query | string | No | Optional exact selector; omission does not add this filter. |
| `nodeId` | query | integer (int32) | No | Optional exact selector; omission does not add this filter. |
| `nodeName` | query | string | No | Optional exact selector; omission does not add this filter. |
| `nodeExternalId` | query | string | No | Optional exact selector; omission does not add this filter. |
| `nodeType` | query | array of string | No | Optional repeated filter; see family semantics. |
| `status` | query | array of string | No | Optional lifecycle status filter; see family vocabulary. |
| `instanceStatus` | query | array of string | No | Optional repeated filter; see family semantics. |
| `completionReason` | query | array of string | No | Optional repeated filter; see family semantics. |
| `isMultiInstance` | query | boolean | No | Optional exact selector; omission does not add this filter. |
| `isCutoverSeeded` | query | boolean | No | Optional exact selector; omission does not add this filter. |
| `owner` | query | string | No | Optional owner username filter. |
| `startedBy` | query | string | No | Optional exact selector; omission does not add this filter. |
| `completedBy` | query | string | No | Optional exact selector; omission does not add this filter. |
| `enteredViaFlowId` | query | integer (int32) | No | Optional exact selector; omission does not add this filter. |
| `selectedFlowId` | query | integer (int32) | No | Optional exact selector; omission does not add this filter. |
| `exitedViaFlowId` | query | integer (int32) | No | Optional exact selector; omission does not add this filter. |
| `aggregateFlowId` | query | integer (int32) | No | Optional exact selector; omission does not add this filter. |
| `createdFrom` | query | string (date-time) | No | Optional inclusive ISO 8601 timestamp bound. |
| `createdTo` | query | string (date-time) | No | Optional exclusive ISO 8601 timestamp bound. |
| `startedFrom` | query | string (date-time) | No | Optional inclusive ISO 8601 timestamp bound. |
| `startedTo` | query | string (date-time) | No | Optional exclusive ISO 8601 timestamp bound. |
| `updatedFrom` | query | string (date-time) | No | Optional inclusive ISO 8601 timestamp bound. |
| `updatedTo` | query | string (date-time) | No | Optional exclusive ISO 8601 timestamp bound. |
| `completedFrom` | query | string (date-time) | No | Optional inclusive ISO 8601 timestamp bound. |
| `completedTo` | query | string (date-time) | No | Optional exclusive ISO 8601 timestamp bound. |
| `minDurationMilliseconds` | query | integer (int64) | No | Optional exact selector; omission does not add this filter. |
| `maxDurationMilliseconds` | query | integer (int64) | No | Optional exact selector; omission does not add this filter. |
| `var` | query | array of string | No | Repeated `var=name:value` filter. Exact, case-insensitive match on an instance variable's latest scalar value. Multiple entries are AND-combined. Array/object variables never match. |
| `sort` | query | array of string | No | Repeat field:asc or field:desc, at most three; allowed fields depend on endpoint. |
| `page` | query | integer (int32) | No | Default 1; see the endpoint family for numbered versus cursor paging. |
| `pageSize` | query | integer (int32) | No | Default 50; maximum 200. |

**Body:** none.

**Success:** `200` [PagedResultOfNodeExecutionSummaryDto](#schema-pagedresultofnodeexecutionsummarydto).
**Errors:** `400` Bad Request; `401` Unauthorized. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/node-executions?pageSize=50 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalCount": 0
}
```

### POST /api/node-executions/search

Search node executions with advanced variable predicates.

**Access:** Node-activity reader. See [authentication](#http-conventions-and-authentication).

**Parameters:** none.

**JSON body:** [NodeExecutionSearchBodyRequest](#schema-nodeexecutionsearchbodyrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [PagedResultOfNodeExecutionSummaryDto](#schema-pagedresultofnodeexecutionsummarydto).
**Errors:** `400` Bad Request; `401` Unauthorized; `413` Payload Too Large; `415` Unsupported Media Type. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/node-executions/search HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "workflowKey": "example-api-approval",
  "statuses": [
    "completed"
  ],
  "variableFilter": {
    "requestReference": {
      "$eq": "REQ-DEMO-001"
    }
  },
  "sort": [
    {
      "field": "updatedAt",
      "direction": "desc"
    }
  ],
  "page": 1,
  "pageSize": 50
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalCount": 0
}
```

### GET /api/node-executions/{id}

Get one authorized node execution.

Detail includes `startedByClaims` and `completedByClaims`, independent snapshots for the visit's causal starting and completing actors. They use the same claim-name-to-string-array shape and null/empty distinction as history `actorClaims`. Claims are detail-only: node-activity list/search results do not include them. A claim or unclaim does not replace either visit snapshot. See [audit configuration and retention](deployment.md#selected-claim-audit).

**Access:** Node-activity reader. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**Body:** none.

**Success:** `200` [NodeExecutionDetailDto](#schema-nodeexecutiondetaildto).
**Errors:** `400` Bad Request; `401` Unauthorized; `404` Not Found. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/node-executions/101 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "id": 101,
  "instanceId": 101,
  "workflowId": 11,
  "workflowKey": "example-api-approval",
  "workflowName": "API approval example",
  "workflowVersion": 1,
  "tokenId": 201,
  "userTaskId": 301,
  "executionKind": "node",
  "nodeId": 2,
  "nodeName": "Review",
  "nodeType": "userTask",
  "status": "completed",
  "instanceStatus": "completed",
  "completionReason": "userAction",
  "isMultiInstance": false,
  "selectedFlowId": 201,
  "variableChanges": [],
  "createdAt": "2026-09-09T10:00:00Z",
  "updatedAt": "2026-09-09T10:00:00Z",
  "isCutoverSeeded": false
}
```

## Jobs and incidents

Every route in this section requires the Job operator role, including endpoints whose OpenAPI metadata does not repeat `401`/`403`. Lists and attempts use opaque cursors. Operational DTOs expose bounded metadata and failure summaries, not job snapshots or persisted external-response bodies. A resolved incident may remain after its completed job has been pruned, so `IncidentDetailDto.job` may be null.

Job statuses: `queued`, `running`, `resultReady`, `retry`, `completed`, `incident`, `cancelled`, `skipped`. Kinds: `asyncBefore`, `asyncAfter`, `timer`, `timerStart`, `timerBoundary`, `conditionalWake`, `administrativeBatchPrepare`, `administrativeBatchExecute`, `instanceVersionChangeBatchPrepare`, `instanceVersionChangeBatchExecute`, `instanceVariableUpdateBatchPrepare`, `instanceVariableUpdateBatchExecute`. Incident statuses: `open`, `resolved`; `type` is the recorded failure category, including `automatic_loop_limit`. Repeated filters are supplied as repeated query parameters. Retry fences the original activation and queues work; it does not immediately finish the workflow. A no-longer-retryable incident/job returns `409`.

### GET /api/jobs

Search durable workflow jobs.

**Access:** Job operator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `instanceId` | query | integer (int64) | No | Optional exact selector; omission does not add this filter. |
| `workflowId` | query | integer (int64) | No | Optional exact selector; omission does not add this filter. |
| `workflowKey` | query | string | No | Stable authored workflow-family key; URL-encode. |
| `tokenId` | query | integer (int64) | No | Optional exact selector; omission does not add this filter. |
| `status` | query | array of string | No | Optional lifecycle status filter; see family vocabulary. |
| `kind` | query | array of string | No | Optional repeated filter; see family semantics. |
| `dueFrom` | query | string (date-time) | No | Optional inclusive ISO 8601 timestamp bound. |
| `dueTo` | query | string (date-time) | No | Optional exclusive ISO 8601 timestamp bound. |
| `cursor` | query | string | No | Opaque previous response nextCursor; omit for first page. |
| `page` | query | integer (int32) | No | Default 1; see the endpoint family for numbered versus cursor paging. |
| `pageSize` | query | integer (int32) | No | Default 50; maximum 200. |

**Body:** none.

**Success:** `200` [PagedResultOfJobSummaryDto](#schema-pagedresultofjobsummarydto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/jobs?pageSize=50 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalCount": 0
}
```

### GET /api/jobs/statistics

Get durable workflow queue statistics.

**Access:** Job operator. See [authentication](#http-conventions-and-authentication).

**Parameters:** none.

**Body:** none.

**Success:** `200` [JobQueueStatisticsDto](#schema-jobqueuestatisticsdto).
**Errors:** `400` Invalid query or identifier; `401` Unauthorized; `403` Forbidden. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/jobs/statistics HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "runnableDepth": 0,
  "oldestRunnableDueAt": null,
  "queueLagSeconds": 0,
  "timerControlRunnableCount": 0,
  "activeLeaseCount": 0,
  "openIncidentCount": 0,
  "observedAt": "2026-09-09T10:00:00Z"
}
```

### GET /api/jobs/{jobId}

Get durable workflow job metadata.

**Access:** Job operator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `jobId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**Body:** none.

**Success:** `200` [JobDetailDto](#schema-jobdetaildto).
**Errors:** `400` Invalid query or identifier; `401` Unauthorized; `403` Job operator role required; `404` Not Found. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/jobs/801 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "summary": {
    "id": 801,
    "instanceId": 101,
    "workflowDefinitionId": 11,
    "workflowKey": "example-api-approval",
    "tokenId": 201,
    "nodeId": 4,
    "nodeName": "Wait 30 seconds",
    "nodeType": "intermediateTimerCatchEvent",
    "kind": "timer",
    "phase": "timer",
    "queueClass": "control",
    "status": "queued",
    "attemptCount": 0,
    "dueAt": "2026-09-09T10:00:30Z",
    "leaseExpiresAt": null,
    "incidentId": null,
    "createdAt": "2026-09-09T10:00:00Z",
    "updatedAt": "2026-09-09T10:00:00Z",
    "completedAt": null
  },
  "activationId": "11111111-1111-4111-8111-111111111111",
  "workerId": null,
  "leaseGeneration": 1,
  "startedAt": "2026-09-09T10:00:00Z",
  "resultReadyAt": "2026-09-09T10:00:00Z",
  "lastFailureCode": null,
  "lastFailureDescription": null
}
```

### GET /api/jobs/{jobId}/attempts

List bounded attempt history for one job.

**Access:** Job operator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `jobId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |
| `cursor` | query | string | No | Opaque previous response nextCursor; omit for first page. |
| `pageSize` | query | integer (int32) | No | Default 50; maximum 200. |

**Body:** none.

**Success:** `200` [PagedResultOfJobAttemptDto](#schema-pagedresultofjobattemptdto).
**Errors:** `400` Invalid query or identifier; `401` Unauthorized; `403` Job operator role required. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/jobs/801/attempts?pageSize=50 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalCount": 0
}
```

### GET /api/incidents

Search workflow incidents.

**Access:** Job operator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `instanceId` | query | integer (int64) | No | Optional exact selector; omission does not add this filter. |
| `workflowId` | query | integer (int64) | No | Optional exact selector; omission does not add this filter. |
| `workflowKey` | query | string | No | Stable authored workflow-family key; URL-encode. |
| `status` | query | array of string | No | Optional lifecycle status filter; see family vocabulary. |
| `type` | query | array of string | No | Optional repeated filter; see family semantics. |
| `cursor` | query | string | No | Opaque previous response nextCursor; omit for first page. |
| `page` | query | integer (int32) | No | Default 1; see the endpoint family for numbered versus cursor paging. |
| `pageSize` | query | integer (int32) | No | Default 50; maximum 200. |

**Body:** none.

**Success:** `200` [PagedResultOfIncidentSummaryDto](#schema-pagedresultofincidentsummarydto).
**Errors:** `400` Invalid query or identifier; `401` Unauthorized; `403` Job operator role required. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/incidents?pageSize=50 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalCount": 0
}
```

### GET /api/incidents/{incidentId}

Get one workflow incident.

**Access:** Job operator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `incidentId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**Body:** none.

**Success:** `200` [IncidentDetailDto](#schema-incidentdetaildto).
**Errors:** `400` Invalid query or identifier; `401` Unauthorized; `403` Job operator role required; `404` Not Found. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/incidents/901 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "summary": {
    "id": 901,
    "jobId": 801,
    "instanceId": 101,
    "workflowDefinitionId": 11,
    "workflowKey": "example-api-approval",
    "nodeId": 4,
    "nodeName": "Automatic continuation",
    "type": "automatic_loop_limit",
    "status": "open",
    "summary": "Automatic transition hop limit reached",
    "createdAt": "2026-09-09T10:00:00Z",
    "updatedAt": "2026-09-09T10:00:00Z",
    "resolvedAt": null
  },
  "job": null,
  "details": null,
  "resolvedBy": null
}
```

### POST /api/incidents/{incidentId}/retry

Resolve an incident by queueing its fenced job for retry.

**Access:** Job operator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `incidentId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**Body:** none.

**Success:** `200` [RetryIncidentResultDto](#schema-retryincidentresultdto).
**Errors:** `400` Invalid query or identifier; `401` Unauthorized; `403` Job operator role required; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/incidents/901/retry HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "incidentId": 901,
  "jobId": 801,
  "incidentStatus": "resolved",
  "jobStatus": "queued",
  "dueAt": "2026-09-09T10:00:00Z"
}
```

## Instance administrative actions

Use these routes to discover active human-task positions for one instance and immediately execute a selectable authored action. **Access:** workflow administrator with a nonblank actor. Authentication and the current `Workflow.RequiredRole` permission are checked for discovery and execution. Ordinary inbox, task-addressed, legacy instance-action, and multi-instance interrupt routes keep their existing permissions; a request cannot enable an override by supplying an administrator flag or audit identifiers.

Direct overrides ignore task/action roles, assignment, claims, inbox visibility, and the selected flow's condition. They preserve declared required/type/array/input validation, downstream routing, and runtime limits. Default and engine-only flows cannot be selected. Parallel positions are addressed separately; an authored continuation may still cancel a broader scope. Timer-boundary overrides use [administrative batches](#administrative-action-batches).

For multi-instance parents, `forceParent` cancels unfinished children without recording votes, then traverses the selected flow once. `completeAllChildren` completes every unfinished active/pending child with the same selected flow and inputs, records their administrative results, suppresses aggregate routing until all are closed, then traverses that selected flow once. Both modes preserve previously completed children. The current affected-task limit is `WorkflowBatchActions.MaxAffectedTasks`, capped at 10,000.

### GET /api/instances/{id}/administrative-actions

List the instance's active ordinary tasks and multi-instance parents independently of personal inbox eligibility. A multi-instance parent appears once; its affected-task count includes unfinished active and pending children. The database applies the instance filter, count, ordering, and paging. Positions with no selectable direct flow have an empty `actions` array. A known instance with no active positions returns an empty page. Pages beyond the result set return empty `items` and the exact `totalCount`, including page values up to the `int32` maximum.

**Access:** workflow administrator with nonblank actor.

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | Exact workflow-instance ID. |
| `page` | query | integer (int32) | No | Defaults to 1; values below 1 become 1. |
| `pageSize` | query | integer (int32) | No | Defaults to 50; clamped to 1–200. |

**Body:** none.

**Success:** `200` [PagedResultOfInstanceAdministrativeActionPositionDto](#schema-pagedresultofinstanceadministrativeactionpositiondto). Each item combines the exact position, its concurrency values, and action definitions including typed input fields. Current variable values are not included by this endpoint. Ordering is position update time descending, position kind, then position ID descending.

**Errors:** `400` invalid instance identifier; `401` unauthenticated or blank actor; `403` workflow-administrator role missing; `404` instance missing. See [error envelopes](#errors-and-concurrency).

```http
GET /api/instances/101/administrative-actions?page=1&pageSize=50 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response excerpt for the [admin-action example](../examples/basics/10-admin-action.json); use the complete linked schemas and values from your actual response:

```json
{
  "items": [{
    "position": {
      "positionKind": "userTask", "positionId": 301, "instanceId": 101,
      "tokenId": 201, "tokenActivationId": "e8a84055-97a8-47eb-b67b-8c8f9580c10d",
      "workflowDefinitionId": 11, "nodeId": 2, "nodeName": "approval1",
      "positionUpdatedAt": "2026-09-10T10:00:00Z", "affectedTaskCount": 1
    },
    "actions": [
      { "flowId": 102, "name": "approval", "targetNodeId": 3, "targetNodeName": "approval2", "variables": [] },
      { "flowId": 106, "name": "cancel", "targetNodeId": 4, "targetNodeName": "end", "variables": [] }
    ]
  }],
  "page": 1, "pageSize": 50, "totalCount": 1
}
```

### POST /api/instances/{id}/administrative-actions

Execute one exact displayed position/action immediately. The server locks the instance and runtime state before creating an audit batch/item, rechecks the exact workflow, token activation, position timestamp, and affected count, and commits execution plus a completed one-item audit atomically. A failed transaction leaves neither a workflow change nor a partial audit. No administrative preparation/execution jobs are created; authored asynchronous steps still require the Worker normally.

**Access:** workflow administrator with nonblank actor; permission is checked again on submission.

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | Instance that owns the selected position and token. |

**JSON body:** [ExecuteInstanceAdministrativeActionRequest](#schema-executeinstanceadministrativeactionrequest), limited to 1 MiB. Copy all concurrency values from discovery. `sourceNodeId` is the position's `nodeId`; `flowId` comes from its `actions`. An ordinary task uses `positionKind: "userTask"`; a multi-instance parent uses `"multiInstanceExecution"` and requires an explicit mode. Actor identity, roles, batch/item IDs, and timer identity are server-owned.

**Success:** `200` [AdministrativeActionResultDto](#schema-administrativeactionresultdto), containing refreshed instance detail, the selected position, affected-task count, and the completed audit batch ID. Open that audit through the role-protected [batch detail route](#get-apiadministrative-action-batchesbatchid).

**Errors:** `400` missing/invalid inputs or mode, unsupported/default flow, over-limit selection, or downstream domain failure; `401` unauthenticated or blank actor; `403` current workflow-administrator role missing; `404` missing instance/position/token or cross-instance position/token; `409` stale workflow version, active position, activation, timestamp, or affected count; `413` oversized request; `415` unsupported media type. Refresh after a conflict and require a new selection. Concurrent duplicate submissions advance the position once; there is no direct-action idempotency key or automatic replay.

```http
POST /api/instances/101/administrative-actions HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "expectedWorkflowDefinitionId": 11,
  "sourceNodeId": 2,
  "positionKind": "userTask",
  "positionId": 301,
  "flowId": 102,
  "expectedTokenId": 201,
  "expectedTokenActivationId": "e8a84055-97a8-47eb-b67b-8c8f9580c10d",
  "expectedPositionUpdatedAt": "2026-09-10T10:00:00Z",
  "expectedAffectedTaskCount": 1,
  "multiInstanceMode": null,
  "reason": "Approved operational correction",
  "variables": {}
}
```

Response excerpt:

```json
{
  "instance": { "id": 101, "status": "running" },
  "positionKind": "userTask",
  "positionId": 301,
  "affectedTaskCount": 1,
  "administrativeActionBatchId": 401
}
```

## Administrative action batches

**Authorization:** every route in this family requires workflow-administrator permission and a nonblank authenticated actor, including catalogs, candidate search, batch/item reads, creation, confirmation, cancellation, and retries of completed commands. `Workflow.RequiredRole` is a comma-separated, case-insensitive role list; missing/blank defaults to `admin`, and a custom value replaces that default. Catalog/candidate discovery and execution deliberately bypass normal task role, claim, assignment, and inbox-visibility checks. Administrative selections remain typed flow-input operations and are audited. For immediate execution of one position, use [instance administrative actions](#instance-administrative-actions).

The Worker rechecks the current setting for each preparation item against the stored preparer's roles, and for each execution item against the stored confirmer's roles. Unauthorized preparation becomes `ineligible`; unauthorized execution becomes `skipped` with `errorCode: "authentication_changed"`. Stored roles are snapshots, not a live identity-provider lookup. Role-setting changes affect subsequent checks, not an already authorized executing transaction, committed successes, or committed `asyncAfter` continuations. This is a breaking access change for previously authenticated non-admin batch clients; see [upgrade rules](deployment.md#upgrade-and-compatibility-rules).

Discover an exact workflow version, source user-task node, and action. `actionKind` is `directFlow` or `timerBoundary`; position references use `userTask` or `multiInstanceExecution`. `multiInstanceMode` is `forceParent` or `completeAllChildren` where applicable. `selection.mode` is `explicit` with `positions`, or `allMatching` with a candidate-search snapshot and optional exclusions. Supply an appropriate `boundaryNodeId` for a timer action. Freeze the population with POST, poll until `ready`, inspect items/issues, and confirm using the exact eligible-item count, affected-task count, and batch timestamp. Confirmation queues independent transactions; later staleness can skip an item. Cancellation stops unstarted work and cannot reverse successes.

Requests are limited to 1 MiB for candidate search and creation. A batch is bounded by `WorkflowBatchActions.MaxAffectedTasks`, at most 10,000 affected tasks. Reasons are at most 1,000 Unicode scalars; optional idempotency keys are at most 300 characters. Batch statuses: `preparing`, `ready`, `queued`, `running`, `completed`, `completedWithIssues`, `cancelled`, `failed`; item statuses: `preparing`, `eligible`, `ineligible`, `queued`, `succeeded`, `skipped`, `failed`, `cancelled`. Preparation/execution requires the Worker.

`multiInstanceMode` is required for a direct-flow action on a multi-instance parent. It must be absent/null for ordinary tasks and for every timer-boundary action, including a timer attached to a multi-instance task.

### GET /api/administrative-actions/workflows

List exact workflow versions containing administrative batch source nodes.

**Access:** Workflow administrator with nonblank actor. See [authentication](#http-conventions-and-authentication).

**Parameters:** none.

**Body:** none.

**Success:** `200` array of [WorkflowSummaryDto](#schema-workflowsummarydto).
**Errors:** `401` Unauthorized; `403` Forbidden. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/administrative-actions/workflows HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

[]
```

### GET /api/workflows/{workflowId}/administrative-actions/nodes

List ordinary and multi-instance user-task source nodes in an exact workflow version.

**Access:** Workflow administrator with nonblank actor. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `workflowId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**Body:** none.

**Success:** `200` array of [AdministrativeActionSourceNodeDto](#schema-administrativeactionsourcenodedto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/workflows/11/administrative-actions/nodes HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

[]
```

### GET /api/workflows/{workflowId}/nodes/{sourceNodeId}/administrative-actions

List direct flows and attached timer-boundary actions without normal task authorization filtering.

**Access:** Workflow administrator with nonblank actor. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `workflowId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |
| `sourceNodeId` | path | integer (int32) | Yes | Required route identifier; use the actual returned ID. |

**Body:** none.

**Success:** `200` array of [AdministrativeActionSummaryDto](#schema-administrativeactionsummarydto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/workflows/11/nodes/2/administrative-actions HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

[]
```

### POST /api/administrative-actions/candidates/search

Search active ordinary-task and multi-instance execution positions at an exact node. Pages beyond the result set return empty `items` and the exact `totalCount`, including page values up to the `int32` maximum.

**Access:** Workflow administrator with nonblank actor. See [authentication](#http-conventions-and-authentication).

**Parameters:** none.

**JSON body:** [AdministrativeActionCandidateSearchRequest](#schema-administrativeactioncandidatesearchrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [PagedResultOfAdministrativeActionCandidateDto](#schema-pagedresultofadministrativeactioncandidatedto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/administrative-actions/candidates/search HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "workflowDefinitionId": 11,
  "sourceNodeId": 2,
  "positionKind": "userTask",
  "includeVariables": true,
  "page": 1,
  "pageSize": 50
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalCount": 0
}
```

### POST /api/administrative-action-batches

Freeze a selection and asynchronously prepare an administrative-action batch.

**Access:** Workflow administrator with nonblank actor. See [authentication](#http-conventions-and-authentication).

**Parameters:** none.

**JSON body:** [CreateAdministrativeActionBatchRequest](#schema-createadministrativeactionbatchrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `202` [AdministrativeActionBatchDetailDto](#schema-administrativeactionbatchdetaildto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/administrative-action-batches HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "workflowDefinitionId": 11,
  "sourceNodeId": 2,
  "actionKind": "directFlow",
  "flowId": 201,
  "boundaryNodeId": null,
  "multiInstanceMode": null,
  "reason": "Approved operational correction",
  "variables": {
    "reviewNote": "Approved by operations"
  },
  "selection": {
    "mode": "explicit",
    "positions": [
      {
        "positionKind": "userTask",
        "positionId": 301
      }
    ],
    "allMatching": null,
    "excludedPositions": null
  },
  "idempotencyKey": "admin-action-001"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 202 Accepted
Location: /api/administrative-action-batches/401
Content-Type: application/json

{
  "summary": {
    "id": 401,
    "status": "preparing",
    "totalItemCount": 1,
    "eligibleItemCount": 0,
    "updatedAt": "2026-09-09T10:00:00Z"
  }
}
```

### GET /api/administrative-action-batches

List durable batches using workflow, actor, status and paging selectors.

**Access:** Workflow administrator with nonblank actor. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `workflowKey` | query | string | No | Stable authored workflow-family key; URL-encode. |
| `workflowDefinitionId` | query | integer (int64) | No | Optional exact selector; omission does not add this filter. |
| `status` | query | string | No | Optional lifecycle status filter; see family vocabulary. |
| `preparedBy` | query | string | No | Optional preparation actor filter. |
| `page` | query | integer (int32) | No | Default 1; see the endpoint family for numbered versus cursor paging. |
| `pageSize` | query | integer (int32) | No | Default 50; maximum 200. |

**Body:** none.

**Success:** `200` [PagedResultOfAdministrativeActionBatchSummaryDto](#schema-pagedresultofadministrativeactionbatchsummarydto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/administrative-action-batches?pageSize=50 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalCount": 0
}
```

### GET /api/administrative-action-batches/{batchId}

Read the frozen request, progress, actor snapshots and durable job references.

**Access:** Workflow administrator with nonblank actor. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `batchId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**Body:** none.

**Success:** `200` [AdministrativeActionBatchDetailDto](#schema-administrativeactionbatchdetaildto).
**Errors:** `401` Unauthorized; `403` Forbidden; `404` Not Found. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/administrative-action-batches/401 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "summary": {
    "id": 401,
    "status": "ready",
    "totalItemCount": 1,
    "eligibleItemCount": 0,
    "updatedAt": "2026-09-09T10:00:00Z"
  }
}
```

### GET /api/administrative-action-batches/{batchId}/items

List retained per-item preparation and execution results.

**Access:** Workflow administrator with nonblank actor. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `batchId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |
| `status` | query | string | No | Optional lifecycle status filter; see family vocabulary. |
| `page` | query | integer (int32) | No | Default 1; see the endpoint family for numbered versus cursor paging. |
| `pageSize` | query | integer (int32) | No | Default 50; maximum 200. |

**Body:** none.

**Success:** `200` [PagedResultOfAdministrativeActionBatchItemDto](#schema-pagedresultofadministrativeactionbatchitemdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/administrative-action-batches/401/items?pageSize=50 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalCount": 0
}
```

### POST /api/administrative-action-batches/{batchId}/confirm

Idempotently confirm the displayed eligible set and queue independent execution.

**Access:** Workflow administrator with nonblank actor. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `batchId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**JSON body:** [ConfirmAdministrativeActionBatchRequest](#schema-confirmadministrativeactionbatchrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [AdministrativeActionBatchDetailDto](#schema-administrativeactionbatchdetaildto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/administrative-action-batches/401/confirm HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "expectedEligibleItemCount": 1,
  "expectedAffectedTaskCount": 1,
  "expectedBatchUpdatedAt": "2026-09-09T10:00:00Z"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "summary": {
    "id": 401,
    "status": "queued",
    "totalItemCount": 1,
    "eligibleItemCount": 0,
    "updatedAt": "2026-09-09T10:00:00Z"
  }
}
```

### POST /api/administrative-action-batches/{batchId}/cancel

Stop unstarted items without reversing successful administrative actions.

**Access:** Workflow administrator with nonblank actor. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `batchId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**JSON body:** [CancelAdministrativeActionBatchRequest](#schema-canceladministrativeactionbatchrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [AdministrativeActionBatchDetailDto](#schema-administrativeactionbatchdetaildto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/administrative-action-batches/401/cancel HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "reason": "Stop remaining work"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "summary": {
    "id": 401,
    "status": "cancelled",
    "totalItemCount": 1,
    "eligibleItemCount": 0,
    "updatedAt": "2026-09-09T10:00:00Z"
  }
}
```

## Version-change batches

Every route requires workflow-administrator access. Search one exact `sourceWorkflowId`, select explicit instance IDs or `allMatching` with a filter and exclusions, and supply a published `targetWorkflowId` in the same workflow family plus a reason. Candidate selection only finds currently running instances; compatibility is prepared per instance and is rechecked at execution.

Creation returns `202` with a frozen population. Inspect the ready batch and its per-item blockers/warnings, then confirm using all three returned counts (eligible, ineligible, warning) and `expectedBatchUpdatedAt`. The Worker changes instances independently; a batch is not one all-or-nothing transaction. Cancellation does not undo completed switches. Source/target definition IDs and observed instance versions are retained in batch audit. Candidate/create bodies are limited to 1 MiB. Statuses follow the durable prepare/ready/queued/running/completed/completedWithIssues/cancelled/failed lifecycle; item errors and counts identify blocked or stale work.

Capacity is limited by `WorkflowVersionChanges.MaxBatchInstances` to at most 10,000 instances. Reasons are at most 1,000 characters and optional idempotency keys at most 300. Item statuses are `preparing`, `eligible`, `ineligible`, `queued`, `succeeded`, `skipped`, `failed`, `cancelled`.

### POST /api/instance-version-change-batches/candidates/search

Search running instances on one exact source workflow version.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

**Parameters:** none.

**JSON body:** [InstanceVersionChangeCandidateSearchRequest](#schema-instanceversionchangecandidatesearchrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [PagedResultOfInstanceVersionChangeCandidateDto](#schema-pagedresultofinstanceversionchangecandidatedto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/instance-version-change-batches/candidates/search HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "filter": {
    "sourceWorkflowId": 11
  },
  "includeVariables": false,
  "page": 1,
  "pageSize": 50
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalCount": 0
}
```

### POST /api/instance-version-change-batches

Freeze and asynchronously prepare an instance version-change batch.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

**Parameters:** none.

**JSON body:** [CreateInstanceVersionChangeBatchRequest](#schema-createinstanceversionchangebatchrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `202` [InstanceVersionChangeBatchDetailDto](#schema-instanceversionchangebatchdetaildto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/instance-version-change-batches HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "sourceWorkflowId": 11,
  "targetWorkflowId": 12,
  "reason": "Adopt compatible definition",
  "selection": {
    "mode": "explicit",
    "instanceIds": [
      101
    ],
    "filter": null,
    "excludedInstanceIds": null
  },
  "idempotencyKey": "version-change-001"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 202 Accepted
Location: /api/instance-version-change-batches/401
Content-Type: application/json

{
  "summary": {
    "id": 401,
    "status": "preparing",
    "totalItemCount": 1,
    "eligibleItemCount": 0,
    "updatedAt": "2026-09-09T10:00:00Z"
  }
}
```

### GET /api/instance-version-change-batches

List durable batches using workflow, actor, status and paging selectors.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `workflowKey` | query | string | No | Stable authored workflow-family key; URL-encode. |
| `sourceWorkflowId` | query | integer (int64) | No | Optional exact selector; omission does not add this filter. |
| `targetWorkflowId` | query | integer (int64) | No | Optional exact selector; omission does not add this filter. |
| `status` | query | string | No | Optional lifecycle status filter; see family vocabulary. |
| `preparedBy` | query | string | No | Optional preparation actor filter. |
| `page` | query | integer (int32) | No | Default 1; see the endpoint family for numbered versus cursor paging. |
| `pageSize` | query | integer (int32) | No | Default 50; maximum 200. |

**Body:** none.

**Success:** `200` [PagedResultOfInstanceVersionChangeBatchSummaryDto](#schema-pagedresultofinstanceversionchangebatchsummarydto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/instance-version-change-batches?pageSize=50 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalCount": 0
}
```

### GET /api/instance-version-change-batches/{batchId}

Read the frozen request, progress, actor snapshots and durable job references.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `batchId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**Body:** none.

**Success:** `200` [InstanceVersionChangeBatchDetailDto](#schema-instanceversionchangebatchdetaildto).
**Errors:** `401` Unauthorized; `403` Forbidden; `404` Not Found. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/instance-version-change-batches/401 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "summary": {
    "id": 401,
    "status": "ready",
    "totalItemCount": 1,
    "eligibleItemCount": 0,
    "updatedAt": "2026-09-09T10:00:00Z"
  }
}
```

### GET /api/instance-version-change-batches/{batchId}/items

List retained per-item preparation and execution results.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `batchId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |
| `status` | query | string | No | Optional lifecycle status filter; see family vocabulary. |
| `page` | query | integer (int32) | No | Default 1; see the endpoint family for numbered versus cursor paging. |
| `pageSize` | query | integer (int32) | No | Default 50; maximum 200. |

**Body:** none.

**Success:** `200` [PagedResultOfInstanceVersionChangeBatchItemDto](#schema-pagedresultofinstanceversionchangebatchitemdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/instance-version-change-batches/401/items?pageSize=50 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalCount": 0
}
```

### POST /api/instance-version-change-batches/{batchId}/confirm

Confirm the displayed compatibility result and queue independent execution.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `batchId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**JSON body:** [ConfirmInstanceVersionChangeBatchRequest](#schema-confirminstanceversionchangebatchrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [InstanceVersionChangeBatchDetailDto](#schema-instanceversionchangebatchdetaildto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/instance-version-change-batches/401/confirm HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "expectedEligibleItemCount": 1,
  "expectedIneligibleItemCount": 0,
  "expectedWarningItemCount": 0,
  "expectedBatchUpdatedAt": "2026-09-09T10:00:00Z"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "summary": {
    "id": 401,
    "status": "queued",
    "totalItemCount": 1,
    "eligibleItemCount": 0,
    "updatedAt": "2026-09-09T10:00:00Z"
  }
}
```

### POST /api/instance-version-change-batches/{batchId}/cancel

Cancel unstarted version changes without reversing successful items.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `batchId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**JSON body:** [CancelInstanceVersionChangeBatchRequest](#schema-cancelinstanceversionchangebatchrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [InstanceVersionChangeBatchDetailDto](#schema-instanceversionchangebatchdetaildto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/instance-version-change-batches/401/cancel HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "reason": "Stop remaining version changes"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "summary": {
    "id": 401,
    "status": "cancelled",
    "totalItemCount": 1,
    "eligibleItemCount": 0,
    "updatedAt": "2026-09-09T10:00:00Z"
  }
}
```

## Variable-update batches

Every route requires workflow-administrator access. This uses the same raw instance-variable semantics as `PATCH /api/instances/{id}/variables`: authored validation is bypassed, null is stored, and conditional waits/boundaries may react. Select one workflow family with optional exact version filters; multiple versions are grouped into separate durable preparation/execution jobs. Explicit instance IDs or a frozen `allMatching` filter plus exclusions define membership.

Candidate search supports structured sorting, cursor paging and optional current values; its allowed instance sort fields are `id`, `createdAt`, `updatedAt`. Creation freezes common variable writes and returns `202`; review plans/warnings, then confirm with eligible/ineligible/warning counts and the exact batch timestamp. Read `jobs` for all per-version job links, not a single assumed job ID. Each item commits independently. Cancellation stops unstarted work without undoing previous updates. Candidate/create bodies are limited to 1 MiB; the Worker must run.

At most 100 variable writes are accepted per operation. Batch expansion is bounded by `WorkflowVariableUpdates.MaxBatchInstances` (at most 10,000 instances), 100,000 total expanded writes, and 100 MiB expanded payload. Batch and item status vocabulary is the same as [administrative action batches](#administrative-action-batches). Variable outcomes are `added` or `updated`; per-version job phases are `prepare` or `execute`.

### POST /api/instance-variable-update-batches/candidates/search

Search running variable-update candidates in one workflow family.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

**Parameters:** none.

**JSON body:** [InstanceVariableUpdateCandidateSearchRequest](#schema-instancevariableupdatecandidatesearchrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [PagedResultOfInstanceVariableUpdateCandidateDto](#schema-pagedresultofinstancevariableupdatecandidatedto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `413` Payload Too Large; `415` Unsupported Media Type. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/instance-variable-update-batches/candidates/search HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "filter": {
    "workflowKey": "example-api-approval"
  },
  "sort": [
    {
      "field": "updatedAt",
      "direction": "desc"
    }
  ],
  "includeVariables": true,
  "pageSize": 50
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalCount": 0
}
```

### POST /api/instance-variable-update-batches

Freeze and asynchronously prepare a variable-update batch.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

**Parameters:** none.

**JSON body:** [CreateInstanceVariableUpdateBatchRequest](#schema-createinstancevariableupdatebatchrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `202` [InstanceVariableUpdateBatchDetailDto](#schema-instancevariableupdatebatchdetaildto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `409` Conflict; `413` Payload Too Large; `415` Unsupported Media Type. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/instance-variable-update-batches HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "workflowKey": "example-api-approval",
  "variables": [
    {
      "name": "reviewRequired",
      "value": true
    }
  ],
  "reason": "Correct active requests",
  "selection": {
    "mode": "explicit",
    "instanceIds": [
      101
    ],
    "filter": null,
    "excludedInstanceIds": null
  },
  "idempotencyKey": "variable-batch-001"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 202 Accepted
Location: /api/instance-variable-update-batches/401
Content-Type: application/json

{
  "summary": {
    "id": 401,
    "status": "preparing",
    "totalItemCount": 1,
    "eligibleItemCount": 0,
    "updatedAt": "2026-09-09T10:00:00Z"
  }
}
```

### GET /api/instance-variable-update-batches

List durable batches using workflow, actor, status and paging selectors.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `workflowKey` | query | string | No | Stable authored workflow-family key; URL-encode. |
| `status` | query | string | No | Optional lifecycle status filter; see family vocabulary. |
| `preparedBy` | query | string | No | Optional preparation actor filter. |
| `page` | query | integer (int32) | No | Default 1; see the endpoint family for numbered versus cursor paging. |
| `pageSize` | query | integer (int32) | No | Default 50; maximum 200. |

**Body:** none.

**Success:** `200` [PagedResultOfInstanceVariableUpdateBatchSummaryDto](#schema-pagedresultofinstancevariableupdatebatchsummarydto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/instance-variable-update-batches?pageSize=50 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalCount": 0
}
```

### GET /api/instance-variable-update-batches/{batchId}

Read the frozen request, progress, actor snapshots and durable job references.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `batchId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**Body:** none.

**Success:** `200` [InstanceVariableUpdateBatchDetailDto](#schema-instancevariableupdatebatchdetaildto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/instance-variable-update-batches/401 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "summary": {
    "id": 401,
    "status": "ready",
    "totalItemCount": 1,
    "eligibleItemCount": 0,
    "updatedAt": "2026-09-09T10:00:00Z"
  }
}
```

### GET /api/instance-variable-update-batches/{batchId}/items

List retained per-item preparation and execution results.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `batchId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |
| `status` | query | string | No | Optional lifecycle status filter; see family vocabulary. |
| `page` | query | integer (int32) | No | Default 1; see the endpoint family for numbered versus cursor paging. |
| `pageSize` | query | integer (int32) | No | Default 50; maximum 200. |

**Body:** none.

**Success:** `200` [PagedResultOfInstanceVariableUpdateBatchItemDto](#schema-pagedresultofinstancevariableupdatebatchitemdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/instance-variable-update-batches/401/items?pageSize=50 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalCount": 0
}
```

### POST /api/instance-variable-update-batches/{batchId}/confirm

Confirm the prepared population and queue per-version execution jobs.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `batchId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**JSON body:** [ConfirmInstanceVariableUpdateBatchRequest](#schema-confirminstancevariableupdatebatchrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [InstanceVariableUpdateBatchDetailDto](#schema-instancevariableupdatebatchdetaildto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict; `413` Payload Too Large; `415` Unsupported Media Type. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/instance-variable-update-batches/401/confirm HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "expectedEligibleItemCount": 1,
  "expectedIneligibleItemCount": 0,
  "expectedWarningItemCount": 0,
  "expectedBatchUpdatedAt": "2026-09-09T10:00:00Z"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "summary": {
    "id": 401,
    "status": "queued",
    "totalItemCount": 1,
    "eligibleItemCount": 0,
    "updatedAt": "2026-09-09T10:00:00Z"
  }
}
```

### POST /api/instance-variable-update-batches/{batchId}/cancel

Cancel unstarted items without reversing successful updates.

**Access:** Workflow administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `batchId` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**JSON body:** [CancelInstanceVariableUpdateBatchRequest](#schema-cancelinstancevariableupdatebatchrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [InstanceVariableUpdateBatchDetailDto](#schema-instancevariableupdatebatchdetaildto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict; `413` Payload Too Large; `415` Unsupported Media Type. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/instance-variable-update-batches/401/cancel HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "reason": "Stop remaining updates"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "summary": {
    "id": 401,
    "status": "cancelled",
    "totalItemCount": 1,
    "eligibleItemCount": 0,
    "updatedAt": "2026-09-09T10:00:00Z"
  }
}
```

## Delegation

A self-service grant uses the authenticated actor as `delegator`; only the administrator form accepts another delegator. Grants cover finite UTC intervals `[validFrom, validUntil)` for 1–100 distinct stable workflow keys. Names/keys are limited to 300 characters, reasons to 1,000; self-delegation and invalid intervals are rejected. One response item is created per workflow family.

`direction` is `outgoing` (default) or `incoming`. The `state` filter is acceptance state (`notRequired`, `pending`, `accepted`, `rejected`), not computed active/expired status. Only the designated delegate can accept/reject a pending grant; either participant can revoke/withdraw it. Managed routes and delegation-policy reads/writes require Delegation administrator permission. Lifecycle changes require `expectedUpdatedAt`. A missing family policy is represented by default values and null audit fields; use `expectedUpdatedAt: null` when creating its first policy. Policy changes affect new grants rather than rewriting existing captured acceptance requirements. Delegation preserves original assignment/claim ownership and records who acted for whom.

Without a saved family policy, `requiresAcceptance` defaults to false. Creation also requires `validUntil` to be in the future.

### GET /api/user-delegations

List the current actor’s outgoing or incoming standing grants.

**Access:** Bearer with nonblank actor; participant restrictions apply. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `direction` | query | string | No | outgoing (default) or incoming. |
| `workflowKey` | query | string | No | Stable authored workflow-family key; URL-encode. |
| `state` | query | string | No | notRequired, pending, accepted, or rejected. |
| `page` | query | integer (int32) | No | Default 1; see the endpoint family for numbered versus cursor paging. |
| `pageSize` | query | integer (int32) | No | Default 50; maximum 200. |

**Body:** none.

**Success:** `200` [PagedResultOfUserDelegationDto](#schema-pagedresultofuserdelegationdto).
**Errors:** `400` Bad Request; `401` Unauthorized. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/user-delegations?pageSize=50 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalCount": 0
}
```

### POST /api/user-delegations

Create a self-service delegation grant for each requested workflow key.

**Access:** Bearer with nonblank actor; participant restrictions apply. See [authentication](#http-conventions-and-authentication).

**Parameters:** none.

**JSON body:** [CreateUserDelegationRequest](#schema-createuserdelegationrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `201` array of [UserDelegationDto](#schema-userdelegationdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/user-delegations HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "delegate": "backup@example.com",
  "workflowKeys": [
    "example-api-approval"
  ],
  "validFrom": "2026-09-09T10:00:00Z",
  "validUntil": "2026-09-16T10:00:00Z",
  "reason": "Scheduled leave"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 201 Created
Location: /api/user-delegations
Content-Type: application/json

[
  {
    "id": 101,
    "delegator": "reviewer@example.com",
    "delegate": "backup@example.com",
    "workflowKey": "example-api-approval",
    "validFrom": "2026-09-09T10:00:00Z",
    "validUntil": "2026-09-16T10:00:00Z",
    "requiresAcceptance": true,
    "acceptanceState": "pending",
    "updatedAt": "2026-09-09T10:00:00Z",
    "isActive": false
  }
]
```

### POST /api/user-delegations/{id}/accept

Accept a pending grant as its designated delegate.

**Access:** Bearer with nonblank actor; participant restrictions apply. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**JSON body:** [UserDelegationLifecycleRequest](#schema-userdelegationlifecyclerequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [UserDelegationDto](#schema-userdelegationdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/user-delegations/101/accept HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "expectedUpdatedAt": "2026-09-09T10:00:00Z",
  "reason": "Confirmed availability"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "id": 101,
  "delegator": "reviewer@example.com",
  "delegate": "backup@example.com",
  "workflowKey": "example-api-approval",
  "validFrom": "2026-09-09T10:00:00Z",
  "validUntil": "2026-09-16T10:00:00Z",
  "requiresAcceptance": true,
  "acceptanceState": "accepted",
  "revokedAt": null,
  "updatedAt": "2026-09-09T10:00:00Z",
  "isActive": true
}
```

### POST /api/user-delegations/{id}/reject

Reject a pending grant as its designated delegate.

**Access:** Bearer with nonblank actor; participant restrictions apply. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**JSON body:** [UserDelegationLifecycleRequest](#schema-userdelegationlifecyclerequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [UserDelegationDto](#schema-userdelegationdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/user-delegations/101/reject HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "expectedUpdatedAt": "2026-09-09T10:00:00Z",
  "reason": "Confirmed availability"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "id": 101,
  "delegator": "reviewer@example.com",
  "delegate": "backup@example.com",
  "workflowKey": "example-api-approval",
  "validFrom": "2026-09-09T10:00:00Z",
  "validUntil": "2026-09-16T10:00:00Z",
  "requiresAcceptance": true,
  "acceptanceState": "rejected",
  "revokedAt": null,
  "updatedAt": "2026-09-09T10:00:00Z",
  "isActive": false
}
```

### POST /api/user-delegations/{id}/revoke

Withdraw or revoke a grant as one of its participants.

**Access:** Bearer with nonblank actor; participant restrictions apply. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**JSON body:** [UserDelegationLifecycleRequest](#schema-userdelegationlifecyclerequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [UserDelegationDto](#schema-userdelegationdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/user-delegations/101/revoke HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "expectedUpdatedAt": "2026-09-09T10:00:00Z",
  "reason": "Confirmed availability"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "id": 101,
  "delegator": "reviewer@example.com",
  "delegate": "backup@example.com",
  "workflowKey": "example-api-approval",
  "validFrom": "2026-09-09T10:00:00Z",
  "validUntil": "2026-09-16T10:00:00Z",
  "requiresAcceptance": true,
  "acceptanceState": "accepted",
  "revokedAt": "2026-09-09T10:00:00Z",
  "updatedAt": "2026-09-09T10:00:00Z",
  "isActive": false
}
```

### GET /api/user-delegations/manage

Administratively search grants by delegator, delegate, workflow and state.

**Access:** Delegation administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `delegator` | query | string | No | Optional exact selector; omission does not add this filter. |
| `delegate` | query | string | No | Optional exact selector; omission does not add this filter. |
| `workflowKey` | query | string | No | Stable authored workflow-family key; URL-encode. |
| `state` | query | string | No | notRequired, pending, accepted, or rejected. |
| `page` | query | integer (int32) | No | Default 1; see the endpoint family for numbered versus cursor paging. |
| `pageSize` | query | integer (int32) | No | Default 50; maximum 200. |

**Body:** none.

**Success:** `200` [PagedResultOfUserDelegationDto](#schema-pagedresultofuserdelegationdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/user-delegations/manage?pageSize=50 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalCount": 0
}
```

### POST /api/user-delegations/manage

Create grants on behalf of an explicitly named delegator.

**Access:** Delegation administrator. See [authentication](#http-conventions-and-authentication).

**Parameters:** none.

**JSON body:** [CreateManagedUserDelegationRequest](#schema-createmanageduserdelegationrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `201` array of [UserDelegationDto](#schema-userdelegationdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/user-delegations/manage HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "delegator": "reviewer@example.com",
  "delegate": "backup@example.com",
  "workflowKeys": [
    "example-api-approval"
  ],
  "validFrom": "2026-09-09T10:00:00Z",
  "validUntil": "2026-09-16T10:00:00Z",
  "reason": "Scheduled leave"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 201 Created
Location: /api/user-delegations/manage
Content-Type: application/json

[
  {
    "id": 101,
    "delegator": "reviewer@example.com",
    "delegate": "backup@example.com",
    "workflowKey": "example-api-approval",
    "validFrom": "2026-09-09T10:00:00Z",
    "validUntil": "2026-09-16T10:00:00Z",
    "requiresAcceptance": true,
    "acceptanceState": "pending",
    "updatedAt": "2026-09-09T10:00:00Z",
    "isActive": false
  }
]
```

### POST /api/user-delegations/manage/{id}/revoke

Administratively revoke a delegation grant.

**Access:** Delegation administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**JSON body:** [UserDelegationLifecycleRequest](#schema-userdelegationlifecyclerequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [UserDelegationDto](#schema-userdelegationdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/user-delegations/manage/101/revoke HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "expectedUpdatedAt": "2026-09-09T10:00:00Z",
  "reason": "Confirmed availability"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "id": 101,
  "delegator": "reviewer@example.com",
  "delegate": "backup@example.com",
  "workflowKey": "example-api-approval",
  "validFrom": "2026-09-09T10:00:00Z",
  "validUntil": "2026-09-16T10:00:00Z",
  "requiresAcceptance": true,
  "acceptanceState": "accepted",
  "revokedAt": "2026-09-09T10:00:00Z",
  "updatedAt": "2026-09-09T10:00:00Z",
  "isActive": false
}
```

### GET /api/user-delegation-policies/{workflowKey}

Read the family’s acceptance policy or its unpersisted default.

**Access:** Delegation administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `workflowKey` | path | string | Yes | Stable authored workflow-family key; URL-encode. |

**Body:** none.

**Success:** `200` [WorkflowDelegationPolicyDto](#schema-workflowdelegationpolicydto).
**Errors:** `401` Unauthorized; `403` Forbidden; `404` Not Found. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/user-delegation-policies/example-api-approval HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "workflowKey": "example-api-approval",
  "requiresAcceptance": true,
  "createdBy": "admin@example.com",
  "createdAt": "2026-09-09T10:00:00Z",
  "updatedBy": "admin@example.com",
  "updatedAt": "2026-09-09T10:00:00Z"
}
```

### PUT /api/user-delegation-policies/{workflowKey}

Create or replace the acceptance policy for future grants.

**Access:** Delegation administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `workflowKey` | path | string | Yes | Stable authored workflow-family key; URL-encode. |

**JSON body:** [UpdateWorkflowDelegationPolicyRequest](#schema-updateworkflowdelegationpolicyrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [WorkflowDelegationPolicyDto](#schema-workflowdelegationpolicydto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
PUT /api/user-delegation-policies/example-api-approval HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "requiresAcceptance": true,
  "expectedUpdatedAt": null
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "workflowKey": "example-api-approval",
  "requiresAcceptance": true,
  "createdBy": "admin@example.com",
  "createdAt": "2026-09-09T10:00:00Z",
  "updatedBy": "admin@example.com",
  "updatedAt": "2026-09-09T10:00:00Z"
}
```

## Settings

All routes require Settings administrator permission. Engine settings have a string `value`, while workflow settings have a raw JSON `value` for the workflow expression context. Creation selects namespace/key (engine) or namespace/name (workflow); updates replace value/description without renaming the setting. For update/delete round-trip the exact timestamp from the list response. Delete takes `expectedUpdatedAt` in the query, not the body. Invalid identifiers, missing values/timestamps are `400`; duplicate/stale operations are `409`. Engine values are opaque strings, preserving empty/whitespace/multiline content; each setting consumer interprets the value. Invalid actor-identity configuration can prevent the next API startup. Namespace/key/name are at most 300 characters and description at most 1,000. Changing the actor identity claim setting requires process restart because identity configuration is latched at startup. No per-ID GET route exists: retrieve settings with the list.

### GET /api/engine-settings

List settings and their current optimistic timestamps.

**Access:** Settings administrator. See [authentication](#http-conventions-and-authentication).

**Parameters:** none.

**Body:** none.

**Success:** `200` array of [EngineSettingDto](#schema-enginesettingdto).
**Errors:** `401` Unauthorized; `403` Forbidden. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/engine-settings HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

[]
```

### POST /api/engine-settings

Create a setting.

**Access:** Settings administrator. See [authentication](#http-conventions-and-authentication).

**Parameters:** none.

**JSON body:** [CreateEngineSettingRequest](#schema-createenginesettingrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `201` [EngineSettingDto](#schema-enginesettingdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/engine-settings HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "namespace": "Example",
  "key": "Example.Enabled",
  "value": "true",
  "description": "Example integration feature flag"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 201 Created
Location: /api/engine-settings/1001
Content-Type: application/json

{
  "id": 1001,
  "namespace": "Example",
  "key": "Example.Enabled",
  "value": "true",
  "description": "Example integration feature flag",
  "createdAt": "2026-09-09T10:00:00Z",
  "updatedAt": "2026-09-09T10:00:00Z"
}
```

### PUT /api/engine-settings/{id}

Replace an existing setting value and description.

**Access:** Settings administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**JSON body:** [UpdateEngineSettingRequest](#schema-updateenginesettingrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [EngineSettingDto](#schema-enginesettingdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
PUT /api/engine-settings/1001 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "value": "false",
  "description": "Example integration feature flag",
  "expectedUpdatedAt": "2026-09-09T10:00:00Z"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "id": 1001,
  "namespace": "Example",
  "key": "Example.Enabled",
  "value": "false",
  "description": "Example integration feature flag",
  "createdAt": "2026-09-09T10:00:00Z",
  "updatedAt": "2026-09-09T10:00:00Z"
}
```

### DELETE /api/engine-settings/{id}

Delete a setting using its expected timestamp.

**Access:** Settings administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |
| `expectedUpdatedAt` | query | string (date-time) | Yes | Required exact timestamp from the last list/read; URL-encode it. |

**Body:** none.

**Success:** `204` without a response body.
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
DELETE /api/engine-settings/1001?expectedUpdatedAt=2026-09-09T10%3A00%3A00Z HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response:

```http
HTTP/1.1 204 No Content
```

### GET /api/workflow-settings

List settings and their current optimistic timestamps.

**Access:** Settings administrator. See [authentication](#http-conventions-and-authentication).

**Parameters:** none.

**Body:** none.

**Success:** `200` array of [WorkflowSettingDto](#schema-workflowsettingdto).
**Errors:** `401` Unauthorized; `403` Forbidden. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/workflow-settings HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

[]
```

### POST /api/workflow-settings

Create a setting.

**Access:** Settings administrator. See [authentication](#http-conventions-and-authentication).

**Parameters:** none.

**JSON body:** [CreateWorkflowSettingRequest](#schema-createworkflowsettingrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `201` [WorkflowSettingDto](#schema-workflowsettingdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/workflow-settings HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "namespace": "purchase",
  "name": "approvalLimit",
  "value": 1000,
  "description": "Approval threshold"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 201 Created
Location: /api/workflow-settings/1001
Content-Type: application/json

{
  "id": 1001,
  "namespace": "purchase",
  "name": "approvalLimit",
  "value": 1000,
  "description": "Approval threshold",
  "createdAt": "2026-09-09T10:00:00Z",
  "updatedAt": "2026-09-09T10:00:00Z"
}
```

### PUT /api/workflow-settings/{id}

Replace an existing setting value and description.

**Access:** Settings administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**JSON body:** [UpdateWorkflowSettingRequest](#schema-updateworkflowsettingrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [WorkflowSettingDto](#schema-workflowsettingdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
PUT /api/workflow-settings/1001 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "value": 1500,
  "description": "Approval threshold",
  "expectedUpdatedAt": "2026-09-09T10:00:00Z"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "id": 1001,
  "namespace": "purchase",
  "name": "approvalLimit",
  "value": 1500,
  "description": "Approval threshold",
  "createdAt": "2026-09-09T10:00:00Z",
  "updatedAt": "2026-09-09T10:00:00Z"
}
```

### DELETE /api/workflow-settings/{id}

Delete a setting using its expected timestamp.

**Access:** Settings administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |
| `expectedUpdatedAt` | query | string (date-time) | Yes | Required exact timestamp from the last list/read; URL-encode it. |

**Body:** none.

**Success:** `204` without a response body.
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
DELETE /api/workflow-settings/1001?expectedUpdatedAt=2026-09-09T10%3A00%3A00Z HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response:

```http
HTTP/1.1 204 No Content
```

## Shared variables

Catalog list/detail intentionally contains metadata only; fetch values explicitly from `/value`. The immutable contract is key, type, array/nullability, and optional validation. `hasValue:false` means no assigned value and differs from `hasValue:true,value:null` for a nullable variable. `revision` covers catalog changes; `valueRevision` tracks value changes separately. Current-value reads require Shared-variable administrator or a client with read scope; writes allow that administrator or write scope.

Value/description/lifecycle mutations use `expectedRevision` plus optional `requestId` and `reason`. Reuse request IDs only for the same logical mutation. Archival is blocked by published definitions, running instances, or open jobs using the key; inspect `/lifecycle-blockers` first. Archiving retains history and the contract; reactivation restores use. History is an array with page/pageSize queries, not a PagedResult object. List `status` values are `active` and `archived`; `includeArchived` defaults false. Create/value bodies are limited to 1 MiB, lifecycle/metadata bodies to 64 KiB. Keys must be URL-encoded when placed in a path.

To list archived entries, set both `includeArchived=true` and `status=archived`; the default active-only predicate otherwise remains in effect. Detail and current-value reads can return archived entries. Keys are at most 300 Unicode scalars and cannot use reserved context prefixes. Shared-variable validation is at most 4,000 scalars and binds only `value`; permitted explicit null skips validation. Description/reason are at most 1,000 scalars and requestId at most 300.

**Create-null limitation:** in this checkout, POST creation with `hasValue:true,value:null` returns `400` (value required). To initialize an explicit nullable null, create the contract with `hasValue:false`, then PUT `/value` with `value:null` and the returned revision. Current-value reads distinguish that explicit null from unset using `hasValue`. Mutation `requestId` is scoped by caller kind + caller ID across keys; identical retries return the original saved result, while reuse for another operation/key/body returns `409`.

### GET /api/shared-variables

List deployment-wide shared variables.

**Access:** Shared-variable administrator or shared-variable client with read scope. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `search` | query | string | No | Optional catalog search text. |
| `status` | query | string | No | Optional lifecycle status filter; see family vocabulary. |
| `page` | query | integer (int32) | No | Default 1; see the endpoint family for numbered versus cursor paging. |
| `pageSize` | query | integer (int32) | No | Default 50; maximum 200. |
| `includeArchived` | query | boolean | No | Default false. |

**Body:** none.

**Success:** `200` [PagedResultOfSharedVariableMetadataDto](#schema-pagedresultofsharedvariablemetadatadto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/shared-variables?pageSize=50 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalCount": 0
}
```

### POST /api/shared-variables

Create a deployment-wide shared variable.

**Access:** Shared-variable administrator. See [authentication](#http-conventions-and-authentication).

**Parameters:** none.

**JSON body:** [CreateSharedVariableRequest](#schema-createsharedvariablerequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `201` [SharedVariableMetadataDto](#schema-sharedvariablemetadatadto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `409` Conflict; `413` Payload Too Large; `415` Unsupported Media Type. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/shared-variables HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "key": "approval-limit",
  "dataType": "number",
  "isArray": false,
  "nullable": false,
  "hasValue": true,
  "value": 1000,
  "validation": null,
  "description": "Approval threshold",
  "requestId": "shared-create-001",
  "reason": "Initialize threshold"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 201 Created
Location: /api/shared-variables/approval-limit
Content-Type: application/json

{
  "id": 601,
  "key": "approval-limit",
  "dataType": "number",
  "isArray": false,
  "nullable": false,
  "validation": null,
  "description": "Approval threshold",
  "hasValue": true,
  "status": "active",
  "revision": 1,
  "createdAt": "2026-09-09T10:00:00Z",
  "updatedAt": "2026-09-09T10:00:00Z",
  "archivedAt": null,
  "valueRevision": 1,
  "historyPrunedAt": null
}
```

### GET /api/shared-variables/{key}

Get a shared variable by its deployment-wide key.

**Access:** Shared-variable administrator or shared-variable client with read scope. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `key` | path | string | Yes | Deployment-wide shared-variable key; URL-encode. |

**Body:** none.

**Success:** `200` [SharedVariableMetadataDto](#schema-sharedvariablemetadatadto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/shared-variables/approval-limit HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "id": 601,
  "key": "approval-limit",
  "dataType": "number",
  "isArray": false,
  "nullable": false,
  "validation": null,
  "description": "Approval threshold",
  "hasValue": true,
  "status": "active",
  "revision": 1,
  "createdAt": "2026-09-09T10:00:00Z",
  "updatedAt": "2026-09-09T10:00:00Z",
  "archivedAt": null,
  "valueRevision": 1,
  "historyPrunedAt": null
}
```

### PATCH /api/shared-variables/{key}

Update shared-variable description metadata.

**Access:** Shared-variable administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `key` | path | string | Yes | Deployment-wide shared-variable key; URL-encode. |

**JSON body:** [UpdateSharedVariableDescriptionRequest](#schema-updatesharedvariabledescriptionrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [SharedVariableMetadataDto](#schema-sharedvariablemetadatadto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict; `413` Payload Too Large; `415` Unsupported Media Type. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
PATCH /api/shared-variables/approval-limit HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "description": "Threshold in base currency",
  "expectedRevision": 1,
  "requestId": "shared-description-001",
  "reason": "Clarify units"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "id": 601,
  "key": "approval-limit",
  "dataType": "number",
  "isArray": false,
  "nullable": false,
  "validation": null,
  "description": "Threshold in base currency",
  "hasValue": true,
  "status": "active",
  "revision": 2,
  "createdAt": "2026-09-09T10:00:00Z",
  "updatedAt": "2026-09-09T10:00:00Z",
  "archivedAt": null,
  "valueRevision": 1,
  "historyPrunedAt": null
}
```

### GET /api/shared-variables/{key}/value

Get the current shared-variable value.

**Access:** Shared-variable administrator or shared-variable client with read scope. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `key` | path | string | Yes | Deployment-wide shared-variable key; URL-encode. |

**Body:** none.

**Success:** `200` [SharedVariableValueDto](#schema-sharedvariablevaluedto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/shared-variables/approval-limit/value HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "key": "approval-limit",
  "hasValue": true,
  "value": 1000,
  "revision": 1,
  "updatedAt": "2026-09-09T10:00:00Z",
  "valueRevision": 1
}
```

### PUT /api/shared-variables/{key}/value

Update a shared-variable value using optimistic concurrency.

**Access:** Shared-variable administrator or shared-variable client with write scope. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `key` | path | string | Yes | Deployment-wide shared-variable key; URL-encode. |

**JSON body:** [UpdateSharedVariableValueRequest](#schema-updatesharedvariablevaluerequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [SharedVariableValueDto](#schema-sharedvariablevaluedto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict; `413` Payload Too Large; `415` Unsupported Media Type. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
PUT /api/shared-variables/approval-limit/value HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "value": 1500,
  "expectedRevision": 1,
  "requestId": "shared-value-001",
  "reason": "Update threshold"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "key": "approval-limit",
  "hasValue": true,
  "value": 1500,
  "revision": 2,
  "updatedAt": "2026-09-09T10:00:00Z",
  "valueRevision": 2
}
```

### POST /api/shared-variables/{key}/archive

Archive an unused shared variable.

**Access:** Shared-variable administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `key` | path | string | Yes | Deployment-wide shared-variable key; URL-encode. |

**JSON body:** [ArchiveSharedVariableRequest](#schema-archivesharedvariablerequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [SharedVariableMetadataDto](#schema-sharedvariablemetadatadto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict; `413` Payload Too Large; `415` Unsupported Media Type. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/shared-variables/approval-limit/archive HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "expectedRevision": 2,
  "requestId": "shared-archive-001",
  "reason": "No longer referenced"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "id": 601,
  "key": "approval-limit",
  "dataType": "number",
  "isArray": false,
  "nullable": false,
  "validation": null,
  "description": "Approval threshold",
  "hasValue": true,
  "status": "archived",
  "revision": 3,
  "createdAt": "2026-09-09T10:00:00Z",
  "updatedAt": "2026-09-09T10:00:00Z",
  "archivedAt": "2026-09-09T10:00:00Z",
  "valueRevision": 1,
  "historyPrunedAt": null
}
```

### POST /api/shared-variables/{key}/reactivate

Reactivate an archived shared variable.

**Access:** Shared-variable administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `key` | path | string | Yes | Deployment-wide shared-variable key; URL-encode. |

**JSON body:** [ReactivateSharedVariableRequest](#schema-reactivatesharedvariablerequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [SharedVariableMetadataDto](#schema-sharedvariablemetadatadto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict; `413` Payload Too Large; `415` Unsupported Media Type. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/shared-variables/approval-limit/reactivate HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "expectedRevision": 3,
  "requestId": "shared-reactivate-001",
  "reason": "Resume use"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "id": 601,
  "key": "approval-limit",
  "dataType": "number",
  "isArray": false,
  "nullable": false,
  "validation": null,
  "description": "Approval threshold",
  "hasValue": true,
  "status": "active",
  "revision": 4,
  "createdAt": "2026-09-09T10:00:00Z",
  "updatedAt": "2026-09-09T10:00:00Z",
  "archivedAt": null,
  "valueRevision": 1,
  "historyPrunedAt": null
}
```

### GET /api/shared-variables/{key}/history

List immutable revisions for a shared variable.

**Access:** Shared-variable administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `key` | path | string | Yes | Deployment-wide shared-variable key; URL-encode. |
| `page` | query | integer (int32) | No | Default 1; see the endpoint family for numbered versus cursor paging. |
| `pageSize` | query | integer (int32) | No | Default 50; maximum 200. |

**Body:** none.

**Success:** `200` array of [SharedVariableRevisionDto](#schema-sharedvariablerevisiondto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden. See [error envelopes](#errors-and-concurrency) and the family rules above.

Success is a plain revision array despite the page/pageSize query; it is not wrapped in PagedResult.

```http
GET /api/shared-variables/approval-limit/history?pageSize=50 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

[]
```

### GET /api/shared-variables/{key}/lifecycle-blockers

Inspect blockers that prevent shared-variable archival.

**Access:** Shared-variable administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `key` | path | string | Yes | Deployment-wide shared-variable key; URL-encode. |

**Body:** none.

**Success:** `200` [SharedVariableLifecycleBlockersDto](#schema-sharedvariablelifecycleblockersdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/shared-variables/approval-limit/lifecycle-blockers HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "publishedDefinitionCount": 0,
  "runningInstanceCount": 0,
  "openJobCount": 0,
  "reasons": [],
  "canArchive": true
}
```

## Shared-variable clients

JWT Shared-variable administrator permission is required for every route. Client IDs are administrator-chosen, case-sensitive and immutable. Allowed scopes are `shared-variables.read` and `shared-variables.write`; they are explicit separate permissions. Creation and secret rotation return a generated secret exactly once with `Cache-Control: no-store` and `Pragma: no-cache`. Metadata reads cannot recover it. Update replaces displayName/scopes/expiresAt with `expectedRevision`; revoked clients cannot be used. Rotation grace is 0–168 hours, default 24. Revocation immediately invalidates all active secrets. All request bodies are limited to 64 KiB.

### GET /api/shared-variable-clients

List managed shared-variable API clients.

**Access:** Shared-variable administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `page` | query | integer (int32) | No | Default 1; see the endpoint family for numbered versus cursor paging. |
| `pageSize` | query | integer (int32) | No | Default 50; maximum 200. |

**Body:** none.

**Success:** `200` [PagedResultOfSharedVariableClientDto](#schema-pagedresultofsharedvariableclientdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/shared-variable-clients?pageSize=50 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "items": [],
  "page": 1,
  "pageSize": 50,
  "totalCount": 0
}
```

### POST /api/shared-variable-clients

Create a managed API client and return its secret once.

**Access:** Shared-variable administrator. See [authentication](#http-conventions-and-authentication).

**Parameters:** none.

**JSON body:** [CreateSharedVariableClientRequest](#schema-createsharedvariableclientrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `201` [CreateSharedVariableClientResult](#schema-createsharedvariableclientresult).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `409` Conflict; `413` Payload Too Large; `415` Unsupported Media Type. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/shared-variable-clients HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "clientId": "threshold-service",
  "displayName": "Threshold integration",
  "scopes": [
    "shared-variables.read",
    "shared-variables.write"
  ],
  "expiresAt": null
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 201 Created
Location: /api/shared-variable-clients/701
Cache-Control: no-store
Pragma: no-cache
Content-Type: application/json

{
  "client": {
    "id": 701,
    "clientId": "threshold-service",
    "displayName": "Threshold integration",
    "scopes": [
      "shared-variables.read",
      "shared-variables.write"
    ],
    "status": "active",
    "revision": 1,
    "expiresAt": null,
    "createdAt": "2026-09-09T10:00:00Z",
    "updatedAt": "2026-09-09T10:00:00Z",
    "revokedAt": null
  },
  "clientSecret": "ONE_TIME_GENERATED_SECRET"
}
```

### GET /api/shared-variable-clients/{id}

Get managed shared-variable API-client metadata.

**Access:** Shared-variable administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**Body:** none.

**Success:** `200` [SharedVariableClientDto](#schema-sharedvariableclientdto).
**Errors:** `401` Unauthorized; `403` Forbidden; `404` Not Found. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/shared-variable-clients/701 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "id": 701,
  "clientId": "threshold-service",
  "displayName": "Threshold integration",
  "scopes": [
    "shared-variables.read"
  ],
  "status": "active",
  "revision": 1,
  "expiresAt": null,
  "createdAt": "2026-09-09T10:00:00Z",
  "updatedAt": "2026-09-09T10:00:00Z",
  "revokedAt": null
}
```

### PUT /api/shared-variable-clients/{id}

Update API-client metadata and scopes using optimistic concurrency.

**Access:** Shared-variable administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**JSON body:** [UpdateSharedVariableClientRequest](#schema-updatesharedvariableclientrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [SharedVariableClientDto](#schema-sharedvariableclientdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict; `413` Payload Too Large; `415` Unsupported Media Type. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
PUT /api/shared-variable-clients/701 HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "displayName": "Threshold integration",
  "scopes": [
    "shared-variables.read"
  ],
  "expiresAt": null,
  "expectedRevision": 1
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "id": 701,
  "clientId": "threshold-service",
  "displayName": "Threshold integration",
  "scopes": [
    "shared-variables.read"
  ],
  "status": "active",
  "revision": 2,
  "expiresAt": null,
  "createdAt": "2026-09-09T10:00:00Z",
  "updatedAt": "2026-09-09T10:00:00Z",
  "revokedAt": null
}
```

### POST /api/shared-variable-clients/{id}/rotate

Rotate an API-client secret with a zero-to-seven-day grace period.

**Access:** Shared-variable administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**JSON body:** [RotateSharedVariableClientSecretRequest](#schema-rotatesharedvariableclientsecretrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [RotateSharedVariableClientSecretResult](#schema-rotatesharedvariableclientsecretresult).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict; `413` Payload Too Large; `415` Unsupported Media Type. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/shared-variable-clients/701/rotate HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "expectedRevision": 1,
  "gracePeriodHours": 24
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Cache-Control: no-store
Pragma: no-cache
Content-Type: application/json

{
  "client": {
    "id": 701,
    "clientId": "threshold-service",
    "displayName": "Threshold integration",
    "scopes": [
      "shared-variables.read"
    ],
    "status": "active",
    "revision": 2,
    "expiresAt": null,
    "createdAt": "2026-09-09T10:00:00Z",
    "updatedAt": "2026-09-09T10:00:00Z",
    "revokedAt": null
  },
  "clientSecret": "ONE_TIME_GENERATED_REPLACEMENT_SECRET"
}
```

### POST /api/shared-variable-clients/{id}/revoke

Immediately revoke an API client and every active secret.

**Access:** Shared-variable administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `id` | path | integer (int64) | Yes | Required route identifier; use the actual returned ID. |

**JSON body:** [RevokeSharedVariableClientRequest](#schema-revokesharedvariableclientrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [SharedVariableClientDto](#schema-sharedvariableclientdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `404` Not Found; `409` Conflict; `413` Payload Too Large; `415` Unsupported Media Type. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/shared-variable-clients/701/revoke HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "expectedRevision": 2,
  "reason": "Retire integration"
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "id": 701,
  "clientId": "threshold-service",
  "displayName": "Threshold integration",
  "scopes": [
    "shared-variables.read"
  ],
  "status": "revoked",
  "revision": 3,
  "expiresAt": null,
  "createdAt": "2026-09-09T10:00:00Z",
  "updatedAt": "2026-09-09T10:00:00Z",
  "revokedAt": "2026-09-09T10:00:00Z"
}
```

## Retention

Settings administrator permission is required; mutations additionally require a nonblank actor. Categories are `workflowHistory`, `variableHistory`, `nodeActivity`, `administrativeAudits`, `sharedVariableHistory`, `completedJobs`, `resolvedIncidents`. `retentionDays:null` means keep forever. Updates use `expectedRevision`. Preview evaluates a proposed period without saving or deleting; it may be a bounded lower estimate (`isLowerBound`). `/runs` queues or joins a cleanup run using saved policies and returns `202 Location: /api/retention`. The Worker applies bounded deletion and load protection. Pruning instance-owned history permanently prevents reactivation; retained current state and protected audit references are not an instruction to purge an entire workflow.

### GET /api/retention

Get retention policies and cleanup progress.

**Access:** Settings administrator. See [authentication](#http-conventions-and-authentication).

**Parameters:** none.

**Body:** none.

**Success:** `200` [RetentionStatusDto](#schema-retentionstatusdto).
**Errors:** `401` Unauthorized; `403` Forbidden. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
GET /api/retention HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "policies": [],
  "currentRun": null,
  "lastRun": null,
  "nextScheduledAt": null,
  "workerLastSeenAt": "2026-09-09T10:00:00Z"
}
```

### PUT /api/retention/policies/{category}

Update a retention period with an optimistic policy revision.

**Access:** Settings administrator. See [authentication](#http-conventions-and-authentication).

| Parameter | Location | Type | Required | Meaning / default |
| --- | --- | --- | --- | --- |
| `category` | path | string | Yes | Retention category listed in the family introduction. |

**JSON body:** [UpdateRetentionPolicyRequest](#schema-updateretentionpolicyrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [RetentionPolicyDto](#schema-retentionpolicydto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
PUT /api/retention/policies/completedJobs HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "retentionDays": 90,
  "expectedRevision": 1
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "category": "completedJobs",
  "retentionDays": 90,
  "revision": 2,
  "updatedAt": "2026-09-09T10:00:00Z",
  "updatedBy": "admin@example.com",
  "isInitialized": true
}
```

### POST /api/retention/preview

Preview eligible and protected rows without saving or deleting.

**Access:** Settings administrator. See [authentication](#http-conventions-and-authentication).

**Parameters:** none.

**JSON body:** [PreviewRetentionRequest](#schema-previewretentionrequest). Required JSON object; field presence, nullability and defaults are in the linked schema.

**Success:** `200` [RetentionPreviewDto](#schema-retentionpreviewdto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden. See [error envelopes](#errors-and-concurrency) and the family rules above.

```http
POST /api/retention/preview HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
Content-Type: application/json

{
  "category": "completedJobs",
  "retentionDays": 90
}
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 200 OK
Content-Type: application/json

{
  "category": "completedJobs",
  "cutoff": "2026-06-11T10:00:00Z",
  "tables": [],
  "isLowerBound": false,
  "observedAt": "2026-09-09T10:00:00Z"
}
```

### POST /api/retention/runs

Queue or join cleanup using the saved retention policies.

**Access:** Settings administrator. See [authentication](#http-conventions-and-authentication).

**Parameters:** none.

**Body:** none.

**Success:** `202` [RetentionRunDto](#schema-retentionrundto).
**Errors:** `400` Bad Request; `401` Unauthorized; `403` Forbidden; `409` Conflict. See [error envelopes](#errors-and-concurrency) and the family rules above.

No request body. This is an asynchronous cleanup request, not an immediate purge.

```http
POST /api/retention/runs HTTP/1.1
Host: localhost:5017
Authorization: Bearer TOKEN
```

Response example (object bodies may be excerpts; see the full linked schema):

```http
HTTP/1.1 202 Accepted
Location: /api/retention
Content-Type: application/json

{
  "id": "11111111-1111-4111-8111-111111111111",
  "status": "queued",
  "requestedAt": "2026-09-09T10:00:00Z",
  "requestedBy": "admin@example.com",
  "startedAt": null,
  "completedAt": null,
  "message": null,
  "categories": []
}
```

## Shared JSON schemas

These are the JSON contracts shared by the endpoints above. For request schemas, **Required** identifies required inputs to the operation; **No** means omission is allowed, or the field is conditionally required as described. In particular, StartInstanceRequest has optional individual selectors but requires exactly one of workflowId/workflowKey. Nullable fields accept null only where the surrounding business rule permits it. Definition-model properties have defaults, while node-specific graph/configuration requirements are described in [BPMN support](bpmn-support.md). Response tables mark core fields; optional null properties may be omitted as noted. Numeric values are emitted as JSON numbers; .NET input binding also accepts numeric strings for numeric fields represented that way in OpenAPI. All date-time values use ISO 8601 offsets.

The tables use running development OpenAPI for wire types and source DTOs/handlers for semantic requirements and defaults. OpenAPI marks all positional record constructor parameters required even when actual request deserialization permits omission; the request tables correct that metadata. The default slim start schema is included explicitly because OpenAPI registers only one of the two `201` start response representations. Expand a schema to see every property.

<a id="schema-actorcontextdto"></a>

<details>
<summary>ActorContextDto</summary>

The server-resolved identity used for authenticated workflow operations.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `user` | null or string | Yes | — |
| `roles` | array of string | Yes | Actual normalized roles of the authenticated actor. An empty array means the actor has no role claims. |

</details>

<a id="schema-administrativeactionbatchdetaildto"></a>

<details>
<summary>AdministrativeActionBatchDetailDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `summary` | [AdministrativeActionBatchSummaryDto](#schema-administrativeactionbatchsummarydto) | Yes | — |
| `action` | [AdministrativeActionSummaryDto](#schema-administrativeactionsummarydto) | Yes | — |
| `commonVariables` | object | Yes | — |
| `selection` | [JsonElement](#schema-jsonelement) | Yes | — |
| `preparedByRoles` | array of string | Yes | — |
| `confirmedByRoles` | array of string or null | Yes | — |
| `issues` | null or [JsonElement](#schema-jsonelement) | Yes | — |
| `preparationJobId` | null or integer (int64) | Yes | — |
| `executionJobId` | null or integer (int64) | Yes | — |
| `cancelledBy` | null or string | Yes | — |
| `cancellationReason` | null or string | Yes | — |
| `preparedAt` | null or string (date-time) | Yes | — |
| `confirmedAt` | null or string (date-time) | Yes | — |
| `startedAt` | null or string (date-time) | Yes | — |
| `cancelledAt` | null or string (date-time) | Yes | — |

</details>

<a id="schema-administrativeactionbatchitemdto"></a>

<details>
<summary>AdministrativeActionBatchItemDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | — |
| `batchId` | integer (int64) | Yes | — |
| `positionKind` | string | Yes | — |
| `positionId` | integer (int64) | Yes | — |
| `instanceId` | integer (int64) | Yes | Runtime workflow-instance database ID. |
| `userTaskId` | null or integer (int64) | Yes | Exact runtime child/normal work-item database ID. |
| `multiInstanceExecutionId` | null or integer (int64) | Yes | — |
| `tokenId` | integer (int64) | Yes | Runtime execution-token database ID. |
| `tokenActivationId` | string (uuid) | Yes | — |
| `workflowDefinitionId` | integer (int64) | Yes | Exact immutable workflow-version database ID. |
| `sourceNodeId` | integer (int32) | Yes | — |
| `flowId` | integer (int32) | Yes | — |
| `capturedPositionUpdatedAt` | string (date-time) | Yes | — |
| `timerSubscriptionId` | null or integer (int64) | Yes | — |
| `timerJobId` | null or integer (int64) | Yes | — |
| `capturedTimerOccurrence` | null or integer (int64) | Yes | — |
| `capturedTimerStatus` | null or string | Yes | — |
| `capturedTimerSubscriptionUpdatedAt` | null or string (date-time) | Yes | — |
| `affectedTaskCount` | integer (int32) | Yes | — |
| `status` | string | Yes | Lifecycle value; see this resource family’s vocabulary. |
| `issues` | null or [JsonElement](#schema-jsonelement) | Yes | — |
| `result` | null or [JsonElement](#schema-jsonelement) | Yes | — |
| `errorCode` | null or string | Yes | — |
| `errorDescription` | null or string | Yes | — |
| `createdAt` | string (date-time) | Yes | Server creation timestamp. |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |
| `preparedAt` | null or string (date-time) | Yes | — |
| `startedAt` | null or string (date-time) | Yes | — |
| `completedAt` | null or string (date-time) | Yes | Nullable until completed; ISO 8601 timestamp. |

</details>

<a id="schema-administrativeactionbatchselectiondto"></a>

<details>
<summary>AdministrativeActionBatchSelectionDto</summary>

mode explicit uses positions; mode allMatching uses allMatching plus optional excludedPositions. Each positionKind/positionId pair identifies exact normal task or MI parent work.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `mode` | string | Yes | explicit or allMatching. |
| `positions` | array of [AdministrativeActionPositionReferenceDto](#schema-administrativeactionpositionreferencedto) or null | No | — |
| `allMatching` | null or [AdministrativeActionCandidateSearchRequest](#schema-administrativeactioncandidatesearchrequest) | No | — |
| `excludedPositions` | array of [AdministrativeActionPositionReferenceDto](#schema-administrativeactionpositionreferencedto) or null | No | — |

</details>

<a id="schema-administrativeactionbatchsummarydto"></a>

<details>
<summary>AdministrativeActionBatchSummaryDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | — |
| `workflowKey` | string | Yes | Stable authored workflow-family key. |
| `workflowDefinitionId` | integer (int64) | Yes | Exact immutable workflow-version database ID. |
| `workflowVersion` | integer (int32) | Yes | — |
| `sourceNodeId` | integer (int32) | Yes | — |
| `sourceNodeName` | string | Yes | — |
| `actionKind` | string | Yes | — |
| `flowId` | integer (int32) | Yes | — |
| `boundaryNodeId` | null or integer (int32) | Yes | — |
| `multiInstanceMode` | null or string | Yes | — |
| `reason` | null or string | Yes | Human-readable audit reason; required where non-null in version-change/reactivation requests. |
| `status` | string | Yes | Lifecycle value; see this resource family’s vocabulary. |
| `preparedBy` | string | Yes | — |
| `confirmedBy` | null or string | Yes | — |
| `totalItemCount` | integer (int32) | Yes | — |
| `totalAffectedTaskCount` | integer (int32) | Yes | — |
| `eligibleItemCount` | integer (int32) | Yes | — |
| `ineligibleItemCount` | integer (int32) | Yes | — |
| `queuedItemCount` | integer (int32) | Yes | — |
| `succeededItemCount` | integer (int32) | Yes | — |
| `skippedItemCount` | integer (int32) | Yes | — |
| `failedItemCount` | integer (int32) | Yes | — |
| `cancelledItemCount` | integer (int32) | Yes | — |
| `createdAt` | string (date-time) | Yes | Server creation timestamp. |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |
| `completedAt` | null or string (date-time) | Yes | Nullable until completed; ISO 8601 timestamp. |

</details>

<a id="schema-administrativeactioncandidatedto"></a>

<details>
<summary>AdministrativeActionCandidateDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `positionKind` | string | Yes | — |
| `positionId` | integer (int64) | Yes | — |
| `userTaskId` | null or integer (int64) | Yes | Exact runtime child/normal work-item database ID. |
| `multiInstanceExecutionId` | null or integer (int64) | Yes | — |
| `instanceId` | integer (int64) | Yes | Runtime workflow-instance database ID. |
| `tokenId` | integer (int64) | Yes | Runtime execution-token database ID. |
| `tokenActivationId` | string (uuid) | Yes | — |
| `workflowDefinitionId` | integer (int64) | Yes | Exact immutable workflow-version database ID. |
| `workflowVersion` | integer (int32) | Yes | — |
| `workflowKey` | string | Yes | Stable authored workflow-family key. |
| `businessKey` | null or string | Yes | — |
| `nodeId` | integer (int32) | Yes | — |
| `nodeName` | string | Yes | — |
| `nodeExternalId` | null or string | Yes | — |
| `positionUpdatedAt` | string (date-time) | Yes | — |
| `affectedTaskCount` | integer (int32) | Yes | — |
| `timerBoundaries` | array of [AdministrativeTimerBoundaryStateDto](#schema-administrativetimerboundarystatedto) | Yes | — |
| `variables` | null or object | No | Runtime name/value map or write array as typed here; input contracts and raw administrative writes have different rules. |

</details>

<a id="schema-administrativeactioncandidatesearchrequest"></a>

<details>
<summary>AdministrativeActionCandidateSearchRequest</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `workflowDefinitionId` | integer (int64) | Yes | Exact immutable workflow-version database ID. |
| `sourceNodeId` | integer (int32) | Yes | — |
| `positionKind` | null or string | No | — |
| `positionId` | null or integer (int64) | No | — |
| `instanceId` | null or integer (int64) | No | Runtime workflow-instance database ID. |
| `businessKey` | null or string | No | — |
| `variableFilter` | null or [JsonElement](#schema-jsonelement) | No | Optional typed latest-instance-value predicate; see Filtering and pagination. |
| `excludedPositions` | array of [AdministrativeActionPositionReferenceDto](#schema-administrativeactionpositionreferencedto) or null | No | — |
| `includeVariables` | null or boolean | No | Default false. |
| `page` | null or integer (int32) | No | Default 1. |
| `pageSize` | null or integer (int32) | No | Default 50, maximum 200. |

</details>

<a id="schema-administrativeactionpositionreferencedto"></a>

<details>
<summary>AdministrativeActionPositionReferenceDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `positionKind` | string | Yes | userTask or multiInstanceExecution. |
| `positionId` | integer (int64) | Yes | — |

</details>

<a id="schema-administrativeactionsourcenodedto"></a>

<details>
<summary>AdministrativeActionSourceNodeDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `workflowDefinitionId` | integer (int64) | Yes | Exact immutable workflow-version database ID. |
| `workflowVersion` | integer (int32) | Yes | — |
| `nodeId` | integer (int32) | Yes | — |
| `name` | string | Yes | — |
| `externalId` | null or string | Yes | — |
| `isMultiInstance` | boolean | Yes | — |

</details>

<a id="schema-administrativeactionresultdto"></a>

<details>
<summary>AdministrativeActionResultDto</summary>

Result of one committed direct instance administrative action. The audit batch contains one succeeded item and is already completed.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `instance` | [InstanceDetailDto](#schema-instancedetaildto) | Yes | Actual instance state after routing, including execution positions and history. |
| `positionKind` | string | Yes | `userTask` or `multiInstanceExecution`. |
| `positionId` | integer (int64) | Yes | Selected ordinary-task or parent-execution ID. |
| `affectedTaskCount` | integer (int32) | Yes | One ordinary task or the number of unfinished children affected. |
| `administrativeActionBatchId` | integer (int64) | Yes | Server-created audit reference; use batch detail/items for audit. |

</details>

<a id="schema-executeinstanceadministrativeactionrequest"></a>

<details>
<summary>ExecuteInstanceAdministrativeActionRequest</summary>

All identifiers are positive. The server derives actor identity, roles, and audit IDs. The maximum request body is 1 MiB.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `expectedWorkflowDefinitionId` | integer (int64) | Yes | Exact immutable definition ID from `position.workflowDefinitionId`. |
| `sourceNodeId` | integer (int32) | Yes | Authored source ID from `position.nodeId`. |
| `positionKind` | string | Yes | Exactly `userTask` or `multiInstanceExecution`. |
| `positionId` | integer (int64) | Yes | Exact displayed ordinary-task or parent-execution ID. |
| `flowId` | integer (int32) | Yes | Selectable non-default direct flow from the position's `actions`. |
| `expectedTokenId` | integer (int64) | Yes | `position.tokenId`. |
| `expectedTokenActivationId` | string (uuid) | Yes | Nonempty `position.tokenActivationId`. |
| `expectedPositionUpdatedAt` | string (date-time) | Yes | Exact `position.positionUpdatedAt`, not a client timestamp. |
| `expectedAffectedTaskCount` | integer (int32) | Yes | Positive displayed count; must still match under the runtime locks. |
| `multiInstanceMode` | null or string | No | Required for a multi-instance parent: `forceParent` or `completeAllChildren`; absent/null for an ordinary task. |
| `reason` | null or string | No | Optional trimmed reason, at most 1,000 Unicode scalar values. |
| `variables` | null or object | No | Declared action input name/value map. Required fields must be supplied; unknown/duplicate names and invalid typed values are rejected. |

</details>

<a id="schema-instanceadministrativeactionpositiondto"></a>

<details>
<summary>InstanceAdministrativeActionPositionDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `position` | [AdministrativeActionCandidateDto](#schema-administrativeactioncandidatedto) | Yes | Active ordinary task or one multi-instance parent, including immutable-version and concurrency values. |
| `actions` | array of [AdministrativeActionSummaryDto](#schema-administrativeactionsummarydto) | Yes | Selectable, non-default direct flows; may be empty. Timer actions are excluded. |

</details>

<a id="schema-pagedresultofinstanceadministrativeactionpositiondto"></a>

<details>
<summary>PagedResultOfInstanceAdministrativeActionPositionDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `items` | array of [InstanceAdministrativeActionPositionDto](#schema-instanceadministrativeactionpositiondto) | Yes | Positions on this page. |
| `page` | integer (int32) | Yes | One-based numbered page. |
| `pageSize` | integer (int32) | Yes | Maximum positions per page, 1–200. |
| `totalCount` | integer (int64) | Yes | Database count of all matching active positions. |
| `nextCursor` | null or string | No | Omitted for this numbered-page endpoint. |

</details>

<a id="schema-administrativeactionsummarydto"></a>

<details>
<summary>AdministrativeActionSummaryDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `workflowDefinitionId` | integer (int64) | Yes | Exact immutable workflow-version database ID. |
| `workflowVersion` | integer (int32) | Yes | — |
| `actionKind` | string | Yes | — |
| `flowId` | integer (int32) | Yes | — |
| `flowExternalId` | null or string | Yes | — |
| `name` | string | Yes | — |
| `sourceNodeId` | integer (int32) | Yes | — |
| `sourceNodeName` | string | Yes | — |
| `targetNodeId` | integer (int32) | Yes | — |
| `targetNodeName` | string | Yes | — |
| `targetNodeType` | string | Yes | — |
| `variables` | array of [VariableModel](#schema-variablemodel) | Yes | Runtime name/value map or write array as typed here; input contracts and raw administrative writes have different rules. |
| `condition` | null or string | No | — |
| `roles` | array of string | No | Role strings; this schema determines whether these represent actor claims or a configured permission list. |
| `boundaryNodeId` | null or integer (int32) | No | — |
| `boundaryNodeName` | null or string | No | — |
| `timer` | null or [TimerDefinitionModel](#schema-timerdefinitionmodel) | No | — |
| `authoredCancelActivity` | null or boolean | No | — |

</details>

<a id="schema-administrativetimerboundarystatedto"></a>

<details>
<summary>AdministrativeTimerBoundaryStateDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `boundaryNodeId` | integer (int32) | Yes | — |
| `timerSubscriptionId` | null or integer (int64) | Yes | — |
| `timerJobId` | null or integer (int64) | Yes | — |
| `status` | null or string | Yes | Lifecycle value; see this resource family’s vocabulary. |
| `nextDueAt` | null or string (date-time) | Yes | — |
| `occurrence` | null or integer (int64) | Yes | — |
| `updatedAt` | null or string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |
| `eligible` | boolean | Yes | — |

</details>

<a id="schema-archivesharedvariablerequest"></a>

<details>
<summary>ArchiveSharedVariableRequest</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `expectedRevision` | integer (int64) | Yes | Exact current revision from a preceding read. |
| `requestId` | null or string | No | Optional mutation retry identifier; reuse only for the identical logical request. |
| `reason` | null or string | No | Human-readable audit reason; required where non-null in version-change/reactivation requests. |

</details>

<a id="schema-assignmentmodel"></a>

<details>
<summary>AssignmentModel</summary>

Represents a variable assignment inside a script task.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `variable` | string | No | The name of the process variable to assign the value to. |
| `expression` | string | No | The expression (NCalc) to evaluate and assign. |

</details>

<a id="schema-assignusertaskrequest"></a>

<details>
<summary>AssignUserTaskRequest</summary>

actorId is nullable in the transport schema but assignment requires a nonblank normalized actor, limited to 300 characters. expectedUpdatedAt is mandatory for concurrency.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `actorId` | null or string | Yes | — |
| `expectedUpdatedAt` | string (date-time) | Yes | Exact server-returned timestamp for optimistic concurrency. |
| `reason` | null or string | No | Human-readable audit reason; required where non-null in version-change/reactivation requests. |

</details>

<a id="schema-businesskeymodel"></a>

<details>
<summary>BusinessKeyModel</summary>

Configures the start variable used as a workflow-family business key.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `variable` | string | No | Required scalar string input variable of this start event (message starts use a required mapped field). |
| `uniqueness` | string | No | active (unique among running instances) or all (permanent uniqueness across the workflow family). |

</details>

<a id="schema-canceladministrativeactionbatchrequest"></a>

<details>
<summary>CancelAdministrativeActionBatchRequest</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `reason` | null or string | No | Human-readable audit reason; required where non-null in version-change/reactivation requests. |

</details>

<a id="schema-cancelinstancevariableupdatebatchrequest"></a>

<details>
<summary>CancelInstanceVariableUpdateBatchRequest</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `reason` | null or string | No | Human-readable audit reason; required where non-null in version-change/reactivation requests. |

</details>

<a id="schema-cancelinstanceversionchangebatchrequest"></a>

<details>
<summary>CancelInstanceVersionChangeBatchRequest</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `reason` | null or string | No | Human-readable audit reason; required where non-null in version-change/reactivation requests. |

</details>

<a id="schema-changeinstanceversionrequest"></a>

<details>
<summary>ChangeInstanceVersionRequest</summary>

Request payload for atomically changing a running instance's workflow version.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `targetWorkflowId` | integer (int64) | Yes | Exact target workflow-version database ID. |
| `expectedSourceWorkflowId` | integer (int64) | Yes | — |
| `expectedUpdatedAt` | string (date-time) | Yes | Exact server-returned timestamp for optimistic concurrency. |
| `reason` | string | Yes | Human-readable audit reason; required where non-null in version-change/reactivation requests. |

</details>

<a id="schema-changeinstanceversionresultdto"></a>

<details>
<summary>ChangeInstanceVersionResultDto</summary>

The updated instance and audit entry returned after a workflow version change.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `instance` | [InstanceDetailDto](#schema-instancedetaildto) | Yes | — |
| `versionChange` | [InstanceVersionChangeAuditDto](#schema-instanceversionchangeauditdto) | Yes | — |

</details>

<a id="schema-changeusertaskflowrolesrequest"></a>

<details>
<summary>ChangeUserTaskFlowRolesRequest</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `flowId` | integer (int32) | Yes | — |
| `roles` | array of string | Yes | Replacement action allowlist; same limits and explicit-empty behavior as task roles. |

</details>

<a id="schema-changeusertaskrolesrequest"></a>

<details>
<summary>ChangeUserTaskRolesRequest</summary>

Replaces every editable role list for the specified waiting activation.

Supply all editable action role lists from GET /roles. roles/flows cannot be null; empty role lists are intentionally unrestricted. Lists: at most 100 roles, at most 300 Unicode scalars per normalized role. reason is optional.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `expectedRolePolicyId` | integer (int64) | Yes | Exact current immutable policy ID; complete replacement, with identical-stale retry support. |
| `roles` | array of string | Yes | Replacement task allowlist; explicit empty is unrestricted. At most 100 roles, each at most 300 Unicode scalars after trimming. |
| `flows` | array of [ChangeUserTaskFlowRolesRequest](#schema-changeusertaskflowrolesrequest) | Yes | Every editable selectable non-default action for role replacement. |
| `reason` | null or string | No | Human-readable audit reason; required where non-null in version-change/reactivation requests. |

</details>

<a id="schema-completioninfodto"></a>

<details>
<summary>CompletionInfoDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `kind` | string | Yes | — |
| `tokenId` | integer (int64) | Yes | Runtime execution-token database ID. |
| `nodeId` | integer (int32) | Yes | — |
| `nodeName` | string | Yes | — |
| `nodeExternalId` | null or string | Yes | — |
| `completedAt` | string (date-time) | Yes | Nullable until completed; ISO 8601 timestamp. |

</details>

<a id="schema-complexgatewaystatedto"></a>

<details>
<summary>ComplexGatewayStateDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `gatewayNodeId` | integer (int32) | Yes | — |
| `phase` | string | Yes | waitingForStart, waitingForReset, or interruptedDraining. |
| `cycle` | integer (int32) | Yes | — |
| `contributingFlowIds` | array of integer (int32) | Yes | — |
| `remainingFlowIds` | array of integer (int32) | Yes | — |
| `drainingTokenIds` | array of integer (int64) | Yes | — |
| `activeExecutionId` | null or integer (int64) | Yes | — |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |

</details>

<a id="schema-conditionaldefinitionmodel"></a>

<details>
<summary>ConditionalDefinitionModel</summary>

Defines an intermediate conditional catch or conditional boundary event. The condition is an NCalc expression over statically declared, persisted instance variables. A missing delivery mode has the same meaning as `atomic`.

deliveryMode: atomic (default) or durableAsync. Conditions are bounded observable persisted instance-variable expressions; see BPMN support.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `condition` | string | No | — |
| `deliveryMode` | null or string | No | atomic (default) or durableAsync. |

</details>

<a id="schema-confirmadministrativeactionbatchrequest"></a>

<details>
<summary>ConfirmAdministrativeActionBatchRequest</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `expectedEligibleItemCount` | integer (int32) | Yes | — |
| `expectedAffectedTaskCount` | integer (int32) | Yes | — |
| `expectedBatchUpdatedAt` | string (date-time) | Yes | Exact ready-batch summary.updatedAt; do not use a client timestamp. |

</details>

<a id="schema-confirminstancevariableupdatebatchrequest"></a>

<details>
<summary>ConfirmInstanceVariableUpdateBatchRequest</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `expectedEligibleItemCount` | integer (int32) | Yes | — |
| `expectedIneligibleItemCount` | integer (int32) | Yes | — |
| `expectedWarningItemCount` | integer (int32) | Yes | — |
| `expectedBatchUpdatedAt` | string (date-time) | Yes | Exact ready-batch summary.updatedAt; do not use a client timestamp. |

</details>

<a id="schema-confirminstanceversionchangebatchrequest"></a>

<details>
<summary>ConfirmInstanceVersionChangeBatchRequest</summary>

Confirms the exact prepared population using server-returned counts and an optimistic batch timestamp.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `expectedEligibleItemCount` | integer (int32) | Yes | — |
| `expectedIneligibleItemCount` | integer (int32) | Yes | — |
| `expectedWarningItemCount` | integer (int32) | Yes | — |
| `expectedBatchUpdatedAt` | string (date-time) | Yes | Exact ready-batch summary.updatedAt; do not use a client timestamp. |

</details>

<a id="schema-createadministrativeactionbatchrequest"></a>

<details>
<summary>CreateAdministrativeActionBatchRequest</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `workflowDefinitionId` | integer (int64) | Yes | Exact immutable workflow-version database ID. |
| `sourceNodeId` | integer (int32) | Yes | — |
| `actionKind` | string | Yes | directFlow or timerBoundary. |
| `flowId` | integer (int32) | Yes | — |
| `boundaryNodeId` | null or integer (int32) | No | Required for timerBoundary actions; null for directFlow. |
| `multiInstanceMode` | null or string | No | Required forceParent or completeAllChildren for directFlow on MI; null/omitted for normal tasks and all timerBoundary actions. |
| `reason` | null or string | No | Human-readable audit reason; required where non-null in version-change/reactivation requests. |
| `variables` | null or object | No | Runtime name/value map or write array as typed here; input contracts and raw administrative writes have different rules. |
| `selection` | [AdministrativeActionBatchSelectionDto](#schema-administrativeactionbatchselectiondto) | Yes | — |
| `idempotencyKey` | null or string | No | Optional logical-operation retry key. Not the start-node HTTP idempotency header. |

</details>

<a id="schema-createenginesettingrequest"></a>

<details>
<summary>CreateEngineSettingRequest</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `namespace` | null or string | No | — |
| `key` | string | Yes | — |
| `value` | string | Yes | — |
| `description` | null or string | No | — |

</details>

<a id="schema-createinstancevariableupdatebatchrequest"></a>

<details>
<summary>CreateInstanceVariableUpdateBatchRequest</summary>

Freezes and asynchronously prepares a variable-update batch.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `workflowKey` | string | Yes | Stable authored workflow-family key. |
| `variables` | array of [InstanceVariableWriteDto](#schema-instancevariablewritedto) | Yes | Runtime name/value map or write array as typed here; input contracts and raw administrative writes have different rules. |
| `reason` | null or string | No | Human-readable audit reason; required where non-null in version-change/reactivation requests. |
| `selection` | [InstanceVariableUpdateBatchSelectionDto](#schema-instancevariableupdatebatchselectiondto) | Yes | — |
| `idempotencyKey` | null or string | No | Optional logical-operation retry key. Not the start-node HTTP idempotency header. |

</details>

<a id="schema-createinstanceversionchangebatchrequest"></a>

<details>
<summary>CreateInstanceVersionChangeBatchRequest</summary>

Creates and asynchronously prepares a version-change batch.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `sourceWorkflowId` | integer (int64) | Yes | Exact source workflow-version database ID. |
| `targetWorkflowId` | integer (int64) | Yes | Exact target workflow-version database ID. |
| `reason` | string | Yes | Human-readable audit reason; required where non-null in version-change/reactivation requests. |
| `selection` | [InstanceVersionChangeBatchSelectionDto](#schema-instanceversionchangebatchselectiondto) | Yes | — |
| `idempotencyKey` | null or string | No | Optional logical-operation retry key. Not the start-node HTTP idempotency header. |

</details>

<a id="schema-createmanageduserdelegationrequest"></a>

<details>
<summary>CreateManagedUserDelegationRequest</summary>

Administrative form of delegation creation. The authenticated administrator remains the creation actor while string CreateManagedUserDelegationRequest.Delegator is the represented user.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `delegator` | string | Yes | — |
| `delegate` | string | Yes | — |
| `workflowKeys` | array of string | Yes | — |
| `validFrom` | string (date-time) | Yes | — |
| `validUntil` | string (date-time) | Yes | Required finite instant later than validFrom and in the future. |
| `reason` | null or string | No | Human-readable audit reason; required where non-null in version-change/reactivation requests. |

</details>

<a id="schema-createsharedvariableclientrequest"></a>

<details>
<summary>CreateSharedVariableClientRequest</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `clientId` | string | Yes | Trimmed immutable identifier: 3–100 ASCII characters from A–Z, a–z, 0–9, dot, underscore, colon or hyphen. Case-sensitive. |
| `displayName` | string | Yes | Required nonblank name, at most 300 Unicode scalars. |
| `scopes` | array of string | Yes | Nonempty list of exact-case shared-variables.read and/or shared-variables.write. |
| `expiresAt` | null or string (date-time) | No | Null or a future ISO 8601 instant. |

</details>

<a id="schema-createsharedvariableclientresult"></a>

<details>
<summary>CreateSharedVariableClientResult</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `client` | [SharedVariableClientDto](#schema-sharedvariableclientdto) | Yes | — |
| `clientSecret` | string | Yes | Returned only by creation/rotation; never recoverable from metadata. |

</details>

<a id="schema-createsharedvariablerequest"></a>

<details>
<summary>CreateSharedVariableRequest</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `key` | string | Yes | Nonblank key, at most 300 Unicode scalars; reserved sys./config./setting./mi./gateway. prefixes are prohibited. |
| `dataType` | string | Yes | string, number, boolean, date, datetime, or json; arrays use isArray. |
| `isArray` | boolean | No | — Default: `false`. |
| `nullable` | boolean | No | — Default: `false`. |
| `hasValue` | boolean | No | False means unset; true with null is an explicit null value. Default: `false`. |
| `value` | null or [JsonElement](#schema-jsonelement) | No | Conditionally required when hasValue is true; omit when creating an unset variable. See the create-null limitation in Shared variables. |
| `validation` | null or string | No | Optional expression at most 4000 Unicode scalars using the parameter value; no alias-key binding. Explicit permitted null skips validation. |
| `description` | null or string | No | — |
| `requestId` | null or string | No | Optional identifier up to 300 scalars, scoped by caller kind and caller ID across keys. Identical retries return the original result; reuse for a different key/operation/body returns 409. |
| `reason` | null or string | No | Human-readable audit reason; required where non-null in version-change/reactivation requests. |

</details>

<a id="schema-createuserdelegationrequest"></a>

<details>
<summary>CreateUserDelegationRequest</summary>

Creates one standing delegation grant for each requested workflow family. The authenticated caller is always used as the delegator.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `delegate` | string | Yes | — |
| `workflowKeys` | array of string | Yes | — |
| `validFrom` | string (date-time) | Yes | — |
| `validUntil` | string (date-time) | Yes | Required finite instant later than validFrom and in the future. |
| `reason` | null or string | No | Human-readable audit reason; required where non-null in version-change/reactivation requests. |

</details>

<a id="schema-createworkflowrequest"></a>

<details>
<summary>CreateWorkflowRequest</summary>

Request payload for creating a new workflow definition.

definition is the complete editor JSON. publish defaults false. The engine validates graph, expressions, role sources, variables and feature availability before saving.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `definition` | [WorkflowModel](#schema-workflowmodel) | Yes | The structural JSON model of the workflow to create. |
| `publish` | boolean | No | Whether to publish this workflow immediately after creation. Default: `false`. |

</details>

<a id="schema-createworkflowsettingrequest"></a>

<details>
<summary>CreateWorkflowSettingRequest</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `namespace` | null or string | No | — |
| `name` | string | Yes | — |
| `value` | [JsonElement](#schema-jsonelement) | Yes | — |
| `description` | null or string | No | — |

</details>

<a id="schema-delegatedtaskaccessdto"></a>

<details>
<summary>DelegatedTaskAccessDto</summary>

Identifies a standing grant used to act on a task while preserving its original assignee or claimant.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `delegationId` | integer (int64) | Yes | — |
| `actingFor` | string | Yes | — |

</details>

<a id="schema-distributableusertasksearchrequest"></a>

<details>
<summary>DistributableUserTaskSearchRequest</summary>

Advanced task-distribution search request.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `taskId` | null or integer (int64) | No | — |
| `instanceId` | null or integer (int64) | No | Runtime workflow-instance database ID. |
| `workflowId` | null or integer (int64) | No | Exact immutable workflow-version database ID. |
| `businessKey` | null or string | No | — |
| `nodeId` | null or integer (int32) | No | — |
| `nodeExternalId` | null or string | No | — |
| `owner` | null or string | No | — |
| `ownership` | null or string | No | — |
| `variableFilter` | null or [JsonElement](#schema-jsonelement) | No | Optional typed latest-instance-value predicate; see Filtering and pagination. |
| `includeVariables` | null or boolean | No | Default false. |
| `page` | null or integer (int32) | No | Default 1. |
| `pageSize` | null or integer (int32) | No | Default 50, maximum 200. |

</details>

<a id="schema-enginesettingdto"></a>

<details>
<summary>EngineSettingDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | — |
| `namespace` | null or string | Yes | — |
| `key` | string | Yes | — |
| `value` | string | Yes | — |
| `description` | null or string | Yes | — |
| `createdAt` | string (date-time) | Yes | Server creation timestamp. |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |

</details>

<a id="schema-executionpositiondto"></a>

<details>
<summary>ExecutionPositionDto</summary>

One durable execution-token position exposed by instance APIs.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `tokenId` | integer (int64) | Yes | Runtime execution-token database ID. |
| `nodeId` | integer (int32) | Yes | — |
| `nodeName` | string | Yes | — |
| `nodeExternalId` | null or string | Yes | — |
| `nodeType` | string | Yes | — |
| `tokenStatus` | string | Yes | active, completed, faulted, cancelled, or merged. |
| `arrivedViaFlowId` | null or integer (int32) | Yes | — |
| `terminationReason` | null or string | Yes | — |
| `userTaskId` | null or integer (int64) | Yes | Exact runtime child/normal work-item database ID. |
| `multiInstanceExecutionId` | null or integer (int64) | Yes | — |
| `activationId` | null or string (uuid) | No | — |
| `waitState` | null or string | No | — |
| `waitingJobId` | null or integer (int64) | No | — |
| `waitingTimerSubscriptionId` | null or integer (int64) | No | — |

</details>

<a id="schema-faultinfodto"></a>

<details>
<summary>FaultInfoDto</summary>

Business-facing fault information snapshotted when an instance enters an error end event.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `code` | null or string | Yes | Stable authored code, or null for a legacy definition. |
| `description` | string | Yes | Authored description, falling back to the error end node name. |

</details>

<a id="schema-flownodemodel"></a>

<details>
<summary>FlowNodeModel</summary>

Represents a flow node (event, task, gateway) inside a workflow.

Type-specific requirements and defaults are in BPMN support. Missing cancelActivity means interrupting; conditional deliveryMode defaults atomic; synchronous execution is the default unless applicable async flags are enabled.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int32) | No | The unique integer ID of the node. |
| `name` | string | No | The name of the node. |
| `externalId` | null or string | No | The user-defined external ID of the node. |
| `attributes` | array of [WorkflowAttributeModel](#schema-workflowattributemodel) | No | Ordered string metadata attached to this node. |
| `type` | string | No | The type of the flow node (e.g. userTask, serviceTask, gateway, etc.). Default: `"userTask"`. |
| `asyncBefore` | boolean | No | Creates a durable transaction boundary before this task activity is activated. Omitted/false preserves the historical synchronous behavior. Default: `false`. |
| `asyncAfter` | boolean | No | Creates a durable transaction boundary after this task activity completes and before its outgoing sequence flow is traversed. Default: `false`. |
| `job` | null or [JobPolicyModel](#schema-jobpolicymodel) | No | — |
| `timer` | null or [TimerDefinitionModel](#schema-timerdefinitionmodel) | No | — |
| `conditional` | null or [ConditionalDefinitionModel](#schema-conditionaldefinitionmodel) | No | — |
| `cancelActivity` | null or boolean | No | Timer and conditional boundary events: true interrupts the attached activity; false creates a sibling branch while the activity remains active. Missing values normalize to true. |
| `laneId` | null or integer (int32) | No | The ID of the lane this node resides in. |
| `x` | integer (int32) | No | The X coordinate on the designer canvas. |
| `y` | integer (int32) | No | The Y coordinate on the designer canvas. |
| `roles` | array of string | No | The roles authorized to claim or act on this userTask. |
| `rolesVariable` | null or string | No | Declared string-array variable resolved once when this user task is created. |
| `requiresClaim` | boolean | No | Whether the task requires claiming before actions can be taken. Default: `false`. |
| `claimMode` | string | No | fresh (default), previous, or fromNode; fromNode also requires inheritClaimFromNodeId. Default: `"fresh"`. |
| `inheritClaimFromNodeId` | null or integer (int32) | No | The node ID from which to inherit the claim when claimMode is fromNode. |
| `variables` | array of [VariableModel](#schema-variablemodel) or null | No | Start input declarations. Message starts use typed outputMappings instead of a separate variables declaration section. |
| `service` | null or [ServiceTaskModel](#schema-servicetaskmodel) | No | — |
| `message` | null or [MessageCatchModel](#schema-messagecatchmodel) | No | — |
| `businessKey` | null or [BusinessKeyModel](#schema-businesskeymodel) | No | — |
| `idempotency` | null or [IdempotencyModel](#schema-idempotencymodel) | No | — |
| `scriptFormat` | string | No | ncalc (default) or javascript. Default: `"ncalc"`. |
| `assignments` | array of [AssignmentModel](#schema-assignmentmodel) | No | Ordered list of assignments executed in NCalc format. |
| `script` | null or string | No | The JavaScript code body to execute (for script tasks). |
| `usesFlowInfo` | null or boolean | No | Whether a JavaScript script task may read instance-wide sequence-flow evidence through `execution.getFlowInfo`. Null is retained only while loading older definitions so the migrator can infer the historical direct call shape; normalized definitions always carry an explicit value. |
| `assignee` | null or string | No | Optional NCalc expression that resolves the direct task assignee. |
| `requiresAssignment` | boolean | No | Whether this user task must have a direct assignee before it is exposed to, or can be acted on by, workflow users. Default: `false`. |
| `assignmentMode` | string | No | fresh (default), previous, or fromNode; fromNode also requires inheritAssignmentFromNodeId. Default: `"fresh"`. |
| `inheritAssignmentFromNodeId` | null or integer (int32) | No | The user-task node whose latest completed work item supplies an inherited assignment. |
| `inboxVisibilityCondition` | null or string | No | Optional bounded expression evaluated by PostgreSQL to decide whether this user task belongs in a caller's inbox and may be accessed by that caller. Blank values normalize to null. |
| `multiInstance` | null or [MultiInstanceModel](#schema-multiinstancemodel) | No | — |
| `attachedToRef` | null or integer (int32) | No | Authored host node ID for the boundary. Allowed hosts depend on error/timer/conditional boundary type; see BPMN support. |
| `errorVariable` | null or string | No | The variable name where the failure reason is written when caught. |
| `errorCode` | null or string | No | Stable, business-facing fault code thrown by an errorEndEvent. |
| `errorDescription` | null or string | No | Optional public description for the fault thrown by an errorEndEvent. The node name is used at runtime when this value is omitted. |
| `activationCondition` | null or string | No | Complex gateway only: the NCalc expression that decides when the current start/reset phase is enabled. IncomingCount(flowId) and TotalIncomingCount() are available while evaluating this expression. |
| `gatewayRef` | null or integer (int32) | No | Scoped interrupt event only: the split gateway whose nearest active runtime activation is interrupted when this event is entered. |
| `joinCancellation` | null or [JoinCancellationModel](#schema-joincancellationmodel) | No | — |

</details>

<a id="schema-gatewayexecutiondto"></a>

<details>
<summary>GatewayExecutionDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | — |
| `gatewayNodeId` | integer (int32) | Yes | — |
| `gatewayType` | string | Yes | — |
| `direction` | string | Yes | split or merge. |
| `phase` | null or string | Yes | start or reset for Complex firings; null for gateway executions without phases. |
| `cycle` | null or integer (int32) | Yes | — |
| `selectedFlowIds` | array of integer (int32) | Yes | — |
| `parentExecutionId` | null or integer (int64) | Yes | — |
| `status` | string | Yes | active, joined, completed, interrupted, or cancelled. |
| `completionReason` | null or string | Yes | — |
| `interruptingNodeId` | null or integer (int32) | Yes | — |
| `interruptingTokenId` | null or integer (int64) | Yes | — |
| `totalBranchCount` | integer (int32) | Yes | — |
| `activeBranchCount` | integer (int32) | Yes | — |
| `completedBranchCount` | integer (int32) | Yes | — |
| `mergedBranchCount` | integer (int32) | Yes | — |
| `interruptedBranchCount` | integer (int32) | Yes | — |
| `cancelledBranchCount` | integer (int32) | Yes | — |
| `createdAt` | string (date-time) | Yes | Server creation timestamp. |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |
| `completedAt` | null or string (date-time) | Yes | Nullable until completed; ISO 8601 timestamp. |

</details>

<a id="schema-idempotencymodel"></a>

<details>
<summary>IdempotencyModel</summary>

Configures the header-sourced transport retry key for an entry event.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `headerName` | string | No | Required HTTP header used to receive the transport key. |
| `variable` | string | No | Declared implicit required string variable; populate only from the configured HTTP header. |

</details>

<a id="schema-inboxitemdto"></a>

<details>
<summary>InboxItemDto</summary>

Represents a user task in the inbox of an actor.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `instanceId` | integer (int64) | Yes | The database ID of the workflow instance. |
| `userTaskId` | integer (int64) | Yes | The exact work-item ID represented by this inbox row. |
| `multiInstanceExecutionId` | null or integer (int64) | Yes | The owning multi-instance execution, when applicable. |
| `itemIndex` | null or integer (int32) | Yes | The zero-based multi-instance item index. |
| `itemValue` | null or [JsonElement](#schema-jsonelement) | Yes | — |
| `assignee` | null or string | Yes | The snapshotted direct username assignment, when present. |
| `multiInstance` | null or [MultiInstanceProgressDto](#schema-multiinstanceprogressdto) | Yes | — |
| `workflowId` | integer (int64) | Yes | The database ID of the workflow version. |
| `workflowName` | string | Yes | The name of the workflow. |
| `businessKey` | null or string | Yes | The normalized domain business key, when configured. |
| `businessKeyUniqueness` | null or string | Yes | The snapshotted active/all uniqueness policy. |
| `currentNodeId` | integer (int32) | Yes | The ID of the current userTask flow node. |
| `currentNodeName` | string | Yes | The name of the current userTask flow node. |
| `currentNodeExternalId` | null or string | Yes | The user-defined external ID of the current flow node. |
| `nodeRoles` | array of string | Yes | The roles allowed to claim/act on this userTask. |
| `requiresClaim` | boolean | Yes | Indicates whether the task must be claimed before taking any actions. |
| `requiresAssignment` | boolean | Yes | Indicates whether an explicit assignee is required before regular users can discover or act on the task. |
| `claimedBy` | null or string | Yes | The username of the actor who has currently claimed the task. |
| `claimedByMe` | boolean | Yes | True if the current caller is the one who claimed this task. |
| `canClaim` | boolean | Yes | True if the current caller is authorized to claim this task based on role constraints. |
| `canAct` | boolean | Yes | True if the current caller is authorized to act on this task. |
| `createdAt` | string (date-time) | Yes | Deprecated alias for TaskCreatedAt. |
| `updatedAt` | string (date-time) | Yes | Deprecated alias for TaskUpdatedAt. |
| `taskCreatedAt` | string (date-time) | Yes | The timestamp when this user-task work item was created. |
| `taskUpdatedAt` | string (date-time) | Yes | The timestamp when this user-task work item was last updated. |
| `instanceCreatedAt` | string (date-time) | Yes | The timestamp when the owning workflow instance was created. |
| `instanceUpdatedAt` | string (date-time) | Yes | The timestamp when the owning workflow instance was last updated. |
| `variables` | null or object | No | Gets the latest instance variable values when explicitly requested. |
| `sharedVariables` | array of [SharedVariableBindingMetadataDto](#schema-sharedvariablebindingmetadatadto) or null | No | — |
| `delegatedAccess` | null or [DelegatedTaskAccessDto](#schema-delegatedtaskaccessdto) | No | — |
| `attributes` | array of [WorkflowAttributeModel](#schema-workflowattributemodel) | No | Authored metadata key/value entries; no workflow authorization semantics are implied. |

</details>

<a id="schema-inboxsearchrequest"></a>

<details>
<summary>InboxSearchRequest</summary>

Advanced actor inbox search request.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `instanceId` | null or integer (int64) | No | Runtime workflow-instance database ID. |
| `workflowId` | null or integer (int64) | No | Exact immutable workflow-version database ID. |
| `workflowKey` | null or string | No | Stable authored workflow-family key. |
| `businessKey` | null or string | No | — |
| `nodeId` | null or integer (int32) | No | — |
| `nodeExternalId` | null or string | No | — |
| `variableFilter` | null or [JsonElement](#schema-jsonelement) | No | Optional typed latest-instance-value predicate; see Filtering and pagination. |
| `sort` | array of [SearchSortDto](#schema-searchsortdto) or null | No | Up to three field/direction clauses; allowed fields depend on endpoint. |
| `includeVariables` | null or boolean | No | Default false. |
| `page` | null or integer (int32) | No | Default 1. |
| `pageSize` | null or integer (int32) | No | Default 50, maximum 200. |

</details>

<a id="schema-incidentdetaildto"></a>

<details>
<summary>IncidentDetailDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `summary` | [IncidentSummaryDto](#schema-incidentsummarydto) | Yes | — |
| `job` | null or [JobSummaryDto](#schema-jobsummarydto) | Yes | — |
| `details` | null or string | Yes | — |
| `resolvedBy` | null or string | Yes | — |

</details>

<a id="schema-incidentsummarydto"></a>

<details>
<summary>IncidentSummaryDto</summary>

Bounded operations projection for an unresolved or historical workflow incident. JobId is the immutable originating job identity; after the configured job retention window a resolved incident can remain without a live job detail until its own retention window expires (initial defaults: 30 and 90 days respectively). Detailed diagnostics are available only from the detail endpoint.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | — |
| `jobId` | integer (int64) | Yes | — |
| `instanceId` | null or integer (int64) | Yes | Runtime workflow-instance database ID. |
| `workflowDefinitionId` | integer (int64) | Yes | Exact immutable workflow-version database ID. |
| `workflowKey` | string | Yes | Stable authored workflow-family key. |
| `nodeId` | integer (int32) | Yes | — |
| `nodeName` | string | Yes | — |
| `type` | string | Yes | — |
| `status` | string | Yes | open or resolved. |
| `summary` | string | Yes | — |
| `createdAt` | string (date-time) | Yes | Server creation timestamp. |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |
| `resolvedAt` | null or string (date-time) | Yes | — |

</details>

<a id="schema-instancedetaildto"></a>

<details>
<summary>InstanceDetailDto</summary>

Represents full details of a workflow instance, including variable values and history.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | The database ID of the instance. |
| `workflow` | [WorkflowDetailDto](#schema-workflowdetaildto) | Yes | The workflow definition and metadata associated with this instance. |
| `currentNodeId` | integer (int32) | Yes | The ID of the current resting flow node. |
| `currentNodeName` | string | Yes | The name of the current resting flow node. |
| `currentNodeExternalId` | null or string | Yes | The user-defined external ID of the current resting flow node. |
| `status` | string | Yes | The current execution status of the instance. |
| `businessKey` | null or string | Yes | The normalized domain business key, when configured. |
| `businessKeyUniqueness` | null or string | Yes | The snapshotted active/all uniqueness policy. |
| `startedBy` | null or string | Yes | The username of the actor who started the instance. |
| `createdAt` | string (date-time) | Yes | The timestamp when the instance was created. |
| `updatedAt` | string (date-time) | Yes | The timestamp when the instance was last updated. |
| `variables` | array of [InstanceVariableDto](#schema-instancevariabledto) | Yes | The complete list of instance variables and their values. |
| `history` | array of [InstanceHistoryDto](#schema-instancehistorydto) | Yes | The complete execution history of sequence flow hops and resting states. |
| `multiInstance` | null or [MultiInstanceProgressDto](#schema-multiinstanceprogressdto) | Yes | — |
| `userTasks` | null or [UserTaskWorkSummaryDto](#schema-usertaskworksummarydto) | Yes | — |
| `fault` | null or [FaultInfoDto](#schema-faultinfodto) | No | — |
| `finishedAt` | null or string (date-time) | No | — |
| `historyPrunedAt` | null or string (date-time) | No | — |
| `executionPositions` | array of [ExecutionPositionDto](#schema-executionpositiondto) | No | All exposed token positions; the representative currentNode fields do not imply a single active token. |
| `multiInstances` | array of [MultiInstanceProgressDto](#schema-multiinstanceprogressdto) | No | — |
| `gatewayExecutions` | array of [GatewayExecutionDto](#schema-gatewayexecutiondto) | No | — |
| `complexGatewayStates` | array of [ComplexGatewayStateDto](#schema-complexgatewaystatedto) | No | — |
| `completion` | null or [CompletionInfoDto](#schema-completioninfodto) | No | — |
| `versionChanges` | array of [InstanceVersionChangeAuditDto](#schema-instanceversionchangeauditdto) | No | — |
| `variableUpdates` | array of [InstanceVariableUpdateAuditDto](#schema-instancevariableupdateauditdto) | No | Append-only administrative variable-update operations applied to this instance, including the actor, reason, and correlated history rows. |
| `sharedVariables` | array of [SharedVariableBindingMetadataDto](#schema-sharedvariablebindingmetadatadto) | No | — |

</details>

<a id="schema-instancehistorydto"></a>

<details>
<summary>InstanceHistoryDto</summary>

Represents a single step in the execution history of a workflow instance.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | The unique database ID of the history record. |
| `tokenId` | null or integer (int64) | Yes | The execution token correlated to this history row. |
| `userTaskId` | null or integer (int64) | Yes | The user-task work item correlated to this history row. |
| `multiInstanceExecutionId` | null or integer (int64) | Yes | The multi-instance execution correlated to this row. |
| `itemIndex` | null or integer (int32) | Yes | The zero-based multi-instance item index. |
| `sequenceFlowId` | null or integer (int32) | Yes | Optional. The ID of the sequence flow taken during this step. |
| `fromNodeId` | integer (int32) | Yes | The ID of the source flow node transitioned from. |
| `toNodeId` | integer (int32) | Yes | The ID of the destination flow node transitioned to. |
| `performedBy` | null or string | Yes | The username of the actor who triggered or performed this step. |
| `actorClaims` | null or object of string arrays | No | Selected claims of the actual actor at this event. Repeated values are retained; null/absent means not recorded, `{}` means no selected claims were present. |
| `payload` | null or object | Yes | Optional. The input payload or variables submitted during the transition. |
| `note` | null or string | Yes | Optional. Execution notes describing internal hops or transition kinds. |
| `performedAt` | string (date-time) | Yes | The timestamp when this step was executed. |
| `actingFor` | null or string | No | — |
| `delegationId` | null or integer (int64) | No | — |
| `reason` | null or string | No | Human-readable audit reason; required where non-null in version-change/reactivation requests. |
| `administrativeActionBatchId` | null or integer (int64) | No | — |
| `sharedVariableWrites` | array of [SharedVariableWriteCorrelationDto](#schema-sharedvariablewritecorrelationdto) | No | Value-free shared writes committed by this transition. Shared values are deliberately excluded from the instance-history payload. |

</details>

<a id="schema-instancejobsummarydto"></a>

<details>
<summary>InstanceJobSummaryDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `openCount` | integer (int64) | Yes | — |
| `queuedCount` | integer (int64) | Yes | — |
| `runningCount` | integer (int64) | Yes | — |
| `incidentCount` | integer (int64) | Yes | — |
| `nearestDueAt` | null or string (date-time) | Yes | — |

</details>

<a id="schema-instancereactivationissuedto"></a>

<details>
<summary>InstanceReactivationIssueDto</summary>

A structured finding produced by instance-reactivation validation.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `code` | string | Yes | — |
| `message` | string | Yes | — |
| `nodeId` | null or integer (int32) | No | — |
| `stateId` | null or integer (int64) | No | — |

</details>

<a id="schema-instancereactivationpreviewdto"></a>

<details>
<summary>InstanceReactivationPreviewDto</summary>

The result of a non-mutating check for reactivating a completed or cancelled workflow instance at one of its eligible prior user tasks.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `instanceId` | integer (int64) | Yes | Runtime workflow-instance database ID. |
| `workflowId` | integer (int64) | Yes | Exact immutable workflow-version database ID. |
| `status` | string | Yes | Lifecycle value; see this resource family’s vocabulary. |
| `canReactivate` | boolean | Yes | — |
| `targets` | array of [InstanceReactivationTargetDto](#schema-instancereactivationtargetdto) | Yes | — |
| `blockers` | array of [InstanceReactivationIssueDto](#schema-instancereactivationissuedto) | Yes | — |
| `warnings` | array of [InstanceReactivationIssueDto](#schema-instancereactivationissuedto) | Yes | — |
| `expectedUpdatedAt` | string (date-time) | Yes | Exact server-returned timestamp for optimistic concurrency. |

</details>

<a id="schema-instancereactivationtargetdto"></a>

<details>
<summary>InstanceReactivationTargetDto</summary>

A previously visited, top-level ordinary user task that can receive a fresh activation when a terminal workflow instance is reactivated.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `nodeId` | integer (int32) | Yes | — |
| `nodeName` | string | Yes | — |
| `nodeExternalId` | null or string | Yes | — |
| `lastVisitedAt` | string (date-time) | Yes | — |

</details>

<a id="schema-instancesearchrequest"></a>

<details>
<summary>InstanceSearchRequest</summary>

Advanced workflow-instance search request.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `status` | null or string | No | Lifecycle value; see this resource family’s vocabulary. |
| `instanceId` | null or integer (int64) | No | Runtime workflow-instance database ID. |
| `workflowId` | null or integer (int64) | No | Exact immutable workflow-version database ID. |
| `workflowKey` | null or string | No | Stable authored workflow-family key. |
| `businessKey` | null or string | No | — |
| `nodeId` | null or integer (int32) | No | — |
| `nodeExternalId` | null or string | No | — |
| `variableFilter` | null or [JsonElement](#schema-jsonelement) | No | Optional typed latest-instance-value predicate; see Filtering and pagination. |
| `sort` | array of [SearchSortDto](#schema-searchsortdto) or null | No | Up to three field/direction clauses; allowed fields depend on endpoint. |
| `cursor` | null or string | No | Opaque previous nextCursor; omit on first page. |
| `includeVariables` | null or boolean | No | Default false. |
| `page` | null or integer (int32) | No | Default 1. |
| `pageSize` | null or integer (int32) | No | Default 50, maximum 200. |

</details>

<a id="schema-instancesummarydto"></a>

<details>
<summary>InstanceSummaryDto</summary>

Represents a summary of a workflow instance.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | The database ID of the instance. |
| `workflowId` | integer (int64) | Yes | The database ID of the workflow version. |
| `workflowName` | string | Yes | The name of the workflow. |
| `workflowVersion` | integer (int32) | Yes | The version of the workflow definition. |
| `currentNodeId` | integer (int32) | Yes | The ID of the current resting flow node. |
| `currentNodeName` | string | Yes | The name of the current resting flow node. |
| `currentNodeExternalId` | null or string | Yes | The user-defined external ID of the current resting flow node. |
| `status` | string | Yes | The current execution status of the instance. |
| `businessKey` | null or string | Yes | The normalized domain business key, when configured. |
| `businessKeyUniqueness` | null or string | Yes | The snapshotted active/all uniqueness policy. |
| `startedBy` | null or string | Yes | The username of the actor who started the instance. |
| `createdAt` | string (date-time) | Yes | The timestamp when the instance was created. |
| `updatedAt` | string (date-time) | Yes | The timestamp when the instance was last updated. |
| `userTasks` | null or [UserTaskWorkSummaryDto](#schema-usertaskworksummarydto) | Yes | — |
| `variables` | null or object | Yes | Latest instance variable values when explicitly requested; otherwise omitted. |
| `fault` | null or [FaultInfoDto](#schema-faultinfodto) | No | — |
| `executionPositions` | array of [ExecutionPositionDto](#schema-executionpositiondto) | No | All exposed token positions; the representative currentNode fields do not imply a single active token. |
| `completion` | null or [CompletionInfoDto](#schema-completioninfodto) | No | — |
| `jobs` | null or [InstanceJobSummaryDto](#schema-instancejobsummarydto) | No | — |
| `sharedVariables` | array of [SharedVariableBindingMetadataDto](#schema-sharedvariablebindingmetadatadto) or null | No | — |

</details>

<a id="schema-instancevariabledto"></a>

<details>
<summary>InstanceVariableDto</summary>

Represents a variable currently stored in a workflow instance.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | The unique database ID of the variable record. |
| `variableName` | string | Yes | The declared name of the variable. |
| `sourceFlowId` | null or integer (int32) | Yes | Optional. The ID of the sequence flow that was taken when setting this variable. |
| `setBy` | null or string | Yes | The username of the actor or background task that set the variable. |
| `value` | [JsonElement](#schema-jsonelement) | Yes | The JSON value of the variable. |
| `setAt` | string (date-time) | Yes | The timestamp when the variable was set. |
| `actingFor` | null or string | No | — |
| `delegationId` | null or integer (int64) | No | — |
| `instanceVariableUpdateAuditId` | null or integer (int64) | No | — |

</details>

<a id="schema-instancevariableupdateauditdto"></a>

<details>
<summary>InstanceVariableUpdateAuditDto</summary>

Immutable audit for a successful administrative variable update.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | — |
| `instanceId` | integer (int64) | Yes | Runtime workflow-instance database ID. |
| `workflowDefinitionId` | integer (int64) | Yes | Exact immutable workflow-version database ID. |
| `performedBy` | string | Yes | — |
| `performedByRoles` | array of string | Yes | — |
| `reason` | null or string | Yes | Human-readable audit reason; required where non-null in version-change/reactivation requests. |
| `variables` | array of [InstanceVariableUpdateOutcomeDto](#schema-instancevariableupdateoutcomedto) | Yes | Runtime name/value map or write array as typed here; input contracts and raw administrative writes have different rules. |
| `performedAt` | string (date-time) | Yes | — |
| `idempotencyKey` | null or string | Yes | Optional logical-operation retry key. Not the start-node HTTP idempotency header. |
| `batchId` | null or integer (int64) | Yes | — |
| `batchItemId` | null or integer (int64) | Yes | — |

</details>

<a id="schema-instancevariableupdatebatchdetaildto"></a>

<details>
<summary>InstanceVariableUpdateBatchDetailDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `summary` | [InstanceVariableUpdateBatchSummaryDto](#schema-instancevariableupdatebatchsummarydto) | Yes | — |
| `selection` | [JsonElement](#schema-jsonelement) | Yes | — |
| `variables` | array of [InstanceVariableWriteDto](#schema-instancevariablewritedto) | Yes | Runtime name/value map or write array as typed here; input contracts and raw administrative writes have different rules. |
| `preparedByRoles` | array of string | Yes | — |
| `confirmedByRoles` | array of string or null | Yes | — |
| `issues` | array of [InstanceVariableUpdateIssueDto](#schema-instancevariableupdateissuedto) | Yes | — |
| `jobs` | array of [InstanceVariableUpdateBatchJobLinkDto](#schema-instancevariableupdatebatchjoblinkdto) | Yes | — |
| `cancelledBy` | null or string | Yes | — |
| `cancellationReason` | null or string | Yes | — |
| `preparedAt` | null or string (date-time) | Yes | — |
| `confirmedAt` | null or string (date-time) | Yes | — |
| `startedAt` | null or string (date-time) | Yes | — |
| `cancelledAt` | null or string (date-time) | Yes | — |

</details>

<a id="schema-instancevariableupdatebatchitemdto"></a>

<details>
<summary>InstanceVariableUpdateBatchItemDto</summary>

Prepared plan and eventual result for one frozen instance.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | — |
| `batchId` | integer (int64) | Yes | — |
| `instanceId` | integer (int64) | Yes | Runtime workflow-instance database ID. |
| `businessKey` | null or string | Yes | — |
| `capturedWorkflowDefinitionId` | integer (int64) | Yes | — |
| `capturedInstanceUpdatedAt` | string (date-time) | Yes | — |
| `status` | string | Yes | Lifecycle value; see this resource family’s vocabulary. |
| `plan` | array of [InstanceVariableUpdateOutcomePlanDto](#schema-instancevariableupdateoutcomeplandto) | Yes | — |
| `warnings` | array of [InstanceVariableUpdateIssueDto](#schema-instancevariableupdateissuedto) | Yes | — |
| `result` | null or [JsonElement](#schema-jsonelement) | Yes | — |
| `updateOperationId` | null or integer (int64) | Yes | — |
| `errorCode` | null or string | Yes | — |
| `errorDescription` | null or string | Yes | — |
| `createdAt` | string (date-time) | Yes | Server creation timestamp. |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |
| `preparedAt` | null or string (date-time) | Yes | — |
| `startedAt` | null or string (date-time) | Yes | — |
| `completedAt` | null or string (date-time) | Yes | Nullable until completed; ISO 8601 timestamp. |

</details>

<a id="schema-instancevariableupdatebatchjoblinkdto"></a>

<details>
<summary>InstanceVariableUpdateBatchJobLinkDto</summary>

A durable prepare/execute job associated with one workflow version.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | — |
| `originalJobId` | integer (int64) | Yes | — |
| `jobId` | null or integer (int64) | Yes | — |
| `phase` | string | Yes | prepare or execute. |
| `workflow` | [WorkflowSummaryDto](#schema-workflowsummarydto) | Yes | — |
| `jobStatus` | null or string | Yes | — |

</details>

<a id="schema-instancevariableupdatebatchselectiondto"></a>

<details>
<summary>InstanceVariableUpdateBatchSelectionDto</summary>

mode explicit uses instanceIds; mode allMatching uses filter plus optional excludedInstanceIds.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `mode` | string | Yes | explicit or allMatching. |
| `instanceIds` | array of integer (int64) or null | No | — |
| `filter` | null or [InstanceVariableUpdateCandidateFilterDto](#schema-instancevariableupdatecandidatefilterdto) | No | — |
| `excludedInstanceIds` | array of integer (int64) or null | No | — |

</details>

<a id="schema-instancevariableupdatebatchsummarydto"></a>

<details>
<summary>InstanceVariableUpdateBatchSummaryDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | — |
| `workflowKey` | string | Yes | Stable authored workflow-family key. |
| `status` | string | Yes | Lifecycle value; see this resource family’s vocabulary. |
| `preparedBy` | string | Yes | — |
| `confirmedBy` | null or string | Yes | — |
| `reason` | null or string | Yes | Human-readable audit reason; required where non-null in version-change/reactivation requests. |
| `variableCount` | integer (int32) | Yes | — |
| `workflowDefinitionCount` | integer (int32) | Yes | — |
| `totalItemCount` | integer (int32) | Yes | — |
| `eligibleItemCount` | integer (int32) | Yes | — |
| `ineligibleItemCount` | integer (int32) | Yes | — |
| `warningItemCount` | integer (int32) | Yes | — |
| `queuedItemCount` | integer (int32) | Yes | — |
| `succeededItemCount` | integer (int32) | Yes | — |
| `skippedItemCount` | integer (int32) | Yes | — |
| `failedItemCount` | integer (int32) | Yes | — |
| `cancelledItemCount` | integer (int32) | Yes | — |
| `createdAt` | string (date-time) | Yes | Server creation timestamp. |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |
| `completedAt` | null or string (date-time) | Yes | Nullable until completed; ISO 8601 timestamp. |

</details>

<a id="schema-instancevariableupdatecandidatedto"></a>

<details>
<summary>InstanceVariableUpdateCandidateDto</summary>

One running instance available to a variable-update batch.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `instanceId` | integer (int64) | Yes | Runtime workflow-instance database ID. |
| `workflowDefinitionId` | integer (int64) | Yes | Exact immutable workflow-version database ID. |
| `workflowKey` | string | Yes | Stable authored workflow-family key. |
| `workflowName` | string | Yes | — |
| `workflowVersion` | integer (int32) | Yes | — |
| `status` | string | Yes | Lifecycle value; see this resource family’s vocabulary. |
| `businessKey` | null or string | Yes | — |
| `createdAt` | string (date-time) | Yes | Server creation timestamp. |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |
| `executionPositions` | array of [ExecutionPositionDto](#schema-executionpositiondto) | Yes | All exposed token positions; the representative currentNode fields do not imply a single active token. |
| `variables` | null or object | No | Runtime name/value map or write array as typed here; input contracts and raw administrative writes have different rules. |
| `jobs` | null or [InstanceJobSummaryDto](#schema-instancejobsummarydto) | No | — |

</details>

<a id="schema-instancevariableupdatecandidatefilterdto"></a>

<details>
<summary>InstanceVariableUpdateCandidateFilterDto</summary>

Server-authoritative filters for variable-update candidates.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `workflowKey` | string | Yes | Stable authored workflow-family key. |
| `workflowId` | null or integer (int64) | No | Exact immutable workflow-version database ID. |
| `instanceId` | null or integer (int64) | No | Runtime workflow-instance database ID. |
| `businessKey` | null or string | No | — |
| `nodeId` | null or integer (int32) | No | — |
| `nodeExternalId` | null or string | No | — |
| `variableFilter` | null or [JsonElement](#schema-jsonelement) | No | Optional typed latest-instance-value predicate; see Filtering and pagination. |

</details>

<a id="schema-instancevariableupdatecandidatesearchrequest"></a>

<details>
<summary>InstanceVariableUpdateCandidateSearchRequest</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `filter` | [InstanceVariableUpdateCandidateFilterDto](#schema-instancevariableupdatecandidatefilterdto) | Yes | — |
| `sort` | array of [SearchSortDto](#schema-searchsortdto) or null | No | Up to three field/direction clauses; allowed fields depend on endpoint. |
| `cursor` | null or string | No | Opaque previous nextCursor; omit on first page. |
| `includeVariables` | null or boolean | No | Default false. |
| `page` | null or integer (int32) | No | Default 1. |
| `pageSize` | null or integer (int32) | No | Default 50, maximum 200. |

</details>

<a id="schema-instancevariableupdateissuedto"></a>

<details>
<summary>InstanceVariableUpdateIssueDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `code` | string | Yes | — |
| `message` | string | Yes | — |

</details>

<a id="schema-instancevariableupdateoutcomedto"></a>

<details>
<summary>InstanceVariableUpdateOutcomeDto</summary>

The actual result of one append-only variable write.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `name` | string | Yes | — |
| `outcome` | string | Yes | added or updated. |
| `variableId` | integer (int64) | Yes | — |
| `value` | [JsonElement](#schema-jsonelement) | Yes | — |

</details>

<a id="schema-instancevariableupdateoutcomeplandto"></a>

<details>
<summary>InstanceVariableUpdateOutcomePlanDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `name` | string | Yes | — |
| `outcome` | string | Yes | added or updated. |

</details>

<a id="schema-instancevariablewritedto"></a>

<details>
<summary>InstanceVariableWriteDto</summary>

One raw JSON variable value to append to an instance.

name identifies a raw instance variable; value is any JSON including null. This shape is different from a TakeFlowRequest variables object.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `name` | string | Yes | — |
| `value` | [JsonElement](#schema-jsonelement) | Yes | — |

</details>

<a id="schema-instanceversionchangeauditdto"></a>

<details>
<summary>InstanceVersionChangeAuditDto</summary>

An immutable audit entry recording a completed workflow version change.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | — |
| `instanceId` | integer (int64) | Yes | Runtime workflow-instance database ID. |
| `sourceWorkflow` | [WorkflowSummaryDto](#schema-workflowsummarydto) | Yes | — |
| `targetWorkflow` | [WorkflowSummaryDto](#schema-workflowsummarydto) | Yes | — |
| `direction` | string | Yes | Sort asc/desc, delegation incoming/outgoing, or version upgrade/downgrade according to schema. |
| `changedBy` | null or string | Yes | — |
| `changedByRoles` | array of string | Yes | — |
| `reason` | string | Yes | Required audit reason for the completed version change. |
| `changedAt` | string (date-time) | Yes | — |
| `batchId` | null or integer (int64) | No | — |
| `batchItemId` | null or integer (int64) | No | — |

</details>

<a id="schema-instanceversionchangebatchdetaildto"></a>

<details>
<summary>InstanceVersionChangeBatchDetailDto</summary>

Full frozen request, actor snapshots, and lifecycle metadata.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `summary` | [InstanceVersionChangeBatchSummaryDto](#schema-instanceversionchangebatchsummarydto) | Yes | — |
| `selection` | [JsonElement](#schema-jsonelement) | Yes | — |
| `preparedByRoles` | array of string | Yes | — |
| `confirmedByRoles` | array of string or null | Yes | — |
| `issues` | null or [JsonElement](#schema-jsonelement) | Yes | — |
| `preparationJobId` | null or integer (int64) | Yes | — |
| `executionJobId` | null or integer (int64) | Yes | — |
| `cancelledBy` | null or string | Yes | — |
| `cancellationReason` | null or string | Yes | — |
| `preparedAt` | null or string (date-time) | Yes | — |
| `confirmedAt` | null or string (date-time) | Yes | — |
| `startedAt` | null or string (date-time) | Yes | — |
| `cancelledAt` | null or string (date-time) | Yes | — |

</details>

<a id="schema-instanceversionchangebatchitemdto"></a>

<details>
<summary>InstanceVersionChangeBatchItemDto</summary>

Prepared compatibility and execution result for one instance.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | — |
| `batchId` | integer (int64) | Yes | — |
| `instanceId` | integer (int64) | Yes | Runtime workflow-instance database ID. |
| `businessKey` | null or string | Yes | — |
| `capturedSourceWorkflowId` | integer (int64) | Yes | — |
| `capturedInstanceUpdatedAt` | string (date-time) | Yes | — |
| `status` | string | Yes | Lifecycle value; see this resource family’s vocabulary. |
| `blockers` | array of [InstanceVersionChangeIssueDto](#schema-instanceversionchangeissuedto) | Yes | — |
| `warnings` | array of [InstanceVersionChangeIssueDto](#schema-instanceversionchangeissuedto) | Yes | — |
| `result` | null or [JsonElement](#schema-jsonelement) | Yes | — |
| `versionChangeAuditId` | null or integer (int64) | Yes | — |
| `errorCode` | null or string | Yes | — |
| `errorDescription` | null or string | Yes | — |
| `createdAt` | string (date-time) | Yes | Server creation timestamp. |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |
| `preparedAt` | null or string (date-time) | Yes | — |
| `startedAt` | null or string (date-time) | Yes | — |
| `completedAt` | null or string (date-time) | Yes | Nullable until completed; ISO 8601 timestamp. |

</details>

<a id="schema-instanceversionchangebatchselectiondto"></a>

<details>
<summary>InstanceVersionChangeBatchSelectionDto</summary>

A frozen candidate population expressed as explicit instance ids or as a server-side filter snapshot plus exclusions.

mode explicit uses instanceIds; mode allMatching uses filter plus optional excludedInstanceIds.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `mode` | string | Yes | explicit or allMatching. |
| `instanceIds` | array of integer (int64) or null | No | — |
| `filter` | null or [InstanceVersionChangeCandidateFilterDto](#schema-instanceversionchangecandidatefilterdto) | No | — |
| `excludedInstanceIds` | array of integer (int64) or null | No | — |

</details>

<a id="schema-instanceversionchangebatchsummarydto"></a>

<details>
<summary>InstanceVersionChangeBatchSummaryDto</summary>

Aggregate lifecycle and progress for one durable batch.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | — |
| `sourceWorkflow` | [WorkflowSummaryDto](#schema-workflowsummarydto) | Yes | — |
| `targetWorkflow` | [WorkflowSummaryDto](#schema-workflowsummarydto) | Yes | — |
| `direction` | string | Yes | Sort asc/desc, delegation incoming/outgoing, or version upgrade/downgrade according to schema. |
| `reason` | string | Yes | Human-readable audit reason; required where non-null in version-change/reactivation requests. |
| `status` | string | Yes | Lifecycle value; see this resource family’s vocabulary. |
| `preparedBy` | string | Yes | — |
| `confirmedBy` | null or string | Yes | — |
| `totalItemCount` | integer (int32) | Yes | — |
| `eligibleItemCount` | integer (int32) | Yes | — |
| `warningItemCount` | integer (int32) | Yes | — |
| `staleItemCount` | integer (int32) | Yes | — |
| `blockedItemCount` | integer (int32) | Yes | — |
| `ineligibleItemCount` | integer (int32) | Yes | — |
| `queuedItemCount` | integer (int32) | Yes | — |
| `succeededItemCount` | integer (int32) | Yes | — |
| `skippedItemCount` | integer (int32) | Yes | — |
| `failedItemCount` | integer (int32) | Yes | — |
| `cancelledItemCount` | integer (int32) | Yes | — |
| `createdAt` | string (date-time) | Yes | Server creation timestamp. |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |
| `completedAt` | null or string (date-time) | Yes | Nullable until completed; ISO 8601 timestamp. |

</details>

<a id="schema-instanceversionchangecandidatedto"></a>

<details>
<summary>InstanceVersionChangeCandidateDto</summary>

One running workflow instance eligible for batch selection.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `instanceId` | integer (int64) | Yes | Runtime workflow-instance database ID. |
| `workflowDefinitionId` | integer (int64) | Yes | Exact immutable workflow-version database ID. |
| `workflowKey` | string | Yes | Stable authored workflow-family key. |
| `workflowName` | string | Yes | — |
| `workflowVersion` | integer (int32) | Yes | — |
| `status` | string | Yes | Lifecycle value; see this resource family’s vocabulary. |
| `businessKey` | null or string | Yes | — |
| `createdAt` | string (date-time) | Yes | Server creation timestamp. |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |
| `executionPositions` | array of [ExecutionPositionDto](#schema-executionpositiondto) | Yes | All exposed token positions; the representative currentNode fields do not imply a single active token. |
| `variables` | null or object | No | Runtime name/value map or write array as typed here; input contracts and raw administrative writes have different rules. |

</details>

<a id="schema-instanceversionchangecandidatefilterdto"></a>

<details>
<summary>InstanceVersionChangeCandidateFilterDto</summary>

Server-authoritative filters used to find running instances on one exact workflow definition for a version-change batch.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `sourceWorkflowId` | integer (int64) | Yes | Exact source workflow-version database ID. |
| `instanceId` | null or integer (int64) | No | Runtime workflow-instance database ID. |
| `businessKey` | null or string | No | — |
| `nodeId` | null or integer (int32) | No | — |
| `nodeExternalId` | null or string | No | — |
| `variableFilter` | null or [JsonElement](#schema-jsonelement) | No | Optional typed latest-instance-value predicate; see Filtering and pagination. |

</details>

<a id="schema-instanceversionchangecandidatesearchrequest"></a>

<details>
<summary>InstanceVersionChangeCandidateSearchRequest</summary>

Paged search request for version-change batch candidates.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `filter` | [InstanceVersionChangeCandidateFilterDto](#schema-instanceversionchangecandidatefilterdto) | Yes | — |
| `includeVariables` | null or boolean | No | Default false. |
| `page` | null or integer (int32) | No | Default 1. |
| `pageSize` | null or integer (int32) | No | Default 50, maximum 200. |

</details>

<a id="schema-instanceversionchangeissuedto"></a>

<details>
<summary>InstanceVersionChangeIssueDto</summary>

A structured compatibility finding produced by instance version-change validation.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `code` | string | Yes | — |
| `message` | string | Yes | — |
| `stateType` | null or string | No | — |
| `stateId` | null or integer (int64) | No | — |
| `nodeId` | null or integer (int32) | No | — |
| `flowId` | null or integer (int32) | No | — |
| `variableName` | null or string | No | — |

</details>

<a id="schema-instanceversionchangepreviewdto"></a>

<details>
<summary>InstanceVersionChangePreviewDto</summary>

The result of a non-mutating compatibility check for a workflow version change.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `instanceId` | integer (int64) | Yes | Runtime workflow-instance database ID. |
| `sourceWorkflow` | [WorkflowSummaryDto](#schema-workflowsummarydto) | Yes | — |
| `targetWorkflow` | [WorkflowSummaryDto](#schema-workflowsummarydto) | Yes | — |
| `direction` | string | Yes | upgrade or downgrade, based on target version number. |
| `compatible` | boolean | Yes | — |
| `blockers` | array of [InstanceVersionChangeIssueDto](#schema-instanceversionchangeissuedto) | Yes | — |
| `warnings` | array of [InstanceVersionChangeIssueDto](#schema-instanceversionchangeissuedto) | Yes | — |
| `expectedSourceWorkflowId` | integer (int64) | Yes | — |
| `expectedUpdatedAt` | string (date-time) | Yes | Exact server-returned timestamp for optimistic concurrency. |

</details>

<a id="schema-jobattemptdto"></a>

<details>
<summary>JobAttemptDto</summary>

One separately paged durable-job attempt.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | — |
| `jobId` | integer (int64) | Yes | — |
| `attemptNumber` | integer (int32) | Yes | — |
| `status` | string | Yes | running, resultReady, completed, failed, leaseLost, or cancelled. |
| `workerId` | null or string | Yes | — |
| `leaseGeneration` | integer (int64) | Yes | — |
| `startedAt` | string (date-time) | Yes | — |
| `finishedAt` | null or string (date-time) | Yes | — |
| `failureCode` | null or string | Yes | — |
| `failureDescription` | null or string | Yes | — |

</details>

<a id="schema-jobdetaildto"></a>

<details>
<summary>JobDetailDto</summary>

Administrative detail for one job without exposing its immutable execution snapshot or persisted result payload.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `summary` | [JobSummaryDto](#schema-jobsummarydto) | Yes | — |
| `activationId` | null or string (uuid) | Yes | — |
| `workerId` | null or string | Yes | — |
| `leaseGeneration` | integer (int64) | Yes | — |
| `startedAt` | null or string (date-time) | Yes | — |
| `resultReadyAt` | null or string (date-time) | Yes | — |
| `lastFailureCode` | null or string | Yes | — |
| `lastFailureDescription` | null or string | Yes | — |

</details>

<a id="schema-jobpolicymodel"></a>

<details>
<summary>JobPolicyModel</summary>

Per-node durable job retry and failure-order policy.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `failureHandling` | string | No | boundaryFirst (default) or retryFirst. Default: `"boundaryFirst"`. |
| `retryDelays` | array of string or null | No | Optional list of up to 10 fixed ISO 8601 durations; policy applies only to durable execution. |

</details>

<a id="schema-jobqueuestatisticsdto"></a>

<details>
<summary>JobQueueStatisticsDto</summary>

Constant-size administrative snapshot of durable queue health. QueueLagSeconds is measured from the oldest currently runnable job.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `runnableDepth` | integer (int64) | Yes | — |
| `oldestRunnableDueAt` | null or string (date-time) | Yes | — |
| `queueLagSeconds` | number (double) | Yes | — |
| `timerControlRunnableCount` | integer (int64) | Yes | — |
| `activeLeaseCount` | integer (int64) | Yes | — |
| `openIncidentCount` | integer (int64) | Yes | — |
| `observedAt` | string (date-time) | Yes | — |

</details>

<a id="schema-jobsummarydto"></a>

<details>
<summary>JobSummaryDto</summary>

Bounded operations projection for a durable workflow job. Snapshots, result payloads, stack traces, and attempt collections are intentionally excluded.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | — |
| `instanceId` | null or integer (int64) | Yes | Runtime workflow-instance database ID. |
| `workflowDefinitionId` | integer (int64) | Yes | Exact immutable workflow-version database ID. |
| `workflowKey` | string | Yes | Stable authored workflow-family key. |
| `tokenId` | null or integer (int64) | Yes | Runtime execution-token database ID. |
| `nodeId` | integer (int32) | Yes | — |
| `nodeName` | string | Yes | — |
| `nodeType` | string | Yes | — |
| `kind` | string | Yes | — |
| `phase` | string | Yes | Persisted job phase: asyncBefore, asyncAfter, timer, conditionalWake, or the batch prepare/execute phase as appropriate to kind. |
| `queueClass` | string | Yes | control or activity. |
| `status` | string | Yes | Lifecycle value; see this resource family’s vocabulary. |
| `attemptCount` | integer (int32) | Yes | — |
| `dueAt` | string (date-time) | Yes | — |
| `leaseExpiresAt` | null or string (date-time) | Yes | — |
| `incidentId` | null or integer (int64) | Yes | — |
| `createdAt` | string (date-time) | Yes | Server creation timestamp. |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |
| `completedAt` | null or string (date-time) | Yes | Nullable until completed; ISO 8601 timestamp. |

</details>

<a id="schema-joincancellationmodel"></a>

<details>
<summary>JoinCancellationModel</summary>

Flowbit merge-gateway extension that bounds cancellation to a referenced Parallel, Inclusive, or Complex split activation.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `gatewayRef` | null or integer (int32) | No | The split gateway whose nearest active activation supplies the cancellation scope. Authored definitions require a positive value. |

</details>

<a id="schema-jsonelement"></a>

<details>
<summary>JsonElement</summary>

An arbitrary JSON value (object, array, string, number, Boolean or null), constrained by the surrounding authored or API contract.

Any JSON value.

</details>

<a id="schema-lanemodel"></a>

<details>
<summary>LaneModel</summary>

Represents a swimlane container in a workflow layout.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int32) | No | The unique integer ID of the lane. |
| `name` | string | No | The display name of the lane. |
| `externalId` | null or string | No | An optional external identifier mapping the lane to business structures. |
| `x` | integer (int32) | No | The X coordinate of the lane on the designer canvas. |
| `y` | integer (int32) | No | The Y coordinate of the lane on the designer canvas. |
| `w` | integer (int32) | No | The width of the lane. |
| `h` | integer (int32) | No | The height of the lane. |

</details>

<a id="schema-legacyactionmodel"></a>

<details>
<summary>LegacyActionModel</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int32) | No | — |
| `name` | string | No | — |
| `attributes` | array of [WorkflowAttributeModel](#schema-workflowattributemodel) | No | Authored metadata key/value entries; no workflow authorization semantics are implied. |
| `toStepId` | integer (int32) | No | — |
| `roles` | array of string | No | Role strings; this schema determines whether these represent actor claims or a configured permission list. |
| `variables` | array of [VariableModel](#schema-variablemodel) | No | Runtime name/value map or write array as typed here; input contracts and raw administrative writes have different rules. |

</details>

<a id="schema-legacystepmodel"></a>

<details>
<summary>LegacyStepModel</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int32) | No | — |
| `name` | string | No | — |
| `attributes` | array of [WorkflowAttributeModel](#schema-workflowattributemodel) | No | Authored metadata key/value entries; no workflow authorization semantics are implied. |
| `type` | string | No | — |
| `phaseId` | null or integer (int32) | No | — |
| `x` | integer (int32) | No | — |
| `y` | integer (int32) | No | — |
| `roles` | array of string | No | Role strings; this schema determines whether these represent actor claims or a configured permission list. |
| `requiresClaim` | boolean | No | — |
| `autoAdvance` | boolean | No | — |
| `nextStepId` | null or integer (int32) | No | — |
| `variables` | array of [VariableModel](#schema-variablemodel) | No | Runtime name/value map or write array as typed here; input contracts and raw administrative writes have different rules. |
| `actions` | array of [LegacyActionModel](#schema-legacyactionmodel) | No | — |

</details>

<a id="schema-manageableusertasksearchrequest"></a>

<details>
<summary>ManageableUserTaskSearchRequest</summary>

Advanced manager-scoped user-task search request.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `status` | null or string | No | active (default), pending, or open (active and pending). |
| `taskId` | null or integer (int64) | No | — |
| `instanceId` | null or integer (int64) | No | Runtime workflow-instance database ID. |
| `workflowId` | null or integer (int64) | No | Exact immutable workflow-version database ID. |
| `workflowKey` | null or string | No | Stable authored workflow-family key. |
| `businessKey` | null or string | No | — |
| `nodeId` | null or integer (int32) | No | — |
| `nodeExternalId` | null or string | No | — |
| `owner` | null or string | No | — |
| `ownership` | null or string | No | — |
| `variableFilter` | null or [JsonElement](#schema-jsonelement) | No | Optional typed latest-instance-value predicate; see Filtering and pagination. |
| `page` | null or integer (int32) | No | Default 1. |
| `pageSize` | null or integer (int32) | No | Default 50, maximum 200. |

</details>

<a id="schema-managedusertaskdto"></a>

<details>
<summary>ManagedUserTaskDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `userTaskId` | integer (int64) | Yes | Exact runtime child/normal work-item database ID. |
| `instanceId` | integer (int64) | Yes | Runtime workflow-instance database ID. |
| `tokenId` | integer (int64) | Yes | Runtime execution-token database ID. |
| `workflowId` | integer (int64) | Yes | Exact immutable workflow-version database ID. |
| `workflowKey` | string | Yes | Stable authored workflow-family key. |
| `workflowName` | string | Yes | — |
| `workflowVersion` | integer (int32) | Yes | — |
| `businessKey` | null or string | Yes | — |
| `nodeId` | integer (int32) | Yes | — |
| `nodeName` | string | Yes | — |
| `nodeExternalId` | null or string | Yes | — |
| `nodeRoles` | array of string | Yes | — |
| `requiresClaim` | boolean | Yes | — |
| `requiresAssignment` | boolean | Yes | — |
| `ownership` | string | Yes | — |
| `owner` | null or string | Yes | — |
| `multiInstanceExecutionId` | null or integer (int64) | Yes | — |
| `itemIndex` | null or integer (int32) | Yes | — |
| `itemValue` | null or [JsonElement](#schema-jsonelement) | Yes | — |
| `multiInstance` | null or [MultiInstanceProgressDto](#schema-multiinstanceprogressdto) | Yes | — |
| `createdAt` | string (date-time) | Yes | Server creation timestamp. |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |
| `variables` | null or object | Yes | Runtime name/value map or write array as typed here; input contracts and raw administrative writes have different rules. |
| `status` | string | No | Lifecycle value; see this resource family’s vocabulary. |
| `canManageAssignment` | boolean | No | — |
| `canManageRoles` | boolean | No | — |
| `attributes` | array of [WorkflowAttributeModel](#schema-workflowattributemodel) | No | Authored metadata key/value entries; no workflow authorization semantics are implied. |

</details>

<a id="schema-messagecatchmodel"></a>

<details>
<summary>MessageCatchModel</summary>

Delivery configuration for catching external messages or webhooks.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `clientId` | string | No | Expected node-specific client ID or supported trusted configuration reference; this endpoint does not implement OAuth token exchange. |
| `clientSecret` | string | No | Expected node-specific client secret or supported trusted configuration reference. |
| `headerName` | string | No | The required custom header name for correlation. |
| `headerValue` | string | No | The required header value for validation. |
| `headerValidation` | null or string | No | An optional NCalc validation rule for additional header value checks. |
| `outputMappings` | array of [MessageOutputMappingModel](#schema-messageoutputmappingmodel) | No | Dotted-path maps to extract fields from the incoming JSON message body. |
| `deliveryIdempotency` | boolean | No | Default false. Enable instance-scoped idempotency for intermediate message catches using deliveryIdempotencyHeaderName. Message starts use node-level idempotency instead. Default: `false`. |
| `deliveryIdempotencyHeaderName` | null or string | No | Default Idempotency-Key when delivery idempotency is enabled. This transport value is not an instance variable. |
| `idempotencyVariable` | null or string | No | Legacy message-start compatibility property; author new start idempotency through the node-level idempotency object. |

</details>

<a id="schema-messagedeliveryackdto"></a>

<details>
<summary>MessageDeliveryAckDto</summary>

Slim acknowledgment returned after successfully delivering a message to an intermediate message catch event.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | The database ID of the workflow instance. |
| `currentNodeId` | integer (int32) | Yes | The ID of the current resting flow node after the delivery. |
| `currentNodeName` | string | Yes | The name of the current resting flow node. |
| `currentNodeExternalId` | null or string | Yes | The user-defined external ID of the current resting flow node. |
| `status` | string | Yes | The execution status of the instance after the delivery. |
| `updatedAt` | string (date-time) | Yes | The timestamp when the instance was updated. |
| `fault` | null or [FaultInfoDto](#schema-faultinfodto) | No | — |
| `executionPositions` | array of [ExecutionPositionDto](#schema-executionpositiondto) | No | All exposed token positions; the representative currentNode fields do not imply a single active token. |
| `completion` | null or [CompletionInfoDto](#schema-completioninfodto) | No | — |

</details>

<a id="schema-messagedeliveryconflictdto"></a>

<details>
<summary>MessageDeliveryConflictDto</summary>

Conflict returned when a message delivery key has already been committed or another request consumed the catch activation while this request was waiting.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `code` | string | Yes | — |
| `instanceId` | integer (int64) | Yes | Runtime workflow-instance database ID. |
| `sourceNodeId` | integer (int32) | Yes | — |

</details>

<a id="schema-messageoutputmappingmodel"></a>

<details>
<summary>MessageOutputMappingModel</summary>

Maps a typed value from an inbound message. On a message start the mapping is also the complete declaration of the start variable. On an intermediate message catch it is a typed, operation-specific write contract.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `variable` | string | No | — |
| `path` | string | No | — |
| `required` | boolean | No | — |
| `dataType` | null or string | No | Expected scalar element type. Null identifies a legacy raw mapping until the definition migrator canonicalizes it. |
| `isArray` | null or boolean | No | Whether the mapped value must be an array of string? MessageOutputMappingModel.DataType. |
| `defaultValue` | null or [JsonElement](#schema-jsonelement) | No | — |
| `validation` | null or string | No | — |

</details>

<a id="schema-messagestartackdto"></a>

<details>
<summary>MessageStartAckDto</summary>

Slim acknowledgment returned when starting a workflow instance via a message start event.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `instanceId` | integer (int64) | Yes | The database ID of the created workflow instance. |
| `currentNodeId` | integer (int32) | Yes | The ID of the current resting flow node after starting. |
| `currentNodeName` | string | Yes | The name of the current resting flow node. |
| `currentNodeExternalId` | null or string | Yes | The user-defined external ID of the current resting flow node. |
| `status` | string | Yes | The execution status of the instance after starting. |
| `createdAt` | string (date-time) | Yes | The timestamp when the instance was started. |
| `fault` | null or [FaultInfoDto](#schema-faultinfodto) | No | — |
| `executionPositions` | array of [ExecutionPositionDto](#schema-executionpositiondto) | No | All exposed token positions; the representative currentNode fields do not imply a single active token. |
| `completion` | null or [CompletionInfoDto](#schema-completioninfodto) | No | — |

</details>

<a id="schema-multiinstanceflowcountdto"></a>

<details>
<summary>MultiInstanceFlowCountDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `flowId` | integer (int32) | Yes | — |
| `count` | integer (int32) | Yes | — |
| `percent` | number (double) | Yes | — |

</details>

<a id="schema-multiinstancemodel"></a>

<details>
<summary>MultiInstanceModel</summary>

Configures parallel or sequential repetitions of a user task.

mode: parallel or sequential; source: collection or cardinality; completionEvaluation: afterEach (missing default) or afterAll. Recognized casing canonicalizes; null/unknown enum values are invalid. onePerActor applies to cardinality only.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `mode` | string | No | parallel (default) or sequential; null/unknown values rejected. Default: `"parallel"`. |
| `source` | string | No | collection (default) or cardinality; null/unknown values rejected. Default: `"collection"`. |
| `collectionVariable` | null or string | No | Required declared string-array variable for collection source; it snapshots usernames on entry. |
| `cardinalityExpression` | null or string | No | Required bounded integer-count expression for cardinality source. |
| `onePerActor` | boolean | No | When true for a cardinality source, each authenticated actor may complete at most one item in this multi-instance execution. Default: `false`. |
| `completionEvaluation` | string | No | afterEach (default) or afterAll; null/unknown values rejected. Default: `"afterEach"`. |
| `resultVariable` | string | No | Required destination for ordered child/parent-interrupt JSON results. |

</details>

<a id="schema-multiinstanceprogressdto"></a>

<details>
<summary>MultiInstanceProgressDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `executionId` | integer (int64) | Yes | — |
| `mode` | string | Yes | parallel or sequential. |
| `status` | string | Yes | active, completed, interrupted, or cancelled. |
| `total` | integer (int32) | Yes | — |
| `completed` | integer (int32) | Yes | — |
| `active` | integer (int32) | Yes | — |
| `pending` | integer (int32) | Yes | — |
| `cancelled` | integer (int32) | Yes | — |
| `winningFlowId` | null or integer (int32) | Yes | — |
| `completionReason` | null or string | Yes | — |
| `flowCounts` | array of [MultiInstanceFlowCountDto](#schema-multiinstanceflowcountdto) | Yes | — |

</details>

<a id="schema-nodeexecutiondetaildto"></a>

<details>
<summary>NodeExecutionDetailDto</summary>

Authorized detail for one node execution. VariableChanges contains only writes explicitly attributed to this execution; it is not an instance snapshot and does not include unrelated historical or current values.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `nodeRoles` | array of string or null | No | Immutable node-role snapshot. Null means the snapshot was unavailable at migration cutover; an empty list means the node was known to have no roles. |
| `startedByRoles` | array of string or null | No | — |
| `completedByRoles` | array of string or null | No | — |
| `startedByClaims` | null or object of string arrays | No | Selected claims of the causal starting actor, captured independently of completion. Null/absent means not recorded; `{}` means no selected claims were present. |
| `completedByClaims` | null or object of string arrays | No | Selected claims of the completing actor. Null/absent means not recorded; `{}` means no selected claims were present. |
| `requiresClaim` | null or boolean | No | — |
| `requiresAssignment` | null or boolean | No | — |
| `assignedTo` | null or string | No | — |
| `claimedBy` | null or string | No | — |
| `itemValue` | null or [JsonElement](#schema-jsonelement) | No | — |
| `submittedResult` | null or object | No | — |
| `multiInstance` | null or [NodeExecutionMultiInstanceDto](#schema-nodeexecutionmultiinstancedto) | No | — |
| `error` | null or [NodeExecutionErrorDto](#schema-nodeexecutionerrordto) | No | — |
| `variableChanges` | array of [NodeExecutionVariableChangeDto](#schema-nodeexecutionvariablechangedto) | Yes | — |
| `sharedVariableChanges` | array of [NodeExecutionSharedVariableChangeDto](#schema-nodeexecutionsharedvariablechangedto) | No | — |
| `id` | integer (int64) | Yes | — |
| `instanceId` | integer (int64) | Yes | Runtime workflow-instance database ID. |
| `workflowId` | integer (int64) | Yes | Exact immutable workflow-version database ID. |
| `workflowKey` | string | Yes | Stable authored workflow-family key. |
| `workflowName` | string | Yes | — |
| `workflowVersion` | integer (int32) | Yes | — |
| `businessKey` | null or string | No | — |
| `tokenId` | integer (int64) | Yes | Runtime execution-token database ID. |
| `userTaskId` | null or integer (int64) | No | Exact runtime child/normal work-item database ID. |
| `multiInstanceExecutionId` | null or integer (int64) | No | — |
| `itemIndex` | null or integer (int32) | No | — |
| `entryGatewayBranchId` | null or integer (int64) | No | — |
| `exitGatewayBranchId` | null or integer (int64) | No | — |
| `executionKind` | string | Yes | — |
| `nodeId` | integer (int32) | Yes | — |
| `nodeName` | string | Yes | — |
| `nodeExternalId` | null or string | No | — |
| `nodeType` | string | Yes | — |
| `status` | string | Yes | Lifecycle value; see this resource family’s vocabulary. |
| `instanceStatus` | string | Yes | — |
| `completionReason` | null or string | No | — |
| `isMultiInstance` | boolean | Yes | — |
| `owner` | null or string | No | Effective user-task owner. An explicit assignment takes precedence over a claim; non-user-task executions have no owner. |
| `enteredViaFlowId` | null or integer (int32) | No | — |
| `selectedFlowId` | null or integer (int32) | No | — |
| `exitedViaFlowId` | null or integer (int32) | No | — |
| `aggregateFlowId` | null or integer (int32) | No | The winning aggregate flow of the owning multi-instance execution. This is distinct from a child item's selected flow. |
| `startedBy` | null or string | No | The causal actor that opened or activated this visit. |
| `completedBy` | null or string | No | — |
| `startedDelegatedAccess` | null or [DelegatedTaskAccessDto](#schema-delegatedtaskaccessdto) | No | — |
| `completedDelegatedAccess` | null or [DelegatedTaskAccessDto](#schema-delegatedtaskaccessdto) | No | — |
| `createdAt` | string (date-time) | Yes | Server creation timestamp. |
| `startedAt` | null or string (date-time) | No | — |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |
| `completedAt` | null or string (date-time) | No | Nullable until completed; ISO 8601 timestamp. |
| `durationMilliseconds` | null or integer (int64) | No | Elapsed milliseconds from StartedAt to CompletedAt, or to the search query's captured time for a currently active execution. Pending rows have no duration. |
| `isCutoverSeeded` | boolean | Yes | — |

</details>

<a id="schema-nodeexecutionerrordto"></a>

<details>
<summary>NodeExecutionErrorDto</summary>

A committed failure associated with a node execution.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `code` | null or string | Yes | — |
| `description` | null or string | Yes | — |

</details>

<a id="schema-nodeexecutionmultiinstancedto"></a>

<details>
<summary>NodeExecutionMultiInstanceDto</summary>

Immutable and current multi-instance context associated with one child node execution. The child result remains on the detail DTO; AggregateFlowId is the execution-wide outcome, when one has been selected.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | — |
| `mode` | string | Yes | parallel or sequential. |
| `source` | string | Yes | collection or cardinality. |
| `onePerActor` | boolean | Yes | — |
| `resultVariable` | string | Yes | — |
| `status` | string | Yes | active, completed, interrupted, or cancelled. |
| `totalCount` | integer (int32) | Yes | — |
| `completedCount` | integer (int32) | Yes | — |
| `cancelledCount` | integer (int32) | Yes | — |
| `aggregateFlowId` | null or integer (int32) | Yes | — |
| `completionReason` | null or string | Yes | — |
| `createdAt` | string (date-time) | Yes | Server creation timestamp. |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |
| `completedAt` | null or string (date-time) | Yes | Nullable until completed; ISO 8601 timestamp. |

</details>

<a id="schema-nodeexecutionsearchbodyrequest"></a>

<details>
<summary>NodeExecutionSearchBodyRequest</summary>

Advanced cross-workflow node-execution search request.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `executionId` | null or integer (int64) | No | — |
| `instanceId` | null or integer (int64) | No | Runtime workflow-instance database ID. |
| `workflowId` | null or integer (int64) | No | Exact immutable workflow-version database ID. |
| `workflowKey` | null or string | No | Stable authored workflow-family key. |
| `workflowVersion` | null or integer (int32) | No | — |
| `businessKey` | null or string | No | — |
| `tokenId` | null or integer (int64) | No | Runtime execution-token database ID. |
| `userTaskId` | null or integer (int64) | No | Exact runtime child/normal work-item database ID. |
| `multiInstanceExecutionId` | null or integer (int64) | No | — |
| `gatewayBranchId` | null or integer (int64) | No | — |
| `itemIndex` | null or integer (int32) | No | — |
| `executionKind` | null or string | No | — |
| `nodeId` | null or integer (int32) | No | — |
| `nodeName` | null or string | No | — |
| `nodeExternalId` | null or string | No | — |
| `nodeTypes` | array of string or null | No | — |
| `statuses` | array of string or null | No | — |
| `instanceStatuses` | array of string or null | No | — |
| `completionReasons` | array of string or null | No | — |
| `isMultiInstance` | null or boolean | No | — |
| `isCutoverSeeded` | null or boolean | No | — |
| `owner` | null or string | No | — |
| `startedBy` | null or string | No | — |
| `completedBy` | null or string | No | — |
| `enteredViaFlowId` | null or integer (int32) | No | — |
| `selectedFlowId` | null or integer (int32) | No | — |
| `exitedViaFlowId` | null or integer (int32) | No | — |
| `aggregateFlowId` | null or integer (int32) | No | — |
| `createdFrom` | null or string (date-time) | No | — |
| `createdTo` | null or string (date-time) | No | — |
| `startedFrom` | null or string (date-time) | No | — |
| `startedTo` | null or string (date-time) | No | — |
| `updatedFrom` | null or string (date-time) | No | — |
| `updatedTo` | null or string (date-time) | No | — |
| `completedFrom` | null or string (date-time) | No | — |
| `completedTo` | null or string (date-time) | No | — |
| `minDurationMilliseconds` | null or integer (int64) | No | — |
| `maxDurationMilliseconds` | null or integer (int64) | No | — |
| `variableFilter` | null or [JsonElement](#schema-jsonelement) | No | Optional typed latest-instance-value predicate; see Filtering and pagination. |
| `sort` | array of [SearchSortDto](#schema-searchsortdto) or null | No | Up to three field/direction clauses; allowed fields depend on endpoint. |
| `page` | null or integer (int32) | No | Default 1. |
| `pageSize` | null or integer (int32) | No | Default 50, maximum 200. |

</details>

<a id="schema-nodeexecutionsharedvariablechangedto"></a>

<details>
<summary>NodeExecutionSharedVariableChangeDto</summary>

One deployment-wide shared-variable revision attributed to this execution. The key/revision pair is the durable correlation; shared history is kept separately from instance variable history.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `revisionId` | integer (int64) | Yes | — |
| `key` | string | Yes | — |
| `revision` | integer (int64) | Yes | — |
| `operation` | string | Yes | — |
| `valueChanged` | boolean | Yes | — |
| `hasValue` | boolean | Yes | False means unset; true with null is an explicit null value. |
| `sourceActionId` | null or integer (int32) | Yes | — |
| `callerId` | string | Yes | — |
| `createdAt` | string (date-time) | Yes | Server creation timestamp. |

</details>

<a id="schema-nodeexecutionsummarydto"></a>

<details>
<summary>NodeExecutionSummaryDto</summary>

One durable visit to a workflow node. Normal visits are correlated to an execution token; multi-instance user tasks instead expose one row per child work item and deliberately do not expose a duplicate parent visit.

completionReason vocabulary: normal, userAction, messageDelivery, multiInstanceItem, multiInstanceCompleted, multiInstanceInterrupt, boundaryCaught, normalEnd, terminateEnd, errorEnd, instanceCancelled, gatewayScopeCancelled, gatewayJoinMerged, parallelFork, parallelJoin, inclusiveSplit, inclusiveMerge, complexActivation, complexReset, scopedInterrupt, scopedInterruptSkipped.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | — |
| `instanceId` | integer (int64) | Yes | Runtime workflow-instance database ID. |
| `workflowId` | integer (int64) | Yes | Exact immutable workflow-version database ID. |
| `workflowKey` | string | Yes | Stable authored workflow-family key. |
| `workflowName` | string | Yes | — |
| `workflowVersion` | integer (int32) | Yes | — |
| `businessKey` | null or string | No | — |
| `tokenId` | integer (int64) | Yes | Runtime execution-token database ID. |
| `userTaskId` | null or integer (int64) | No | Exact runtime child/normal work-item database ID. |
| `multiInstanceExecutionId` | null or integer (int64) | No | — |
| `itemIndex` | null or integer (int32) | No | — |
| `entryGatewayBranchId` | null or integer (int64) | No | — |
| `exitGatewayBranchId` | null or integer (int64) | No | — |
| `executionKind` | string | Yes | — |
| `nodeId` | integer (int32) | Yes | — |
| `nodeName` | string | Yes | — |
| `nodeExternalId` | null or string | No | — |
| `nodeType` | string | Yes | — |
| `status` | string | Yes | Lifecycle value; see this resource family’s vocabulary. |
| `instanceStatus` | string | Yes | — |
| `completionReason` | null or string | No | — |
| `isMultiInstance` | boolean | Yes | — |
| `owner` | null or string | No | Effective user-task owner. An explicit assignment takes precedence over a claim; non-user-task executions have no owner. |
| `enteredViaFlowId` | null or integer (int32) | No | — |
| `selectedFlowId` | null or integer (int32) | No | — |
| `exitedViaFlowId` | null or integer (int32) | No | — |
| `aggregateFlowId` | null or integer (int32) | No | The winning aggregate flow of the owning multi-instance execution. This is distinct from a child item's selected flow. |
| `startedBy` | null or string | No | The causal actor that opened or activated this visit. |
| `completedBy` | null or string | No | — |
| `startedDelegatedAccess` | null or [DelegatedTaskAccessDto](#schema-delegatedtaskaccessdto) | No | — |
| `completedDelegatedAccess` | null or [DelegatedTaskAccessDto](#schema-delegatedtaskaccessdto) | No | — |
| `createdAt` | string (date-time) | Yes | Server creation timestamp. |
| `startedAt` | null or string (date-time) | No | — |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |
| `completedAt` | null or string (date-time) | No | Nullable until completed; ISO 8601 timestamp. |
| `durationMilliseconds` | null or integer (int64) | No | Elapsed milliseconds from StartedAt to CompletedAt, or to the search query's captured time for a currently active execution. Pending rows have no duration. |
| `isCutoverSeeded` | boolean | Yes | — |

</details>

<a id="schema-nodeexecutionvariablechangedto"></a>

<details>
<summary>NodeExecutionVariableChangeDto</summary>

One variable write attributed to this execution. SourceActionId retains the runtime write source: it may identify a sequence flow for a user action or a node/boundary for automatic and message writes.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | — |
| `variableName` | string | Yes | — |
| `sourceActionId` | null or integer (int32) | Yes | — |
| `setBy` | null or string | Yes | — |
| `value` | [JsonElement](#schema-jsonelement) | Yes | — |
| `setAt` | string (date-time) | Yes | — |
| `delegatedAccess` | null or [DelegatedTaskAccessDto](#schema-delegatedtaskaccessdto) | No | — |

</details>

<a id="schema-pagedresultofadministrativeactionbatchitemdto"></a>

<details>
<summary>PagedResultOfAdministrativeActionBatchItemDto</summary>

Generic wrapper representing a paged list of items.

items/page/pageSize/totalCount are the core fields. nextCursor is omitted when null. There are no totalPages, hasNextPage or hasPreviousPage fields.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `items` | array of [AdministrativeActionBatchItemDto](#schema-administrativeactionbatchitemdto) | Yes | The collection of items on the current page. |
| `page` | integer (int32) | Yes | The 1-based page index. |
| `pageSize` | integer (int32) | Yes | The maximum number of items in the page. |
| `totalCount` | integer (int64) | Yes | The total number of matching items across all pages. |
| `nextCursor` | null or string | No | Opaque keyset cursor for the next page when this endpoint supports cursor paging. Null means there is no later page. |

</details>

<a id="schema-pagedresultofadministrativeactionbatchsummarydto"></a>

<details>
<summary>PagedResultOfAdministrativeActionBatchSummaryDto</summary>

Generic wrapper representing a paged list of items.

items/page/pageSize/totalCount are the core fields. nextCursor is omitted when null. There are no totalPages, hasNextPage or hasPreviousPage fields.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `items` | array of [AdministrativeActionBatchSummaryDto](#schema-administrativeactionbatchsummarydto) | Yes | The collection of items on the current page. |
| `page` | integer (int32) | Yes | The 1-based page index. |
| `pageSize` | integer (int32) | Yes | The maximum number of items in the page. |
| `totalCount` | integer (int64) | Yes | The total number of matching items across all pages. |
| `nextCursor` | null or string | No | Opaque keyset cursor for the next page when this endpoint supports cursor paging. Null means there is no later page. |

</details>

<a id="schema-pagedresultofadministrativeactioncandidatedto"></a>

<details>
<summary>PagedResultOfAdministrativeActionCandidateDto</summary>

Generic wrapper representing a paged list of items.

items/page/pageSize/totalCount are the core fields. nextCursor is omitted when null. There are no totalPages, hasNextPage or hasPreviousPage fields.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `items` | array of [AdministrativeActionCandidateDto](#schema-administrativeactioncandidatedto) | Yes | The collection of items on the current page. |
| `page` | integer (int32) | Yes | The 1-based page index. |
| `pageSize` | integer (int32) | Yes | The maximum number of items in the page. |
| `totalCount` | integer (int64) | Yes | The total number of matching items across all pages. |
| `nextCursor` | null or string | No | Opaque keyset cursor for the next page when this endpoint supports cursor paging. Null means there is no later page. |

</details>

<a id="schema-pagedresultofinboxitemdto"></a>

<details>
<summary>PagedResultOfInboxItemDto</summary>

Generic wrapper representing a paged list of items.

items/page/pageSize/totalCount are the core fields. nextCursor is omitted when null. There are no totalPages, hasNextPage or hasPreviousPage fields.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `items` | array of [InboxItemDto](#schema-inboxitemdto) | Yes | The collection of items on the current page. |
| `page` | integer (int32) | Yes | The 1-based page index. |
| `pageSize` | integer (int32) | Yes | The maximum number of items in the page. |
| `totalCount` | integer (int64) | Yes | The total number of matching items across all pages. |
| `nextCursor` | null or string | No | Opaque keyset cursor for the next page when this endpoint supports cursor paging. Null means there is no later page. |

</details>

<a id="schema-pagedresultofincidentsummarydto"></a>

<details>
<summary>PagedResultOfIncidentSummaryDto</summary>

Generic wrapper representing a paged list of items.

items/page/pageSize/totalCount are the core fields. nextCursor is omitted when null. There are no totalPages, hasNextPage or hasPreviousPage fields.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `items` | array of [IncidentSummaryDto](#schema-incidentsummarydto) | Yes | The collection of items on the current page. |
| `page` | integer (int32) | Yes | The 1-based page index. |
| `pageSize` | integer (int32) | Yes | The maximum number of items in the page. |
| `totalCount` | integer (int64) | Yes | The total number of matching items across all pages. |
| `nextCursor` | null or string | No | Opaque keyset cursor for the next page when this endpoint supports cursor paging. Null means there is no later page. |

</details>

<a id="schema-pagedresultofinstancesummarydto"></a>

<details>
<summary>PagedResultOfInstanceSummaryDto</summary>

Generic wrapper representing a paged list of items.

items/page/pageSize/totalCount are the core fields. nextCursor is omitted when null. There are no totalPages, hasNextPage or hasPreviousPage fields.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `items` | array of [InstanceSummaryDto](#schema-instancesummarydto) | Yes | The collection of items on the current page. |
| `page` | integer (int32) | Yes | The 1-based page index. |
| `pageSize` | integer (int32) | Yes | The maximum number of items in the page. |
| `totalCount` | integer (int64) | Yes | The total number of matching items across all pages. |
| `nextCursor` | null or string | No | Opaque keyset cursor for the next page when this endpoint supports cursor paging. Null means there is no later page. |

</details>

<a id="schema-pagedresultofinstancevariableupdatebatchitemdto"></a>

<details>
<summary>PagedResultOfInstanceVariableUpdateBatchItemDto</summary>

Generic wrapper representing a paged list of items.

items/page/pageSize/totalCount are the core fields. nextCursor is omitted when null. There are no totalPages, hasNextPage or hasPreviousPage fields.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `items` | array of [InstanceVariableUpdateBatchItemDto](#schema-instancevariableupdatebatchitemdto) | Yes | The collection of items on the current page. |
| `page` | integer (int32) | Yes | The 1-based page index. |
| `pageSize` | integer (int32) | Yes | The maximum number of items in the page. |
| `totalCount` | integer (int64) | Yes | The total number of matching items across all pages. |
| `nextCursor` | null or string | No | Opaque keyset cursor for the next page when this endpoint supports cursor paging. Null means there is no later page. |

</details>

<a id="schema-pagedresultofinstancevariableupdatebatchsummarydto"></a>

<details>
<summary>PagedResultOfInstanceVariableUpdateBatchSummaryDto</summary>

Generic wrapper representing a paged list of items.

items/page/pageSize/totalCount are the core fields. nextCursor is omitted when null. There are no totalPages, hasNextPage or hasPreviousPage fields.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `items` | array of [InstanceVariableUpdateBatchSummaryDto](#schema-instancevariableupdatebatchsummarydto) | Yes | The collection of items on the current page. |
| `page` | integer (int32) | Yes | The 1-based page index. |
| `pageSize` | integer (int32) | Yes | The maximum number of items in the page. |
| `totalCount` | integer (int64) | Yes | The total number of matching items across all pages. |
| `nextCursor` | null or string | No | Opaque keyset cursor for the next page when this endpoint supports cursor paging. Null means there is no later page. |

</details>

<a id="schema-pagedresultofinstancevariableupdatecandidatedto"></a>

<details>
<summary>PagedResultOfInstanceVariableUpdateCandidateDto</summary>

Generic wrapper representing a paged list of items.

items/page/pageSize/totalCount are the core fields. nextCursor is omitted when null. There are no totalPages, hasNextPage or hasPreviousPage fields.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `items` | array of [InstanceVariableUpdateCandidateDto](#schema-instancevariableupdatecandidatedto) | Yes | The collection of items on the current page. |
| `page` | integer (int32) | Yes | The 1-based page index. |
| `pageSize` | integer (int32) | Yes | The maximum number of items in the page. |
| `totalCount` | integer (int64) | Yes | The total number of matching items across all pages. |
| `nextCursor` | null or string | No | Opaque keyset cursor for the next page when this endpoint supports cursor paging. Null means there is no later page. |

</details>

<a id="schema-pagedresultofinstanceversionchangebatchitemdto"></a>

<details>
<summary>PagedResultOfInstanceVersionChangeBatchItemDto</summary>

Generic wrapper representing a paged list of items.

items/page/pageSize/totalCount are the core fields. nextCursor is omitted when null. There are no totalPages, hasNextPage or hasPreviousPage fields.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `items` | array of [InstanceVersionChangeBatchItemDto](#schema-instanceversionchangebatchitemdto) | Yes | The collection of items on the current page. |
| `page` | integer (int32) | Yes | The 1-based page index. |
| `pageSize` | integer (int32) | Yes | The maximum number of items in the page. |
| `totalCount` | integer (int64) | Yes | The total number of matching items across all pages. |
| `nextCursor` | null or string | No | Opaque keyset cursor for the next page when this endpoint supports cursor paging. Null means there is no later page. |

</details>

<a id="schema-pagedresultofinstanceversionchangebatchsummarydto"></a>

<details>
<summary>PagedResultOfInstanceVersionChangeBatchSummaryDto</summary>

Generic wrapper representing a paged list of items.

items/page/pageSize/totalCount are the core fields. nextCursor is omitted when null. There are no totalPages, hasNextPage or hasPreviousPage fields.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `items` | array of [InstanceVersionChangeBatchSummaryDto](#schema-instanceversionchangebatchsummarydto) | Yes | The collection of items on the current page. |
| `page` | integer (int32) | Yes | The 1-based page index. |
| `pageSize` | integer (int32) | Yes | The maximum number of items in the page. |
| `totalCount` | integer (int64) | Yes | The total number of matching items across all pages. |
| `nextCursor` | null or string | No | Opaque keyset cursor for the next page when this endpoint supports cursor paging. Null means there is no later page. |

</details>

<a id="schema-pagedresultofinstanceversionchangecandidatedto"></a>

<details>
<summary>PagedResultOfInstanceVersionChangeCandidateDto</summary>

Generic wrapper representing a paged list of items.

items/page/pageSize/totalCount are the core fields. nextCursor is omitted when null. There are no totalPages, hasNextPage or hasPreviousPage fields.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `items` | array of [InstanceVersionChangeCandidateDto](#schema-instanceversionchangecandidatedto) | Yes | The collection of items on the current page. |
| `page` | integer (int32) | Yes | The 1-based page index. |
| `pageSize` | integer (int32) | Yes | The maximum number of items in the page. |
| `totalCount` | integer (int64) | Yes | The total number of matching items across all pages. |
| `nextCursor` | null or string | No | Opaque keyset cursor for the next page when this endpoint supports cursor paging. Null means there is no later page. |

</details>

<a id="schema-pagedresultofjobattemptdto"></a>

<details>
<summary>PagedResultOfJobAttemptDto</summary>

Generic wrapper representing a paged list of items.

items/page/pageSize/totalCount are the core fields. nextCursor is omitted when null. There are no totalPages, hasNextPage or hasPreviousPage fields.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `items` | array of [JobAttemptDto](#schema-jobattemptdto) | Yes | The collection of items on the current page. |
| `page` | integer (int32) | Yes | The 1-based page index. |
| `pageSize` | integer (int32) | Yes | The maximum number of items in the page. |
| `totalCount` | integer (int64) | Yes | The total number of matching items across all pages. |
| `nextCursor` | null or string | No | Opaque keyset cursor for the next page when this endpoint supports cursor paging. Null means there is no later page. |

</details>

<a id="schema-pagedresultofjobsummarydto"></a>

<details>
<summary>PagedResultOfJobSummaryDto</summary>

Generic wrapper representing a paged list of items.

items/page/pageSize/totalCount are the core fields. nextCursor is omitted when null. There are no totalPages, hasNextPage or hasPreviousPage fields.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `items` | array of [JobSummaryDto](#schema-jobsummarydto) | Yes | The collection of items on the current page. |
| `page` | integer (int32) | Yes | The 1-based page index. |
| `pageSize` | integer (int32) | Yes | The maximum number of items in the page. |
| `totalCount` | integer (int64) | Yes | The total number of matching items across all pages. |
| `nextCursor` | null or string | No | Opaque keyset cursor for the next page when this endpoint supports cursor paging. Null means there is no later page. |

</details>

<a id="schema-pagedresultofmanagedusertaskdto"></a>

<details>
<summary>PagedResultOfManagedUserTaskDto</summary>

Generic wrapper representing a paged list of items.

items/page/pageSize/totalCount are the core fields. nextCursor is omitted when null. There are no totalPages, hasNextPage or hasPreviousPage fields.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `items` | array of [ManagedUserTaskDto](#schema-managedusertaskdto) | Yes | The collection of items on the current page. |
| `page` | integer (int32) | Yes | The 1-based page index. |
| `pageSize` | integer (int32) | Yes | The maximum number of items in the page. |
| `totalCount` | integer (int64) | Yes | The total number of matching items across all pages. |
| `nextCursor` | null or string | No | Opaque keyset cursor for the next page when this endpoint supports cursor paging. Null means there is no later page. |

</details>

<a id="schema-pagedresultofnodeexecutionsummarydto"></a>

<details>
<summary>PagedResultOfNodeExecutionSummaryDto</summary>

Generic wrapper representing a paged list of items.

items/page/pageSize/totalCount are the core fields. nextCursor is omitted when null. There are no totalPages, hasNextPage or hasPreviousPage fields.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `items` | array of [NodeExecutionSummaryDto](#schema-nodeexecutionsummarydto) | Yes | The collection of items on the current page. |
| `page` | integer (int32) | Yes | The 1-based page index. |
| `pageSize` | integer (int32) | Yes | The maximum number of items in the page. |
| `totalCount` | integer (int64) | Yes | The total number of matching items across all pages. |
| `nextCursor` | null or string | No | Opaque keyset cursor for the next page when this endpoint supports cursor paging. Null means there is no later page. |

</details>

<a id="schema-pagedresultofsharedvariableclientdto"></a>

<details>
<summary>PagedResultOfSharedVariableClientDto</summary>

Generic wrapper representing a paged list of items.

items/page/pageSize/totalCount are the core fields. nextCursor is omitted when null. There are no totalPages, hasNextPage or hasPreviousPage fields.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `items` | array of [SharedVariableClientDto](#schema-sharedvariableclientdto) | Yes | The collection of items on the current page. |
| `page` | integer (int32) | Yes | The 1-based page index. |
| `pageSize` | integer (int32) | Yes | The maximum number of items in the page. |
| `totalCount` | integer (int64) | Yes | The total number of matching items across all pages. |
| `nextCursor` | null or string | No | Opaque keyset cursor for the next page when this endpoint supports cursor paging. Null means there is no later page. |

</details>

<a id="schema-pagedresultofsharedvariablemetadatadto"></a>

<details>
<summary>PagedResultOfSharedVariableMetadataDto</summary>

Generic wrapper representing a paged list of items.

items/page/pageSize/totalCount are the core fields. nextCursor is omitted when null. There are no totalPages, hasNextPage or hasPreviousPage fields.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `items` | array of [SharedVariableMetadataDto](#schema-sharedvariablemetadatadto) | Yes | The collection of items on the current page. |
| `page` | integer (int32) | Yes | The 1-based page index. |
| `pageSize` | integer (int32) | Yes | The maximum number of items in the page. |
| `totalCount` | integer (int64) | Yes | The total number of matching items across all pages. |
| `nextCursor` | null or string | No | Opaque keyset cursor for the next page when this endpoint supports cursor paging. Null means there is no later page. |

</details>

<a id="schema-pagedresultofuserdelegationdto"></a>

<details>
<summary>PagedResultOfUserDelegationDto</summary>

Generic wrapper representing a paged list of items.

items/page/pageSize/totalCount are the core fields. nextCursor is omitted when null. There are no totalPages, hasNextPage or hasPreviousPage fields.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `items` | array of [UserDelegationDto](#schema-userdelegationdto) | Yes | The collection of items on the current page. |
| `page` | integer (int32) | Yes | The 1-based page index. |
| `pageSize` | integer (int32) | Yes | The maximum number of items in the page. |
| `totalCount` | integer (int64) | Yes | The total number of matching items across all pages. |
| `nextCursor` | null or string | No | Opaque keyset cursor for the next page when this endpoint supports cursor paging. Null means there is no later page. |

</details>

<a id="schema-pagedresultofusertaskdto"></a>

<details>
<summary>PagedResultOfUserTaskDto</summary>

Generic wrapper representing a paged list of items.

items/page/pageSize/totalCount are the core fields. nextCursor is omitted when null. There are no totalPages, hasNextPage or hasPreviousPage fields.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `items` | array of [UserTaskDto](#schema-usertaskdto) | Yes | The collection of items on the current page. |
| `page` | integer (int32) | Yes | The 1-based page index. |
| `pageSize` | integer (int32) | Yes | The maximum number of items in the page. |
| `totalCount` | integer (int64) | Yes | The total number of matching items across all pages. |
| `nextCursor` | null or string | No | Opaque keyset cursor for the next page when this endpoint supports cursor paging. Null means there is no later page. |

</details>

<a id="schema-previewinstanceversionchangerequest"></a>

<details>
<summary>PreviewInstanceVersionChangeRequest</summary>

Request payload for checking whether a running instance can move to another workflow version.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `targetWorkflowId` | integer (int64) | Yes | Exact target workflow-version database ID. |

</details>

<a id="schema-previewretentionrequest"></a>

<details>
<summary>PreviewRetentionRequest</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `category` | string | Yes | — |
| `retentionDays` | null or integer (int32) | No | null means keep forever; otherwise integer from 1 through 36500. |

</details>

<a id="schema-reactivateinstancerequest"></a>

<details>
<summary>ReactivateInstanceRequest</summary>

Request payload for atomically reactivating a completed or cancelled workflow instance at a previously visited user task.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `targetNodeId` | integer (int32) | Yes | — |
| `expectedWorkflowId` | integer (int64) | Yes | — |
| `expectedUpdatedAt` | string (date-time) | Yes | Exact server-returned timestamp for optimistic concurrency. |
| `reason` | string | Yes | Human-readable audit reason; required where non-null in version-change/reactivation requests. |

</details>

<a id="schema-reactivatesharedvariablerequest"></a>

<details>
<summary>ReactivateSharedVariableRequest</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `expectedRevision` | integer (int64) | Yes | Exact current revision from a preceding read. |
| `requestId` | null or string | No | Optional mutation retry identifier; reuse only for the identical logical request. |
| `reason` | null or string | No | Human-readable audit reason; required where non-null in version-change/reactivation requests. |

</details>

<a id="schema-retentioncategoryprogressdto"></a>

<details>
<summary>RetentionCategoryProgressDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `category` | string | Yes | — |
| `cutoff` | null or string (date-time) | Yes | — |
| `policyRevision` | integer (int64) | Yes | — |
| `status` | string | Yes | pending, running, paused, succeeded, failed, keepForever, policyChanged, or budgetReached. |
| `deletedRows` | integer (int64) | Yes | — |
| `protectedRows` | integer (int64) | Yes | — |
| `message` | null or string | Yes | — |

</details>

<a id="schema-retentionpolicydto"></a>

<details>
<summary>RetentionPolicyDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `category` | string | Yes | — |
| `retentionDays` | null or integer (int32) | Yes | null means keep forever; otherwise integer from 1 through 36500. |
| `revision` | integer (int64) | Yes | — |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |
| `updatedBy` | null or string | Yes | — |
| `isInitialized` | boolean | Yes | False means the operational retention default has not yet been initialized by the Worker. |

</details>

<a id="schema-retentionpreviewdto"></a>

<details>
<summary>RetentionPreviewDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `category` | string | Yes | — |
| `cutoff` | null or string (date-time) | Yes | — |
| `tables` | array of [RetentionTablePreviewDto](#schema-retentiontablepreviewdto) | Yes | — |
| `isLowerBound` | boolean | Yes | — |
| `observedAt` | string (date-time) | Yes | — |

</details>

<a id="schema-retentionrundto"></a>

<details>
<summary>RetentionRunDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | string (uuid) | Yes | — |
| `status` | string | Yes | queued, running, paused, succeeded, budgetReached, or failed. |
| `requestedAt` | string (date-time) | Yes | — |
| `requestedBy` | null or string | Yes | — |
| `startedAt` | null or string (date-time) | Yes | — |
| `completedAt` | null or string (date-time) | Yes | Nullable until completed; ISO 8601 timestamp. |
| `message` | null or string | Yes | — |
| `categories` | array of [RetentionCategoryProgressDto](#schema-retentioncategoryprogressdto) | Yes | — |

</details>

<a id="schema-retentionstatusdto"></a>

<details>
<summary>RetentionStatusDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `policies` | array of [RetentionPolicyDto](#schema-retentionpolicydto) | Yes | — |
| `currentRun` | null or [RetentionRunDto](#schema-retentionrundto) | Yes | — |
| `lastRun` | null or [RetentionRunDto](#schema-retentionrundto) | Yes | — |
| `nextScheduledAt` | null or string (date-time) | Yes | — |
| `workerLastSeenAt` | null or string (date-time) | Yes | — |

</details>

<a id="schema-retentiontablepreviewdto"></a>

<details>
<summary>RetentionTablePreviewDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `table` | string | Yes | — |
| `eligibleCount` | integer (int64) | Yes | — |
| `protectedCount` | integer (int64) | Yes | — |

</details>

<a id="schema-retryincidentresultdto"></a>

<details>
<summary>RetryIncidentResultDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `incidentId` | integer (int64) | Yes | — |
| `jobId` | integer (int64) | Yes | — |
| `incidentStatus` | string | Yes | — |
| `jobStatus` | string | Yes | — |
| `dueAt` | string (date-time) | Yes | — |

</details>

<a id="schema-revokesharedvariableclientrequest"></a>

<details>
<summary>RevokeSharedVariableClientRequest</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `expectedRevision` | integer (int64) | Yes | Exact current revision from a preceding read. |
| `reason` | null or string | No | Human-readable audit reason; required where non-null in version-change/reactivation requests. |

</details>

<a id="schema-rotatesharedvariableclientsecretrequest"></a>

<details>
<summary>RotateSharedVariableClientSecretRequest</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `expectedRevision` | integer (int64) | Yes | Exact current revision from a preceding read. |
| `gracePeriodHours` | integer (int32) | No | Default 24; integer from 0 through 168. Default: `24`. |

</details>

<a id="schema-rotatesharedvariableclientsecretresult"></a>

<details>
<summary>RotateSharedVariableClientSecretResult</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `client` | [SharedVariableClientDto](#schema-sharedvariableclientdto) | Yes | — |
| `clientSecret` | string | Yes | Returned only by creation/rotation; never recoverable from metadata. |

</details>

<a id="schema-searchsortdto"></a>

<details>
<summary>SearchSortDto</summary>

One structured search sort clause. Each endpoint validates its own supported field names and applies its existing default order when no clauses are supplied.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `field` | string | Yes | — |
| `direction` | string | Yes | asc or desc. |

</details>

<a id="schema-sequenceflowmodel"></a>

<details>
<summary>SequenceFlowModel</summary>

Represents a directed sequence flow (transition connection) in a workflow.

Flow IDs are authored integer IDs. Normal user tasks cannot have default flows; multi-instance tasks require one pure engine-only default fallback. isSelectable false is engine-only on MI outcomes. condition and input validation are checked again at execution.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int32) | No | The unique integer ID of the sequence flow. |
| `name` | string | No | The name of the sequence flow. |
| `externalId` | null or string | No | The user-defined external ID of the sequence flow. |
| `attributes` | array of [WorkflowAttributeModel](#schema-workflowattributemodel) | No | Ordered string metadata attached to this sequence flow. |
| `sourceRef` | integer (int32) | No | The ID of the source flow node. |
| `targetRef` | integer (int32) | No | The ID of the target flow node. |
| `roles` | array of string | No | Roles authorized to transition along this flow (userTask flows only). |
| `rolesVariable` | null or string | No | Declared string-array variable resolved with the source user task's role policy. |
| `variables` | array of [VariableModel](#schema-variablemodel) | No | Required input variable declarations for this transition flow. |
| `condition` | null or string | No | The NCalc condition expression evaluated to determine if this flow is taken. |
| `conditionPriority` | null or integer (int32) | No | Evaluation order for a non-default exclusive-gateway flow. Lower values are evaluated first. This field is not used by user-task flows. |
| `isDefault` | boolean | No | If true, this flow acts as the default fallback when other conditions fail. Default: `false`. |
| `isSelectable` | boolean | No | Default true. Set false for an engine-only multi-instance outcome/fallback. Default: `true`. |
| `canActWithoutClaim` | boolean | No | If true, an actor can trigger this flow without claiming the userTask first. Default: `false`. |
| `canActWithoutClaimRoles` | array of string | No | Roles allowed to bypass this flow's claim requirement. These roles are evaluated in addition to the task and flow roles. An empty list permits every otherwise-authorized actor, preserving older definitions. |
| `completionCondition` | null or string | No | Multi-instance aggregate condition; evaluation timing is afterEach or afterAll according to the task configuration. |
| `completionPriority` | null or integer (int32) | No | Lower values win when several multi-instance completion conditions match. |
| `cancelRemainingInstances` | boolean | No | Whether selecting this flow immediately cancels the other multi-instance items. Default: `false`. |

</details>

<a id="schema-serviceheadermodel"></a>

<details>
<summary>ServiceHeaderModel</summary>

Represents a key-value HTTP header entry.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `name` | string | No | The HTTP header name (e.g., Authorization). |
| `value` | string | No | The HTTP header value (supports variable interpolation). |

</details>

<a id="schema-serviceoutputmappingmodel"></a>

<details>
<summary>ServiceOutputMappingModel</summary>

Maps a typed value from an HTTP service response into an instance variable.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `variable` | string | No | The name of the process variable to write the extracted value to. |
| `path` | string | No | The dotted path within the JSON response body to extract the value from. |
| `required` | boolean | No | If true, a missing or unresolvable path will fail the step. |
| `dataType` | null or string | No | The scalar element type expected from the response. Null identifies a legacy raw mapping until normalization. |
| `isArray` | null or boolean | No | Whether the response value must be an array of string? ServiceOutputMappingModel.DataType. |
| `defaultValue` | null or [JsonElement](#schema-jsonelement) | No | — |
| `validation` | null or string | No | Optional NCalc rule evaluated against the final output overlay. |

</details>

<a id="schema-servicetaskmodel"></a>

<details>
<summary>ServiceTaskModel</summary>

Configures the HTTP REST call made by a service task.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `type` | null or string | No | The connector implementation used by this service task. Existing definitions that omit the discriminator are normalized to `rest`. Default: `"rest"`. |
| `method` | string | No | The HTTP method to use (e.g. GET, POST, PUT). Default: `"GET"`. |
| `url` | string | No | The target REST API URL (supports variable interpolation). |
| `headers` | array of [ServiceHeaderModel](#schema-serviceheadermodel) | No | Optional HTTP header values to send with the request. |
| `body` | null or string | No | Optional body payload to send (for POST/PUT). |
| `timeoutSeconds` | integer (int32) | No | Timeout limit in seconds before the HTTP request is considered failed. Default: `30`. |
| `statusVariable` | null or string | No | Optional variable name to store the returned HTTP status code. |
| `outputMappings` | array of [ServiceOutputMappingModel](#schema-serviceoutputmappingmodel) | No | Dotted-path maps to extract fields from the JSON response and write to variables. |

</details>

<a id="schema-sharedvariablebindingmetadatadto"></a>

<details>
<summary>SharedVariableBindingMetadataDto</summary>

Value-free catalog metadata for one workflow-local shared alias. Instance APIs expose this contract without copying deployment-wide values into an instance response or history.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `alias` | string | Yes | — |
| `key` | string | Yes | — |
| `access` | string | Yes | — |
| `dataType` | string | Yes | string, number, boolean, date, datetime, or json; arrays use isArray. |
| `isArray` | boolean | Yes | — |
| `nullable` | boolean | Yes | — |
| `validation` | null or string | Yes | — |
| `status` | string | Yes | Lifecycle value; see this resource family’s vocabulary. |
| `revision` | integer (int64) | Yes | — |
| `hasValue` | boolean | Yes | False means unset; true with null is an explicit null value. |
| `createdAt` | string (date-time) | Yes | Server creation timestamp. |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |
| `archivedAt` | null or string (date-time) | Yes | — |
| `valueRevision` | integer (int64) | No | Value-change revision, separate from the metadata-inclusive revision. Default: `0`. |

</details>

<a id="schema-sharedvariableclientdto"></a>

<details>
<summary>SharedVariableClientDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | — |
| `clientId` | string | Yes | — |
| `displayName` | string | Yes | — |
| `scopes` | array of string | Yes | shared-variables.read and/or shared-variables.write. |
| `status` | string | Yes | active or revoked. |
| `revision` | integer (int64) | Yes | — |
| `expiresAt` | null or string (date-time) | Yes | Nullable expiry; null means no configured expiry. |
| `createdAt` | string (date-time) | Yes | Server creation timestamp. |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |
| `revokedAt` | null or string (date-time) | Yes | — |

</details>

<a id="schema-sharedvariablelifecycleblockersdto"></a>

<details>
<summary>SharedVariableLifecycleBlockersDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `publishedDefinitionCount` | integer (int64) | Yes | — |
| `runningInstanceCount` | integer (int64) | Yes | — |
| `openJobCount` | integer (int64) | Yes | — |
| `reasons` | array of string | Yes | — |
| `canArchive` | boolean | No | — |

</details>

<a id="schema-sharedvariablemetadatadto"></a>

<details>
<summary>SharedVariableMetadataDto</summary>

Catalog and lifecycle metadata for a deployment-wide shared variable. The current value is intentionally available only from the explicit value route.

Does not contain the value. Fetch SharedVariableValueDto explicitly. dataType is immutable after creation; status is active or archived.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | — |
| `key` | string | Yes | — |
| `dataType` | string | Yes | string, number, boolean, date, datetime, or json; arrays use isArray. |
| `isArray` | boolean | Yes | — |
| `nullable` | boolean | Yes | — |
| `validation` | null or string | Yes | — |
| `description` | null or string | Yes | — |
| `hasValue` | boolean | Yes | False means unset; true with null is an explicit null value. |
| `status` | string | Yes | active or archived. |
| `revision` | integer (int64) | Yes | — |
| `createdAt` | string (date-time) | Yes | Server creation timestamp. |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |
| `archivedAt` | null or string (date-time) | Yes | — |
| `valueRevision` | integer (int64) | No | Value-change revision, separate from the metadata-inclusive revision. Default: `0`. |
| `historyPrunedAt` | null or string (date-time) | No | — |

</details>

<a id="schema-sharedvariablerevisiondto"></a>

<details>
<summary>SharedVariableRevisionDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `revision` | integer (int64) | Yes | — |
| `operation` | string | Yes | — |
| `valueChanged` | boolean | Yes | — |
| `hasValue` | boolean | Yes | False means unset; true with null is an explicit null value. |
| `value` | null or [JsonElement](#schema-jsonelement) | Yes | — |
| `callerKind` | string | Yes | user, client, workflow, or system. |
| `callerId` | string | Yes | — |
| `source` | string | Yes | — |
| `reason` | null or string | Yes | Human-readable audit reason; required where non-null in version-change/reactivation requests. |
| `workflowDefinitionId` | null or integer (int64) | Yes | Exact immutable workflow-version database ID. |
| `instanceId` | null or integer (int64) | Yes | Runtime workflow-instance database ID. |
| `nodeExecutionId` | null or integer (int64) | Yes | — |
| `sourceActionId` | null or integer (int32) | Yes | — |
| `createdAt` | string (date-time) | Yes | Server creation timestamp. |

</details>

<a id="schema-sharedvariablevaluedto"></a>

<details>
<summary>SharedVariableValueDto</summary>

Current-value projection. HasValue distinguishes an explicitly stored JSON null from a variable for which no value has been set.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `key` | string | Yes | — |
| `hasValue` | boolean | Yes | False means unset; true with null is an explicit null value. |
| `value` | null or [JsonElement](#schema-jsonelement) | Yes | — |
| `revision` | integer (int64) | Yes | — |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |
| `valueRevision` | integer (int64) | No | Value-change revision, separate from the metadata-inclusive revision. Default: `0`. |

</details>

<a id="schema-sharedvariablewritecorrelationdto"></a>

<details>
<summary>SharedVariableWriteCorrelationDto</summary>

Value-free correlation between a workflow-local alias and the durable shared-variable revision produced by one workflow action.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `alias` | string | Yes | — |
| `key` | string | Yes | — |
| `revision` | integer (int64) | Yes | — |
| `valueChanged` | boolean | Yes | — |

</details>

<a id="schema-startconflictdto"></a>

<details>
<summary>StartConflictDto</summary>

Conflict returned when a start idempotency key or message-start business key is already owned.

Used for start idempotency and message-start business-key conflicts. User-start business-key conflicts use a separate middleware object with existingInstanceId and error.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `code` | string | Yes | Machine-readable conflict code. |
| `instanceId` | integer (int64) | Yes | The existing workflow instance that owns the key. |

</details>

<a id="schema-startinstancerequest"></a>

<details>
<summary>StartInstanceRequest</summary>

Request payload for starting a new workflow instance.

Exactly one of workflowId and workflowKey must be non-null. startEventId is optional; variables must satisfy the selected start event. Idempotency comes from a configured HTTP header, never a request-body field.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `workflowId` | null or integer (int64) | No | Optional exact immutable definition ID; exactly one of workflowId/workflowKey is required. |
| `workflowKey` | null or string | No | Optional stable family key; exactly one of workflowId/workflowKey is required. |
| `startEventId` | null or integer (int32) | No | Optional authored user start-event ID; omission uses the default user start. |
| `variables` | null or object | No | Optional name/value input object; required entries depend on the selected start event. |

</details>

<a id="schema-startinstanceresultdto"></a>

<details>
<summary>StartInstanceResultDto</summary>

Default slim start response. Automatic routing has already run; inspect executionPositions for parallel execution. fault and completion are omitted when null.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | — |
| `currentNodeId` | integer (int32) | Yes | — |
| `currentNodeName` | string | Yes | — |
| `currentNodeExternalId` | null or string | Yes | — |
| `status` | string | Yes | Lifecycle value; see this resource family’s vocabulary. |
| `businessKey` | null or string | Yes | — |
| `businessKeyUniqueness` | null or string | Yes | — |
| `startedBy` | null or string | Yes | — |
| `createdAt` | string (date-time) | Yes | Server creation timestamp. |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |
| `fault` | null or [FaultInfoDto](#schema-faultinfodto) | No | — |
| `executionPositions` | array of [ExecutionPositionDto](#schema-executionpositiondto) | No | All exposed token positions; the representative currentNode fields do not imply a single active token. |
| `completion` | null or [CompletionInfoDto](#schema-completioninfodto) | No | — |

</details>

<a id="schema-takeflowrequest"></a>

<details>
<summary>TakeFlowRequest</summary>

Request payload for advancing a workflow instance down a sequence flow.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `variables` | null or object | No | Optional name/value input object; required entries depend on the selected flow. |

</details>

<a id="schema-taskdistributionmodel"></a>

<details>
<summary>TaskDistributionModel</summary>

Machine credentials for the workflow-family task-distribution API.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `clientId` | string | No | — |
| `clientSecret` | string | No | Returned only by creation/rotation; never recoverable from metadata. |

</details>

<a id="schema-timerdefinitionmodel"></a>

<details>
<summary>TimerDefinitionModel</summary>

BPMN-aligned timer expression. Exactly one member must be nonblank.

Timer kinds: timeDate, timeDuration, timeCycle. Worker execution is required; see BPMN support for node-specific allowed forms.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `timeDate` | null or string | No | Absolute timer instant; exactly one of timeDate/timeDuration/timeCycle must be nonblank. |
| `timeDuration` | null or string | No | ISO 8601 duration (for example PT30S). |
| `timeCycle` | null or string | No | Supported repeating ISO 8601 schedule; see BPMN support for node-specific restrictions. |

</details>

<a id="schema-unassignusertaskrequest"></a>

<details>
<summary>UnassignUserTaskRequest</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `expectedUpdatedAt` | string (date-time) | Yes | Exact server-returned timestamp for optimistic concurrency. |
| `reason` | null or string | No | Human-readable audit reason; required where non-null in version-change/reactivation requests. |

</details>

<a id="schema-updateenginesettingrequest"></a>

<details>
<summary>UpdateEngineSettingRequest</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `value` | string | Yes | — |
| `description` | null or string | No | — |
| `expectedUpdatedAt` | string (date-time) | Yes | Exact server-returned timestamp for optimistic concurrency. |

</details>

<a id="schema-updateinstancevariablesrequest"></a>

<details>
<summary>UpdateInstanceVariablesRequest</summary>

Atomically updates one or more variables on one running instance.

1 MiB request limit. Variables are raw administrative writes, not authored input validation. Read the resulting warnings; writes can trigger conditional events.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `variables` | array of [InstanceVariableWriteDto](#schema-instancevariablewritedto) | Yes | Runtime name/value map or write array as typed here; input contracts and raw administrative writes have different rules. |
| `reason` | null or string | No | Human-readable audit reason; required where non-null in version-change/reactivation requests. |
| `idempotencyKey` | null or string | No | Optional logical-operation retry key. Not the start-node HTTP idempotency header. |

</details>

<a id="schema-updateinstancevariablesresultdto"></a>

<details>
<summary>UpdateInstanceVariablesResultDto</summary>

Result of one atomic administrative instance-variable operation.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `operationId` | integer (int64) | Yes | — |
| `instanceId` | integer (int64) | Yes | Runtime workflow-instance database ID. |
| `workflowDefinitionId` | integer (int64) | Yes | Exact immutable workflow-version database ID. |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |
| `variables` | array of [InstanceVariableUpdateOutcomeDto](#schema-instancevariableupdateoutcomedto) | Yes | Runtime name/value map or write array as typed here; input contracts and raw administrative writes have different rules. |
| `warnings` | array of [InstanceVariableUpdateIssueDto](#schema-instancevariableupdateissuedto) | Yes | — |
| `batchId` | null or integer (int64) | No | — |
| `batchItemId` | null or integer (int64) | No | — |

</details>

<a id="schema-updateretentionpolicyrequest"></a>

<details>
<summary>UpdateRetentionPolicyRequest</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `retentionDays` | null or integer (int32) | No | null means keep forever; otherwise integer from 1 through 36500. |
| `expectedRevision` | integer (int64) | Yes | Exact current revision from a preceding read. |

</details>

<a id="schema-updatesharedvariableclientrequest"></a>

<details>
<summary>UpdateSharedVariableClientRequest</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `displayName` | string | Yes | — |
| `scopes` | array of string | Yes | Replacement nonempty list of exact-case shared-variables.read and/or shared-variables.write. |
| `expiresAt` | null or string (date-time) | No | Null or a future ISO 8601 instant. |
| `expectedRevision` | integer (int64) | Yes | Exact current revision from a preceding read. |

</details>

<a id="schema-updatesharedvariabledescriptionrequest"></a>

<details>
<summary>UpdateSharedVariableDescriptionRequest</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `description` | null or string | No | — |
| `expectedRevision` | integer (int64) | Yes | Exact current revision from a preceding read. |
| `requestId` | null or string | No | Optional mutation retry identifier; reuse only for the identical logical request. |
| `reason` | null or string | No | Human-readable audit reason; required where non-null in version-change/reactivation requests. |

</details>

<a id="schema-updatesharedvariablevaluerequest"></a>

<details>
<summary>UpdateSharedVariableValueRequest</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `value` | [JsonElement](#schema-jsonelement) | Yes | — |
| `expectedRevision` | integer (int64) | Yes | Exact current revision from a preceding read. |
| `requestId` | null or string | No | Optional mutation retry identifier; reuse only for the identical logical request. |
| `reason` | null or string | No | Human-readable audit reason; required where non-null in version-change/reactivation requests. |

</details>

<a id="schema-updateworkflowdelegationpolicyrequest"></a>

<details>
<summary>UpdateWorkflowDelegationPolicyRequest</summary>

Creates or updates a workflow-family delegation policy.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `requiresAcceptance` | boolean | Yes | — |
| `expectedUpdatedAt` | null or string (date-time) | No | Null/omitted for a missing policy; otherwise the exact current policy timestamp is required. |

</details>

<a id="schema-updateworkflowrequest"></a>

<details>
<summary>UpdateWorkflowRequest</summary>

Request payload for updating an existing workflow definition to a new version.

Creates a new immutable version in the source family; publish defaults false.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `definition` | [WorkflowModel](#schema-workflowmodel) | Yes | The structural JSON model of the workflow to update. |
| `publish` | boolean | No | Whether to publish this new version immediately. Default: `false`. |

</details>

<a id="schema-updateworkflowsettingrequest"></a>

<details>
<summary>UpdateWorkflowSettingRequest</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `value` | [JsonElement](#schema-jsonelement) | Yes | — |
| `description` | null or string | No | — |
| `expectedUpdatedAt` | string (date-time) | Yes | Exact server-returned timestamp for optimistic concurrency. |

</details>

<a id="schema-userdelegationdto"></a>

<details>
<summary>UserDelegationDto</summary>

A retained standing delegation grant for one stable workflow family.

isActive additionally considers revocation, validity time and acceptance; state filters select acceptanceState rather than isActive.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | — |
| `delegator` | string | Yes | — |
| `delegate` | string | Yes | — |
| `workflowKey` | string | Yes | Stable authored workflow-family key. |
| `validFrom` | string (date-time) | Yes | — |
| `validUntil` | string (date-time) | Yes | — |
| `requiresAcceptance` | boolean | Yes | — |
| `acceptanceState` | string | Yes | notRequired, pending, accepted, or rejected. |
| `createdBy` | string | Yes | — |
| `creationReason` | null or string | Yes | — |
| `createdAt` | string (date-time) | Yes | Server creation timestamp. |
| `decisionBy` | null or string | Yes | — |
| `decisionAt` | null or string (date-time) | Yes | — |
| `decisionReason` | null or string | Yes | — |
| `revokedBy` | null or string | Yes | — |
| `revokedAt` | null or string (date-time) | Yes | — |
| `revocationReason` | null or string | Yes | — |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |
| `isActive` | boolean | Yes | — |

</details>

<a id="schema-userdelegationlifecyclerequest"></a>

<details>
<summary>UserDelegationLifecycleRequest</summary>

Optimistic lifecycle command used to accept, reject, withdraw, or revoke a grant.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `expectedUpdatedAt` | string (date-time) | Yes | Exact server-returned timestamp for optimistic concurrency. |
| `reason` | null or string | No | Human-readable audit reason; required where non-null in version-change/reactivation requests. |

</details>

<a id="schema-usertaskactionackdto"></a>

<details>
<summary>UserTaskActionAckDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `userTaskId` | integer (int64) | Yes | Exact runtime child/normal work-item database ID. |
| `instanceId` | integer (int64) | Yes | Runtime workflow-instance database ID. |
| `taskStatus` | string | Yes | — |
| `instanceStatus` | string | Yes | — |
| `selectedFlowId` | integer (int32) | Yes | — |
| `currentNodeId` | integer (int32) | Yes | — |
| `currentNodeName` | string | Yes | — |
| `currentNodeExternalId` | null or string | Yes | — |
| `multiInstance` | null or [MultiInstanceProgressDto](#schema-multiinstanceprogressdto) | Yes | — |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |
| `fault` | null or [FaultInfoDto](#schema-faultinfodto) | No | — |
| `executionPositions` | array of [ExecutionPositionDto](#schema-executionpositiondto) | No | All exposed token positions; the representative currentNode fields do not imply a single active token. |
| `completion` | null or [CompletionInfoDto](#schema-completioninfodto) | No | — |
| `delegatedAccess` | null or [DelegatedTaskAccessDto](#schema-delegatedtaskaccessdto) | No | — |

</details>

<a id="schema-usertaskassignmentackdto"></a>

<details>
<summary>UserTaskAssignmentAckDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `userTaskId` | integer (int64) | Yes | Exact runtime child/normal work-item database ID. |
| `instanceId` | integer (int64) | Yes | Runtime workflow-instance database ID. |
| `operation` | string | Yes | assigned, reassigned, unassigned, or unchanged. |
| `previousOwnership` | string | Yes | — |
| `previousOwner` | null or string | Yes | — |
| `currentOwnership` | string | Yes | — |
| `currentOwner` | null or string | Yes | — |
| `requiresClaim` | boolean | Yes | — |
| `requiresAssignment` | boolean | Yes | — |
| `changed` | boolean | Yes | — |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |

</details>

<a id="schema-usertaskcapabilitiesdto"></a>

<details>
<summary>UserTaskCapabilitiesDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `claimedByMe` | boolean | Yes | — |
| `canClaim` | boolean | Yes | — |
| `canUnclaim` | boolean | Yes | — |
| `canAct` | boolean | Yes | — |

</details>

<a id="schema-usertaskdto"></a>

<details>
<summary>UserTaskDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | — |
| `instanceId` | integer (int64) | Yes | Runtime workflow-instance database ID. |
| `tokenId` | integer (int64) | Yes | Runtime execution-token database ID. |
| `nodeId` | integer (int32) | Yes | — |
| `nodeName` | string | Yes | — |
| `nodeExternalId` | null or string | Yes | — |
| `roles` | array of string | Yes | Role strings; this schema determines whether these represent actor claims or a configured permission list. |
| `requiresClaim` | boolean | Yes | — |
| `requiresAssignment` | boolean | Yes | — |
| `status` | string | Yes | pending, active, completed, or cancelled. |
| `claimedBy` | null or string | Yes | — |
| `assignee` | null or string | Yes | — |
| `itemIndex` | null or integer (int32) | Yes | — |
| `itemValue` | null or [JsonElement](#schema-jsonelement) | Yes | — |
| `selectedFlowId` | null or integer (int32) | Yes | — |
| `completedBy` | null or string | Yes | — |
| `result` | null or object | Yes | — |
| `capabilities` | [UserTaskCapabilitiesDto](#schema-usertaskcapabilitiesdto) | Yes | — |
| `multiInstance` | null or [MultiInstanceProgressDto](#schema-multiinstanceprogressdto) | Yes | — |
| `createdAt` | string (date-time) | Yes | Server creation timestamp. |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |
| `completedAt` | null or string (date-time) | Yes | Nullable until completed; ISO 8601 timestamp. |
| `delegatedAccess` | null or [DelegatedTaskAccessDto](#schema-delegatedtaskaccessdto) | No | — |
| `completedDelegatedAccess` | null or [DelegatedTaskAccessDto](#schema-delegatedtaskaccessdto) | No | — |
| `completionKind` | null or string | No | — |
| `completionReason` | null or string | No | — |
| `administrativeActionBatchId` | null or integer (int64) | No | — |
| `attributes` | array of [WorkflowAttributeModel](#schema-workflowattributemodel) | No | Authored metadata key/value entries; no workflow authorization semantics are implied. |

</details>

<a id="schema-usertaskflowrolesdto"></a>

<details>
<summary>UserTaskFlowRolesDto</summary>

A selectable action's effective roles for one waiting activation.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `flowId` | integer (int32) | Yes | — |
| `name` | string | Yes | — |
| `roles` | array of string | Yes | Captured action allowlist; an explicitly empty list is unrestricted for this role check. |

</details>

<a id="schema-usertaskrolepolicydto"></a>

<details>
<summary>UserTaskRolePolicyDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `instanceId` | integer (int64) | Yes | Runtime workflow-instance database ID. |
| `tokenId` | integer (int64) | Yes | Runtime execution-token database ID. |
| `nodeId` | integer (int32) | Yes | — |
| `nodeName` | string | Yes | — |
| `userTaskId` | null or integer (int64) | Yes | Exact runtime child/normal work-item database ID. |
| `multiInstanceExecutionId` | null or integer (int64) | Yes | — |
| `rolePolicyId` | integer (int64) | Yes | Immutable captured policy ID, used as expectedRolePolicyId when editing. |
| `roles` | array of string | Yes | Captured task allowlist; an explicitly empty list is unrestricted for this role check. |
| `flows` | array of [UserTaskFlowRolesDto](#schema-usertaskflowrolesdto) | Yes | Every editable selectable non-default action for role replacement. |
| `activeTaskCount` | integer (int32) | Yes | — |
| `pendingTaskCount` | integer (int32) | Yes | — |

</details>

<a id="schema-usertaskroleschangeackdto"></a>

<details>
<summary>UserTaskRolesChangeAckDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `changed` | boolean | Yes | — |
| `policy` | [UserTaskRolePolicyDto](#schema-usertaskrolepolicydto) | Yes | — |

</details>

<a id="schema-usertaskworksummarydto"></a>

<details>
<summary>UserTaskWorkSummaryDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `isMultiInstance` | boolean | Yes | — |
| `activeCount` | integer (int32) | Yes | — |
| `pendingCount` | integer (int32) | Yes | — |
| `claimedCount` | integer (int32) | Yes | — |
| `assignedCount` | integer (int32) | Yes | — |
| `soleClaimedBy` | null or string | Yes | — |
| `soleAssignee` | null or string | Yes | — |
| `normalTaskCount` | integer (int32) | No | — |
| `multiInstanceTaskCount` | integer (int32) | No | — |
| `soleUserTaskId` | null or integer (int64) | No | — |

</details>

<a id="schema-variablemodel"></a>

<details>
<summary>VariableModel</summary>

Represents a variable declaration model within a scope.

Input declarations use required; process declarations use nullable. dataType json is supported in addition to scalar types. Top-level shared aliases use scope shared, sharedKey and access read/readWrite. Node/flow input declarations cannot opt into shared scope/nullability.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int32) | No | The unique integer ID of the variable declaration. |
| `name` | string | No | The declared variable name. |
| `scope` | null or string | No | Optional storage scope for a top-level process variable. A missing value retains the historical instance-scoped behavior; `shared` binds the local variable name to a deployment-wide shared-variable catalog entry. Node and sequence-flow input declarations cannot set this property. |
| `sharedKey` | null or string | No | Deployment-wide shared-variable key for scope shared; name remains the local workflow alias. |
| `access` | null or string | No | Requested shared-variable access (`read` or `readWrite`). Present only for shared top-level declarations. |
| `dataType` | string | No | string, number, boolean, date, datetime, or json. Use isArray for arrays. |
| `isArray` | boolean | No | If true, the variable holds an array of values instead of a single scalar. Default: `false`. |
| `nullable` | boolean | No | If true, a process-level variable may hold JSON null. This contract is supported only for top-level workflow variables; start/flow variables remain input contracts and cannot opt into nullability. Default: `false`. |
| `required` | boolean | No | If true, the variable must be supplied before advancing the transition. Default: `false`. |
| `defaultValue` | null or [JsonElement](#schema-jsonelement) | No | — |
| `validation` | null or string | No | An optional NCalc validation expression. |

</details>

<a id="schema-workflowattributemodel"></a>

<details>
<summary>WorkflowAttributeModel</summary>

Represents one ordered, user-authored metadata entry attached to a workflow node or sequence flow.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `key` | string | No | The case-insensitively unique attribute key within its owning element. |
| `value` | string | No | The opaque string value associated with the key. |

</details>

<a id="schema-workflowdelegationpolicydto"></a>

<details>
<summary>WorkflowDelegationPolicyDto</summary>

Workflow-family policy controlling whether new grants require delegate acceptance. A missing persisted policy is represented by the default values and null audit fields.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `workflowKey` | string | Yes | Stable authored workflow-family key. |
| `requiresAcceptance` | boolean | Yes | Default false when no persisted family policy exists; new grants snapshot the current policy. |
| `createdBy` | null or string | Yes | — |
| `createdAt` | null or string (date-time) | Yes | Server creation timestamp. |
| `updatedBy` | null or string | Yes | — |
| `updatedAt` | null or string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |

</details>

<a id="schema-workflowdetaildto"></a>

<details>
<summary>WorkflowDetailDto</summary>

Represents detailed metadata and structural definition of a workflow version.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | The unique database ID of this workflow version. |
| `name` | string | Yes | The human-readable name of the workflow. |
| `workflowKey` | string | Yes | The stable cross-version identifier of the workflow. |
| `version` | integer (int32) | Yes | The version number (starts at 1). |
| `isPublished` | boolean | Yes | Indicates if this version is published and can start new instances. |
| `isDefault` | boolean | Yes | Indicates if this is the default version for its workflow key (used when starting instances by key). |
| `createdAt` | string (date-time) | Yes | The timestamp when this version was created. |
| `definition` | [WorkflowModel](#schema-workflowmodel) | Yes | The full structural workflow JSON model representation. |

</details>

<a id="schema-workflowmodel"></a>

<details>
<summary>WorkflowModel</summary>

Represents the complete structure and schema of a workflow definition.

The authored definition format includes editor layout as well as execution contracts. Graph/conditional field requirements are semantic and go beyond optional JSON properties; use BPMN support and the example catalog. Legacy steps/actions exist for migration compatibility, not new integration authoring.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | JSON | Yes | Stable workflow-family key. Author a nonblank string of at most 300 Unicode scalars; legacy numeric IDs are normalized on loading. |
| `name` | string | Yes | The human-readable name of the workflow. |
| `initialEventId` | null or integer (int32) | No | The ID of the initial start event node. |
| `variables` | array of [VariableModel](#schema-variablemodel) | No | The list of process-level variables declared in the workflow. |
| `lanes` | array of [LaneModel](#schema-lanemodel) | No | Designer swimlanes for visual grouping. Lane names do not grant runtime authorization. |
| `flowNodes` | array of [FlowNodeModel](#schema-flownodemodel) | Yes | The list of flow nodes (steps, tasks, gateways, events) in the workflow. |
| `sequenceFlows` | array of [SequenceFlowModel](#schema-sequenceflowmodel) | Yes | The list of sequence flows (directed edges) connecting the flow nodes. |
| `cancelRoles` | array of string | No | The list of roles allowed to cancel instances of this workflow. |
| `unclaimRoles` | array of string | No | The list of roles allowed to unclaim tasks in this workflow. |
| `taskAssignmentRoles` | array of string | No | The roles allowed to assign, reassign, or release active user-task work items. An empty list disables task-assignment management for this workflow version. |
| `taskRoleManagementRoles` | array of string | No | Roles allowed to inspect and replace waiting-task role policies. Empty disables this authority. |
| `taskDistribution` | null or [TaskDistributionModel](#schema-taskdistributionmodel) | No | — |
| `initialStepId` | null or integer (int32) | No | — |
| `phases` | array of [LaneModel](#schema-lanemodel) or null | No | — |
| `steps` | array of [LegacyStepModel](#schema-legacystepmodel) or null | No | — |

</details>

<a id="schema-workflowsettingdto"></a>

<details>
<summary>WorkflowSettingDto</summary>

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | — |
| `namespace` | null or string | Yes | — |
| `name` | string | Yes | — |
| `value` | [JsonElement](#schema-jsonelement) | Yes | — |
| `description` | null or string | Yes | — |
| `createdAt` | string (date-time) | Yes | Server creation timestamp. |
| `updatedAt` | string (date-time) | Yes | Server last-change timestamp; preserve exact precision for optimistic commands. |

</details>

<a id="schema-workflowsummarydto"></a>

<details>
<summary>WorkflowSummaryDto</summary>

Represents a summary of a workflow definition version.

| Field | JSON type / schema | Required | Meaning / default |
| --- | --- | --- | --- |
| `id` | integer (int64) | Yes | The unique database ID of this workflow version. |
| `name` | string | Yes | The human-readable name of the workflow. |
| `workflowKey` | string | Yes | The stable cross-version identifier of the workflow. |
| `version` | integer (int32) | Yes | The version number (starts at 1). |
| `isPublished` | boolean | Yes | Indicates if this version is published and can start new instances. |
| `isDefault` | boolean | Yes | Indicates if this is the default version for its workflow key (used when starting instances by key). |
| `createdAt` | string (date-time) | Yes | The timestamp when this version was created. |

</details>


## Worker operational endpoints

These endpoints belong to **Flowbit.Worker**, on its configured operational listener, not the API's origin. They have no built-in bearer authorization. Configure network exposure as described in [Deployment](deployment.md).

| Method and path | Purpose and response | Parameters/body | Failure behavior |
| --- | --- | --- | --- |
| `GET /health/live` | Process liveness; `200 text/plain` with `Healthy` when live. | None. | Health-check failures return `503` with health status text. |
| `GET /health/ready` | Reports whether the Worker has completed its first successful durable-queue query. | None. | Before that first success it returns `503`. The readiness flag stays set: it is not a continuous database connectivity check or proof that the queue is empty. |
| `GET /metrics` | Prometheus exposition; `200 text/plain; version=0.0.4; charset=utf-8`. | None. | No custom JSON error contract. |

```http
GET /health/live HTTP/1.1
Host: localhost:8081

HTTP/1.1 200 OK
Content-Type: text/plain

Healthy
```

```http
GET /health/ready HTTP/1.1
Host: localhost:8081

HTTP/1.1 200 OK
Content-Type: text/plain

Healthy
```

```http
GET /metrics HTTP/1.1
Host: localhost:8081

HTTP/1.1 200 OK
Content-Type: text/plain; version=0.0.4; charset=utf-8

# TYPE flowbit_worker_ready gauge
flowbit_worker_ready 1
# TYPE flowbit_worker_jobs_active gauge
flowbit_worker_jobs_active 0
```

The metrics response body is Prometheus text generated by the Worker; metric names and labels are defined in [WorkerTelemetry](../Flowbit/src/Flowbit.Worker/WorkerTelemetry.cs). It is not JSON.

## API development endpoints

`GET /` on the API returns `302 Found` with `Location: /swagger`. In Development, `GET /openapi/v1.json` returns the OpenAPI document (`200 application/json`) and `/swagger` serves the Swagger UI. Those development surfaces are not enabled in Production; the root redirect is not a production health check.

```http
GET / HTTP/1.1
Host: localhost:5017

HTTP/1.1 302 Found
Location: /swagger
```

```http
GET /openapi/v1.json HTTP/1.1
Host: localhost:5017

HTTP/1.1 200 OK
Content-Type: application/json
```

The OpenAPI JSON describes operations and component schemas; the endpoint rules in this reference also cover service validation and authorization that OpenAPI metadata alone cannot express.

---

[Documentation home](index.md) · [Developer guide](developer-guide.md) · [BPMN support](bpmn-support.md) · [Deployment](deployment.md)
