using System.Diagnostics;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.Playwright;
using Xunit;

namespace Flowbit.BrowserAcceptanceTests;

/// <summary>An identity applied through the real token screen, with clients bound to its token.</summary>
internal sealed record MiActor(string User, string[] Roles, WorkflowFixtureClient Runtime, HttpClient Http) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        Http.Dispose();
        await Runtime.DisposeAsync();
    }
}

/// <summary>
/// Shared steps for the automatic multi-instance acceptance scenarios. State is set up only
/// through authenticated HTTP and the real UI; reads poll real API responses.
/// </summary>
internal static class AutomaticMi
{
    public static readonly (int Width, int Height)[] Viewports = [(1440, 900), (1024, 768), (390, 844)];
    public static readonly string[] OperatorRoles = ["admin"];
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static JsonElement Json(object? value) => JsonSerializer.SerializeToElement(value, JsonOptions);

    public static string Unique(string prefix) => $"{prefix}-{Guid.NewGuid().ToString("N")[..8]}";

    public static async Task<MiActor> ApplyActorAsync(BrowserScenario scenario, string user, params string[] roles)
    {
        var identity = new IdentityScreen(scenario.Page, scenario.Stack.UiBaseAddress);
        var token = await identity.GenerateAndApplyIdentityAsync(user, roles);
        scenario.OnCleanup(identity.ClearIdentityAsync);
        return CreateActor(scenario, user, roles, token);
    }

    /// <summary>Changes the UI's identity from a second tab, as a person switching users would.</summary>
    public static async Task<MiActor> SwitchIdentityInAnotherTabAsync(BrowserScenario scenario, string user, params string[] roles)
    {
        var other = await scenario.Context.NewPageAsync();
        try
        {
            var token = await new IdentityScreen(other, scenario.Stack.UiBaseAddress).GenerateAndApplyIdentityAsync(user, roles);
            return CreateActor(scenario, user, roles, token);
        }
        finally { await other.CloseAsync(); }
    }

    private static MiActor CreateActor(BrowserScenario scenario, string user, string[] roles, string token)
    {
        var http = new HttpClient { BaseAddress = new Uri(scenario.Stack.ApiBaseAddress) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return new(user, roles, scenario.Stack.CreateClient(user, roles, token, $"{scenario.Name}-{user}"), http);
    }

    public static async Task<T> ReadAsync<T>(HttpClient http, string path)
    {
        using var response = await http.GetAsync(path, ScenarioCancellation.Token);
        var content = await response.Content.ReadAsStringAsync(ScenarioCancellation.Token);
        Assert.True(response.IsSuccessStatusCode, $"GET {path}: {(int)response.StatusCode} {content}");
        return JsonSerializer.Deserialize<T>(content, JsonOptions)!;
    }

    public static async Task<(int Status, string Body)> SendAsync(HttpClient http, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        using var response = await http.SendAsync(request, ScenarioCancellation.Token);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync(ScenarioCancellation.Token));
    }

    public static async Task<T> SendOkAsync<T>(HttpClient http, HttpMethod method, string path, object? body = null)
    {
        var (status, content) = await SendAsync(http, method, path, body);
        Assert.True(status is >= 200 and < 300, $"{method} {path}: {status} {content}");
        return JsonSerializer.Deserialize<T>(content, JsonOptions)!;
    }

    public static async Task<T> UntilAsync<T>(Func<Task<T>> read, Func<T, bool> accepted, string description,
        TimeSpan? timeout = null)
    {
        var timer = Stopwatch.StartNew();
        var budget = timeout ?? TimeSpan.FromSeconds(90);
        while (true)
        {
            var value = await read();
            if (accepted(value)) return value;
            if (timer.Elapsed >= budget)
                throw new TimeoutException($"Timed out waiting for {description}. Last value: {JsonSerializer.Serialize(value, JsonOptions)}");
            await Task.Delay(250, ScenarioCancellation.Token);
        }
    }

    public static Task<InstanceDetailDto> InstanceAsync(MiActor actor, long instanceId) =>
        actor.Runtime.GetInstanceAsync(instanceId);

