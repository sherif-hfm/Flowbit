# EVENT-04G: Parallel signal delivery and a higher recipient limit

[Parent EVENT-04](event-04-signal-events.md) · [Enhancement backlog](README.md) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Done |
| Category | New events |
| Effort | M |
| Priority | Not ranked; seventh of eleven in the [EVENT-04 follow-up order](README.md#event-04-follow-up-subtasks) |
| Parent | [EVENT-04](event-04-signal-events.md) |
| Depends on | [EVENT-04](event-04-signal-events.md) (Done) |
| Related | [EVENT-04E](event-04e-signal-broadcast-expiry.md), [PERF-01](perf-01-throughput-and-worker-capacity.md) |

## Summary

Deliver one large broadcast with several jobs at once, and raise the recipient limit above 100,000 once delivery keeps up.

A broadcast that reaches many waits of one workflow definition is split into several delivery jobs for that definition. Each job delivers the waits of its own set of instances. Each Worker limits how many of these jobs it runs at once.

Schedule this when a deployment's measurements show that delivery time is a problem.

## Why it adds value

- One job delivers one definition's recipients one after another. At the measured 20.4 deliveries per second, 10,000 recipients take about 8 minutes, and 100,000 would take more than 80.
- A signal that should stop work is weak when its last recipient hears it an hour later.
- An [expiry](event-04e-signal-broadcast-expiry.md) counts from the throw, so a short expiry on a large broadcast ends most deliveries although nothing is wrong.

## First release acceptance criteria

These criteria are revised by this plan on 2026-11-25. The revision names the settings, limits the Worker's cap to the jobs of split broadcasts, sets the measured gain that counts, names the new hard cap, and adds retention. The [plan review](#plan-review-of-2026-11-25) of the same day added the criterion on waiting share jobs, the processor use and the API cost to the recorded run, and the migration to the hard cap.

- [x] A broadcast with at least twice `Signals.MinDeliveriesPerJob` recipients of one definition gets several delivery jobs for that definition, up to `Signals.MaxDeliveryJobsPerDefinition`. A smaller broadcast gets one job and is delivered as today. Each delivery still ends exactly once.
- [x] Every delivery to one instance belongs to one job of its broadcast, so the jobs of a split broadcast do not queue behind each other on an instance. The lock order of a delivery is unchanged, and a PostgreSQL test runs the jobs at the same time without a deadlock.
- [x] Each Worker runs at most `FlowbitWorker:SignalShareConcurrency` jobs of split broadcasts at once, by default 2 (planned as two fewer than its activity slots; [changed 2026-11-26](#decision-of-2026-11-26)). Timers keep their control slots, and service tasks, scripts, and the jobs of small broadcasts keep the remaining activity slots.
- [x] Cancel, retry of failed deliveries, expiry, and the recount stay correct with several jobs per definition. A job that loses its last lease fails only its own share of the deliveries.
- [x] Share jobs that wait for a share slot do not pause retention and do not read as a job backlog in the Worker's queue metrics.
- [x] A recorded run on a named machine, with its processor use, shows at least twice the deliveries per second with four jobs than with one. A machine that one job already keeps busy counts neither for nor against. The run shows no cost for a workflow without signal events and no later start for the delivery of a small broadcast, and it records what a split broadcast costs API requests. *Met except the small broadcasts: 3.07 times, no cost for starts, API cost recorded. Four jobs delayed small deliveries by seconds, so the default became 2, at which they began 0.10 / 0.73 s after due (p50 / p95) against 0.07 / 0.24 s during one job ([evidence](#evidence-of-2026-11-26)). The user accepted that later start at the default as a documented limit on 2026-11-26 ([decision](#decision-of-2026-11-26)).*
- [x] The hard cap on recipients rises from 100,000 to 250,000 only if every statement that touches a whole broadcast passes its gate in [section 8](#8-the-recipient-limit) at the new size. A migration then widens the database check that holds the limit. Otherwise the cap stays, and the figures are recorded here. *Every gate passed on 2026-11-26, so the cap is 250,000 and `RaiseSignalBroadcastRecipientCap` widens the check ([figures](#recipient-cap-of-2026-11-26)).*
- [x] Retention removes a finished broadcast of the hard cap's size inside its own timeouts. *At today's cap of 100,000: 73 ms.*
- [x] The engine settings and deployment guides describe the new settings and how to size them.

## Later releases

- Fair delivery among several open broadcasts.
- A delivery queue separate from `workflow_jobs`.
- Moving work between the jobs of one broadcast. Until then a job that waits in a retry delay leaves its share waiting while the other jobs finish.
- A bound on the jobs of a broadcast that reaches many definitions. Such a broadcast takes one activity slot for each definition, as today.
- Fewer database statements for each delivery, with [PERF-01](perf-01-throughput-and-worker-capacity.md) phase 5.

## Current state

Done on 2026-11-26, on top of commit `eb4ac95`: every step of the [delivery sequence](#12-delivery-sequence-rollout-and-risks) is done, including the higher recipient cap, and every acceptance criterion is met, one of them with a limit the user accepted. The user set the status to Done without the run with eight jobs; see [not done](#not-done). See [implementation](#implementation-of-2026-11-26), [evidence](#evidence-of-2026-11-26), the [review](#review-of-2026-11-26), the [recipient cap](#recipient-cap-of-2026-11-26), and [not done](#not-done). The sections from [Before the change](#before-the-change) to [Plan review of 2026-11-25](#plan-review-of-2026-11-25) describe the code before the change.

### Implementation of 2026-11-26

- **Split.** `SignalDeliveryShares` in [SignalModels.cs](../../Flowbit/src/Flowbit.Service/Models/SignalModels.cs) gives the job count, parses the two settings, and names the phase `deliverShare`. [SignalBroadcastWriter.cs](../../Flowbit/src/Flowbit.Service/Services/SignalBroadcastWriter.cs) `EnqueueDeliveryJobsAsync` takes each definition with its count, reads both settings in one query through the new `IEngineSettingsRepository.GetByKeysAsync` only when a definition has two or more recipients, and still inserts all jobs and all links in two statements. A definition with one job gets today's phase and payload byte for byte. `SignalBroadcastJobPayload` gained the optional `ShareIndex` and `ShareCount`, written only for share jobs.
- **Persistence.** Migration [AddSignalDeliveryShares](../../Flowbit/src/Flowbit.Infrastructure/Data/Migrations/20261126122213_AddSignalDeliveryShares.cs) adds the two columns with defaults 0 and 1, `CK_signal_broadcast_jobs_share`, and the two seeds; its down migration refuses while a share job is not finished and removes a seed only while it still has its seeded value, as `AddSignalCatalog` does.
- **Share.** [SignalDeliveryShareSql.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/SignalDeliveryShareSql.cs) holds the one condition. `ListPendingDeliveryIdsAsync` uses it for a share job and today's query otherwise, and `ReconcileExhaustedSignalBroadcastsAsync` uses it with the link's share. A start delivery falls in the share that instance ID 0 hashes to, which is not always share 0.
- **Delivery job.** [SignalBroadcastJobProcessor.cs](../../Flowbit/src/Flowbit.Service/Services/SignalBroadcastJobProcessor.cs) validates the share, pages within it, and recounts after a page only every two seconds for a share job. An internal `PageSize` lets tests reach several pages with a few waits.
- **Retry.** `RetryFailedDeliveriesAsync` returns each definition's pending count, and `RetryFailedAsync` enqueues by it and logs the jobs it created.
- **Worker.** `FlowbitWorker:SignalShareConcurrency` with its default and validation in [WorkerOptions.cs](../../Flowbit/src/Flowbit.Worker/WorkerOptions.cs); `WorkflowJobLeaseRequest.MaxSignalShareCount`; the condition in the three acquisition queries and the cap in the selection loop of [WorkflowJobRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/WorkflowJobRepository.cs); `GetNextWakeAtAsync(includeSignalShares)`; [JobDispatcher.cs](../../Flowbit/src/Flowbit.Worker/JobDispatcher.cs) tracks share jobs and logs the share slots; [WorkerTelemetry.cs](../../Flowbit/src/Flowbit.Worker/WorkerTelemetry.cs) adds the gauge and leaves share jobs out of the lag histogram.
- **Statistics.** `GetQueueStatisticsAsync`, which also serves `GET /api/jobs/statistics`, and retention's `IsRuntimeBusyAsync` in [RetentionRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/RetentionRepository.cs) leave share jobs out.
- **Load test.** The [lifecycle load test](../../Flowbit/tools/LifecycleLoadTest/README.md#large-broadcast) has `--broadcast`, `--broadcast-versions`, `--broadcast-users`, and processor use per process through `--pg-container`, `--worker-pid`, and `--other-pid`, and lists share jobs on a line of their own.
- **Tests.** `SignalDeliverySharesTests` (job count, settings, payload), `SignalDeliveryShareTests` (26 PostgreSQL cases), `SignalDeliveryShareMigrationTests`, `DurableJobRepositoryTests.SignalShares` (leasing, statistics, wake time, retention's check), and new cases in `WorkerDispatcherTests` (lease requests, wake queries, options, gauge, lag histogram). The signal, retention, durable job, Worker, lifecycle event, engine setting, and job operation suites, 1,411 cases, pass.

### Evidence of 2026-11-26

On one Windows machine with 4 logical processors and 32 GB, the API and one Worker as Release builds published to one disk, PostgreSQL 17 in Docker Desktop with `pg_stat_statements`, and an idle Compose stack beside them. Each broadcast reached 10,000 claims of [signal example 01](../../examples/signals/01-signal-catch-policy-change.json), whose route after the catch cancels a timer boundary and opens a user task and uses no shared variable; each delivery ran about 54 statements. The figures are in [capacity planning](../deployment.md#one-large-signal-broadcast).

| Run | Result |
| --- | --- |
| Step 1, code before the change: 1, 2, and 4 versions | 16.4, 31.1, and 54.0 deliveries per second. The one-job run left 2.1 processors idle (PostgreSQL 0.31, Worker 0.26), so two jobs at 1.90 times one passed the gate. |
| After the change: `Signals.MaxDeliveryJobsPerDefinition` 1 and 4 | 17.0 and 52.1 per second: **3.07 times** with four share jobs, while the run with one job left 2.3 processors idle. The criterion of twice the rate is met. Each share job ran about 190 s and began within 0.25 s of its due time. |
| Purchase requests at 8 users, `--no-browse` | 3.95 per second alone, 3.51 during the broadcast with one job, and 2.73 with four share jobs: four jobs cost 22% of the throughput measured at one job, below the quarter that would lower the default to 2, so by this rule the default stayed 4; the next row changed that. The browsing reads were left out because 60,000 delivered claims left tasks without roles in every inbox, which made a full inbox page take 8 s. |
| Timer and signal cases (120 and 4 users) | During four share jobs, small signal deliveries began 1.17 / 6.19 s after due (p50 / p95) and reminder scripts 2.25 / 8.31 s, against 0.05 / 0.12 s and 0.09 / 1.11 s without a broadcast and 0.07 / 0.24 s and 0.12 / 0.52 s during one job. With 2 share jobs: 0.10 / 0.73 s and 0.16 / 1.79 s. With four share jobs on a Worker with 10 activity slots: 0.26 / 1.96 s and 0.43 / 3.73 s. Timers stayed on time in every run. **The criterion of no later start for small broadcasts is not met with four jobs on this machine**, and at two jobs only the median stays close; see [decision of 2026-11-26](#decision-of-2026-11-26). |
| Starts of a workflow without signal events, 20,000 after a warm-up, alternating builds | 69.6 and 68.2 per second before the change, 67.4 and 71.2 after: no cost. |
| Throw | 0.16–0.18 s for 10,000 recipients with one job or four; the split adds one settings query when a definition has two or more recipients, and none for one. |
| Retention at today's cap | Retention's own `DELETE` of one finished broadcast with 100,000 deliveries made by SQL (108 MB of `signal_deliveries` at the time), under `statement_timeout` 5 s and `lock_timeout` 500 ms: 73 ms, the deliveries removed by the cascade in the same statement. The rows were in PostgreSQL's cache; a batch holds up to `RetentionBatchSize` (250) broadcasts, so a batch of many broadcasts near the cap could still pass 5 s. The delete was not changed. |

Runs that were spoiled and repeated: a one-job run during which a stopped shell script still used two processors (8.95 per second), purchase runs with the browsing reads, and a timer run during which 5,329 one-hour timer boundaries of a cancelled broadcast's claims fired.

### Decision of 2026-11-26

Four share jobs on a Worker with 6 activity slots delayed small broadcasts and scripts by seconds on a saturated 4-processor machine, although the plan's purchase-request rule kept 4. Asked to choose between keeping 4 with the limit documented and lowering the default, the user lowered the default of `Signals.MaxDeliveryJobsPerDefinition` to **2** the same day. The code default, the migration's seed, the tests, and the guides say 2; the guides tell deployments with processors to spare to raise it to 4 with more activity slots. `SignalShareConcurrency` first kept its default of two fewer than the activity slots, so a Worker at its defaults still ran two split definitions at once, such as two large versions of one family: four share jobs, the load that delayed small broadcasts. A review the same day found this, and the user lowered that default to **2** as well. Unset, it is now 2, or two fewer than the activity slots when that is less, and never below 1 unless the Worker has no activity slot; a Worker at its defaults runs one split definition at a time, and a second waits for a share slot, about six minutes at 10,000 recipients. The measurements above were made with explicit values and stay valid.

Even at two jobs, the delivery of a small broadcast starts later while a split broadcast delivers: 0.10 / 0.73 s after its due time (p50 / p95) against 0.07 / 0.24 s during one job, and reminder scripts 0.16 / 1.79 s against 0.12 / 0.52 s. The criterion asked for no later start. After the second review the user accepted this as a documented limit the same day, rather than lowering the default to 1, which would give up the faster delivery. It is listed under [section 1](#1-scope-and-invariants) and in the [deployment guide](../deployment.md#signal-events); `Signals.MaxDeliveryJobsPerDefinition` = `1` removes it.

### Review of 2026-11-26

A second review of the finished change read every path a share job takes and found no correctness defect:

- **Lost leases.** `OpenExhaustedLeaseIncidentsAsync` runs unfiltered in every lease round, so a Worker with no free share slot still opens the incident of an exhausted share job and fails its share.
- **Leasing.** A round skips a share job only after it has leased one, so it never returns nothing while other work is runnable; the next round then filters share jobs out before the ranking.
- **Links.** No runtime code reads `signal_broadcast_jobs` except the lost-lease statement, and none identifies a delivery job by its phase except the share conditions.
- **The share query.** On a delivered broadcast of 10,000 it uses `IX_signal_deliveries_BroadcastId_WorkflowDefinitionId_Status_Id` with the share as a row filter, and the hash put 4,963 and 5,037 deliveries in two shares, and 274 to 340 in each of 32.

It changed four small things. The share page query is now a static string, which removes the one build warning the change had added (`EF1002`). The down migration's message and the rollback text in the [deployment guide](../deployment.md#signal-events) say that a share job with an open incident also blocks the down migration until the incident is retried. The retention paragraph of that guide says waiting share jobs do not count as a busy queue, and AGENTS.md records that the two defaults of 2 are a user decision and stay equal.

Still unexercised: the Bash commands of the load test's README, whose `ss` lookup of the Worker's process needs Linux.

### Recipient cap of 2026-11-26

Step 5 ran the gates of [section 8](#8-the-recipient-limit) on the reference machine, at 100,000 and twice at 250,000, against an API built with the higher cap, the database check widened by hand, and no Worker. The rows were inserted by SQL: 700,000 active subscriptions in random order, 350,000 for a family of one version and 350,000 for a family of 250 versions, pointing at no instance. The API requests were timed with `pg_stat_statements`; the recount, the lost-lease statements, the settle, and retention's delete are the code's SQL run in `psql`. The figures are in [the recipient cap](../deployment.md#the-signal-recipient-cap). The scripts that built the rows and ran the gates are not in the repository; the statements they time are the code's own.

| Gate at 250,000 | Measured, two runs | Limit |
| --- | ---: | ---: |
| Throw to one definition, and to 250 definitions (500 share jobs) | 3.3 / 3.5 s, and 4.1 / 4.3 s | 10 s |
| Refused throw | 3.9 / 4.0 s | 10 s |
| Cancel | 4.9 / 4.3 s | 10 s |
| Settle of an expired broadcast | 3.9 / 3.8 s | 10 s |
| Retry when every delivery failed | 5.5 / 5.2 s | 10 s |
| One recount | 81 ms, up to 226 ms just after an update of every delivery | 250 ms |
| Lost lease of one share, 62,500 deliveries | 1.5 / 1.5 s | 2 s |
| Lost lease of a job that owns all 250,000 | 5.4 / 4.4 s | 10 s |
| Retention's delete of the finished broadcast | 0.30 / 0.40 s, under its own 5 s timeout | 10 s |
| Storage | 145 MB of write-ahead log per throw; about 170 bytes of table and 190 of index per delivery | none |

Every gate passed, so the cap rose. The retention delete passed without the change section 8 describes for a failure, so retention is unchanged. The closest margins are the recount right after a whole-broadcast update and the lost lease of one share; widening the database check took 9 ms on 2,151 broadcasts.

- **Code.** `SignalSettings.MaxSubscriptionsPerBroadcastCap` is 250,000. [RaiseSignalBroadcastRecipientCap](../../Flowbit/src/Flowbit.Infrastructure/Data/Migrations/20261126185620_RaiseSignalBroadcastRecipientCap.cs) replaces `CK_signal_broadcasts_counts`, rewrites the setting's seeded description while it is unchanged, and refuses its down migration while a broadcast has more than 100,000 deliveries.
- **Tests.** `SignalRecipientCapSettingTests` (the parsed cap, and the model's check holding the same number) and `SignalRecipientCapMigrationTests` (250,000 accepted and 250,001 refused, down refused above 100,000, the older limit after down, and an operator's description kept). After the change `dotnet test Flowbit/Flowbit.slnx` passed 6,622 of 6,622 in 20 m 45 s, and `dotnet ef migrations has-pending-model-changes` reports none.
- **A limit found on the way.** Retention deletes up to 250 broadcasts in one statement under a 5-second timeout. Two finished broadcasts of 250,000 took 0.7–0.8 s together, so a batch of about a dozen such broadcasts would time out, and retention retries the same batch. The cap of 100,000 had the same limit at about thirty. The deployment guide tells operators who keep many large broadcasts to lower `FlowbitWorker:RetentionBatchSize`; a batch bounded by the total of `TotalDeliveryCount` would remove the limit.

### Not done

The user set the status to Done on 2026-11-26 with these left as they are:

- **Eight jobs.** The run with a maximum of 8, which needs a Worker with 8 share slots, was not made. No criterion needs it: the default is 2, and the recorded runs show four jobs already saturating the reference machine.
- **One test of the test table.** No test counts the statements of a lease round. Its claim holds by construction: the change adds one parameter to the same three acquisition queries and no statement. The retention row of the table does not apply, because retention's delete did not change.
- **Unexercised commands.** The Bash commands of the load test's README, whose `ss` lookup needs Linux.
- **Verification.** The browser smoke suite was not run; no page changed. Done on the final tree, after both defaults became 2: `dotnet test Flowbit/Flowbit.slnx` passed 6,615 of 6,615 in 21 m 56 s, and the signal acceptance scenarios S1 to S16, with the published API, UI, and Worker, passed 22 of 22 cases in 6 m 44 s. Their broadcasts reach at most 400 waits, so they cover unsplit delivery, cancel, lease recovery, retry, and expiry on this build; split delivery with a real Worker is covered by the recorded runs and the manual check below. `dotnet ef migrations has-pending-model-changes` reports none. An earlier full run, before the default changes, passed 6,606 of 6,607, and its one failure, the engine settings reference's stated count of 25 settings, passes after it was changed to 27. The manual check on a local stack with the published API, UI, and Worker passed: 2,000 claims thrown from **Signals** were delivered by share jobs (two while the setting was 2, in 75 s); a second throw with the setting at 4 ran four `deliverShare` jobs while `flowbit_worker_jobs_signal_share_active` read 4 and the runnable depth 0, and **Cancel pending deliveries** during it left 584 delivered once and 1,416 cancelled, with all four jobs completed on their first attempt and the gauges back at 0.

### Before the change

Verified on 2026-11-25 at commit `033a603`, when the [plan](#plan) was written and agreed:

- [SignalBroadcastJobProcessor.cs](../../Flowbit/src/Flowbit.Service/Services/SignalBroadcastJobProcessor.cs) handles one job for one broadcast and definition. It loops over pages of 100 deliveries (`DeliveryPageSize`), with a new scope and transaction for each delivery, on the `activity` queue.
- A broadcast reaches at most `Signals.MaxSubscriptionsPerBroadcast` recipients (default 10,000, hard cap 100,000), and at most `Signals.MaxOpenBroadcasts` broadcasts (default 20) deliver at once ([engine settings](../engine-settings.md#signal-settings)).
- The [EVENT-04 performance run](event-04-signal-events.md#current-state) on 2026-11-16, on a two-core machine: a throw froze 10,000 recipients in 493 ms and 100,000 in 2,202 ms, cancelling 100,000 took 1,753 ms, and one job delivered 10,000 recipients in 498 s.

### Facts the plan rests on

Verified in the code on 2026-11-25.

- **Job creation.** `EnqueueDeliveryJobsAsync` in [SignalBroadcastWriter.cs](../../Flowbit/src/Flowbit.Service/Services/SignalBroadcastWriter.cs) creates one job for each workflow definition among the recipients, for a throw and for a retry of failed deliveries. A job has the kind `signalBroadcastDeliver`, the phase `deliver`, the class `activity`, no instance, and four attempts with delays of 10 seconds, one minute, and five minutes. Its payload is `SignalBroadcastJobPayload(BroadcastId, WorkflowDefinitionId)`, and one `signal_broadcast_jobs` row links it to its broadcast.
- **Paging.** `ListPendingDeliveryIdsAsync` in [SignalBroadcastRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/SignalBroadcastRepository.cs) reads the next 100 pending delivery IDs of a broadcast and definition after a cursor, through the index on `(BroadcastId, WorkflowDefinitionId, Status, Id)`.
- **Exactly once.** `DeliverSignalAsync` in [WorkflowEngineService.Signals.cs](../../Flowbit/src/Flowbit.Service/Services/WorkflowEngineService.Signals.cs) reads the delivery without a lock, then locks the instance hierarchy, the subscription, and the delivery row, and returns when the row is no longer pending. The processor's `RecordAsync` does the same before it records a failure. Two jobs that meet on one delivery therefore cannot both end it: the second waits for the instance and then writes nothing.
- **Jobs can already overlap.** A retry of failed deliveries enqueues a new job for a definition while an earlier job of that definition can still wait in a retry delay or an incident. Correctness has never depended on one job per definition.
- **Recount.** A job recounts after every page and at its end, in a transaction that holds the broadcast's row lock. `RefreshCountsAsync` counts every delivery row of the broadcast each time.
- **Leasing.** `LeaseRunnableAsync` in [WorkflowJobRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/WorkflowJobRepository.cs) leases `control` jobs first and then `activity` jobs up to `MaxActivityCount`. One round returns at most one job for each fairness key: the instance, or the workflow family for a job without an instance. A delivery job has no instance, so the jobs of one family are leased one per round. [JobDispatcher.cs](../../Flowbit/src/Flowbit.Worker/JobDispatcher.cs) runs the next round at once while a round returned jobs and slots remain. It knows the queue class of each running job and nothing else about it.
- **Slots.** A Worker has 8 slots, of which activity jobs may use 6 ([WorkerOptions.cs](../../Flowbit/src/Flowbit.Worker/WorkerOptions.cs)). A broadcast that reaches several definitions already runs one job for each at the same time, and can use every activity slot.
- **Lost lease.** `ReconcileExhaustedSignalBroadcastsAsync` ends every pending delivery of the job's broadcast and definition, found through the job's link row, as `failed` with `lease_exhausted`, or as expired after the expiry.
- **Cancel, expiry, retry.** A cancel and an expiry each end every pending delivery of a broadcast in one `UPDATE` under the broadcast's row lock. `RetryFailedDeliveriesAsync` returns the definitions that have pending deliveries after the retry, and `RetryFailedAsync` in [SignalBroadcastService.cs](../../Flowbit/src/Flowbit.Service/Services/SignalBroadcastService.cs) enqueues one job for each.
- **Limits.** `SignalSettings` in [SignalModels.cs](../../Flowbit/src/Flowbit.Service/Models/SignalModels.cs) holds the keys, defaults, and hard caps. The API's throw and the engine's `ThrowSignalFromNodeAsync` read them for each throw, and each read through `GetByKeyAsync` is a database query of its own. The check `CK_signal_broadcasts_counts` also holds `TotalDeliveryCount` to 100,000 ([AppDbContext.cs](../../Flowbit/src/Flowbit.Infrastructure/Data/AppDbContext.cs)).
- **Timeouts.** The API and the Worker set no command timeout, so Npgsql's 30 seconds apply. The retention pool sets a command timeout of 5 seconds ([RetentionDataSource.cs](../../Flowbit/src/Flowbit.Infrastructure/Data/RetentionDataSource.cs)), and each retention transaction also sets the server's `lock_timeout` and its `statement_timeout`, the latter from an option that defaults to 5 seconds and accepts at most 30.
- **Queue statistics.** `GetQueueStatisticsAsync` and retention's overload check count every due job of a queue class, whatever its kind. Retention pauses while 100 jobs are due or one has been due for 30 seconds. The Worker's `flowbit.worker.queue.lag` histogram records, for every job it leases, the time since the job was due. `GetNextWakeAtAsync` returns the earliest time any job is due, and a Worker that sees a due job polls at `PollMilliseconds` instead of its idle backoff.
- **Retention.** The `signalBroadcasts` category deletes finished broadcasts in batches with one `DELETE` on `signal_broadcasts`; their deliveries go by `ON DELETE CASCADE` in the same statement ([RetentionRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/RetentionRepository.cs)). No recorded run removed a broadcast with 100,000 deliveries.
- **API and pages.** No DTO exposes a broadcast's jobs. The **Signals** page shows the stored counters and the deliveries.
- **Hash function.** `hashint8` is callable and immutable on PostgreSQL 17.10. Over the 10,000 even numbers from 2 to 20,000 it filled four shares with 2,444, 2,514, 2,536, and 2,506.
- **Load test.** The [lifecycle load test](../../Flowbit/tools/LifecycleLoadTest/README.md) throws many small signals. It has no case for one large broadcast.

### What the measurements do and do not show

- **The baseline is on another machine.** The 20.4 deliveries per second come from a two-core machine. The [capacity runs](../deployment.md#timers-signals-and-the-worker) and [PERF-01](perf-01-throughput-and-worker-capacity.md#current-state) used a machine with 4 logical processors.
- **More jobs may not mean more deliveries.** On the 4-processor machine one Worker finished at most about 26 jobs per second, with 16 slots and more jobs due than it could run. That is close to what one delivery job does alone. A delivery inside a job's loop costs less than a job of its own, so the two figures are not the same measure, but they are a reason to measure before building.
- **The connection settings change the baseline.** Automatic statement preparation raised signal cases from 0.43–0.58 to 1.44 per second in PERF-01's runs. No delivery rate has been recorded with it.
- **No run delivered one definition with several jobs.** The first step of this plan does, with today's code.
- **The 100,000 recipients of the parent's run.** The parent's page records the durations and not how the recipients were produced.

### Plan review of 2026-11-25

A review of the plan against the code, on the day it was written, found one error and these gaps. Each is corrected in the sections below.

- **The recipient cap is also a database check.** The plan said that raising the cap needs no migration. `CK_signal_broadcasts_counts` holds `TotalDeliveryCount` to 100,000, so a larger throw would fail. [Section 3](#3-persistence-and-migration) now has the second migration, and the documentation table the pages that state the limit.
- **Waiting share jobs read as a backlog.** Retention's overload check and the queue metrics count every due job. With more share jobs queued than share slots, retention would pause for the whole broadcast and the queue metrics would report a Worker that cannot keep up. [Section 5](#5-the-worker) now leaves share jobs out of those statistics.
- **One split definition fills the share slots.** At the defaults a split definition has four jobs and a Worker four share slots, so a second split broadcast waits for a job of the first to end, where today it starts at once. The plan keeps four jobs and states the limit in [section 1](#1-scope-and-invariants) and in the guides.
- **The stop rule could not tell a busy machine from a design that does not help.** The reference machine runs PostgreSQL, the API, the Worker, and the load tool on four processors. The rule now applies only while processors are idle, and the plan says what happens to a result between the gate and the criterion.
- **API requests were not measured.** Four delivery loops take database capacity from users, and splitting is on by default. The evidence now has a purchase-request run, with a rule for the default.
- **Every throw read two more settings.** Each read is a query, and workflows throw thousands of small signals. The settings are now read after the freeze, in one query, and only when a definition has two or more recipients.
- **Retention has two timeouts.** The plan named the command timeout only. A retention transaction also sets the server's `statement_timeout`, which accepts at most 30 seconds.
- **The lost-lease gate left out its worst case.** With splitting off, one job owns every delivery of a definition, 250,000 at the new cap.
- **The plan did not say how 250,000 recipients are produced.** Real waiting instances would take about 1.6 hours to start at the measured 43 starts per second and add about 3 GB.
- **A Worker without share slots never became idle.** It would see due share jobs it never leases and poll every second.
- **Smaller corrections.** The rolling-upgrade tip named a setting row that does not exist before the migration. The deadlock test read a counter that is database-wide and updated late. The gates recorded time and not storage. Deliveries that take a shared-variable row are named as a limit.

Checked and left unchanged:

- **Incident retry.** It queues the same job row again, so a share job keeps its payload and phase.
- **Unique indexes.** None on `workflow_jobs` or `signal_broadcast_jobs` prevents several jobs for one broadcast and definition.
- **Operation descriptions.** No API description states how many delivery jobs a broadcast gets.
- **Webhook bodies.** A counter of 250,000 has six digits, as 100,000 has, so the bound on a broadcast event's body holds.

## Builds on

- Job leasing, heartbeats, retries, incidents, exhausted-lease reconciliation, and the cursor paging of `ListPendingDeliveryIdsAsync`.
- The delivery's own fence: the delivery row lock and the pending check, which already make overlapping jobs safe.
- The Worker's activity concurrency setting, and the fairness key that leases one job of a family in a round.
- The cancel and expiry statements, which end a whole broadcast under its row lock whatever jobs exist.
- `SignalSettings.ParseLimit` and the engine settings read for each throw.
- The webhook delivery queue, as the model of a queue with its own concurrency and connection pool, for the [later release](#later-releases).

## Open questions

No question remains open for the design. One is answered by a measurement that is the plan's first step and can stop the item. The user agreed on 2026-11-25 to the decisions that change what an operator sees: the two settings, the Worker option, splitting by default, and the new hard cap. The user accepted the corrections of the [plan review](#plan-review-of-2026-11-25) the same day; they keep four jobs by default and leave waiting share jobs out of the queue statistics. After the evidence runs the user lowered the default to two jobs on 2026-11-26 ([decision](#decision-of-2026-11-26)); the rows and sections below that named four say so.

| Question | Decision |
| --- | --- |
| Does parallel work stay on the `activity` queue, or does delivery get a queue and pool of its own, as webhooks have? | It stays on `workflow_jobs` and the `activity` class. Delivery jobs then keep their leases, heartbeats, retries, incidents, and the lost-lease reconciliation without a second implementation. A Worker option caps the jobs of split broadcasts. A queue of its own stays a [later release](#later-releases). |
| Do jobs divide the deliveries by ID range, or does each job skip rows that another job has locked? | Neither. Each job takes the deliveries whose instance falls in its share, by a hash of the instance ID. An ID range can put two waits of one instance into two jobs, which then queue on that instance. Skipping locked rows needs a stored claim on each delivery, because every delivery commits on its own; that is one more write for each delivery, and claims to recover after a crash. A hash needs no write and no column on the delivery. Its cost is that no job takes over another job's share. |
| Is the rate limited by the cost of one delivery transaction and not by the number of jobs? | Not known. [Step 1](#12-delivery-sequence-rollout-and-risks) measures it with today's code before anything is built, and the item stops if two jobs do not deliver at least 1.5 times what one does on a machine with idle processors. |
| What limits the parallel jobs of one broadcast, and of all broadcasts together? | One broadcast gets at most `Signals.MaxDeliveryJobsPerDefinition` jobs for each definition, decided at the throw. All split broadcasts together run at most `FlowbitWorker:SignalShareConcurrency` jobs on each Worker. |
| Is every broadcast split? | No. A definition needs at least twice `Signals.MinDeliveriesPerJob` recipients, 1,000 by default. Workflows throw thousands of small broadcasts, and each stays one job with today's phase, payload, and paging query. |
| Is splitting on by default? | Yes, with two jobs since the [decision of 2026-11-26](#decision-of-2026-11-26); the plan had four. A deployment that never throws a large broadcast sees no change, and one that does gets the gain without tuning. The value `1` turns splitting off. |
| Does the Worker's cap also bound the jobs of small broadcasts? | No. A cap on every delivery job would make small signals wait behind a large broadcast, which they do not do today. The cap counts only jobs with the phase `deliverShare`. |
| Why is the cap a Worker option and not an engine setting? | Slots belong to a Worker process, as `ActivityConcurrency` does. A deployment can give one Worker many share slots and another none. |
| Does a changed setting affect a broadcast that is already delivering? | No. The number of jobs is decided at the throw, and again at each retry of failed deliveries. |
| In which order are deliveries made? | In ascending ID inside each job. A split broadcast has no order across its jobs. |
| Can one broadcast take every share slot? | With four jobs, yes: a split definition then has four jobs and a Worker four share slots, and a second split broadcast waits for a job of the first to end. At the defaults since 2026-11-26, two jobs and two share slots, one split definition fills a Worker's share slots and a second waits. Fair delivery among broadcasts is a [later release](#later-releases). |
| Do share jobs that wait for a slot count as a backlog? | No. They are held back on purpose. Retention's overload check, the queue gauges, and the lag histogram leave them out, and the signal gauges show the pending deliveries. |
| What is the new hard cap? | 250,000, if every gate of [section 8](#8-the-recipient-limit) passes; a migration then widens `CK_signal_broadcasts_counts`. The default stays 10,000. |
| Does the API or the **Signals** page show the jobs? | No. Progress is the stored counters, as today. The jobs are on the **Jobs & incidents** page with their phase. |
| How does this item relate to PERF-01? | [PERF-01](perf-01-throughput-and-worker-capacity.md) phase 1 changes the baseline, and phase 4 edits the same leasing code. Build this item after both, or rebase whichever lands second. The default of the share cap follows `ActivityConcurrency`, so it keeps its meaning if phase 4 changes the slots. |

## Plan

Written on 2026-11-25 following [Writing a plan](README.md#writing-a-plan). Effort stays **M**: one migration and a second if the cap rises, the throw and retry paths, the delivery job, job leasing and its statistics, the Worker's dispatcher, and the load test. The item changes no workflow JSON, no editor or viewer code, no API contract, and no Flowbit.Ui page, so the plan has no section for those.

### 1. Scope and invariants

A split broadcast is the same broadcast with more jobs. Nothing a caller, a receiver, or an instance sees changes except how soon the last delivery runs.

The invariants of the [parent plan](event-04-signal-events.md#1-scope-and-invariants) and of [EVENT-04E](event-04e-signal-broadcast-expiry.md#1-scope-and-invariants) hold unchanged. These matter here:

1. **The delivery's own fence decides exactly once.** The delivery row lock and the pending check end each delivery once, whatever jobs exist. The split never carries correctness; it only keeps jobs from meeting.
2. **One instance, one job.** Within one set of jobs, every delivery to an instance is in the same share, so two jobs of a broadcast do not wait on one instance's row.
3. **A delivery's transaction is unchanged.** It takes the same locks in the same order, never the broadcast row, and makes no external call.
4. **A share is computed, never stored.** The page query and the lost-lease statement compute it in SQL from the instance ID, with one shared expression. No delivery row records a job.
5. **A small broadcast is untouched.** One job, the phase `deliver`, the same payload, the same paging query, and the same leasing.
6. **The split is decided once.** Settings are read at the throw and at a retry of failed deliveries. Jobs that exist keep their shares.
7. **A job ends only its own share when it loses its last lease.** The other jobs of the definition keep delivering.
8. **Workflows without signals pay nothing.** No transition runs a new statement, and a lease round runs the same number of statements.
9. **A throw to one recipient for each definition runs no new statement.** The split settings are read only when a definition has two or more recipients.

This release does not add a queue of its own, fair delivery among broadcasts, a bound on the jobs of a broadcast that reaches many definitions, work moving between jobs, a cheaper delivery transaction, or any change to scopes, freezing, expiry rules, version changes, or reactivation.

Seven limits follow; the seventh was added on 2026-11-26 from the evidence:

- **No rebalancing.** A job that waits in a retry delay or an incident leaves its share pending while the others finish. At most one share of a definition is affected by one job.
- **Unsplit jobs are not capped.** A broadcast that reaches 50 definitions with 200 recipients each still runs one job for each definition, up to every activity slot, as today.
- **The gain depends on the database.** Several jobs help while PostgreSQL and the Worker have processors to spare. On a machine that one job already saturates, they do not.
- **One split definition can fill the share slots.** At the defaults since 2026-11-26, a split definition's two jobs take both of a Worker's two share slots; with four of each, as first planned, it took all four. A second split broadcast, or a second split definition of the same one, starts only when one of those jobs ends: after about six minutes at 10,000 recipients and the measured rate of two jobs, and far later at the cap. Its expiry keeps counting meanwhile. Today it would start at once in another activity slot.
- **A split broadcast takes database capacity from requests.** Four delivery loops do the work of one in a quarter of the time, on the database that serves the API. The evidence measures it, and decides the default.
- **Deliveries that take the same row still run one at a time.** A route after the catch that uses a shared variable locks that variable's row, so such deliveries queue across jobs as they do across requests today, and gain less from the split. The review did not trace this path; step 1 names the definition it measures.
- **Other activity jobs start later while a split broadcast delivers.** The share cap leaves them their activity slots, but the share jobs load the database they also use. At the default of two jobs on the reference machine, small signal deliveries began 0.10 / 0.73 s after their due time (p50 / p95) against 0.07 / 0.24 s during one job, and reminder scripts 0.16 / 1.79 s against 0.12 / 0.52 s; timers stayed on time. Accepted by the user as a limit; `Signals.MaxDeliveryJobsPerDefinition` = `1` removes it.

### 2. How a broadcast is split

Two engine settings, read at a throw and at a retry of failed deliveries when a definition has two or more recipients, in the format of the other [signal limits](../engine-settings.md#signal-settings):

| Setting | Default | Hard cap | Meaning |
| --- | ---: | ---: | --- |
| `Signals.MaxDeliveryJobsPerDefinition` | 2 (planned as 4; [changed 2026-11-26](#decision-of-2026-11-26)) | 32 | The most delivery jobs a broadcast gets for one workflow definition. `1` turns splitting off. |
| `Signals.MinDeliveriesPerJob` | 500 | 100,000 | The fewest recipients a job is created for when a definition's recipients are split. |

For a definition with `n` recipients the number of jobs is `n / MinDeliveriesPerJob`, rounded down, and then held between 1 and `MaxDeliveryJobsPerDefinition`:

| Recipients of one definition | Jobs at the defaults | Jobs with a maximum of 4 | Jobs with a maximum of 16 |
| ---: | ---: | ---: | ---: |
| 999 | 1 | 1 | 1 |
| 1,000 | 2 | 2 | 2 |
| 1,999 | 2 | 3 | 3 |
| 2,000 | 2 | 4 | 4 |
| 10,000 | 2 | 4 | 16 |
| 250,000 | 2 | 4 | 16 |

- **Shares.** A definition with `k` jobs has the shares 0 to `k - 1`. A delivery belongs to the share

  ```sql
  (hashint8(COALESCE(d."InstanceId", 0))::bigint & 2147483647) % @shareCount
  ```

  `hashint8` returns a signed 32-bit value; the mask makes it non-negative before the remainder. A start delivery has no instance until it is delivered, so the one start of a definition falls in the share of 0.
- **Why a hash and not the remainder of the ID.** Instance IDs follow patterns. A parent that calls one child for each case gives the children every second ID, and a plain remainder would then fill only half of the shares.
- **The function is never stored.** Both statements that use the expression run in the database at the time they need the answer, so its value need not be the same in another PostgreSQL version. The expression is one constant in `Flowbit.Infrastructure`, used by both.
- **Job shape.** A job of a definition with one job is exactly today's job. A job of a definition with several has the phase `deliverShare`, and its payload adds `ShareIndex` and `ShareCount`. A payload without them reads as share 0 of 1, so jobs queued before the upgrade run unchanged.
- **Code.** `SignalDeliveryShares` in `Flowbit.Service/Models` owns the job count. `EnqueueDeliveryJobsAsync` takes each definition with its recipient count, which `ListRecipientDefinitionsAsync` already returns, and still inserts all jobs and all links in two statements.
- **Reading the settings.** `EnqueueDeliveryJobsAsync` reads both settings itself, in one query, and only when a definition has two or more recipients, the fewest that any setting can split. Each settings read is a query, and workflows throw thousands of signals that reach one wait, so the callers do not read them beside the recipient limit.

### 3. Persistence and migration

One migration, `AddSignalDeliveryShares`, in a transaction. Update the entity, `AppDbContext`, and the model snapshot with it.

| Object | Change |
| --- | --- |
| `signal_broadcast_jobs."ShareIndex"` | New `integer`, not null, default 0. |
| `signal_broadcast_jobs."ShareCount"` | New `integer`, not null, default 1. |
| `CK_signal_broadcast_jobs_share` | New check: `ShareCount` is from 1 to 32, and `ShareIndex` is from 0 to `ShareCount - 1`. |
| `engine_settings` | Seeds `Signals.MaxDeliveryJobsPerDefinition` = `2` (planned as `4`) and `Signals.MinDeliveriesPerJob` = `500` with descriptions, when missing. |

- **Existing rows** read as share 0 of 1, which is the whole definition, as today.
- **Cost.** Both columns change only the catalog, and the check scans `signal_broadcast_jobs`, which has one row for each delivery job.
- **Deliveries.** `signal_deliveries` gets no column and no index. The paging index serves the share query; the share condition is a filter on the rows it reads.
- **Down migration.** It refuses while a job with the phase `deliverShare` is not finished. Without the columns, a lost lease would fail the whole definition's deliveries. It then drops the check, the columns, and the two settings.
- **Recipient cap.** `CK_signal_broadcasts_counts` holds `TotalDeliveryCount` to 100,000, so the constant `SignalSettings.MaxSubscriptionsPerBroadcastCap` cannot rise alone. If the gates of [section 8](#8-the-recipient-limit) pass, a second migration, `RaiseSignalBroadcastRecipientCap`, replaces the check with a limit of 250,000. It scans `signal_broadcasts` once in its transaction, as the checks of `AddSignalBroadcastExpiry` did. Its down migration refuses while a broadcast has more than 100,000 deliveries; retention removes such a broadcast once it is finished and old enough. If a gate fails, this migration is not written.

### 4. The delivery job

Changes are in `SignalBroadcastJobProcessor` and `ListPendingDeliveryIdsAsync`.

- **Paging.** A job with `ShareCount` 1 runs today's query. A job with more runs the same query with the share condition of [section 2](#2-how-a-broadcast-is-split):

  ```sql
  SELECT d."Id"
  FROM flowbit.signal_deliveries AS d
  WHERE d."BroadcastId" = @broadcastId
    AND d."WorkflowDefinitionId" = @definitionId
    AND d."Status" = 'pending'
    AND d."Id" > @afterId
    AND (hashint8(COALESCE(d."InstanceId", 0))::bigint & 2147483647) % @shareCount = @shareIndex
  ORDER BY d."Id"
  LIMIT 100;
  ```

  It reads about `ShareCount` index entries for each ID it returns. A job's last page, which finds nothing, reads the pending rows the other jobs still hold; that happens once for each job.
- **Deliveries, failures, and retries** are unchanged. A delivery that fails unexpectedly stays pending, and its own job retries after its delay. The other jobs complete.
- **Expiry** is unchanged. Every job asks before each page. The first one that finds the expiry passed settles the whole broadcast, and the others find nothing pending.
- **Recount.** An unsplit job recounts after every page, as today. A share job recounts after a page only when two seconds have passed since its last recount, and always at its end. Several jobs would otherwise recount a large broadcast several times a second, each time counting every row under the broadcast's row lock. The **Signals** page polls every three seconds, so progress stays current.
- **Finish.** The broadcast finishes in the recount that first finds nothing pending, which is the last job's final recount, a cancel, or a settle.
- **Backlog gauge.** `GetBacklogAsync` computes the pending count from the stored counters. It can run ahead of the deliveries by the pages the jobs are working on, as today by one page.

### 5. The Worker

| Piece | Change |
| --- | --- |
| `WorkerOptions.SignalShareConcurrency` | New optional whole number: how many jobs with the phase `deliverShare` this Worker runs at once. From 0 to `ActivityConcurrency`. Unset, it is 2, or `ActivityConcurrency - 2` when that is less, and never below 1 unless `ActivityConcurrency` is 0 (planned as `ActivityConcurrency - 2`; [changed 2026-11-26](#decision-of-2026-11-26)). At the defaults that is 2 of 6 activity slots. |
| `WorkflowJobLeaseRequest.MaxSignalShareCount` | New last parameter, unlimited by default, so every existing caller and test leases as before. |
| `JobDispatcher` | Remembers for each running job whether it is a share job, and passes the option minus the running share jobs. Its start line reports the share slots. |
| `LeaseRunnableAsync` | When the request allows no share job, the three acquisition queries leave share jobs out before they rank. When it allows some, the selection loop stops taking share jobs at that number. |
| `GetNextWakeAtAsync` | Takes whether this Worker has any share slot. A Worker whose option is 0 gets the next wake time without share jobs. |
| `GetQueueStatisticsAsync` and retention's overload check | Leave out jobs with the phase `deliverShare`. |
| `WorkerTelemetry` | New gauge `flowbit.worker.jobs.signal_share.active`. The `flowbit.worker.queue.lag` histogram leaves out share jobs. |

The condition in each acquisition query:

```sql
AND (@signal_shares_allowed
     OR j."Kind" <> 'signalBroadcastDeliver'
     OR j."Phase" <> 'deliverShare')
```

- **Why the queries filter, and not only the loop.** A round returns the first job of each family. If the first job of a family were a share job the loop then skipped, a service task or a batch job of that family would wait behind it. With the filter, the ranking sees only jobs this Worker may run.
- **Recovered leases.** The filter is in all three queries, so a share job whose lease expired is picked up again only inside the allowance.
- **Rounds.** The share jobs of one family are leased one per round, and the dispatcher runs rounds back to back while slots remain. Four jobs start within four rounds.
- **Several Workers.** Each has its own cap. Two Workers at the defaults run eight share jobs, so a deployment with more Workers raises `Signals.MaxDeliveryJobsPerDefinition` to use them.
- **A Worker with no share slot** leases no share job. A deployment in which every Worker has 0 never delivers a split broadcast; the backlog gauge shows it, and the guide warns against it.
- **Waiting share jobs are not a backlog.** A share job that is due and waits for a share slot is held back on purpose. Counted as today, a second split definition at the defaults leaves its two jobs due for minutes. Retention pauses once a job has been due for 30 seconds, so it would stop for the whole broadcast, and the queue gauges and the lag histogram would report a Worker that cannot keep up. Those three statistics therefore leave share jobs out. The work stays visible in `flowbit.signals.deliveries.pending`, in the age of the oldest open broadcast, and in the new gauge of running share jobs; the guide tells operators to alert on those for large broadcasts.
- **A share job that never starts is not hidden either.** When no Worker has a share slot, the same signal gauges show it: the pending count stays, and the oldest open broadcast ages.
- **Filling the share slots.** One split definition at the defaults takes both of a Worker's two share slots, as [section 1](#1-scope-and-invariants) states. A deployment that throws large broadcasts close together gives its Workers more share slots than `Signals.MaxDeliveryJobsPerDefinition`, or lowers that setting.
- **Idle polling.** A Worker whose share slots are all busy is not idle, so its polling does not change. A Worker with no share slot would see due share jobs it never leases and poll every `PollMilliseconds`; the change to `GetNextWakeAtAsync` lets it reach its idle backoff. A finished job still refills its slot at once.
- **Connections.** Share jobs run inside `MaxConcurrency`, so the Worker's pool rule does not change.
- **Unchanged.** Control jobs are leased first, `MaxPerInstance` applies to jobs with an instance, and the fairness key stays as it is.

### 6. Cancel, retry, expiry, and a lost lease

| Operation | With several jobs for a definition |
| --- | --- |
| Cancel | Unchanged. One `UPDATE` under the broadcast's row lock ends every pending delivery. It waits for each delivery that is inside its transaction, up to one for each running job, and leaves it as it finished. The jobs then find nothing pending and complete. |
| Expiry | Unchanged. The settle is one statement under the row lock, a second settle does nothing, and the sweep covers a broadcast none of whose jobs runs. |
| Retry of failed deliveries | `RetryFailedDeliveriesAsync` returns the pending count of each definition, and the retry enqueues jobs by the rule of [section 2](#2-how-a-broadcast-is-split) with the settings as they are then. A few failed deliveries of a split broadcast become one job. |
| A retry while an earlier job still exists | The new jobs and the earlier one can cover the same deliveries, as today. Invariant 1 keeps each delivery to one outcome. |
| Lost lease | The delivery statement of `ReconcileExhaustedSignalBroadcastsAsync` adds the share condition with the link's `ShareIndex` and `ShareCount`, so it ends only that job's share. Its recount is unchanged: the broadcast stays `delivering` while other shares are pending. |
| Incident retry of a share job | The job runs again with its payload, and delivers what is still pending in its share. |
| Deleting a workflow version | Unchanged. It is refused while a delivering broadcast has a pending delivery of the version. |
| Webhook events | Unchanged. The status changes once, in one transaction, whoever writes it. |

`RefreshCountsAsync` and the lost-lease recount stay the only two statements that derive a status, and neither changes.

### 7. Transactions and lock order

| Transaction | Locks, in order | Changed |
| --- | --- | --- |
| Delivery to a wait | Instance hierarchy, subscription, delivery row. | No. |
| Delivery to a start | The family's shared start lock, delivery row. | No. |
| Recount, cancel, settle, retry | The broadcast row, then delivery rows. | No. |
| Lost lease | Job rows, broadcast rows in ID order, then the delivery rows of the job's share. | Fewer delivery rows. |
| Lease round | Job rows with `SKIP LOCKED`. | No. |

A delivery holds the rows of one instance and one delivery row, and never asks for a broadcast row. Every other transaction takes the broadcast row first and never a runtime row. More jobs add more of the same transactions at once; they add no new order, so no cycle is possible. Deliveries to different instances already run at the same time today, from the jobs of different definitions and of different broadcasts.

| Race | Result |
| --- | --- |
| Two jobs of a broadcast reach deliveries of one instance. | Does not happen within one set of jobs: one instance is in one share. |
| A retry job and an earlier job reach the same delivery. | The second waits for the instance, finds the delivery no longer pending, and writes nothing. |
| A cancel while four jobs are each inside a delivery. | The `UPDATE` waits for the four rows, leaves them as they finished, and ends the rest. |
| Two jobs find the expiry passed. | The row lock orders their settles; the second does nothing, and one event is raised. |
| A job loses its last lease while the other jobs deliver. | Its share ends as `failed`. The other shares are delivered, and the broadcast finishes with issues when they are done. |
| A share job's final recount and another job's recount. | The row lock orders them. The one that finds nothing pending finishes the broadcast. |

PostgreSQL tests for the second, third, and fifth rows are a release gate, with a run of four jobs at once in which no delivery may fail or be retried for a deadlock (`40P01`).

### 8. The recipient limit

`Signals.MaxSubscriptionsPerBroadcast` keeps its default of 10,000. Its hard cap rises from 100,000 to 250,000 only if every statement below passes its gate with a broadcast of 250,000 recipients, on the machine of the [recorded runs](../deployment.md#capacity-planning). The gate for the API and the Worker is 10 seconds, a third of the command timeout.

| Statement | Measured so far | Gate at 250,000 |
| --- | --- | --- |
| Throw: the freeze, the grouping by definition, and the job insert | 2,202 ms at 100,000, two-core machine | 10 s, for one definition and for 250 definitions |
| Refused throw: the freeze of one recipient too many, then its delete | Not measured | 10 s |
| Cancel | 1,753 ms at 100,000 | 10 s |
| Settle of an expired broadcast | Not measured; the statement has the shape of a cancel | 10 s |
| Retry of failed deliveries when every delivery failed | Not measured | 10 s |
| One recount | Not measured | 250 ms |
| Lost lease of one share, 62,500 deliveries | Not measured | 2 s, because it runs inside a lease round |
| Lost lease of a job that owns a whole definition, 250,000 deliveries, with splitting off | Not measured at any size | 10 s |
| Retention delete of the finished broadcast | Not measured; both retention timeouts are 5 s | 10 s, with the change below |
| Storage | Not measured | No gate. Record the table, index, and write-ahead-log size one broadcast adds. |

- **How the recipients are produced.** The delivery runs of [section 11](#11-tests-and-verification) use real waiting instances, 10,000 of them. The gates deliver nothing, so they use rows inserted by SQL: subscription rows for the two throw gates, and delivery rows of a stored broadcast for the others. 250,000 real instances would take about 1.6 hours to start and add about 3 GB. Such rows skip the engine's rules, so no gate result says anything about delivery. The page records the statements used.
- **Retention.** Measure the delete at 100,000 first. If one finished broadcast of that size cannot be removed inside five seconds, today's cap already has a defect, and this item fixes it. The category then deletes broadcasts whose `TotalDeliveryCount` adds up to at most 20,000 in one statement, at least one broadcast. For that statement it raises both timeouts, the command's and the transaction's `statement_timeout`, to 30 seconds, the most the retention option accepts. The cap rises only if the delete at 250,000 then takes at most 10 seconds. A finished broadcast is still read and can be retried while it is deleted; a retry waits for the delete and then finds no broadcast.
- **The database check.** Passing the gates is what allows the migration of [section 3](#3-persistence-and-migration).
- **Splitting off at the new cap.** A deployment that raises the limit above 100,000 keeps splitting on. With `Signals.MaxDeliveryJobsPerDefinition` at `1`, one job owns every delivery, and a lost lease then ends them all in one statement inside a lease round.
- **A throw from a workflow** freezes its recipients inside the instance's transaction, and holds that instance for the time the freeze takes. The guide says so beside the setting.
- **Delivery time.** At the new cap, four jobs at the measured rate decide how long the broadcast is open. The guide gives the figure from the recorded run.
- **If a gate fails,** the cap stays at 100,000, the figures are recorded under [Current state](#current-state), and the criterion is closed as not raised.

### 9. API, authorization, and Flowbit.Ui

No route, request, response, scope, or page changes.

- The two settings are ordinary engine-setting rows, managed on **Engine settings** by a settings administrator.
- A job's `phase` in the job routes and on the **Jobs & incidents** page can read `deliverShare`.
- `GET /api/signal-broadcasts/{id}/deliveries` still lists deliveries in ascending ID. The order in which they are delivered is no longer one sequence for a split broadcast.

### 10. Documentation

| Owning page | Update |
| --- | --- |
| [Engine settings](../engine-settings.md#signal-settings) | The two new settings, with the job table of [section 2](#2-how-a-broadcast-is-split) and how to size them against the Workers' share slots. The new hard cap of `Signals.MaxSubscriptionsPerBroadcast`, if it rises, with what a throw of that size costs and the advice to keep splitting on above 100,000. The seed table. |
| [Deployment](../deployment.md#signal-events) | **Capacity** under signal events: the split, the share slots, that small broadcasts are not capped, and that a broadcast to many definitions still takes a slot for each. **Failures**: a lost lease fails one share. A rollout part with the order of [section 12](#12-delivery-sequence-rollout-and-risks), and a row for `AddSignalDeliveryShares` in the [upgrade table](../deployment.md#upgrade-and-compatibility-rules). `SignalShareConcurrency` in the [Worker options](../deployment.md#worker-operation). The recorded run under [timers, signals, and the Worker](../deployment.md#timers-signals-and-the-worker). The new gauge and the troubleshooting row for an `expired` broadcast, which gives one job's rate today, under [signals and actions](../deployment.md#signals-and-actions). There also: the queue gauges, the lag histogram, and retention's overload check leave share jobs out, so alerts for large broadcasts use the signal gauges, and a second split broadcast waits for a share slot. A row for `RaiseSignalBroadcastRecipientCap` in the upgrade table, if the cap rises. Retention, if its delete changes. |
| [Developer guide](../developer-guide.md#let-a-signal-expire) | "Size it for the whole broadcast" gives the rate of one job and says deliveries run in ascending ID. It gets the new figures and the order of a split broadcast. |
| [Database schema](../database-schema.md#signal_broadcast_jobs) | The two columns and the check. Under [`signal_broadcasts`](../database-schema.md#signal_broadcasts), the new limit of `CK_signal_broadcasts_counts`, if the cap rises. |
| [Runtime reference](../../Flowbit/README.md#signal-events) and [AGENTS.md](../../AGENTS.md) | The invariants of [section 1](#1-scope-and-invariants): the computed share, the one expression in two statements, the phase `deliverShare`, the share cap in leasing, and the lost lease of one share. The runtime reference's paragraph on paging in ascending ID. In AGENTS.md also the load test's new case. |
| [Lifecycle load test README](../../Flowbit/tools/LifecycleLoadTest/README.md) | The large-broadcast case, its options, and how to read its result, in PowerShell and Bash. |
| [API reference](../api-guide.md#signals) | The limits paragraph, which says a broadcast reaches at most 100,000 recipients, if the cap rises. The sentence on delivery order, if one states it; confirm when implementing. |
| This page, the [backlog](README.md), the [parent](event-04-signal-events.md#later-releases), and [PERF-01](perf-01-throughput-and-worker-capacity.md) | Evidence and status. PERF-01 learns which leasing code this item changed. |

Validate changed relative links and anchors. Keep the load test's commands aligned in PowerShell and Bash, and run them.

### 11. Tests and verification

PostgreSQL tests go in a new class `SignalDeliveryShareTests` in the `PostgresApiCollection`. They set `Signals.MinDeliveriesPerJob` to 2, so eight waits split into four jobs, and restore both settings at their end.

| Layer | Cases |
| --- | --- |
| Job count | `SignalDeliverySharesTests`: 0, 1, and 999 recipients give 1 job; 1,000 give 2; 1,999 give 3; 2,000 and 250,000 give 4. A maximum of 1 always gives 1. Both settings fall back to their defaults for missing, blank, zero, negative, and malformed values, and are capped. |
| Share assignment | For 10,000 consecutive instance IDs and for 10,000 even ones, each of four shares holds between 20% and 30%. Every pending delivery of a definition is listed by exactly one of the share queries for 2, 4, and 32 jobs, a start delivery included. Two waits of one instance are in one share. Share 0 of 1 lists what today's query lists. |
| Throw | Eight waits of one definition give four jobs with the phase `deliverShare`, links 0 to 3 of 4, and matching payloads. Three waits give one job with the phase `deliver` and a link 0 of 1. Two definitions with eight and three waits give four jobs and one. A maximum of 1 gives one job. A workflow's throw event splits the same way. A setting changed after the throw adds no job. A throw that reaches one wait runs the same number of statements as before, counted as the existing command-count test counts them; a throw that reaches two waits of one definition runs one more. |
| Delivery | Four share jobs run at the same time over 200 waits: every delivery is `delivered` with one attempt, every instance has one `signalReceived` row, the broadcast is `completed` with the right counters, and no delivery failed or was retried for a deadlock (`40P01`). |
| Overlap | A retry job and an earlier job cover the same deliveries: each is delivered once, and the later transaction writes nothing. |
| Unexpected failure | A delivery of one share fails unexpectedly: only its job retries, the other jobs complete, and the broadcast stays `delivering` until the retry. On the last attempt the delivery fails and the broadcast finishes with issues. |
| Cancel | A cancel while a delivery of each job is held open waits for them, leaves them as they finish, cancels the rest, and the jobs complete. |
| Expiry | The expiry passes while share jobs run: one settle ends the rest, the other jobs complete, the status is `expired`, and one webhook event is stored. |
| Lost lease | Job 1 of 4 loses its last lease: only the pending deliveries of its share fail with `lease_exhausted`, the other shares stay pending and are then delivered, and the incident opens. After the expiry they end as expired. A link 0 of 1 fails the whole definition, as today. |
| Retry failed | After failures in a split broadcast, a retry enqueues jobs by each definition's pending count with the settings of that moment: a few failures become one `deliver` job, many become share jobs. Each is delivered once. |
| Incident retry | Retrying the incident of a share job runs that share again. |
| Recount | A share job that finishes pages faster than every two seconds recounts at most once in two seconds and once at its end. An unsplit job recounts after every page. The broadcast finishes in the last job's final recount. |
| Leasing | In `DurableJobRepositoryTests`: with no share allowed, a round leases an unsplit delivery job, a service-task job of the same family, and jobs of other families, and no share job. With two allowed and four due, rounds lease two. A share job whose lease expired is leased again only inside the allowance. A request that sets no allowance leases as before. The existing lease and fairness tests pass unchanged. |
| Queue statistics | With share jobs due for longer than retention's lag limit and nothing else queued, `GetQueueStatisticsAsync` reports no runnable job and retention's overload check does not pause; an unsplit delivery job due that long still pauses it. The lag histogram records nothing for a leased share job. A Worker with no share slot gets no wake time from due share jobs, and one with a slot does. |
| Dispatcher and options | In `WorkerDispatcherTests`: the request carries the option minus the running share jobs. `WorkerOptions.Validate` refuses −1 and a value above `ActivityConcurrency`; unset gives 2 at the defaults and for any `ActivityConcurrency` of 4 or more, 1 for 1 to 3, and 0 for 0. |
| Migration | Up, then down, then up. Existing links read 0 of 1. The check refuses a `ShareIndex` at or above `ShareCount`. The seeds do not overwrite existing rows. Down is refused while a share job is not finished. If the cap rises: a broadcast with 250,000 deliveries is accepted and one with 250,001 refused, and the second migration's down is refused while a broadcast has more than 100,000. |
| Retention | If its delete changes: an old finished broadcast with many deliveries is removed, a batch stops at the delivery total, and the statement runs with both raised timeouts while the category's other statements keep theirs. |
| No cost without signals | The existing command-count test still finds no `signal_` statement for a workflow without signals, and a lease round runs the same number of statements as before. |
| Telemetry | The gauge counts running share jobs and returns to zero. |

Performance evidence, recorded on this page and in [Capacity planning](../deployment.md#capacity-planning). The lifecycle load test gains a large-broadcast case: it publishes [Example 01](../../examples/signals/01-signal-catch-policy-change.json) under its own family in a chosen number of versions, starts the waiting claims, throws once, and reports the throw's duration, the time until the broadcast finishes, the deliveries per second, and the jobs' run times with `--database`. Its report of how long jobs waited lists share jobs on a line of their own, because they wait for a share slot on purpose. Every delivery run records the host's processor use, split into PostgreSQL, the Worker, and the rest.

| Run | Records |
| --- | --- |
| Before any change: 10,000 waits in one version, then in two and in four versions of one family | Deliveries per second for one, two, and four jobs with today's code, the processor use, and the statements for each delivery from `pg_stat_statements`. This is the gate of step 1. The page names the definition, and whether its route after the catch uses a shared variable. |
| After the change: 10,000 waits in one version with a maximum of 1, 4, and 8 jobs | Deliveries per second, alternating the settings on one database. The criterion is at least twice the rate at 4 than at 1, on a machine that the run at 1 leaves at least one processor idle. |
| After the change: purchase requests at 8 users, alone, during a broadcast of 10,000 at a maximum of 1, and during the same broadcast at 4 | Requests per second and the **Approve** times for each. If four jobs cost more than a quarter of the throughput measured at one job, the default of `Signals.MaxDeliveryJobsPerDefinition` becomes 2, and the guide tells deployments with spare database capacity to raise it. |
| After the change: the timer and signal cases of the [capacity runs](../deployment.md#timers-signals-and-the-worker) while a split broadcast of 10,000 delivers | How late timer jobs, script jobs, and small signal deliveries begin, against the same run without the broadcast. |
| `tools/InstanceLoadTest`, 20,000 starts after a warm-up, alternating the base build and this one, both published to one disk | Starts per second for a workflow without signal events. |
| The statements of [section 8](#8-the-recipient-limit) at 100,000 and at 250,000, on rows inserted by SQL | Each duration against its gate, the storage one broadcast adds, and the statements that produced the rows. |

No page changes, so the [real-browser steps](../../AGENTS.md#mandatory-real-browser-ui-verification) do not apply. Delivery does change, so run with real Workers:

1. The signal acceptance scenarios S1 to S16 once on the final build.
2. Once by hand on a local stack with the published API, UI, and Worker: start 2,000 waiting claims of Example 01, throw from **Signals**, and watch the counters reach **Completed** while the **Jobs & incidents** page shows four jobs with the phase `deliverShare` and the Worker's `/metrics` shows the gauge. Cancel a second broadcast while it delivers.

Regression commands, run from the repository root with the same arguments in PowerShell 7 and Bash:

```text
dotnet test Flowbit/Flowbit.slnx --nologo --verbosity quiet
dotnet publish Flowbit/src/Flowbit.Api/Flowbit.Api.csproj -c Release -o artifacts/browser/hosts/api /p:UseAppHost=false
dotnet publish Flowbit/src/Flowbit.Ui/Flowbit.Ui.csproj -c Release -o artifacts/browser/hosts/ui /p:UseAppHost=false
dotnet build Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release
dotnet test Flowbit/tests/Flowbit.BrowserTests/Flowbit.BrowserTests.csproj -c Release --no-build --no-restore
git diff --check
```

Full solution runs have had order-dependent failures at the base commit, so compare failures against a base run before attributing one to this change. Before setting the status to Done, walk the test table case by case against the tests that exist.

### 12. Delivery sequence, rollout, and risks

1. **Measure.** Add the large-broadcast case to the load test and run the first row of the evidence table with today's code. Measure the retention delete at 100,000 in the same step. Then decide by the processor use of the run with one job:
   - **At least one processor idle, and two jobs deliver at least 1.5 times what one does:** continue.
   - **At least one processor idle, and two jobs deliver less:** the design does not help. Record the figures and the statement profile here, set the status back to Not started, and hand the per-delivery cost to [PERF-01](perf-01-throughput-and-worker-capacity.md) phase 5.
   - **No processor idle:** the run says nothing about the design. Repeat it with PostgreSQL on another machine, or on a machine with more processors, before deciding.
2. **Split.** `SignalDeliveryShares`, the two settings, the migration, the payload, the phase, the links, the throw, and the retry. Exit with the job-count, throw, and migration tests green and every existing signal test unchanged.
3. **Deliver.** The share query, the recount rule, and the lost-lease statement. Exit with `SignalDeliveryShareTests` green.
4. **Worker.** The lease request, the three queries, the dispatcher, the option, the gauge, and the three statistics that leave share jobs out. Exit with the leasing, statistics, and dispatcher tests green.
5. **Limit.** Run the gates of [section 8](#8-the-recipient-limit), and change the retention delete if step 1 or a gate requires it. If every gate passes, write `RaiseSignalBroadcastRecipientCap` and raise the constant; otherwise record why not.
6. **Default.** Run the purchase-request row of the evidence table, and keep or lower the default number of jobs by its rule. If four jobs deliver between 1.5 and 2 times what one does on a machine with an idle processor, the criterion is not met: record the figures and ask the user whether the gain is worth shipping before continuing.
7. **Documentation.** The owning pages of [section 10](#10-documentation).
8. **Verification.** The remaining evidence runs, the acceptance scenarios, and the manual check. Record the evidence under [Current state](#current-state), then set the status to Done here, in the [backlog](README.md#event-04-follow-up-subtasks), and in the [parent](event-04-signal-events.md#later-releases).

Rollout:

1. Apply `AddSignalDeliveryShares`. It changes the catalog, scans `signal_broadcast_jobs` once for the check, and seeds two settings. Nothing behaves differently yet.
2. Upgrade every Worker and API replica to the same release. Mixed replicas are unsupported: a Worker without this release runs each share job as a job for the whole definition, so the jobs meet on the same instances and waste work, and its lost-lease statement fails the whole definition's pending deliveries. No delivery ends twice. For a rolling upgrade, create the engine setting `Signals.MaxDeliveryJobsPerDefinition` with the value `1` before step 1; the migration seeds the row only when it is missing, with `2`. Raise it when every replica runs the release.
3. Check that at least one Worker has share slots, and size `SignalShareConcurrency` and `Signals.MaxDeliveryJobsPerDefinition` together.
4. If the release raises the cap, apply `RaiseSignalBroadcastRecipientCap` before any replica that accepts the higher limit starts; a throw above 100,000 fails the old check. Raise `Signals.MaxSubscriptionsPerBroadcast` only when a broadcast needs it, and keep splitting on above 100,000.
5. Move alerts on the queue gauges for large broadcasts to the signal gauges, because the queue gauges leave share jobs out.
6. To roll the binaries back, set `Signals.MaxDeliveryJobsPerDefinition` to `1`, and let split broadcasts finish or cancel them. The down migration of `AddSignalDeliveryShares` refuses while a share job is not finished, and that of `RaiseSignalBroadcastRecipientCap` while a broadcast has more than 100,000 deliveries.

| Risk | Guard |
| --- | --- |
| Several jobs deliver no faster, because the database or the per-delivery cost sets the rate. | Step 1 measures it with today's code and stops the item below 1.5 times. The acceptance criterion needs twice the rate with four jobs. |
| The measurement stops a sound design, or passes a poor one, because the test machine was already busy. | Every delivery run records processor use, and step 1 decides only from a run that left a processor idle. |
| A split broadcast slows API requests more than one job did. | A purchase-request run measures it, and lowers the default to two jobs when four cost more than a quarter of the throughput. |
| A second split broadcast waits minutes for a share slot and expires while it waits. | Stated as a limit, with sizing advice: more share slots than jobs for one definition. The sweep still settles an expired broadcast whose jobs never ran. Fair delivery among broadcasts is a [later release](#later-releases). |
| Share jobs held back by the cap pause retention and raise queue-lag alerts. | The queue statistics, the lag histogram, and retention's overload check leave share jobs out, and a test covers each. The signal gauges show the pending work. |
| A raised limit is refused by the database. | `CK_signal_broadcasts_counts` is widened by a migration that the rollout applies before a replica accepts the limit. A test stores a broadcast of 250,000. |
| The split adds a query to every small throw. | The settings are read only when a definition has two or more recipients. A test counts the statements of a throw to one wait. |
| Deliveries of one definition queue on a shared-variable row, and the split gains little. | Stated as a limit. The measured definition is named with whether its route uses a shared variable. |
| A delivery ends twice. | The delivery row lock and the pending check decide, as today. Tests run overlapping jobs and four jobs at once. |
| Jobs of one broadcast queue on one instance, or deadlock. | One instance is in one share, and a delivery's locks are unchanged. A test checks the share of two waits of one instance, and the run of four jobs fails on any deadlock error. |
| Instance IDs follow a pattern, and one share gets most of the work. | A hash, not a remainder. A test spreads consecutive and even IDs. |
| A large broadcast takes the Worker from service tasks and scripts. | Share jobs are capped below the activity slots by default, and control jobs are leased first. A recorded run measures the other jobs' delays while a split broadcast delivers. |
| Small signals wait behind a large broadcast. | The cap counts only share jobs, so unsplit jobs are leased as today. The same run measures it. |
| A family's service task waits behind its own share jobs once the allowance is used up. | The acquisition queries leave share jobs out before they rank. A leasing test covers it. |
| Every Worker is configured with no share slot, and a split broadcast never delivers. | The default is at least 1 while a Worker has an activity slot. The guide warns, and the backlog gauges show the pending deliveries. |
| The page query and the lost-lease statement compute different shares. | One constant holds the expression. A test compares what each statement selects. |
| A job in a retry delay leaves its share waiting although slots are free. | Stated as a limit. It affects one share of a definition, and the job's own delays bound it. Moving work between jobs is a [later release](#later-releases). |
| Recounts of a large broadcast hold its row lock and load the database. | A share job recounts at most once in two seconds. The gate measures one recount at the new size. |
| The leasing change slows every round. | One more condition on rows the queries already read. The recorded runs compare job start delays and start throughput with the base build. |
| The new cap makes one throw, cancel, lost lease, or retention pass too slow. | The gates of [section 8](#8-the-recipient-limit) decide whether the cap rises at all, the worst case of one job for a whole definition included. Retention's delete gets both of its timeouts raised. |
| The gates are measured on rows that no engine wrote. | They time statements only, and the page records how the rows were made. Every delivery figure comes from real waiting instances. |
| A split broadcast still expires before it is delivered. | The guides give the measured rate for four jobs and tell callers to size the expiry for the whole broadcast. |
| PERF-01 phase 4 and this item edit the same leasing code. | Build this item after phase 4, or rebase the second one. The share cap is a separate condition and does not depend on how the classes share slots. |
| Deployments that tuned `ActivityConcurrency` get a share cap they did not choose. | The default follows their value, and the cap applies only to split broadcasts, which exist only after this release. |
| Several Worker restarts during a long delivery use up a job's attempts. | Unchanged from today. A share job runs for a fraction of the time one job did, so it meets fewer restarts. |
