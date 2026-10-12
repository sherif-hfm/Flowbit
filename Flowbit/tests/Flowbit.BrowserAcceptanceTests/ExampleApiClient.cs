using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.BrowserTests.Infrastructure;
using Flowbit.BrowserTests.Support;
using Flowbit.Shared.Dtos;
using Xunit;

namespace Flowbit.BrowserAcceptanceTests;

/// <summary>Authenticated HTTP only; uses canonical definitions with the shared identity suffix.</summary>
internal sealed class ExampleApiClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly HttpClient http;
    public WorkflowFixtureClient Workflows { get; }

    private ExampleApiClient(BrowserStackFixture stack, string token, string actor, string[] roles)
    {
        http = new() { BaseAddress = new Uri(stack.ApiBaseAddress) };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        Workflows = stack.CreateClient(actor, roles, token, "examples");
    }

    public static async Task<ExampleApiClient> CreateAsync(BrowserScenario scenario, string actor, params string[] roles)
    {
        var identity = new IdentityScreen(scenario.Page, scenario.Stack.UiBaseAddress);
        var token = await identity.GenerateAndApplyIdentityAsync(actor, roles);
        return new(scenario.Stack, token, actor, roles);
    }

    public Task<T> GetAsync<T>(string path) => SendAsync<T>(HttpMethod.Get, path);
    public Task<T> PostAsync<T>(string path, object? body = null) => SendAsync<T>(HttpMethod.Post, path, body);

    public async Task<T> SendAsync<T>(HttpMethod method, string path, object? body = null,
        IReadOnlyDictionary<string, string>? headers = null)
    {
        using var response = await SendRawAsync(method, path, body, headers);
        var content = await response.Content.ReadAsStringAsync(ScenarioCancellation.Token);
        Assert.True(response.IsSuccessStatusCode, $"{method} {path}: {(int)response.StatusCode}: {content}");
        return JsonSerializer.Deserialize<T>(content, JsonOptions)!;
    }

    public async Task ExpectStatusAsync(HttpStatusCode expected, HttpMethod method, string path,
        object? body = null, IReadOnlyDictionary<string, string>? headers = null)
    {
        using var response = await SendRawAsync(method, path, body, headers);
        Assert.True(response.StatusCode == expected,
            $"{method} {path}: expected {(int)expected}, got {(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
    }

    private async Task<HttpResponseMessage> SendRawAsync(HttpMethod method, string path,
        object? body, IReadOnlyDictionary<string, string>? headers)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        if (headers is not null)
            foreach (var (name, value) in headers) request.Headers.Add(name, value);
        return await http.SendAsync(request, ScenarioCancellation.Token);
    }

    public async Task<IReadOnlyList<JobSummaryDto>> JobsAsync(long instanceId) =>
        (await GetAsync<PagedResult<JobSummaryDto>>($"/api/jobs?instanceId={instanceId}&pageSize=200")).Items;
    public async Task<IReadOnlyList<IncidentSummaryDto>> IncidentsAsync(long instanceId) =>
        (await GetAsync<PagedResult<IncidentSummaryDto>>($"/api/incidents?instanceId={instanceId}&pageSize=200")).Items;
    public async Task<IReadOnlyList<NodeExecutionSummaryDto>> ExecutionsAsync(long instanceId) =>
        (await GetAsync<PagedResult<NodeExecutionSummaryDto>>($"/api/node-executions?instanceId={instanceId}&pageSize=200")).Items;
    public async Task<IReadOnlyList<ManagedUserTaskDto>> ManagedTasksAsync(long instanceId) =>
        (await GetAsync<PagedResult<ManagedUserTaskDto>>($"/api/user-tasks/manage?instanceId={instanceId}&status=open&pageSize=200")).Items;
    public async Task<IReadOnlyList<JobAttemptDto>> AttemptsAsync(long jobId) =>
        (await GetAsync<PagedResult<JobAttemptDto>>($"/api/jobs/{jobId}/attempts?pageSize=200")).Items;

    public static Dictionary<string, JsonElement> Variables(params (string Name, object Value)[] values) =>
        values.ToDictionary(item => item.Name, item => JsonSerializer.SerializeToElement(item.Value));

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
                throw new TimeoutException($"Timed out waiting for {description}. Last response: {JsonSerializer.Serialize(value, JsonOptions)}");
            await Task.Delay(200, ScenarioCancellation.Token);
        }
    }

    public static IReadOnlyDictionary<string, string> MessageHeaders(string correlation, string deliveryKey) =>
        new Dictionary<string, string>
        {
            ["X-Client-Id"] = ExampleAcceptanceFixture.MessageClientId,
            ["X-Client-Secret"] = ExampleAcceptanceFixture.MessageClientSecret,
            ["X-Correlation"] = correlation,
            ["X-Flowbit-Delivery-Key"] = deliveryKey,
        };

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await Workflows.DisposeAsync();
    }
}
