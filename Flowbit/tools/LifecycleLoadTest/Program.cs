// Drives business processes from the example catalog through the HTTP API and
// reports throughput, per-request latency, how late timers and signals arrive,
// and, with --database, database growth. See README.md in this folder.
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

Options options;
try
{
    options = Options.Parse(args);
}
catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException or IndexOutOfRangeException)
{
    Console.Error.WriteLine(ex is IndexOutOfRangeException ? "An option is missing its value." : ex.Message);
    Console.Error.WriteLine("Run with --help for the options.");
    return 2;
}

if (options.Help)
{
    Options.PrintHelp();
    return 0;
}

using var http = new HttpClient(new SocketsHttpHandler
{
    MaxConnectionsPerServer = 1024,
    PooledConnectionLifetime = TimeSpan.FromMinutes(30)
})
{
    BaseAddress = new Uri(options.ApiBase.TrimEnd('/') + "/"),
    Timeout = TimeSpan.FromMinutes(2)
};
var api = new ApiClient(http, options);
var admin = new Actor("lt-admin", ["admin"]);
var signalThrower = new Actor("lt-signal-system", ["admin"]);

if (options.Setup)
    return await SetupAsync();

PurchaseIds? purchase = null;
TimerIds? timers = null;
SignalIds? signals = null;
try
{
    var published = await ReadPublishedAsync();
    if (options.PurchaseUsers > 0)
        purchase = PurchaseIds.Resolve(await LoadFamilyAsync(published, Families.Purchase));
    if (options.TimerUsers > 0)
        timers = TimerIds.Resolve(
            await LoadFamilyAsync(published, Families.TimerDelay),
            await LoadFamilyAsync(published, Families.TimerReminder));
    if (options.SignalUsers > 0)
        signals = SignalIds.Resolve(
            await LoadFamilyAsync(published, Families.SignalOrders),
            await LoadFamilyAsync(published, Families.SignalRecall),
            await LoadFamilyAsync(published, Families.SignalNotices));
}
catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or JsonException)
{
    return Fail(ex.Message);
}

api.Stats.Clear();
var delays = new ConcurrentDictionary<string, ConcurrentBag<double>>();
var remindersExpected = 0;
var remindersFired = 0;
var remindersStillQueued = 0;
var timerFirings = 0;
var signalDeliveries = 0;
var throwRetries = 0;
var scenarios = new List<Scenario>();
if (purchase is not null)
    scenarios.Add(new Scenario("purchase", options.StartsOnly ? "Purchase requests started" : "Purchase requests", options.PurchaseUsers,
        n => options.StartsOnly ? StartPurchaseAsync(purchase, n, board: false) : RunPurchaseRequestAsync(purchase, n)));
if (timers is not null)
    scenarios.Add(new Scenario("timer", "Timer cases", options.TimerUsers, n => RunTimerCaseAsync(timers, n)));
if (signals is not null)
    scenarios.Add(new Scenario("signal", "Signal cases", options.SignalUsers, n => RunSignalCaseAsync(signals, n)));

Console.WriteLine("Flowbit lifecycle load test");
Console.WriteLine($"  {"API:",-28}{options.ApiBase}");
foreach (var scenario in scenarios)
    Console.WriteLine($"  {scenario.Title + ":",-28}{scenario.Users} virtual users");
if (purchase is not null && !options.StartsOnly)
    Console.WriteLine($"  {"Purchase mix:",-28}{options.BoardShare:P0} to the board vote; browsing reads {(options.Browse ? "on" : "off")}");
Console.WriteLine($"  {"Limit:",-28}{(options.Cases == int.MaxValue ? "" : options.Cases.ToString("N0", CultureInfo.InvariantCulture) + " of each kind")}" +
    $"{(options.Cases != int.MaxValue && options.DurationSeconds > 0 ? ", " : "")}{(options.DurationSeconds > 0 ? $"new work for {options.DurationSeconds} s" : "")}");
Console.WriteLine($"  {"Run tag:",-28}{options.Tag}");
Console.WriteLine();

DatabaseSnapshot? before;
try
{
    before = options.Database is { } connectionString ? await DatabaseSnapshot.ReadAsync(connectionString) : null;
}
catch (Exception ex) when (ex is NpgsqlException or ArgumentException)
{
    return Fail("--database: " + ex.Message);
}

var deadline = options.DurationSeconds > 0 ? DateTime.UtcNow.AddSeconds(options.DurationSeconds) : DateTime.MaxValue;
var elapsed = Stopwatch.StartNew();
using var stopReporting = new CancellationTokenSource();
var reporter = Task.Run(async () =>
{
    var last = scenarios.ToDictionary(s => s, _ => 0);
    var lastRequests = 0L;
    var lastSeconds = 0.0;
    while (!stopReporting.IsCancellationRequested)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(15), stopReporting.Token); }
        catch (OperationCanceledException) { break; }
        var seconds = elapsed.Elapsed.TotalSeconds;
        var requests = api.Stats.Requests;
        var interval = seconds - lastSeconds;
        var parts = scenarios.Select(s =>
        {
            var done = Volatile.Read(ref s.Completed);
            var text = $"{s.Name} {done:N0} ({(done - last[s]) / interval:F2}/s)";
            last[s] = done;
            return text + (s.Failed > 0 ? $" {s.Failed} failed" : "");
        });
        Console.WriteLine($"  {seconds,6:F0}s  {string.Join("  ", parts)}  {(requests - lastRequests) / interval:F1} HTTP requests/s");
        lastRequests = requests;
        lastSeconds = seconds;
    }
});

await Task.WhenAll(scenarios.Select(s => s.RunAsync(deadline, options.Cases, TimeSpan.FromSeconds(options.RampSeconds), elapsed)));
elapsed.Stop();
stopReporting.Cancel();
await reporter;