    public static Task<InstanceDetailDto> UntilInstanceAsync(MiActor actor, long instanceId,
        Func<InstanceDetailDto, bool> accepted, string description, TimeSpan? timeout = null) =>
        UntilAsync(() => actor.Runtime.GetInstanceAsync(instanceId), accepted, description, timeout);

    public static AutomaticMultiInstanceProgressDto? Body(InstanceDetailDto detail, int nodeId) =>
        detail.AutomaticMultiInstances.SingleOrDefault(body => body.NodeId == nodeId);

    public static Func<InstanceDetailDto, bool> AtNode(int nodeId) => detail =>
        detail.ExecutionPositions.Any(position => position.NodeId == nodeId && position.TokenStatus == "active");

    public static bool Visited(InstanceDetailDto detail, int nodeId) =>
        detail.ExecutionPositions.Any(position => position.NodeId == nodeId);

    public static JsonElement Latest(InstanceDetailDto detail, string name) =>
        detail.Variables.Where(variable => variable.VariableName == name).OrderByDescending(variable => variable.Id).First().Value;

    public static Task<PagedResult<AutomaticMultiInstanceItemSummaryDto>> ItemsAsync(MiActor actor, long executionId,
        int page = 1, int pageSize = 50) =>
        ReadAsync<PagedResult<AutomaticMultiInstanceItemSummaryDto>>(actor.Http,
            $"/api/multi-instance-executions/{executionId}/items?page={page}&pageSize={pageSize}");

    public static async Task<IReadOnlyList<IncidentSummaryDto>> IncidentsAsync(MiActor actor, long instanceId) =>
        (await ReadAsync<PagedResult<IncidentSummaryDto>>(actor.Http, $"/api/incidents?instanceId={instanceId}&pageSize=200")).Items;

    public static async Task<IReadOnlyList<JobSummaryDto>> JobsAsync(MiActor actor, long instanceId) =>
        (await ReadAsync<PagedResult<JobSummaryDto>>(actor.Http, $"/api/jobs?instanceId={instanceId}&pageSize=200")).Items;

    public static async Task<IReadOnlyList<CallActivityExecutionDto>> CallsAsync(MiActor actor, long instanceId) =>
        (await ReadAsync<PagedResult<CallActivityExecutionDto>>(actor.Http, $"/api/instances/{instanceId}/calls?pageSize=200")).Items;

    public static Task<UpdateInstanceVariablesResultDto> SetVariableAsync(MiActor actor, long instanceId, string name, object value) =>
        SendOkAsync<UpdateInstanceVariablesResultDto>(actor.Http, HttpMethod.Patch, $"/api/instances/{instanceId}/variables",
            new UpdateInstanceVariablesRequest([new(name, Json(value))], "Automatic multi-instance acceptance", null));

    public static async Task<SharedVariableMetadataDto> CreateSharedAsync(MiActor actor, string key, string dataType, object value) =>
        await SendOkAsync<SharedVariableMetadataDto>(actor.Http, HttpMethod.Post, "/api/shared-variables",
            new CreateSharedVariableRequest(key, dataType, IsArray: false, Nullable: false, HasValue: true, Json(value)));

    public static async Task SetSharedAsync(MiActor actor, string key, object value)
    {
        var current = await ReadAsync<SharedVariableMetadataDto>(actor.Http, $"/api/shared-variables/{key}");
        await SendOkAsync<JsonElement>(actor.Http, HttpMethod.Put, $"/api/shared-variables/{key}/value",
            new UpdateSharedVariableValueRequest(Json(value), current.Revision, Reason: "Concurrent acceptance change"));
    }

    public static async Task<JsonElement> SharedValueAsync(MiActor actor, string key) =>
        (await ReadAsync<JsonElement>(actor.Http, $"/api/shared-variables/{key}/value")).GetProperty("value");

    public static Task WaitForInteractiveAsync(IPage page) => Assertions.Expect(page.Locator(".app-shell"))
        .ToHaveAttributeAsync("data-interactive", "true");

