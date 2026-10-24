# UI-05: Live refresh

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started |
| Category | Flowbit.Ui feature (with API changes) |
| Effort | M |
| Priority | Recommended order #4 |
| Depends on | None |
| Related | [UI-13](ui-13-notification-center.md), [INT-01](int-01-outbound-lifecycle-webhooks.md) |

## Summary

After a commit that changes tasks, instances, or jobs, the API emits a change signal, and open Flowbit.Ui pages reload their data through their normal authorized API calls. The signal carries no data, so authorization stays in one place. Stored notifications are a separate item ([UI-13](ui-13-notification-center.md)).

## Why it adds value

- Users see new work and status changes without reloading pages.
- No second authorization path is needed, because no data is pushed.

## First release acceptance criteria

- [ ] Commits by any API replica or the Worker that change tasks, instances, or jobs emit a change signal through PostgreSQL `NOTIFY`, and Flowbit.Ui receives it over a push connection.
- [ ] My work, instance detail, and Jobs & incidents reload through their normal API calls when a signal arrives; no data is pushed.
- [ ] Signals carry no identifiers or values.
- [ ] Reloads are throttled, so a burst of changes causes at most one reload per page per interval.
- [ ] Pages keep working, with manual refresh, when the push connection drops.

## Later releases

- Signals targeted at affected users, if that can be done without revealing information.
- Signals for applications that embed Flowbit.

## Current state

Verified on 2026-10-24: not implemented.

- No push channel from the API to the UI exists under `Flowbit/src`.
- [Find your way around](../ui-guide.md#find-your-way-around) asks users to refresh after another actor or the Worker changes state, and [Jobs & incidents](../ui-guide.md#monitor-jobs-and-incidents) does not poll.
- The administrative-actions batch page polls every three seconds while a batch is active.

## Builds on

- PostgreSQL `LISTEN`/`NOTIFY`: a notification is delivered only when its transaction commits, and a missed signal is harmless because pages reload anyway.
- The Blazor Server circuit for updating open pages.
- The AI assistant's streamed responses as a precedent for streaming from the API.
- Note: the UI calls the API only over HTTP, so signals must reach it through the API.

## Open questions

- Transport from the API to the UI: server-sent events, long polling, or SignalR?
- The throttle interval, and which pages subscribe.
- With content-free signals, every open page reloads on every change. At what scale does that need coarse scoping, for example by workflow family, and what would such scoping reveal?

## Plan

_Not written yet._ Follow [Writing a plan](README.md#writing-a-plan).