var after = options.Database is { } connection ? await DatabaseSnapshot.ReadAsync(connection) : null;
var jobs = options.Database is { } jobConnection && before is not null ? await JobSummary.ReadAsync(jobConnection, before.Now) : null;
PrintSummary();
return scenarios.Any(s => s.Failed > 0) ? 1 : 0;

// ---------------------------------------------------------------- setup

async Task<int> SetupAsync()
{
    try
    {
        var published = await ReadPublishedAsync(includeDrafts: true);
        foreach (var family in Families.All)
        {
            if (published.TryGetValue(family.Key, out var existing))
            {
                if (!existing.Published)
                    return Fail($"{family.Key} exists but its newest version is not published; publish it or use a fresh database.");
                Console.WriteLine($"  {family.Key}: version {existing.Version} is already published (id {existing.Id}).");
                continue;
            }

            var path = Path.Combine(options.ExamplesDirectory, family.File);
            var definition = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
            if (definition["id"]?.GetValue<string>() != family.Key)
                return Fail($"{path} does not define workflow '{family.Key}'.");
            var created = await api.SendAsync("setup", HttpMethod.Post, "api/workflows", admin, new { definition, publish = false });
            var id = created!["id"]!.GetValue<long>();
            await api.SendAsync("setup", HttpMethod.Post, $"api/workflows/{id}/publish", admin, null);
            Console.WriteLine($"  {family.Key}: imported and published as id {id}.");
        }

        return 0;
    }
    catch (Exception ex) when (ex is InvalidOperationException or HttpRequestException or IOException or JsonException)
    {
        return Fail(ex.Message);
    }
}

async Task<Dictionary<string, (long Id, int Version, bool Published)>> ReadPublishedAsync(bool includeDrafts = false)
{
    var list = await api.SendAsync("setup", HttpMethod.Get, "api/workflows", admin, null);
    return list!.AsArray()
        .Where(w => includeDrafts || w!["isPublished"]?.GetValue<bool>() == true)
        .GroupBy(w => w!["workflowKey"]!.GetValue<string>())
        .ToDictionary(
            g => g.Key,
            g =>
            {
                var newest = g.OrderByDescending(w => w!["version"]!.GetValue<int>()).First()!;
                return (newest["id"]!.GetValue<long>(), newest["version"]!.GetValue<int>(), newest["isPublished"]?.GetValue<bool>() == true);
            });
}

async Task<Family> LoadFamilyAsync(Dictionary<string, (long Id, int Version, bool Published)> published, FamilyFile family)
{
    if (!published.TryGetValue(family.Key, out var entry))
        throw new InvalidOperationException($"No published '{family.Key}' workflow. Run the tool with --setup first.");
    var workflow = await api.SendAsync("setup", HttpMethod.Get, $"api/workflows/{entry.Id}", admin, null);
    return new Family(family.Key, entry.Id, entry.Version, workflow!["definition"]!);
}

// ---------------------------------------------------------------- purchase requests

async Task<long> StartPurchaseAsync(PurchaseIds ids, int n, bool board)
{
    var requester = new Actor($"lt-requester-{n % 200}", ["Requester"]);
    var amount = board ? 25000 + n % 50 * 1000 : 500 + n * 37 % 23000;
    var started = await StartInstanceAsync("purchase", requester, ids.WorkflowId, ids.StartEvent, new Dictionary<string, object>
    {
        ["requestNumber"] = $"PR-{options.Tag}-{n}",
        ["title"] = $"Load test request {n}",
        ["category"] = PurchaseIds.Categories[n % PurchaseIds.Categories.Length],
        ["amount"] = amount,
        ["managerUser"] = $"lt-manager-{n % 100}"
    });
    return started["id"]!.GetValue<long>();
}

async Task RunPurchaseRequestAsync(PurchaseIds ids, int n)
{
    var manager = new Actor($"lt-manager-{n % 100}", ["Manager"]);
    var finance = new Actor($"lt-finance-{n % 10}", ["Finance"]);
    var lead = new Actor($"lt-lead-{n % 3}", ["ProcurementLead"]);
    var buyer = new Actor($"lt-buyer-{n % 40}", ["Buyer"]);
    // 61 shares no factor with 100, so every 100 consecutive requests hold the same share, spread out.
    var board = n * 61 % 100 < (int)Math.Round(options.BoardShare * 100);
    var instanceId = await StartPurchaseAsync(ids, n, board);

    await ActAsync("approve", manager, instanceId, ids.Review, ids.Approve, new { managerComment = "Looks fine" }, claim: false);
    if (board)
    {
        // Two different board members, because the vote allows one item per actor.
        var first = n % 7;
        var second = (first + 1 + n % 6) % 7;
        await ActAsync("vote", new Actor($"lt-board-{first}", ["BoardMember"]), instanceId, ids.BoardVote, ids.BoardApprove, new { voteComment = "Approve" }, claim: false);
        await ActAsync("vote", new Actor($"lt-board-{second}", ["BoardMember"]), instanceId, ids.BoardVote, ids.BoardApprove, new { voteComment = "Approve" }, claim: false);
    }

    await ActAsync("budget", finance, instanceId, ids.ConfirmBudget, ids.BudgetConfirmed, new { budgetCode = "IT-2026" }, claim: true);

    var page = await api.SendAsync("purchase.manageSearch", HttpMethod.Post, "api/user-tasks/manage/search", lead,
        new { instanceId, nodeId = ids.SelectSupplier, page = 1, pageSize = 10 });
    var item = page!["items"]!.AsArray().SingleOrDefault()
        ?? throw new InvalidOperationException($"manageSearch: no Select Supplier task on instance {instanceId}");
    await api.SendAsync("purchase.assign", HttpMethod.Post, $"api/user-tasks/{item["userTaskId"]}/assign", lead,
        new { actorId = buyer.Name, expectedUpdatedAt = item["updatedAt"]!.GetValue<string>(), reason = (string?)null }, write: true);

    await ActAsync("supplier", buyer, instanceId, ids.SelectSupplier, ids.SupplierSelected, new { supplierName = "Contoso", quotedAmount = 1000 + n % 9000 }, claim: false);
    var ack = await ActAsync("order", buyer, instanceId, ids.PlaceOrder, ids.OrderPlaced, new { purchaseOrderNumber = $"PO-{n}" }, claim: false);
    ExpectCompleted(ack, instanceId, "order");

    if (options.Browse)
        await api.SendAsync("purchase.detail", HttpMethod.Get, $"api/instances/{instanceId}", new Actor($"lt-requester-{n % 200}", ["Requester"]), null);
}