    public static async Task<IPage> OpenInstanceAsync(BrowserScenario scenario, long instanceId)
    {
        var page = await scenario.OpenUiAsync($"instances/{instanceId}");
        await WaitForInteractiveAsync(page);
        // "Loading instance #N" never matches: the loaded heading is "Instance #N".
        await Assertions.Expect(page.Locator("h1")).ToContainTextAsync($"Instance #{instanceId}", new() { Timeout = 30_000 });
        return page;
    }

    /// <summary>Reloads an instance page and waits until it has loaded, not just until "Loading instance".</summary>
    public static async Task ReloadInstanceAsync(IPage page)
    {
        var instanceId = System.Text.RegularExpressions.Regex.Match(page.Url, @"/instances/(\d+)").Groups[1].Value;
        await page.ReloadAsync();
        await WaitForInteractiveAsync(page);
        await Assertions.Expect(page.Locator("h1")).ToContainTextAsync($"Instance #{instanceId}", new() { Timeout = 30_000 });
    }

    public static ILocator AutomaticSection(IPage page) => page.Locator("#automatic-multi-instance-results");

    public static ILocator Summary(IPage page) => page.Locator("section[aria-labelledby='instance-summary-heading']");

    /// <summary>
    /// Selects an active node on the read-only diagram with the keyboard and returns the
    /// details panel, which describes what the position waits for and its item progress.
    /// </summary>
    public static async Task<ILocator> DiagramDetailsAsync(IPage page, int nodeId)
    {
        var node = page.Locator($".flowbit-editor[data-mode='viewer'] .node.instance-marked[data-id='{nodeId}']");
        await Assertions.Expect(node).ToBeVisibleAsync();
        await node.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        var details = page.Locator(".instance-diagram-details");
        await Assertions.Expect(details.Locator("h3")).Not.ToHaveTextAsync("Selected node");
        return details;
    }

    /// <summary>Opens a body's item table through its real button and waits for the rows.</summary>
    public static async Task<ILocator> ShowItemsAsync(IPage page, long executionId)
    {
        var section = AutomaticSection(page);
        var row = section.Locator("tbody tr").Filter(new LocatorFilterOptions { HasText = $"#{executionId}" }).First;
        await row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Show items" }).ClickAsync();
        await Assertions.Expect(section.GetByRole(AriaRole.Heading, new LocatorGetByRoleOptions { Name = $"Execution #{executionId} items" }))
            .ToBeVisibleAsync();
        return section.Locator("table").Nth(1);
    }

    public static ILocator ItemRow(IPage page, long itemId) =>
        AutomaticSection(page).Locator("tbody tr").Filter(new LocatorFilterOptions { HasText = $"Item #{itemId}" });

    /// <summary>Retries an incident from Operations with the keyboard: focus Retry, press Enter.</summary>
    public static async Task RetryFromOperationsAsync(BrowserScenario scenario, IncidentSummaryDto incident, string captureName)
    {
        var page = await scenario.OpenUiAsync("operations");
        await WaitForInteractiveAsync(page);
        var row = page.Locator("section[aria-labelledby='incident-heading'] tbody tr")
            .Filter(new LocatorFilterOptions { HasTextRegex = new System.Text.RegularExpressions.Regex($"Job #{incident.JobId}(?![0-9])") });
        await Assertions.Expect(row).ToContainTextAsync(incident.Type);
        if (incident.AutomaticMultiInstanceItemId is { } itemId)
            await Assertions.Expect(row).ToContainTextAsync($"Automatic item #{itemId}");
        await CaptureAtViewportsAsync(scenario, captureName);
        var retry = row.GetByRole(AriaRole.Button, new LocatorGetByRoleOptions { Name = "Retry", Exact = true });
        await retry.FocusAsync();
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(row).ToHaveCountAsync(0, new LocatorAssertionsToHaveCountOptions { Timeout = 30_000 });
    }

