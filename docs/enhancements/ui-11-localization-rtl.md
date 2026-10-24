# UI-11: Localization and right-to-left layout

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Flowbit.Ui feature |
| Effort | M |
| Priority | Not ranked |
| Depends on | None |
| Related | [UI-03](ui-03-rich-task-forms.md), [TASK-04](task-04-email-and-chat-send.md) |

## Summary

Translatable text for Flowbit.Ui and the editor, starting with English and Arabic, and a right-to-left layout.

## Why it adds value

- Arabic-speaking users can work in their own language. The AI assistant's PDF extraction already supports Arabic OCR.

## First release acceptance criteria

- [ ] The Flowbit.Ui shell, My work, task detail, and start pages are translatable, with complete English and Arabic text.
- [ ] Users can choose the language, and Arabic switches those pages to a right-to-left layout without visual breakage, verified in a real browser.
- [ ] Dates and numbers display in the chosen culture, while submitted values keep their current invariant formats.

## Later releases

- Administration pages and the editor.
- Translated workflow content, such as node names and flow labels.

## Current state

Verified on 2026-10-24: not implemented.

- No `IStringLocalizer`, `AddLocalization`, or `.resx` resources exist under `Flowbit/src`; UI text is written directly in English.
- PDF extraction supports English and Arabic OCR ([AI workflow assistant](../ui-guide.md#ai-workflow-assistant)).

## Builds on

- ASP.NET Core and Blazor localization.
- The editor's scoped CSS for right-to-left adjustments.

## Open questions

- Language selection: browser setting, user profile attribute, or deployment setting?
- A string table for the editor's dependency-free assets.

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