async Task<JsonNode?> ActAsync(string op, Actor actor, long instanceId, int nodeId, int flowId, object variables, bool claim)
{
    if (options.Browse)
        await api.SendAsync("purchase.inboxPage", HttpMethod.Post, "api/instances/inbox/search", actor, new { page = 1, pageSize = 50 });
    var found = await api.SendAsync("purchase.inboxFind", HttpMethod.Post, "api/instances/inbox/search", actor, new { instanceId, page = 1, pageSize = 50 });
    var task = found!["items"]!.AsArray().FirstOrDefault(t => t!["currentNodeId"]!.GetValue<int>() == nodeId)
        ?? throw new InvalidOperationException($"{op}: {actor.Name} has no inbox item at node {nodeId} on instance {instanceId}");
    var taskId = task["userTaskId"]!.GetValue<long>();
    if (claim)
        await api.SendAsync("purchase.claim", HttpMethod.Post, $"api/user-tasks/{taskId}/claim", actor, null, write: true);
    return await CompleteTaskAsync("purchase", op, actor, taskId, flowId, variables);
}

// ---------------------------------------------------------------- timers

async Task RunTimerCaseAsync(TimerIds ids, int n)
{
    var clerk = new Actor($"lt-clerk-{n % 50}", []);
    switch (n % 4)
    {
        case 0:
        case 1:
        {
            // An intermediate timer: the instance waits 30 seconds, then opens a task.
            var started = await StartInstanceAsync("timer", clerk, ids.DelayWorkflowId, ids.DelayStart, null);
            var instanceId = started["id"]!.GetValue<long>();
            await Task.Delay(TimeSpan.FromSeconds(30));
            var task = await WaitForTaskAsync("timer", clerk, new { instanceId, page = 1, pageSize = 50 }, ids.DelayTask, "30-second timer");
            RecordDelay(DelayKinds.TimerWait, task.CreatedAt - ReadTime(started["createdAt"]) - TimeSpan.FromSeconds(30));
            Interlocked.Increment(ref timerFirings);
            ExpectCompleted(await CompleteTaskAsync("timer", "finish", clerk, task.TaskId, ids.DelayFinish, new { }), instanceId, "timer wait");
            break;
        }
        case 2:
        {
            // A task with three reminders 15 seconds apart, approved before its one-minute deadline.
            var started = await StartInstanceAsync("timer", clerk, ids.ReminderWorkflowId, ids.ReminderStart, null);
            var instanceId = started["id"]!.GetValue<long>();
            await Task.Delay(TimeSpan.FromSeconds(52));
            var task = await WaitForTaskAsync("timer", clerk, new { instanceId, page = 1, pageSize = 50 }, ids.ApproveTask, "approval");
            ExpectCompletedOrReminding(await CompleteTaskAsync("timer", "approve", clerk, task.TaskId, ids.Approve, new { }), instanceId, "approval");
            await CountRemindersAsync(instanceId);
            break;
        }
        default:
        {
            // The same task left alone: after three reminders the deadline cancels it and escalates.
            var started = await StartInstanceAsync("timer", clerk, ids.ReminderWorkflowId, ids.ReminderStart, null);
            var instanceId = started["id"]!.GetValue<long>();
            await Task.Delay(TimeSpan.FromSeconds(60));
            var task = await WaitForTaskAsync("timer", clerk, new { instanceId, page = 1, pageSize = 50 }, ids.EscalatedTask, "one-minute deadline");
            RecordDelay(DelayKinds.Deadline, task.CreatedAt - ReadTime(started["createdAt"]) - TimeSpan.FromMinutes(1));
            Interlocked.Increment(ref timerFirings);
            ExpectCompletedOrReminding(await CompleteTaskAsync("timer", "escalated", clerk, task.TaskId, ids.CompleteEscalated, new { }), instanceId, "escalation");
            await CountRemindersAsync(instanceId);
            break;
        }
    }
}

// A reminder's branch runs its script as a Worker job, so when the Worker is behind, the
// instance can still be running after its task closes; it completes once those jobs run.
void ExpectCompletedOrReminding(JsonNode? ack, long instanceId, string what)
{
    var status = ack?["instanceStatus"]?.ToString();
    if (string.Equals(status, "running", StringComparison.OrdinalIgnoreCase))
        Interlocked.Increment(ref remindersStillQueued);
    else
        ExpectCompleted(ack, instanceId, what);
}

async Task CountRemindersAsync(long instanceId)
{
    // The instance search shows other people's instances only to a workflow administrator.
    var page = await api.SendAsync("timer.variables", HttpMethod.Post, "api/instances/search", admin,
        new { instanceId, includeVariables = true, page = 1, pageSize = 1 });
    var instance = page!["items"]!.AsArray().SingleOrDefault()
        ?? throw new InvalidOperationException($"reminders: instance {instanceId} is not in the instance search");
    var count = instance["variables"]?["remindersSent"]?.GetValue<int>() ?? 0;
    Interlocked.Add(ref remindersExpected, 3);
    Interlocked.Add(ref remindersFired, count);
    Interlocked.Add(ref timerFirings, count);
}

