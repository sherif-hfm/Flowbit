using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace Flowbit.Tests;

/// <summary>The API's request log: an error the API maps is logged with the status the client receives, and only a failure nothing maps is a 500.</summary>
[Collection(PostgresApiCollection.Name)]
public sealed class ApiRequestLoggingTests(PostgresApiFixture fixture)
{
    private const string FailurePath = "/request-log-failures";
    private const string RequestLogSource = "Serilog.AspNetCore.RequestLoggingMiddleware";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task EachMappedErrorIsLoggedWithTheStatusItReturns()
    {
        var logs = new CapturingSink();
        await using var factory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<ILogEventSink>(logs);
            services.AddSingleton<IStartupFilter, FailAtPipelineEnd>();
        }));
        using var client = factory.CreateClient();

        foreach (var (failure, status) in new (string, HttpStatusCode)[]
                 {
                     ("unreadable", HttpStatusCode.RequestEntityTooLarge),
                     ("domain", HttpStatusCode.BadRequest),
                     ("business-key", HttpStatusCode.Conflict),
                     ("message-delivery", HttpStatusCode.Conflict),
                     ("conflict", HttpStatusCode.Conflict),
                     ("forbidden", HttpStatusCode.Forbidden),
                     ("unauthorized", HttpStatusCode.Unauthorized),
                     ("key-protection", HttpStatusCode.ServiceUnavailable)
                 })
        {
            var path = $"{FailurePath}/{failure}";
            var before = logs.Events.Count;
            using (var response = await SendAsync(client, HttpMethod.Post, path))
                Assert.True(status == response.StatusCode, $"{path}: {(int)response.StatusCode}");

            var logged = logs.Events.Skip(before).ToList();
            var entry = Assert.Single(RequestLog(logged, path));
            Assert.Equal((int)status, StatusCode(entry));
            Assert.Null(entry.Exception);
            // Only the unavailable key ring is a server error; the rest are the client's.
            if ((int)status >= 500)
            {
                Assert.Equal(LogEventLevel.Error, entry.Level);
            }
            else
            {
                Assert.Equal(LogEventLevel.Information, entry.Level);
                Assert.DoesNotContain(logged, item => item.Level >= LogEventLevel.Error);
            }
        }

        // A failure nothing maps is still a 500, logged as an error with its exception.
        var unmapped = $"{FailurePath}/unmapped";
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(() => SendAsync(client, HttpMethod.Post, unmapped));
        var failed = Assert.Single(RequestLog(logs.Events, unmapped));
        Assert.Equal(500, StatusCode(failed));
        Assert.Equal(LogEventLevel.Error, failed.Level);
        Assert.Same(thrown, failed.Exception);
    }

    [Fact]
    public async Task RoutesLogTheErrorsTheyReturnWithTheirStatus()
    {
        var logs = new CapturingSink();
        await using var factory = fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILogEventSink>(logs)));
        using var client = factory.CreateClient();
        var create = new CreateEngineSettingRequest("Tests", $"RequestLog{Guid.NewGuid():N}", "logged");

        EngineSettingDto setting;
        using (var created = await SendAsync(client, HttpMethod.Post, "/api/engine-settings", create))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            setting = (await created.Content.ReadFromJsonAsync<EngineSettingDto>(JsonOptions))!;
        }
        try
        {
            // The same key again is a WorkflowConflictException, a body that is not JSON a
            // BadHttpRequestException, and a filter the route does not take a WorkflowDomainException.
            using (var duplicate = await SendAsync(client, HttpMethod.Post, "/api/engine-settings", create))
                Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
            using (var unreadable = await SendAsync(client, HttpMethod.Post, "/api/engine-settings", json: "{"))
                Assert.Equal(HttpStatusCode.BadRequest, unreadable.StatusCode);
            using (var unsupported = await SendAsync(client, HttpMethod.Get, "/api/webhook-deliveries?unknown=1"))
                Assert.Equal(HttpStatusCode.BadRequest, unsupported.StatusCode);
        }
        finally
        {
            var expected = Uri.EscapeDataString(setting.UpdatedAt.ToString("O", CultureInfo.InvariantCulture));
            using var deleted = await SendAsync(client, HttpMethod.Delete, $"/api/engine-settings/{setting.Id}?expectedUpdatedAt={expected}");
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        var settings = RequestLog(logs.Events, "/api/engine-settings").ToList();
        Assert.Equal(new[] { 201, 409, 400 }, settings.Select(StatusCode));
        var deliveries = Assert.Single(RequestLog(logs.Events, "/api/webhook-deliveries"));
        Assert.Equal(400, StatusCode(deliveries));
        Assert.All(settings.Append(deliveries), entry => Assert.Equal(LogEventLevel.Information, entry.Level));
        Assert.DoesNotContain(logs.Events, item => item.Level >= LogEventLevel.Error);
    }

    private static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpMethod method,
        string path,
        object? body = null,
        string? json = null)
    {
        using var request = ApiTestAuth.Authorize(new HttpRequestMessage(method, path));
        if (json is not null)
            request.Content = new StringContent(json, Encoding.UTF8, "application/json");
        else if (body is not null)
            request.Content = JsonContent.Create(body, options: JsonOptions);
        return await client.SendAsync(request);
    }

    // The request log writes one entry per request, after the response.
    private static IEnumerable<LogEvent> RequestLog(IEnumerable<LogEvent> events, string path) =>
        events.Where(item => Scalar(item, "SourceContext") as string == RequestLogSource
            && Scalar(item, "RequestPath") as string == path);

    private static int StatusCode(LogEvent entry) => Assert.IsType<int>(Scalar(entry, "StatusCode"));

    private static object? Scalar(LogEvent logEvent, string property) =>
        logEvent.Properties.TryGetValue(property, out var value) && value is ScalarValue scalar
            ? scalar.Value
            : null;

    // No endpoint matches FailurePath, so each request passes every middleware Program registers
    // and fails here, after them, where an endpoint would fail.
    private sealed class FailAtPipelineEnd : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            next(app);
            app.Run(context => throw Failure(context.Request.Path.Value![(FailurePath.Length + 1)..]));
        };

        private static Exception Failure(string name) => name switch
        {
            "unreadable" => new BadHttpRequestException("The request body is too large.", StatusCodes.Status413PayloadTooLarge),
            "domain" => new WorkflowDomainException("The workflow is not published."),
            "business-key" => new BusinessKeyConflictException(41),
            "message-delivery" => new MessageDeliveryConflictException(
                "message_wait_conflict", 42, 3, "Another request consumed this message wait activation."),
            "conflict" => new WorkflowConflictException("The task is no longer active."),
            "forbidden" => new WorkflowForbiddenException("The actor lacks the required role."),
            "unauthorized" => new WorkflowUnauthorizedException("The client credentials do not match."),
            "key-protection" => new WebhookKeyProtectionUnavailableException("The webhook key ring cannot be read."),
            _ => new InvalidOperationException("Nothing maps this failure.")
        };
    }

    // Receives the API host's Serilog events, which it reads from its services.
    private sealed class CapturingSink : ILogEventSink
    {
        private readonly ConcurrentQueue<LogEvent> events = new();

        public IReadOnlyCollection<LogEvent> Events => events.ToArray();

        public void Emit(LogEvent logEvent) => events.Enqueue(logEvent);
    }
}
