# Built-in engine settings reference

[Documentation home](index.md) · [Deployment and operations](deployment.md#configuration) · [Settings API](api-guide.md#settings) · [Settings UI](ui-guide.md#engine-and-workflow-settings)

This is the complete reference for the **17 current built-in settings** read from PostgreSQL's `flowbit.engine_settings` table. These are deployment-wide string records, shared by API and Worker replicas using that database. They are distinct from [host configuration](deployment.md#configuration) in `appsettings.json` or environment variables, JSON [workflow-context settings](developer-guide.md#output-mappings-and-expression-context), and properties authored in workflow definitions.

A **runtime default** is what a consumer uses without a usable setting. A **seed value** is a row inserted by a migration when the setting is missing. Deleting a seeded row does not rerun an already-applied migration; consumers still apply their documented fallback. The final entry, `AiAuthoring.Models`, is reserved and has its own management API.

## Overview

| Setting | Purpose | Runtime default |
| --- | --- | --- |
| [Workflow.RequiredRole](#workflowrequiredrole) | Workflow definitions, AI authoring, version changes, and administrative actions. | `admin` |
| [WorkflowInstances.RequiredRole](#workflowinstancesrequiredrole) | Global instance list/search visibility. | `admin` |
| [NodeExecution.RequiredRole](#nodeexecutionrequiredrole) | Global node-execution activity visibility. | `admin` |
| [WorkflowJobs.RequiredRole](#workflowjobsrequiredrole) | Durable job and incident operations. | `admin` |
| [Settings.RequiredRole](#settingsrequiredrole) | Settings, AI models, and retention administration. | `admin` |
| [Users.RequiredRole](#usersrequiredrole) | Local user and user-attribute administration. | `admin` |
| [SharedVariables.RequiredRole](#sharedvariablesrequiredrole) | JWT access to shared variables and client administration. | `admin` |
| [Delegation.AdminRoles](#delegationadminroles) | Delegation and acceptance-policy administration. | `admin` |
| [Workflow.Gateway.MaxActiveTokens](#workflowgatewaymaxactivetokens) | Active-token bound at gateway splits. | `1000` |
| [Workflow.MultiInstance.MaxInstances](#workflowmultiinstancemaxinstances) | Multi-instance child count and durable-job capacity. | `1000`; job capacity `2000` |
| [Workflow.Async.MaxConsecutiveAutomaticActivations](#workflowasyncmaxconsecutiveautomaticactivations) | Consecutive durable automatic activity activations. | `1000` |
| [WorkflowBatchActions.MaxAffectedTasks](#workflowbatchactionsmaxaffectedtasks) | Tasks affected by one administrative action batch. | `10000` |
| [WorkflowVersionChanges.MaxBatchInstances](#workflowversionchangesmaxbatchinstances) | Instances frozen into one version-change batch. | `10000` |
| [WorkflowVariableUpdates.MaxBatchInstances](#workflowvariableupdatesmaxbatchinstances) | Instances frozen into one variable-update batch. | `10000` |
| [UserTasks.DueSoonThreshold](#usertasksduesoonthreshold) | How long before its due time an open user task counts as due soon. | `P1D` |
| [Authentication.UserIdentityClaim](#authenticationuseridentityclaim) | Authoritative JWT claim identifying the actor. | Unset: identity selection chain |
| [AiAuthoring.Models](#aiauthoringmodels) | Assistant model catalog, including provider keys. | Empty catalog |

## Storage, lookup, and management

### Logical keys and casing

The logical name is the trimmed namespace plus `.` plus the trimmed key. The canonical representation splits at the **last dot**: `Workflow.MultiInstance.MaxInstances` has namespace `Workflow.MultiInstance` and key `MaxInstances`.

Ordinary runtime lookups are **case-sensitive**. Use the exact spelling in this reference; `workflow.RequiredRole` does not override `Workflow.RequiredRole`. Matching role *values* is case-insensitive, which is a separate rule. The reserved AI-model name is protected case-insensitively by generic management, but its ordinary repository lookup still uses the canonical spelling.

Legacy records with a null, empty, or whitespace-only namespace and the entire dotted name in the key remain readable. Other namespace/key splits that form the same dotted logical name are also readable. When historical duplicates exist, lookup prefers the exact canonical namespace/key, then its trimmed equivalent, then other namespaced representations, then a blank-namespace representation; equal-priority rows use the lowest ID. New management writes reject an existing identical logical name rather than create another representation. They do not merge or remove historical duplicates.

### Values and concurrency

Use [Engine settings](ui-guide.md#engine-and-workflow-settings) or the [settings API](api-guide.md#settings), with a role authorized by `Settings.RequiredRole`. Namespace and key are fixed after creation. Each is limited to 300 .NET string characters; a description is optional and limited to 1,000. Identifiers and descriptions are trimmed; a blank namespace/description becomes null.

Engine values are **opaque strings**, including empty, whitespace, and multiline content. Generic CRUD requires a non-null value but does not validate its meaning for the selected built-in setting. A successful save can therefore store a value that a consumer falls back from, or an identity setting that prevents the next startup. There is no built-in-key whitelist: adding a custom record does not give it engine behavior. See the [schema reference](database-schema.md#engine_settings).

`GET /api/engine-settings` lists records and their `updatedAt` timestamps; there is no per-ID GET. Updates and deletes require the exact `expectedUpdatedAt` from the latest response. Duplicate logical names and stale mutations return `409`; invalid request fields return `400`, and an unknown mutation ID returns `404`. Update replaces both value and description; retain the existing description explicitly if it should remain. Delete puts `expectedUpdatedAt` in the query string. Reload after a conflict and use the new timestamp after every successful update.

Deleting a permission row restores `admin`; it does not remove the permission check. Deleting a numeric row restores its runtime default; it does not disable the bound. A legacy duplicate can become effective after the preferred row is deleted. Identity deletion takes effect after an API restart. The reserved AI-model row cannot be deleted through generic settings CRUD.

Implementation: [lookup and persistence](../Flowbit/src/Flowbit.Infrastructure/Repositories/EngineSettingsRepository.cs), [management validation](../Flowbit/src/Flowbit.Service/Services/SettingManagementValidation.cs), [HTTP handlers and permission filter](../Flowbit/src/Flowbit.Api/Endpoints/SettingsEndpoints.cs), and [reserved-row protection](../Flowbit/src/Flowbit.Service/Services/EngineSettingsService.cs).

### HTTP request examples

These are request templates with public placeholders, not credentials or runnable setup commands. Use your API origin and an authorized token. List first; the ordinary numeric setting below is normally already seeded.

```http
GET /api/engine-settings HTTP/1.1
Host: YOUR_API_HOST
Authorization: Bearer YOUR_SETTINGS_ADMIN_ACCESS_TOKEN
```

If the row is absent, create it using its canonical namespace/key:

```http
POST /api/engine-settings HTTP/1.1
Host: YOUR_API_HOST
Authorization: Bearer YOUR_SETTINGS_ADMIN_ACCESS_TOKEN
Content-Type: application/json

{
  "namespace": "Workflow.MultiInstance",
  "key": "MaxInstances",
  "value": "2000",
  "description": "Maximum child items; also controls per-instance durable-job capacity."
}
```

To update an existing row, replace `42` with its actual ID and replace the illustrative timestamp below with its exact `updatedAt` value. `value` is a JSON **string**, even for numeric limits.

```http
PUT /api/engine-settings/42 HTTP/1.1
Host: YOUR_API_HOST
Authorization: Bearer YOUR_SETTINGS_ADMIN_ACCESS_TOKEN
Content-Type: application/json

{
  "value": "2000",
  "description": "Maximum child items; also controls per-instance durable-job capacity.",
  "expectedUpdatedAt": "2026-10-23T10:00:00Z"
}
```

To restore the fallback by deleting the row, substitute its actual ID and URL-encode the **newest** timestamp from the update/list response:

```http
DELETE /api/engine-settings/42?expectedUpdatedAt=2026-10-23T10%3A01%3A00Z HTTP/1.1
Host: YOUR_API_HOST
Authorization: Bearer YOUR_SETTINGS_ADMIN_ACCESS_TOKEN
```

## Permission settings

All eight permission settings below use a comma-separated string, such as `admin, workflow-operators`. Whitespace around entries is trimmed, empty entries are ignored, and **any one** matching role authorizes the relevant operation, ignoring case. Missing, empty, whitespace-only, or separator-only values fall back to `admin`.

A nonempty custom list **replaces** `admin`; include `admin` if it should retain access. There is no special malformed-role fallback or role-name registry: `unknown-role` is a literal role name, and `["admin"]` is not parsed as a JSON array. These settings have no separate consumer-enforced list-size or per-role-length bound. Token and request limits still apply. They do not grant unrelated management permissions or satisfy roles authored on tasks, actions, starts, assignment, or cancellation. Authentication and any required actor/scope checks still apply.

These settings are read at their authorization checks without restarting replicas. A change affects subsequent checks, not completed actions; it does not rewrite JWT roles, task ownership, captured task-role policies, or audit history. Additional timing and feature-specific rules follow.

### Workflow.RequiredRole

- **Namespace / key:** `Workflow` / `RequiredRole`.
- **Value / fallback:** permission-list format above; default `admin`; example `admin, workflow-designers`.
- **Seed:** `admin` in `SeedDefaultSettings`; existing overrides are preserved.
- **Takes effect:** subsequent workflow-management and administrative authorization checks, including checks performed during durable administrative work using stored actor roles. No restart.

Controls workflow-definition management under `/api/workflows`, including AI authoring, and workflow-administrator operations such as instance reactivation, version changes, administrative task/instance actions, variable updates, and their batch surfaces. Administrative execution also requires a nonblank actor. Every authorized workflow author can use the configured AI models; this setting does not grant permission to configure those models.

Changing it can make queued administrative work ineligible at its next check. Completed items remain committed. It does not grant global instance-list access, node-activity access, job operations, or ordinary task authority.

See [administrator contracts](api-guide.md#instance-administrative-actions) and [identity and permissions](developer-guide.md#authenticate-the-actual-actor). Implementation: [shared policy](../Flowbit/src/Flowbit.Service/Services/WorkflowAdministratorPolicy.cs), [HTTP authorization](../Flowbit/src/Flowbit.Api/Auth/WorkflowAdministratorAuthorization.cs), and [definition authorization](../Flowbit/src/Flowbit.Api/Endpoints/WorkflowDefinitionEndpoints.cs).

### WorkflowInstances.RequiredRole

- **Namespace / key:** `WorkflowInstances` / `RequiredRole`.
- **Value / fallback:** permission-list format; default `admin`; example `admin, instance-auditors`.
- **Seed:** `admin` in `SeedDefaultSettings`.
- **Takes effect:** the next instance list/search authorization resolution; no restart or change to existing instance data.

Grants global visibility across workflow versions in instance list/search. Callers without a matching global role retain the visibility granted by the definition's `taskAssignmentRoles`. Visibility is applied in the database before count, ordering, and paging. This is a read policy, not assignment, mutation, inbox, claim, or cancellation authority, and is not a general gate on instance-detail GET.

See [instance queries](api-guide.md#instances-and-messages) and [runtime sorting and visibility](../Flowbit/README.md#instance-and-inbox-sorting). Implementation: [instance query service](../Flowbit/src/Flowbit.Service/Services/WorkflowInstanceQueryService.cs).

### NodeExecution.RequiredRole

- **Namespace / key:** `NodeExecution` / `RequiredRole`.
- **Value / fallback:** permission-list format; default `admin`; example `admin, execution-auditors`.
- **Seed:** `admin` in `SeedDefaultSettings`.
- **Takes effect:** the next node-execution list/search/detail authorization resolution; no restart or rewriting of execution records.

Grants global visibility of node execution activity. A caller may also see an immutable workflow version through its `taskAssignmentRoles`. Visibility precedes SQL count/order/page; an out-of-scope detail ID returns `404`, while a caller with no visible versions receives an empty collection. This grants no claim, task action, assignment, cancellation, or workflow mutation authority.

See [node execution activity](api-guide.md#node-execution-activity). Implementation: [node execution query service](../Flowbit/src/Flowbit.Service/Services/NodeExecutionQueryService.cs).

### WorkflowJobs.RequiredRole

- **Namespace / key:** `WorkflowJobs` / `RequiredRole`.
- **Value / fallback:** permission-list format; default `admin`; example `admin, workflow-operations`.
- **Seed:** `admin` in `SeedDefaultSettings`.
- **Takes effect:** the next job/incident operation's authorization check; no restart. Changing access does not pause the Worker or cancel jobs.

Controls durable-job list, statistics, detail and attempts, incident list/detail, and incident retry operations used by the UI Operations page. It does not control the Worker's internal execution or authorize workflow-definition edits. Retention operations use `Settings.RequiredRole` instead.

See [jobs and incidents](api-guide.md#jobs-and-incidents) and [operations UI](ui-guide.md#monitor-jobs-and-incidents). Implementation: [job operations service](../Flowbit/src/Flowbit.Service/Services/WorkflowJobOperationsService.cs).

### Settings.RequiredRole

- **Namespace / key:** `Settings` / `RequiredRole`.
- **Value / fallback:** permission-list format; default `admin`; example `admin, settings-administrators`.
- **Seed:** `admin` in `AddSettingDescriptionsAndManagementRole`, not `SeedDefaultSettings`.
- **Takes effect:** the next settings-authorized request; no restart. This can remove the editing identity's access immediately after a successful update.

Controls engine-setting and workflow-setting CRUD, AI-model administration, and retention policy, preview, and cleanup-run operations. AI-model administrators choose provider destinations and credentials. This setting does not grant local-user administration, shared-variable administration, or ordinary workflow permissions. Provision an identity with a replacement role before removing your own role from this list.

See [settings](api-guide.md#settings), [AI models](api-guide.md#ai-models), and [retention](api-guide.md#retention). Implementation: [settings authorization filter](../Flowbit/src/Flowbit.Api/Endpoints/SettingsEndpoints.cs), reused by [AI-model endpoints](../Flowbit/src/Flowbit.Api/Endpoints/AiModelEndpoints.cs) and [retention endpoints](../Flowbit/src/Flowbit.Api/Endpoints/RetentionEndpoints.cs).

### Users.RequiredRole

- **Namespace / key:** `Users` / `RequiredRole`.
- **Value / fallback:** permission-list format; default `admin`; example `admin, user-administrators`.
- **Seed:** `admin` in `AddLocalIdentity`. Its seed existence check ignores logical-name casing; runtime lookup still requires the canonical spelling.
- **Takes effect:** subsequent local-directory authorization and last-administrator checks; no restart. It does not update existing tokens or automatically grant users the newly configured roles.

Controls local user and user-attribute administration: accounts, profiles, roles/claims, activation, password resets, and typed attribute definitions. These routes are available only in Local authentication mode; authorized external signed administrator tokens can also manage the directory. It does not control login, refresh, logout, or workflow authorization.

Directory mutations protect the last active user matching the current management roles. Changing this engine setting is a separate operation: provision replacement-role access before changing it. On first-user bootstrap, the new administrator receives the configured management roles plus `admin`; changing the setting later does not rerun bootstrap.

See [local user administration](local-authentication.md#manage-users-and-fields). Implementation: [local identity service](../Flowbit/src/Flowbit.Service/Services/LocalIdentityService.cs) and [endpoint authorization](../Flowbit/src/Flowbit.Api/Endpoints/LocalIdentityEndpoints.cs).

### SharedVariables.RequiredRole

- **Namespace / key:** `SharedVariables` / `RequiredRole`.
- **Value / fallback:** permission-list format; default `admin`; example `admin, shared-data-administrators`.
- **Seed:** `admin` in `AddSharedVariables`.
- **Takes effect:** subsequent JWT shared-variable authorization checks; no restart or change to stored values, definition bindings, or client scopes.

Controls JWT-user access to shared-variable catalog and value/history operations, including reads, lifecycle operations, and shared-variable API-client administration. It also gates shared-variable distribution search and selecting shared catalog context for AI authoring through their shared read authorization. Runtime workflow bindings retain their own authored access contracts.

Managed machine clients use their persisted `shared-variables.read` / `shared-variables.write` scopes on client-enabled routes instead of this role list; they cannot acquire administrator-only access through it.

See [shared variables](api-guide.md#shared-variables) and [shared-variable clients](api-guide.md#shared-variable-clients). Implementation: [authorization policies and handler](../Flowbit/src/Flowbit.Api/Auth/SharedVariableAuthorization.cs).

### Delegation.AdminRoles

- **Namespace / key:** `Delegation` / `AdminRoles`.
- **Value / fallback:** permission-list format; default `admin`; example `admin, delegation-administrators`.
- **Seed:** `admin` in `SeedDefaultSettings`.
- **Takes effect:** the next delegation-administration authorization check; no restart or automatic revocation of existing grants.

Controls administrative listing, creation, and revocation of other users' delegations and management of workflow delegation-acceptance policies. A nonblank actor is required. Personal grant/accept/reject/revoke operations use their own participant rules; this setting neither makes an actor a delegate nor bypasses task authorization or workflow delegation restrictions. Acceptance-policy changes affect new grants.

See [delegation contracts](api-guide.md#delegation). Implementation: [delegation service](../Flowbit/src/Flowbit.Service/Services/UserDelegationService.cs) and [setting constant](../Flowbit/src/Flowbit.Service/Models/UserDelegationModels.cs).

## Execution limits

These settings accept positive decimal integers fitting a signed 32-bit integer (`1` through `2147483647`), stored as strings. Surrounding whitespace and a leading `+` are accepted; decimals, thousands separators, exponents, nonnumbers, overflow, zero, and negative values fall back to `1000`. There is no disable/unlimited sentinel. Generic settings CRUD does not validate these rules.

### Workflow.Gateway.MaxActiveTokens

- **Namespace / key:** `Workflow.Gateway` / `MaxActiveTokens`.
- **Value / fallback:** positive-integer format above; default `1000`; example `2000`.
- **Seed:** `1000` in `SeedDefaultSettings`.
- **Takes effect:** each gateway split's capacity check; no restart. Existing tokens are not removed when the limit is lowered.

Bounds active execution tokens in an instance when a gateway splits. The check uses the current active-token count plus the selected outgoing-flow count minus the replaced incoming token. A split exceeding the limit throws a domain error before creating that split; normal transaction/error handling applies. This is not a total-history count, a per-lane limit, or a Worker-concurrency setting.

See [gateway semantics](../Flowbit/README.md#gateways-and-scoped-interruption) and [node reference](node-reference.md). Implementation: [gateway split check](../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.cs).

### Workflow.MultiInstance.MaxInstances

- **Namespace / key:** `Workflow.MultiInstance` / `MaxInstances`.
- **Value / fallback:** positive-integer format above; default `1000`; example `2000` permits up to 2,000 child items and sets open-job capacity to 4,000.
- **Seed:** `1000` in `SeedDefaultSettings`.
- **Takes effect:** initialization of a new multi-instance execution and capacity checks when instance-owned durable jobs are enqueued. No restart; existing multi-instance children are not resized or cancelled.

Bounds the collection size or evaluated cardinality of a multi-instance user task, including sequential and parallel modes. Zero items, a nonintegral cardinality, or a count above the current limit is rejected. Once the parent execution exists, its child set is retained even if this setting changes.

The same value also sets the **per-instance open durable-job bound to twice the limit**, computed with 64-bit arithmetic (default `2000`). Capacity checking covers individual enqueues and complete conditional-wake waves under the instance lock, credits the job currently completing when applicable, and rejects additional work that would exceed the bound. Lowering it can prevent new jobs for an existing instance until capacity is available. It is separate from Worker execution slots and does not delete, cancel, or throttle already-enqueued jobs by itself.

See [multi-instance authoring](node-reference.md#multi-instance-properties) and [durable work](../Flowbit/README.md#durable-async-work-and-timers). Implementation: [multi-instance initialization](../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.cs), [enqueue checks](../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.Jobs.cs), and [capacity calculation](../Flowbit/src/Flowbit.Service/Services/WorkflowJobCapacity.cs).

### Workflow.Async.MaxConsecutiveAutomaticActivations

- **Namespace / key:** `Workflow.Async` / `MaxConsecutiveAutomaticActivations`.
- **Value / fallback:** positive-integer format above, parsed with invariant culture; default `1000`; example `2000`.
- **Seed:** `1000` in `SeedDefaultSettings`.
- **Takes effect:** when scheduling each new guarded durable automatic activity activation; no restart, reset of existing counters, or automatic resolution of open incidents.

Bounds consecutive durable automatic `task`, `serviceTask`, and `scriptTask` activations along an execution-token lineage. Async phases and retries of the same activation do not each consume another activation. Forks inherit the parent's count; a join continues with the greatest contributing count rather than summing parallel work.

With the default, the first 1,000 activations are allowed; attempting the 1,001st persists an `automatic_loop_limit` incident before that activity body runs. The blocked activation retains a stable job identity. User-task actions, multi-instance completion, message delivery, and timer triggers reset the allowance; conditional-event handling also resets counters at its external-trigger continuation. An operator with `WorkflowJobs.RequiredRole` can retry the incident: the blocked activation is queued as activation one of a new allowance. Increasing the setting alone does not retry an incident.

This is a durable-lineage guard, not a replacement for the engine's synchronous pass-through protection or a bound on total parallel work.

See [durable execution](../Flowbit/README.md#durable-async-work-and-timers) and [incident operations](api-guide.md#jobs-and-incidents). Implementation: [guard and counter rules](../Flowbit/src/Flowbit.Service/Services/WorkflowAutomaticActivationGuard.cs), [scheduling](../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.Jobs.cs), and [incident retry](../Flowbit/src/Flowbit.Service/Services/WorkflowJobOperationsService.cs).

## Batch limits

All three settings accept positive signed 32-bit integers as strings, with whitespace/leading `+` allowed. The **effective range is 1–10,000**. A parseable value above 10,000 is capped at 10,000; missing, blank, malformed, overflowing, zero, or negative values use the default 10,000. Thus `"20000"` is capped, while `"2147483648"` fails parsing and uses the default. No value disables the bound. Existing request-size, variable-count, expanded-payload, authorization, and eligibility rules still apply independently.

### WorkflowBatchActions.MaxAffectedTasks

- **Namespace / key:** `WorkflowBatchActions` / `MaxAffectedTasks`.
- **Value / fallback:** batch-integer format above; default/hard maximum `10000`; example `500`.
- **Seed:** `RepairPositionFirstAdministrativeActionBatches` creates the current key, preserving an existing current-key row or migrating a valid legacy `MaxItems` value; see [obsolete settings](#obsolete-settings).
- **Takes effect:** batch creation and the Worker's preparation-finalization capacity check. No restart; completed task changes remain committed.

Bounds the total ordinary or multi-instance user tasks affected by one administrative action batch, not just the number of parent instances or batch items. Creation bounds the frozen selection; preparation calculates the expanded affected-task total. If preparation exceeds the current limit, the batch fails with `affected_task_limit_exceeded`. Lowering the setting during preparation can therefore fail a batch accepted earlier; it does not retroactively trim a prepared selection or undo completed work. It is not a per-instance direct-action limit.

See [administrative action batches](api-guide.md#administrative-action-batches). Implementation: [batch creation](../Flowbit/src/Flowbit.Service/Services/AdministrativeActionBatchService.cs), [Worker preparation](../Flowbit/src/Flowbit.Service/Services/AdministrativeActionBatchJobProcessor.cs), and [hard cap](../Flowbit/src/Flowbit.Service/Models/AdministrativeActionBatchModels.cs).

### WorkflowVersionChanges.MaxBatchInstances

- **Namespace / key:** `WorkflowVersionChanges` / `MaxBatchInstances`.
- **Value / fallback:** batch-integer format; default/hard maximum `10000`; example `500`.
- **Seed:** `10000` in `AddInstanceVersionChangeBatches` when missing.
- **Takes effect:** new version-change batch creation; no restart or resizing of already-frozen batches.

Bounds the running instances selected into one version-change batch, including explicit selections and all-matching frozen selections. An over-limit selection is rejected. Preparation/execution continues against the accepted frozen batch with its usual eligibility, concurrency, and authorization checks; changing the limit does not change its selected IDs or migrate instances by itself.

See [version-change batches](api-guide.md#version-change-batches). Implementation: [batch service](../Flowbit/src/Flowbit.Service/Services/InstanceVersionChangeBatchService.cs) and [hard cap](../Flowbit/src/Flowbit.Service/Models/InstanceVersionChangeBatchModels.cs).

### WorkflowVariableUpdates.MaxBatchInstances

- **Namespace / key:** `WorkflowVariableUpdates` / `MaxBatchInstances`.
- **Value / fallback:** batch-integer format; default/hard maximum `10000`; example `500`.
- **Seed:** `10000` in `AddInstanceVariableUpdates`, not `SeedDefaultSettings`.
- **Takes effect:** new administrative variable-update batch creation; no restart or resizing of existing frozen batches.

Bounds running instances frozen into one administrative variable-update batch. Both explicit selection and the resulting frozen selection are checked. Separate fixed bounds still limit each operation to 100 variables, expansion to 100,000 writes, and expanded payload to 100 MiB; increasing this setting cannot raise those limits. It does not govern a single-instance variable update or rewrite existing values on its own.

See [administrative variable updates](api-guide.md#variable-update-batches) and [runtime update rules](../Flowbit/README.md#administrative-instance-variable-updates). Implementation: [batch service](../Flowbit/src/Flowbit.Service/Services/InstanceVariableUpdateBatchService.cs) and [limits](../Flowbit/src/Flowbit.Service/Models/InstanceVariableUpdateModels.cs).

## Task settings

### UserTasks.DueSoonThreshold

- **Namespace / key:** `UserTasks` / `DueSoonThreshold`.
- **Value / fallback:** a positive fixed ISO-8601 duration with the grammar of a timer's `timeDuration`, such as `PT8H`, `P1D`, or `P1W`; surrounding whitespace is ignored. Missing, blank, malformed, zero, and calendar values such as `P1M` fall back to `P1D`.
- **Seed:** `P1D` in `AddUserTaskDueDateAndPriority` when missing.
- **Takes effect:** the next response or inbox filter that needs it; no restart. It never changes a task's saved due date.

An active or pending work item with a due time has the `dueStatus` `overdue` once that time is reached, `dueSoon` when it is due within this window, and `upcoming` otherwise. Inbox, task, task-management, and task-distribution responses compute it from the server time when they are built, and the inbox `dueStatus` filter uses the same window, so Flowbit.Ui and other clients show the same badges. The setting is read once per request, and only when a returned task has a due time or a `dueSoon` or `upcoming` filter needs it.

See [due date and priority](node-reference.md#due-date-and-priority) and [inbox filters](api-guide.md#get-apiinstancesinbox). Implementation: [setting rules](../Flowbit/src/Flowbit.Service/Models/UserTaskDueModels.cs) and [inbox and task responses](../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.cs).

## Identity and AI settings

### Authentication.UserIdentityClaim

- **Namespace / key:** `Authentication` / `UserIdentityClaim`.
- **Value:** a nonblank JWT claim-type string, trimmed at API startup; example `Username` or `sub`. It must fit the normal engine-value storage/request constraints; there is no additional configured claim-type length or registry check here.
- **Default / seed:** no row is seeded. An **absent** row enables the identity selection rules below. A present empty or whitespace-only value **fails API startup** instead of enabling the fallback.
- **Takes effect:** once per API process at startup. Restart all API replicas together after creating, editing, or deleting this setting. It does not change existing actor strings, assignments, claims, delegation, or history.

A configured claim is authoritative and strict. Without the row, a non-default OIDC `Authentication:Oidc:NameClaimType` is the next strict override; otherwise identity selection uses `Username`, then `ClaimTypes.Name` (`http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name`), then `sub` or mapped `ClaimTypes.NameIdentifier`.

Only **absent** claims fall through in the default chain. A selected claim that is blank, non-string, ambiguous, or longer than 300 .NET string characters after trimming fails authentication; repeated case-equivalent values identify the same actor. Configuring `Username` explicitly requires it and disables fallback to `sub`. Bare `name`, `preferred_username`, and `flowbit_user` are not automatic fallbacks. Inspect `GET /api/auth/context` to confirm the effective configuration and actor.

In Local mode, the API refuses a configured identity claim equal (ignoring case) to `iss`, `aud`, `exp`, `nbf`, `iat`, `jti`, `sid`, `given_name`, `family_name`, or the configured local role-claim type. Local tokens emit `Username` and additionally emit the username under a configured custom identity claim. This setting does not select an authentication mode, signing key, audience, role-claim type, or expression-claim allowlist.

Preserve the same actor strings when switching providers or claim types. There is no automatic ownership migration. Require fresh sign-in when changing token identity conventions, and coordinate replicas so they agree on actor identity.

See [actor identity](developer-guide.md#authenticate-the-actual-actor), [local identity and trust](local-authentication.md#identity-and-trust), and [authentication context](api-guide.md#authentication-context). Implementation: [startup-latched configuration](../Flowbit/src/Flowbit.Api/Auth/ActorIdentityConfiguration.cs), [startup restrictions](../Flowbit/src/Flowbit.Api/Program.cs), and [actor resolution](../Flowbit/src/Flowbit.Api/Auth/ActorContextResolver.cs).

### AiAuthoring.Models

- **Namespace / key:** `AiAuthoring` / `Models`.
- **Value:** a string containing a JSON array of configured models. Example of an empty catalog: `[]`. Populated catalogs contain provider credentials and must be managed using the dedicated interface, not copied into workflow JSON or documentation examples.
- **Default / seed:** no row is seeded. A missing, blank, or whitespace-only value is an empty catalog. JSON `null`, malformed JSON, or an invalid model entry is not an empty-catalog fallback.
- **Takes effect:** subsequent catalog reads and authoring requests; no API restart. A running request has already resolved its connection; changes are checked again when the conversation continues.

Manage models on [AI models](ui-guide.md#ai-models) or `/api/ai-models`, using `Settings.RequiredRole`. The generic engine-settings list omits this row, and generic create/update/delete refuses it with `400`, including case variants and legacy blank-namespace representations. Provider keys are write-only in the dedicated API and never returned in model DTOs. Treat the stored row and backups as credential-bearing data.

The catalog supports at most **100 models**. The first is the assistant default; there is no built-in provider/model fallback. Each entry stores an ID, display name, endpoint, provider model name, optional reasoning effort, API key, positive revision, and creation/update timestamps. The API creates IDs and starts revisions at one. Constraints are:

| Field | Rules |
| --- | --- |
| Display name | Required, trimmed, at most 100 .NET string characters, no control characters; unique ignoring case. |
| Provider model name | Required, trimmed, at most 200 characters, no control characters. |
| Endpoint | At most 2,000 input characters; absolute HTTPS base URL, or HTTP on a loopback host. No user information, query, fragment, or trailing `/chat/completions` endpoint; normalized with a trailing slash. |
| Reasoning effort | `low`, `medium`, or `high`, ignoring case. Missing, blank, or `none` means do not send an effort. |
| API key | Required on create; trimmed, 16–4,096 characters, no control characters. Blank/omitted on update preserves the key. A keyless local endpoint still needs a unique placeholder of at least 16 characters. |
| Model ID / revision | Stored IDs are unique, nonblank, at most 64 characters, and contain no control characters; revisions are at least one. Update/delete sends the exact `expectedRevision` from the latest list. |

Invalid API fields return `400`, duplicate names or stale revisions return `409`, and unknown model IDs return `404`. A corrupt stored catalog returns `409` from model administration; runtime connection resolution fails closed with `provider_configuration` (`503`). Unknown stored fields are retained on subsequent catalog writes. Deleting the last model leaves an empty catalog.

Changing a model's ID selection, provider model, endpoint, or reasoning effort invalidates an existing conversation's connection binding; changed connection configuration returns `checkpoint_configuration_changed` (`409`) and requires a new conversation. Deleting the selected model makes the next turn return `unsupported_model` (`400`). Replacing only its key preserves that binding. Model display-name changes do not invalidate it. `WorkflowAi:Enabled=false` is a separate host switch that hides authoring models even when this catalog is populated.

See [AI authoring](ai-authoring.md), [catalog operations](api-guide.md#ai-models), and [deployment catalog guidance](deployment.md#ai-model-catalog). Implementation: [catalog rules](../Flowbit/src/Flowbit.Service/Ai/AiModelSettingRules.cs), [storage and revision handling](../Flowbit/src/Flowbit.Service/Ai/AiModelSettingsService.cs), and [generic protection](../Flowbit/src/Flowbit.Service/Services/EngineSettingsService.cs).

## Seeding and upgrades

Migration names below link to their actual seed implementation. Seeds are applied by database migrations, not on every request; Development API startup applies migrations, while other environments need the normal [deployment migration procedure](deployment.md#database-migrations). Existing recognized setting rows are preserved rather than reset to defaults. Seed checks have migration-specific legacy/casing rules; always use canonical spellings for operational writes.

| Migration | Current engine settings seeded |
| --- | --- |
| [SeedDefaultSettings](../Flowbit/src/Flowbit.Infrastructure/Data/Migrations/20260802150520_SeedDefaultSettings.cs) | `Workflow.RequiredRole`, `WorkflowInstances.RequiredRole`, `NodeExecution.RequiredRole`, `WorkflowJobs.RequiredRole`, `Delegation.AdminRoles` = `admin`; gateway, multi-instance, and automatic-activation limits = `1000`. Also seeds the two example **workflow-context** settings, which are not engine settings. |
| [AddSettingDescriptionsAndManagementRole](../Flowbit/src/Flowbit.Infrastructure/Data/Migrations/20260803175244_AddSettingDescriptionsAndManagementRole.cs) | `Settings.RequiredRole` = `admin`; fills missing descriptions for baseline records. |
| [RepairPositionFirstAdministrativeActionBatches](../Flowbit/src/Flowbit.Infrastructure/Data/Migrations/20260808120000_RepairPositionFirstAdministrativeActionBatches.cs) | Current `WorkflowBatchActions.MaxAffectedTasks`, from a valid legacy value or `10000`; removes obsolete batch keys. |
| [AddInstanceVersionChangeBatches](../Flowbit/src/Flowbit.Infrastructure/Data/Migrations/20260808124633_AddInstanceVersionChangeBatches.cs) | `WorkflowVersionChanges.MaxBatchInstances` = `10000`. |
| [AddInstanceVariableUpdates](../Flowbit/src/Flowbit.Infrastructure/Data/Migrations/20260810174726_AddInstanceVariableUpdates.cs) | `WorkflowVariableUpdates.MaxBatchInstances` = `10000`. |
| [AddSharedVariables](../Flowbit/src/Flowbit.Infrastructure/Data/Migrations/20260824174251_AddSharedVariables.cs) | `SharedVariables.RequiredRole` = `admin`. |
| [AddLocalIdentity](../Flowbit/src/Flowbit.Infrastructure/Data/Migrations/20261021153025_AddLocalIdentity.cs) | `Users.RequiredRole` = `admin`. |
| [AddUserTaskDueDateAndPriority](../Flowbit/src/Flowbit.Infrastructure/Data/Migrations/20261027075021_AddUserTaskDueDateAndPriority.cs) | `UserTasks.DueSoonThreshold` = `P1D`. Down removes the row only if it is unchanged. |

`Authentication.UserIdentityClaim` and `AiAuthoring.Models` are optional and unseeded. No provider keys or task-distribution credentials are seeded. Host configuration supplies deployment secrets where the relevant feature requires it; the AI catalog has its dedicated credential management.

## Obsolete settings

These historical keys are **not current built-ins or runtime aliases**. Recreating them through generic CRUD does not restore their old behavior.

| Obsolete logical name | Replacement and migration behavior |
| --- | --- |
| `WorkflowBatchActions.RequiredRole` | Administrative actions now use `Workflow.RequiredRole`. The repair migration removes the old row; it does not copy its role list into `Workflow.RequiredRole`. Review the intended current administrator roles before resuming batch work. |
| `WorkflowBatchActions.MaxItems` | Replaced by `WorkflowBatchActions.MaxAffectedTasks`, measuring expanded affected tasks. If the current key is missing, the repair migration carries forward a legacy digits-only value between 1 and 10,000; otherwise it seeds `10000`. Existing current-key values are preserved. The old row is removed. |

The earlier [AddAdministrativeActionBatches migration](../Flowbit/src/Flowbit.Infrastructure/Data/Migrations/20260804155154_AddAdministrativeActionBatches.cs) seeded the obsolete keys. The [repair migration](../Flowbit/src/Flowbit.Infrastructure/Data/Migrations/20260808120000_RepairPositionFirstAdministrativeActionBatches.cs) owns their replacement. Follow the [upgrade and compatibility rules](deployment.md#upgrade-and-compatibility-rules); changing a setting is not a substitute for applying migrations and upgrading matching API/Worker replicas.

[Documentation home](index.md) · [Settings API](api-guide.md#settings) · [Deployment and operations](deployment.md)