// ---------------------------------------------------------------- signals

async Task RunSignalCaseAsync(SignalIds ids, int n)
{
    var clerk = new Actor($"lt-warehouse-{n % 20}", []);
    var orderNumber = $"ORD-{options.Tag}-{n}";
    var order = await StartInstanceAsync("signal", clerk, ids.OrdersWorkflowId, ids.OrderStart, new { orderNumber });
    var orderId = order["id"]!.GetValue<long>();

    // An API throw to one order by business key; its non-interrupting boundary opens a branch.
    var price = await ThrowAsync(new { name = "warehouse.price-update", workflowKey = ids.OrdersKey, businessKey = orderNumber, payload = new { unitPrice = 10 + n % 90 } });
    var invoice = await WaitForTaskAsync("signal", clerk, new { instanceId = orderId, page = 1, pageSize = 50 }, ids.InvoiceTask, "price update");
    RecordDelay(DelayKinds.ApiThrow, invoice.CreatedAt - ReadTime(price["createdAt"]));
    Interlocked.Increment(ref signalDeliveries);
    await CompleteTaskAsync("signal", "invoice", clerk, invoice.TaskId, ids.InvoiceUpdated, new { });

    // A signal end event in another workflow interrupts the order through its other boundary.
    var decision = await StartInstanceAsync("signal", clerk, ids.RecallWorkflowId, ids.RecallStart, new { orderNumber });
    var decisionId = decision["id"]!.GetValue<long>();
    var decide = await WaitForTaskAsync("signal", clerk, new { instanceId = decisionId, page = 1, pageSize = 50 }, ids.DecideTask, "recall decision");
    var decided = await CompleteTaskAsync("signal", "recall", clerk, decide.TaskId, ids.Recall, new { recallReason = "Supplier recall notice" });
    ExpectCompleted(decided, decisionId, "recall decision");
    var stop = await WaitForTaskAsync("signal", clerk, new { instanceId = orderId, page = 1, pageSize = 50 }, ids.StopTask, "recall");
    RecordDelay(DelayKinds.EndEvent, stop.CreatedAt - ReadTime(decided!["updatedAt"]));
    Interlocked.Increment(ref signalDeliveries);
    ExpectCompleted(await CompleteTaskAsync("signal", "stop", clerk, stop.TaskId, ids.Stopped, new { }), orderId, "stopped order");

    // A throw to a family whose signal start event begins a new instance.
    var recallNumber = $"RC-{options.Tag}-{n}";
    var announced = await ThrowAsync(new { name = "warehouse.recall-announced", workflowKey = ids.NoticesKey, payload = new { recallNumber, product = new { name = "Widget" }, reason = "Load test" } });
    var notice = await WaitForTaskAsync("signal", clerk, new { workflowKey = ids.NoticesKey, businessKey = recallNumber, page = 1, pageSize = 50 }, ids.NoticeTask, "signal start");
    RecordDelay(DelayKinds.Start, notice.CreatedAt - ReadTime(announced["createdAt"]));
    Interlocked.Increment(ref signalDeliveries);
    var sent = await CompleteTaskAsync("signal", "notice", clerk, notice.TaskId, ids.SendNotice, new { });
    ExpectCompleted(sent, sent!["instanceId"]!.GetValue<long>(), "notice");
}

async Task<JsonNode> ThrowAsync(object request)
{
    for (var attempt = 1; ; attempt++)
    {
        try
        {
            return (await api.SendAsync("signal.throw", HttpMethod.Post, "api/signal-broadcasts", signalThrower, request, write: true))!;
        }
        catch (ApiException ex) when (ex.Status == 409 && ex.Body.Contains("still delivering", StringComparison.Ordinal) && attempt < 240)
        {
            // The API refuses a throw while Signals.MaxOpenBroadcasts broadcasts are delivering.
            Interlocked.Increment(ref throwRetries);
            await Task.Delay(250);
        }
    }
}

// ---------------------------------------------------------------- shared steps

async Task<JsonNode> StartInstanceAsync(string scenario, Actor actor, long workflowId, int startEventId, object? variables)
{
    var started = await api.SendAsync($"{scenario}.start", HttpMethod.Post, "api/instances", actor,
        new { workflowId, startEventId, variables }, write: true);
    return started!;
}

async Task<(long TaskId, DateTimeOffset CreatedAt)> WaitForTaskAsync(string scenario, Actor actor, object filter, int nodeId, string what)
{
    var giveUp = DateTime.UtcNow.AddMinutes(10);
    var wait = options.PollMilliseconds;
    while (true)
    {
        var found = await api.SendAsync($"{scenario}.inboxPoll", HttpMethod.Post, "api/instances/inbox/search", actor, filter);
        var item = found!["items"]!.AsArray().FirstOrDefault(t => t!["currentNodeId"]!.GetValue<int>() == nodeId);
        if (item is not null)
            return (item["userTaskId"]!.GetValue<long>(), ReadTime(item["taskCreatedAt"]));
        if (DateTime.UtcNow > giveUp)
            throw new InvalidOperationException($"{what}: no task after 10 minutes; is a Worker running?");
        // Back off while the Worker is behind, as a person refreshing a page would, so that waiting
        // users do not add load of their own. Delays are measured from server timestamps, not polls.
        await Task.Delay(wait);
        wait = Math.Min(wait * 2, Math.Max(options.PollMilliseconds, 10_000));
    }
}

