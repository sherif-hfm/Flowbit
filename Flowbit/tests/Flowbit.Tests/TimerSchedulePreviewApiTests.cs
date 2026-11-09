using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class TimerSchedulePreviewApiTests(PostgresApiFixture fixture)
{
    private const string PreviewPath = "/api/workflows/timer-schedules/preview";
    private const string TimeZonesPath = "/api/workflows/timer-schedules/time-zones";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task PreviewListsTheNextOccurrencesInTheScheduleZoneAndUtc()
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            PreviewPath,
            new TimerSchedulePreviewRequest(" 0 8 * * 1-5 ", " Europe/London ", "2026-11-08T12:00:00Z", 5));

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;
        Assert.Equal("0 8 * * 1-5", root.GetProperty("cron").GetString());
        Assert.Equal("Europe/London", root.GetProperty("timeZone").GetString());
        Assert.Equal("2026-11-08T12:00:00+00:00", root.GetProperty("fromUtc").GetString());
        Assert.Equal(
            ["2026-11-09", "2026-11-10", "2026-11-11", "2026-11-12", "2026-11-13"],
            root.GetProperty("occurrences").EnumerateArray().Select(row => row.GetProperty("dueAt").GetString()![..10]));
        var first = root.GetProperty("occurrences")[0];
        Assert.Equal("2026-11-09T08:00:00+00:00", first.GetProperty("dueAt").GetString());
        Assert.Equal("2026-11-09T08:00:00+00:00", first.GetProperty("localTime").GetString());
    }

    [Fact]
    public async Task PreviewShowsRepeatedHourOffsetsAndAcceptsAZeroOffsetReference()
    {
        var preview = await PreviewAsync(new TimerSchedulePreviewRequest("*/30 1 * * *", "Europe/London", "2026-10-24T23:45:00+00:00", 4));

        Assert.Equal(
            [
                ("2026-10-25T00:00:00+00:00", "2026-10-25T01:00:00+01:00"),
                ("2026-10-25T00:30:00+00:00", "2026-10-25T01:30:00+01:00"),
                ("2026-10-25T01:00:00+00:00", "2026-10-25T01:00:00+00:00"),
                ("2026-10-25T01:30:00+00:00", "2026-10-25T01:30:00+00:00")
            ],
            preview.Occurrences.Select(row => (Format(row.DueAt), Format(row.LocalTime))));
        Assert.Equal(TimeSpan.Zero, preview.FromUtc.Offset);
    }

    [Fact]
    public async Task PreviewDefaultsToFiveOccurrencesAfterTheServersClock()
    {
        var before = DateTimeOffset.UtcNow;
        var preview = await PreviewAsync(new TimerSchedulePreviewRequest("* * * * *", "Etc/UTC"));
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(preview.FromUtc, before.AddSeconds(-1), after.AddSeconds(1));
        Assert.Equal(5, preview.Occurrences.Count);
        Assert.True(preview.Occurrences[0].DueAt > preview.FromUtc);
        Assert.True(preview.Occurrences.Zip(preview.Occurrences.Skip(1)).All(pair => pair.First.DueAt < pair.Second.DueAt));
    }

    [Theory]
    [InlineData("0 8 * * 7", "Europe/London", null, null, "cron day-of-week value 7 is not allowed; write Sunday as 0.")]
    [InlineData("0 8 * * 1/2", "Europe/London", null, null, "cron day-of-week item '1/2' has an open-ended step; write */n or a range such as 1-5/2.")]
    [InlineData("5/- 8 * * *", "Europe/London", null, null, "cron minute item '5/-' is malformed; write *, */n, a, a-b, a/n, or a-b/n.")]
    [InlineData("0 8 * * *", "Mars/Olympus", null, null, "timeZone 'Mars/Olympus' is not an IANA time zone that this server knows, such as Europe/London.")]
    [InlineData("0 8 * * *", null, null, null, "timeZone is required with cron; name an IANA time zone such as Europe/London, or Etc/UTC for UTC.")]
    [InlineData("0 8 * * *", "Etc/UTC", "2026-11-08T12:00:00", null, "fromUtc '2026-11-08T12:00:00' must be a UTC date and time ending in Z or +00:00, such as 2026-11-08T12:00:00Z.")]
    [InlineData("0 8 * * *", "Etc/UTC", "2026-11-08T12:00:00+02:00", null, "fromUtc '2026-11-08T12:00:00+02:00' must be a UTC date and time ending in Z or +00:00, such as 2026-11-08T12:00:00Z.")]
    [InlineData("0 8 * * *", "Etc/UTC", "2026-11-08", null, "fromUtc '2026-11-08' must be a UTC date and time ending in Z or +00:00, such as 2026-11-08T12:00:00Z.")]
    [InlineData("0 8 * * *", "Etc/UTC", null, 0, "count must be from 1 to 10.")]
    [InlineData("0 8 * * *", "Etc/UTC", null, 11, "count must be from 1 to 10.")]
    [InlineData("0 8 31 12 *", "Etc/UTC", "9998-12-31T09:00:00Z", 2, "cron '0 8 31 12 *' in time zone 'Etc/UTC' gives only 1 of the 2 requested occurrences after 9998-12-31T09:00:00Z; each must fall within 36,525 days of the one before.")]
    public async Task PreviewRejectsAnInvalidRequestWithItsCause(
        string cron,
        string? timeZone,
        string? fromUtc,
        int? count,
        string expected)
    {
        using var response = await SendAsync(HttpMethod.Post, PreviewPath, new TimerSchedulePreviewRequest(cron, timeZone, fromUtc, count));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(expected, document.RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public async Task PreviewRejectsAZoneWithOtherCasing()
    {
        using var response = await SendAsync(HttpMethod.Post, PreviewPath, new TimerSchedulePreviewRequest("0 8 * * *", "europe/london"));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Contains(document.RootElement.GetProperty("error").GetString(), CronTimerDefinitionTests.MiscasedZoneMessages("timeZone"));
    }

    [Fact]
    public async Task PreviewRejectsAReferenceThatIsNotText()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, PreviewPath)
        {
            Content = new StringContent("""{"cron":"0 8 * * *","timeZone":"Etc/UTC","fromUtc":1762603200}""", System.Text.Encoding.UTF8, "application/json")
        };
        ApiTestAuth.Authorize(request, "cron-author");
        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("fromUtc", await response.Content.ReadAsStringAsync());
    }

    [Theory]
    [InlineData("POST", PreviewPath)]
    [InlineData("GET", TimeZonesPath)]
    public async Task TheRoutesNeedWorkflowAuthorityRatherThanSettingsAuthority(string method, string path)
    {
        object? body = method == "POST" ? new TimerSchedulePreviewRequest("0 8 * * *", "Etc/UTC", "2026-11-08T12:00:00Z") : null;
        using (var anonymous = await fixture.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)
               {
                   Content = body is null ? null : JsonContent.Create(body, options: JsonOptions)
               }))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        var previous = await SetWorkflowRoleAsync("cron-author");
        try
        {
            using (var author = await SendAsync(new HttpMethod(method), path, body, "author", ["cron-author"]))
            {
                Assert.Equal(HttpStatusCode.OK, author.StatusCode);
            }

            // Settings administrators manage business calendars, not workflow authoring.
            using var settingsAdmin = await SendAsync(new HttpMethod(method), path, body, "settings-admin", ["admin"]);
            Assert.Equal(HttpStatusCode.Forbidden, settingsAdmin.StatusCode);
        }
        finally
        {
            await SetWorkflowRoleAsync(previous);
        }
    }

    [Fact]
    public async Task TimeZonesListsTheHostsIanaZonesInOrdinalOrder()
    {
        using var response = await SendAsync(HttpMethod.Get, TimeZonesPath);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var zones = (await response.Content.ReadFromJsonAsync<List<string>>(JsonOptions))!;
        Assert.Contains("Etc/UTC", zones);
        Assert.Contains("Europe/London", zones);
        Assert.Equal(zones.Order(StringComparer.Ordinal), zones);
        Assert.Equal(zones.Count, zones.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task PreviewWritesNothingAndWorksWhileDurablePublicationIsDisabled()
    {
        var before = await CountScheduleRowsAsync();
        await using var closed = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DurableProcessingOptions>();
            services.AddSingleton(new DurableProcessingOptions { PublicationEnabled = false });
        }));
        using var client = closed.CreateClient();
        var request = new HttpRequestMessage(HttpMethod.Post, PreviewPath)
        {
            Content = JsonContent.Create(new TimerSchedulePreviewRequest("0 9 1 * *", "Asia/Tokyo", "2026-11-08T12:00:00Z", 3), options: JsonOptions)
        };
        ApiTestAuth.Authorize(request, "cron-author");

        using var response = await client.SendAsync(request);

        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        var preview = (await response.Content.ReadFromJsonAsync<TimerSchedulePreviewDto>(JsonOptions))!;
        Assert.Equal(
            ["2026-12-01T00:00:00+00:00", "2027-01-01T00:00:00+00:00", "2027-02-01T00:00:00+00:00"],
            preview.Occurrences.Select(row => Format(row.DueAt)));
        Assert.All(preview.Occurrences, row => Assert.Equal(TimeSpan.FromHours(9), row.LocalTime.Offset));
        Assert.Equal(before, await CountScheduleRowsAsync());
    }

    private async Task<TimerSchedulePreviewDto> PreviewAsync(TimerSchedulePreviewRequest request)
    {
        using var response = await SendAsync(HttpMethod.Post, PreviewPath, request);
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<TimerSchedulePreviewDto>(JsonOptions))!;
    }

    private async Task<(int Subscriptions, int Jobs, int Definitions)> CountScheduleRowsAsync()
    {
        await using var db = fixture.CreateDbContext();
        return (await db.TimerSubscriptions.CountAsync(), await db.WorkflowJobs.CountAsync(), await db.WorkflowDefinitions.CountAsync());
    }

    private async Task<string?> SetWorkflowRoleAsync(string? value)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var settings = scope.ServiceProvider.GetRequiredService<IEngineSettingsRepository>();
        var previous = await settings.GetByKeyAsync(WorkflowAdministratorPolicy.RequiredRoleSettingKey, CancellationToken.None);
        if (value is null)
        {
            await settings.DeleteAsync(WorkflowAdministratorPolicy.RequiredRoleSettingKey, "settings-test", CancellationToken.None);
        }
        else
        {
            await settings.SetAsync(WorkflowAdministratorPolicy.RequiredRoleSettingKey, value, "settings-test", CancellationToken.None);
        }

        return previous?.Value;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "cron-author",
        string[]? roles = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        ApiTestAuth.Authorize(request, user, roles ?? []);
        if (roles is not null)
        {
            request.Headers.TryAddWithoutValidation("X-Test-Suppress-Admin", "true");
        }

        return await fixture.Client.SendAsync(request);
    }

    private static string Format(DateTimeOffset value) => value.ToString("yyyy-MM-dd'T'HH:mm:sszzz", System.Globalization.CultureInfo.InvariantCulture);
}
