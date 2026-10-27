# User-task examples

These definitions focus on the authorization, claim, assignee, required-assignment, and due-date and priority contracts of a normal `userTask`. Create and publish a definition before starting it. The examples use authenticated user endpoints and do not require the durable-job worker because every task has `asyncBefore` and `asyncAfter` disabled.

Useful endpoints:

- Start: `POST /api/instances` with exactly one of `workflowId` or `workflowKey`, an optional `startEventId`, and `variables`.
- Inspect work: `GET /api/instances/inbox` or `GET /api/user-tasks/{taskId}`.
- Claim lifecycle: `POST /api/user-tasks/{taskId}/claim` and `POST /api/user-tasks/{taskId}/unclaim`.
- Take an action: `POST /api/user-tasks/{taskId}/flows/{flowId}` with `{ "variables": { ... } }`.
- Assignment managers: `GET /api/user-tasks/manage`, `POST /api/user-tasks/{taskId}/assign`, and `POST /api/user-tasks/{taskId}/unassign`.

## [01 - Roles, claim, and bypass](01-roles-claim-and-bypass.json)

- Start values: node `1`; `requestReference` is a required string of at least three trimmed characters.
- Actors and actions: a caller with `Requester` starts the instance. A `Reviewer` or `Supervisor` may claim task `2`; the claimant can take flow `201` with a required `reviewNote`. A `Supervisor` may instead take flow `202` with `escalationNote` without first claiming because that flow enables bypass only for the `Supervisor` role. `Supervisor` is also the workflow unclaim role, and `WorkflowAdministrator` may cancel the instance.
- Expected result: flow `201` completes at node `3`; flow `202` completes at node `4`. A non-supervisor cannot use the bypass action, and an unclaimed reviewer cannot use the regular action.
- API/worker/configuration: authenticated instance and user-task APIs only; no worker and no external configuration.

## [02 - Claim inheritance](02-claim-inheritance.json)

- Start values: node `1`; no variables. The starter needs `Coordinator`.
- Actors and actions: let actor A, with `CaseAgent`, claim task `2` and take flow `201`. Task `3` uses `claimMode: "previous"` and is automatically claimed to actor A; actor A takes flow `301`. Actor B, with `Specialist`, freshly claims task `4` and takes flow `401`. Task `5` uses `claimMode: "fromNode"` with source node `2`, so it returns to actor A even though actor B performed the most recent action. Actor A takes flow `501`.
- Expected result: the instance completes at node `6`, with history showing a fresh claim, a previous-actor inheritance, a second fresh claim, and source-node inheritance. An inherited actor must still satisfy the destination task and flow roles.
- API/worker/configuration: authenticated instance, claim, flow, and task-detail APIs only; no worker and no external configuration.

## [03 - Direct assignee expression](03-direct-assignee-expression.json)

- Start values: node `1`; `requestSummary` is required. `requestedAssignee` defaults to `"  Alice.Example  "` and may be supplied explicitly.
- Actors and actions: a `Requester` starts the instance. Task `2` evaluates `if(IsNullOrWhiteSpace(requestedAssignee), Lower(Trim([sys.user])), Lower(Trim(requestedAssignee)))` once on entry. A nonblank value is trimmed and lowercased; a blank value falls back to the authenticated starter from `sys.user`. The resolved user must also hold `Reviewer` and takes flow `201` with `reviewOutcome` set to an approved/rejected value. The form shows that input as **Review outcome**, with help text and the fixed options **Approve** and **Reject**; the form offers only those options, and the authored `validation` rule still checks values sent through the API.
- Expected result: only the snapshotted direct assignee can act, and the instance completes at node `3`. With the default, the assignee is `alice.example`; with whitespace input, it is the normalized starter identity.
- API/worker/configuration: authenticated instance and user-task APIs only; no claim call, worker, or external configuration.

## [04 - Required assignment and distribution](04-required-assignment-and-distribution.json)

- Start values: node `1`; `workReference` is a required string of at least three trimmed characters. The starter needs `Requester`.
- Actors and actions: task `2` is invisible to normal workers until a distributor or an assignment manager assigns it. Its assigned `Worker` takes flow `201`. Task `3` uses `assignmentMode: "previous"` and follows the completed owner. An `OperationsManager` takes flow `401` from the independent task `4`. Task `5` uses `assignmentMode: "fromNode"` with node `2`, returning ownership to the original distributed worker, who takes flow `501`. Users with `AssignmentManager` or `OperationsManager` may assign, reassign, or release managed tasks.
- Expected result: the fresh assignment gate is enforced, previous and source-node ownership are snapshotted on entry, and the instance completes at node `6` under the original worker.
- API/worker/configuration: an external distributor calls `GET /api/task-distribution/workflows/{workflowKey}/tasks` and `POST /api/task-distribution/workflows/{workflowKey}/tasks/{taskId}/assign` with `X-Client-Id` and `X-Client-Secret`. Configure `WorkflowContext:Config:exampleDistributorClientId` and `WorkflowContext:Config:exampleDistributorSecret`; the JSON contains only trusted `${config.*}` references. No durable-job worker is required.

For concurrent clients, use the task's current `updatedAt` as `expectedUpdatedAt` on assignment changes, then refresh after a `409` conflict.

## [06 - Due date and priority](06-due-date-and-priority.json)

- Start values: node `1`; `amount` is a required positive number and `contractEnd` a required date-time with an offset, such as `2030-01-31T17:00:00+02:00`. The starter needs `Requester`.
- Actors and actions: task `2` evaluates `dueDate` `if(amount >= 10000, 'PT4H', 'P2D')` and `priority` `if(amount >= 10000, 80, 40)` once when it is created, so a purchase of 10,000 or more is due four hours after the task is created with priority 80, and a smaller one two days later with priority 40. A `Reviewer` takes flow `201`. Task `3` is due at `contractEnd` with the fixed priority `60`; a `Signatory` takes flow `301`.
- Expected result: inbox items and `GET /api/user-tasks/{taskId}` return `dueAt`, `priority`, and `dueStatus` (`overdue`, `dueSoon` within the `UserTasks.DueSoonThreshold` window, which defaults to one day, or `upcoming`). Changing `amount` after the review was created does not move its due date. The instance completes at node `4`.
- API/worker/configuration: authenticated instance and user-task APIs only; no worker and no external configuration. Sort My work by `dueAt` or `priority`, or filter it with `dueStatus`, `dueFrom`, `dueTo`, `minPriority`, and `maxPriority`.