async Task<JsonNode?> CompleteTaskAsync(string scenario, string op, Actor actor, long taskId, int flowId, object variables)
{
    var flows = await api.SendAsync($"{scenario}.flows", HttpMethod.Get, $"api/user-tasks/{taskId}/flows", actor, null);
    if (!flows!.AsArray().Any(f => f!["id"]!.GetValue<int>() == flowId))
        throw new InvalidOperationException($"{op}: flow {flowId} is not available on task {taskId}");
    return await api.SendAsync($"{scenario}.{op}", HttpMethod.Post, $"api/user-tasks/{taskId}/flows/{flowId}", actor, new { variables }, write: true);
}

void RecordDelay(string kind, TimeSpan delay) =>
    delays.GetOrAdd(kind, _ => []).Add(delay.TotalMilliseconds);

static void ExpectCompleted(JsonNode? ack, long instanceId, string what)
{
    var status = ack?["instanceStatus"]?.ToString();
    if (!string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"{what}: instance {instanceId} is '{status}', not completed");
}

static DateTimeOffset ReadTime(JsonNode? node) =>
    DateTimeOffset.Parse(node?.GetValue<string>() ?? throw new InvalidOperationException("A response has no timestamp."), CultureInfo.InvariantCulture);

// ---------------------------------------------------------------- summary

void PrintSummary()
{
    var stats = api.Stats.Snapshot().ToList();
    var seconds = elapsed.Elapsed.TotalSeconds;
    Console.WriteLine();
    Console.WriteLine($"Elapsed {seconds:F1} s; {api.Stats.Requests / seconds:F1} HTTP requests/s " +
        $"(writes {api.Stats.Writes / seconds:F1}/s, reads {(api.Stats.Requests - api.Stats.Writes) / seconds:F1}/s)");
    foreach (var scenario in scenarios)
    {
        var own = stats.Where(s => s.Op.StartsWith(scenario.Name + ".", StringComparison.Ordinal)).ToList();
        var writes = own.Where(s => s.Write).Sum(s => s.Samples.Length);
        var reads = own.Where(s => !s.Write).Sum(s => s.Samples.Length);
        var durations = scenario.Durations.OrderBy(x => x).ToArray();
        var active = scenario.Elapsed.TotalSeconds;
        Console.WriteLine($"  {scenario.Title}: {scenario.Completed:N0} completed, {scenario.Failed:N0} failed, " +
            $"{scenario.Completed / Math.Max(0.001, active):F2}/s over {active:F0} s");
        if (scenario.Completed > 0)
            Console.WriteLine($"    each: {(double)writes / scenario.Completed:F1} writes, {(double)reads / scenario.Completed:F1} reads; " +
                $"duration p50 {Percentile(durations, .50) / 1000:F1} s, p95 {Percentile(durations, .95) / 1000:F1} s");
    }

    if (timers is not null)
        Console.WriteLine($"  Timer firings observed: {timerFirings:N0} ({timerFirings / seconds:F2}/s); reminders fired before the task closed: {remindersFired:N0} of {remindersExpected:N0}" +
            (remindersStillQueued > 0 ? $"; tasks closed with reminder work still queued: {remindersStillQueued:N0}" : ""));
    if (signals is not null)
        Console.WriteLine($"  Signal deliveries observed: {signalDeliveries:N0} ({signalDeliveries / seconds:F2}/s); throws retried at the open-broadcast limit: {throwRetries:N0}");

    if (before is not null && after is not null)
    {
        var instances = after.Instances - before.Instances;
        var bytes = after.Bytes - before.Bytes;
        var transactions = after.Transactions - before.Transactions;
        Console.WriteLine($"  Database: +{instances:N0} instances, +{bytes / 1048576.0:F1} MB" +
            (instances > 0 ? $" ({bytes / 1024.0 / instances:F1} KB per instance)" : "") +
            $", {transactions / seconds:F0} transactions/s" +
            (api.Stats.Requests > 0 ? $" ({(double)transactions / api.Stats.Requests:F1} per HTTP request)" : ""));
    }

    if (jobs is { Count: > 0 })
    {
        Console.WriteLine();
        Console.WriteLine("  Worker jobs finished during the run (start = how long after its due time a job began):");
        Console.WriteLine($"  {"kind",-24} {"queue",-9} {"status",-10} {"count",7} {"per s",7} {"start p50 s",12} {"start p95 s",12} {"start max s",12} {"run p50 ms",11}");
        foreach (var job in jobs)
            Console.WriteLine(
                $"  {job.Kind,-24} {job.Queue,-9} {job.Status,-10} {job.Count,7:N0} {job.Count / seconds,7:F2} " +
                $"{Seconds(job.StartP50),12} {Seconds(job.StartP95),12} {Seconds(job.StartMax),12} {(job.RunP50 is { } run ? (run * 1000).ToString("F0", CultureInfo.InvariantCulture) : "-"),11}");
    }

    if (!delays.IsEmpty)
    {
        Console.WriteLine();
        Console.WriteLine("  Delay from when the event was due or thrown to when its task existed (server clocks):");
        Console.WriteLine($"  {"event",-44} {"count",7} {"p50 ms",8} {"p95 ms",8} {"max ms",8}");
        foreach (var kind in DelayKinds.Order)
        {
            if (!delays.TryGetValue(kind, out var bag)) continue;
            var sorted = bag.OrderBy(x => x).ToArray();
            Console.WriteLine($"  {kind,-44} {sorted.Length,7:N0} {Percentile(sorted, .5),8:F0} {Percentile(sorted, .95),8:F0} {sorted[^1],8:F0}");
        }
    }

    Console.WriteLine();
    Console.WriteLine($"  {"operation",-22} {"count",8} {"per item",9} {"p50 ms",8} {"p95 ms",8} {"p99 ms",8} {"max ms",8} {"errors",7}");
    foreach (var (op, samples, write, errorCount) in stats)
    {
        var sorted = samples.OrderBy(x => x).ToArray();
        if (sorted.Length == 0) continue;
        var owner = scenarios.FirstOrDefault(s => op.StartsWith(s.Name + ".", StringComparison.Ordinal));
        var perItem = owner is null ? "" : ((double)sorted.Length / Math.Max(1, owner.Completed)).ToString("F2", CultureInfo.InvariantCulture);
        Console.WriteLine(
            $"  {op,-22} {sorted.Length,8:N0} {perItem,9} " +
            $"{Percentile(sorted, .50),8:F1} {Percentile(sorted, .95),8:F1} {Percentile(sorted, .99),8:F1} {sorted[^1],8:F1} {errorCount,7}");
    }

    foreach (var scenario in scenarios.Where(s => !s.Errors.IsEmpty))
    {
        Console.WriteLine();
        Console.WriteLine($"Sample errors ({scenario.Title}):");
        foreach (var error in scenario.Errors.Take(10)) Console.WriteLine("  - " + error);
    }
}

