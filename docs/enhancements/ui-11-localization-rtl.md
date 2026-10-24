# UI-11: Localization and right-to-left layout

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Flowbit.Ui feature |
| Effort | M |
| Priority | Not ranked |
| Related | [UI-03](ui-03-rich-task-forms.md), [TASK-04](task-04-email-and-chat-send.md) |

## Summary

Translatable text for Flowbit.Ui and the editor, starting with English and Arabic, and a right-to-left layout.

## Why it adds value

- Arabic-speaking users can work in their own language. The AI assistant's PDF extraction already supports Arabic OCR.

## Current state

Verified on 2026-10-24: not implemented.

- No `IStringLocalizer`, `AddLocalization`, or `.resx` resources exist under `Flowbit/src`; UI text is written directly in English.
- PDF extraction supports English and Arabic OCR ([AI workflow assistant](../ui-guide.md#ai-workflow-assistant)).

## Builds on

- ASP.NET Core and Blazor localization.
- The editor's scoped CSS for right-to-left adjustments.

## Open questions

- Language selection: browser setting, user profile attribute, or a deployment setting?
- The editor's dependency-free assets need their own string table.
- Is translating workflow content, such as node names and flow labels, in scope?
- Date and number formats in typed inputs.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