    /// <summary>
    /// Captures the current page at each runtime viewport, checking that it never scrolls
    /// sideways, then returns to the scenario's own size.
    /// </summary>
    public static async Task CaptureAtViewportsAsync(BrowserScenario scenario, string name)
    {
        var page = scenario.Page;
        var original = page.ViewportSize!;
        try
        {
            foreach (var (width, height) in Viewports)
            {
                await page.SetViewportSizeAsync(width, height);
                await page.EvaluateAsync("() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))");
                Assert.True(await page.EvaluateAsync<bool>("document.documentElement.scrollWidth <= window.innerWidth + 2"),
                    $"'{name}' overflows horizontally at {width}x{height}.");
                await PageScrolling.ScrollToTopAsync(page);
                await page.ScreenshotAsync(new PageScreenshotOptions
                {
                    Path = Path.Combine(scenario.ArtifactDirectory, $"{name}-{width}x{height}.png"),
                    FullPage = true,
                });
            }
        }
        finally { await page.SetViewportSizeAsync(original.Width, original.Height); }
    }

    /// <summary>
    /// Selects a diagram element through the editor's search: a click on a small boundary
    /// at fit zoom can start connect mode instead of selecting it.
    /// </summary>
    public static async Task SelectBySearchAsync(IPage page, int nodeId)
    {
        // As the editor's own "/" shortcut does: the tools handle reveals the docked search,
        // then the box takes focus. The handle can sit under a pinned inspector at phone width.
        await page.Locator("#diagramToolsHandle").FocusAsync();
        await page.Locator("#diagramSearchInput").FocusAsync();
        await Assertions.Expect(page.Locator("#diagramSearchInput")).ToBeFocusedAsync();
        await page.Keyboard.PressAsync("Control+a");
        await page.Keyboard.TypeAsync($"#{nodeId}");
        await page.Keyboard.PressAsync("Enter");
        await Assertions.Expect(page.Locator("#inspector").GetByRole(AriaRole.Heading,
            new LocatorGetByRoleOptions { Name = $"Node #{nodeId}", Exact = true })).ToHaveCountAsync(1);
    }

