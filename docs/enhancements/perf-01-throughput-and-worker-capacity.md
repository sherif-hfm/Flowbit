# PERF-01: Throughput and Worker capacity

[Enhancement backlog](README.md) · [Capacity planning](../deployment.md#capacity-planning) · [Documentation home](../index.md)

| Field | Value |
| --- | --- |
| Status | Not started. The [plan](#plan) is a proposal from 2026-11-25. Its phase order and first release were agreed on 2026-11-26, after the wider load tests; set **Planned** once the [open questions](#open-questions) are decided. |
| Category | Performance and capacity |
| Effort | L for all six phases; the first release is S, then M to L, then M |
| Priority | Not ranked |
| Depends on | None |
| Related | [EVENT-04G](event-04g-parallel-signal-delivery.md), [INT-02](int-02-webhook-verification-follow-ups.md) |

## Summary

Keep a deployment fast as its data grows, then raise how much work it carries, in the order the load tests of 2026-11-25 and 26 suggest:

1. Apply the quick fixes: one missing index, PostgreSQL settings, and connection settings.
2. Make inboxes, searches, and the Worker's polling cost what they return, not all the data behind them.
3. Let retention remove history as fast as a deployment creates it.
4. Give the Worker's script, service, and signal jobs a guaranteed share of its slots.
5. Run fewer database statements for each transition.
6. Store less for each completed instance.

The first release is phases 1 to 3. Each later phase is planned and released on its own.

## Why it adds value

- **Throughput falls as history grows.** On a small database, 8 users completed 3.87 purchase requests per second. After 100,000 completed requests the same machine completed 2.37, and after a million 1.31. At 3,700 requests a day, a million is nine months.
- **Inboxes slow with all open work, not with the user's own.** With 250,000 open tasks every inbox took 3.2–3.8 seconds, and a user who opens the inbox before each action completed 0.20 purchase requests per second. With 32,625 open requests on a million completed ones, a manager's inbox of 327 tasks took 57 seconds.
- **Retention cannot keep history small.** A retention run clears about 20 finished instances for each category, so the hourly runs clear about 480 a day. History then grows without limit, which makes the two problems above worse.
- **Searches across all history fail.** On a million completed requests, a node-execution search over one day's 1.37 million visits passed the 30-second timeout and returned `500`.
- **Much of this is cheap to fix.** One index should restore most of the lost throughput, and turning PostgreSQL's JIT compiler off made inboxes 3 to 4 times faster on 250,000 open tasks.
- **A setting alone gives about 30% on a small database.** Preparing statements automatically raised purchase throughput from about 3.7 to 4.9 requests per second, with no code change. It is not a default, so no deployment has it.
- **A burst of users fails requests.** With no pool size set, 132 simultaneous users exhausted PostgreSQL's connections, and 42 instance starts failed with `500`.
- **Signals and scripts wait behind timers.** At a load one Worker can carry, timer jobs began on time while script jobs waited 4 to 7 seconds at the median. Past that load, a signal delivery waited 3 minutes behind routine timers.
- **Capacity costs too much database work.** One purchase request ran 908 database statements, about 40 for each HTTP request, and added about 54 KB.

## First release acceptance criteria

Each figure is measured with the [lifecycle load test](../../Flowbit/tools/LifecycleLoadTest/README.md) on the machine of the [recorded runs](../deployment.md#capacity-planning), with one API and one Worker. Three databases are named below, each built as that README describes:

- **The open-task database:** 150,000 purchase requests parked at the manager's review and 50,000 at the budget check, which is 250,000 open tasks.
- **The history database:** 100,000 real purchase requests copied nine times, which is about one million completed requests.
- **The combined database:** the history database with about 30,000 requests parked at the manager's review.

The targets are proposals from one set of runs, and the plan may revise them.

Quick fixes (phase 1):

- [ ] On the history database, 8 users complete at least 3.3 purchase requests per second. It was 1.31, and 3.87 on a small database.
- [ ] No table an instance owns is read by a full scan during a purchase-request run on the history database. `pg_stat_user_tables` shows this, and a migration adds each index that was missing.
- [ ] Flowbit's database sessions run with PostgreSQL's JIT compiler off, by default or through a documented setting, and on the open-task database every inbox probe takes at most 1.2 seconds.
- [ ] The Compose stack gives PostgreSQL at least 1 GB of shared memory, and the [deployment guide](../deployment.md) says why.
- [ ] The Compose stack, the documented host commands, and the deployment guide set a `Maximum Pool Size` on the API and automatic statement preparation on the API and the Worker. A comparison recorded on this page chose the values.
- [ ] A host started without a pool size on its connection string logs a warning that names the setting, as the Worker already does for its own pool.
- [ ] 132 virtual users starting at the same moment (`--ramp 0`) complete without a `53300` error.
- [ ] On a small database, purchase requests reach at least 4.7 per second at 8 users, 25% above the 3.7 of 2026-11-25.
- [ ] With prepared statements on, no read probe on the three databases is slower than with them off.
- [ ] Prepared statements stay correct across an upgrade: a host that outlives a migration either keeps working or stops with a clear message, and the upgrade guide says which.
- [ ] The full test suite and both browser suites pass with the new settings.

Reads at scale (phase 2):

- [ ] On the open-task database, a manager's inbox of 1,500 tasks takes at most 100 ms at the median, the Finance pool's first page of 50,000 at most 500 ms, and the inbox of an actor with no tasks at most 50 ms. The first two took 3.6 and 3.4 seconds, and an empty Finance inbox took 1.1 seconds at 150,000 open tasks.
- [ ] On the combined database, a manager's inbox takes at most 200 ms. It took 57 seconds.
- [ ] On the open-task database, 8 users who open the inbox before each action complete at least 2.8 purchase requests per second. They completed 0.20, and 3.76 without the inbox reads.
- [ ] Task management's first page takes at most 1 second on the open-task database and on the combined database. It took 6.4 and 48 seconds.
- [ ] The first page of running instances takes at most 200 ms on the combined database. It took 6.3 seconds.
- [ ] No search returns `500` because it ran out of time. On the history database, the node-execution search over the last day finishes within 5 seconds, and a search that cannot finish within its limit returns a documented error that tells the caller to narrow it.
- [ ] With 150,000 queued jobs and none due, each of the Worker's polling statements takes under 5 ms on average. The wake-up query took 128 ms.
- [ ] Every inbox, task-management, and search response keeps its current membership, order, and paging. A test runs the old and the new inbox statements over generated data that covers roles, assignment, claims, delegation, one item per actor, visibility conditions, filters, and sorts, and compares their results.

Retention (phase 3):

- [ ] On the history database with a 240-day policy, scheduled retention removes the history of at least 100,000 finished instances for each category in 24 hours. At the measured 20 a run, the hourly runs remove about 480.
- [ ] While retention runs at that rate, 8 users lose at most 10% of their purchase-request rate.
- [ ] The retention preview answers on the history database, with a lower bound if it cannot count everything in time. It returned `500`.
- [ ] Retention keeps every protection it has today: referenced and current records stay, an instance's history and its pruned marker commit together, and locked instances are skipped.
- [ ] The Retention page and the [getting-started guide](../getting-started.md) say that history is kept forever until a policy is set.

All phases:

- [ ] [Capacity planning](../deployment.md#capacity-planning) records the new figures, and the deployment guide describes each new setting and how to size it.

## Later releases

- **A share of Worker slots for activity jobs** (phase 4). Targets: with 300 timer users and 8 signal users at the default slots, activity jobs begin within 0.5 seconds of their due time at the median and 2 seconds at p95, and timer jobs within 0.3 seconds at p95. With 600 timer users, a signal delivery begins within 5 seconds at p95 while timer jobs are queued.
- **Fewer statements for each transition** (phase 5). Target: at most 550 statements for one purchase request, 40% fewer than 908, with every change measured by the statement profile.
- **Smaller rows** (phase 6). Target: a completed purchase request adds at most 35 KB.
- **Remaining evidence.** Scaling with PostgreSQL on its own server, a long run at half the saturation rate, the cause of the stalls, and data more varied than nine copies of one run.
- Parallel delivery of one large broadcast stays with [EVENT-04G](event-04g-parallel-signal-delivery.md), and the webhook delivery targets with [INT-02](int-02-webhook-verification-follow-ups.md).

## Current state

Verified on 2026-11-26: none of this is implemented, and no fix below has been tried. The hosts measured were built at commit `a367ede`; later commits changed only tools and documentation. The figures come from one Windows machine with 4 logical processors and 32 GB, with PostgreSQL 17 in Docker Desktop at its default configuration, the API and Worker as Release builds, and the load tool on the same machine. The tables of the runs are in [Capacity planning](../deployment.md#capacity-planning) and are not repeated here.

### History slows every transition

From [months of history](../deployment.md#months-of-history):

- **The cause found.** `gateway_executions` has only partial indexes on `InstanceId`, for active rows. `ListGatewayExecutionsAsync` and the branch read beside it in [WorkflowRuntimeRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/WorkflowRuntimeRepository.cs) read all of an instance's gateway executions, so each read scans the whole table. A purchase request made about five of each.
- **Its cost.** One read took 114 ms after 100,000 completed requests and 320–400 ms after a million. That is 1.2 seconds of the 3.4 seconds each request took at 100,000, and 3.8 of the 6.1 seconds at a million.
- **What an index should give.** Without those reads, the same arithmetic gives about 3.5 purchase requests per second on both databases. This is an estimate; the index has not been tried.
- **No other table showed it.** After the 100,000-request run, `pg_stat_user_tables` showed heavy sequential reads on `gateway_executions` only, apart from two settings tables of a few rows.
- **Other reads slowed too.** A completed instance's detail took 21–26 ms on a small database, 65–68 ms at 100,000, and 128–149 ms at a million.

### Open work slows inboxes and polling

From [many open tasks](../deployment.md#many-open-tasks):

- **The inbox reads all open work.** The inbox statement, built by `BuildInboxWhere` in the same file, reads every active task the actor's roles could match and every running instance, and only then applies the assignee and claim rules; then it counts and ranks the result. The count and the page each do this. At 250,000 open tasks every inbox took 3.2–3.8 seconds, and at 150,000 an empty one took 1.1 seconds.
- **JIT costs most of that.** Past about 100,000 open tasks PostgreSQL compiled the inbox statement on every call, about 0.75 seconds each. With `jit = off` inboxes took 0.85–1.2 seconds.
- **On top of history it is far worse.** With 32,625 open requests on a million completed ones, a manager's inbox of 327 tasks took 57 seconds, with JIT on or off. The statement's generic plan reads all 4.6 million task rows, though its filters alone found the 327 tasks through the indexes in 0.19 seconds. The plan of the call that ran was not captured.
- **Task management counts everything.** Its first page counts every open task the manager may see, joined to its token and instance: 6.4 seconds at 250,000 open tasks and 48 seconds on the combined database.
- **Transitions do not slow down.** Without the inbox reads, purchase requests ran at 3.76 per second on 250,000 open tasks.
- **The Worker scans its queue.** Its wake-up query in [WorkflowJobRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/WorkflowJobRepository.cs) reads every queued job on each poll, because the only index on the due time leads with the queue class and priority: 128 ms on average with up to 150,000 future timer jobs, about 30% of one processor of PostgreSQL time with nothing due. The queue statistics statement (`GetQueueStatisticsAsync`) reads the same rows and averaged 97 ms, then 276 ms as the queue grew.

### Wide searches and retention

- **Wide searches time out.** On a million completed requests, an instance search filtered by a variable took 4.4 seconds, the first page of running instances 6.3 seconds once 32,625 were running, and a node-execution search over 1.37 million visits passed the API's 30-second command timeout and returned `500`. Searches narrowed to a business key or one instance stayed fast.
- **Retention clears about 480 instances a day.** [RetentionRepository.cs](../../Flowbit/src/Flowbit.Infrastructure/Repositories/RetentionRepository.cs) removes one finished instance in a batch and stops a category after 20 batches or 30 seconds. A run removed 288 history rows, 172 node visits, 8 variable rows, and 5,000 completed jobs, and took about 90 seconds.
- **The retention preview failed.** It passed its 5-second statement limit on the history database and returned `500`.
- **Docker's default shared memory fails parallel queries.** With 64 MB of `/dev/shm`, large queries failed with `53100`; the runs then used `--shm-size=1g`. The Compose stack sets no `shm_size`.

### External calls, webhooks, and replicas

- **A synchronous REST call holds its transaction** ([REST service tasks](../deployment.md#rest-service-tasks)). With 1-second calls and 64 users, up to 54 of the API's 60 connections waited idle inside a transaction. A Worker job holds none, but one Worker finished only about 5 one-second calls per second, because it runs 6 at once.
- **Webhooks** ([webhooks](../deployment.md#webhooks)). Capture and fanout cost 7–11% of purchase throughput with 1 to 100 subscriptions. Delivery reached 22.6 per second for one subscription and 94.7 for ten, slightly below [INT-02](int-02-webhook-verification-follow-ups.md)'s targets; with delivery on beside the load, throughput fell 20%.
- **Replicas are correct, not faster here** ([several processes](../deployment.md#several-api-and-worker-processes)). Two APIs and two Workers ran every case with no job done twice and no deadlock, also when one Worker was killed. On four processors a second Worker shortened job waits but did not raise throughput.

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

Purchase requests at 8 users, 300 for each run, on one small database. Only the API's connection string changed.

| Connection string | Purchase requests per second | **Approve**, median |
| --- | --- | --- |
| `Maximum Pool Size=60` | 3.38, 2.03, 3.62, 2.89, and 3.79 in the profiled run | 363–494 ms |
| plus `Max Auto Prepare=200;Auto Prepare Min Usages=2` | 4.90, 4.90, 4.82 | 283–285 ms |
| plus `No Reset On Close=true` | 5.06, 5.00, 4.99 | 273–283 ms |

- Preparing statements gave about 30% and steadier runs. Two of the runs without it stalled for 6 to 8 seconds; the cause is not known. In the 8-hour run of 2026-11-26, also without it, no request took longer than 3.6 seconds.
- Skipping the reset added about 3%, because Npgsql already sends a lighter reset once a connection has prepared statements. It is not worth the risk of session state reaching the next request.
- None of these settings was tried on a large database.

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
- **Timers are picked first.** `LeaseRunnableAsync` in `WorkflowJobRepository.cs` leases `control` jobs with every free slot, then gives `activity` jobs what is left. [JobDispatcher.cs](../../Flowbit/src/Flowbit.Worker/JobDispatcher.cs) caps activity jobs at `ActivityConcurrency`, which keeps two slots for control jobs, and reserves none for activity jobs.
- **Slots were idle while jobs waited.** In the runs without preparation, the job counts and run times account for about four of the eight slots. The Worker was slow to pick jobs, not short of slots, which is why faster statements helped so much.
- **Past capacity the order matters.** With 600 timer users and 16 slots, timer jobs began 7 to 11 seconds late and activity jobs 2 to 3 minutes late. That run did not have preparation.

### Other findings

- **Pool size.** The data source is built from the connection string in [ServiceCollectionExtensions.cs](../../Flowbit/src/Flowbit.Infrastructure/DependencyInjection/ServiceCollectionExtensions.cs), which sets no pool size or preparation. Npgsql's default of 100 connections for each API process equals PostgreSQL's default `max_connections`. `compose.yaml` passes the same connection string to the API and the Worker.
- **Storage.** A completed purchase request added 50–54 KB and about 100 rows. `node_executions` was the largest table at about 1.7 KB a row, because each row carries its role and node snapshots; at a million requests it held 19 of the database's 48 GB.
- **A long run.** The 8.1-hour run of 100,000 requests had no failure, deadlock, or rollback. Its slowest request took 3.6 seconds, and one 30-second stretch, seven hours in, ran at about half the rate around it.

### What the runs do not show

- **No fix is measured.** The index's effect is arithmetic, and the inbox's 57 seconds has no captured plan.
- **The large data is uniform.** The million requests are nine copies of one run, the open tasks sit at two steps, and old instances have the highest ids, which real data does not.
- **One machine, one run each.** Every process shared four processors, and no configuration was repeated.

## Builds on

- The [lifecycle load test](../../Flowbit/tools/LifecycleLoadTest/README.md): its `--database` report, read probes, `--park-at`, and `sql/copy-history.sql`, which builds the history database from 100,000 real requests in 26 minutes.
- `pg_stat_statements` and `pg_stat_user_tables`, for the statement profile and for tables read by full scans.
- The indexes `user_tasks` already has for an actor's own work: by lowercase assignee, by lowercase owner, and a GIN index on roles.
- The retention preview's `isLowerBound` field, and the [retention](../deployment.md#retention-and-backup) categories, protections, and Worker cleanup.
- The Worker's acquisition telemetry (`RecordAcquisition`) and its startup warning for a pool below `MaxConcurrency` plus 8.
- The Worker's fairness key and `MaxPerInstance` rule, and their lease tests.
- The definition check that already skips signal-subscription statements (`SignalEventRules.HasSignalWaits`).

## Open questions

First release:

- **Does the index restore the rate?** Create it by hand on the history database and measure before anything else. If the rate stays low, what is the next cost?
- **Building the index.** Does a plain `CREATE INDEX` hold writes too long on a large live table, or must it be built `CONCURRENTLY` in a migration of its own that runs outside a transaction?
- **Where to turn JIT off.** In the connection string (`Options=-c jit=off`), applied by the data source, or only documented as `ALTER DATABASE ... SET jit = off`? Does any Flowbit statement run faster with JIT on?
- **Prepared statements on large tables.** After a few uses PostgreSQL may keep one generic plan for a prepared statement. Is that plan as good as a fresh one for the inbox and the searches, or do those statements need `plan_cache_mode = force_custom_plan`?
- **Why 57 seconds?** What plan did the inbox statement run with on the combined database, and why did the planner not start from the actor's own tasks?
- **Exact totals.** Inboxes, task management, and searches return an exact total today, and counting is much of their cost. Keep it, cap it, or make it optional? Any change is an API contract change.
- **Searches that cannot finish.** Which limit applies, and what does the caller get: a `400` that asks for a narrower search, or a partial page?
- **Retention's budget.** Why one instance in a batch and 20 batches in a run? Which of these protect running work, and which can grow with the backlog?
- **Retention on real data.** Old instances had the highest ids in the copied database. Did that slow each batch, and what does a run remove when old instances have the lowest ids?
- **Prepared statements and migrations.** Does a host that outlives a schema change fail with `cached plan must not change result type`? Which upgrades already stop every host, and what should the others do?
- **Connection poolers.** Does any deployment put a transaction-mode pooler in front of PostgreSQL? Prepared statements need care there.

Later phases:

- **Stalls.** What caused the 6-to-8-second stalls in two runs without preparation: checkpoints on the Docker disk, or something in Flowbit?
- **The Worker's idle slots.** Is the wait in the ranking query, in running one acquisition round at a time, or in picking control jobs first? Measure with the acquisition telemetry before choosing a design.
- **A share for activity jobs.** Reserve slots with a new option, or alternate which class is picked first? What happens to deployments that tuned `ActivityConcurrency`?
- **Repeated reads.** Which reads may be reused inside one transaction? A row read before its `FOR UPDATE` lock must be read again after it.
- **Fewer saves.** Does combining `SaveChanges` calls change the lock order, the order the commit-time triggers see, or what an error boundary keeps when a script fails?
- **Savepoints.** Does any path depend on EF's automatic savepoint to continue a transaction after a failed save?
- **Synchronous REST calls.** Should saving warn about a service task without `asyncBefore` whose timeout is long?
- **How far to go.** What throughput is enough? Phase 5 is the costly one, and its target should come from a real deployment's needs.

## Plan

A proposal. Each phase ends with its figures recorded on this page and in [Capacity planning](../deployment.md#capacity-planning).

### Risk to current features

No phase is meant to change what a workflow does, what an API returns, or what a page shows, except where an open question above decides otherwise. That is an intention, not a guarantee, and the risk differs by phase:

| Phase | Risk | What could go wrong | What contains it |
| --- | --- | --- | --- |
| 1. Quick fixes | Low | Building an index on a large table can hold its writes. Without JIT, a rare heavy statement over many rows may run slower. A host that keeps running through a schema change can fail on a prepared statement until it reconnects. A deployment behind a transaction-mode connection pooler cannot use prepared statements. PostgreSQL may reuse one plan where a fresh plan was faster, which matters most on a large database. A pool that is too small makes requests wait for a connection. | The index is measured by hand first. The settings are connection-string or database values, so a deployment can turn them off without a new build. The test suites and the probes on the large databases run with them on before they become defaults. |
| 2. Reads at scale | High | The inbox statement carries the rules for who sees a task: roles, assignment, claims, delegation, one item per actor, and visibility conditions. A rewrite could show or hide a task wrongly, or change a count, an order, or a page. A wrong wake-up time could start Worker jobs late. | The existing inbox and search tests, a test that compares the old and the new statements over generated data, and the probes. The Worker still polls on its interval if a wake-up is missed. |
| 3. Retention | Medium | Removing more in a run holds locks longer and writes more, which can slow running work. A mistake could remove a protected row, or history without its pruned marker. | The existing retention tests and protections, the busy-queue check, and a purchase run beside retention. |
| 4. Worker job picking | Medium | A mistake could starve timer jobs in place of activity jobs, or break the one-job-for-each-instance rule. Under overload, timers start later than today because activity jobs take a share. | The change stays inside job leasing. The existing lease and fairness tests keep running, and a new test holds a backlog of each class. |
| 5. Fewer statements | High | This phase edits the transaction code of the engine. A reused row can be stale, a combined save can change the order that webhook capture or an error boundary sees, and such faults may show only under concurrency. | One step at a time, each behind the existing engine tests, the lock-order and capture tests, and a rerun of the load test. A step that cannot be shown safe is dropped. |
| 6. Smaller rows | Medium | Moving the snapshots out of `node_executions` needs a migration and changes what the node-activity pages read. | A migration with a backfill, the node-execution API tests, and a browser check of the Activity pages. |

What is verified so far: with automatic preparation on, about 2,100 purchase requests and 1,050 timer and signal cases completed without a failure, on a small database. That covers the paths the load test uses and nothing else. The test suites have not been run with the settings, and no other change in this plan has been tried.

### Phase 1: quick fixes (effort S, one migration)

1. Measure the index first. On the history database, create an index on `gateway_executions ("InstanceId")` by hand, then rerun the 8-user purchase run and the probes. Record the build time and whether writes waited. If the rate stays low, find the next cost with `sql/top-statements.sql` before going on.
2. Add the index in a migration, built as step 1 and the open question decide. After a purchase run on the history database, read `pg_stat_user_tables` for any other table an instance owns that was read by full scans, and add what is missing.
3. Turn JIT off for Flowbit's sessions where the open question decides, and rerun the open-task probes.
4. Give the Compose PostgreSQL service a `shm_size` of at least 1 GB.
5. Choose the connection values: a `Maximum Pool Size` for the API, and `Max Auto Prepare` with `Auto Prepare Min Usages` for the API and the Worker. Leave `No Reset On Close` off.
6. Check correctness with all of these on:
   - the full test suite, the browser smoke suite, and the acceptance suite;
   - a migration applied while a host runs, and a host restarted after one;
   - the Worker's notification connection, advisory locks, and transaction-local settings;
   - the read probes on the three databases with prepared statements on and off.
7. Decide where the connection defaults live: in the connection strings of `compose.yaml` and the documented commands, or applied in `ServiceCollectionExtensions.cs` when the connection string leaves them out. Prefer the second if step 6 finds no risk, so every deployment gets them.
8. Warn at startup when the API's connection string has no pool size.
9. Update `compose.yaml`, `.env.example`, [getting started](../getting-started.md) in PowerShell and Bash, [deployment](../deployment.md) configuration and capacity planning, the [database schema reference](../database-schema.md) for the index, and the load test's README.
10. Rerun the connection-settings and Worker tables above, the 132-user burst, and the purchase runs on the large databases, and record them.

### Phase 2: reads at scale (effort M to L)

No step may change who sees a task or an instance.

1. Capture the plans that ran. For the inbox count and page, task management, the running-instances search, and the node-execution search, record what each reads on the open-task, history, and combined databases, with real parameter values.
2. Restructure the inbox statement so the actor's own conditions select the candidates first, through the indexes `user_tasks` already has: assignee, claimant, roles, and delegation. Join instances, evaluate visibility conditions, and rank one item per actor for those candidates only. Membership, total, order, and paging stay decided in PostgreSQL.
3. Add the test that compares the old and the new inbox statements over generated data, and keep it while both builders exist.
4. Give task management and task distribution the same treatment, and apply the decision on exact totals.
5. Find what the running-instances search and the node-execution search read, and fix each with an index or a narrower statement. Return the documented error when a search passes its limit.
6. Answer the Worker's wake-up query and its queue-lag statement from an index on the due time of queued jobs.
7. Add a probe for an actor with no tasks to the load test. Rerun the probes on the three databases and the purchase run with inbox reads, and record them.

### Phase 3: retention that keeps up (effort M)

1. Measure a run on data whose oldest instances have the lowest ids, which needs an option in `sql/copy-history.sql`, and record what one batch reads and how long it takes.
2. Raise what a run removes: several instances in a batch, more batches when the backlog is large, or the next run at once when one ends at its budget and the queue is idle. Keep the per-instance lock, the pruned marker in the same transaction, skipped locked instances, and the busy-queue check.
3. Make the preview answer on a large database, with a lower bound when it cannot count everything in time.
4. Tell an administrator that history is kept forever: in [getting started](../getting-started.md), on the Retention page, and in a Worker startup message while every history category is unlimited.
5. Run retention beside the 8-user purchase load on the history database, and record the instances removed in an hour and the purchase rate.
6. Document the measured rate and how to size the policy in the [retention section](../deployment.md#retention-and-backup).

### Phase 4: the Worker's job picking (effort M)

1. Measure first, at 300 and 600 timer users with phase 1 in place: how long an acquisition round takes, how many rounds run each second, how many slots are busy, and how many jobs of each class are due.
2. Give activity jobs a guaranteed share when both classes have due jobs, and keep timers first otherwise. Keep the fairness key and `MaxPerInstance`.
3. If step 1 shows the ranking query slows as the due queue grows, bound the rows it ranks.
4. Add a test that holds a backlog of due control jobs and shows an activity job starts, beside the existing lease and fairness tests.
5. Review the defaults of `MaxConcurrency` and `ActivityConcurrency` against the new figures, including the calls a Worker makes at once for [REST service tasks](../deployment.md#rest-service-tasks), and document how to size them.
6. Record the runs. Tell [EVENT-04G](event-04g-parallel-signal-delivery.md) what changed, because it also divides activity slots.

### Phase 5: fewer statements for each transition (effort L)

Run the statement profile before and after each step, and land each step on its own. No step may change the lock order, an activation fence, what the commit-time triggers capture, or any API response.

1. Hold one connection for a request or job scope, so a request resets its connection once, not ten times.
2. Skip the cancellation statements for conditional-boundary subscriptions, timer subscriptions, and jobs when the definition has no element that could own one.
3. Write `workflow_instances."UpdatedAt"` once in a transaction.
4. Reuse the instance, token, and task rows a transaction has already locked and read.
5. Combine the saves of one transition so inserts and updates are sent in batches, and turn off automatic savepoints where nothing depends on them.
6. Record throughput, the statement count, and the lock and deadlock checks after each step.

### Phase 6: smaller rows (effort M)

1. Measure what fills a `node_executions` row, and store its role and node snapshots once for each task or policy if they repeat.
2. Decide from the history database whether any table needs partitioning. Record the decision either way.

### Remaining evidence

Not a phase; each can run when its condition is met.

1. Run two API replicas and two Workers with PostgreSQL on its own server, and record how throughput and job waits scale. On one machine they were correct but no faster.
2. Add a rate limit to the load test, run one hour at half the saturation rate, and record the slowest requests, the database's growth, and any incident.
3. Find the cause of the stalls, and document a PostgreSQL setting if one fixes them.
4. Build a large database from more varied work: several workflows, open tasks at every step, and timer and signal cases.

### Documentation and tests

- **Documentation:** [deployment](../deployment.md) for configuration, Worker options, capacity planning, retention, and upgrade rules; [getting started](../getting-started.md) for changed commands; the [database schema reference](../database-schema.md) for each new index; the [API reference](../api-guide.md) if totals or search limits change; the [Flowbit.Ui guide](../ui-guide.md) for the Retention page; [engine settings](../engine-settings.md) only if a phase adds a setting; the [runtime reference](../../Flowbit/README.md) and `AGENTS.md` for any changed Worker, retention, or persistence invariant.
- **Tests:** the existing suite for every phase; the comparison of old and new inbox statements in phase 2; the retention tests in phase 3; lease, fairness, and starvation tests in phase 4; the lock-order and capture tests around each step of phase 5. Phase 3 changes text on the Retention page, which needs [real-browser verification](../../AGENTS.md#mandatory-real-browser-ui-verification); no other phase changes Flowbit.Ui.
