# Lifecycle load test

This console tool measures how much work a Flowbit stack can carry. It runs
workflows from the [example catalog](../../../examples/README.md) through the
HTTP API, in up to four kinds of case side by side, each with its own number of
virtual users:

- **Purchase requests** (`--users`) run the purchase-request showcase from
  submission to order, with a separate actor for each role.
- **Timer cases** (`--timer-users`) wait for intermediate timers, recurring
  reminders, and an interrupting deadline.
- **Signal cases** (`--signal-users`) throw signals through the API and from a
  workflow, to waiting instances and to a signal start event.
- **REST cases** (`--rest-users`) run service tasks that call a stub the tool
  starts, inside the API's request or as Worker jobs.

It reports each kind's completed cases per second, the latency of every
operation, how late each timer and signal arrived, and, with `--database`, how
much the database grew, how long Worker jobs waited, and how many sessions
waited on locks. A virtual user starts its next case as soon as the last one
ends, so the result is the stack's maximum rate, not the response time people
see at ordinary usage.

It also sets up the conditions a small test database lacks:
[webhook receivers](#rest-service-tasks-and-webhooks),
[several API and Worker processes](#several-api-and-worker-processes),
[many open tasks](#many-open-tasks), and
[months of history](#months-of-history), with read probes that time inboxes and
searches on the result.

The results recorded for a development machine, and how to turn them into a
daily volume, are in [Capacity planning](../../../docs/deployment.md#capacity-planning).
Use `tools/InstanceLoadTest` instead to measure only instance starts.

## What each case does

### Purchase requests

Each case runs
[`01-purchase-request-approval.json`](../../../examples/showcases/01-purchase-request-approval.json):

| Step | Actor | HTTP requests |
| --- | --- | --- |
| Submit | `Requester` | `POST /api/instances` with a unique `requestNumber` and an amount. |
| Approve | `Manager` named as `managerUser` | Inbox page, inbox search by instance, available flows, **Approve**. |
| Board vote, for 20% of requests (amounts of 25,000 or more) | Two different `BoardMember` actors | The same four requests each, with **Approve**. |
| Confirm the budget | A `Finance` actor from the pool | The same requests plus a claim, then **Budget Confirmed**. |
| Assign supplier selection | `ProcurementLead` | Task-management search, then assign to a buyer. |
| Select the supplier and place the order | `Buyer` | The same four requests for **Supplier Selected**, then for **Order Placed**, which must complete the instance. |
| Check the result | `Requester` | `GET /api/instances/{id}`. |

That is about 7 writes and 15 reads per request. `--no-browse` leaves out the
inbox page before each action and the final detail read, which leaves the reads
needed to find each task. `--park-at` stops each request early to leave open
work behind; see [many open tasks](#many-open-tasks).

### Timer cases

| Share | Workflow | What happens | What the tool records |
| --- | --- | --- | --- |
| Half | [Intermediate timer delay](../../../examples/timers/02-intermediate-timer-delay.json) | The instance waits 30 seconds at a timer; the tool then completes **Timer Fired**. | How long after the due time the task existed. |
| A quarter | [User-task reminder and deadline](../../../examples/timers/05-user-task-reminder-and-deadline.json) | Three reminders 15 seconds apart each run a script; the tool approves the task at 52 seconds, before the one-minute deadline. | How many reminders had fired when the task closed. |
| A quarter | The same workflow, left alone | After its reminders, the one-minute deadline cancels the task and opens **Escalated Approval**, which the tool completes. | How long after the deadline the escalated task existed. |

### Signal cases

Each case runs three steps with
[signal boundaries](../../../examples/signals/02-signal-boundaries-recall.json),
a [signal end event](../../../examples/signals/04-signal-end-recall-decision.json),
and a [signal start event](../../../examples/signals/05-signal-start-recall-notice.json):

1. It starts an order with a unique order number and throws
   `warehouse.price-update` through the API to that order's business key. The
   non-interrupting boundary opens **Update the invoice**, which the tool
   completes.
2. It starts a recall decision for the same order and takes **Recall**. The
   decision's signal end event throws `warehouse.product-recall` to the order,
   whose interrupting boundary opens **Stop the shipment**; the tool completes it.
3. It throws `warehouse.recall-announced` to the recall-notice family, whose
   signal start event starts an instance; the tool completes
   **Write the customer notice**.

For each step the tool records how long after the throw the task existed. The
first step's throw time is the broadcast's; the second's is when the decision
completed, which is when its end event threw.

### REST cases

Half the cases run each of two examples, or only one with `--rest-mix sync` or
`--rest-mix job`:

| Workflow | What happens | What the tool records |
| --- | --- | --- |
| [REST templating and typed output](../../../examples/service-tasks/01-rest-templating-and-typed-output.json) | The service task has no `asyncBefore`, so the API calls the stub inside the start request, holding the instance's transaction open meanwhile, and maps ten typed fields from the answer. The tool completes **Inspect Typed Outputs**. | The start request's latency, which includes the call. |
| [Retries and incident recovery](../../../examples/durable-jobs/02-retries-and-incident-recovery.json) | The service task has `asyncBefore`, so a Worker job calls the stub between its own short transactions. The tool completes **Inspect Recovered Result**. | How long after the start the task existed. |

The stub answers after `--stub-delay-ms` (default 100 ms). For the second
example it counts calls by the job id the workflow sends: a `recover` job must
be called once, and with `--rest-fail-share` a share of jobs are `fail-once`,
whose first call gets `503`, so the job retries two seconds later and must be
called exactly twice. The summary reports any job called another number of
times. The stub's service is always recovered, so `recover` succeeds on its
first call.

Timer, signal, and REST job cases need a running Worker. While the tool waits for one, it
polls the inbox, starting at `--poll-ms` and backing off to 10 seconds, as a
person refreshing a page would; delays come from server timestamps, so the
polling interval does not change them. The tool finds every node and flow it
uses by its external id and stops with an error if one is missing.

## Run it

You need the .NET 10 SDK and Docker. Run against a database you throw away: the
tool creates thousands of instances and deletes none. Run the commands from the
repository root, each host in its own terminal. Use `Release` builds and keep
host logging at warnings, or console output becomes part of what you measure.

The API command sets `Maximum Pool Size=60`. Without it Npgsql allows each API
process 100 connections, which is also PostgreSQL's default `max_connections`,
so a burst of users leaves none for the Worker and fails requests with
`53300: sorry, too many clients already`.

### PowerShell

Start a disposable PostgreSQL 17 database:

```powershell
docker run -d --name flowbit-load-pg -e POSTGRES_USER=flowbit -e POSTGRES_PASSWORD=load-test-only -e POSTGRES_DB=flowbit_load -p 127.0.0.1:55447:5432 postgres:17
```

Start the API in a second terminal. It applies migrations, then logs
`Application started`:

```powershell
$db = 'Host=127.0.0.1;Port=55447;Database=flowbit_load;Username=flowbit;Password=load-test-only'
$apiRoot = (Resolve-Path ./Flowbit/src/Flowbit.Api).Path
dotnet run -c Release --no-launch-profile --project ./Flowbit/src/Flowbit.Api/Flowbit.Api.csproj -- --contentRoot $apiRoot --environment Development --Authentication:Mode DevelopmentToken --Serilog:MinimumLevel:Default Warning --urls http://127.0.0.1:5017 --ConnectionStrings:Flowbit "$db;Maximum Pool Size=60"
```

Start a Worker in a third terminal. Timer and signal cases need it, and purchase
requests should run beside one, as a deployment would:

```powershell
$db = 'Host=127.0.0.1;Port=55447;Database=flowbit_load;Username=flowbit;Password=load-test-only'
$workerRoot = (Resolve-Path ./Flowbit/src/Flowbit.Worker).Path
dotnet run -c Release --no-launch-profile --project ./Flowbit/src/Flowbit.Worker/Flowbit.Worker.csproj -- --contentRoot $workerRoot --environment Development --Logging:LogLevel:Default Warning --FlowbitWorker:HealthListenUrl http://127.0.0.1:8081 --ConnectionStrings:Flowbit $db
```

Import and publish the eight example definitions, warm up, then measure purchase
requests alone and with timer and signal cases:

```powershell
$db = 'Host=127.0.0.1;Port=55447;Database=flowbit_load;Username=flowbit;Password=load-test-only'
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --setup
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --cases 400
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --duration 180 --users 8 --database $db
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --duration 180 --users 8 --timer-users 120 --signal-users 4 --database $db
```

Stop the hosts with Ctrl+C, then remove the database:

```powershell
docker rm -f flowbit-load-pg
```

### Bash

Start a disposable PostgreSQL 17 database:

```bash
docker run -d --name flowbit-load-pg -e POSTGRES_USER=flowbit -e POSTGRES_PASSWORD=load-test-only -e POSTGRES_DB=flowbit_load -p 127.0.0.1:55447:5432 postgres:17
```

Start the API in a second terminal. It applies migrations, then logs
`Application started`:

```bash
db='Host=127.0.0.1;Port=55447;Database=flowbit_load;Username=flowbit;Password=load-test-only'
dotnet run -c Release --no-launch-profile --project ./Flowbit/src/Flowbit.Api/Flowbit.Api.csproj -- --contentRoot "$PWD/Flowbit/src/Flowbit.Api" --environment Development --Authentication:Mode DevelopmentToken --Serilog:MinimumLevel:Default Warning --urls http://127.0.0.1:5017 --ConnectionStrings:Flowbit "$db;Maximum Pool Size=60"
```

Start a Worker in a third terminal. Timer and signal cases need it, and purchase
requests should run beside one, as a deployment would:

```bash
db='Host=127.0.0.1;Port=55447;Database=flowbit_load;Username=flowbit;Password=load-test-only'
dotnet run -c Release --no-launch-profile --project ./Flowbit/src/Flowbit.Worker/Flowbit.Worker.csproj -- --contentRoot "$PWD/Flowbit/src/Flowbit.Worker" --environment Development --Logging:LogLevel:Default Warning --FlowbitWorker:HealthListenUrl http://127.0.0.1:8081 --ConnectionStrings:Flowbit "$db"
```

Import and publish the eight example definitions, warm up, then measure purchase
requests alone and with timer and signal cases:

```bash
db='Host=127.0.0.1;Port=55447;Database=flowbit_load;Username=flowbit;Password=load-test-only'
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --setup
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --cases 400
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --duration 180 --users 8 --database "$db"
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --duration 180 --users 8 --timer-users 120 --signal-users 4 --database "$db"
```

Stop the hosts with Ctrl+C, then remove the database:

```bash
docker rm -f flowbit-load-pg
```

### Reading the result

- **Throughput.** Run at several user counts. While throughput still rises, the
  stack has spare capacity; once it stays flat and latency grows with each step,
  the stack is saturated and extra users only wait longer. Check the host's CPU
  at the same time to see which process saturates. Timer cases last 30 to 60
  seconds by design, so their rate follows from the number of timer users; what
  they measure is how late timers fire.
- **Timer and signal delays.** The delay table shows, per kind of event, how long
  after its due time or throw the task it opens existed. On a stack with spare
  capacity this is well under a second. **Tasks closed with reminder work still
  queued** counts reminder cases whose instance was still running when its task
  closed, because a reminder's script was waiting for the Worker; the instance
  completes once it runs.
- **Worker jobs.** With `--database`, the tool lists the jobs the Worker
  finished during the run by kind and queue. Timer firings run on the `control`
  queue; script and service tasks run by the Worker and signal deliveries run on
  the `activity` queue. **start** is how long after its due time each job began:
  when it grows, the Worker is behind, and activity jobs fall behind first. The
  Worker's `MaxConcurrency` and `ActivityConcurrency` settings set how many jobs
  it runs at once.
- **Database.** The database line reports bytes and instances added during the
  run and PostgreSQL transactions per HTTP request; every statement that runs
  outside an explicit transaction counts as one. Its figures include any other
  client of that database.

- **Sessions.** With `--database`, the tool samples PostgreSQL's sessions once a
  second and reports how many waited on a lock, how many sat idle inside an open
  transaction, and how many were connected. The database line adds deadlocks and
  rollbacks during the run, and a clock line gives the database's clock against
  the machine's, which matters only for webhook timings.
- **Several APIs.** With more than one `--api` URL, the summary adds each API's
  request count and latency.

To compare two builds, publish both to the same disk and alternate their runs:
a few percent can come from where the binaries live rather than from the code.

## REST service tasks and webhooks

The tool's stub stands in for the service the REST examples call and for
webhook receivers. Give the API and Worker its address as the examples'
`exampleApiBaseUrl` (the stub ignores `exampleApiToken`, but the examples need a
value), and for webhooks a key-ring certificate made as the
[deployment guide](../../../docs/deployment.md#signing-keys-and-the-key-ring)
describes, permission to deliver to a loopback HTTP receiver, and delivery on the
Worker. `--webhooks <n>` then creates `n` subscriptions to every subscribable
event type, turns lifecycle capture on, checks every delivery's signature, and
afterwards waits for the queue to drain, archives the subscriptions, and turns
capture off again if it was off.

PowerShell, with the database from [Run it](#run-it):

```powershell
$db = 'Host=127.0.0.1;Port=55447;Database=flowbit_load;Username=flowbit;Password=load-test-only'
$extra = @('--WorkflowContext:Config:exampleApiBaseUrl', 'http://127.0.0.1:5099', '--WorkflowContext:Config:exampleApiToken', 'load-test-stub',
    '--Webhooks:KeyProtection:CertificatePath', (Resolve-Path ./dev/webhooks/webhook-keys.pfx).Path, '--Webhooks:KeyProtection:CertificatePassword', 'flowbit-local-webhooks',
    '--Webhooks:Network:AllowLoopbackHttp', 'true')
$apiRoot = (Resolve-Path ./Flowbit/src/Flowbit.Api).Path
dotnet run -c Release --no-launch-profile --project ./Flowbit/src/Flowbit.Api/Flowbit.Api.csproj -- --contentRoot $apiRoot --environment Development --Authentication:Mode DevelopmentToken --Serilog:MinimumLevel:Default Warning --urls http://127.0.0.1:5017 --ConnectionStrings:Flowbit "$db;Maximum Pool Size=60" @extra
```

```powershell
$db = 'Host=127.0.0.1;Port=55447;Database=flowbit_load;Username=flowbit;Password=load-test-only'
$extra = @('--WorkflowContext:Config:exampleApiBaseUrl', 'http://127.0.0.1:5099', '--WorkflowContext:Config:exampleApiToken', 'load-test-stub',
    '--Webhooks:KeyProtection:CertificatePath', (Resolve-Path ./dev/webhooks/webhook-keys.pfx).Path, '--Webhooks:KeyProtection:CertificatePassword', 'flowbit-local-webhooks',
    '--Webhooks:Network:AllowLoopbackHttp', 'true', '--Webhooks:DeliveryEnabled', 'true')
$workerRoot = (Resolve-Path ./Flowbit/src/Flowbit.Worker).Path
dotnet run -c Release --no-launch-profile --project ./Flowbit/src/Flowbit.Worker/Flowbit.Worker.csproj -- --contentRoot $workerRoot --environment Development --Logging:LogLevel:Default Warning --FlowbitWorker:HealthListenUrl http://127.0.0.1:8081 --ConnectionStrings:Flowbit $db @extra
```

```powershell
$db = 'Host=127.0.0.1;Port=55447;Database=flowbit_load;Username=flowbit;Password=load-test-only'
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --setup
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --users 0 --rest-users 16 --rest-mix sync --duration 120 --stub 5099 --database $db
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --users 0 --rest-users 16 --rest-mix job --duration 120 --stub 5099 --database $db
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --users 8 --duration 180 --webhooks 10 --stub 5099 --database $db
```

Bash:

```bash
db='Host=127.0.0.1;Port=55447;Database=flowbit_load;Username=flowbit;Password=load-test-only'
extra=(--WorkflowContext:Config:exampleApiBaseUrl http://127.0.0.1:5099 --WorkflowContext:Config:exampleApiToken load-test-stub
  --Webhooks:KeyProtection:CertificatePath "$PWD/dev/webhooks/webhook-keys.pfx" --Webhooks:KeyProtection:CertificatePassword flowbit-local-webhooks
  --Webhooks:Network:AllowLoopbackHttp true)
dotnet run -c Release --no-launch-profile --project ./Flowbit/src/Flowbit.Api/Flowbit.Api.csproj -- --contentRoot "$PWD/Flowbit/src/Flowbit.Api" --environment Development --Authentication:Mode DevelopmentToken --Serilog:MinimumLevel:Default Warning --urls http://127.0.0.1:5017 --ConnectionStrings:Flowbit "$db;Maximum Pool Size=60" "${extra[@]}"
```

```bash
db='Host=127.0.0.1;Port=55447;Database=flowbit_load;Username=flowbit;Password=load-test-only'
extra=(--WorkflowContext:Config:exampleApiBaseUrl http://127.0.0.1:5099 --WorkflowContext:Config:exampleApiToken load-test-stub
  --Webhooks:KeyProtection:CertificatePath "$PWD/dev/webhooks/webhook-keys.pfx" --Webhooks:KeyProtection:CertificatePassword flowbit-local-webhooks
  --Webhooks:Network:AllowLoopbackHttp true --Webhooks:DeliveryEnabled true)
dotnet run -c Release --no-launch-profile --project ./Flowbit/src/Flowbit.Worker/Flowbit.Worker.csproj -- --contentRoot "$PWD/Flowbit/src/Flowbit.Worker" --environment Development --Logging:LogLevel:Default Warning --FlowbitWorker:HealthListenUrl http://127.0.0.1:8081 --ConnectionStrings:Flowbit "$db" "${extra[@]}"
```

```bash
db='Host=127.0.0.1;Port=55447;Database=flowbit_load;Username=flowbit;Password=load-test-only'
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --setup
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --users 0 --rest-users 16 --rest-mix sync --duration 120 --stub 5099 --database "$db"
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --users 0 --rest-users 16 --rest-mix job --duration 120 --stub 5099 --database "$db"
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --users 8 --duration 180 --webhooks 10 --stub 5099 --database "$db"
```

To measure what capture costs without delivery, start the Worker with
`--Webhooks:DeliveryEnabled false` and pass `--webhook-wait 0`; deliveries then
queue and the archive at the end settles them. To measure the delivery rate,
create the events quickly and let the queue drain: `--park-at manager --users 16
--cases 3000 --webhooks 1 --stub 5099`. The `Stub` lines report deliveries per
second over the delivery window, how long after its event each delivery arrived,
and any repeated delivery or bad signature; the tool exits with `1` if any
arrived, or if a REST job was called the wrong number of times.

## Several API and Worker processes

Start a second API and a second Worker against the same database on other
ports, as the commands above do with `--urls http://127.0.0.1:5027` and
`--FlowbitWorker:HealthListenUrl http://127.0.0.1:8082`. Give each API
`Maximum Pool Size=30` so both fit beside the Workers in PostgreSQL's default
100 connections; two such APIs and two Workers used up to 80. Pass both API URLs
to the tool: each request goes to the next one, so every step of a case may run
on a different API than the step before.

```powershell
$db = 'Host=127.0.0.1;Port=55447;Database=flowbit_load;Username=flowbit;Password=load-test-only'
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --api http://127.0.0.1:5017,http://127.0.0.1:5027 --duration 180 --users 8 --timer-users 120 --signal-users 4 --database $db
Get-Content ./Flowbit/tools/LifecycleLoadTest/sql/replica-checks.sql | docker exec -i flowbit-load-pg psql -U flowbit -d flowbit_load
```

```bash
db='Host=127.0.0.1;Port=55447;Database=flowbit_load;Username=flowbit;Password=load-test-only'
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --api http://127.0.0.1:5017,http://127.0.0.1:5027 --duration 180 --users 8 --timer-users 120 --signal-users 4 --database "$db"
docker exec -i flowbit-load-pg psql -U flowbit -d flowbit_load < Flowbit/tools/LifecycleLoadTest/sql/replica-checks.sql
```

[`sql/replica-checks.sql`](sql/replica-checks.sql) is read-only. It lists jobs
completed more than once (expect none), job attempts by Worker process, jobs
not finished, open incidents, instances by status, and the database's deadlock
count. To test a Worker failing, start both Workers with
`--FlowbitWorker:LeaseSeconds 15 --FlowbitWorker:HeartbeatSeconds 5`, stop one
during a run, and check that every case still completes. Attempts the stopped
Worker held show as `leaseLost`. With REST cases running, a call it had in flight
reaches the stub again with the same job id, because external calls are at least
once; the stub reports that job, and the tool exits with `1` for it.

## Many open tasks

`--park-at` starts purchase requests and leaves them open. `manager` stops at the
manager's review, a task assigned to one of 100 managers. `budget` also
approves, which leaves an unclaimed **Confirm Budget** task that every `Finance`
user sees, plus a hidden **Select Supplier** task waiting for assignment, so two
open tasks per request. `--probe` then times a fixed set of reads one request at
a time: inboxes, instance and task searches, node-execution searches, and the
detail and diagram history of the oldest and newest instance.

```powershell
$db = 'Host=127.0.0.1;Port=55447;Database=flowbit_load;Username=flowbit;Password=load-test-only'
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --probe --database $db
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --users 16 --park-at manager --no-browse --cases 50000
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --users 16 --park-at budget --no-browse --cases 50000
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --probe --database $db
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --duration 180 --users 8 --database $db
```

```bash
db='Host=127.0.0.1;Port=55447;Database=flowbit_load;Username=flowbit;Password=load-test-only'
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --probe --database "$db"
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --users 16 --park-at manager --no-browse --cases 50000
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --users 16 --park-at budget --no-browse --cases 50000
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --probe --database "$db"
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --duration 180 --users 8 --database "$db"
```

Each probe reports the rows it found (the total count where the API returns
one) and its median, p95, and slowest time over `--probe-repeat` requests.

## Months of history

Run real purchase requests first, for as long as you can, then multiply them
with [`sql/copy-history.sql`](sql/copy-history.sql). The script copies every
completed instance of the purchase-request family `copies` times, moving each
copy's ids above every existing row and its times back by up to `days`, and
gives each copy its own business key. It checks before copying that every id
column of the tables it copies is accounted for, and afterwards that every
foreign key and reference of the copied rows points at a row of the same
instance; it stops with an error otherwise, and it refuses to run twice on one
database. Stop the API and Worker while it runs. Start the PostgreSQL container
for this with `--shm-size=1g` after `docker run`: Docker's default 64 MB of shared
memory makes large parallel queries fail with `53100`. `--check-copies` then reads
random originals and every copy of each through the API and compares their
detail, diagram history, and node executions, leaving out ids, times, and
business keys.

```powershell
$db = 'Host=127.0.0.1;Port=55447;Database=flowbit_load;Username=flowbit;Password=load-test-only'
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --users 8 --cases 100000 --database $db
Get-Content ./Flowbit/tools/LifecycleLoadTest/sql/copy-history.sql | docker exec -i flowbit-load-pg psql -U flowbit -d flowbit_load -v copies=9 -v days=270
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --check-copies 20 --database $db
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --probe --database $db
```

```bash
db='Host=127.0.0.1;Port=55447;Database=flowbit_load;Username=flowbit;Password=load-test-only'
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --users 8 --cases 100000 --database "$db"
docker exec -i flowbit-load-pg psql -U flowbit -d flowbit_load -v copies=9 -v days=270 < Flowbit/tools/LifecycleLoadTest/sql/copy-history.sql
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --check-copies 20 --database "$db"
dotnet run -c Release --project ./Flowbit/tools/LifecycleLoadTest -- --probe --database "$db"
```

Copies keep the original's JSON payloads, such as history details, so ids and
times inside them are the original's. Every copy is a completed request; add
open tasks afterwards with `--park-at`. For the statements behind a slow probe,
start PostgreSQL with `-c shared_preload_libraries=pg_stat_statements`, run
`CREATE EXTENSION pg_stat_statements` in the database, and read
[`sql/top-statements.sql`](sql/top-statements.sql) after a run.
`--print-token` prints an administrator's token for direct API calls, such as
starting a [retention](../../../docs/deployment.md#retention-and-backup) run.

## Options

```text
--setup                Import and publish the eight example definitions that are missing, then exit
--examples <dir>       Example catalog folder for --setup (default: examples)
--users <n>            Purchase-request virtual users (default: 16; 0 runs none)
--timer-users <n>      Timer-case virtual users (default: 0)
--signal-users <n>     Signal-case virtual users (default: 0)
--rest-users <n>       REST-case virtual users (default: 0); needs --stub
--cases <n>            Cases of each kind to run (default: 500 when no --duration)
--duration <seconds>   Stop starting new cases after this time; started cases finish
--poll-ms <n>          Inbox polling interval while waiting for the Worker (default: 1000)
--ramp <seconds>       Spread each kind's users' first case over this time (default: 10)
--board-share <0-1>    Share of purchase requests that go to the board vote (default: 0.2)
--no-browse            Skip the purchase inbox page before each action and the final detail read
--park-at <step>       Leave purchase requests open: 'manager' stops at the manager's review,
                       assigned to one of 100 managers; 'budget' also approves, leaving an
                       unclaimed task in the Finance pool
--starts-only          The same as --park-at manager
--rest-mix <mix>       both (default), sync, or job
--rest-fail-share <0-1> Share of REST jobs whose first call gets a 503 and retries (default: 0)
--stub <port>          Answer the REST examples' calls and webhooks on http://127.0.0.1:<port>;
                       give the API and Worker WorkflowContext:Config:exampleApiBaseUrl with it
--stub-delay-ms <n>    How long the stub waits before it answers (default: 100)
--stub-only            Only run the stub, reporting each minute, until Ctrl+C
--webhooks <n>         Create n webhook subscriptions to the stub (at most 100), turn lifecycle
                       capture on, and afterwards archive them and restore capture
--webhook-events <a,b> Subscribe to these event types only (default: every subscribable type)
--webhook-wait <s>     After the run, wait up to this long for deliveries to drain (default: 600; 0 skips)
--probe                Only time a fixed set of reads, one at a time, and exit
--probe-repeat <n>     Timed repeats of each probe (default: 30)
--check-copies <n>     After sql/copy-history.sql, compare n random copies with their originals
                       through the API, and exit; needs --database
--print-token          Print an administrator's token for this API and exit
--database <conn>      Npgsql connection string; reports growth, transactions, deadlocks,
                       lock waits, and Worker jobs
--api <url[,url]>      API base URL, or several to rotate requests across (default: http://127.0.0.1:5017)
--jwt-key <key>        DevelopmentToken signing key (default: the API's Development key)
--issuer <issuer>      Token issuer (default: workflow-engine-dev)
--audience <audience>  Token audience (default: workflow-engine-api)
-h, --help             Show this help
```

Against the [Docker Compose stack](../../../docs/deployment.md#local-docker-compose-stack),
pass its API address and token settings: `--api http://127.0.0.1:15017
--issuer flowbit-local --audience flowbit-api --jwt-key <FLOWBIT_JWT_KEY>`. Its
database is your development data, so prefer the disposable database above.