static string Seconds(double? value) => value is { } v ? v.ToString("F2", CultureInfo.InvariantCulture) : "-";

static double Percentile(double[] sorted, double p) =>
    sorted.Length == 0 ? 0 : sorted[Math.Clamp((int)Math.Ceiling(p * sorted.Length) - 1, 0, sorted.Length - 1)];

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}

// ---------------------------------------------------------------- types

sealed record Actor(string Name, string[] Roles);

sealed record FamilyFile(string Key, string File);

static class Families
{
    public static readonly FamilyFile Purchase = new("example-showcase-purchase-request", "showcases/01-purchase-request-approval.json");
    public static readonly FamilyFile TimerDelay = new("example-intermediate-timer-delay", "timers/02-intermediate-timer-delay.json");
    public static readonly FamilyFile TimerReminder = new("example-user-task-reminder-deadline", "timers/05-user-task-reminder-and-deadline.json");
    public static readonly FamilyFile SignalOrders = new("example-signal-boundaries-recall", "signals/02-signal-boundaries-recall.json");
    public static readonly FamilyFile SignalRecall = new("example-signal-end-recall-decision", "signals/04-signal-end-recall-decision.json");
    public static readonly FamilyFile SignalNotices = new("example-signal-start-recall-notice", "signals/05-signal-start-recall-notice.json");

    // The recall decision throws to the orders family, so publication needs that family first.
    public static readonly FamilyFile[] All = [Purchase, TimerDelay, TimerReminder, SignalOrders, SignalRecall, SignalNotices];
}

static class DelayKinds
{
    public const string TimerWait = "Intermediate timer (30 s wait)";
    public const string Deadline = "Interrupting timer boundary (1 min deadline)";
    public const string ApiThrow = "API throw by business key to a boundary";
    public const string EndEvent = "Signal end event to another instance";
    public const string Start = "API throw to a signal start event";
    public static readonly string[] Order = [TimerWait, Deadline, ApiThrow, EndEvent, Start];
}

/// <summary>A published definition's node and flow ids, looked up by external id.</summary>
sealed class Family
{
    private readonly Dictionary<string, int> _nodes;
    private readonly Dictionary<string, int> _flows;

    public Family(string key, long id, int version, JsonNode definition)
    {
        Key = key;
        Id = id;
        Version = version;
        _nodes = Index(definition["flowNodes"]);
        _flows = Index(definition["sequenceFlows"]);
    }

    public string Key { get; }
    public long Id { get; }
    public int Version { get; }

    public int Node(string externalId) => _nodes.TryGetValue(externalId, out var id)
        ? id
        : throw new InvalidOperationException($"Workflow '{Key}' has no flow node with external id {externalId}.");

    public int Flow(string externalId) => _flows.TryGetValue(externalId, out var id)
        ? id
        : throw new InvalidOperationException($"Workflow '{Key}' has no sequence flow with external id {externalId}.");

    private static Dictionary<string, int> Index(JsonNode? elements) =>
        (elements?.AsArray() ?? [])
            .Where(e => e?["externalId"] is not null && e["id"] is not null)
            .GroupBy(e => e!["externalId"]!.GetValue<string>(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First()!["id"]!.GetValue<int>(), StringComparer.Ordinal);
}

sealed record PurchaseIds(
    long WorkflowId, int StartEvent, int Review, int BoardVote, int ConfirmBudget, int SelectSupplier, int PlaceOrder,
    int Approve, int BoardApprove, int BudgetConfirmed, int SupplierSelected, int OrderPlaced)
{
    public static readonly string[] Categories = ["equipment", "software", "services"];

    public static PurchaseIds Resolve(Family f) => new(
        f.Id,
        f.Node("START_PURCHASE_REQUESTED"), f.Node("TASK_MANAGER_REVIEW"), f.Node("TASK_BOARD_VOTE"),
        f.Node("TASK_CONFIRM_BUDGET"), f.Node("TASK_SELECT_SUPPLIER"), f.Node("TASK_PLACE_ORDER"),
        f.Flow("FLOW_MANAGER_APPROVE"), f.Flow("FLOW_BOARD_APPROVE"), f.Flow("FLOW_BUDGET_CONFIRMED"),
        f.Flow("FLOW_SUPPLIER_SELECTED"), f.Flow("FLOW_ORDER_PLACED"));
}

sealed record TimerIds(
    long DelayWorkflowId, int DelayStart, int DelayTask, int DelayFinish,
    long ReminderWorkflowId, int ReminderStart, int ApproveTask, int Approve, int EscalatedTask, int CompleteEscalated)
{
    public static TimerIds Resolve(Family delay, Family reminder) => new(
        delay.Id, delay.Node("START_INTERMEDIATE_TIMER_DELAY"), delay.Node("TASK_ACKNOWLEDGE_TIMER_DELAY"), delay.Flow("FLOW_FINISH_TIMER_DELAY"),
        reminder.Id, reminder.Node("START_REMINDER_DEADLINE_EXAMPLE"), reminder.Node("TASK_APPROVE_BEFORE_DEADLINE"),
        reminder.Flow("FLOW_APPROVE_BEFORE_DEADLINE"), reminder.Node("TASK_ESCALATED_APPROVAL"), reminder.Flow("FLOW_COMPLETE_ESCALATED_APPROVAL"));
}

sealed record SignalIds(
    string OrdersKey, long OrdersWorkflowId, int OrderStart, int InvoiceTask, int InvoiceUpdated, int StopTask, int Stopped,
    long RecallWorkflowId, int RecallStart, int DecideTask, int Recall,
    string NoticesKey, int NoticeTask, int SendNotice)
{
    public static SignalIds Resolve(Family orders, Family recall, Family notices) => new(
        orders.Key, orders.Id, orders.Node("START_ORDER"), orders.Node("TASK_UPDATE_INVOICE"), orders.Flow("FLOW_INVOICE_UPDATED"),
        orders.Node("TASK_STOP_SHIPMENT"), orders.Flow("FLOW_STOPPED"),
        recall.Id, recall.Node("START_RECALL_REQUEST"), recall.Node("TASK_DECIDE_RECALL"), recall.Flow("FLOW_RECALL"),
        notices.Key, notices.Node("TASK_WRITE_NOTICE"), notices.Flow("FLOW_SEND_NOTICE"));
}

sealed class Scenario(string name, string title, int users, Func<int, Task> body)
{
    public int Completed;
    public int Failed;

