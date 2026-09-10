# Using Flowbit.Ui

[Documentation home](index.md) · [Local setup](getting-started.md) · [API reference](api-guide.md)

Flowbit.Ui is the browser interface for importing workflow definitions, starting instances, completing human work, and inspecting runtime activity. This guide takes you through an approval using the UI, then explains the management and operations screens in the current checkout.

The standalone [Flowbit editor](../flowbit-editor.html) designs workflows and exports JSON. **Flowbit.Ui operates the runtime** through Flowbit.Api; it does not replace the visual authoring editor.

![Flowbit.Ui operations overview with instance metrics, recent workflows, and human work](images/flowbit-ui.jpg)

*Operations overview with sample data. Your dashboard depends on the current identity, permissions, and database contents.*

## Contents

- [Before you begin](#before-you-begin)
- [Set your development identity](#set-your-development-identity)
- [Complete your first approval](#complete-your-first-approval)
- [Find your way around](#find-your-way-around)
- [Manage workflow versions](#manage-workflow-versions)
- [Use My work and task forms](#use-my-work-and-task-forms)
- [Inspect instances and activity](#inspect-instances-and-activity)
- [Use instance administrative actions](#use-instance-administrative-actions)
- [Manage assignments and roles](#manage-assignments-and-roles)
- [Delegate work](#delegate-work)
- [Monitor jobs and incidents](#monitor-jobs-and-incidents)
- [Use administrative batches](#use-administrative-batches)
- [Manage settings and shared variables](#manage-settings-and-shared-variables)
- [Manage retention](#manage-retention)
- [Troubleshooting](#troubleshooting)

## Before you begin

Follow [Getting started](getting-started.md#prerequisites) to create an isolated PostgreSQL database, start the API, and [start Flowbit.Ui](getting-started.md#obtain-a-development-bearer-token). Both PowerShell and Bash commands are provided there. You can stop before the HTTP walkthrough and continue on this page instead.

With that setup, open [Flowbit.Ui](http://127.0.0.1:15152). The UI's server-side API address is `WorkflowApi:BaseUrl`; its development JWT issuer, audience, and key must match the API configuration. Opening the UI alone does not start the API or database.

The first approval requires **API + database + UI**. Run the [Worker](getting-started.md#when-to-run-the-worker) for timers, async tasks, durable conditional wakes, administrative batches, and retention cleanup. The UI itself does not execute queued jobs.

**Development identity boundary:** the current UI stores one identity for the entire UI process. Another visitor changing Test identity changes the identity attached to that process's API requests. This remains true in Production; production OIDC and per-user UI sessions require implementation. Read [authentication boundaries](deployment.md#authentication-boundaries) before exposing this interface beyond a trusted development environment.

## Set your development identity

1. Open **Test identity** in the sidebar (`/token`).
2. Enter `docs.reviewer` in **User**.
3. Enter `admin, Requester, Reviewer` in **Roles**.
4. Leave custom claims empty for this tutorial. The default expiry is 60 minutes.
5. Click **Generate and apply**. Confirm the **Current identity** shows your username and roles.

The UI now attaches the bearer token to its API requests. You do not need to copy **Raw JWT** or call HTTP endpoints to follow this guide. **Apply pasted token** accepts an existing compatible token; **Clear identity** removes the process's current token. Generate or apply another token when it expires.

Custom claim values are strings. **Add claim** lets you supply a name/value pair, but workflow expressions can access it only if the API also allowlists that name in `WorkflowContext:AllowedClaims`. Follow the [configuration guide](deployment.md#configuration) when an example needs claims or integration settings.

The tutorial combines roles so one development identity can import and publish, start, and review:

| Role | Purpose in this example |
| --- | --- |
| `admin` | Definition administration on a freshly initialized database. The configured `Workflow.RequiredRole` may differ elsewhere. |
| `Requester` | Allows the example's manual start event. |
| `Reviewer` | Allows its review task and approval action. |

`admin` does not automatically grant the workflow's other roles. Sidebar visibility also does not establish permission: each API operation enforces its own checks. The bundled **Workflows** and start screens read definitions through the workflow-administration API, so navigating them also requires definition access in addition to start-event roles.

## Complete your first approval

Use [Roles, Claim, and Bypass](../examples/user-tasks/01-roles-claim-and-bypass.json), already present in your repository. Its [catalog entry](../examples/README.md#user-tasks) explains the alternative supervisor path.

### 1. Import the definition

1. Choose **Workflows** in the sidebar.
2. In **Import editor JSON**, use **Workflow definition file** to select `examples/user-tasks/01-roles-claim-and-bypass.json` from your local checkout.
3. Confirm the displayed file name; expand **Preview JSON** if you want to inspect it.
4. Clear **Publish on import** so you can perform publication separately. It is checked by default.
5. Click **Import definition**.

The file-size limit is **2 MiB**. Select the exported workflow document itself, not the API request envelope containing `definition` and `publish`. A successful import adds the family **User Task Example 01 - Roles, Claim, and Bypass** under **Definitions**. Invalid definitions display an error instead of becoming runnable work.

### 2. Publish the version

Expand the workflow family and click **Publish** on the imported version. Its badge changes to **Published**, and **Start** becomes available. The **Default** badge is separate from publication; a newly created family can have an unpublished default until you publish it.

For later imports, you may leave **Publish on import** checked and use **Import and publish** to combine these steps. Durable definitions additionally require the API's configured publication gate; the gate does not check that a Worker is actually running.

### 3. Start an instance

1. Click **Start** on the published version you want to run.
2. Confirm the workflow name and version at the top of the form.
3. This example selects **Submit Restricted Request**. Enter `REQ-DEMO-001` in **requestReference**.
4. Click **Start instance**.

The UI opens **Instance #…**, initially at **Claimed Review**, with status **Running**. Note the returned instance number so you can find this exact run later. A new click that successfully starts again creates another instance; this example does not configure a transport retry key or business-key uniqueness.

When a workflow has several permitted manual starts, choose **Start event** explicitly. Message and timer starts are triggered by their respective mechanisms and are not ordinary user-start choices. A configured idempotent start also displays **Transport retry key**; keep the same key for the same logical retry and reconcile the returned conflict rather than creating another request.

### 4. Find and claim the task

1. Choose **My work** in the sidebar.
2. Find **Claimed Review** for the instance you just started. Its readiness should be **Can claim**.
3. Click **Open task**.
4. In **Task summary**, click **Claim task**.

**Claimed by** now shows `docs.reviewer`. The **Approve after claim** card appears under **Available actions**. Before claiming, **No actions available** is expected for this tutorial identity.

Use the work item's **Open task** link even when the instance and task numbers happen to match. A workflow instance can have several tasks; the authored node ID, instance ID, and task ID are different identifiers. The UI's links keep them associated correctly.

### 5. Approve and verify completion

1. In **Approve after claim**, enter `Reviewed and approved` in **reviewNote**.
2. Click that card's **Take action** button.
3. The completed workflow returns you to its instance detail. Confirm **Completed** and the **Approved** end position.
4. Open **Variables** and verify `requestReference` and `reviewNote` contain your values.
5. Open **History** and inspect the start and approval transitions, selected flow, and actor. The submitted note is visible in **Variables** with its source flow and actor.

Both example inputs require at least three characters after trimming. The supervisor-only **Escalate without claim** action does not appear for a `Reviewer`; it requires `Supervisor` and its own `escalationNote` input. You have completed the normal approval route entirely through the UI after import.

## Find your way around

Paths below are relative to the UI origin, not API endpoints. Most data screens have a **Refresh**, **Apply filters**, or search button; use it after changing identity or after another actor/Worker changes state.

| Sidebar entry | UI path | Use it for |
| --- | --- | --- |
| **Overview** | `/` | Instance totals, lifecycle distribution, recent instances, My work, and published quick starts available to the current identity. |
| **My work** | `/inbox` | Personal worklist and task actions. |
| **My delegations** | `/delegations` | Grants given by you and offers made to you. |
| **Instances** | `/instances` | Search workflow runs and open instance details. |
| **Activity** | `/activity` | Search committed node visits across workflow versions. |
| **Jobs & incidents** | `/operations` | Durable queue metrics, failures, and retry operations. |
| **Task management** | `/task-management` | Assign waiting work and manage captured task/action roles. |
| **User delegations** | `/delegation-management` | Administer delegation grants and family acceptance policies. |
| **Workflows** | `/workflows` | Import, publish, download, and manage definition versions. |
| **Batch version changes** | `/instance-version-changes` | Prepare compatible version switches for multiple instances. |
| **Instance variables** | `/instance-variable-updates` | Prepare administrative raw-JSON variable updates. |
| **Administrative actions** | `/administrative-actions` | Prepare explicit overrides of waiting task or timer-boundary positions. |
| **Engine settings** | `/engine-settings` | Deployment-wide engine setting records. |
| **Retention policies** | `/retention` | Preview and configure cleanup policies and inspect runs. |
| **Workflow settings** | `/workflow-settings` | JSON settings consumed by workflow expressions/templates. |
| **Shared variables** | `/shared-variables` | Shared catalog contracts, current values, revisions, and lifecycle. |
| **Shared-variable clients** | `/shared-variable-clients` | Machine-client scopes, credentials, rotation, and revocation. |
| **Distribution search** | `/task-distribution` | Development-only search using workflow distribution credentials. |
| **Test identity** | `/token` | Configure the UI process's development identity. |

Overview totals and lists reflect the API's visibility rules; a restricted panel can report an authorization error while another panel works. Treat **My work** as the personal inbox rather than interpreting every displayed instance as actionable by you.

## Manage workflow versions

Expand a family in **Workflows** to see its version rows. Importing another document with the same authored workflow key creates a new immutable version; running instances retain their existing version until an explicit compatible switch.

| Action | Effect |
| --- | --- |
| **Publish** / **Unpublish** | Controls whether a version can start new instances. It does not cancel existing runs. The default version cannot be unpublished; select another published default first. |
| **Set default** | Makes that published version the family default for starts by workflow key and relevant system entry mechanisms. The row's **Start** link still selects that exact version. |
| **Start** | Opens the manual start form for the selected published version. |
| **Download JSON** | Downloads the stored definition. Open it in the standalone editor to author changes, then import the edited JSON as a new version. |
| **Delete** | Requests deletion of that version. Runtime or retained audit references can block it; deletion is not a way to detach existing instances. |

Timer-start schedules follow the published default version. Before changing defaults for those workflows, read [timer semantics](bpmn-support.md#timers) and [upgrade guidance](deployment.md#upgrade-and-compatibility-rules).

## Use My work and task forms

Use **Show filters** to narrow the inbox by instance, workflow, node, or variable data; click **Apply filters**. **Sort results** supports up to three fields and **Apply sorting**. **Previous** and **Next** move through the server's pages. Optional variable enrichment shows latest values, not an audit snapshot.

Readiness labels help distinguish **Ready to act**, **Can claim**, and **View only**. **View only** can be a legitimate result when no action currently satisfies all conditions. On task detail, **Claim task** establishes claim ownership; **Unclaim** releases it when permitted. **View instance** opens the surrounding process.

Choose an action from the cards the server returns. Each card has its own inputs and **Take action** button. After a successful action, an instance still running returns you to the inbox; a terminal instance opens its detail. If another actor, timer, or event moves the task first, refresh before trying again.

### Enter typed values

Start and action inputs show their type, with an asterisk for required values. Use these forms:

| Type | Example input |
| --- | --- |
| `string` | `Reviewed and approved` — no JSON quotes in ordinary task/start text fields. |
| `number` | `1250.50` — use a period as the decimal separator. |
| `boolean` | `true` or `false`. |
| `date` | `2026-09-09`. |
| `datetime` | `2026-09-09T10:30:00Z` or a timestamp with an explicit offset. |
| Typed array | `["alice","bob"]`, `[10,20]`, or `[true,false]`; non-JSON types also accept comma-separated scalar values. Use a JSON string array when a value contains a comma. |
| `json` / `json[]` | Valid JSON, for example `{"approved":true}` or `[{"amount":10}]`. |

Blank task/start fields are omitted from the submission; they do not mean “write null.” The server applies defaults, required-input checks, and authored validation. Administrative **Raw JSON value** fields have different rules: even a string must be quoted there.

### Concurrent and multi-instance work

Parallel branches can create several work items for one instance. Multi-instance user tasks can create many child items beneath one parent execution. Open the exact task from **My work**, check its assignment/item context, and submit that child's action.

An early quorum, an interrupt, or another actor may cancel unfinished work. With one-per-actor voting, several actors can initially see the same representative item; the first completion wins. A successful child action also does not necessarily complete the parent execution. Inspect **Execution positions**, **Multi-instance results**, and any available parent interrupt controls on instance detail. Engine-only aggregate/default flows are not ordinary action buttons. See [multi-instance semantics](bpmn-support.md#human-work-and-multi-instance-tasks).

## Inspect instances and activity

Use **Instances** to filter runs, then open a result. On instance detail:

- **Execution positions** shows the active or terminal tokens and links to their human work. Do not infer the whole process from one node name.
- **Administrative actions** lists active human-task positions and their authored direct actions when the API grants workflow-administrator permission. See [the override procedure](#use-instance-administrative-actions).
- **Variables** shows the latest displayed values and their attribution; **History** shows transition times, node/flow references, actors, and available event details.
- **History** describes claim, release, recovery, delegation, and inherited-claim events. Expand **Actor claims** under an actor to inspect the captured claim names and values, including repeated values. Delegated events also identify the represented owner.
- **Gateway scopes**, **Complex states**, **Multi-instance results**, **Version changes**, **Variable updates**, and **Shared variables** appear when applicable.
- **Change version** is available for authorized running-instance administration. Choose a compatible published target, enter a reason, preview, and inspect blockers/warnings before confirming.
- **Reactivate instance** is a guarded recovery operation for eligible completed/cancelled instances. Preview the target and blockers first. Pruned instance history permanently blocks reactivation.
- **Cancel instance** uses the workflow's cancellation permissions. It cancels remaining runtime work; it cannot undo an already completed external HTTP effect.

Personal task visibility and instance administration are different access scopes. In particular, the current instance-detail API is authenticated but does not apply the personal task-visibility predicate. See the [API authorization boundary](api-guide.md#http-conventions-and-authentication) when designing a production interface.

For a committed visit to a node, use **Activity** (**Node activity**). Combine lifecycle status, **Advanced filters**, and sorting, then open a result. The detail separates timing, actors, task/token correlations, submitted results, committed failures, and **Execution-local variable changes**. Those changes are attributed writes, not a complete execution-time snapshot. Date filters use inclusive **From** and exclusive **To** bounds; variable filters search the owning instance's latest data. Read access comes from workflow `taskAssignmentRoles` or `NodeExecution.RequiredRole` (default `admin`) and grants no task mutation authority.

Under **Actors, roles, and claims**, expand **Starting actor claims** or **Completing actor claims** to inspect the separate snapshots. Claim and unclaim events leave these visit snapshots unchanged. **Not recorded** means a snapshot is unavailable, including older data or disabled capture; **No selected claims present** means capture was enabled but none of the selected names were in that actor's token. These messages also appear in instance history. Claim values are displayed as text. Capture is configured by an operator through [selected-claim audit](deployment.md#selected-claim-audit), and retained snapshots may disappear with their owning history or activity records.

## Use instance administrative actions

On `/instances/{id}`, the **Administrative actions** section uses `Workflow.RequiredRole` (default `admin`). The API grants access from its current setting; changing the development identity or receiving `401`/`403` clears administrative data and controls. A custom setting is a comma-separated, case-insensitive role list and replaces the default. Ordinary **My work**, task actions, claims, assignments, and parent interrupts still require their own authored permissions.

The instance page also refreshes its displayed actor and personal actions when the identity changes.

1. Find the exact task or multi-instance execution, its token, and affected-task count. Parallel positions appear independently. **Refresh tasks**, **Previous**, and **Next** retrieve current positions.
2. Choose the action button, which shows its destination and flow ID. Enter its typed inputs and optional **Reason (optional)**; reasons are limited to 1,000 Unicode characters. Required inputs must be provided.
3. For a multi-instance parent, choose **Multi-instance operation**: **Force parent** cancels unfinished children without creating votes and takes the selected action once; **Complete unfinished children** applies that action and its inputs to every remaining active/pending child, then advances the parent once. Both preserve completed-child history. No operation is preselected.
4. Click **Review action**, inspect the source, destination, affected count, and mode, then **Confirm administrative action**. Duplicate submissions are disabled while execution is in progress. **Edit** returns to the form.
5. After success, the instance's tasks, execution positions, variables, and history refresh. **View audit batch** opens the completed one-item audit. A stale-state conflict refreshes the page state and requires selecting an action again; it is never automatically resubmitted.

The override ignores task/action roles, assignment, claims, inbox visibility, and the selected flow's condition. Declared input validation and downstream routing still apply. It creates new history and may invoke downstream services again; it does not reverse completed effects. Engine-only/default flows are unavailable. Timer-boundary overrides remain on the [administrative batch screen](#use-administrative-batches).

The override executes immediately through the API and database; it creates no administrative Worker jobs. A workflow that reaches an authored asynchronous step still needs the Worker for that continuation. See [instance administrative HTTP contracts](api-guide.md#instance-administrative-actions).

For [10-admin-action.json](../examples/basics/10-admin-action.json), a normal task actor needs `User` at `approval1` and `Manager` at `approval2`; `back`/`cancel` also require the flow's `admin` role. An admin-only actor therefore has no ordinary inbox task at those nodes. With the default administrator setting, the separate section offers **approval/cancel** at `approval1` and **approval/back/cancel** at `approval2`. Each authored `cancel` action reaches the normal end event; it is different from **Cancel instance**.

## Manage assignments and roles

**Task management** is for authorized managers. Click **Show filters** in **Task filters**, set scope, and click **Apply filters**. Management visibility differs from the personal inbox.

For assignment, choose **Assign** or **Reassign**, enter **New actor ID**, optionally add a reason, and click **Save assignment**. **Unassign** releases ownership. The actor ID must match the user's canonical authenticated identity; Flowbit does not confirm that the username exists in your identity provider or has the roles needed to act. Assignment requires the workflow's `taskAssignmentRoles` permission and applies to active work.

**Edit roles** requires the separate `taskRoleManagementRoles` permission. Enter one role per line for **Task roles** and each action, or deliberately select **Allow any role**, then **Save roles**. Editing an MI task replaces the active/pending execution's policy together; completed-item history remains unchanged. Existing claims and assignments are preserved. A stale policy edit reloads current state for review.

Changing a variable that originally supplied task roles does not retroactively change waiting work. Management edits replace the captured policy. Neither assignment management nor role management automatically grants permission to perform the task's actions.

## Delegate work

In **My delegations**, use **Given by me** to enter **Delegate user**, comma-separated **Workflow keys**, **Valid from (UTC)**, **Valid until (UTC)**, and an optional reason, then **Create delegation**. **Offered to me** lets a delegate **Accept** or **Reject** a pending offer when acceptance is required. Use **Revoke** for an eligible outgoing grant.

A delegate acts under their own identity and roles. Delegation allows eligible access to represented work; it does not transfer assignment/claim ownership or erase the actual actor from audit records.

**User delegations** is the administrative view, gated by `Delegation.AdminRoles` (default `admin`). **Create for users** names both users. In **Workflow acceptance policy**, load a workflow key, set **Delegate must accept new grants**, and **Save policy**. The policy affects new grants; an absent policy does not require acceptance. See [delegation contracts](api-guide.md#delegation) for eligibility and time-window rules.

## Monitor jobs and incidents

Open **Jobs & incidents** with `WorkflowJobs.RequiredRole` (default `admin`). Inspect **Runnable jobs**, **Queue lag**, **Active leases**, and **Open incidents**, then filter the job/incident lists. Use **Refresh** to retrieve current state; the page does not continuously poll.

For an open incident, inspect the error and related instance, correct the underlying configuration or business issue, then use **Retry** if appropriate. Retry queues durable work; keep a compatible Worker running and refresh until you see its outcome. **Retry** and **Cancel instance** execute directly from this page, without an additional confirmation dialog. Cancellation still needs the workflow's independent cancellation permission.

Do not retry by taking the original completed user action again. External service calls can have occurred before an incident or connection failure, so use the documented [recovery and at-least-once execution guidance](deployment.md#monitoring-and-recovery).

The Worker's health/metrics endpoints are separate from this UI and the API. An empty operations screen or a published definition is not proof of Worker readiness; see [Worker operational endpoints](deployment.md#operational-endpoints).

## Use administrative batches

These screens prepare and execute persisted batches through the Worker. The normal sequence is: choose scope → search → select items or all matching results → prepare → refresh → inspect eligibility/issues → confirm → refresh results. **Recent batches → Open** revisits the saved request and item results. Cancelling unstarted work does not undo items that already completed.

| Screen | Procedure and permission |
| --- | --- |
| **Batch version changes** | Select a family, exact source version, and different published target in that family. Supply a reason; **Prepare compatibility asynchronously**. Review warnings/blockers before confirming eligible instances. Requires `Workflow.RequiredRole` (default `admin`). |
| **Instance variables** | Select running instances, enter variable **Name** and **Raw JSON value** rows, then **Freeze and prepare asynchronously**. Review and confirm eligible instances. Requires `Workflow.RequiredRole`. These are administrative raw writes, not normal task-form validation; they can wake conditional events. |
| **Administrative actions** | Requires `Workflow.RequiredRole` and a nonblank actor. Select the family, **Exact immutable version**, and **Waiting user-task node**. Search positions; choose **Task actions** or **Timer boundary actions**, then **Prepare batch asynchronously**. Review affected positions/tasks before confirming. MI choices include **Force parent** and **Complete all unfinished children**. Timer overrides accept no submitted variables. |

All administrative-action discovery, audit, and mutation APIs enforce the current workflow-administrator permission. The batch screen clears restricted data and reports access denial when authorization fails. Changing the development identity clears the previous selection and reloads permitted workflows; late responses from the previous identity cannot clear the new identity's selections or re-enable controls while its requests are pending. Queued preparation/execution also rechecks the setting against stored preparer/confirmer roles per item: unauthorized preparation is **ineligible**, and execution is **skipped** with `authentication_changed`. Completed items and committed asynchronous continuations remain intact. See [administrative action contracts](api-guide.md#administrative-action-batches) and [deployment changes](deployment.md#upgrade-and-compatibility-rules).

## Manage settings and shared variables

### Engine and workflow settings

Both screens require `Settings.RequiredRole` (default `admin`). **Engine settings** uses plain-text values: **Add setting**, enter namespace/key/value and optional description, then **Create setting**. **Edit → Save changes** updates a value. **Workflow settings** uses **JSON value**: numbers, booleans, arrays, objects, and quoted strings are accepted. These are settings consumed by workflows, not per-instance data.

Namespace/key or namespace/name identifies the record and is not editable after creation. On a stale update use **Reload latest** before retrying. `Authentication.UserIdentityClaim` changes require restarting API replicas; changing permission settings can change your access on the next request. Follow [configuration](deployment.md#configuration) for host-level values such as connection strings, signing keys, and claim allowlists; these are not all editable engine settings.

### Shared-variable catalog

**Shared variables** requires `SharedVariables.RequiredRole` (default `admin`). **Add variable** defines the key, type, **Array**, **Nullable**, validation, description, and optional **Set initial value**. Its type contract is immutable after creation.

Use **Update → Save value revision** to write a value, **History** to inspect revisions, and **Load current revision** when a concurrency conflict occurs. **Archive** can be blocked by published definitions, running instances, or other active references; use **Reactivate** to restore an archived entry.

JSON value fields require quoted strings. To initialize explicit nullable null in this checkout, leave **Set initial value** off when creating, then **Update** the value to JSON `null`. Creating with an initial `null` is rejected by the current API. Shared values are deployment-wide and can affect several workflows; inspect usage before changes.

### Shared-variable clients and distribution search

**Shared-variable clients** uses the same shared-variable administrator permission. **Add client**, choose its ID, display name, scopes, and optional expiry, then **Create and reveal secret**. Store the one-time secret using **Copy header template** and acknowledge **I saved it**. **Edit**, **Rotate**, and **Revoke** manage the lifecycle. Rotation offers a previous-secret grace period; choosing zero invalidates it immediately.

These credentials authenticate shared-variable APIs, not the UI identity or message/distribution APIs. **Distribution search** is a Development-only diagnostic page using a workflow family's **Workflow key**, **Client ID**, and **Client secret**; **Search tasks** inspects eligible distribution work. Assign work through **Task management** or the distribution API. See [machine authentication](deployment.md#machine-integrations).

## Manage retention

**Retention policies** requires `Settings.RequiredRole`. Choose **Edit policy**, then **Keep forever** or **Delete after a period**, with **Whole days** between 1 and 36,500. **Preview proposed policy** estimates eligible/protected records; it neither saves the policy nor deletes data. **Save policy** changes what subsequent cleanup runs use.

Saved policies apply to automatic Worker cleanup. **Run cleanup now** queues a run using those saved policies; it still requires the Worker. Inspect current/last runs and **Worker last seen**, and refresh for progress. Deleted history cannot be recovered through the UI, and pruning an instance's history permanently blocks reactivation. Follow [retention and backup guidance](deployment.md#retention-and-backup) before enabling deletion.

## Troubleshooting

| Symptom | Next step |
| --- | --- |
| UI opens but data fails to load | Confirm API/database startup and the UI's `WorkflowApi:BaseUrl`. Read the page's error message. |
| Authentication error / `401` | Generate or apply a nonexpired token; confirm matching API/UI issuer, audience, and signing key. |
| Workflow list or Start page denied | Check definition-administration permission as well as the selected event's start roles. |
| Import fails | Select a valid exported Flowbit JSON document under 2 MiB, inspect the validation error, and check prerequisites in the example catalog. |
| No start choices | The identity lacks an accepted manual-start role, or the workflow uses system-only message/timer starts. |
| Empty My work | Confirm the current username/roles, filters, assignments, and stored-state visibility conditions. An administrator can still lack task roles. |
| No available actions | Claim if needed; inspect assignment/ownership and conditions. Refresh after another actor/event changes work. |
| `404` / `409` after opening work | The resource can be hidden or stale. Reopen from the current inbox rather than reusing old action state. |
| Required or typed input rejected | Use the format shown above and the action's required values. Blank omits an input; administrative JSON fields require valid JSON. |
| Timer, retry, or batch does not progress | Check the Worker, correct database/configuration, due times, queue, and incidents. The UI does not run durable jobs. |
| Archive, deletion, version change, or reactivation blocked | Read the reported references/compatibility blockers; these operations have different eligibility rules. |

[Documentation home](index.md) · [Developer guide](developer-guide.md) · [API reference](api-guide.md) · [Deployment](deployment.md)
