# Flowbit workflow example catalog

This directory is the canonical catalog of editor-loadable, runtime-valid
Flowbit workflow definitions. Each JSON file uses the current editor save
format, stable workflow ids, integer node and sequence-flow ids, and no
legacy properties. Most ids use `example-*`; the administrative-action sample
retains its authored `admin-action` id. The examples are intentionally focused: combine their
patterns when building a production workflow.

The catalog contains 53 workflows. Automated tests recursively discover every
JSON file, deserialize it, validate it with the runtime's strict JavaScript
parser, load and render it in the editor, and verify that this page links it.
Those definition/editor tests do not execute integrations. The separate
[Worker-enabled acceptance suite](../Flowbit/tests/Flowbit.BrowserAcceptanceTests/README.md)
executes the focused scenarios listed in the [coverage matrix](#coverage-matrix)
through real HTTP endpoints and a controlled loopback REST service.

## Load and run an example

New to the runtime? Follow the [HTTP getting-started guide](../docs/getting-started.md) for isolated PostgreSQL setup, authentication, import, publication, and a complete approval.

1. Open `flowbit-editor.html` (the launcher for the shared editor assets) and choose **Load JSON**, or use **Workflow editor → File → Load JSON** in Flowbit.Ui.
2. Select a definition below, inspect it, and save it without validation errors.
3. Publish the definition through the API or Blazor UI and start an instance.
4. Use an authenticated actor with the documented roles to complete its work.
5. Run `Flowbit.Worker` for timers and durable jobs. Other examples need only
   the API and PostgreSQL unless their row says otherwise.

Dates, timer intervals, and retry delays are short or illustrative local-test
values. Adjust them before production use.

## Basics

| Workflow | Start values | Actors and actions | Expected result | Requirements |
| --- | --- | --- | --- | --- |
| [Lanes, external IDs, and variable scopes](basics/01-lanes-external-ids-and-variable-scopes.json) | As `Requester`, supply required `requestId`; optional typed values have defaults. | A `FinanceReviewer` approves an eligible stored request with `approvalNote`; a `FinanceManager` may approve or escalate with `escalationReason`. | Demonstrates lanes; node/flow `externalId`; process, start, and action scopes; every data type; arrays; a nullable process value; defaults; validation; and stored-state action conditions. | API and database; no Worker or configuration. |
| [Two-step approval with role-protected return](basics/02-two-step-approval.json) | No values or special start role. | Complete `approval1`, then an `admin` may take `back` from `approval2` for another visit or complete the second approval normally. Leave `approval2` waiting for the one-minute timer-triggered return. | Demonstrates an ordinary role-protected return flow, administrative selection, and a timer boundary that returns waiting work. | API and database for ordinary actions; `Flowbit.Worker` is required for the one-minute timer return. No configuration. |
| [Required user-task assignment](basics/03-user-task-assignment.json) | No values. | A `Manager` assigns the pending work; the assigned actor completes it. | Demonstrates a user task that must be explicitly assigned before action. | Authenticated API and database; no Worker or configuration. |
| [Inbox visibility from actor claims](basics/04-inbox-task-condition.json) | Supply numeric `depId`. | An actor whose allowlisted `depId` claim matches the stored value sees and completes the task. | Demonstrates PostgreSQL-authoritative inbox visibility using stored state and caller claims. | Authenticated API and database with the actor claim allowlisted; no Worker. |
| [Intermediate conditional event](basics/05-conditional-event.json) | `amount` defaults to zero. | Update the amount while the sibling branch is active, then confirm the user task. | An atomic conditional catch continues when its stored-variable predicate becomes true. | API and database; no Worker or configuration. |
| [Durable asynchronous conditional event](basics/06-conditional-event-durable-async.json) | `amount` defaults to zero. | Update the amount while the sibling branch is active, then confirm the user task. | A durable conditional catch latches a wake job and resumes through worker processing. | API, database, and `Flowbit.Worker`; no external configuration. |
| [Shared-variable user action](basics/07-shared-variable-user-action.json) | Create active catalog key `examples.approval.amount` as a non-nullable scalar number. | A `SharedStateOperator` completes **Update Shared Approval Amount** with the new amount. | An explicit user action updates a `readWrite` shared binding without using shared state as a conditional-event wake signal. | Authenticated API, database, and the documented catalog prerequisite; no Worker. |
| [Interrupting conditional boundary](basics/08-interrupting-conditional-boundary.json) | `riskScore` defaults to zero. | Update `riskScore` to at least 80 while **Review Risk** is active, or complete the review normally. | The default interrupting boundary cancels the original task and opens **Escalated Review**. | API and database; no Worker or configuration. |
| [Recurring non-interrupting conditional boundary](basics/09-recurring-non-interrupting-conditional-boundary.json) | `alertRaised` defaults to false and `alertsHandled` to zero. | Alternate `alertRaised` from false to true while **Monitor Case** remains active; run the Worker after each true edge. | Every false-to-true edge creates an independent durable alert branch, while true-to-true writes are suppressed and the host stays active. | API, database, and `Flowbit.Worker`; no configuration. |
| [Administrative actions across protected approvals](basics/10-admin-action.json) | No values or special start role. | Ordinary approval requires `User` at `approval1` and `Manager` at `approval2`; ordinary `back`/`cancel` additionally require the flow's `admin` role. A caller holding `Workflow.RequiredRole` (default `admin`) can use instance-detail administrative actions independently of those task roles. | Administrative `approval` advances to the next approval/end; `back` returns to `approval1`; both authored `cancel` flows reach the normal end event. Overrides retain a completed audit batch and leave normal inbox permissions intact. | API and database; optional Flowbit.Ui instance detail. No Worker for immediate actions in this definition; batch execution requires a Worker. |

See [instance administrative actions](../docs/ui-guide.md#use-instance-administrative-actions)
and the [direct HTTP contract](../docs/api-guide.md#instance-administrative-actions).
An `admin` role on a sequence flow does not itself satisfy the source task's role;
the explicit administrative endpoints use their separate configured permission.

## User tasks

See the [user-task guide](user-tasks/README.md) for endpoint examples and actor
walkthroughs.

| Workflow | Start values | Actors and actions | Expected result | Requirements |
| --- | --- | --- | --- | --- |
| [Roles, claim, and bypass](user-tasks/01-roles-claim-and-bypass.json) | `Requester` supplies required `requestReference`. | A `Reviewer`/`Supervisor` claims and reviews; `Supervisor` can use the protected claim-bypass flow and unclaim; `WorkflowAdministrator` can cancel. | Normal and bypass routes complete at distinct end events while start, node, flow, claim-bypass, cancellation, and unclaim roles are enforced. | Authenticated API and database; no Worker or configuration. |
| [Claim inheritance](user-tasks/02-claim-inheritance.json) | `Coordinator` starts with no values. | Actor A claims the fresh task and inherits `previous`; actor B claims the specialist task; the final `fromNode` task returns to actor A. | Demonstrates distinct actors and `fresh`, `previous`, and `fromNode` claim modes. | Authenticated user-task API and database; no Worker or configuration. |
| [Direct assignee expression](user-tasks/03-direct-assignee-expression.json) | `Requester` supplies `requestSummary`; `requestedAssignee` has a padded default and may be blank. | The resolved `Reviewer` acts, choosing **Approve** or **Reject** for the labelled `reviewOutcome` input. `Lower`, `Trim`, `IsNullOrWhiteSpace`, variables, and `[sys.user]` normalize the direct assignee. | Only the snapshotted assignee can complete the review. | Authenticated API and database; no Worker or configuration. |
| [Required assignment and distribution](user-tasks/04-required-assignment-and-distribution.json) | `Requester` supplies required `workReference`. | A distributor assigns the fresh task; `Worker` completes inherited work; `OperationsManager` handles the independent task; manager roles can assign/release. | Shows required assignment plus `fresh`, `previous`, and `fromNode` assignment ownership. | API and database; configure `${config.exampleDistributorClientId}` and `${config.exampleDistributorSecret}` for the external distribution endpoints; no Worker. |
| [Due date and priority](user-tasks/06-due-date-and-priority.json) | `Requester` supplies `amount` and `contractEnd`, a date-time with an offset. | A `Reviewer` approves the purchase; a `Signatory` countersigns. | The review is due four hours after it is created with priority 80 for 10,000 or more, otherwise two days later with priority 40; the countersignature is due at `contractEnd` with priority 60. My work shows overdue and due-soon badges and sorts by both. | Authenticated API and database; no Worker or configuration. |

The [variable roles and role management example](user-tasks/05-variable-roles-and-role-management.json)
starts without input or a start role. Task roles capture `reviewRoles` as
`Finance, Manager`, while the **Approve** action captures `approvalRoles` as
`Finance`. Updating either process variable later leaves the waiting task
unchanged. A `RoleManager` can use **Task management → Edit roles** or the roles
API to replace that task's saved permissions, including its action roles. The
manager permission does not grant permission to complete the task. The example
requires the role-policy migration, upgraded API, and database; no Worker or
external configuration is needed. Shared `string[]` bindings can be used in the
same selectors after creating their matching shared-variable catalog entries.

## Script tasks

See the [script-task guide](scripts/README.md) for expected values, the complete
JavaScript execution API, FlowInfo shapes, and atomic error behavior.

| Workflow | Start values | Actors and actions | Expected result | Requirements |
| --- | --- | --- | --- | --- |
| [NCalc helper functions](scripts/01-ncalc-helper-functions.json) | No values; typed process defaults provide input. | `ExampleReviewer` inspects results and finishes. | Ordered assignments exercise `Length`/`Len`, null/whitespace checks, contains/prefix/suffix, case conversion, trim, regex, and representative built-ins. | API and database; no Worker or configuration. |
| [JavaScript execution API](scripts/02-javascript-execution-api.json) | No values; defaults provide typed bridge data. | `ExampleReviewer` inspects and finishes. | Uses `getVariable`, `setVariable`, `getVariables`, and `hasVariable`, writing validated string, number, boolean, date, datetime, array, and JSON values. | API and database with the configured Jint sandbox; no Worker or external configuration. |
| [FlowInfo in NCalc and JavaScript](scripts/03-flowinfo-ncalc-and-javascript.json) | No values. | `ExampleReviewer` selects **Accept** or **Return**. | NCalc and JavaScript inspect action/traversal evidence; an Exclusive gateway routes according to the selected flow. | API and database; no Worker or configuration. |
| [JavaScript error boundary](scripts/04-javascript-error-boundary.json) | No values. | `ExampleOperator` acknowledges the handled failure. | A deliberate script exception rolls back staged writes, populates the boundary error variable, and follows the error path. | API and database; no Worker or configuration. |

## Service tasks

See the [service-task guide](service-tasks/README.md) for safe local mock request
and response contracts. The definitions contain no public service URL or secret.

| Workflow | Start values | Actors and actions | Expected result | Requirements |
| --- | --- | --- | --- | --- |
| [REST templating and typed output](service-tasks/01-rest-templating-and-typed-output.json) | `ExampleRequester` supplies required `requestId` and `customerId`; other request values have defaults. | `ExampleReviewer` completes review after a successful call. | URL/header/body interpolation feeds a REST call; status plus declared/dynamic typed mappings, arrays, defaults, and validation commit atomically. | API, database, controlled mock API, `${config.exampleApiBaseUrl}`, and `${config.exampleApiToken}`; no Worker. |
| [REST error boundary and error end](service-tasks/02-rest-error-boundary-and-error-end.json) | `ExampleRequester` supplies `requestId` and `resourceId`. | `ExampleOperator` retries after fixing the mock or ends with a domain fault. | HTTP status and error variables are captured; retry can succeed, while the terminal route produces `EXAMPLE.REST_FAILURE`. | API, database, controlled failing mock, and the same two trusted REST settings; no Worker. |
| [Async REST output to shared state](service-tasks/03-rest-shared-variable-output.json) | Create active catalog key `examples.service.status` as a non-nullable scalar string. | Start an instance; no user action is required. | An async-before REST call maps a non-empty `status` response into the shared catalog exactly once; the boundary faults the instance on failure. | API, database, `Flowbit.Worker`, controlled mock API, `${config.exampleApiBaseUrl}`, and the catalog prerequisite. |

## Messages

See the [message guide](messages/README.md) for headers, payloads, duplicate
delivery behavior, and local test calls.

| Workflow | Start values | Actors and actions | Expected result | Requirements |
| --- | --- | --- | --- | --- |
| [Typed message start](messages/01-message-start-typed-mapping.json) | Deliver the documented order payload to the system-only message start with its correlation and `Idempotency-Key` headers. | No actor starts it; `OrderReviewer` accepts the imported order. | Typed payload fields map atomically, `orderId` becomes an active-scope business key, and duplicate start delivery is rejected by its independent idempotency key. | API and database; the two `setting.examples.*` values have seeded defaults, while `${config.exampleMessageClientSecret}` must be configured; no Worker. |
| [Message catch delivery idempotency](messages/02-message-catch-delivery-idempotency.json) | `Purchasing` starts with a `PO-*` `purchaseOrderId`, then sends the order. | The authenticated external client delivers a typed receipt; `Purchasing` verifies it. | The catch validates credentials/header, maps values, and accepts a delivery key once; a duplicate `X-Flowbit-Delivery-Key` delivery is rejected. | API and database; the message client id has a seeded default, while `${config.exampleMessageClientSecret}` must be configured; no Worker. |
| [Receipt versus timeout](messages/03-message-catch-with-timeout.json) | `ExampleRequester` starts with `caseKey` or its default. | Deliver the receipt before expiry and let `ExampleReviewer` confirm it, or let the deadline expire and let `ExampleOperator` resolve escalation. | The competing message wait or 30-second timer is cancelled. A late delivery returns `400` without changing mapped values; a duplicate committed key returns `409`. | API, database, Worker, seeded message client id, and `${config.exampleMessageClientSecret}`. |

## Start events

See the [start-event guide](start-events/README.md) for request shapes and
conflict semantics.

| Workflow | Start values | Actors and actions | Expected result | Requirements |
| --- | --- | --- | --- | --- |
| [Business-key scopes and idempotency](start-events/01-business-key-scopes-and-idempotency.json) | Choose the `active` start as `ActiveCaseStarter` with `ACTIVE-*`, or the `all` start as `PermanentCaseStarter` with `PERM-*`; send that start's transport-idempotency header. | `CaseWorker` completes the shared task; `CaseManager` may cancel. | Active-scope business keys can be reused after termination; all-scope keys and both independent transport-idempotency keys stay reserved across versions. | Authenticated API and database; no Worker or configuration. |

## Gateways and scoped interruption

The [gateway guide](gateways/README.md) explains topology and suggested action
sequences for all eight definitions.

| Workflow | Start values | Actors and actions | Expected result | Requirements |
| --- | --- | --- | --- | --- |
| [Exclusive priority and default](gateways/01-exclusive-priority-and-default.json) | Set `urgent` and `amount`. | Complete the task selected by the Exclusive split. | Priority 1 wins before the amount condition; otherwise the required default is used, and a separate merge rejoins the routes. | API and database; no roles, Worker, or configuration. |
| [Parallel fork and join](gateways/02-parallel-fork-and-join.json) | No values. | Actors with `Finance`, `Legal`, and `Security` complete their respective reviews in any order; a `Coordinator` then completes **Consolidate Reviews** with **Finish**. | Three tokens run concurrently and the Parallel join waits for all incoming branches before consolidation. | Authenticated API and database with the listed actor roles; no Worker or configuration. |
| [Inclusive split and merge](gateways/03-inclusive-conditional-split-and-merge.json) | Set the Legal and Security flags. | Complete every selected review. | Every true route is created; the default is used when none match, and the merge waits only for selected reachable branches. | API and database; no roles, Worker, or configuration. |
| [Complex two-of-three merge](gateways/04-complex-two-of-three-merge.json) | No values. | Complete any two reviewer tasks, inspect the cancelled third review, then finalize. | `TotalIncomingCount() >= 2` activates the coordinator task; the merge's `joinCancellation` cancels the unfinished review before finalization. | API and database; no roles, Worker, or configuration. |
| [Complex start/reset cycle](gateways/05-complex-start-reset-cycle.json) | No values. | Choose reset/next-cycle or finish actions on the phase tasks. | `IncomingCount` and `[gateway.waitingForStart]` demonstrate persisted start/reset phases and repeatable cycles. | API and database; no roles, Worker, or configuration. |
| [Scoped interrupt of a parallel branch](gateways/06-scoped-interrupt-parallel-branch.json) | No values. | Leave normal investigation active and trigger the emergency branch. | The scoped interrupt resolves its nearest Parallel activation, cancels sibling work, and promotes the trigger to emergency response. | API and database; no roles, Worker, or configuration. |
| [Nested scope cancellation](gateways/07-nested-scope-cancellation.json) | `ExampleRequester` starts with `caseKey` or its default. | `Supervisor` interrupts the inner scope and completes recovery; `ExampleReviewer` completes the independent outer task. Optional inner reviewers are `inner-analyst`, `inner-manager`, and `inner-director`, each with `Reviewer`. | Inner active/pending MI items, message wait, timer wait, and its job are cancelled. Outer work survives and the outer join still waits for both branches. | API, database, Worker, seeded message client id, and `${config.exampleMessageClientSecret}`. |
| [Terminate with live siblings](gateways/08-terminate-live-siblings.json) | `ExampleRequester` starts with no input. | `Supervisor` chooses normal end or terminate; `ExampleReviewer` owns ordinary sibling work. | Normal end leaves sibling work and its 15-second timer alive; terminate completes the instance and cancels both. No cancelling join is used. | API, database, and Worker for the timer; no external configuration. |

## Multi-instance user tasks

See the [multi-instance guide](multi-instance/README.md) for child-task actors,
result shapes, aggregate evidence, and completion paths.

| Workflow | Start values | Actors and actions | Expected result | Requirements |
| --- | --- | --- | --- | --- |
| [Parallel collection assignees](multi-instance/01-parallel-collection-assignees.json) | `ReviewCoordinator` starts with no input; the declared process collection supplies its default reviewers. | Every directly assigned `Reviewer` completes one parallel child. | All items are available together; after all complete, outcome `201` wins and ordered result evidence is written. The engine-only default remains the required no-match safety route. | API and database; no Worker or configuration. |
| [Sequential collection assignees](multi-instance/02-sequential-collection-assignees.json) | `ReviewCoordinator` starts with no input; the declared process collection supplies its ordered default reviewers. | Assigned `Reviewer` actors complete one child at a time in collection order. | Only the current child is active; after all items, outcome `201` wins and the result array preserves collection order. The engine-only default remains available only if no condition matches. | API and database; no Worker or configuration. |
| [Cardinality one per actor](multi-instance/03-cardinality-one-per-actor.json) | `ReviewCoordinator` supplies `panelSize` or uses its default. | Distinct `Panelist` actors each complete at most one representative pooled item. | Cardinality fan-out, one-per-actor inbox selection, stale-action conflict behavior, and aggregate results are visible. | API and database; no Worker or configuration. |
| [Early quorum and priorities](multi-instance/04-early-quorum-and-priorities.json) | `ApprovalCoordinator` supplies `quorumSize` or uses its default. | Distinct `Approver` actors vote; `ApprovalManager` resolves a mixed quorum. | `afterEach` evaluates `CountFlow`, `PercentFlow`, `mi.total/completed/remaining`, completion priorities, and `FlowInfo`; a winning quorum cancels unfinished items. | API and database; no Worker or configuration. |
| [Parent interrupt](multi-instance/05-parent-interrupt.json) | `ReviewCoordinator` starts the collection or uses defaults. | Assigned reviewers may work; a role-authorized `ReviewManager` takes the parent-level interrupt independently of a child item. | The interrupt advances the parent exactly once, cancels unfinished items, appends parent-interrupt evidence after child results, and retains the required engine-only default. | Authenticated API and database, including the parent multi-instance flow endpoint; no Worker or configuration. |
| [Multi-instance reminder](multi-instance/06-multi-instance-reminder.json) | No values; cardinality is three. | Complete three approvals while the non-interrupting reminder remains observable. | `afterAll` aggregate completion, a parent boundary timer, and durable execution record reminders without cancelling the parent. | API, database, and `Flowbit.Worker`; no external configuration. |
| [Role management across active and pending items](multi-instance/07-role-management-active-and-pending.json) | `ReviewCoordinator` starts with no input; defaults assign `analyst`, `manager`, then `director`. | `analyst` initially needs `Reviewer` and `Approver`; after the first completion, `RoleManager` replaces task/action roles for unfinished items. | The active/pending children receive the replacement policy together; assignees and completed history remain intact. Identical stale retries are unchanged success; conflicting stale edits return `409`. | API and database with the role-policy migration; no Worker or configuration. |

## Durable jobs and timers

See the [durable-job guide](durable-jobs/README.md) for a controlled REST mock,
attempt inspection, incident recovery, and PowerShell/Bash walkthroughs.

| Workflow | Start values | Actors and actions | Expected result | Requirements |
| --- | --- | --- | --- | --- |
| [Async before and after](durable-jobs/01-async-before-after.json) | No values; `executionCount` defaults to zero. | After worker processing, complete **Inspect Result**. | `asyncBefore` queues the script; `asyncAfter` commits the automatic task before routing, with explicit retry delays and an empty retry list. | API, database, and `Flowbit.Worker`; no configuration. |
| [Retries and incident recovery](durable-jobs/02-retries-and-incident-recovery.json) | `ExampleRequester` starts with `scenario=fail-once` or `recover`. | A job operator inspects attempts, corrects the mock, and retries an incident; `ExampleOperator` finishes inspection. | `retryFirst` with two 2-second delays demonstrates second-attempt success or exhaustion after three attempts, followed by fenced manual recovery. Job/attempt headers correlate calls. | API, database, Worker, controlled REST mock, `${config.exampleApiBaseUrl}`, and `${config.exampleApiToken}`. |
| [Error-boundary precedence](durable-jobs/03-error-boundary-precedence.json) | `ExampleRequester` starts without values. | `ExampleOperator` inspects both captured errors after Worker processing. | `boundaryFirst` catches the first script immediately; `retryFirst` catches the second after two retries. Neither handled failure creates an incident. | API, database, and Worker; no external configuration. |
| [Intermediate timer delay](timers/02-intermediate-timer-delay.json) | No values. | Wait 30 seconds, then finish **Timer Fired**. | The token rests at an ISO-8601 duration timer and resumes through durable work. | API, database, and Worker; no configuration. |
| [Recurring timer start](timers/03-recurring-timer-start.json) | No values; use the manual start only for comparison. | Acknowledge each scheduled instance. | `R5/PT30S` creates five instances, 30 seconds apart. | API, database, and Worker with timer schedules enabled; no configuration. |
| [Absolute timer start](timers/04-absolute-timer-start.json) | No values; change the authored UTC time to a near-future instant before testing. | Acknowledge the scheduled instance. | A one-time absolute schedule creates an instance at its UTC timestamp. | API, database, and Worker with timer schedules enabled; no configuration. |
| [User-task reminder and deadline](timers/05-user-task-reminder-and-deadline.json) | No values; `remindersSent` defaults to zero. | Approve normally before one minute, or complete escalated approval after the deadline. | A recurring non-interrupting boundary records reminders while an interrupting deadline cancels the original task and escalates. | API, database, and Worker; no configuration. |
| [Working-day deadline](timers/06-working-day-deadline.json) | No values. | Approve **Review Claim** before its deadline, or complete **Escalated Review** after it. | The review is due two working days after it is created, and an interrupting timer escalates it after three working days, both counted in the `examples.weekdays` calendar (Monday to Friday, 09:00 to 17:00 UTC), so a weekend does not count. The escalated review is due four working hours after it is created. | API and database; Worker for the deadline timer; the `examples.weekdays` business calendar, which the migration seeds when missing. |
| [Contract end wait](timers/07-contract-end-wait.json) | Supply `contractEnd` with `Z` or an explicit offset; for a test, use a time a minute or two ahead, such as the current UTC time plus two minutes. Its validation rule rejects a value without an offset at the start. | Wait until the contract end, then complete **Renew or Close Contract**. | The timer catch's `timeDate` expression `${contractEnd}` is evaluated once when the token reaches it, and the due time is saved; changing `contractEnd` afterwards does not move it. | API, database, and Worker; no configuration. |
| [Urgency review deadline](timers/08-urgency-review-deadline.json) | Supply Boolean `urgent`. | Approve **Review Request** in time, or complete **Escalated Review** after the deadline. | The interrupting boundary's `timeDuration` expression `if(urgent, 'PT2M', 'PT10M')` gives two minutes for an urgent request and ten otherwise, chosen when the review starts. | API, database, and Worker; no configuration. |
| [Working-day SLA from a number](timers/09-working-day-sla-from-number.json) | Supply a whole number `slaDays` from 1 to 20. | Resolve **Resolve Case** in time, or complete **Escalated Case** after the SLA. | A JavaScript script task turns the number into a duration such as `P2D`, because a timer expression cannot join text and a number; the boundary counts that duration in the `examples.weekdays` calendar. | API and database; Worker for the SLA timer; the `examples.weekdays` business calendar, which the migration seeds when missing. |

## Trusted configuration placeholders

Definitions use deployment-owned values, never embedded secrets or public
services:

| Reference | Purpose |
| --- | --- |
| `${config.exampleApiBaseUrl}` | Base URL of a local/private REST mock. |
| `${config.exampleApiToken}` | Token supplied to that mock. |
| `${setting.examples.messageClientId}` | Client id accepted by message start/catch nodes; seeded as `example-message-client` when missing. |
| `${config.exampleMessageClientSecret}` | Message client secret kept outside workflow JSON. |
| `${setting.examples.messageCorrelation}` | Expected message-start correlation header value; seeded as `orders:inbound` when missing. |
| `${config.exampleDistributorClientId}` | Client id for external task distribution. |
| `${config.exampleDistributorSecret}` | Secret for external task distribution. |

## Coverage matrix

Type presence is covered by catalog validation; it is not a claim that every
combination or production integration has been executed. The acceptance suite
loads canonical definitions, uses real PostgreSQL/API/UI/Worker hosts and HTTP
actions, and records the outcome of each test run separately.

| Coverage layer | Included | Verification boundary |
| --- | --- | --- |
| Authored node and event types | All 21: four tasks, four gateways, three starts, three intermediate catches, three boundaries, three ends, and the scoped-interrupt extension. | Recursive runtime/editor validation and the explicit 21-type assertion; editor execution here uses Jint rather than a real browser. |
| Demonstrated catalog scenarios | Claims, assignment/distribution, captured roles, task due dates and priorities, working-time durations in a business calendar, scripts, typed REST/messages, idempotency, multi-instance aggregation, timers, conditional events, retries/recovery, and scope cancellation. | The 50 definitions and category walkthroughs explain actors, inputs, prerequisites, and expected outcomes. |
| Focused runtime acceptance | Both REST recovery paths and boundary-order policies; both receipt/deadline paths; nested cancellation; MI role replacement/stale edits; normal versus terminate end; approval timer return; quorum cancellation before finalization. | `ExampleWorkflowAcceptanceTests` uses real HTTP and Worker processing with controlled local services. It is explicitly run outside the solution and CI; inspect its results before claiming a verified run. |
| Browser and manual checks | New diagrams at desktop/tablet widths; incident recovery and role management at desktop, tablet, and mobile widths. | Real Chromium input, screenshots, and browser diagnostics supplement API assertions; native picker and visual-review limitations are reported separately. |

Run commands and evidence locations are in the
[acceptance README](../Flowbit/tests/Flowbit.BrowserAcceptanceTests/README.md).

## Runtime-only companion features

Some operational capabilities are not authored as workflow JSON fields. Use the
runtime documentation alongside these definitions:

- [Standing delegation policies and `/api/user-delegations`](../AGENTS.md#runtime-engine-flowbit)
- [Assignment, reassignment, release, claim, unclaim, and task actions](../Flowbit/README.md#user-tasks)
- [Instance, inbox, and advanced variable search](../Flowbit/README.md#advanced-variable-search)
- [Cross-workflow node-execution reporting](../Flowbit/README.md#node-execution-activity)
- [Immutable definition versioning and workflow-key storage](../Flowbit/README.md#storage)
- [Runtime administration and API endpoints](../Flowbit/README.md#main-api)
- [Durable jobs, schedules, retries, and worker operation](../Flowbit/README.md#durable-async-work-and-timers)

## Manual verification checklist

After automated validation, exercise integrations with local test identities and
controlled mocks:

- claim, bypass, unclaim, inherited claim, direct assignee, assignment manager,
  and distributor actors;
- REST success, typed mapping failure, caught HTTP failure, retry, and error-end
  paths;
- typed message start, catch delivery, duplicate delivery key, and duplicate
  start idempotency key;
- active/all business-key conflicts and independent transport-idempotency
  conflicts;
- both FlowInfo action routes and each multi-instance aggregate, interrupt, and
  after-all completion path;
- timers and durable retry boundaries with `Flowbit.Worker` running.
