# Enhancement backlog

[Documentation home](../index.md) · [BPMN support](../bpmn-support.md) · [Flowbit.Ui guide](../ui-guide.md)

This backlog records candidate new task types, events, integrations, and Flowbit.Ui features. The items come from a review on 2026-10-24 of the [supported node types](../bpmn-support.md#supported-node-types), the [unsupported capabilities](../bpmn-support.md#unsupported-capabilities), and every Flowbit.Ui page. None of them is implemented or scheduled.

Each item has its own page with a summary, the evidence for its current status, what it can reuse, open questions, and a **Plan** section to complete before implementation starts.

## Status values

| Status | Meaning |
| --- | --- |
| Not started | The idea is recorded; no plan yet. |
| Planned | The item's **Plan** section is written and agreed. |
| In progress | Implementation has started. |
| Done | Implemented, tested, and documented as [AGENTS.md](../../AGENTS.md#mandatory-documentation-maintenance) requires. |

When an item's status changes, update its page and its row below in the same change.

## Effort values

| Effort | Meaning |
| --- | --- |
| S | A contained change in one layer, usually without a migration. |
| M | Spans the engine, API, and UI, or needs a migration. |
| L | A new runtime concept affecting lifecycle, versioning, recovery, and compatibility rules. |

Efforts are relative estimates made before planning; a plan may revise them.

## Recommended order

1. [UI-01 Live instance diagram](ui-01-live-instance-diagram.md)
2. [EVENT-01 Expression timers and business calendars](event-01-expression-timers-business-calendars.md) together with [UI-04 Task due date and priority](ui-04-task-due-date-and-priority.md)
3. [TASK-01 Call activity](task-01-call-activity.md)
4. [TASK-02 External task](task-02-external-task.md)
5. [UI-05 Notification center and live refresh](ui-05-notification-center-live-refresh.md) together with [TASK-04 Email and chat send](task-04-email-and-chat-send.md)

## New task types

| ID | Item | Effort | Status |
| --- | --- | --- | --- |
| [TASK-01](task-01-call-activity.md) | Call activity (reusable subprocess) | L | Not started |
| [TASK-02](task-02-external-task.md) | External task for pull-based workers | M | Not started |
| [TASK-03](task-03-business-rule-task-decision-table.md) | Business rule task with decision tables | M | Not started |
| [TASK-04](task-04-email-and-chat-send.md) | Email and chat send connectors | S–M | Not started |
| [TASK-05](task-05-multi-instance-automatic-activities.md) | Multi-instance service, script, and call activities | M–L | Not started |

## New events and gateways

| ID | Item | Effort | Status |
| --- | --- | --- | --- |
| [EVENT-01](event-01-expression-timers-business-calendars.md) | Expression timers, cron, and business calendars | M | Not started |
| [EVENT-02](event-02-message-correlation-by-business-key.md) | Message correlation by business key | M | Not started |
| [EVENT-03](event-03-event-based-gateway.md) | Event-based gateway | M | Not started |
| [EVENT-04](event-04-signal-events.md) | Signal events | M–L | Not started |
| [EVENT-05](event-05-error-code-boundaries.md) | Error-code matching on error boundaries | S | Not started |
| [EVENT-06](event-06-link-events.md) | Link events | S | Not started |
| [EVENT-07](event-07-compensation.md) | Compensation | L | Not started |

## Integration

| ID | Item | Effort | Status |
| --- | --- | --- | --- |
| [INT-01](int-01-outbound-lifecycle-webhooks.md) | Outbound lifecycle webhooks | M | Not started |

## Flowbit.Ui features

| ID | Item | Effort | Status |
| --- | --- | --- | --- |
| [UI-01](ui-01-live-instance-diagram.md) | Live instance diagram | M | Not started |
| [UI-02](ui-02-process-analytics-heatmap.md) | Process analytics and heatmap | M | Not started |
| [UI-03](ui-03-rich-task-forms.md) | Rich task forms | M | Not started |
| [UI-04](ui-04-task-due-date-and-priority.md) | Task due date and priority | M | Not started |
| [UI-05](ui-05-notification-center-live-refresh.md) | Notification center and live refresh | M | Not started |
| [UI-06](ui-06-comments-and-attachments.md) | Comments and attachments | M–L | Not started |
| [UI-07](ui-07-version-compare.md) | Version compare | S–M | Not started |
| [UI-08](ui-08-editor-test-run.md) | Test run in the editor | L | Not started |
| [UI-09](ui-09-saved-views-csv-export.md) | Saved views and CSV export | S | Not started |
| [UI-10](ui-10-bulk-task-actions.md) | Bulk task actions in My work | S–M | Not started |
| [UI-11](ui-11-localization-rtl.md) | Localization and right-to-left layout | M | Not started |

## Documentation housekeeping

| ID | Item | Effort | Status |
| --- | --- | --- | --- |
| [DOC-01](doc-01-stale-refactoring-links.md) | Stale links to the removed `docs/refactoring` pages | S | Done |

## Writing a plan

Before implementing an item, replace the **Plan** placeholder on its page with:

1. Scope and non-goals, including how each open question is decided.
2. Workflow JSON and model changes, with compatibility for older documents in the editor loader and `WorkflowModelMigrator`.
3. Engine, persistence, and migration changes, including lock order, Worker behavior, and version-switch compatibility.
4. API contracts and authorization.
5. Editor and Flowbit.Ui changes.
6. Documentation to update, chosen from the [ownership map](../../AGENTS.md#mandatory-documentation-maintenance).
7. Tests and verification, including [real-browser verification](../../AGENTS.md#mandatory-real-browser-ui-verification) for UI changes.

Skip the parts that do not apply to the item. Then set its status to **Planned** on the item page and in its table row.

## Adding an item

Give a new item the next ID in its category, copy the section structure of an existing page (field table, Summary, Why it adds value, Current state, Builds on, Open questions, Plan), and add its row to the matching table above.
