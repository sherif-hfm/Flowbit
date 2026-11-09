# EVENT-09: Cron schedules for timer starts

[Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Done |
| Category | New event capability |
| Effort | M |
| Priority | Next tier: deadline and timer follow-up 3 |
| Depends on | None |
| Related | [EVENT-08](event-08-business-calendars.md), [EVENT-01](event-01-expression-timers.md) |

## Summary

Add `timerStartEvent` schedules written as cron expressions with an explicit IANA time zone, such as "every weekday at 08:00 in Europe/London". Reuse the existing durable timer-start subscription and Worker queue.

## Why it adds value

- Business schedules such as weekday mornings or month-ends cannot be written as fixed ISO repeating intervals, which cannot skip weekends or follow calendar months.
- A named time zone keeps a business schedule tied to local time as its UTC offset changes.

## First release acceptance criteria

These criteria are revised by this plan on 2026-11-08 and 2026-11-09; the second revision adds the database guard against completing a cron schedule. Upcoming-occurrence preview is a hosted-editor feature in the first release. The standalone editor remains usable from a local file, validates the supported grammar, and explains that authoritative time-zone validation and preview require Flowbit.Ui/API.

- [x] `timerStartEvent` accepts exactly one of `timeDate`, `timeDuration`, `timeCycle`, or `cron`; `cron` requires `timeZone` and uses the five-field dialect below.
- [x] API and editor validation reject unsupported syntax, impossible calendar-date combinations, multiple schedule values, misplaced time zones, and cron on intermediate or boundary timers. The API additionally verifies that its host resolves the exact IANA zone.
- [x] First and subsequent occurrences obey the documented activation, daylight-saving, and strictly-more-than-one-minute misfire rules without replaying a downtime backlog.
- [x] A committed UTC due time survives restarts, retries, default reconciliation, and time-zone-data updates. Default-version changes retire obsolete starts under the existing fences.
- [x] Concurrent Workers, lease recovery, and duplicate job delivery cannot commit a second instance for the same subscription occurrence.
- [x] The database refuses to complete a cron subscription, so neither a defect nor a not-yet-upgraded Worker can silently end a schedule after one firing.
- [x] A workflow-authorized, read-only API previews bounded upcoming occurrences. The hosted editor shows the next five in the schedule's time zone and UTC, with stale-response protection.
- [x] Existing timer JSON, expression timers, business calendars, finite cycles, and dependency-free standalone editing retain their behavior.
- [x] Migration, recovery, API/OpenAPI, editor, documentation, examples, and real-browser verification are complete before this item is marked Done.

## Later releases

- Cron schedules on intermediate and boundary timers.
- Skipping holidays from a business calendar ([EVENT-08](event-08-business-calendars.md)).
- Offline occurrence preview, additional cron syntax, configurable daylight-saving or misfire policies, and finite cron repetition counts.
- User-defined schedule suspension, overlap prevention, jitter, and manual backfill. Existing workflow-version and Operations actions remain the available controls.
- Giving a cron-started instance its scheduled occurrence: the UTC instant and the local date and time in the schedule's time zone. `sys.now` and `sys.today` are UTC, so a summer 00:30 Europe/London run, or any 08:00 Asia/Tokyo run, sees the previous date.
- Showing a published default's next scheduled start on the Workflows page. Until then it appears as the family's `timerStart` job in Operations.

## Current state

Verified on 2026-11-09: done. The first release acceptance criteria are met.

- **Model and rules.** `TimerDefinitionModel.Cron` and `TimeZone` ([WorkflowModel.cs](../../Flowbit/src/Flowbit.Shared/Models/WorkflowModel.cs)) carry the schedule, and `CountConfiguredExpressions` counts cron as a fourth schedule. [CronScheduleRules.cs](../../Flowbit/src/Flowbit.Shared/Models/CronScheduleRules.cs) parses the five-field dialect into the membership sets the calculator must match and words each problem. `WorkflowDefinitionValidator` adds the timer-start, zone, and misplaced-setting rules, and the editor's save validator gives the same messages. `WorkflowModelMigrator` trims both properties and leaves older documents unchanged.
- **Calculation.** [CronTimerSchedule.cs](../../Flowbit/src/Flowbit.Service/Services/CronTimerSchedule.cs) adapts Cronos 0.13.0, pinned in Flowbit.Service only, behind that grammar. `IanaTimeZones` resolves exact-case IANA ids for cron and business calendars alike. `WorkflowTimerSchedule` resolves the first and next cron occurrences with the one-minute grace and the 36,525-day horizon, and `TimerScheduleKinds.IsRecurring` replaces the checks that knew only `timeCycle`.
- **Persistence and runtime.** Migration `AddCronTimerSchedules` adds `timer_subscriptions."ScheduleTimeZone"`, the `cron` kind, `CK_timer_subscriptions_cron_shape`, and `CK_timer_subscriptions_cron_not_completed`. It adds the constraints `NOT VALID` in one transaction. `ValidateCronTimerScheduleConstraints` then validates each outside a transaction and can be applied again if it stops, and the down migration of `AddCronTimerSchedules` refuses while any cron row remains. The Worker's reconciliation saves the expression and zone and schedules the first occurrence strictly after default activation. Firing commits the start, the successor, and the job completion together, and a successor that cannot be calculated rolls the start back. `WorkflowVersionCompatibilityEvaluator` compares the kind, expression, and zone.
- **API and UI.** `POST /api/workflows/timer-schedules/preview` and `GET /api/workflows/timer-schedules/time-zones` read nothing persistent and need `Workflow.RequiredRole`. A timer start's **Schedule** offers **Cron schedule**, with expression and zone fields, live validation, the both-days note, and a zone hint from the browser's own data. Flowbit.Ui adds the server's zone suggestions and **Preview next 5**, which keeps only the answer for the schedule, node, and document on screen; the standalone editor says that it cannot preview. Examples [timers/10](../../examples/timers/10-weekday-morning-start.json) and [timers/11](../../examples/timers/11-month-end-close-start.json), the owning guides, and the portable authoring guide describe the syntax and its limits.
- **Tests.** New: `CronScheduleRulesTests`, `CronTimerScheduleTests` (real London and New York daylight-saving vectors, synthetic 30-minute and gap zones, leap years, the horizon), `CronTimerDefinitionTests`, `CronTimerStartRuntimeTests` (activation, the exact one-minute grace, backlog skipping, restarts and missing-job repair, an unresolvable zone's incident and retry, successor rollback, the completion and shape constraints, default switches, concurrent reconcilers, and a stale duplicate delivery), `TimerSchedulePreviewApiTests`, `EditorCronScheduleTests`, `WorkflowEditorHostTimerScheduleTests`, and `CronTimerScheduleMigrationTests` (upgrade over existing timers, every constraint validated, a stopped validation finished by applying it again, NULL-safe rejections, and the refused, then successful, down migration). The example-catalog and AI docs-view counts were updated for the two examples.
- **Full suite.** `dotnet test Flowbit/Flowbit.slnx` ran 4,470 tests: 4,423 passed and 47 failed, all in the administrative-action, call-activity, and conditional-boundary runtime classes. The same command at the base commit failed 46 of 4,248 tests in the same classes. Every failing class passes when run alone, and the call-activity, conditional-boundary, cron runtime, and preview API classes pass together (223 tests), so the new runtime tests do not cause them. After the last changes, the editor, host, and cron classes (945 tests) and both migration tests passed.
- **Browser smoke.** The full suite passed 110 of 115 cases. E21 failed at both sizes on its own locator, which also matched the help text; with the locator fixed it passed at 1440×900 and 1024×768. R6 at 1024×768, R12 at 1440×900, and the local-login case failed only under the full run's load and passed when run again alone. H15 passed at 1440×900 and 1024×768, including its 390×844 check, and retained `hosted-cron-preview.png` and `hosted-cron-preview-narrow.png`.
- **Worker-enabled acceptance.** `CronScheduleAcceptanceTests` in the [acceptance suite](../../Flowbit/tests/Flowbit.BrowserAcceptanceTests/README.md) waits for real minute boundaries. A published `* * * * *` start in `Etc/UTC` queued its first whole minute after activation, which **Operations** showed, started one instance per minute, and queued each next minute from the nominal time. With the Worker stopped for 75 seconds past an occurrence, the job stayed queued; after the restart it completed without a start, and the schedule resumed and fired at the first minute after the restart. A second version, `0 9 1 1 *` in `Asia/Tokyo`, made the default over HTTP, retired the old schedule and queued midnight UTC on 1 January, and no later minute started an instance. With no Worker, a real preview answer that the proxy held past an expression edit, or past selecting another node and coming back, was discarded at 1440×900 and 1024×768. The first run's 1024×768 case failed only on the test's own count of preview requests, which included the previous case's; counted from the case's start, the rerun passed all three cases.
- **Linux images.** The root `compose.yaml`, built as a separate project on other ports, ran Ubuntu 24.04 API and Worker images with the IANA data in `/usr/share/zoneinfo`. The API listed 420 zones and previewed the London skipped and repeated hours, New York month-ends crossing from -04:00 to -05:00, Kolkata at +05:30, and Lord Howe's 30-minute change, where a skipped 02:15 ran at 02:30 +11:00; it refused `europe/london` with the casing message. A published every-minute start in `Europe/London` was fired by the Linux Worker at the next minute, which started one instance and queued the following minute.
- **Real-browser verification.** On a local published stack in the built-in Chromium browser at 1440×900, 1024×768, and 390×844, a new hosted workflow became a timer start with a cron schedule. Its previews showed the 29 March skipped 01:30 running at 02:00 +01:00, both offsets of the 25 October repeated hour for `*/30 1 * * *`, and month-ends in New York moving from -04:00 to -05:00. Invalid expressions and a missing zone showed the server's wording as they were typed and disabled the preview, and the server refused a miscased zone in the preview and when saving. Help opened from the keyboard, an example was inserted and undone in place, and editor undo and redo changed one edit at a time. Version 1 was stored with exactly `{"cron": "0 8 * * 1-5", "timeZone": "Europe/London"}`, and version 2 reopened with its expression and zone. In the standalone editor, the misplaced settings on a timer catch were removed, undone, and redone, and the standalone note, unknown-zone hint, and both-days note appeared. The console had no errors apart from reconnect attempts while the UI was republished.
- **Fixes from verification.** Pressing **Preview next 5** from the keyboard dropped focus to the page, because the button was disabled while the request ran. It now stays focusable, marked unavailable, and ignores presses until the answer arrives; the editor test and H15 check this. The deployment guide and the down migration's hint said that retiring cron versions lets the schema roll back, but retiring only cancels a subscription row, so both now say that only deleting a version removes its rows. A step holding a dash, such as `5/-` or `0/-5`, reached the server's number reader and threw, so saving or previewing it returned 500, while the editor read `5/-` as minute 5 alone. Both parsers now reject such an item as malformed, which the shared fixtures, a definition case, and a preview API case check. The editor also trimmed and split cron and its zone with JavaScript's white space, which includes the U+FEFF byte-order mark and omits U+0085. It therefore refused an expression ending in U+0085 that the server accepts, silently removed a trailing U+FEFF that the server refuses, and worded some problems differently. From import to the preview request it now uses the server's white space, which the shared fixtures, definition cases, and a normalization test check. A later review found three more gaps. `AddCronTimerSchedules` committed its schema change before validating outside the transaction, so a stopped validation left a migration that could not be applied again (`42701`); the validation is now the separate `ValidateCronTimerScheduleConstraints`, which can. The editor's loader dropped a timer from a node that is not a timer event, which the server refuses; the node now keeps it, save validation reports it, and the inspector offers its removal. A `cron` or `timeZone` that was not text, such as `["Etc/UTC"]`, looked valid in the inspector and was converted for the hosted preview; the field now shows the save validator's message and the preview never sends it.

## Builds on

- [WorkflowModel.cs](../../Flowbit/src/Flowbit.Shared/Models/WorkflowModel.cs), [WorkflowModelMigrator.cs](../../Flowbit/src/Flowbit.Shared/Models/WorkflowModelMigrator.cs), and [WorkflowDefinitionValidator.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowDefinitionValidator.cs): authored timer contracts, normalization, and definition validation.
- [WorkflowTimerSchedule.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowTimerSchedule.cs) and [WorkflowEngineService.Jobs.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.Jobs.cs): UTC scheduling, misfires, transaction boundaries, and job fences.
- [TimerStartReconciliationService.cs](../../Flowbit/src/Flowbit.Worker/TimerStartReconciliationService.cs) and [WorkflowDefinitionRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/WorkflowDefinitionRepository.cs): published-default activation and retirement.
- [JobModels.cs](../../Flowbit/src/Flowbit.Service/Models/JobModels.cs), [TimerSubscriptionRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/TimerSubscriptionRepository.cs), and [AppDbContext.cs](../../Flowbit/src/Flowbit.Infrastructure/Data/AppDbContext.cs): persisted descriptors, compare-and-swap advancement, and occurrence uniqueness.
- [BusinessCalendarTimeZones.cs](../../Flowbit/src/Flowbit.Service/Services/BusinessCalendarTimeZones.cs): existing host-resolvable, case-sensitive IANA validation. Its calendar-specific local-time conversion is not the cron calculator.
- [Canonical editor](../../Flowbit/src/Flowbit.Ui/wwwroot/editor/flowbit-editor.js), [host adapter](../../Flowbit/src/Flowbit.Ui/wwwroot/workflow-editor-host.js), and [WorkflowEditor.razor](../../Flowbit/src/Flowbit.Ui/Components/Pages/WorkflowEditor.razor): shared authoring and Blazor-owned API access.

## Open questions

No question remains open. The plan chooses the following first-release defaults, revised on 2026-11-09 after a review against the code and the Cronos parser; changing one requires updating its contract and tests before implementation:

- Five fields with numeric values of one or two ASCII digits, lists, ascending ranges, positive steps, and standalone day-of-month `L`; no seconds or year field. Day of week takes no open-ended `a/n` step, because the calculator continues such a step to 7, which it reads as Sunday.
- Day-of-month and day-of-week restrictions both have to match, as the calculator computes them. Because crontab runs when either matches, the inspector notes the rule whenever both are restricted.
- Explicit IANA `timeZone`, without a browser, server-local, or business-calendar default.
- Server calculation through a pinned Cronos dependency, behind Flowbit's narrower grammar and a small service-layer adapter.
- Cronos daylight-saving behavior, made explicit below; no selectable policy in workflow JSON.
- Hosted-only authoritative occurrence preview; local grammar checks in both editor modes. A browser time-zone check is only an inspector hint.
- `Occurrence` is a monotonic scheduling generation for cron, not a count of skipped calendar matches.
- The database refuses to complete a cron subscription. There is no cron-specific publication switch; the documented deployment order remains the guard against a released Worker's reconciliation failure.
- A started instance does not receive its scheduled occurrence in the first release. `sys.now` and `sys.today` stay UTC; the documentation says so, and [Later releases](#later-releases) records the follow-up.

## Plan

Written on 2026-11-08 following [Writing a plan](README.md#writing-a-plan), and revised on 2026-11-09 after a review against the code and the Cronos parser. It was implemented on 2026-11-09; [Current state](#current-state) records the result and its evidence. Effort remains **M** because the existing runtime lifecycle is reused, but the migration, server calculator, authoring bridge, and durable acceptance tests must ship together.

### 1. Scope and compatibility boundary

Support cron only on `timerStartEvent`. Keep existing start topology, required-input rules, system attribution, durable-publication gate, and published-default selection. A cron firing creates a fresh instance through the existing start path; previous instances may still be running. This release adds no overlap lock, input generator, new job kind, cron service, or external scheduler.

The schedule belongs to an immutable definition version and its current default activation. Saving a draft, previewing it, or publishing a non-default version does not activate its timer start. An unchanged default retains its activation; moving away and later returning creates a new activation under the existing rules. A running instance's version switch does not transfer or restart a family-level cron schedule.

Cron is a Flowbit scheduling extension. Do not present the new JSON property as a standard BPMN serialization, widen ISO duration parsing to calendar months/years, or alter expression-timer evaluation.

### 2. Workflow JSON and validation

This is a node fragment, not a complete runnable workflow:

```json
{
  "id": 1,
  "type": "timerStartEvent",
  "name": "Weekday morning review",
  "timer": {
    "cron": "0 8 * * 1-5",
    "timeZone": "Europe/London"
  }
}
```

| Property | Contract |
| --- | --- |
| `timer.cron` | Optional string; nonblank after trimming; at most 128 UTF-16 code units, matching the existing schedule-value limit. A fourth alternative in the exactly-one-schedule rule. |
| `timer.timeZone` | Required with cron; trimmed IANA identifier, at most 100 characters, with exact casing. `Etc/UTC` is the explicit UTC choice. Forbidden with another schedule kind or without cron. |
| `timer.calendar` | Existing duration-only contract. Reject it with cron, including when a calendar happens to use the same zone. |
| Existing timer properties | Keep their present formats, expression eligibility, and recurrence semantics. Empty optional strings normalize to absence under existing conventions. |

Add nullable `Cron` and `TimeZone` members to `TimerDefinitionModel`, omitted when null. Do not add a schema version or an expression-mode discriminator. Extend `CountConfiguredExpressions`, authored/normalized validation, and timer descriptor helpers explicitly; no fallback may interpret cron as `timeCycle`.

`WorkflowModelMigrator` trims the two values but does not infer a zone, convert cron into an interval, or rewrite the expression's operators. Preserve internal syntax: a range and an equivalent list may have different repeated-hour behavior. Validate forbidden combinations before normalization can remove evidence. Older documents without these fields must serialize and execute as before.

#### Supported dialect

The five positions are **minute, hour, day of month, month, day of week**. This deliberately restricted Flowbit dialect is enforced before the server calculator is invoked.

| Part | Accepted values |
| --- | --- |
| Numbers | Every value, range endpoint, and step is one or two ASCII digits, the most the calculator reads per number: `05` means 5, and `005` is rejected. |
| Minute / hour | Integers `0–59` / `0–23`. |
| Day of month / month | Integers `1–31` / `1–12`. |
| Day of week | Integers `0–6`, where `0` is Sunday and `1` is Monday. Reject `7` to keep one Sunday spelling. Steps use `*/n` or `a-b/n` only; reject an open-ended `a/n`. The calculator's field runs to 7, which it reads as Sunday, so it would read `1/2` as Monday, Wednesday, Friday, and Sunday, while a field ending at 6 omits Sunday. |
| Ordinary fields | A bare `*`, `*/n`, or a comma-separated list of numeric items. Each item is `a`, `a-b`, `a/n` (not in day of week), or `a-b/n`. Ranges are ascending; every endpoint fits the field. |
| Step | Positive integer bounded to `59`, `23`, `31`, `12`, or `6`, respectively, for the five fields. These limits stay within the calculator's accepted bounds. It selects values from the start through the range end; for `a/n`, the end is the field's maximum, which equals the calculator's for minute, hour, day of month, and month. It is a calendar-field selection, not elapsed-duration arithmetic. |
| Last day | Uppercase `L` as the entire day-of-month field. No `L` lists, offsets, ranges, or weekday variants. |
| Separators | Trim outer whitespace; fields are separated by one or more ASCII spaces or tabs. Reject newlines, empty list items, signs, decimals, and non-ASCII digits. |

Reject aliases such as `@daily`, named months/weekdays, reversed ranges, `?`, `W`, `#`, `H`, seconds, years, and NCalc/templates. Repeated list entries may collapse in parsed membership sets, but retain the authored text after trimming.

Day-of-month and day-of-week restrictions both have to match, as the calculator computes them. Document this with `0 8 13 * 5`: Friday the thirteenth at 08:00. Standard crontab instead runs when either field matches, so a pasted crontab line that restricts both fields changes meaning; the inspector notes the rule whenever both are restricted. `L` follows the same rule: `0 17 L * 5` runs only in months that end on a Friday, not on each month's last Friday. Do not claim compatibility with every Unix or Quartz cron string.

| Example | Intended schedule in `timeZone` |
| --- | --- |
| `0 8 * * 1-5` | Weekdays at 08:00. |
| `*/15 9-17 * * 1-5` | Each quarter hour from 09:00 through 17:45 on weekdays. |
| `0 9 1 * *` | First day of each month at 09:00. |
| `0 17 L * *` | Last calendar day of each month at 17:00; holidays are included. |
| `0 17 L * 5` | 17:00 on the last day of a month, only when that day is a Friday. |
| `0 8 29 2 *` | Leap day at 08:00. |

Introduce shared, dependency-free `CronScheduleRules` for field parsing, limits, and calendar feasibility. Mirror the limited grammar in the editor and use common accepted/rejected fixtures, including `05` (accepted), `005` (rejected), and day-of-week `1/2` (rejected). Check that the selected month/day/weekday sets can match at least one Gregorian date in a bounded 400-year cycle; cache by expression within the validation call and stop at the first match. This rejects February 30 and impossible combined restrictions without evaluating time zones or scanning minutes. Use a fixed Gregorian cycle, not today's date, so definition validity does not vary over time.

The server then parses the allowed expression with Cronos and resolves `timeZone` through the existing IANA checks. Factor those checks into a neutral helper if useful, preserving business-calendar behavior.

The editor's grammar mirror, and every helper that `validateSaveAsyncAndTimers` calls, lives inside the `// BEGIN/END WORKFLOW SAVE VALIDATOR` markers, because the Jint suites run only that span. The save validator checks `timeZone` only for type, presence, and length, and never reads `Intl`: it must return the same ordered errors for the same candidate, the Jint suites stub `Intl`, and browsers rename zones (Chromium's engine reports `Etc/UTC` as `UTC` and `Europe/Kyiv` as `Europe/Kiev`). The inspector may show a hint when `Intl.DateTimeFormat` rejects an identifier. That hint never blocks saving or exporting, never judges casing, which the server checks, and cannot certify the API/Worker's installed zone data. Local validation must state that limitation rather than claim full server parity.

Diagnostics identify the node and `timer.cron` or `timer.timeZone`, with a specific cause: wrong field count, a number longer than two digits, invalid field range, forbidden operator (including an open-ended day-of-week step), impossible date, missing zone, unsupported node, or zone unavailable on this host. Only a timer start names cron in the exactly-one-schedule message (`timeDate, timeDuration, timeCycle, or cron`). An intermediate or boundary timer keeps the present three-way message, and a `cron` or `timeZone` there gets its own unsupported-node error. Keep normal definition errors and AI validation paths; do not change AI instructions, tool names, or tool descriptions.

### 3. Server calculator and daylight-saving contract

Add a service-layer cron adapter used by definition checks, preview, reconciliation, and successor scheduling. Add Cronos only to `Flowbit.Service`, pin an exact compatible package version during implementation, and record it in the dependency change. Do not add it to Shared, the editor, or an external scheduling daemon. Characterize the chosen package before wiring the runtime; future upgrades must pass the same fixtures. Its main-branch parser, checked on 2026-11-09, reads at most two digits per number, runs day of week to 7 and reads 7 as Sunday, accepts reversed ranges and `H`, and treats only spaces and tabs as separators. Flowbit's grammar matches or rejects each of these before calling it; repeat the check against the pinned version.

The [Cronos documentation](https://github.com/HangfireIO/Cronos#daylight-saving-time) describes moving a nonexistent local time to the next valid time. At a repeated hour, fixed/list clock schedules occur once; clock fields using a wildcard, range, or step can occur in both offsets. Flowbit will retain those semantics and test them through its adapter. This dependency supplies date calculation only; Flowbit continues to own persistence and delivery.

Examples to pin with `Europe/London` data and equivalent synthetic-zone fixtures:

| Schedule / transition | Required result |
| --- | --- |
| `30 1 * * *`, 2026-03-29 | The missing 01:30 local occurrence resolves to 02:00 `+01:00`, or 01:00 UTC. |
| `30 1 * * *`, 2026-10-25 | One occurrence at 01:30 `+01:00`, or 00:30 UTC; none at the repeated 01:30 `+00:00`. |
| `*/30 * * * *`, 2026-10-25 | Include 01:00 and 01:30 in both offsets, ordered by their distinct UTC instants. |

Keep this distinction visible in inspector help. In particular, do not expand a clock range into a list while normalizing, saving, or exporting. Verify gap behavior for lists and interval schedules too, including collisions that resolve to the same transition instant; return strictly increasing UTC instants and never enqueue the same instant twice.

The adapter accepts a UTC reference, an explicit inclusive/exclusive bound, the expression, and the resolved zone. Return UTC instants; compute offset/local display values separately. Never pass machine-local `DateTime.Now` into calculation or derive the next occurrence by adding 24 hours to the previous UTC time.

Accept a next result only within 36,525 days after its reference, capped by the representable timestamp range. Use field-based search directly from the lower bound, not enumeration from original activation, and stop after the requested occurrence count. The [calculator implementation](https://raw.githubusercontent.com/HangfireIO/Cronos/main/src/Cronos/CronExpression.cs) does not make a result horizon an execution-time limit. Apply grammar/calendar-feasibility checks before calling it, test worst-case sparse schedules and near-limit dates, and check cancellation between bounded preview calls. Do not construct an unbounded occurrence collection or claim the synchronous library call is preemptible. Treat unavailable zone data, calculation failure, or no result in the supported horizon as a scheduling error, not completion of an unbounded cron schedule. API preview returns a validation error for this condition. Guard timestamp and generation overflow explicitly.

### 4. Persistence and migration

Reuse `timer_subscriptions` and `workflow_jobs`; no new table or job kind is needed.

| Location | Planned change |
| --- | --- |
| `TimerScheduleKinds` in `JobModels.cs` | Add `Cron = "cron"` and explicit recurring-kind classification. |
| Timer create/read records and `TimerSubscriptionEntity` | Add nullable `ScheduleTimeZone`. Cron stores its authored value in `ScheduleExpression` and IANA zone in this new member. |
| `TimerSubscriptionRepository` | Carry the zone through creation, queries, mapping, and incident/recovery paths. Keep compare-and-swap advancement. |
| `AppDbContext` and migration/model snapshot | Add nullable `ScheduleTimeZone` with maximum length 100; allow `cron` in the schedule-kind constraint. |
| Descriptor helpers | Extend the Worker's schedule kind/expression extraction, Jobs helpers, and version-compatibility descriptor comparison. Reject unsupported kinds explicitly. |

Create an additive `AddCronTimerSchedules` migration. Existing rows keep a null zone and need no due-time backfill. Add a null-safe shape constraint requiring a nonblank zone, null `ScheduleCalendar`, and a timer-start-shaped subscription for `cron`; require null zone for existing kinds. The timer-start shape is the existing absence of instance/token/attached-host identifiers. Preserve the present expression column size (300); the authoring limit remains 128.

Add a second constraint: a `cron` subscription is never `completed`. A cron schedule ends only by cancellation, through a default change, retirement, or a retried scheduling incident; a calculation that finds no occurrence is a scheduling error, not completion ([section 3](#3-server-calculator-and-daylight-saving-contract)). The constraint also covers released Workers, which cannot read `cron` ([Current state](#current-state)). One that processes a cron job fires it without the misfire check and then completes the subscription, which reconciliation never recreates under the same activation. With the constraint, that completion fails, the attempted start rolls back with it, and the job retries into an incident that an operator retries after upgrading. It equally catches a defect that treats cron as a one-shot.

Write the replaced schedule-kind constraint and both new constraints in raw SQL as `NOT VALID`, then run `VALIDATE CONSTRAINT` outside a transaction (`suppressTransaction: true`) in a second migration, `ValidateCronTimerScheduleConstraints`. Adding a constraint takes an exclusive lock that lasts until commit, so validating in the same transaction would scan the whole table under it. `timer_subscriptions` also holds every instance timer activation, and Workers keep firing timers while this additive migration applies. Existing rows satisfy all three constraints, but a validation can still stop partway, for example at a statement timeout. EF records a migration only after its last statement, so one that mixed the schema change with these statements could not be applied again once its transaction had committed; the validation migration alone can, because validating a valid constraint changes nothing. [20260808120000_RepairPositionFirstAdministrativeActionBatches.cs](../../Flowbit/src/Flowbit.Infrastructure/Data/Migrations/20260808120000_RepairPositionFirstAdministrativeActionBatches.cs) uses the same pair, but within one transaction.

Retain both existing uniqueness guarantees:

- One active/paused timer-start subscription per immutable definition and timer node.
- One job per `(TimerSubscriptionId, ScheduledOccurrenceAt)` when both values are present.

`NextDueAt` and `ScheduledOccurrenceAt` remain nominal UTC instants. Do not persist browser-local timestamps, a fixed UTC offset in place of the zone, or a list of all future firings. For cron, initialize `Occurrence` to zero and increment it once for each persisted successor, including a jump over downtime. It is a fencing generation, not an ordinal over all theoretical cron matches or a missed-fire count. Retain the existing nominal-slot meaning for ISO cycles.

Down migration must refuse while cron subscription rows remain; it must not delete history or strip zones to make a rollback pass. Application downgrade additionally requires retiring cron-bearing definitions and queued work through supported operations or restoring a compatible backup. Record that limitation in deployment documentation.

### 5. Worker scheduling, locking, and recovery

#### Initial and next occurrence

Do not disguise cron as a `WorkflowTimerSchedule` with a null interval. Introduce explicit schedule-kind dispatch around initial and next calculation; retain the existing fixed-duration/cycle methods and their tests.

| Situation | Required calculation |
| --- | --- |
| New default activation at `A` | First cron instant strictly after persisted `DefaultActivatedAt = A`. An exact match at `A` is excluded. |
| First reconciliation finds that instant at most one minute late | Persist that occurrence and enqueue its job; it remains eligible. |
| First reconciliation finds it more than one minute late | Jump directly to the first instant strictly after the reconciliation's captured `now`; do not replay the intervening backlog. |
| Successful firing at nominal time `D` | Next instant strictly greater than `D` and at or after captured `now - one minute`. This preserves the current grace-window behavior without drifting to completion time. |
| An occurrence job is more than one minute late | Create no instance for it; atomically advance to the first instant strictly after captured `now` and complete the old job. |
| Exactly one minute late | Eligible to fire, matching the existing strict comparison. |

For example, with `* * * * *`, a stored 08:00 occurrence seen at 08:01:00 is eligible. Seen at 08:01:00.001 it is a misfire, so advance to 08:02 even though the intervening 08:01 occurrence lies within the grace window. If seen at exactly 08:02, advance to 08:03. These are compatibility tests, not a new catch-up policy.

Use one captured clock instant per scheduling decision. Extend `IsRecurringMisfire` to both `timeCycle` and `cron`. A long outage must require a bounded number of next-occurrence lookups, independent of the number of skipped matches. Do not use `Occurrence` to regenerate a cron timestamp from activation.

#### Lifecycle invariants

- Retain the reconciliation leader, bounded family batches, default-definition checks, and existing family locking. Scheduling one invalid start must not stop other families or starts from being reconciled.
- Retain firing lock order: **workflow family → job → subscription**, followed by the existing start path. Preserve lease token/generation, activation, current due time, and required-default-version fences.
- Commit instance creation, successor advancement/enqueue, and current job completion in the same transaction. Crash before commit leaves no partial start; duplicate delivery after commit fails the existing fences.
- Keep `forceDurableActivities` for timer starts. This guarantees one committed instance per occurrence, not exactly-once external effects from later service jobs.
- Existing active subscriptions keep their saved due time; missing-job repair re-enqueues that time. Reconciliation must not recalculate it after a restart or a time-zone-data upgrade.
- Default replacement, unpublishing, and deletion retire old schedules/jobs through existing operations. Test races in which the old firing either commits before default replacement or becomes stale afterwards; it must never use the new definition's schedule under the old activation.
- Cron does not create an open instance timer. Audit `WorkflowVersionCompatibilityEvaluator` helpers for the new fields without weakening compatibility checks for catches and boundaries.
- A cron subscription ends only by cancellation, and the database's never-completed constraint enforces it ([Persistence and migration](#4-persistence-and-migration)).

#### Failure and time-zone-data recovery

Initial calculation failure reuses the existing paused subscription plus `timer_schedule_failed` incident and a timer-start job with no `ScheduledOccurrenceAt`. Persist the cron text and zone on that failed descriptor too. Retrying the scheduling incident cancels its subscription, allowing reconciliation to try again under the same default activation and the current misfire rules.

A successor-calculation failure occurs inside the firing transaction: roll back the attempted instance, successor, and completion together, then use existing durable retries/incidents. Do not commit an instance while silently losing its next scheduled start. A later retry still rechecks lateness and may skip the old occurrence. Explain this in Operations recovery guidance.

Already queued UTC occurrences remain fixed when host zone rules change. Newly calculated successors use that Worker's current time-zone data. API and Worker deployments must use compatible zone data and the same pinned calculator version; previews are advisory and are not a reservation of future timestamps. An unavailable zone fails visibly rather than falling back to UTC. Do not copy prompts, workflow input values, or secrets into logs; use existing timer/job telemetry and bounded diagnostics with node/zone context.

### 6. API contracts and authorization

Add the following operations inside the existing workflow endpoint group, inheriting authentication and `Workflow.RequiredRole`. Do not put authoring preview behind `Settings.RequiredRole`: the current business-calendar preview and zone-list endpoints use that administrator boundary.

| Operation | Contract |
| --- | --- |
| `POST /api/workflows/timer-schedules/preview` | Preview an unsaved cron schedule using the runtime calculator; no definition lookup or write. |
| `GET /api/workflows/timer-schedules/time-zones` | Return an ordinally sorted array of host-known IANA identifiers for authoring suggestions, reusing the existing zone-list helper. |

Proposed preview request:

```json
{
  "cron": "0 8 * * 1-5",
  "timeZone": "Europe/London",
  "fromUtc": "2026-11-08T12:00:00Z",
  "count": 5
}
```

`fromUtc` is optional; omission captures the server's clock once. When supplied, require an explicitly offset UTC timestamp (`Z` or `+00:00`), not an offset-free local value; use binding/validation that cannot silently assume the host's zone. It is an exclusive lower bound. `count` defaults to 5 and must be 1–10; reject larger values rather than clamping them. Apply the same cron/zone lengths, grammar, and search horizon as runtime.

Proposed response for that request:

```json
{
  "cron": "0 8 * * 1-5",
  "timeZone": "Europe/London",
  "fromUtc": "2026-11-08T12:00:00Z",
  "occurrences": [
    { "dueAt": "2026-11-09T08:00:00Z", "localTime": "2026-11-09T08:00:00+00:00" },
    { "dueAt": "2026-11-10T08:00:00Z", "localTime": "2026-11-10T08:00:00+00:00" },
    { "dueAt": "2026-11-11T08:00:00Z", "localTime": "2026-11-11T08:00:00+00:00" },
    { "dueAt": "2026-11-12T08:00:00Z", "localTime": "2026-11-12T08:00:00+00:00" },
    { "dueAt": "2026-11-13T08:00:00Z", "localTime": "2026-11-13T08:00:00+00:00" }
  ]
}
```

Use `200` for success, `400` with the existing `{ "error": "..." }` style for malformed input or an unresolvable bounded preview, `401` for unauthenticated callers, and `403` for missing workflow-authoring authority. Reject an incomplete requested result rather than returning unexplained fewer rows. Preview remains available when durable publication is disabled because it schedules no work. It does not save definitions, create subscriptions, enqueue jobs, or change default activations.

Add DTOs under `Flowbit.Shared/Dtos`, a focused service contract/implementation, DI registration, endpoint descriptions, and OpenAPI tests. Reuse authentication/error middleware. No new role setting, anonymous endpoint, arbitrary calculator options, or management endpoint for schedule mutation is required.

### 7. Editor and Flowbit.Ui

In the canonical editor, add **Cron schedule** to the Schedule selector only for timer starts. Show **Cron expression**, **Time zone**, live errors, and inspector help with weekday/month-end examples, the five field meanings, the day-of-month/day-of-week rule (both must match, unlike crontab, and `0 17 L * 5` runs only in months that end on a Friday), daylight-saving behavior, and that a started instance's `sys.now` and `sys.today` are UTC. Use existing accessible labels and `attachInspectorHelp`; keep static explanations out of always-visible warning notes. Two notes depend on the current values and may stay visible: one when both day fields are restricted, stating that both must match, and the time-zone hint described in [section 2](#2-workflow-json-and-validation). Neither affects saving.

Extend `normalizeTimerDefinition`, import/export, copy/paste, type conversion, and `validateSaveAsyncAndTimers`. Imported forbidden or malformed cron configuration must remain visible and repairable until an explicit edit removes it, or a malformed document is rejected without replacing the current document. Selecting or redrawing must not erase it. An intentional schedule/type change clears incompatible fields in one history action; undo/redo restores the original values. Preserve existing validation order and characterization arrays except timer-start messages that now list cron as a fourth alternative; intermediate and boundary timers keep the three-way message.

For hosted preview:

1. Add an optional async mount capability for preview and zone suggestions. Route it through `workflow-editor-host.js`, `[JSInvokable]` methods in `WorkflowEditor.razor`, and the typed `WorkflowApiClient`; Blazor retains API credentials and orchestration.
2. Offer **Preview next 5** and an optional UTC reference input, blank for server-now. Display the echoed reference, named zone, local timestamp with numeric offset, and UTC instant for each row. Do not format these rows only in the viewer's preferred zone, which would obscure the authored schedule.
3. Fetch suggestions lazily; allow free text because the list may be incomplete on a Windows host. The server validates the submitted identifier. Use explicit preview requests rather than one HTTP call per keystroke.
4. Represent idle, loading, success, invalid schedule, unavailable server, and lost-permission states. Keep authored values when a request fails; invalidate old results when cron, zone, or reference changes.
5. Fence replies by document generation, node, configuration, and request generation. Cancel outstanding work where possible; ignore late responses after node selection, document replacement, newer requests, identity change, or disposal.
6. Keep preview results, suggestions, errors, and request state outside workflow JSON, history, the saved baseline, and AI snapshots. Preview must not make a document dirty or reactivate a schedule.

In standalone mode, provide the same fields and local grammar checks, plus a concise note that preview and authoritative time-zone checks are available in Flowbit.Ui. Do not fetch from a hard-coded API, add browser credentials, load a CDN package, or require a build step. Viewer mode gets no authoring controls. The root compatibility launcher remains unchanged.

### 8. Automated and real-browser verification

The plan required the following tests. [Current state](#current-state) lists those written and what ran.

| Layer | Required cases |
| --- | --- |
| Shared rules and JSON | All accepted/rejected dialect forms; every field boundary; steps and lists; `05` accepted and `005` rejected; day-of-week `1/2` rejected; `L` with and without a weekday; impossible dates; combined weekday/date restrictions; Unicode digits; overlength and non-string input; missing/wrong-case/unknown zone; four-way exclusivity on timer starts and unchanged three-way messages elsewhere; wrong node and calendar combinations; old-document and new-field round trips. |
| Calculator | Cronos selects exactly Flowbit's parsed membership sets for every accepted fixture, including day-of-week steps; exact exclusive/inclusive bounds; UTC and non-whole-hour zones; month/year rollover; leap years including a non-leap century; monthly `L`; fixed/list/range/step DST cases; gap collisions; repeated-hour distinct UTC values; synthetic 30-minute transitions; supported horizon, overflow, and direct search after years of downtime. |
| Existing timer regressions | `WorkflowTimerScheduleTests`, expression-timer and business-calendar tests retain fixed schedules, working durations, finite-cycle exhaustion, and grace behavior. Changing cron must not alter `ResolveNextCycleOccurrence`. |
| PostgreSQL runtime | First activation versus Worker startup; zero/60-second/greater-than-60-second lateness; no backlog replay; successor calculation from nominal UTC; repeated-hour uniqueness; two reconcilers; two processors; stale leases; transaction rollback; a refused cron completion rolls back its start; missing-job repair; restart preserving due time; paused incident and retry; unavailable zone; successor failure and retry; defaults switched/unpublished/deleted while firing; reselecting an old default gets a new activation. |
| Persistence and deployment | Migration on existing timer rows; null-safe shape and never-completed constraint rejections; constraints added `NOT VALID` and validated outside a transaction by a separate migration that a stopped run can apply again; occurrence uniqueness; entity/repository mappings; schema-readiness gate; guarded Down behavior; no new lifecycle job-kind classification. Use `TimerStartReconciliationServiceTests`, repository tests, and `DatabaseSchemaGateTests` as baselines. |
| API and OpenAPI | Actual author identity versus settings administrator; `400/401/403`; request bounds and timestamp grammar; deterministic UTC/local results; zone list; disabled-publication preview; no database writes; registered routes, response DTOs, and documented errors. |
| Editor and hosted bridge | Shared grammar fixtures and ordered errors; a save validator that never reads `Intl`; the inspector-only zone hint and both-day-fields note; import/export/type conversion; live validation; undo/redo; no dirty state from preview; stale/failed/disposed replies; document replacement and permission changes; client request construction. Extend characterization, runtime-smoke, and authoring-package tests. |

Use fixed clocks for deterministic runtime tests, and synthetic zones for calculator policy tests. Add real IANA smoke vectors on Windows and the Linux container image so missing zone/globalization data is visible. An API accepting a zone is insufficient evidence that a differently packaged Worker can resolve it.

Run the existing Worker-free [browser smoke suite](../../Flowbit/tests/Flowbit.BrowserTests/README.md) for standalone/hosted authoring. Add focused scenarios at **1440×900** and **1024×768**, plus hosted responsive checks at **390×844**:

- Select a timer start, enter cron and zone, repair validation errors, switch schedule/type, undo and redo, save and reload.
- Use preview, change its reference across both DST transitions, distinguish repeated-hour offsets, and verify failure/permission feedback without losing edits.
- Confirm standalone local-file use remains functional and explains its preview limitation; no external network dependency is introduced.
- Exercise keyboard access and inspect labels, focus, table wrapping, overflow, and browser errors/warnings. Retain screenshots of the changed inspector and preview states.

Use the separate [Worker-enabled acceptance suite](../../Flowbit/tests/Flowbit.BrowserAcceptanceTests/README.md) to publish a cron start, observe a real instance/next job, restart the Worker, and test a default replacement and a downtime misfire. Its opt-in transparent API proxy also owns the stale-preview scenario: leave the Worker stopped, delay a real preview response, change the node/document, and prove the old response is discarded. The ordinary smoke fixture has no proxy and remains unchanged. Set up through real HTTP operations and wait for actual minute boundaries with bounded timeouts. Synthetic clocks, direct row edits, application-state injection, and substituted API responses must not manufacture browser acceptance outcomes. Keep minute-scale races and DST matrices in deterministic integration tests rather than extending browser runs unnecessarily.

Implementation regression commands, run from the repository root with the same arguments in PowerShell 7 and Bash:

```text
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet
dotnet publish Flowbit/src/Flowbit.Api/Flowbit.Api.csproj -c Release -o artifacts/browser/hosts/api /p:UseAppHost=false
dotnet publish Flowbit/src/Flowbit.Ui/Flowbit.Ui.csproj -c Release -o artifacts/browser/hosts/ui /p:UseAppHost=false
dotnet publish Flowbit/src/Flowbit.Worker/Flowbit.Worker.csproj -c Release -o artifacts/browser/hosts/worker /p:UseAppHost=false
dotnet build Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release
dotnet build Flowbit/tests/Flowbit.BrowserAcceptanceTests/Flowbit.BrowserAcceptanceTests.csproj -c Release
pwsh Flowbit/tests/Flowbit.BrowserTests/bin/Release/net10.0/playwright.ps1 install chromium
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore
dotnet test Flowbit/tests/Flowbit.BrowserAcceptanceTests/Flowbit.BrowserAcceptanceTests.csproj -c Release --no-build --no-restore
git diff --check
```

Use the suite READMEs for Docker prerequisites, Linux browser dependencies, logs, and evidence paths. After implementation, follow [mandatory real-browser verification](../../AGENTS.md#mandatory-real-browser-ui-verification) with scenario-specific manual checks too. Report automated commands/results separately from browser name, actual localhost URLs, interactions, viewport sizes, console results, and screenshots. Report unavailable checks explicitly.

### 9. Documentation and examples to ship with implementation

The implementation updated each owning page below.

| Owning page | Implementation update |
| --- | --- |
| [Node reference](../node-reference.md) | `cron`, `timeZone`, exact dialect, `L` with and without a weekday, the both-days-must-match rule and how it differs from crontab, examples, property exclusions, DST behavior, UTC `sys.now`/`sys.today` in started instances, and hosted/standalone validation limits. |
| [BPMN support](../bpmn-support.md) | Timer capability/extension tables, recurring semantics, and the revised unsupported list; intermediate/boundary cron remains unsupported. |
| [API guide](../api-guide.md) | Preview and zone-list routes, DTOs, authorization, errors, bounds, and UTC examples checked against development OpenAPI. |
| [Developer guide](../developer-guide.md) | HTTP-only authored definition/preview examples, default activation, system starts, overlap, no-backfill semantics, and the absence of scheduled-occurrence context (`sys.today` is UTC). |
| [Flowbit.Ui guide](../ui-guide.md) | Cron fields and notes, preview reference and zone display, errors, standalone limitation, finding a published schedule's next run as its `timerStart` job in Operations, and Operations recovery. |
| [Deployment](../deployment.md) | Migration/upgrade/rollback order, the never-completed constraint and its `NOT VALID` validation, released-Worker symptoms and recovery, compatible API/Worker time-zone data, pinned calculator, Worker prerequisites, misfire examples, and scheduling incidents. |
| [Database schema](../database-schema.md) | `ScheduleTimeZone`, expanded kind/shape constraints, the never-completed constraint, unchanged indexes, and cron generation semantics. |
| [Runtime reference](../../Flowbit/README.md) and [AGENTS.md](../../AGENTS.md) | Calculator ownership, persistence/fences, first/next rules, failure behavior, and per-mount preview lifecycle. |
| [Example catalog](../../examples/README.md) | Add complete weekday-start and month-end-start definitions under `examples/timers/`, using unused file numbers. Specify Worker/default-version prerequisites, chosen zones, no required unsupplied inputs, expected occurrences, and overlap behavior. Neither derives a business date from `sys.today`. |
| [Portable authoring guide](../../authoring/flowbit-authoring/references/authoring-guide.md) | Workflow syntax and limitations; regenerate schema/package resources through the existing authoring builder and validate examples. Leave AI instructions/tools unchanged. |
| [Root README](../../README.md) and browser suite READMEs | Update affected capability text and coverage; replace images only if the changed timer inspector makes them misleading. |
| This page and [backlog](README.md) | Record implementation/evidence and set Done only after every delivery gate passes; move remaining desired work into separate backlog items. |

Validate all changed relative links, anchors, example JSON, generated authoring resources, and HTTP examples. Update [documentation home](../index.md) only if a guide/link is added or renamed; no new guide is required by this plan.

### 10. Delivery sequence and release gate

1. **Characterize and define the contract.** Pin the server package, prove the DST vectors and bounded lookup, add shared grammar fixtures and prove the calculator selects the same values for each, settle diagnostics, and implement model/rules/migrator changes. Exit with old JSON regressions green and no external scheduler dependency.
2. **Persist and calculate.** Add the zone migration with its shape and never-completed constraints, the mappings, the adapter, and explicit schedule dispatch, and cover initial/next/misfire calculations with fixed clocks. Exit with schema and non-cron timer regressions green.
3. **Integrate durable execution.** Wire reconciliation, job processing, failure descriptors, default retirement, and recovery. Exit with PostgreSQL concurrency/crash/default-switch tests proving one committed start per occurrence and no backlog replay.
4. **Expose preview and authoring.** Add the authorized routes/client/bridge, shared editor controls, preview states, and import/export/history behavior. Exit with API/OpenAPI, editor characterization, and hosted stale-response tests green.
5. **Document and verify.** Add examples, regenerate authoring resources, update owning guides, run full relevant regressions and both browser suites, and retain manual screenshots/console evidence. Resolve discrepancies before enabling cron authoring in a deployment.
6. **Deploy in order.** Apply the additive migration; upgrade all API, UI, and Worker replicas to the same supported implementation; confirm zone data and calculator compatibility; only then save/publish cron definitions. Mixed old/new readers or Workers are unsupported once cron is authored. The schema gate only warns an old Worker about the newer migration, so this order is the guard. If it is broken, every reconciliation pass an old Worker leads fails while a cron default awaits its first activation, stalling timer-start activation and repair for all families on those passes until that Worker is upgraded. An old Worker that processes a cron job ends in an incident, because the database refuses the completion. Upgrading clears the first; retrying the incident after the upgrade clears the second. No database backfill or schedule activation should occur merely because the migration runs.

The main delivery risks are parser drift between C#, JavaScript, and the calculator, accidentally treating cron as a one-shot (which the database also refuses), package-specific DST behavior, mismatched host zone data, successor failure losing a schedule, a not-yet-upgraded Worker, and stale preview replies. Each has an explicit test gate or deployment guard above. A preview confirms calculation under current rules; it cannot promise Worker availability or an exact wall-clock execution time.
