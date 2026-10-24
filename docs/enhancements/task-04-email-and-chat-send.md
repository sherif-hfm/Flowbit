# TASK-04: Email and chat send connectors

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New task type |
| Effort | S–M |
| Priority | Recommended order #5, with [UI-05](ui-05-notification-center-live-refresh.md) |
| Related | [UI-05](ui-05-notification-center-live-refresh.md), [INT-01](int-01-outbound-lifecycle-webhooks.md), [UI-11](ui-11-localization-rtl.md) |

## Summary

New connectors that send notifications from a workflow: `service.type: "email"` over SMTP, and chat webhooks such as Microsoft Teams or Slack. A dedicated `sendTask` node type is the alternative. Subject, body, and recipients use `${var}` templates and expressions.

## Why it adds value

- Almost every business process notifies people, and today each deployment needs its own REST endpoint for a service task to call.

## Current state

Verified on 2026-10-24: not implemented.

- `serviceTask` supports only `service.type: "rest"` ([WorkflowModel.cs](../../Flowbit/src/Flowbit.Shared/Models/WorkflowModel.cs)).
- No email, SMTP, or notification code exists under `Flowbit/src`.
- [Unsupported capabilities](../bpmn-support.md#unsupported-capabilities) lists non-REST service connectors.

## Builds on

- The `service.type` discriminator, kept so new connectors do not change the node shape.
- `ServiceTaskTemplating` for `${var}` substitution.
- Asynchronous execution with retries and incidents.
- `${config.*}` and `${setting.*}` templates for credentials.

## Open questions

- Recipients: email addresses held in variables, or user IDs resolved through the Local identity directory or OIDC claims?
- Should sends require `asyncBefore`, so SMTP latency never holds a workflow transaction?
- HTML or plain-text templates, attachments ([UI-06](ui-06-comments-and-attachments.md)), and localized templates ([UI-11](ui-11-localization-rtl.md)).
- Should workflow sends and the system notifications in [UI-05](ui-05-notification-center-live-refresh.md) share one delivery pipeline?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
