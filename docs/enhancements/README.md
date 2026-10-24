# Enhancement backlog

[Documentation home](../index.md) · [BPMN support](../bpmn-support.md) · [Flowbit.Ui guide](../ui-guide.md)

This backlog records candidate new task types, events, integrations, and Flowbit.Ui features. The items come from a review on 2026-10-24 of the [supported node types](../bpmn-support.md#supported-node-types), the [unsupported capabilities](../bpmn-support.md#unsupported-capabilities), and every Flowbit.Ui page. A second review the same day reordered the work toward everyday usability, split combined items, corrected effort estimates, and gave every item its dependencies and first release acceptance criteria. No open item is implemented or scheduled.

Each item has its own page, described in [Item pages](#item-pages).

## Status values

| Status | Meaning |
| --- | --- |
| Not started | The idea is recorded; no plan yet. |
| Planned | The item's **Plan** section is written and agreed. |
| In progress | Implementation has started. |
| Done | The first release acceptance criteria are met, and the change is tested and documented as [AGENTS.md](../../AGENTS.md#mandatory-documentation-maintenance) requires. |

When an item's status changes, update its page and its row below in the same change. When an item is Done, record any later-release work that is still wanted as a new item.

## Effort values

| Effort | Meaning |
| --- | --- |
| S | A contained change in one layer, usually without a migration. |
| M | Spans the engine, API, and UI, or needs a migration. |
| L | A new runtime concept affecting lifecycle, versioning, recovery, and compatibility rules. |

Effort estimates an item's full scope, including later releases; a first release is usually smaller. Estimates are relative and made before planning, and a plan may revise them.

## Recommended order

The order favors improvements people feel in everyday work over new runtime capabilities. Each step is the item's first release.

1. [UI-01 Live instance diagram](ui-01-live-instance-diagram.md): current positions, waiting work, and links, without history.
2. [UI-03 Rich task forms](ui-03-rich-task-forms.md): input controls chosen by data type first, then labels, help text, and fixed options.
3. [UI-04 Task due date and priority](ui-04-task-due-date-and-priority.md)
4. [UI-05 Live refresh](ui-05-live-refresh.md): change signals only, independent of webhooks.
5. [UI-07 Version compare](ui-07-version-compare.md)
6. [INT-01 Outbound lifecycle webhooks](int-01-outbound-lifecycle-webhooks.md)

### Next tier

Deadline and timer follow-ups, in this order:

1. [EVENT-01 Expression timers](event-01-expression-timers.md)
2. [UI-12 Task escalation](ui-12-task-escalation.md)
3. [EVENT-08 Business calendars](event-08-business-calendars.md) and [EVENT-09 Cron schedules for timer starts](event-09-cron-schedules.md). Move calendars up if your SLAs are counted in working days.

Conditional items:

- [EVENT-02 Message correlation by business key](event-02-message-correlation-by-business-key.md): bring it forward if most adopters embed Flowbit over HTTP rather than using Flowbit.Ui.
- [TASK-01 Call activity](task-01-call-activity.md) and [TASK-02 External task](task-02-external-task.md): schedule them when a concrete reuse or integration case justifies their lifecycle complexity.

Other items are not ranked yet.

## New task types

| ID | Item | Effort | Depends on | Status |
| --- | --- | --- | --- | --- |
| [TASK-01](task-01-call-activity.md) | Call activity (reusable subprocess) | L | None | Not started |
| [TASK-02](task-02-external-task.md) | External task for pull-based workers | L | None | Not started |
| [TASK-03](task-03-business-rule-task-decision-table.md) | Business rule task with decision tables | M | None | Not started |
| [TASK-04](task-04-email-and-chat-send.md) | Email and chat send connectors | M | None | Not started |
| [TASK-05](task-05-multi-instance-automatic-activities.md) | Multi-instance service, script, and call activities | M–L | None | Not started |

## New events and gateways

| ID | Item | Effort | Depends on | Status |
| --- | --- | --- | --- | --- |
| [EVENT-01](event-01-expression-timers.md) | Expression timers | M | None | Not started |
| [EVENT-02](event-02-message-correlation-by-business-key.md) | Message correlation by business key | M | None | Not started |
| [EVENT-03](event-03-event-based-gateway.md) | Event-based gateway | M | None | Not started |
| [EVENT-04](event-04-signal-events.md) | Signal events | M–L | None | Not started |
| [EVENT-05](event-05-error-code-boundaries.md) | Error-code matching on error boundaries | M | None | Not started |
| [EVENT-06](event-06-link-events.md) | Link events | M | None | Not started |
| [EVENT-07](event-07-compensation.md) | Compensation | L | None | Not started |
| [EVENT-08](event-08-business-calendars.md) | Business calendars | M–L | [UI-04](ui-04-task-due-date-and-priority.md) | Not started |
| [EVENT-09](event-09-cron-schedules.md) | Cron schedules for timer starts | M | None | Not started |

## Integration

| ID | Item | Effort | Depends on | Status |
| --- | --- | --- | --- | --- |
| [INT-01](int-01-outbound-lifecycle-webhooks.md) | Outbound lifecycle webhooks | M | None | Not started |

## Flowbit.Ui and human work

| ID | Item | Effort | Depends on | Status |
| --- | --- | --- | --- | --- |
| [UI-01](ui-01-live-instance-diagram.md) | Live instance diagram | M | None | Not started |
| [UI-02](ui-02-process-analytics-heatmap.md) | Process analytics and heatmap | M | [UI-01](ui-01-live-instance-diagram.md) | Not started |
| [UI-03](ui-03-rich-task-forms.md) | Rich task forms | M | None | Not started |
| [UI-04](ui-04-task-due-date-and-priority.md) | Task due date and priority | M | None | Not started |
| [UI-05](ui-05-live-refresh.md) | Live refresh | M | None | Not started |
| [UI-06](ui-06-comments-and-attachments.md) | Comments and attachments | M–L | None | Not started |
| [UI-07](ui-07-version-compare.md) | Version compare | M | [UI-01](ui-01-live-instance-diagram.md) | Not started |
| [UI-08](ui-08-editor-test-run.md) | Test run in the editor | L | None | Not started |
| [UI-09](ui-09-saved-views-csv-export.md) | Saved views and CSV export | S | None | Not started |
| [UI-10](ui-10-bulk-task-actions.md) | Bulk task actions in My work | S–M | None | Not started |
| [UI-11](ui-11-localization-rtl.md) | Localization and right-to-left layout | M | None | Not started |
| [UI-12](ui-12-task-escalation.md) | Task escalation | M–L | [UI-04](ui-04-task-due-date-and-priority.md) | Not started |
| [UI-13](ui-13-notification-center.md) | Notification center | M | [INT-01](int-01-outbound-lifecycle-webhooks.md) | Not started |

## Documentation housekeeping

| ID | Item | Effort | Depends on | Status |
| --- | --- | --- | --- | --- |
| [DOC-01](doc-01-stale-refactoring-links.md) | Stale links to the removed `docs/refactoring` pages | S | None | Done |

## Item pages

Each item page starts with a field table:

| Field | Meaning |
| --- | --- |
| Status | One of the [status values](#status-values). |
| Category | The kind of change. |
| Effort | One of the [effort values](#effort-values), for the item's full scope. |
| Priority | The item's place in the [recommended order](#recommended-order), its tier, or **Not ranked**. |
| Depends on | Items whose first release must ship before this item's first release can. |
| Related | Optional connections that are not prerequisites. |

The sections that follow are:

- **Summary** and **Why it adds value**.
- **First release acceptance criteria**: a checklist that decides when the item is Done. The criteria are proposals; a plan may revise them.
- **Later releases**: scope deliberately left out of the first release.
- **Current state**: the evidence, with its verification date, for the item's status.
- **Builds on**: existing code and contracts the item can reuse.
- **Open questions** and **Plan**.

## Writing a plan

Before implementing an item, replace the **Plan** placeholder on its page with:

1. Scope and non-goals: confirm or revise the first release acceptance criteria, and decide each open question.
2. Workflow JSON and model changes, with compatibility for older documents in the editor loader and `WorkflowModelMigrator`.
3. Engine, persistence, and migration changes, including lock order, Worker behavior, and version-switch compatibility.
4. API contracts and authorization.
5. Editor and Flowbit.Ui changes.
6. Documentation to update, chosen from the [ownership map](../../AGENTS.md#mandatory-documentation-maintenance).
7. Tests and verification, including [real-browser verification](../../AGENTS.md#mandatory-real-browser-ui-verification) for UI changes.

Skip the parts that do not apply to the item. Then set its status to **Planned** on the item page and in its table row.

## Adding an item

Give a new item the next ID in its category, follow the structure in [Item pages](#item-pages), and add its row to the matching table above.