    public string Name { get; } = name;
    public string Title { get; } = title;
    public int Users { get; } = users;
    public ConcurrentBag<double> Durations { get; } = [];
    public ConcurrentQueue<string> Errors { get; } = new();
    public TimeSpan Elapsed { get; private set; }

    public async Task RunAsync(DateTime deadline, int limit, TimeSpan ramp, Stopwatch clock)
    {
        var next = -1;
        await Task.WhenAll(Enumerable.Range(0, Users).Select(async user =>
        {
            // Users join one by one over the ramp, as people and systems arrive, not all at once.
            await Task.Delay(ramp * user / Users);
            while (DateTime.UtcNow < deadline)
            {
                var n = Interlocked.Increment(ref next);
                if (n >= limit) break;
                var watch = Stopwatch.StartNew();
                try
                {
                    await body(n);
                    Durations.Add(watch.Elapsed.TotalMilliseconds);
                    Interlocked.Increment(ref Completed);
                }
                catch (Exception ex)
                {
                    Interlocked.Increment(ref Failed);
                    if (Errors.Count < 20) Errors.Enqueue(ex.Message);
                }
            }
        }));
        Elapsed = clock.Elapsed;
    }
}

sealed class ApiException(string message, int status, string body) : InvalidOperationException(message)
{
    public int Status { get; } = status;
    public string Body { get; } = body;
}

sealed class ApiClient(HttpClient http, Options options)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, string> _tokens = new(StringComparer.Ordinal);

    public Stats Stats { get; } = new();

    public async Task<JsonNode?> SendAsync(string op, HttpMethod method, string url, Actor actor, object? body, bool write = false)
    {
        using var request = new HttpRequestMessage(method, url);
        var tokenKey = actor.Name + "|" + string.Join(',', actor.Roles);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _tokens.GetOrAdd(tokenKey, _ => CreateToken(actor)));
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
        var watch = Stopwatch.StartNew();
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Stats.Record(op, watch.Elapsed.TotalMilliseconds, write, response.IsSuccessStatusCode);
        if (!response.IsSuccessStatusCode)
            throw new ApiException(
                $"{op}: {method} {url} as {actor.Name} returned {(int)response.StatusCode} {(text.Length > 300 ? text[..300] + "..." : text)}",
                (int)response.StatusCode, text);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private string CreateToken(Actor actor)
    {
        var credentials = new SigningCredentials(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(options.JwtKey)), SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim> { new(ClaimTypes.Name, actor.Name), new(JwtRegisteredClaimNames.Sub, actor.Name) };
        claims.AddRange(actor.Roles.Select(role => new Claim(ClaimTypes.Role, role)));
        var token = new JwtSecurityToken(options.Issuer, options.Audience, claims,
            DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddHours(24), credentials);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}

sealed class Stats
{
    private readonly ConcurrentDictionary<string, Operation> _operations = new();
    private long _requests;
    private long _writes;

    public long Requests => Interlocked.Read(ref _requests);
    public long Writes => Interlocked.Read(ref _writes);

    public void Record(string op, double milliseconds, bool write, bool succeeded)
    {
        var operation = _operations.GetOrAdd(op, _ => new Operation(write));
        operation.Samples.Add(milliseconds);
        if (!succeeded) Interlocked.Increment(ref operation.Errors);
        Interlocked.Increment(ref _requests);
        if (write) Interlocked.Increment(ref _writes);
    }

    public void Clear()
    {
        _operations.Clear();
        Interlocked.Exchange(ref _requests, 0);
        Interlocked.Exchange(ref _writes, 0);
    }

    public IEnumerable<(string Op, double[] Samples, bool Write, int Errors)> Snapshot() =>
        _operations.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => (kv.Key, kv.Value.Samples.ToArray(), kv.Value.Write, Volatile.Read(ref kv.Value.Errors)));

    private sealed class Operation(bool write)
    {
        public readonly ConcurrentBag<double> Samples = [];
        public readonly bool Write = write;
        public int Errors;
    }
}

sealed record DatabaseSnapshot(long Bytes, long Instances, long Transactions, DateTime Now)
{
    public static async Task<DatabaseSnapshot> ReadAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT pg_database_size(current_database()),
                   (SELECT count(*) FROM flowbit.workflow_instances),
                   (SELECT xact_commit FROM pg_stat_database WHERE datname = current_database()),
                   now()
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new DatabaseSnapshot(reader.GetInt64(0), reader.GetInt64(1), reader.GetInt64(2), reader.GetDateTime(3));
    }
}