    public static async Task WriteEvidenceAsync(BrowserScenario scenario, string name, object evidence) =>
        await File.WriteAllTextAsync(Path.Combine(scenario.ArtifactDirectory, name + ".json"),
            JsonSerializer.Serialize(evidence, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }),
            ScenarioCancellation.Token);

    // ----- Definitions -----

    public static MultiInstanceOutputVariableModel Local(string name, string dataType = "number") => new()
    {
        Name = name, DataType = dataType, IsArray = false, Nullable = false
    };

    public static MultiInstanceModel Multi(string mode, string collection, string result, int maxConcurrency,
        params MultiInstanceOutputVariableModel[] outputs) => new()
    {
        Mode = mode, Source = "collection", CollectionVariable = collection, CompletionEvaluation = "afterAll",
        MaxConcurrency = mode == "sequential" ? 1 : maxConcurrency, ResultVariable = result, OutputVariables = outputs.ToList()
    };

    /// <summary>
    /// Start → body (node 2) → "Review results" user task (node 4) → end (node 3). The review
    /// task keeps the instance running so a finished body stays on screen.
    /// </summary>
    public static WorkflowModel BodyWorkflow(string key, FlowNodeModel body, params VariableModel[] variables) => new()
    {
        Id = key,
        Name = key,
        InitialEventId = 1,
        Variables = variables.ToList(),
        FlowNodes =
        [
            new() { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent, X = 60, Y = 152 },
            body,
            new() { Id = 4, Name = "Review results", Type = BpmnFlowNodeTypes.UserTask, X = 480, Y = 140 },
            new() { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent, X = 760, Y = 152 },
        ],
        SequenceFlows =
        [
            new() { Id = 101, SourceRef = 1, TargetRef = 2 },
            new() { Id = 201, SourceRef = 2, TargetRef = 4 },
            new() { Id = 401, Name = "Accept", SourceRef = 4, TargetRef = 3 },
        ]
    };

    public static VariableModel Process(int id, string name, string dataType, object? defaultValue, bool isArray = false) => new()
    {
        Id = id, Name = name, DataType = dataType, IsArray = isArray, DefaultValue = Json(defaultValue)
    };

    /// <summary>A nullable string that starts null, such as an error boundary's variable.</summary>
    public static VariableModel NullableString(int id, string name) => new()
    {
        Id = id, Name = name, DataType = "string", Nullable = true
    };

    public static VariableModel SharedBinding(int id, string alias, string key, string dataType, string access = "readWrite") => new()
    {
        Id = id, Name = alias, DataType = dataType, IsArray = false, Nullable = false,
        Scope = "shared", SharedKey = key, Access = access
    };

    /// <summary>
    /// One POST per item to the controlled service's per-item route, keyed by the item's own
    /// stable job id. The receipt id and the HTTP status go to item-local outputs.
    /// </summary>
    public static FlowNodeModel ServiceBody(string mode = "parallel", int maxConcurrency = 3,
        string failureHandling = JobFailureHandling.BoundaryFirst, params string[] retryDelays) => new()
    {
        Id = 2, Name = "Send receipts", Type = BpmnFlowNodeTypes.ServiceTask, AsyncBefore = true, X = 200, Y = 140,
        Job = new() { FailureHandling = failureHandling, RetryDelays = retryDelays.ToList() },
        MultiInstance = Multi(mode, "items", "receipts", maxConcurrency, Local("receipt", "string"), Local("httpStatus")),
        Service = new()
        {
            Type = "rest", Method = "POST", TimeoutSeconds = 120,
            Url = "${config.exampleApiBaseUrl}/items/${group}/${mi.index}",
            Headers =
            [
                new() { Name = "Authorization", Value = "Bearer ${config.exampleApiToken}" },
                new() { Name = "Content-Type", Value = "application/json" },
                new() { Name = "Idempotency-Key", Value = "item-${sys.jobId}" },
            ],
            Body = "{\"item\":${mi.item},\"index\":${mi.index},\"total\":${mi.total}}",
            StatusVariable = "httpStatus",
            OutputMappings = [new() { Variable = "receipt", Path = "id", DataType = "string", IsArray = false, Required = true }]
        }
    };

    /// <summary>The variables <see cref="ServiceBody"/> reads and writes.</summary>
    public static VariableModel[] ServiceVariables(string group, params int[] items) =>
    [
        Process(1, "group", "string", group),
        Process(2, "items", "number", items, isArray: true),
        Process(3, "receipts", "json", Array.Empty<object>()),
    ];

    /// <summary>An attached boundary on the body with its own user task and end.</summary>
    public static void AddBoundary(WorkflowModel model, FlowNodeModel boundary, string taskName)
    {
        boundary.AttachedToRef = 2;
        boundary.X = 270;
        boundary.Y = 214;
        var task = boundary.Id + 1;
        var end = boundary.Id + 2;
        model.FlowNodes.Add(boundary);
        model.FlowNodes.Add(new() { Id = task, Name = taskName, Type = BpmnFlowNodeTypes.UserTask, X = 480, Y = 300 });
        model.FlowNodes.Add(new() { Id = end, Name = taskName + " done", Type = BpmnFlowNodeTypes.EndEvent, X = 760, Y = 312 });
        model.SequenceFlows.Add(new() { Id = boundary.Id * 100 + 1, SourceRef = boundary.Id, TargetRef = task });
        model.SequenceFlows.Add(new() { Id = task * 100 + 1, Name = "Close", SourceRef = task, TargetRef = end });
    }

    public static FlowNodeModel TimerBoundary(int id, string duration, bool interrupting) => new()
    {
        Id = id, Name = interrupting ? "Too slow" : "Reminder", Type = BpmnFlowNodeTypes.TimerBoundaryEvent,
        CancelActivity = interrupting ? null : false, Timer = new() { TimeDuration = duration }
    };

    public static FlowNodeModel ConditionalBoundary(int id, string condition, bool interrupting) => new()
    {
        Id = id, Name = interrupting ? "Stopped" : "Noticed", Type = BpmnFlowNodeTypes.ConditionalBoundaryEvent,
        CancelActivity = interrupting ? null : false, Conditional = new() { Condition = condition }
    };

    public static FlowNodeModel ErrorBoundary(int id, string errorVariable) => new()
    {
        Id = id, Name = "Item failed", Type = BpmnFlowNodeTypes.ErrorBoundaryEvent, ErrorVariable = errorVariable
    };
}
