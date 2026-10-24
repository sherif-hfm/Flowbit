# TASK-04: Email and chat send connectors

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | New task type |
| Effort | M |
| Priority | Not ranked |
| Depends on | None |
| Related | [UI-13](ui-13-notification-center.md), [INT-01](int-01-outbound-lifecycle-webhooks.md), [UI-06](ui-06-comments-and-attachments.md), [UI-11](ui-11-localization-rtl.md) |

## Summary

New connectors that send notifications from a workflow: `service.type: "email"` over SMTP, and chat webhooks such as Microsoft Teams or Slack. A dedicated `sendTask` node type is the alternative. Subject, body, and recipients use `${var}` templates and expressions.

## Why it adds value

- Almost every business process notifies people, and today each deployment needs its own REST endpoint for a service task to call.

## First release acceptance criteria

- [ ] `service.type: "email"` sends a plain-text or HTML email over SMTP, through a server configured by the host; credentials never appear in the definition.
- [ ] Subject, body, and recipient addresses support `${var}` templates.
- [ ] Email tasks require `asyncBefore` and use the existing retries and incidents, so SMTP latency never holds a workflow transaction.
- [ ] Definition validation, in the editor and the API, rejects a missing recipient, subject, or body.
- [ ] The editor offers the email connector and its fields.

## Later releases

- Microsoft Teams and Slack webhooks.
- Recipients resolved from user IDs.
- Attachments ([UI-06](ui-06-comments-and-attachments.md)) and localized templates ([UI-11](ui-11-localization-rtl.md)).

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

- Recipients beyond plain addresses: user IDs resolved through the Local identity directory or OIDC claims?
- Should workflow email and [UI-13](ui-13-notification-center.md) notification email share one delivery pipeline?
- How to avoid duplicate emails when a send is retried after an unknown SMTP outcome.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
