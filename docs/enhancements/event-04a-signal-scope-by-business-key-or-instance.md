# EVENT-04A: Signal scope by business key or instance

[Parent EVENT-04](event-04-signal-events.md) · [Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Done |
| Category | New events |
| Effort | M |
| Priority | Not ranked; first of eleven in the [EVENT-04 follow-up order](README.md#event-04-follow-up-subtasks) |
| Parent | [EVENT-04](event-04-signal-events.md) |
| Depends on | [EVENT-04](event-04-signal-events.md) (Done) |
| Related | [EVENT-02](event-02-message-correlation-by-business-key.md), [EVENT-04B](event-04b-signal-throw-and-end-events.md), [EVENT-04H](event-04h-expression-signal-names.md) |

## Summary

A throw can limit its recipients to the waits of the instance that owns one business key in a workflow family, or to the waits of one instance named by its ID. Today the narrowest scope is one workflow family.

## Why it adds value

- "Stop everything that is open for order 4711" becomes one throw to the order workflow. Today a targeted reaction needs a message to each instance by its ID, which the caller must have stored, and a message reaches a catch but never a boundary on an open task.
- The caller needs only the business key it already knows, not the instance IDs or which tasks are open.
- It prepares two later items. A throw event ([EVENT-04B](event-04b-signal-throw-and-end-events.md)) can address the instance with the thrower's own business key, and most reasons for a name built from an expression ([EVENT-04H](event-04h-expression-signal-names.md)) disappear.

## First release acceptance criteria

These criteria are revised by this plan on 2026-11-20. The revision states which combinations of scope fields are allowed, says what a scope without waits does, adds the subscription summary filters that the review step needs, and names the one message that changes for existing requests.

- [x] `POST /api/signal-broadcasts` accepts an optional `businessKey` together with a `workflowKey`, or an `instanceId` on its own. A request still states exactly one scope, and every other combination is rejected with `400` and a message that names it.
- [x] A business key is trimmed and then compared exactly and case-sensitively, as a start stores and compares it. A blank key, or one longer than 300 characters, is rejected, not read as "no key".
- [x] An `instanceId` must name an existing instance, and a `businessKey` must name a family that has enabled business keys; otherwise the throw returns `400` and stores nothing. A scope that matches no active wait is accepted and completes with no deliveries, as a family without waits does: no running instance owns the key, or the instance is not waiting for the name or has finished.
- [x] The recipients are still frozen in one statement that takes no runtime lock. A subscription outside the scope gets no delivery row.
- [x] The broadcast records its scope. The API responses and the **Signals** page show it in the throw form, the review step, the list, and the detail.
- [x] `GET /api/signal-subscriptions/summary` accepts the same two filters, and the review step counts the waits of the chosen scope.
- [x] A repeated `idempotencyKey` with a different business key or instance is a conflict, not a replay. A key stored before the upgrade still replays its request.
- [x] A managed API client still reads, cancels, and retries only the broadcasts it threw. The new scopes need no new client scope.
- [x] A throw without the new fields reaches the same waits and returns the same statuses as before, stored broadcasts keep their meaning, and the schema change is additive. One message changes: a request without any scope is now told that `instanceId` is also a choice.
- [x] No log statement, metric, or job payload of Flowbit contains a business key. The summary filter travels in a query string, as the instance filters do, and the API's request log records the path without it.
- [x] The API reference, developer guide, UI guide, database schema reference, and the signal example are updated, with PostgreSQL tests for each scope and real-browser verification of the **Signals** page.

## Later releases

- A business key across all workflow families in one throw. It needs an index on the business key alone, and it can match instances of unrelated families that use the same key text.
- Several business keys or instances in one throw.
- Limiting a signal to one call tree: a root instance and the children its call activities started.
- Filters for business key and instance on the broadcast list.
- A link from an instance's **Waiting for signals** list that opens the **Signals** page with that instance and name filled in.

The subscription summary filtered by business key was listed here and is now part of the first release: without it, the review step would show the family's count for a throw that reaches one instance.

## Current state

Implemented and verified on 2026-11-20: done. The first release acceptance criteria are met.

- **Rules and scope.** [BusinessKeyRules.cs](../../Flowbit/src/Flowbit.Shared/Models/BusinessKeyRules.cs) is the one normalization of a business key; `NormalizeBusinessKeyInput` calls it and keeps its two messages. `SignalBroadcastScope` and `SignalSubscriptionFilter` in [SignalModels.cs](../../Flowbit/src/Flowbit.Service/Models/SignalModels.cs) build the four scopes and the summary filters with the messages of [section 2](#2-request-contract-and-validation). `SignalBroadcastService.HashRequest` hashes a family or all-workflows request as before and adds a length-prefixed business-key line or an instance line.
- **Persistence.** `AddSignalBroadcastScope` adds `signal_broadcasts."BusinessKey"` (`varchar(300)`, `C` collation) and the plain `"InstanceId"` column with `CK_signal_broadcasts_scope`, and its down migration refuses while a scoped broadcast exists. `FreezeDeliveriesAsync` has one literal statement per scope kind; the business-key statement joins `workflow_instances` without a locking clause. `FindInstanceWorkflowKeyAsync` reads an instance's family by primary key, and `SummarizeActiveAsync` takes the two filters.
- **API.** `ThrowSignalRequest`, `SignalBroadcastSummaryDto`, and `SignalBroadcastDetailDto` carry `businessKey` and `instanceId`, with every property documented for the OpenAPI document; the summary route takes `businessKey` and `instanceId` and can return `400`.
- **Flowbit.Ui.** The **Signals** page offers **One workflow family** with an optional **Business key**, **All workflows**, and **One instance**; counts a business-key or instance scope through the scoped summary, dropping a late reply; ends the review on any scope edit; sends only the reviewed scope's fields; and shows the scope in the review, the open broadcast's heading and panel (an instance scope links to the instance), and **Recent broadcasts**.
- **Documentation and example.** The API reference, developer guide (new section **Limit a signal to one case**), Flowbit.Ui guide, database schema, deployment (upgrade row and rollout), engine settings, BPMN support, node reference, runtime reference, AGENTS.md, root README, the example catalog and the signal examples README, both browser-suite READMEs, and [EVENT-04B](event-04b-signal-throw-and-end-events.md) and [EVENT-02](event-02-message-correlation-by-business-key.md). [Example 02](../../examples/signals/02-signal-boundaries-recall.json) makes `orderNumber` its business key; the catalog still counts 71 workflows.
- **Tests.** [SignalScopeTests.cs](../../Flowbit/tests/Flowbit.Tests/SignalScopeTests.cs) (27 PostgreSQL cases: both new scopes, the shipped Example 02 with one order's boundaries reached by business key and by instance, a family without business keys, every rejected combination with its message, numbers read from strings and binding errors, the 300-character limit counted in scalar values, idempotency across scopes and a stored pre-upgrade hash that still replays, the no-lock race for both scopes, the summary filters, a managed client, a host whose captured `Flowbit.*` logs hold no business key, and the migration's check and down guard). [BusinessKeyRulesTests.cs](../../Flowbit/tests/Flowbit.Tests/BusinessKeyRulesTests.cs) has 26 unit cases for the key rule, the scope builder's order of problems, and the summary filter. `SignalsPageTests` gains seven cases (16 in all), `SignalFormattingTests` two (12 in all), and `OpenApiContractTests` one that pins the new request fields, their descriptions, and the summary route's parameters and `400`. A full run of Flowbit.Tests on the final sources passed 5,657 of 5,657 in 18 minutes.
- **Browser.** The smoke suite passed 139 of 139 cases in 13 minutes, including R16 at 1440×900, 1024×768, and 390×844 with its new instance-scope, business-key, and refusal steps; R16 passed again at the three sizes after the review's page fixes. All signal acceptance scenarios passed with a Worker on the final build, nine cases in under five minutes: S1, S2, and the new S6 at 1440×900 and 390×844, and S3, S4, and S5. The acceptance scenarios of other features were not rerun.
- **Plan evidence.** In a disposable PostgreSQL 17 database, one waiting order of Example 02 was cloned into 100,000 keyed orders, giving 100,002 active waits for `warehouse.price-update` in one family. `EXPLAIN (ANALYZE, BUFFERS)` of the business-key statement's `SELECT` starts from `IX_workflow_instances_WorkflowKey_BusinessKey_Status`, probes `IX_signal_subscriptions_active_token` in a nested loop, and runs in 0.10 ms with 12 buffers; the family statement over the same name scans 10,001 rows of the primary key in 4.1 ms. Through the published API, throws by business key took 55 and 49 ms and by instance 49 ms, each freezing one recipient, and a summary by business key took 20 ms.
- **Manual checks.** In the built-in browser against the published API, UI, and Worker with that database, at the pane's 1024×768 and at 375×812:
  - **Keyboard.** Tab moved from **Signal name** to the scope radios, then to **Workflow key** and **Business key**.
  - **Business key.** A key typed with surrounding spaces counted one wait. Its throw ended `Boundary added a branch` for order 1 only, and the scope showed in the heading, the panel, and the list.
  - **Instance.** **One instance** with ID 2 counted one wait and the recall ended `Boundary interrupted its host` for order 2 only, with the link to `instances/2`. Over the API, order 1 kept both waits and order 2 had none.
  - **Long keys.** At 375×812 a 320-character key showed the length message in the status line. A 300-character key wrapped in the review, the heading, the panel, and its 144-pixel history cell without page overflow; the history table scrolls inside its own container, as its eight columns already did at that width.
  - **Logs.** The console held only Blazor connection messages, and no API, UI, or Worker log line contained a business key. That API ran at Warning level, so it wrote no request lines; the acceptance run's API log, at Information level, has 56 lines for the summary route, none with a query string or a business key. The manual API log's only error was a canceled `GET /api/signal-broadcasts` while navigating from `/token`, a client abort on a route this change does not touch.
  - **Walkthrough.** The example README's new PowerShell and Bash commands, pointed at that stack, returned the expected business-key and instance broadcasts.

### Review follow-up

A review of the implementation on 2026-11-20 found these gaps. Each is fixed, and the test and browser results above are from runs after the fixes.

- **Tests that had not run on the final sources.** The first full run of Flowbit.Tests was built before the last change to the **Signals** page and before the documentation and catalog edits that some tests read. The suite was run again on the final sources.
- **Missing tests.** The plan's unit cases for `BusinessKeyRules` and the scope builder did not exist, the instance scope was tested only against a catch, and no test compared the two changed operations with the OpenAPI document. `BusinessKeyRulesTests`, the Example 02 case in `SignalScopeTests`, and the new `OpenApiContractTests` case cover them.
- **Signals page.** When the request that counts a business-key or instance scope failed, the status line kept saying that it was counting; it now says that the waits could not be counted. With **One instance** chosen and no usable ID, the line now asks for a positive whole number, because the number box can still show text the page refused.
- **Documentation.** The signal scope row under [Flowbit extensions](../bpmn-support.md#flowbit-extensions) still named only a family or all workflows, and the route and page summaries in AGENTS.md and the [runtime reference](../../Flowbit/README.md#signal-events) did not mention the new scopes. The lists of facts below read as the present state; they now say that they predate the implementation.
- **Evidence.** The manual check's API ran at Warning level and so could not show what the request log records; the acceptance run's log now supplies that.

Two findings are left as they are:

- **A null character returns `500`.** A `businessKey` that contains U+0000 reaches PostgreSQL, which refuses it, and the API maps no database error, so the throw and the summary answer `500`. A probe showed the same for a null character in `workflowKey`, `idempotencyKey`, and the payload, in the business-key filter of the instance list, and in a start's business key, all of which predate this item. It needs one fix for every route, not one for this field.
- **Later releases.** The backlog asks that later-release work that is still wanted becomes new items once an item is Done. The five entries under [Later releases](#later-releases) have no items yet.

### Facts the plan rested on

Both lists were recorded before implementation and describe the code as it was then.

Recorded on 2026-11-17:

- `ThrowSignalRequest` in [SignalDtos.cs](../../Flowbit/src/Flowbit.Shared/Dtos/SignalDtos.cs) has `name`, `workflowKey`, `allWorkflows`, `payload`, and `idempotencyKey`. Exactly one of `workflowKey` and `allWorkflows` states the scope ([API reference](../api-guide.md#signals)).
- `FreezeDeliveriesAsync` in [SignalBroadcastRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/SignalBroadcastRepository.cs) selects from `signal_subscriptions` by status, signal name, and optionally workflow key. The table holds `InstanceId` and `WorkflowKey` but no business key.
- `workflow_instances.BusinessKey` holds the key, with an index on `(WorkflowKey, BusinessKey, Status)`. `signal_subscriptions` has an index on `(InstanceId, TokenId)` for active rows. Both filters therefore have an index without a change to the subscription table.
- `signal_broadcasts` records only `WorkflowKey` as its scope, and `HashRequest` in [SignalBroadcastService.cs](../../Flowbit/src/Flowbit.Service/Services/SignalBroadcastService.cs) hashes the name, the workflow key, and the payload.
- Instances started before their family enabled business keys have no key ([business key configuration](../node-reference.md#business-key-configuration)), so a business-key scope never reaches them.

Verified on 2026-11-20, before implementation:

- **One running owner per key.** `ReserveBusinessKeyAsync` in [WorkflowRuntimeRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/WorkflowRuntimeRepository.cs) refuses a start while the claim for `(WorkflowKey, BusinessKey)` has an active instance, and reactivation takes the claim back under its lock. A family therefore has at most one running instance per key, with either uniqueness mode.
- **The key never changes.** `workflow_instances.BusinessKey` is written when the instance is created and nowhere else. It stays on the row after the instance ends, so finished instances can share a key with the running one.
- **How a start normalizes a key.** `NormalizeBusinessKeyInput` in [WorkflowEngineService.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.cs) trims with .NET `Trim()`, refuses an empty result, and refuses more than 300 Unicode scalar values. The column uses the `C` collation, and the instance filters compare a trimmed value exactly.
- **No index on the key alone.** The only index that contains `BusinessKey` starts with `WorkflowKey`.
- **Unknown request members are ignored.** The API does not disallow unmapped JSON members, so a replica without this change reads `{ "workflowKey": ..., "businessKey": ... }` as a throw to the whole family. A request with only `instanceId` is refused by such a replica, because it names neither a family nor all workflows.
- **Foreign keys wait.** `signal_deliveries` names its instance by a plain ID because a foreign-key check waits for a transaction that holds the instance row `FOR UPDATE`. The same holds for a foreign key from `signal_broadcasts`.
- **Instances are never deleted.** `RetryFailedDeliveriesAsync` already relies on it.
- **Signals page.** [Signals.razor](../../Flowbit/src/Flowbit.Ui/Components/Pages/Signals.razor) keeps the scope as an `allWorkflows` flag and a `workflowKey`, computes its match line from the unfiltered summary, and holds the reviewed draft in a `ThrowReview` record. `ScopeLabel` in `SignalFormatting` takes only a workflow key. [SignalsPageTests.cs](../../Flowbit/tests/Flowbit.Tests/SignalsPageTests.cs) sets the page's private fields by name.
- **Messages.** The three validation messages of the throw are in `SignalBroadcastService` only; no document or test quotes them in full. `Throw_RefusesAnInvalidNameOrScope` in [SignalRuntimeTests.cs](../../Flowbit/tests/Flowbit.Tests/SignalRuntimeTests.cs) checks for the fragments `allWorkflows`, `cannot be blank`, and `No workflow definition`.
- **Example.** The start variable `orderNumber` of [02-signal-boundaries-recall.json](../../examples/signals/02-signal-boundaries-recall.json) is a required string without a default, so it can be a business key. The acceptance scenario S2 imports that example under a workflow key of its own for each run and starts one instance, so a business key on the example cannot make it collide with an earlier run.
- **Numbers in JSON.** The API uses the default web JSON options, which also read a number from a string, so `"instanceId": "42"` binds as 42. Text that is not a number, and a fraction, fail binding.
- **Keyed families.** `EnsureBusinessKeyFamilyStartableAsync` lets a version with business keys start only after it became the published default, which creates the family's row in `workflow_business_key_scopes`. A family without that row has no instance with a key, and once the row exists an older version without business keys can no longer start.
- **Request logging.** `UseSerilogRequestLogging()` runs with its defaults, which log the request path without the query string, and the checked-in settings keep the `Microsoft` and `System` log sources at Warning.
- **Downgrade test.** `RetentionRepositoryTests.MigrationDowngradeCannotRemovePermanentPruningMarkers` migrates the shared test database down past the signal migrations and deletes every signal broadcast first.
- **Schema descriptions.** The OpenAPI document takes property descriptions from the `<param>` comments on the DTO records, which the signal DTOs did not have. The schema tables of the API reference are written by hand.
- **Advice on messages.** The sentence "Use a message for one instance or for something that must not be missed" is on the **Signals** page and in the [UI guide](../ui-guide.md#throw-signals), [BPMN support](../bpmn-support.md#signals), the [node reference](../node-reference.md#intermediatesignalcatchevent), and the [portable authoring guide](../../authoring/flowbit-authoring/references/authoring-guide.md). The editor's help says only that an instance that must not miss an event needs a message.

## Builds on

- `ThrowAsync` and `FreezeDeliveriesAsync`, which already take an optional workflow key.
- The business key columns and index of `workflow_instances`, and the business-key claims that allow one running instance per key.
- `IX_signal_subscriptions_active_token`, the index of active subscriptions by instance.
- The review step of the **Signals** page, which already ends when the name, scope, or payload changes.
- The throw-scope cases in `SignalRuntimeTests`, and `Throw_DoesNotWaitForARecipientWhoseInstanceIsLocked`.

## Open questions

No question remains open. The plan decides the four original questions and the ones it raised.

| Question | Decision |
| --- | --- |
| Does `businessKey` need a `workflowKey`? | Yes. A key identifies an instance only within one family, and two families can use the same text for unrelated things, such as an order number and a customer number. The existing index starts with the workflow key, so the lookup needs no new index. `businessKey` with `allWorkflows` is rejected, and the cross-family throw is recorded under [Later releases](#later-releases). A caller that wants several families throws once per family. |
| Is `instanceId` a scope of its own, or a filter added to one of them? | A scope of its own. An instance ID is unique in the deployment, so a family beside it adds nothing but a second way to be wrong. `instanceId` with `workflowKey`, `allWorkflows`, or `businessKey` is rejected. |
| Join `workflow_instances` in the freeze statement, or copy the key to each subscription? | Join. A plain read takes no row lock and never waits for a transaction that holds the instance, the key never changes, and the existing index serves it. A copy would add a column, an index, and a write for every subscription of every workflow, and existing subscriptions would need a backfill or would silently never match. |
| Does a managed API client need a new scope to address one instance? | No. `signals.throw` already allows a throw to every waiting instance of every family, so a narrower target grants nothing more. A client still sees only its own broadcasts. |
| Is the business key trimmed? | Yes, with the function a start uses, extracted into one shared rule. A stored key is always trimmed, so an untrimmed comparison could never match. The trimmed key is what the broadcast stores and what the request hash contains. |
| What happens when nothing matches? | A family that does not exist and an instance that does not exist return `400`, because both are most likely a mistyped value. A `businessKey` for a family that has not enabled business keys also returns `400`: no instance of such a family can have a key, so the throw could never reach anything. Decided with the user on 2026-11-20. A key that no running instance owns, and an instance that is not waiting or has finished, complete with no deliveries: the same request is valid a moment earlier or later, and a signal reaches the waits that exist when it is thrown. |
| How many instances does a business-key scope reach? | At most one, because a family has at most one running instance per key. It can still reach several waits: catches on parallel branches and every armed boundary with the name. |
| How does a broadcast record an instance scope? | In two new nullable columns, `BusinessKey` and `InstanceId`. An instance scope also stores the instance's family in `WorkflowKey`, read when the signal is thrown. `WorkflowKey` is then still null exactly for a broadcast to all workflows, and the list's `workflowKey` filter finds the broadcast. |
| Does `InstanceId` on the broadcast get a foreign key? | No. The check would make the throw wait for a transaction that holds the instance row, which invariant 5 of the [parent plan](event-04-signal-events.md#1-scope-and-invariants) forbids. |
| Does the review step count the scoped waits? | Yes. The subscription summary gains `businessKey` and `instanceId` filters, which moves that item out of the later releases. |
| Does `Signals.MaxOpenBroadcasts` change? | No. A broadcast without recipients is stored as `completed` and never counts, and one with a few recipients finishes within seconds while a Worker runs. An integration that throws many narrow signals at once raises the setting and repeats a throw that returns `409`. [EVENT-04B](event-04b-signal-throw-and-end-events.md) must decide the limit again for throws from a workflow. |
| Do the examples change? | Example 02 gains a business key on `orderNumber`, so that the business-key scope has a walkthrough that can be run. Enabling business keys cannot be undone for a workflow key; the example's README says so. |

## Plan

Written on 2026-11-20 following [Writing a plan](README.md#writing-a-plan). Effort is revised from S–M to **M**: the item needs a migration and changes the service, the API, and a page. It changes no workflow JSON, no engine lifecycle, no delivery, and nothing in the editor, so the plan has no section for those.

### 1. Scope and invariants

A throw states exactly one of four scopes. The first two exist today.

| Scope | Request fields | Recipients |
| --- | --- | --- |
| Family | `workflowKey` | Every active wait with the name in that family. |
| All workflows | `allWorkflows: true` | Every active wait with the name. |
| Business key | `workflowKey` and `businessKey` | The active waits with the name in the family's instance that has this key: at most one running instance. |
| Instance | `instanceId` | The active waits with the name in that instance. |

The invariants of the [parent plan](event-04-signal-events.md#1-scope-and-invariants) hold unchanged. Four matter here, and two are new:

1. **A signal is a broadcast, not a queue.** A narrow scope changes who can receive a signal, not when. A wait that starts after the throw receives nothing, also when the throw named its instance.
2. **Recipients are frozen once**, in one statement, for every scope.
3. **Throwing never waits for an instance.** The freeze reads `workflow_instances` without a lock, the instance lookup is a plain read, and the broadcast holds no foreign key to an instance.
4. **Workflows without signals pay nothing.** No transition runs a new statement, and no subscription column or index is added.
5. **One rule for business keys.** A start and a throw normalize a key with the same function, so they cannot drift.
6. **A business key is business data.** It is stored on the broadcast and returned to callers who may read that broadcast. No log statement, metric, or job payload of Flowbit contains it. The summary's `businessKey` filter is a query parameter, as the business-key filters of the instance routes are, so a deployment that logs query strings records it for both.

This release does not add a business key across families, several keys or instances in one throw, a call-tree scope, new filters on the broadcast list, or any change to delivery, retries, cancellation, retention, version changes, or reactivation.

### 2. Request contract and validation

Three requests, each with the fields that state its scope:

```json
{ "name": "warehouse.price-update", "workflowKey": "example-signal-boundaries-recall", "businessKey": "ORD-2001", "payload": { "unitPrice": 12.5 } }
```

```json
{ "name": "warehouse.price-update", "instanceId": 42, "payload": { "unitPrice": 12.5 } }
```

```json
{ "name": "warehouse.product-recall", "workflowKey": "example-signal-boundaries-recall", "payload": { "reason": "Supplier recall" } }
```

| Field | Rule |
| --- | --- |
| `businessKey` | Optional string. Trimmed as a start trims it. After trimming it has 1 to 300 Unicode scalar values. It needs a `workflowKey` and excludes `allWorkflows` and `instanceId`. Compared exactly and case-sensitively. |
| `instanceId` | Optional whole number of 1 or more. It excludes `workflowKey`, `allWorkflows`, and `businessKey`. The instance must exist; it need not be running. |
| `workflowKey`, `allWorkflows` | Unchanged when neither new field is given. `allWorkflows: false` counts as absent, as today. |

The checks run in this order, and the first problem is reported. Checks 1, 2, and 9 exist today with these messages.

| # | Problem | Message |
| --- | --- | --- |
| 1 | Invalid name | The message of `SignalEventRules.ThrowNameProblem`. |
| 2 | `workflowKey` present and blank | `workflowKey cannot be blank; omit it and set allWorkflows to true to reach every workflow.` |
| 3 | `businessKey` present and empty after trimming | `businessKey cannot be blank; omit it to reach every instance of the workflow family.` |
| 4 | `businessKey` longer than 300 characters | `businessKey can have at most 300 characters.` |
| 5 | `instanceId` less than 1 | `instanceId must be a positive whole number.` |
| 6 | `instanceId` with another scope field | `instanceId states the whole scope; do not combine it with workflowKey, allWorkflows, or businessKey.` |
| 7 | `businessKey` without `workflowKey` | `businessKey needs a workflowKey: a business key identifies an instance only within one workflow family.` |
| 8 | No scope, or `workflowKey` with `allWorkflows` | `A signal must name one workflowKey, set allWorkflows to true, or name one instanceId.` |
| 9 | After the replay check: no definition has the workflow key | `No workflow definition has the key 'x'.` |
| 10 | After the replay check: `businessKey` for a family that has not enabled business keys | `Workflow 'x' has not enabled business keys, so businessKey can match none of its instances.` |
| 11 | After the replay check: no instance has the ID | `No workflow instance has the ID 42.` |

- Check 10 reads the family's row in `workflow_business_key_scopes` through the existing `IsBusinessKeyScopeActiveAsync`. The row appears when a version with business keys becomes the published default, and it is never removed, so a family that passes the check keeps passing it.
- Message 8 replaces `A signal must name one workflowKey or set allWorkflows to true, but not both.` The status stays `400`. This is the one change for a request without the new fields.
- A value of the wrong JSON type is a binding failure with the existing `{"error": ...}` body and its path: a `businessKey` that is a number, or an `instanceId` that is a fraction or text that is not a number. The API reads a number from a string everywhere, so `"instanceId": "42"` means 42.
- The recipient-limit message ends `narrow its scope or raise Signals.MaxSubscriptionsPerBroadcast.` in place of `limit it to one workflow family or raise ...`.
- Checks 9 to 11 stay after the replay check, so a repeated `idempotencyKey` returns its broadcast whatever has happened to the family since.

Code:

- **`BusinessKeyRules`**, a new dependency-free class in `Flowbit.Shared/Models`. It owns the limit of 300 and one function that trims a value and reports `blank` or `too long`. `NormalizeBusinessKeyInput` calls it and keeps its two messages, so a start behaves as before. The throw and the summary call it with their own messages.
- **`SignalBroadcastScope`**, a record in [SignalModels.cs](../../Flowbit/src/Flowbit.Service/Models/SignalModels.cs) with `WorkflowKey`, `BusinessKey`, and `InstanceId`, and a kind derived from them: `all`, `family`, `businessKey`, or `instance`. One function builds it from a request and returns the scope or the message of checks 2 to 8. `ThrowAsync`, the hash, the freeze, and the summary take the record. [EVENT-04B](event-04b-signal-throw-and-end-events.md) can then build a scope from the throwing instance without the HTTP request type.
- **`ThrowSignalRequest`** gains `BusinessKey` and `InstanceId` as its last two parameters with null defaults, so existing callers compile unchanged.

### 3. Persistence and migration

One additive migration, `AddSignalBroadcastScope`, in a transaction. Update the entity, the records, `AppDbContext`, and the model snapshot with it.

| Object | Change |
| --- | --- |
| `signal_broadcasts."BusinessKey"` | New nullable `varchar(300)` with the `C` collation: the trimmed key of a business-key scope. |
| `signal_broadcasts."InstanceId"` | New nullable `bigint`, a plain column without a foreign key: the instance of an instance scope. |
| `signal_broadcasts."WorkflowKey"` | Unchanged column. It now also holds the family of an instance scope's instance. Null still means all workflows. |
| `CK_signal_broadcasts_scope` | New check: `BusinessKey` and `InstanceId` are not both set; when either is set, `WorkflowKey` is set; `BusinessKey` is null or not empty; `InstanceId` is null or greater than 0. |

- **Existing rows.** Both columns are null in every existing row, which satisfies the check and keeps each row's meaning: a family or all workflows. Nothing is backfilled.
- **Cost.** Adding a nullable column without a default changes only the catalog. The check is validated in the same migration: it scans `signal_broadcasts`, which holds one row per broadcast and is small beside the runtime tables. No statement needs to run outside a transaction.
- **No new index.** The freeze and the summary use the index of `workflow_instances` on `(WorkflowKey, BusinessKey, Status)` and `IX_signal_subscriptions_active_token`. `signal_subscriptions` and `signal_deliveries` do not change.
- **Counts.** The schema keeps its 71 tables, and no engine setting or retention category is added.
- **Down migration.** It refuses while a broadcast has a `BusinessKey` or an `InstanceId`, because dropping the columns would turn that broadcast into one that reached its whole family. Retention removes such broadcasts once they are finished and old enough.

### 4. Throw transaction and the freeze statement

The throw keeps its shape from the [parent plan](event-04-signal-events.md#4-transactions-and-lock-order): no runtime lock, one freeze statement, one job per represented definition. The changes:

1. **Validate and build the scope**, checks 1 to 8.
2. **Hash the request** from its own fields, before anything is read from the database.
3. **Replay check**, as today.
4. **Resolve the scope.** For a family or a business key, the family must exist, as today. For a business key, the family must also have enabled business keys, check 10: one read by primary key, without a lock. For an instance, a new `FindInstanceWorkflowKeyAsync` on `ISignalBroadcastRepository` reads the instance's workflow key by primary key, without a lock and without tracking. No row means check 11. The result becomes the broadcast's `WorkflowKey`.
5. **Insert the broadcast** with its scope columns.
6. **Freeze** with the statement of the scope's kind.

`FreezeDeliveriesAsync` takes the scope and keeps one literal statement per kind, as it has two today, so that each has a stable plan and none contains an `IS NULL OR` filter. The two existing statements do not change. The two new ones select the same columns:

```sql
-- Business key
INSERT INTO flowbit.signal_deliveries (...)
SELECT ...
FROM flowbit.workflow_instances w
JOIN flowbit.signal_subscriptions s ON s."InstanceId" = w."Id"
WHERE w."WorkflowKey" = @workflowKey
  AND w."BusinessKey" = @businessKey
  AND s."Status" = 'active'
  AND s."SignalName" = @name
ORDER BY s."Id"
LIMIT @max + 1;

-- Instance
INSERT INTO flowbit.signal_deliveries (...)
SELECT ...
FROM flowbit.signal_subscriptions s
WHERE s."Status" = 'active'
  AND s."SignalName" = @name
  AND s."InstanceId" = @instanceId
ORDER BY s."Id"
LIMIT @max + 1;
```

- **No lock.** A `SELECT` without a locking clause reads the instance row from the statement's snapshot. A transaction that holds the row `FOR UPDATE` does not delay it.
- **One snapshot.** Both tables are read in the same statement, so "active when thrown" keeps its one meaning. A subscription is written only under its instance's lock, in the transaction that starts or ends the wait, so an active subscription always belongs to a running instance and the statement needs no status filter on the instance.
- **Finished instances.** With `active` uniqueness, finished instances can have the same key. Each costs one probe of the active-subscription index, which finds nothing.
- **Intended plan.** The business-key statement starts from the instance index, which returns a handful of rows, and probes `IX_signal_subscriptions_active_token` for each. It must not walk every active wait with the name. [Section 8](#8-tests-and-verification) records the plan as evidence. If the planner chooses the ordered scan of `IX_signal_subscriptions_active_name`, select the instance IDs first in a materialized common table expression.
- **Limits.** `Signals.MaxSubscriptionsPerBroadcast` and `Signals.MaxOpenBroadcasts` apply unchanged.

**Request hash.** `HashRequest` keeps its three lines for a request without the new fields: the name, the workflow key or `*`, and the canonical payload. A business-key scope appends a fourth line, `businessKey:`, the trimmed key's length in UTF-16 units, `:`, and the key. An instance scope uses `*` as its second line and appends `instance:` followed by the ID. The length prefix keeps the line unambiguous for a key that contains a line break, without depending on a JSON encoder whose escaping could change between .NET versions.

- A request without the new fields hashes to the same bytes as before, so an idempotency key stored before the upgrade still replays, and a test pins one such hash.
- The same key with another business key, another instance, or another kind of scope differs in the fourth line and returns `409`.
- `" ORD-2001 "` and `"ORD-2001"` are the same request, because the hash contains the trimmed key.

Everything after the freeze is unchanged: the recipient list per family, the delivery jobs, the Worker, each delivery's transaction and lock order, cancel, retry, the lease reconciliation, and retention. A delivery never reads the scope. Version changes and reactivation are not affected, because no subscription or delivery column changes.

**Logging and telemetry.** The throw's log line keeps its three values: broadcast ID, recipient count, and family count. `SignalRuntimeTelemetry` is unchanged.

### 5. API contracts and authorization

| Route | Change |
| --- | --- |
| `POST /api/signal-broadcasts` | Accepts `businessKey` and `instanceId` by the rules of [section 2](#2-request-contract-and-validation). The statuses are unchanged: `202`, `200` for a replay, `400`, `409`. |
| `GET /api/signal-broadcasts` and `GET /api/signal-broadcasts/{id}` | Each broadcast carries `businessKey` and `instanceId`, null when the scope has none. For an instance scope, `workflowKey` is the instance's family and `allWorkflows` is false. The existing filters are unchanged; `workflowKey=` also finds instance-scoped broadcasts of that family. |
| `GET /api/signal-subscriptions/summary` | Accepts `businessKey` and `instanceId`. `businessKey` needs `workflowKey`, and `instanceId` excludes `workflowKey` and `businessKey`; otherwise `400`. Its two messages follow messages 6 and 7 but name only the summary's own parameters: `instanceId cannot be combined with workflowKey or businessKey.` and message 7 unchanged. A blank value means no filter, as for `name` and `workflowKey` today. An instance that does not exist, and a family that has not enabled business keys, give an empty result, not an error: the summary is a read, and only the throw applies checks 10 and 11. The rows keep their shape: name, family, and count. |
| Deliveries, cancel, retry | Unchanged. |

The response of a business-key throw, shortened:

```json
{
  "id": 57,
  "name": "warehouse.price-update",
  "workflowKey": "example-signal-boundaries-recall",
  "allWorkflows": false,
  "businessKey": "ORD-2001",
  "instanceId": null,
  "status": "delivering",
  "totalDeliveryCount": 1,
  "recipientsByWorkflowKey": [{ "workflowKey": "example-signal-boundaries-recall", "count": 1 }]
}
```

- **DTOs.** `SignalBroadcastSummaryDto` and `SignalBroadcastDetailDto` gain `BusinessKey` and `InstanceId`. `ToSummary` and `ToDetail` keep `AllWorkflows` as "`WorkflowKey` is null".
- **Summary query.** `SummarizeActiveAsync` gains the two filters. The business-key filter goes through the subscription's instance, with the family and the key on `workflow_instances`; the instance filter is on the subscription. It takes no lock.
- **Authorization.** Nothing changes. A user needs a role from `Signals.RequiredRole`; a client needs `signals.throw` to throw and `signals.read` to read. A client sees the scope of its own broadcasts, which it sent itself, and still gets `404` for any other broadcast. The summary stays for users only.
- **What a client can learn.** Check 11 tells a client whether an instance ID exists. Instance IDs are sequential numbers, and a client with `signals.throw` already receives the instance IDs of its deliveries. Check 10 tells it whether a family uses business keys, which is a property of the published definition and no instance data.
- **OpenAPI.** Update the summary and description of the throw and summary operations in `SignalEndpoints` and `FlowbitOperationCatalog`, and the comment on `ThrowSignalRequest`, which says that exactly one of two fields states the scope. Give every property of the three records a `<param>` comment, because the OpenAPI document takes its descriptions from them and a record with only some documented warns, and update the hand-written schema tables of the API reference.

### 6. Signals page and other clients

All changes are in [Signals.razor](../../Flowbit/src/Flowbit.Ui/Components/Pages/Signals.razor), its display components, `SignalFormatting`, and `WorkflowApiClient.Signals.cs`. The editor, the viewer, instance detail, and the portable authoring package do not change.

**Step 1, "Choose a signal and its scope".**

- The **Scope** group has three choices: **One workflow family**, **All workflows**, and **One instance**. The page keeps the choice in one enum in place of the `allWorkflows` flag.
- **One workflow family** shows **Workflow key**, as today, and a new optional **Business key** field with the help text "Limits the signal to the instance with this business key. Leave it empty to reach every instance of the family." The page trims the text, and an empty field sends no `businessKey`. The field has no suggestions and no `maxlength`: the attribute counts UTF-16 code units and the rule counts Unicode scalar values, so the page checks the length with `BusinessKeyRules` when the signal is reviewed.
- **One instance** shows **Instance ID**, a whole-number field with a minimum of 1, as the instance filter on **Instances** is.
- Every new control has a label, and each help text is linked with `aria-describedby`.
- **Match line.** For a family or all workflows it stays as it is: the waits per family for the name, from the summary the page already holds. With a business key or an instance, the page asks the summary for that scope whenever the name or a scope field changes and shows one count, such as "2 waits for business key ORD-2001 in example-signal-boundaries-recall" or "No wait for 'warehouse.price-update' in instance #42 now." A request counter drops a late reply, as the history list does.

**Step 3, review and throw.**

- `ThrowReview` gains the business key and the instance ID. **Review signal** refuses an instance scope without an ID with "Enter an instance ID.", asks the summary for the chosen scope, and shows the scope in words.
- Changing the scope choice, the business key, or the instance ID ends the review, as changing the name, the workflow key, or the payload does. The comparison after the count has loaded covers the whole scope.
- **Throw signal** sends the reviewed scope. A scope field that belongs to another choice is never sent: a business key typed before the user switched to **One instance** stays out of the request.
- A `400` from the throw, such as an unknown instance or a family that has not enabled business keys, is shown in the page's error area, as today. The review cannot report either: for both, the summary returns no rows and the review shows no wait.

**Showing the scope.** `ScopeLabel` takes the workflow key, the business key, and the instance ID:

| Scope | Text |
| --- | --- |
| All workflows | `All workflows` |
| Family | `claims-handling` |
| Business key | `example-signal-boundaries-recall · business key ORD-2001` |
| Instance | `Instance #42 · example-signal-boundaries-recall` |

- The review, the heading of the open broadcast, the **Scope** row of `SignalBroadcastSummary`, and the **Scope** column of `SignalBroadcastHistory` use it. In the summary panel, an instance scope links to `/instances/42`.
- A key can have 300 characters, so the scope text wraps inside its cell and panel and causes no horizontal overflow at 390 pixels.

**Page behavior.** `ClearSignalData` resets the scope choice and both new fields, so an identity change or a `401` or `403` leaves no key behind. `WorkflowApiClient` passes the two fields to the throw and the two filters to the summary.

The note at the top of the page ends "Use a message for something that must not be missed." in place of "Use a message for one instance or for something that must not be missed.", because a signal can now address one instance.

### 7. Documentation and examples

| Owning page | Update |
| --- | --- |
| [API reference](../api-guide.md#signals) | The opening sentence and the **Name and scope** bullet: the four scopes, the allowed combinations, trimming, the `400` for an unknown instance and for a family that has not enabled business keys, and what completes with no deliveries. A business-key request and an instance request beside the existing example. The two summary parameters. The schema tables of `ThrowSignalRequest`, `SignalBroadcastSummaryDto`, and `SignalBroadcastDetailDto`. |
| [Developer guide](../developer-guide.md#broadcast-signals) | The "Addressed by" row of the message and signal comparison. A new subsection, **Limit a signal to one case**, with the HTTP request for a business key and for an instance, the fact that a family has one running instance per key, and what still separates this from a message: no credentials on the node, delivery by the Worker, boundaries as recipients, and no error when nothing waits. The limit sentence ("narrow its scope"), a sentence on many narrow throws and `Signals.MaxOpenBroadcasts`, and the summary filters under **Read progress**. One sentence under [business identity](../developer-guide.md#separate-retries-from-business-identity) that links here. |
| [Flowbit.Ui guide](../ui-guide.md#throw-signals) | Step 1 with the three choices and the business key field, the scoped match line and review, the scope shown in the list and the detail, and the reworded note. |
| [Database schema](../database-schema.md#signal_broadcasts) | The two columns, the widened meaning of `WorkflowKey`, the check, the request hash, and the sentence about a throw, which now also reads `workflow_instances` without a lock for a business key. |
| [Deployment](../deployment.md#signal-events) | The migration, the rollout order of [section 9](#9-delivery-sequence-rollout-and-risks) with its warning about older API replicas, the down guard, a capacity note for integrations that throw many narrow signals, and a sentence that a host configured to log query strings records the summary's `businessKey` filter, as it records the instance filters. |
| [Engine settings](../engine-settings.md#signal-settings) | `Signals.MaxOpenBroadcasts`: when to raise it for narrow throws. |
| [BPMN support](../bpmn-support.md#signals) and [node reference](../node-reference.md#business-key-configuration) | The scope sentence of the Signals section, one sentence under business key configuration that a signal can be limited to a key, and the advice on messages in the Signals section and in the [signal catch section](../node-reference.md#intermediatesignalcatchevent), reworded as on the page. |
| [Runtime reference](../../Flowbit/README.md#signal-events) and [AGENTS.md](../../AGENTS.md) | The throw's scopes, the freeze statement's read of `workflow_instances`, `BusinessKeyRules` as the one normalization, the plain `InstanceId` column, and the rule that a business key is never logged. |
| [Signal examples](../../examples/signals/README.md) and the [example catalog](../../examples/README.md) | Example 02's start event gains `"businessKey": { "variable": "orderNumber", "uniqueness": "active" }`. Its walkthrough gains a step that starts two orders and throws `warehouse.price-update` for one of them by business key, and the same throw by `instanceId`, each in PowerShell and Bash. The catalog row says that `orderNumber` is the family's business key, that a second running order with the same number is refused, and that enabling business keys cannot be undone for the workflow key. The walkthrough also says what a database that already holds the example needs: the imported version must become the published default before it can start, and from then on the earlier version without a business key cannot. The example count stays 71. |
| Browser suite READMEs | The extended R16 and the new S6. |
| This page, the [backlog](README.md), and the [parent](event-04-signal-events.md#later-releases) | Evidence and status. |
| [EVENT-04B](event-04b-signal-throw-and-end-events.md) and [EVENT-02](event-02-message-correlation-by-business-key.md) | What this item settles for them. For EVENT-04B: the four scopes and `SignalBroadcastScope`, which a throw event can author and build, and the open-broadcast limit it must still decide. For EVENT-02: `BusinessKeyRules` and the lookup of the one running instance with a key. |

The [portable authoring guide](../../authoring/flowbit-authoring/references/authoring-guide.md) keeps its sentence. It advises which event to model, a message catch is still the event whose sender learns that nothing was waiting, and the package describes no throw request. The editor's help, the AI authoring guide, and the documentation home need no change.

Validate changed relative links, anchors, and the example JSON. Compare the two changed operations with their registrations, DTOs, tests, and the development OpenAPI document, and run the new walkthrough commands against a local stack with a Worker.

### 8. Tests and verification

| Layer | Cases |
| --- | --- |
| Shared rule | `BusinessKeyRules`: trimming as .NET `Trim()` does, an empty and a white-space value, 300 and 301 scalar values including a character outside the basic plane. The existing start tests pass unchanged with their messages. |
| Scope validation | Checks 2 to 8 through the API, by extending `Throw_RefusesAnInvalidNameOrScope` with the two fields: each rejected combination with its message, `allWorkflows: false` beside an `instanceId` accepted, `"instanceId": "42"` read as 42, and a fraction, text that is not a number, and a numeric `businessKey` as binding errors. |
| Business-key scope (PostgreSQL) | Two running instances of one keyed family wait for one name: a throw with the first key creates one delivery, the second subscription gets no delivery row, and after processing only the first instance has moved. A key in another letter case reaches nothing. A key with surrounding spaces matches. An instance of another family with the same key and name is not reached. An instance without a key is not reached. With `active` uniqueness, a finished instance and a running one share a key and only the running one is reached. One instance with a catch on each of two parallel branches receives two deliveries. A throw with a business key to a family that has not enabled business keys returns `400` and stores no broadcast, the same family accepts a throw without the key, and a family whose keyed version later becomes the published default then accepts the key. |
| Instance scope (PostgreSQL) | A task with an interrupting and a non-interrupting boundary of different names, and a second instance on the same task: the throw reaches only the named instance. An unknown ID returns `400`. A finished instance and a running one that is not waiting complete with no deliveries and enqueue no job. The broadcast stores the instance's family. |
| Idempotency | The same key and request replay with `200`. Another business key, another instance, and the family in place of the business key return `409`. A broadcast row with a hash computed by the old formula replays, against a pinned hash value. A key with surrounding spaces replays the trimmed one. |
| Recorded scope | List and detail return `businessKey` and `instanceId` for each of the four scopes, `allWorkflows` is true only for all workflows, and `workflowKey=` on the list finds an instance-scoped broadcast. A row written before the migration reads as before. |
| Managed client | A client with `signals.throw` throws with each new scope and reads the scope on its own broadcast. Another client's scoped broadcast returns `404`. A client without the scope gets `403`. |
| No lock (release gate) | `Throw_DoesNotWaitForARecipientWhoseInstanceIsLocked`, repeated for the business-key and the instance scope: another transaction holds the recipient's instance row `FOR UPDATE`, and the throw returns with its delivery row. |
| Summary | Both filters, with and without a name; the two rejected combinations; a blank value as no filter; a client refused as today. |
| Migration | Up and down. The check refuses a row with both new columns, and one with a new column but no workflow key. The down guard refuses while a scoped broadcast exists and passes after it is removed. `RetentionRepositoryTests` already deletes every broadcast before its downgrade of the shared database; confirm that no other test migrates that database down past the new migration. |
| Privacy | A test captures the log output of a business-key throw, its delivery, and a summary request with the filter, with a logger provider as `WebhookDispatchTests` uses one, and finds no key in it. |
| Flowbit.Ui | `SignalFormattingTests`: the four scope texts. `SignalsPageTests`: a reviewed throw sends the business key with its workflow key; a reviewed throw sends only the instance ID; editing the business key, the instance ID, or the scope choice ends the review; a field of another choice is not sent; an identity change clears both fields; a late scoped count does not replace a newer one. |
| Contracts and example | The OpenAPI and operation-catalog tests, the example catalog tests, and the authoring package tests with the changed Example 02. |
| Plan evidence (recorded) | On a database with at least 100,000 active waits for one name in one family, the output of `EXPLAIN (ANALYZE, BUFFERS)` for the business-key statement's `SELECT`, which must start from the instance index, and the duration of the throw. |

Browser smoke suite, which has no Worker:

- **R16, Signals page**, at 1440×900, 1024×768, and 390×844. Choose **One instance**, type the ID of an instance that is not waiting, review, and throw: the review names the instance and shows no wait, and the broadcast completes with no deliveries and shows its scope in the heading, the summary panel with its instance link, and the list. Type a business key under **One workflow family**, review, change the key, and see the review end. Throw to an instance ID that does not exist and see the API's message.

Worker acceptance suite:

- **S6**, at 1440×900 and 390×844. Start two orders of Example 02 with different order numbers. From the page, throw `warehouse.price-update` with the first order's business key: one delivery ends **Boundary added a branch**, the first order has an **Update the invoice** task, and the second order's detail still lists both waits and no new task. Then throw `warehouse.product-recall` to the second order by its instance ID: its shipment task is interrupted, and the first order still waits for a recall.

Then follow [mandatory real-browser verification](../../AGENTS.md#mandatory-real-browser-ui-verification) by hand on a local stack with a Worker: the **Signals** page at the three sizes with real clicks, typing, and keyboard navigation through the scope controls, a 300-character business key in the review, the list, and the detail at 390×844, and the console. Keep screenshots of the three scope choices, a review with a business key, and a broadcast with an instance scope.

Regression commands, run from the repository root with the same arguments in PowerShell 7 and Bash:

```text
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet
dotnet publish Flowbit/src/Flowbit.Api/Flowbit.Api.csproj -c Release -o artifacts/browser/hosts/api /p:UseAppHost=false
dotnet publish Flowbit/src/Flowbit.Ui/Flowbit.Ui.csproj -c Release -o artifacts/browser/hosts/ui /p:UseAppHost=false
dotnet build Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore
git diff --check
```

The suite READMEs cover Docker, the Chromium install, and the acceptance suite's own commands. Full solution runs have had order-dependent failures at the base commit, so compare failures against a base run before attributing one to this change.

### 9. Delivery sequence, rollout, and risks

1. **Rules.** `BusinessKeyRules` with the start calling it, `SignalBroadcastScope` with its validation, and the request hash. Exit with the rule, validation, and hash tests green and the start tests unchanged.
2. **Persistence.** The migration, the entity and records, the two freeze statements, the instance lookup, and the summary filters. Exit with the migration and repository tests green.
3. **Service and API.** `ThrowAsync` with the scope, the DTOs, the summary route, and the OpenAPI texts. Exit with the scope, idempotency, client, and no-lock tests green.
4. **Flowbit.Ui.** The scope controls, the scoped match line and review, and the scope texts. Exit with the component tests green.
5. **Documentation and example.** The owning pages and Example 02.
6. **Verification.** R16, S6, the plan evidence, and the manual checks. Record the evidence under [Current state](#current-state), then set the status to Done here, in the [backlog](README.md#event-04-follow-up-subtasks), and in the [parent](event-04-signal-events.md#later-releases).

Rollout:

1. Apply `AddSignalBroadcastScope`. It is additive and holds its lock on `signal_broadcasts` only for the catalog change and one scan of that table.
2. Upgrade every API and UI replica to the same release, with the Workers, whose delivery code does not change.
3. Only then send `businessKey` or `instanceId`. An API replica without this release ignores an unknown `businessKey` and throws to the whole family.
4. To roll the binaries back, first stop every caller that sends the new fields. The schema can stay. The down migration refuses while a scoped broadcast exists.

| Risk | Guard |
| --- | --- |
| An older API replica drops `businessKey` and the signal reaches the whole family. | The rollout order, stated in the deployment guide. The response shows `businessKey` and the recipient count, so a caller can check both. |
| The planner walks every active wait with the name before it filters by key. | The join starts from an index that returns a handful of rows, the plan is recorded as evidence on a large name, and a materialized instance lookup is the fallback. |
| The freeze starts to wait for instances. | No locking clause, no foreign key on the new column, and the no-lock race test for both new scopes. |
| A start and a throw normalize a key differently, so a throw never matches. | One function in `BusinessKeyRules` for both, and a test with an untrimmed key. |
| An idempotency key from before the upgrade stops replaying. | The hash of a request without the new fields is unchanged, and a pinned value proves it. |
| A caller expects a business key to reach an order's instances in every family, or the children of its call activities. | The request requires a workflow key and says why, the guides state that the scope is one instance of one family, and both wider scopes are recorded under [Later releases](#later-releases). |
| A caller sends a business key to a family that never enabled business keys and waits for a reaction that cannot come. | Check 10 returns `400` with a message that says why. |
| A caller expects a signal by instance ID to be kept until the instance waits. | The scope changes who receives a signal, not when. The page's note, the review step with its count, and the guides say so, and the broadcast reports that it had no deliveries. |
| An integration that throws one signal per order meets `Signals.MaxOpenBroadcasts` and gets `409`. | A broadcast without recipients never counts, small broadcasts finish within seconds, the setting can be raised to 1,000, and the developer guide says to repeat a throw that returns `409`. |
| A business key appears in a log or a metric. | The throw logs only counts, the request log leaves out query strings, and a test reads the captured log. The deployment guide says that a host configured to log query strings records the summary filter, as it records the instance filters. |
| Example 02 with a business key refuses a second running order with the same number, and business keys cannot be turned off for its workflow key. In a database that already holds the example, the earlier version stops starting once the new one is the default. | The example's README and catalog row say so, and the test scenarios import the example under a workflow key of their own and start each order with its own number. |
