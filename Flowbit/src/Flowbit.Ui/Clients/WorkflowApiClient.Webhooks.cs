using System.Globalization;
using System.Net.Http.Json;
using Flowbit.Shared.Dtos;

namespace Flowbit.Ui.Clients;

/// <summary>Delivery-list filters the Webhooks page sends; empty values apply no filter.</summary>
public sealed record WebhookDeliverySearch(
    Guid? SubscriptionId = null,
    Guid? EventId = null,
    string? EventType = null,
    string? WorkflowKey = null,
    long? InstanceId = null,
    long? RevisionId = null,
    string? State = null,
    string? FailureCode = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    int Page = 1,
    int PageSize = 50);

/// <summary>Settings administrators' outbound lifecycle webhooks: subscriptions, one-time secrets, and delivery recovery.</summary>
public sealed partial class WorkflowApiClient
{
    public async Task<WebhookEventCatalogDto> GetWebhookEventCatalogAsync(CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync("/api/webhook-event-types", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<WebhookEventCatalogDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty event catalog.");
    }

    public async Task<PagedResult<WebhookSubscriptionDto>> GetWebhookSubscriptionsAsync(
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"/api/webhook-subscriptions?page={Math.Max(1, page)}&pageSize={Math.Clamp(pageSize, 1, 200)}",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PagedResult<WebhookSubscriptionDto>>(cancellationToken)
            ?? new PagedResult<WebhookSubscriptionDto>([], page, pageSize, 0);
    }

    public async Task<WebhookSubscriptionDetailDto?> GetWebhookSubscriptionAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync($"/api/webhook-subscriptions/{id:D}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<WebhookSubscriptionDetailDto>(cancellationToken);
    }

    public Task<WebhookSecretResultDto> CreateWebhookSubscriptionAsync(
        CreateWebhookSubscriptionRequest request,
        CancellationToken cancellationToken = default) =>
        PostWebhookAsync<WebhookSecretResultDto>("/api/webhook-subscriptions", request, cancellationToken);

    public async Task<WebhookSubscriptionDto> UpdateWebhookSubscriptionAsync(
        Guid id,
        UpdateWebhookSubscriptionRequest request,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PutAsJsonAsync($"/api/webhook-subscriptions/{id:D}", request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<WebhookSubscriptionDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty subscription.");
    }

    public async Task<WebhookSubscriptionDto> PauseWebhookSubscriptionAsync(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsync($"/api/webhook-subscriptions/{id:D}/pause", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<WebhookSubscriptionDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty subscription.");
    }

    public Task<WebhookSubscriptionDto> ResumeWebhookSubscriptionAsync(
        Guid id,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        PostWebhookAsync<WebhookSubscriptionDto>(
            $"/api/webhook-subscriptions/{id:D}/resume",
            new WebhookRevisionRequest(expectedRevision),
            cancellationToken);

    public async Task<WebhookTestResultDto> SendWebhookTestAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsync($"/api/webhook-subscriptions/{id:D}/test", null, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<WebhookTestResultDto>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty test result.");
    }

    public Task<WebhookSecretResultDto> RotateWebhookSecretAsync(
        Guid id,
        RotateWebhookSecretRequest request,
        CancellationToken cancellationToken = default) =>
        PostWebhookAsync<WebhookSecretResultDto>($"/api/webhook-subscriptions/{id:D}/rotate-secret", request, cancellationToken);

    public Task<WebhookSubscriptionDto> RevokePreviousWebhookSecretAsync(
        Guid id,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        PostWebhookAsync<WebhookSubscriptionDto>(
            $"/api/webhook-subscriptions/{id:D}/revoke-previous-secret",
            new WebhookRevisionRequest(expectedRevision),
            cancellationToken);

    public Task<WebhookSubscriptionDto> ArchiveWebhookSubscriptionAsync(
        Guid id,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        PostWebhookAsync<WebhookSubscriptionDto>(
            $"/api/webhook-subscriptions/{id:D}/archive",
            new WebhookRevisionRequest(expectedRevision),
            cancellationToken);

    public Task<WebhookBulkDeliveryResultDto> BulkRetryWebhookDeliveriesAsync(
        Guid subscriptionId,
        WebhookBulkDeliveryRequest request,
        CancellationToken cancellationToken = default) =>
        PostWebhookAsync<WebhookBulkDeliveryResultDto>(
            $"/api/webhook-subscriptions/{subscriptionId:D}/deliveries/retry",
            InUtc(request),
            cancellationToken);

    public Task<WebhookBulkDeliveryResultDto> BulkDiscardWebhookDeliveriesAsync(
        Guid subscriptionId,
        WebhookBulkDeliveryRequest request,
        CancellationToken cancellationToken = default) =>
        PostWebhookAsync<WebhookBulkDeliveryResultDto>(
            $"/api/webhook-subscriptions/{subscriptionId:D}/deliveries/discard",
            InUtc(request),
            cancellationToken);

    public async Task<PagedResult<WebhookDeliveryDto>> GetWebhookDeliveriesAsync(
        WebhookDeliverySearch search,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(search);
        var query = new List<string>
        {
            $"page={Math.Max(1, search.Page).ToString(CultureInfo.InvariantCulture)}",
            $"pageSize={Math.Clamp(search.PageSize, 1, 200).ToString(CultureInfo.InvariantCulture)}"
        };
        AddQuery(query, "subscriptionId", search.SubscriptionId?.ToString("D"));
        AddQuery(query, "eventId", search.EventId?.ToString("D"));
        AddQuery(query, "eventType", search.EventType);
        AddQuery(query, "workflowKey", search.WorkflowKey);
        AddQuery(query, "instanceId", search.InstanceId?.ToString(CultureInfo.InvariantCulture));
        AddQuery(query, "revisionId", search.RevisionId?.ToString(CultureInfo.InvariantCulture));
        AddQuery(query, "state", search.State);
        AddQuery(query, "failureCode", search.FailureCode);
        AddQuery(query, "from", search.From?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        AddQuery(query, "to", search.To?.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
        using var response = await httpClient.GetAsync($"/api/webhook-deliveries?{string.Join('&', query)}", cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PagedResult<WebhookDeliveryDto>>(cancellationToken)
            ?? new PagedResult<WebhookDeliveryDto>([], search.Page, search.PageSize, 0);
    }

    public async Task<WebhookDeliveryDetailDto?> GetWebhookDeliveryAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync($"/api/webhook-deliveries/{id:D}", cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<WebhookDeliveryDetailDto>(cancellationToken);
    }

    public async Task<PagedResult<WebhookDeliveryAttemptDto>> GetWebhookDeliveryAttemptsAsync(
        Guid id,
        int page = 1,
        int pageSize = 50,
        CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.GetAsync(
            $"/api/webhook-deliveries/{id:D}/attempts?page={Math.Max(1, page)}&pageSize={Math.Clamp(pageSize, 1, 200)}",
            cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PagedResult<WebhookDeliveryAttemptDto>>(cancellationToken)
            ?? new PagedResult<WebhookDeliveryAttemptDto>([], page, pageSize, 0);
    }

    public Task<WebhookDeliveryActionResultDto> RetryWebhookDeliveryAsync(
        Guid id,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        PostWebhookAsync<WebhookDeliveryActionResultDto>(
            $"/api/webhook-deliveries/{id:D}/retry",
            new WebhookRevisionRequest(expectedRevision),
            cancellationToken);

    public Task<WebhookDeliveryActionResultDto> DiscardWebhookDeliveryAsync(
        Guid id,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        PostWebhookAsync<WebhookDeliveryActionResultDto>(
            $"/api/webhook-deliveries/{id:D}/discard",
            new WebhookRevisionRequest(expectedRevision),
            cancellationToken);

    private async Task<T> PostWebhookAsync<T>(string path, object request, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsJsonAsync(path, request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken)
            ?? throw new InvalidOperationException("The API returned an empty webhook response.");
    }

    // Occurrence bounds travel as UTC instants, whatever offset the caller's values carry.
    private static WebhookBulkDeliveryRequest InUtc(WebhookBulkDeliveryRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return request with { From = request.From?.ToUniversalTime(), To = request.To?.ToUniversalTime() };
    }

    private static void AddQuery(List<string> query, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            query.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }
}
