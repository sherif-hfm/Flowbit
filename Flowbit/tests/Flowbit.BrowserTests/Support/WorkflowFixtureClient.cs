using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Flowbit.BrowserTests.Infrastructure;

namespace Flowbit.BrowserTests.Support;

/// <summary>
/// Seeds and inspects runtime state through authenticated HTTP only. Tokens are
/// explicit; changing the UI identity never changes a configured client. No
/// direct database access, no fabricated DTOs, and no application-global
/// JavaScript mutations are used for setup or verification.
/// </summary>
public sealed class WorkflowFixtureClient : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient http;
    private readonly string runDirectory;
    private readonly string purpose;

    public WorkflowFixtureClient(
        string apiBaseAddress,
        string token,
        string actor,
        string[] roles,
        string purpose,
        string runDirectory)
    {
        ApiBaseAddress = apiBaseAddress.TrimEnd('/');
        Actor = actor;
        Roles = roles;
        this.purpose = purpose;
        this.runDirectory = runDirectory;
        http = new HttpClient
        {
            BaseAddress = new Uri(ApiBaseAddress),
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    public string ApiBaseAddress { get; }

    public string Actor { get; }

    public string[] Roles { get; }

    public ValueTask DisposeAsync()
    {
        http.Dispose();
        return ValueTask.CompletedTask;
    }

    public void Dispose() => http.Dispose();

    private async Task<T> SendAsync<T>(
        HttpMethod method,
        string path,
        object? body = null,
        [System.Runtime.CompilerServices.CallerMemberName] string step = "")
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        using var response = await http.SendAsync(request, ScenarioCancellation.Token);
        await AssertSuccessAsync(response, step);
        var payload = await response.Content.ReadFromJsonAsync<T>(JsonOptions, ScenarioCancellation.Token);
        return payload!;
    }

    private async Task SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        [CallerMemberName] string step = "")
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        using var response = await http.SendAsync(request, ScenarioCancellation.Token);
        await AssertSuccessAsync(response, step);
    }

    private static async Task AssertSuccessAsync(HttpResponseMessage response, string step)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }
        var bodyText = await response.Content.ReadAsStringAsync();
        throw new InvalidOperationException(
            $"Fixture HTTP {step} failed with {(int)response.StatusCode} {response.StatusCode}: {bodyText}");
    }

    public async Task<IReadOnlyList<WorkflowSummaryDto>> ListWorkflowsAsync() =>
        await SendAsync<IReadOnlyList<WorkflowSummaryDto>>(HttpMethod.Get, "/api/workflows/");

    /// <summary>
    /// Creates and publishes a definition from a fixture JSON file. The
    /// authored <c>id</c> is suffixed with a unique token before POST so
    /// repeated runs cannot collide; checked-in fixture JSON is unchanged.
    /// No other client-side normalization applies.
    /// </summary>
    public async Task<WorkflowSummaryDto> CreateAndPublishAsync(string definitionJsonPath)
    {
        var raw = JsonNode.Parse(await File.ReadAllTextAsync(definitionJsonPath, ScenarioCancellation.Token))
            ?? throw new InvalidOperationException($"Fixture JSON could not be parsed: {definitionJsonPath}");
        if (raw is JsonObject definition)
        {
            var catalogId = definition["id"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(catalogId))
            {
                definition["id"] = $"{catalogId}-{Guid.NewGuid().ToString("N")[..8]}";
            }
        }
        var request = new JsonObject
        {
            ["definition"] = raw,
            ["publish"] = true,
        };
        using var response = await http.PostAsJsonAsync("/api/workflows/", request, JsonOptions, ScenarioCancellation.Token);
        await AssertSuccessAsync(response, "CreateAndPublish");
        var summary = await response.Content.ReadFromJsonAsync<WorkflowSummaryDto>(JsonOptions, ScenarioCancellation.Token);
        return summary!;
    }

    public Task<WorkflowDetailDto> GetWorkflowAsync(long id) =>
        SendAsync<WorkflowDetailDto>(HttpMethod.Get, $"/api/workflows/{id}");

    /// <summary>Publishes an explicit model without changing its stable family key.</summary>
    public Task<WorkflowSummaryDto> CreateAndPublishAsync(WorkflowModel definition) =>
        SendAsync<WorkflowSummaryDto>(HttpMethod.Post, "/api/workflows/",
            new CreateWorkflowRequest(definition, true));

    public Task<SharedVariableMetadataDto> CreateSharedVariableAsync(CreateSharedVariableRequest request) =>
        SendAsync<SharedVariableMetadataDto>(HttpMethod.Post, "/api/shared-variables", request);

    public Task<IReadOnlyList<UserDelegationDto>> CreateDelegationAsync(CreateUserDelegationRequest request) =>
        SendAsync<IReadOnlyList<UserDelegationDto>>(HttpMethod.Post, "/api/user-delegations", request);

    public Task<UserDelegationDto> AcceptDelegationAsync(long id, UserDelegationLifecycleRequest request) =>
        SendAsync<UserDelegationDto>(HttpMethod.Post, $"/api/user-delegations/{id}/accept", request);

    public Task<InstanceVersionChangeBatchDetailDto> CreateVersionChangeBatchAsync(CreateInstanceVersionChangeBatchRequest request) =>
        SendAsync<InstanceVersionChangeBatchDetailDto>(HttpMethod.Post, "/api/instance-version-change-batches", request);

    public Task<InstanceVersionChangeBatchDetailDto> GetVersionChangeBatchAsync(long id) =>
        SendAsync<InstanceVersionChangeBatchDetailDto>(HttpMethod.Get, $"/api/instance-version-change-batches/{id}");

    public Task<InstanceVersionChangeBatchDetailDto> ConfirmVersionChangeBatchAsync(long id, ConfirmInstanceVersionChangeBatchRequest request) =>
        SendAsync<InstanceVersionChangeBatchDetailDto>(HttpMethod.Post, $"/api/instance-version-change-batches/{id}/confirm", request);

    public Task<InstanceVariableUpdateBatchDetailDto> CreateVariableUpdateBatchAsync(CreateInstanceVariableUpdateBatchRequest request) =>
        SendAsync<InstanceVariableUpdateBatchDetailDto>(HttpMethod.Post, "/api/instance-variable-update-batches", request);

    public Task<InstanceVariableUpdateBatchDetailDto> GetVariableUpdateBatchAsync(long id) =>
        SendAsync<InstanceVariableUpdateBatchDetailDto>(HttpMethod.Get, $"/api/instance-variable-update-batches/{id}");

    public Task<InstanceVariableUpdateBatchDetailDto> ConfirmVariableUpdateBatchAsync(long id, ConfirmInstanceVariableUpdateBatchRequest request) =>
        SendAsync<InstanceVariableUpdateBatchDetailDto>(HttpMethod.Post, $"/api/instance-variable-update-batches/{id}/confirm", request);

    public Task<StartInstanceResultDto> StartInstanceAsync(
        long workflowId, Dictionary<string, JsonElement>? variables = null, int? startEventId = null) =>
        SendAsync<StartInstanceResultDto>(HttpMethod.Post, "/api/instances/", new
        {
            workflowId,
            startEventId,
            variables,
        });

    public Task<InstanceDetailDto> GetInstanceAsync(long id) =>
        SendAsync<InstanceDetailDto>(HttpMethod.Get, $"/api/instances/{id}");

    public Task<IReadOnlyList<SequenceFlowModel>> GetAvailableFlowsAsync(long id) =>
        SendAsync<IReadOnlyList<SequenceFlowModel>>(HttpMethod.Get, $"/api/instances/{id}/flows");

    public Task ClaimInstanceAsync(long instanceId) =>
        SendAsync(HttpMethod.Post, $"/api/instances/{instanceId}/claim");

    public Task UnclaimInstanceAsync(long instanceId) =>
        SendAsync(HttpMethod.Post, $"/api/instances/{instanceId}/unclaim");

    public Task TakeInstanceFlowAsync(long instanceId, int flowId, Dictionary<string, JsonElement>? variables = null) =>
        SendAsync(HttpMethod.Post, $"/api/instances/{instanceId}/flows/{flowId}", new { variables });

    public async Task<IReadOnlyList<InboxItemDto>> GetInboxAsync(long? instanceId = null)
    {
        var path = instanceId is null ? "/api/instances/inbox" : $"/api/instances/inbox?instanceId={instanceId}";
        var page = await SendAsync<PagedResult<InboxItemDto>>(HttpMethod.Get, path);
        return page.Items;
    }

    public Task<UserTaskDto> GetUserTaskAsync(long taskId) =>
        SendAsync<UserTaskDto>(HttpMethod.Get, $"/api/user-tasks/{taskId}");

    public Task<IReadOnlyList<SequenceFlowModel>> GetUserTaskFlowsAsync(long taskId) =>
        SendAsync<IReadOnlyList<SequenceFlowModel>>(HttpMethod.Get, $"/api/user-tasks/{taskId}/flows");

    public Task<UserTaskDto> ClaimUserTaskAsync(long taskId) =>
        SendAsync<UserTaskDto>(HttpMethod.Post, $"/api/user-tasks/{taskId}/claim");

    public Task<UserTaskActionAckDto> TakeUserTaskFlowAsync(
        long taskId, int flowId, Dictionary<string, JsonElement>? variables = null) =>
        SendAsync<UserTaskActionAckDto>(
            HttpMethod.Post,
            $"/api/user-tasks/{taskId}/flows/{flowId}",
            new { variables });

    public Task<PagedResult<InstanceSummaryDto>> ListInstancesAsync(
        string? status = null, long? instanceId = null, long? workflowId = null)
    {
        var query = new List<string>();
        if (status is not null) query.Add($"status={Uri.EscapeDataString(status)}");
        if (instanceId is not null) query.Add($"instanceId={instanceId}");
        if (workflowId is not null) query.Add($"workflowId={workflowId}");
        var path = "/api/instances/" + (query.Count > 0 ? "?" + string.Join("&", query) : string.Empty);
        return SendAsync<PagedResult<InstanceSummaryDto>>(HttpMethod.Get, path);
    }

    /// <summary>Reads one administrative batch audit for rendered-content verification.</summary>
    public Task<AdministrativeActionBatchDetailDto> GetAdministrativeActionBatchAsync(long batchId) =>
        SendAsync<AdministrativeActionBatchDetailDto>(HttpMethod.Get, $"/api/administrative-action-batches/{batchId}");

    /// <summary>Reads one administrative batch's item page for rendered-content verification.</summary>
    public Task<PagedResult<AdministrativeActionBatchItemDto>> GetAdministrativeActionBatchItemsAsync(
        long batchId, string? status = null, int page = 1, int pageSize = 50)
    {
        var query = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={Uri.EscapeDataString(status)}");
        return SendAsync<PagedResult<AdministrativeActionBatchItemDto>>(
            HttpMethod.Get,
            $"/api/administrative-action-batches/{batchId}/items?{string.Join("&", query)}");
    }

    /// <summary>Reads the recent administrative batch list for rendered-content verification.</summary>
    public Task<PagedResult<AdministrativeActionBatchSummaryDto>> GetAdministrativeActionBatchesAsync(
        string? status = null, int page = 1, int pageSize = 25)
    {
        var query = new List<string> { $"page={page}", $"pageSize={pageSize}" };
        if (!string.IsNullOrWhiteSpace(status)) query.Add($"status={Uri.EscapeDataString(status)}");
        return SendAsync<PagedResult<AdministrativeActionBatchSummaryDto>>(
            HttpMethod.Get,
            $"/api/administrative-action-batches?{string.Join("&", query)}");
    }

    /// <summary>
    /// Finds the active user-task work item for a given collection item index
    /// through the actor's inbox (used to drive child completion over HTTP).
    /// </summary>
    public async Task<long> FindInboxTaskIdByItemIndexAsync(long instanceId, int itemIndex)
    {
        var inbox = await GetInboxAsync(instanceId);
        var match = inbox.FirstOrDefault(item => item.ItemIndex == itemIndex);
        if (match is null)
        {
            throw new InvalidOperationException(
                $"Inbox for '{Actor}' has no active item #{itemIndex} in instance {instanceId}. " +
                $"Rows: {inbox.Count}");
        }
        return match.UserTaskId;
    }

    /// <summary>Saves a diagnostic record (without secrets) to the run directory.</summary>
    public async Task WriteDiagnosticsAsync(string name, string content)
    {
        var path = Path.Combine(runDirectory, $"{purpose}-{WorkflowFixtureSanitize(name)}.txt");
        await File.WriteAllTextAsync(path, content, Encoding.UTF8);
    }

    private static string WorkflowFixtureSanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
