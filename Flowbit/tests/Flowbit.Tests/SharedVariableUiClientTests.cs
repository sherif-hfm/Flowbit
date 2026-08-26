extern alias FlowbitUi;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using WorkflowApiException = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiException;
using Xunit;

namespace Flowbit.Tests;

public sealed class SharedVariableUiClientTests
{
    [Fact]
    public async Task ListUsesServerPagingSearchStatusAndArchiveContract()
    {
        using var handler = new RecordingHandler(Response(new PagedResult<SharedVariableMetadataDto>([], 2, 25, 41)));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var result = await client.GetSharedVariablesAsync(new SharedVariableListRequest(
            "finance rate", SharedVariableStatuses.Archived, 2, 25, true));

        Assert.Equal(41, result.TotalCount);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal(
            "/api/shared-variables?page=2&pageSize=25&includeArchived=true&search=finance%20rate&status=archived",
            request.PathAndQuery);
    }

    [Fact]
    public async Task DottedSharedKeyIsAddressedAsOneEscapedPathSegment()
    {
        var variable = Value("finance.usd/spot", revision: 3);
        using var handler = new RecordingHandler(Response(variable), Response(variable));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var current = await client.GetSharedVariableValueAsync(variable.Key);
        await client.UpdateSharedVariableValueAsync(
            variable.Key,
            new UpdateSharedVariableValueRequest(JsonSerializer.SerializeToElement(3.75m), current!.Revision, "req-1", "market close"));

        Assert.Equal(3, current.Revision);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request => Assert.Equal("/api/shared-variables/finance.usd%2Fspot/value", request.PathAndQuery));
        Assert.Equal(HttpMethod.Get, handler.Requests[0].Method);
        var request = handler.Requests[1];
        Assert.Equal(HttpMethod.Put, request.Method);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal(3, body.RootElement.GetProperty("expectedRevision").GetInt64());
        Assert.Equal("req-1", body.RootElement.GetProperty("requestId").GetString());
    }

    [Fact]
    public async Task DescriptionEditUsesJwtAdminMetadataRouteWithoutAValueField()
    {
        var metadata = Metadata("finance.usdToSar", revision: 8);
        using var handler = new RecordingHandler(Response(metadata));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        await client.UpdateSharedVariableDescriptionAsync(
            metadata.Key,
            new UpdateSharedVariableDescriptionRequest("Closing rate", 7, "description-1", "clarify"));

        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal("/api/shared-variables/finance.usdToSar", request.PathAndQuery);
        using var body = JsonDocument.Parse(request.Body!);
        Assert.Equal("Closing rate", body.RootElement.GetProperty("description").GetString());
        Assert.False(body.RootElement.TryGetProperty("value", out _));
    }

    [Fact]
    public async Task ClientCreateEditAndRotatePreserveScopesConcurrencyAndGracePeriod()
    {
        var apiClient = new SharedVariableClientDto(
            7, "erp-prod", "ERP", [SharedVariableClientScopes.Read, SharedVariableClientScopes.Write],
            SharedVariableClientStatuses.Active, 4, null, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, null);
        using var handler = new RecordingHandler(
            Response(new CreateSharedVariableClientResult(apiClient, "create-secret"), HttpStatusCode.Created),
            Response(apiClient with { DisplayName = "ERP primary", Scopes = [SharedVariableClientScopes.Read], Revision = 5 }),
            Response(new RotateSharedVariableClientSecretResult(apiClient with { Revision = 6 }, "rotate-secret")));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var created = await client.CreateSharedVariableClientAsync(new CreateSharedVariableClientRequest(
            "erp-prod", "ERP", [SharedVariableClientScopes.Read, SharedVariableClientScopes.Write], null));
        var updated = await client.UpdateSharedVariableClientAsync(
            7,
            new UpdateSharedVariableClientRequest(
                "ERP primary", [SharedVariableClientScopes.Read], null, 4));
        var rotated = await client.RotateSharedVariableClientSecretAsync(
            7, new RotateSharedVariableClientSecretRequest(5, 48));

        Assert.Equal("create-secret", created.ClientSecret);
        Assert.Equal("ERP primary", updated.DisplayName);
        Assert.Equal("rotate-secret", rotated.ClientSecret);
        Assert.Collection(handler.Requests,
            request =>
            {
                Assert.Equal("/api/shared-variable-clients", request.PathAndQuery);
                using var body = JsonDocument.Parse(request.Body!);
                Assert.Equal("erp-prod", body.RootElement.GetProperty("clientId").GetString());
                Assert.Equal(2, body.RootElement.GetProperty("scopes").GetArrayLength());
            },
            request =>
            {
                Assert.Equal(HttpMethod.Put, request.Method);
                Assert.Equal("/api/shared-variable-clients/7", request.PathAndQuery);
                using var body = JsonDocument.Parse(request.Body!);
                Assert.Equal("ERP primary", body.RootElement.GetProperty("displayName").GetString());
                Assert.Equal(4, body.RootElement.GetProperty("expectedRevision").GetInt64());
                Assert.Single(body.RootElement.GetProperty("scopes").EnumerateArray());
            },
            request =>
            {
                Assert.Equal("/api/shared-variable-clients/7/rotate", request.PathAndQuery);
                using var body = JsonDocument.Parse(request.Body!);
                Assert.Equal(5, body.RootElement.GetProperty("expectedRevision").GetInt64());
                Assert.Equal(48, body.RootElement.GetProperty("gracePeriodHours").GetInt32());
            });
    }

    [Fact]
    public async Task SharedIncidentListRetryAndResolveUseOperatorRoutesAndReason()
    {
        var incident = new SharedVariableIncidentDto(
            17,
            "delivery",
            5,
            9,
            5,
            9,
            "examples.service.status",
            44,
            101,
            7,
            12,
            Guid.NewGuid(),
            3,
            "delivery_failed",
            "open",
            "Delivery failed",
            "test detail",
            null,
            null,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            null);
        using var handler = new RecordingHandler(
            Response(new PagedResult<SharedVariableIncidentDto>(
                [incident with { Details = null }],
                2,
                25,
                26)),
            Response(incident),
            Response(incident with
            {
                Status = "resolved",
                ResolvedBy = "retry-admin",
                Details = null
            }),
            Response(incident with
            {
                Status = "resolved",
                ResolvedBy = "admin",
                Details = null
            }));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var page = await client.GetSharedVariableIncidentsAsync("open", 2, 25);
        var detail = await client.GetSharedVariableIncidentAsync(17);
        var retried = await client.RetrySharedVariableIncidentAsync(17);
        var resolved = await client.ResolveSharedVariableIncidentAsync(17, "Recovered manually");

        Assert.Equal(26, page.TotalCount);
        Assert.Null(Assert.Single(page.Items).Details);
        Assert.Equal(17, detail?.Id);
        Assert.Equal("test detail", detail?.Details);
        Assert.Equal("retry-admin", retried.ResolvedBy);
        Assert.Null(retried.Details);
        Assert.Equal("admin", resolved.ResolvedBy);
        Assert.Null(resolved.Details);
        Assert.Collection(
            handler.Requests,
            request => Assert.Equal(
                "/api/shared-variable-incidents?page=2&pageSize=25&status=open",
                request.PathAndQuery),
            request =>
            {
                Assert.Equal(HttpMethod.Get, request.Method);
                Assert.Equal("/api/shared-variable-incidents/17", request.PathAndQuery);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/api/shared-variable-incidents/17/retry", request.PathAndQuery);
            },
            request =>
            {
                Assert.Equal(HttpMethod.Post, request.Method);
                Assert.Equal("/api/shared-variable-incidents/17/resolve", request.PathAndQuery);
                using var body = JsonDocument.Parse(request.Body!);
                Assert.Equal("Recovered manually", body.RootElement.GetProperty("reason").GetString());
            });
    }

    [Fact]
    public async Task SharedIncidentDetailReturnsNullForNotFound()
    {
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.NotFound));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var incident = await client.GetSharedVariableIncidentAsync(404);

        Assert.Null(incident);
        Assert.Equal("/api/shared-variable-incidents/404", Assert.Single(handler.Requests).PathAndQuery);
    }

    [Fact]
    public async Task SharedIncidentListPreservesStructuredApiErrors()
    {
        using var handler = new RecordingHandler(Response(
            new { error = "Unknown shared-variable incident status." },
            HttpStatusCode.BadRequest));
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var client = new WorkflowApiClient(http);

        var exception = await Assert.ThrowsAsync<WorkflowApiException>(() =>
            client.GetSharedVariableIncidentsAsync("unknown"));

        Assert.Equal(HttpStatusCode.BadRequest, exception.StatusCode);
        Assert.Equal("Unknown shared-variable incident status.", exception.Message);
    }

    private static SharedVariableValueDto Value(string key, long revision) => new(
        key, true, JsonSerializer.SerializeToElement(3.5m), revision, DateTimeOffset.UtcNow);

    private static SharedVariableMetadataDto Metadata(string key, long revision) => new(
        1, key, "number", false, false, null, null, true,
        SharedVariableStatuses.Active, revision, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null);

    private static HttpResponseMessage Response<T>(T body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(body) };

    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler, IDisposable
    {
        private readonly Queue<HttpResponseMessage> queue = new(responses);
        public List<RecordedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(new RecordedRequest(
                request.Method,
                request.RequestUri!.PathAndQuery,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return queue.Dequeue();
        }
    }

    private sealed record RecordedRequest(HttpMethod Method, string PathAndQuery, string? Body);
}