/// <summary>Worker jobs that finished since a time, grouped by kind, queue, and final status.</summary>
sealed record JobSummary(string Kind, string Queue, string Status, long Count, double? StartP50, double? StartP95, double? StartMax, double? RunP50)
{
    public static async Task<List<JobSummary>> ReadAsync(string connectionString, DateTime since)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT "Kind", "QueueClass", "Status", count(*),
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY extract(epoch FROM "StartedAt" - "DueAt")::float8),
                   percentile_cont(0.95) WITHIN GROUP (ORDER BY extract(epoch FROM "StartedAt" - "DueAt")::float8),
                   max(extract(epoch FROM "StartedAt" - "DueAt")::float8),
                   percentile_cont(0.5) WITHIN GROUP (ORDER BY extract(epoch FROM "CompletedAt" - "StartedAt")::float8)
            FROM flowbit.workflow_jobs
            WHERE "CompletedAt" >= @since
            GROUP BY "Kind", "QueueClass", "Status"
            ORDER BY count(*) DESC
            """, connection);
        command.Parameters.AddWithValue("since", since);
        await using var reader = await command.ExecuteReaderAsync();
        var rows = new List<JobSummary>();
        while (await reader.ReadAsync())
        {
            double? Nullable(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);
            rows.Add(new JobSummary(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3),
                Nullable(4), Nullable(5), Nullable(6), Nullable(7)));
        }

        return rows;
    }
}

sealed record Options(
    string ApiBase = "http://127.0.0.1:5017",
    bool Setup = false,
    string ExamplesDirectory = "examples",
    int Cases = int.MaxValue,
    int DurationSeconds = 0,
    int PurchaseUsers = 16,
    int TimerUsers = 0,
    int SignalUsers = 0,
    int PollMilliseconds = 1000,
    int RampSeconds = 10,
    double BoardShare = 0.2,
    bool Browse = true,
    bool StartsOnly = false,
    string? Database = null,
    string JwtKey = "dev-only-symmetric-signing-key-change-me-please-32+",
    string Issuer = "workflow-engine-dev",
    string Audience = "workflow-engine-api",
    string Tag = "",
    bool Help = false)
{
    public static Options Parse(string[] args)
    {
        var options = new Options(Tag: Guid.NewGuid().ToString("N")[..4]);
        for (var i = 0; i < args.Length; i++)
        {
            options = args[i] switch
            {
                "--api" => options with { ApiBase = args[++i] },
                "--setup" => options with { Setup = true },
                "--examples" => options with { ExamplesDirectory = args[++i] },
                "--cases" => options with { Cases = Number(args[++i], "--cases", 1) },
                "--duration" => options with { DurationSeconds = Number(args[++i], "--duration", 1) },
                "--users" => options with { PurchaseUsers = Math.Min(512, Number(args[++i], "--users", 0)) },
                "--timer-users" => options with { TimerUsers = Math.Min(2000, Number(args[++i], "--timer-users", 0)) },
                "--signal-users" => options with { SignalUsers = Math.Min(512, Number(args[++i], "--signal-users", 0)) },
                "--poll-ms" => options with { PollMilliseconds = Number(args[++i], "--poll-ms", 50) },
                "--ramp" => options with { RampSeconds = Number(args[++i], "--ramp", 0) },
                "--board-share" => options with { BoardShare = Share(args[++i]) },
                "--no-browse" => options with { Browse = false },
                "--starts-only" => options with { StartsOnly = true },
                "--database" => options with { Database = args[++i] },
                "--jwt-key" => options with { JwtKey = args[++i] },
                "--issuer" => options with { Issuer = args[++i] },
                "--audience" => options with { Audience = args[++i] },
                "--help" or "-h" => options with { Help = true },
                _ => throw new ArgumentException($"Unknown option '{args[i]}'.")
            };
        }

        if (!options.Setup && !options.Help && options.PurchaseUsers + options.TimerUsers + options.SignalUsers == 0)
            throw new ArgumentException("Give at least one of --users, --timer-users, and --signal-users a value above zero.");
        return options.Cases == int.MaxValue && options.DurationSeconds == 0
            ? options with { Cases = 500 }
            : options;
    }

    private static int Number(string value, string name, int minimum)
    {
        var number = int.Parse(value, CultureInfo.InvariantCulture);
        return number >= minimum ? number : throw new ArgumentException($"{name} must be at least {minimum}.");
    }

    private static double Share(string value)
    {
        var share = double.Parse(value, CultureInfo.InvariantCulture);
        return share is >= 0 and <= 1 ? share : throw new ArgumentException("--board-share must be from 0 to 1.");
    }

    public static void PrintHelp() => Console.WriteLine(
        """
        Usage: dotnet run -c Release --project Flowbit/tools/LifecycleLoadTest -- [options]

        Runs workflows from the example catalog through the HTTP API, each kind with its own
        virtual users, and reports throughput, latency, and how late timers and signals arrive.

          Purchase requests (--users): the purchase-request showcase from submission to order.
          Timer cases (--timer-users): a 30-second intermediate timer, and a task with three
            15-second reminders that is either approved at 52 s or escalated by its 1-minute deadline.
          Signal cases (--signal-users): an API throw by business key that opens a branch on an
            order, a signal end event in another workflow that interrupts it, and an API throw
            that starts an instance through a signal start event.

        Timer and signal cases need a running Worker. Each virtual user acts again as soon as its
        last request returns, so the result is the maximum rate, not response time at real usage.

        Options:
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
          -h, --help             Show this help
        """);
}
