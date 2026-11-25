# Lifecycle load test

This console tool measures how much work a Flowbit stack can carry. It runs
workflows from the [example catalog](../../../examples/README.md) through the
HTTP API, in up to three kinds of case side by side, each with its own number of
virtual users:

- **Purchase requests** (`--users`) run the purchase-request showcase from
  submission to order, with a separate actor for each role.
- **Timer cases** (`--timer-users`) wait for intermediate timers, recurring
  reminders, and an interrupting deadline.
- **Signal cases** (`--signal-users`) throw signals through the API and from a
  workflow, to waiting instances and to a signal start event.

It reports each kind's completed cases per second, the latency of every
operation, how late each timer and signal arrived, and, with `--database`, how
much the database grew and how long Worker jobs waited. A virtual user starts
its next case as soon as the last one ends, so the result is the stack's
maximum rate, not the response time people see at ordinary usage.

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
needed to find each task. `--starts-only` only submits requests, which then wait
at the manager review.

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

Timer and signal cases need a running Worker. While the tool waits for one, it
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

Import and publish the six example definitions, warm up, then measure purchase
requests alone and all three kinds together:

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

Import and publish the six example definitions, warm up, then measure purchase
requests alone and all three kinds together:

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

To compare two builds, publish both to the same disk and alternate their runs:
a few percent can come from where the binaries live rather than from the code.

## Options

```text
--setup                Import and publish the six example definitions that are missing, then exit
--examples <dir>       Example catalog folder for --setup (default: examples)
--users <n>            Purchase-request virtual users (default: 16; 0 runs none)
--timer-users <n>      Timer-case virtual users (default: 0)
--signal-users <n>     Signal-case virtual users (default: 0)
--cases <n>            Cases of each kind to run (default: 500 when no --duration)
--duration <seconds>   Stop starting new cases after this time; started cases finish
--poll-ms <n>          Inbox polling interval while waiting for the Worker (default: 1000)
--ramp <seconds>       Spread each kind's users' first case over this time (default: 10)
--board-share <0-1>    Share of purchase requests that go to the board vote (default: 0.2)
--no-browse            Skip the purchase inbox page before each action and the final detail read
--starts-only          Only start purchase requests; they stay at the manager review
--database <conn>      Npgsql connection string; reports growth, transactions, and Worker jobs
--api <url>            API base URL (default: http://127.0.0.1:5017)
--jwt-key <key>        DevelopmentToken signing key (default: the API's Development key)
--issuer <issuer>      Token issuer (default: workflow-engine-dev)
--audience <audience>  Token audience (default: workflow-engine-api)
```

Against the [Docker Compose stack](../../../docs/deployment.md#local-docker-compose-stack),
pass its API address and token settings: `--api http://127.0.0.1:15017
--issuer flowbit-local --audience flowbit-api --jwt-key <FLOWBIT_JWT_KEY>`. Its
database is your development data, so prefer the disposable database above.
