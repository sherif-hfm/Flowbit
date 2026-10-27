# UI-04: Task due date and priority

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Done |
| Category | Flowbit.Ui feature (with engine changes) |
| Effort | M |
| Priority | Recommended order #3 |
| Depends on | None |
| Related | [UI-12](ui-12-task-escalation.md), [EVENT-01](event-01-expression-timers.md), [EVENT-08](event-08-business-calendars.md), [UI-02](ui-02-process-analytics-heatmap.md), [UI-13](ui-13-notification-center.md) |

## Summary

`userTask` gains an optional `dueDate` and `priority`, evaluated once when the work item is created and saved on it. My work shows due dates with overdue and due-soon badges and can sort and filter by due date and priority. Escalation is a separate item ([UI-12](ui-12-task-escalation.md)).

## Why it adds value

- Deadlines become visible to the people doing the work instead of existing only as timers in the model.
- The inbox APIs return them too, so applications that embed Flowbit can sort their own worklists by them.

## First release acceptance criteria

The [plan](#plan) revised these criteria on 2026-10-27: a due date can be a fixed value as well as an expression, a result that cannot be used never fails the transition, the APIs also return a due status computed when they are read, and changing only these fields never blocks a workflow-version change.

- [x] A `userTask`, including a multi-instance one, accepts an optional `dueDate` (a fixed ISO-8601 duration counted from when the work item is created, a fixed date-time with an offset, or an NCalc expression that gives either one as text) and an optional `priority` (an NCalc expression that gives a whole number; higher is more urgent).
- [x] Both are evaluated once when the work item is created and saved on it; multi-instance children get the values of their execution. A result that cannot be used leaves the value empty and logs a warning; the transition does not fail.
- [x] Inbox, task-management, task-distribution, and task APIs return `dueAt`, `priority`, and `dueStatus`; the inbox sorts by due date and priority and filters by due status, due range, and priority range.
- [x] My work shows the due date with overdue and due-soon badges; the `UserTasks.DueSoonThreshold` engine setting sets the due-soon window.
- [x] Definitions and tasks without these fields behave exactly as before, and changing only these fields never blocks a workflow-version change.
- [x] Verified in a real browser at desktop and phone widths.

## Later releases

[UI-16](ui-16-overdue-work-and-due-date-follow-ups.md) records the later-release scope that is still wanted: an "Overdue tasks" card on Overview, due-date and priority sorting and filters in Task management and task distribution, and recomputation when a variable a value reads changes.

- Durations counted in working time stay with [EVENT-08](event-08-business-calendars.md).

## Current state

Verified on 2026-10-27: done. The first release acceptance criteria are met.

- `FlowNodeModel` ([WorkflowModel.cs](../../Flowbit/src/Flowbit.Shared/Models/WorkflowModel.cs)) carries `dueDate` and `priority`, and `UserTaskDueRules` parses fixed values and interprets expression results. `WorkflowDefinitionValidator` and the editor's save validation apply the [due date and priority](../node-reference.md#due-date-and-priority) rules, and `WorkflowVersionCompatibilityEvaluator` leaves both fields out of version contracts.
- `WorkflowEngineService` resolves both where it resolves the assignee and, for a multi-instance task, once per execution. Migration `AddUserTaskDueDateAndPriority` adds `user_tasks."DueAt"` and `"Priority"`, the `(Status, DueAt, Id)` index, and the [`UserTasks.DueSoonThreshold`](../engine-settings.md#usertasksduesoonthreshold) setting.
- The inbox sorts by `dueAt` and `priority` and filters by due status, due range, and priority range ([API reference](../api-guide.md#get-apiinstancesinbox)). Task, inbox, task-management, and task-distribution responses return `dueAt`, `priority`, and `dueStatus`.
- Flowbit.Ui's shared [`TaskDueDate`](../../Flowbit/src/Flowbit.Ui/Components/Shared/TaskDueDate.razor) component shows the date and badge in My work, Overview, task detail, and Task management. The editor's user-task inspector has **Due date** and **Priority** fields, and [example user-tasks/06](../../examples/user-tasks/06-due-date-and-priority.json) uses both.
- Tests: `UserTaskDueRulesTests`, `UserTaskDueDateApiTests`, `UserTaskDueMigrationTests`, and `TaskDueDateUiTests`, plus additions to the definition-validation, version-compatibility, database-schema, editor, and example-catalog tests. The full `Flowbit.Tests` run passed all 2,851 tests. The browser smoke suite passed all 73 cases, including the new R10 (1440×900 and 390×844) and E10 (1440×900 and 1024×768) scenarios.
- A review the same day found that a year duration such as `P1Y` passed validation and left the task without a due date at runtime. Definition validation and the editor now reject it, as they reject months. A new API test confirms that a pending `asyncBefore` task's duration counts from entry and that activation keeps the saved values. Further API tests check the values on claim, unclaim, and task-distribution responses; an actual workflow-version change, where the open work item keeps its saved values and a work item created afterward uses the target version's; and that the `UserTasks.DueSoonThreshold` setting is read only when a response or filter needs it, once per request. E10, which now also checks `P1Y`, and R10 were rerun in Chromium and pass with no console errors.
- Real-browser verification on a local stack at 1440×900 and 390×844 covered My work's badges, tooltips, sorting by due date and priority, the Due and priority filters and Clear; task detail with and without values; Task management; Overview; and the editor fields, including live errors, a blocked save, and a saved version that reloads. It found the summary card's label styles stretching the task detail badge at phone width; the styles now apply only to the card's own label and value. The console showed no errors apart from reconnect attempts while the UI was restarted for that fix.

## Builds on

- Assignee-expression evaluation on task entry: the same evaluate-once-and-save pattern.
- Inbox sorting, filtering, and paging in PostgreSQL.
- Note: overdue is computed when lists are read, so the first release needs no Worker.

## Open questions

The plan decided each question; see [scope and decisions](#1-scope-and-decisions).

- When the expression fails, should the transition fail, or should the task be created without a due date and a warning logged, as assignee expressions do? **Decided:** the work item is created without the value and a warning is logged, as for assignee expressions. A due date or priority orders work; it must not stop the process. Definition validation catches what it can before publication.
- Should the due date be recomputed when a source variable changes? **Decided:** no. Values are saved when the work item is created and never change afterward. A reactivated task is a new work item and evaluates again.
- How is the due-soon threshold configured? **Decided:** by the deployment-wide engine setting `UserTasks.DueSoonThreshold`, a fixed ISO-8601 duration that defaults to `P1D`. The API computes each task's `dueStatus` from it, so Flowbit.Ui and embedding applications agree.
- Migration: new `user_tasks` columns and the indexes needed for sorting. **Decided:** two nullable columns without backfill and one `(Status, DueAt, Id)` index for due-date filters. The inbox orders its candidate tasks after choosing one task per actor, so no index can serve that ordering, and none is added for sorting or priority.

## Plan

Written on 2026-10-27, following [Writing a plan](README.md#writing-a-plan).

### 1. Scope and decisions

The first release lets a user task, normal or multi-instance, carry a due date and a priority. Both are resolved once when each work item is created and saved on it. The APIs return them, and My work shows, sorts, and filters by them. Escalation ([UI-12](ui-12-task-escalation.md)), notifications, recomputation, and working-time durations ([EVENT-08](event-08-business-calendars.md)) are not part of it.

- **Fixed values or expressions.** `dueDate` accepts the fixed forms that timers use, so `"P2D"` works without quotes. Anything else is an NCalc expression evaluated in the same context as the assignee expression.
- **Priority.** A whole number; higher is more urgent, as for job priorities. A task without a priority has none; there is no default.
- **Failures never stop the process.** A result that cannot be used leaves the value empty and logs a warning that names the node, the instance, and the reason. A null result means no value and logs nothing.
- **Evaluated once.** Claims, assignment, role management, delegation, activation of a pending item, administrative actions, and version changes keep the saved values. Reactivation creates a new work item, which evaluates again.
- **Multi-instance.** The execution evaluates both once when it starts, and every child gets the same values. A sequential child's duration therefore counts from the start of the execution, not from its own activation.
- **Due status.** `overdue` once the due time has passed, `dueSoon` within the `UserTasks.DueSoonThreshold` window, `upcoming` otherwise. Only open work items (active or pending) with a due time have one.
- **Surfaces.** My work gets a due column with badges, sorting, and filters; Overview's My work list shows the badge; task detail and Task management show both values. Task management and task distribution do not sort or filter by them yet.

### 2. Workflow JSON and model

A `userTask` may add:

| Property | Rules |
| --- | --- |
| `dueDate` | Trimmed; blank means none. A fixed ISO-8601 duration such as `P2D`, `PT4H`, or `P1W`, with the grammar of a timer's `timeDuration`, counted from when the work item is created. A fixed date-time with `Z` or an offset, with the grammar of a timer's `timeDate`. Otherwise an NCalc expression whose text result is one of those forms, such as `if(amount >= 10000, 'PT4H', 'P2D')` or `contractEnd`. |
| `priority` | Trimmed; blank means none. An NCalc expression whose result is a whole number from -2,147,483,648 to 2,147,483,647, such as `if(amount >= 10000, 80, 40)` or `50`. |

How results are used:

- A due-date duration adds to the creation time. A date-time is used as is; a time already past makes the task overdue at once. Null means no due date. Any other result, such as a date without a time, a date-time without an offset, months or years, a duration that is not positive, a number, or a failed evaluation, leaves no due date and logs a warning.
- A priority must be a finite number with no fractional part, so `2.0` is accepted and `2.5` is not. Null means no priority. Text, booleans, numbers out of range, and failed evaluations leave no priority and log a warning.

```json
{
  "id": 2,
  "type": "userTask",
  "name": "Review purchase",
  "roles": ["Reviewer"],
  "dueDate": "if(amount >= 10000, 'PT4H', 'P2D')",
  "priority": "if(amount >= 10000, 80, 40)"
}
```

- `FlowNodeModel` gains `DueDateExpression` (`dueDate`) and `PriorityExpression` (`priority`), each omitted when null, so older documents serialize exactly as before. They belong to the node, not to `multiInstance`, which version changes compare as a whole.
- `UserTaskDueRules` holds the fixed-value parsing, which reuses the timer rules, and the literal checks shared by validation and runtime.
- `WorkflowModelMigrator` trims both on user tasks, turns blanks into null, and clears both on other node types. Definition validation rejects them on other node types before that.
- The editor loader keeps both on user tasks. The authoring schema is generated from the model.

### 3. Engine, persistence, and migrations

- **Evaluation.** `WorkflowEngineService.ToSnapshot` resolves both values where it resolves the assignee, but also for a multi-instance execution, at the same point where it captures the execution's role policy. A token that only enters a multi-instance task does not evaluate them, so each execution evaluates once.
- **Saving.** The snapshot carries the due value, either an instant or a duration still to be added, and the priority. The repository saves them when it creates a normal work item and when it inserts the children of a multi-instance execution, and adds a duration to the same timestamp it records as `CreatedAt`. All children, including pending sequential ones, are inserted when the execution starts. A normal `asyncBefore` task's work item is created, pending, when the token enters the task, so its duration counts from then; a multi-instance `asyncBefore` task starts its execution after its job.
- **Migration `AddUserTaskDueDateAndPriority`.** It adds nullable `user_tasks."DueAt"` (`timestamptz`) and `"Priority"` (`integer`) without backfill, adds the `(Status, DueAt, Id)` index, and seeds `UserTasks.DueSoonThreshold` = `P1D` with a description when the setting is missing. Down drops the index and columns and removes the setting only if it is unchanged.
- **Version compatibility.** The node contracts that `WorkflowVersionCompatibilityEvaluator` compares leave out `dueDate` and `priority`, like `attributes`, so changing only these fields never blocks an in-place version change, also for a task with an open job. Open work items keep their saved values.
- **No lock-order or Worker change.** Evaluation runs in the transaction that creates the work item, under the existing locks, and the Worker creates work items through the same code. Apply the migration, then upgrade every API and Worker replica before authoring the fields: an older replica creates work items without the values, and an older API drops the properties when it saves a definition.

### 4. API contracts and authorization

`InboxItemDto`, `UserTaskDto`, and `ManagedUserTaskDto` add three properties, each omitted when null. `UserTaskDto` is returned by task detail, claim and unclaim, and an instance's task history; `ManagedUserTaskDto` by task management and task distribution.

| Property | Meaning |
| --- | --- |
| `dueAt` | The saved due time, in UTC. |
| `priority` | The saved priority. |
| `dueStatus` | `overdue` when `dueAt` is at or before the server time, `dueSoon` when it is within `UserTasks.DueSoonThreshold` after it, otherwise `upcoming`. Computed when the response is built, only for active or pending work items with a due time. |

The inbox adds these filters:

| GET query | POST body | Meaning |
| --- | --- | --- |
| `dueStatus`, repeatable | `dueStatuses` | `overdue`, `dueSoon`, `upcoming`, or `none` (no due date), ignoring case. Several values match any of them. |
| `dueFrom` | `dueFrom` | Inclusive lower bound on `dueAt`. Tasks without a due date do not match. |
| `dueTo` | `dueTo` | Exclusive upper bound on `dueAt`. It must be later than `dueFrom`. |
| `minPriority`, `maxPriority` | same | Inclusive bounds on `priority`. Tasks without a priority do not match. The minimum must not exceed the maximum. |

- Filters combine with each other and with the existing filters, and run in PostgreSQL before visibility, one task per actor, counting, ordering, and paging. All children of a multi-instance execution have the same values, so a filter keeps or removes the whole execution and never changes which child represents it.
- New sort fields `dueAt` and `priority` put tasks without a value last in both directions, with the usual task-ID tie-breaker. The default order is unchanged.
- An unknown status, `dueFrom` not earlier than `dueTo`, or a minimum priority above the maximum returns `400`.
- The engine settings are not cached, so the threshold is read only when a request needs it: a returned task has a due time, or a `dueSoon` or `upcoming` filter is used. Requests without due dates cost no extra query.
- Authorization is unchanged; the values appear only on tasks the caller can already see.

### 5. Editor and Flowbit.Ui changes

Editor assets in `Flowbit/src/Flowbit.Ui/wwwroot/editor/`:

- **Inspector.** A user task, including a multi-instance one, has **Due date** and **Priority** fields after the assignee field, with a hint on the accepted forms. A blank field removes the property. Each field shows the same message as save validation as soon as its fixed value cannot be used. Changing the type away from user task removes both.
- **Save validation.** A new phase after the assignment settings reports these fields on other node types, values that are not text, a quoted literal due date that is not a duration or a date-time with an offset, and a whole-number priority outside the range. `FlowInfo` and gateway helper functions are rejected in both expressions, as in the assignee. NCalc syntax is checked by the server, as for the assignee. Existing documents produce the same errors as before.
- **Export.** Saved JSON trims both and omits them when blank or on other node types.

Flowbit.Ui:

- **`TaskDueDate`**, a shared component, shows the due time in the same format as other timestamps, with the exact value as its tooltip, and an **Overdue** or **Due soon** badge from the API's `dueStatus`. Flowbit.Ui keeps no clock or threshold of its own.
- **My work** gets a **Due** column, with the priority below the date, **Due date** and **Priority** sort options, and filters **Due** (any, overdue, due soon, upcoming, no due date), **Minimum priority**, and **Maximum priority**.
- **Overview's** My work list shows the badge; **task detail** shows **Due** and **Priority** when the task has them; **Task management** shows the same **Due** column, without sorting or filtering.

### 6. Documentation

- [Node and property reference](../node-reference.md#usertask): the two properties, how results are used, and multi-instance behavior.
- [Developer guide](../developer-guide.md#build-an-inbox-and-action-form): due status, sorting, and filters for custom worklists; that version changes keep saved values.
- [BPMN support](../bpmn-support.md#flowbit-extensions): due date and priority as a Flowbit extension.
- [API reference](../api-guide.md): the inbox parameters and request, the three response schemas, the node schema, and the sorting conventions.
- [Flowbit.Ui guide](../ui-guide.md#use-my-work-and-task-forms): the due column, badges, sorting, filters, task detail, Task management, and the editor fields.
- [Database schema](../database-schema.md#user_tasks), [engine settings](../engine-settings.md), and [deployment](../deployment.md#upgrade-and-compatibility-rules): the columns and index, the new setting, and the upgrade order.
- [AGENTS.md](../../AGENTS.md), the [runtime reference](../../Flowbit/README.md), the [root README](../../README.md), the [portable skill's authoring guide](../../authoring/flowbit-authoring/references/authoring-guide.md), a new example in the [example catalog](../../examples/README.md), and the [browser smoke suite](../../Flowbit/tests/Flowbit.BrowserTests/README.md).
- This page and the [backlog](README.md). When the item is done, the later-release scope that is still wanted becomes a new item: an "Overdue tasks" card on Overview, due-date and priority sorting and filters in Task management, and recomputation when a source variable changes. Working-time durations stay with [EVENT-08](event-08-business-calendars.md).

### 7. Tests and verification

- **Rules**: fixed values, quoted and whole-number literals, and how each kind of expression result is used, including overflow, null, and wrong types.
- **Model and migrator**: older JSON round-trips unchanged; the fields round-trip, trim, and are cleared on other node types.
- **Definition validation**: each rule; multi-instance tasks accepted.
- **Version compatibility**: changing only these fields keeps the contracts of normal and multi-instance tasks and open jobs.
- **API** (PostgreSQL): a duration counted exactly from `CreatedAt`; a date-time variable; failed and null results; parallel and sequential multi-instance children; a pending `asyncBefore` task; reactivation; the fields on every response listed in section 4; `dueStatus` with the default and a custom threshold, and none on completed tasks; sorting in both directions with tasks without values last; filters, GET and POST parity, and `400` responses; unchanged query counts for tasks without due dates; the index, the seeded setting, the migration snapshot, and the OpenAPI documentation checks.
- **Editor (Jint)**: loading, inspector editing and clearing, type changes, export, and validation parity; the pinned characterization arrays unchanged; every example still valid.
- **Flowbit.Ui**: the component's dates and badges; the My work sort options, filters, and request.
- **Browser smoke**: R10 at 1440×900 and 390×844 shows overdue, due-soon, upcoming, and unscheduled tasks in My work, sorts them by due date and priority, filters overdue work, and opens task detail. E10 at 1440×900 and 1024×768 edits the fields, checks that a malformed quoted due date blocks the save, saves, and reloads.
- [Real-browser verification](../../AGENTS.md#mandatory-real-browser-ui-verification) on a local stack at desktop and phone widths, with the console checked.
