# PERF-01: Throughput and Worker capacity

[Enhancement backlog](README.md) · [Capacity planning](../deployment.md#capacity-planning) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started. The [plan](#plan) below is a proposal from 2026-11-25; set **Planned** once it is agreed. |
| Category | Performance and capacity |
| Effort | L for all five phases; the first release is S plus M |
| Priority | Not ranked |
| Depends on | None |
| Related | [EVENT-04G](event-04g-parallel-signal-delivery.md), [INT-02](int-02-webhook-verification-follow-ups.md) |

## Summary

Raise how much work one deployment carries, and make its defaults safe under a burst, in the order the load test of 2026-11-25 suggests:

1. Ship connection settings that prepare statements and bound the API's pool.
2. Give the Worker's script, service, and signal jobs a guaranteed share of its slots.
3. Run fewer database statements for each transition.
4. Slow the growth of stored history.
5. Measure what the test did not cover.

The first release is phases 1 and 2. Each later phase is planned and released on its own.

## Why it adds value

- **Capacity costs too much database work.** One purchase request ran 908 database statements, about 40 for each HTTP request, and PostgreSQL used about two of the machine's four processors to complete 3.8 requests per second.
- **A setting alone gives about 30%.** Preparing statements automatically raised purchase throughput from about 3.7 to 4.9 requests per second, with no code change. It is not a default, so no deployment has it.
- **Signals and scripts wait behind timers.** At a load one Worker can carry, timer jobs began on time while script jobs waited 4 to 7 seconds at the median. Past that load, a signal delivery waited 3 minutes behind routine timers.
- **A burst of users fails requests.** With no pool size set, 132 simultaneous users exhausted PostgreSQL's connections, and 42 instance starts failed with `500`.
- **Stored history grows fast.** A completed purchase request adds about 54 KB, and history is kept forever unless an administrator configures retention.

## First release acceptance criteria

Each figure is measured with the [lifecycle load test](../../Flowbit/tools/LifecycleLoadTest/README.md) on the machine of the [recorded runs](../deployment.md#capacity-planning), with one API and one Worker.

- [ ] The Compose stack, the documented host commands, and the deployment guide set a `Maximum Pool Size` on the API and automatic statement preparation on the API and the Worker. A comparison recorded on this page chose the values.
- [ ] A host started without a pool size on its connection string logs a warning that names the setting, as the Worker already does for its own pool.
- [ ] 132 virtual users starting at the same moment (`--ramp 0`) complete without a `53300` error.
- [ ] Purchase requests reach at least 4.7 per second at 8 users, 25% above the 3.7 of 2026-11-25.
- [ ] Prepared statements stay correct across an upgrade: a host that outlives a migration either keeps working or stops with a clear message, and the upgrade guide says which.
- [ ] With 300 timer users and 8 signal users at the Worker's default slots, activity jobs begin within 0.5 seconds of their due time at the median and 2 seconds at p95, and timer jobs within 0.3 seconds at p95.
- [ ] With more jobs due than the Worker can run (600 timer users), a signal delivery begins within 5 seconds at p95 while timer jobs are queued. A test holds a backlog of due timer jobs and shows an activity job still starts.
- [ ] The fairness rules stay as they are: one job for each instance or workflow family in a round, and `MaxPerInstance`.
- [ ] The full test suite and both browser suites pass with the new connection settings.
- [ ] [Capacity planning](../deployment.md#capacity-planning) records the new figures, and the [Worker options](../deployment.md#worker-operation) describe any new setting and how to size it.

## Later releases

- **Fewer statements for each transition** (phase 3). Target: at most 550 statements for one purchase request, 40% fewer than 908, with every change measured by the statement profile.
- **Slower data growth** (phase 4). Target: a completed purchase request adds at most 35 KB, and a new deployment is told that history is kept forever before it matters.
- **Wider evidence** (phase 5): a large database, several replicas, REST service tasks, webhooks, and a soak run.
- Parallel delivery of one large broadcast stays with [EVENT-04G](event-04g-parallel-signal-delivery.md).

## Current state

Verified on 2026-11-25 at commit `ec279b8`: none of this is implemented. The figures below come from one Windows machine with 4 logical processors and 32 GB, with PostgreSQL 17 in Docker Desktop at its default configuration, the API and Worker as Release builds, and the load tool on the same machine. The runs that [Capacity planning](../deployment.md#capacity-planning) records are not repeated here.

### Statements for one purchase request

Measured with `pg_stat_statements` over 300 purchase requests at 8 users (6,780 HTTP requests, 3.79 purchase requests per second). The counts are statements the application sent; another 85,800 ran inside triggers and foreign-key checks.

| Statement | For one purchase request | Share of execution time |
| --- | ---: | ---: |
| All | 908 | 100% |
| `SELECT` | 392 | 17% |
| `DISCARD ALL` | 223 | 13% |
| `UPDATE` | 95 | 22% |
| `INSERT` | 81 | 36% |
| `SAVEPOINT` and `RELEASE` | 62 | under 1% |
| `BEGIN` and `COMMIT` | 32 | under 1% |

- **Connection resets.** Npgsql sends `DISCARD ALL` each time a pooled connection is reused. An HTTP request took a connection from the pool about 10 times.
- **Repeated reads.** One purchase request read the instance row by ID 30 times, the user-task row 30 times, and the execution-token row 28 times, across its 7 or 8 writing requests.
- **Many small saves.** One purchase request created 31 savepoints. No code creates a savepoint itself; EF Core adds one around each `SaveChanges` inside a transaction. So a transition saves in many small steps, and most inserts and updates travel one row at a time.
- **Writes that cost most.** Inserting `node_executions` rows took 9% of execution time (13 rows, 1.3 ms each), `instance_variables` 8%, `instance_history` 5%, and the flow-evidence tables 6%. `UPDATE workflow_instances SET "UpdatedAt"` ran 14 times.
- **Statements that may have nothing to do.** Bulk updates that cancel conditional-boundary subscriptions, timer subscriptions, and jobs each ran 11 times for one purchase request, whatever the node had. The showcase has one timer boundary and no conditional boundary. Signal subscriptions already skip their statement when the definition has no signal wait.
- **Execution is the smaller part.** Statements executed for 58 seconds of a 79-second run, while PostgreSQL used about two processors in the equivalent recorded run. The rest is work around each statement, such as parsing and planning, which the next table confirms.

### Connection settings tried

Purchase requests at 8 users, 300 for each run, on one database. Only the API's connection string changed.

| Connection string | Purchase requests per second | **Approve**, median |
| --- | --- | --- |
| `Maximum Pool Size=60` | 3.38, 2.03, 3.62, 2.89, and 3.79 in the profiled run | 363–494 ms |
| plus `Max Auto Prepare=200;Auto Prepare Min Usages=2` | 4.90, 4.90, 4.82 | 283–285 ms |
| plus `No Reset On Close=true` | 5.06, 5.00, 4.99 | 273–283 ms |

- Preparing statements gave about 30% and steadier runs. Two of the runs without it stalled for 6 to 8 seconds; the cause is not known.
- Skipping the reset added about 3%, because Npgsql already sends a lighter reset once a connection has prepared statements. It is not worth the risk of session state reaching the next request.

### The Worker

300 timer users and 8 signal users with the Worker's default 8 slots. The last column had automatic preparation on the API and the Worker; its API also had `No Reset On Close=true`.

| Measure | Without preparation, two runs | With preparation |
| --- | --- | --- |
| Timer jobs began after due, p50 / p95 | 0.05 / 0.19 s and 0.05 / 0.18 s | 0.03 / 0.13 s |
| Script jobs began after due, p50 / p95 | 6.6 / 8.5 s and 3.9 / 7.9 s | 0.29 / 1.12 s |
| Signal deliveries began after due, p50 / p95 | 1.2 / 7.9 s and 1.0 / 5.9 s | 0.30 / 1.13 s |
| Signal cases per second | 0.43 and 0.58 | 1.44 |
| Timer boundary job ran for, median | 191 and 182 ms | 147 ms |
| Reminders fired before their task closed | 1,769 of 1,923 and 1,280 of 1,311 | 1,314 of 1,314 |

- **Job picking is expensive.** The query that ranks due jobs ran 29 times a second while about 20 jobs a second finished. It was the second most expensive statement in the database, at 7% of execution time.
- **Timers are picked first.** `LeaseRunnableAsync` in [WorkflowJobRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/WorkflowJobRepository.cs) leases `control` jobs with every free slot, then gives `activity` jobs what is left. [JobDispatcher.cs](../../Flowbit/src/Flowbit.Worker/JobDispatcher.cs) caps activity jobs at `ActivityConcurrency`, which keeps two slots for control jobs, and reserves none for activity jobs.
- **Slots were idle while jobs waited.** In the runs without preparation, the job counts and run times account for about four of the eight slots. The Worker was slow to pick jobs, not short of slots, which is why faster statements helped so much.
- **Past capacity the order matters.** With 600 timer users and 16 slots, timer jobs began 7 to 11 seconds late and activity jobs 2 to 3 minutes late. That run did not have preparation.

### Other findings

- **Pool size.** The data source is built from the connection string in [ServiceCollectionExtensions.cs](../../Flowbit/src/Flowbit.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs), which sets no pool size or preparation. Npgsql's default of 100 connections for each API process equals PostgreSQL's default `max_connections`. `compose.yaml` passes the same connection string to the API and the Worker.
- **Storage.** A completed purchase request added 50–54 KB and about 100 rows. `node_executions` was the largest table at about 1.7 KB a row, because each row carries its role and node snapshots.
- **Not measured.** A database with months of history or 100,000 open tasks, more than one API or Worker, REST service tasks, webhooks, and a run longer than five minutes.

## Builds on

- The [lifecycle load test](../../Flowbit/tools/LifecycleLoadTest/README.md) and its `--database` report of Worker jobs.
- `pg_stat_statements`, started with the PostgreSQL container, for the statement profile.
- The Worker's acquisition telemetry (`RecordAcquisition`) and its startup warning for a pool below `MaxConcurrency` plus 8.
- The Worker's fairness key and `MaxPerInstance` rule, and their lease tests.
- The definition check that already skips signal-subscription statements (`SignalEventRules.HasSignalWaits`).
- The [retention](../deployment.md#retention-and-backup) categories and their Worker cleanup.

## Open questions

- **Prepared statements and migrations.** Does a host that outlives a schema change fail with `cached plan must not change result type`? Which upgrades already stop every host, and what should the others do?
- **Connection poolers.** Does any deployment put a transaction-mode pooler in front of PostgreSQL? Prepared statements need care there.
- **Stalls.** What caused the 6-to-8-second stalls in two runs without preparation: checkpoints on the Docker disk, or something in Flowbit?
- **The Worker's idle slots.** Is the wait in the ranking query, in running one acquisition round at a time, or in picking control jobs first? Measure with the acquisition telemetry before choosing a design.
- **A share for activity jobs.** Reserve slots with a new option, or alternate which class is picked first? What happens to deployments that tuned `ActivityConcurrency`?
- **Repeated reads.** Which reads may be reused inside one transaction? A row read before its `FOR UPDATE` lock must be read again after it.
- **Fewer saves.** Does combining `SaveChanges` calls change the lock order, the order the commit-time triggers see, or what an error boundary keeps when a script fails?
- **Savepoints.** Does any path depend on EF's automatic savepoint to continue a transaction after a failed save?
- **How far to go.** What throughput is enough? Phase 3 is the costly one, and its target should come from a real deployment's needs.

## Plan

A proposal. Each phase ends with its figures recorded on this page and in [Capacity planning](../deployment.md#capacity-planning).

### Risk to current features

No phase is meant to change what a workflow does, what an API returns, or what a page shows. That is an intention, not a guarantee, and the risk differs by phase:

| Phase | Risk | What could go wrong | What contains it |
| --- | --- | --- | --- |
| 1. Connection settings | Low | A host that keeps running through a schema change can fail on a prepared statement until it reconnects. A deployment behind a transaction-mode connection pooler cannot use prepared statements. PostgreSQL may reuse one query plan where a fresh plan was faster, which matters most on a large database. A pool that is too small makes requests wait for a connection. | The settings are connection-string values, so a deployment can turn them off without a new build. The full test suite and both browser suites run with them on before they become defaults. |
| 2. Worker job picking | Medium | A mistake could starve timer jobs in place of activity jobs, or break the one-job-for-each-instance rule. Under overload, timers start later than today because activity jobs take a share. | The change stays inside job leasing. The existing lease and fairness tests keep running, and a new test holds a backlog of each class. |
| 3. Fewer statements | High | This phase edits the transaction code of the engine. A reused row can be stale, a combined save can change the order that webhook capture or an error boundary sees, and such faults may show only under concurrency. | One step at a time, each behind the existing engine tests, the lock-order and capture tests, and a rerun of the load test. A step that cannot be shown safe is dropped. |
| 4. Data growth | Medium | Moving the snapshots out of `node_executions` needs a migration and changes what the node-activity pages read. | A migration with a backfill, the node-execution API tests, and a browser check of the Activity pages. The retention notices only add text. |
| 5. Wider evidence | None | It adds tests and load-test cases only. | — |

What is verified so far: with automatic preparation on, about 2,100 purchase requests and 1,050 timer and signal cases completed without a failure. That covers the paths the load test uses and nothing else. The test suites have not been run with the settings.

### Phase 1: connection settings (effort S, no migration)

1. Choose the values: a `Maximum Pool Size` for the API, and `Max Auto Prepare` with `Auto Prepare Min Usages` for the API and the Worker. Leave `No Reset On Close` off.
2. Check correctness with the settings on:
   - the full test suite, the browser smoke suite, and the acceptance suite;
   - a migration applied while a host runs, and a host restarted after one;
   - the Worker's notification connection, advisory locks, and transaction-local settings.
3. Decide where the defaults live: in the connection strings of `compose.yaml` and the documented commands, or applied in `ServiceCollectionExtensions.cs` when the connection string leaves them out. Prefer the second if step 2 finds no risk, so every deployment gets them.
4. Warn at startup when the API's connection string has no pool size.
5. Update `compose.yaml`, `.env.example`, [getting started](../getting-started.md) in PowerShell and Bash, [deployment](../deployment.md) configuration and capacity planning, and the load test's README.
6. Rerun the three tables above and the 132-user burst, and record them.

### Phase 2: the Worker's job picking (effort M)

1. Measure first, at 300 and 600 timer users with phase 1 in place: how long an acquisition round takes, how many rounds run each second, how many slots are busy, and how many jobs of each class are due.
2. Give activity jobs a guaranteed share when both classes have due jobs, and keep timers first otherwise. Keep the fairness key and `MaxPerInstance`.
3. If step 1 shows the ranking query slows as the due queue grows, bound the rows it ranks.
4. Add a test that holds a backlog of due control jobs and shows an activity job starts, beside the existing lease and fairness tests.
5. Review the defaults of `MaxConcurrency` and `ActivityConcurrency` against the new figures, and document how to size them.
6. Record the runs. Tell [EVENT-04G](event-04g-parallel-signal-delivery.md) what changed, because it also divides activity slots.

### Phase 3: fewer statements for each transition (effort L)

Run the statement profile before and after each step, and land each step on its own. No step may change the lock order, an activation fence, what the commit-time triggers capture, or any API response.

1. Hold one connection for a request or job scope, so a request resets its connection once, not ten times.
2. Skip the cancellation statements for conditional-boundary subscriptions, timer subscriptions, and jobs when the definition has no element that could own one.
3. Write `workflow_instances."UpdatedAt"` once in a transaction.
4. Reuse the instance, token, and task rows a transaction has already locked and read.
5. Combine the saves of one transition so inserts and updates are sent in batches, and turn off automatic savepoints where nothing depends on them.
6. Record throughput, the statement count, and the lock and deadlock checks after each step.

### Phase 4: data growth (effort M)

1. Measure what fills a `node_executions` row, and store its role and node snapshots once for each task or policy if they repeat.
2. Tell an administrator that history is kept forever: in [getting started](../getting-started.md), on the Retention page, and in a Worker startup message while every history category is unlimited.
3. Load a database with a year of history at a stated daily volume, and record inbox, instance search, and transition times against the small-database figures.
4. Decide from step 3 whether any table needs partitioning. Record the decision either way.

### Phase 5: wider evidence (effort M)

1. Add REST service-task and webhook cases to the load test, with [INT-02](int-02-webhook-verification-follow-ups.md) for webhook delivery targets.
2. Run two API replicas and two Workers against one database, and record how throughput and job waits scale.
3. Run one hour at half the saturation rate, and record the slowest requests, the database's growth, and any incident.
4. Find the cause of the stalls, and document a PostgreSQL setting if one fixes them.

### Documentation and tests

- **Documentation:** [deployment](../deployment.md) for configuration, Worker options, capacity planning, and upgrade rules; [getting started](../getting-started.md) for changed commands; [engine settings](../engine-settings.md) only if a phase adds a setting; the [runtime reference](../../Flowbit/README.md) and `AGENTS.md` for any changed Worker or persistence invariant.
- **Tests:** the existing suite for every phase; lease, fairness, and starvation tests in phase 2; the lock-order and capture tests around each step of phase 3. No phase changes Flowbit.Ui, so no browser verification beyond the two suites.
