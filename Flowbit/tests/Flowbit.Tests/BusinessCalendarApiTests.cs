using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class BusinessCalendarApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("GET", "/api/business-calendars")]
    [InlineData("GET", "/api/business-calendars/time-zones")]
    [InlineData("POST", "/api/business-calendars/preview")]
    public async Task CalendarManagementNeedsASettingsAdministrator(string method, string path)
    {
        using (var anonymous = await fixture.Client.SendAsync(new HttpRequestMessage(new HttpMethod(method), path)))
        {
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        }

        using var viewer = await SendAsync(
            new HttpMethod(method),
            path,
            method == "POST" ? Preview("Etc/UTC", "2026-11-02T10:00", "PT1H") : null,
            user: "calendar-viewer",
            roles: ["viewer"],
            suppressAdmin: true);
        Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);
    }

    [Fact]
    public async Task CalendarCrudNormalizesContentAndUsesOptimisticConcurrency()
    {
        var name = $"api-calendar-{Guid.NewGuid():N}";
        var lunch = Periods("09:00-12:30", "13:30-17:30");
        var request = new CreateBusinessCalendarRequest(
            $"  {name}  ",
            "Europe/London",
            new BusinessCalendarWeekDto(lunch, lunch, lunch, lunch, Periods("09:00-13:00")),
            [
                new BusinessCalendarHolidayDto(new DateOnly(2026, 12, 25), " Christmas Day "),
                new BusinessCalendarHolidayDto(new DateOnly(2026, 12, 24), "Christmas Eve", Periods("09:00-12:00"))
            ],
            "  London office  ");

        BusinessCalendarDto created;
        using (var response = await SendAsync(HttpMethod.Post, "/api/business-calendars", request))
        {
            Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            created = await ReadAsync<BusinessCalendarDto>(response);
            Assert.Equal($"/api/business-calendars/{created.Id}", response.Headers.Location?.OriginalString);
        }

        Assert.Equal(name, created.Name);
        Assert.Equal("London office", created.Description);
        Assert.Equal("Europe/London", created.TimeZone);
        Assert.Equal(["09:00-12:30", "13:30-17:30"], Text(created.WeeklyHours.Monday));
        Assert.Equal(["09:00-13:00"], Text(created.WeeklyHours.Friday));
        Assert.Empty(created.WeeklyHours.Saturday!);
        Assert.Empty(created.WeeklyHours.Sunday!);
        Assert.Equal([new DateOnly(2026, 12, 24), new DateOnly(2026, 12, 25)], created.Holidays.Select(day => day.Date));
        Assert.Equal("Christmas Day", created.Holidays[1].Name);
        Assert.Empty(created.Holidays[1].WorkingHours!);
        Assert.Equal(created.CreatedAt, created.UpdatedAt);

        using (var response = await SendAsync(HttpMethod.Get, "/api/business-calendars"))
        {
            var listed = Assert.Single(
                await ReadAsync<List<BusinessCalendarDto>>(response),
                calendar => calendar.Id == created.Id);
            Assert.Equal(JsonSerializer.Serialize(created, JsonOptions), JsonSerializer.Serialize(listed, JsonOptions));
        }

        BusinessCalendarDto updated;
        using (var response = await SendAsync(
                   HttpMethod.Put,
                   $"/api/business-calendars/{created.Id}",
                   new Dictionary<string, object?>
                   {
                       ["name"] = "attempted-rename",
                       ["timeZone"] = "America/New_York",
                       ["weeklyHours"] = new BusinessCalendarWeekDto(Monday: Periods("08:00-16:00")),
                       ["holidays"] = null,
                       ["description"] = " ",
                       ["expectedUpdatedAt"] = created.UpdatedAt
                   }))
        {
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            updated = await ReadAsync<BusinessCalendarDto>(response);
        }

        Assert.Equal(name, updated.Name);
        Assert.Null(updated.Description);
        Assert.Equal("America/New_York", updated.TimeZone);
        Assert.Equal(["08:00-16:00"], Text(updated.WeeklyHours.Monday));
        Assert.Empty(updated.WeeklyHours.Tuesday!);
        Assert.Empty(updated.Holidays);
        Assert.True(updated.UpdatedAt > created.UpdatedAt);

        using (var stale = await SendAsync(
                   HttpMethod.Put,
                   $"/api/business-calendars/{created.Id}",
                   new UpdateBusinessCalendarRequest(
                       "Etc/UTC", new BusinessCalendarWeekDto(Monday: Periods("09:00-17:00")), null, null, created.UpdatedAt)))
        {
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Contains("was changed by another request", await stale.Content.ReadAsStringAsync());
        }

        using (var staleDelete = await DeleteAsync(created.Id, created.UpdatedAt))
        {
            Assert.Equal(HttpStatusCode.Conflict, staleDelete.StatusCode);
        }

        using (var deleted = await DeleteAsync(created.Id, updated.UpdatedAt))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using (var missing = await DeleteAsync(created.Id, updated.UpdatedAt))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        using var missingUpdate = await SendAsync(
            HttpMethod.Put,
            $"/api/business-calendars/{created.Id}",
            new UpdateBusinessCalendarRequest(
                "Etc/UTC", new BusinessCalendarWeekDto(Monday: Periods("09:00-17:00")), null, null, updated.UpdatedAt));
        Assert.Equal(HttpStatusCode.NotFound, missingUpdate.StatusCode);
    }

    [Fact]
    public async Task NamesAreUniqueIgnoringCase()
    {
        var name = $"api-duplicate-{Guid.NewGuid():N}";
        var created = await CreateCalendarAsync(name);
        try
        {
            using var duplicate = await SendAsync(HttpMethod.Post, "/api/business-calendars", Weekdays(name.ToUpperInvariant()));
            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
            Assert.Contains("already exists", await duplicate.Content.ReadAsStringAsync());
        }
        finally
        {
            await DeleteQuietlyAsync(created);
        }
    }

    [Theory]
    [InlineData("""{ "name": "has space", "timeZone": "Etc/UTC", "weeklyHours": { "monday": [{ "start": "09:00", "end": "17:00" }] } }""", "is not a valid business calendar name")]
    [InlineData("""{ "name": "unknown-zone", "timeZone": "Nowhere/Land", "weeklyHours": { "monday": [{ "start": "09:00", "end": "17:00" }] } }""", "is not an IANA time zone")]
    [InlineData("""{ "name": "windows-zone", "timeZone": "GMT Standard Time", "weeklyHours": { "monday": [{ "start": "09:00", "end": "17:00" }] } }""", "is not an IANA time zone")]
    [InlineData("""{ "name": "bad-time", "timeZone": "Etc/UTC", "weeklyHours": { "monday": [{ "start": "9:00", "end": "17:00" }] } }""", "weeklyHours.monday[0].start '9:00' must be a time in HH:mm form.")]
    [InlineData("""{ "name": "backwards", "timeZone": "Etc/UTC", "weeklyHours": { "monday": [{ "start": "17:00", "end": "09:00" }] } }""", "weeklyHours.monday[0] must end after it starts.")]
    [InlineData("""{ "name": "overlap", "timeZone": "Etc/UTC", "weeklyHours": { "monday": [{ "start": "09:00", "end": "13:00" }, { "start": "12:00", "end": "17:00" }] } }""", "weeklyHours.monday[1] starts at 12:00, before the previous period ends at 13:00.")]
    [InlineData("""{ "name": "closed", "timeZone": "Etc/UTC", "weeklyHours": { } }""", "weeklyHours must have at least one working period.")]
    [InlineData("""{ "name": "twice", "timeZone": "Etc/UTC", "weeklyHours": { "monday": [{ "start": "09:00", "end": "17:00" }] }, "holidays": [{ "date": "2026-12-25" }, { "date": "2026-12-25" }] }""", "holidays lists 2026-12-25 more than once.")]
    public async Task InvalidCalendarsAreRejected(string body, string error)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/business-calendars")
        {
            Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json")
        };
        ApiTestAuth.Authorize(request, "settings-admin", []);
        using var response = await fixture.Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(error, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    // Another script's digits used to escape as a FormatException and a server error.
    [Fact]
    public async Task OtherDigitsInHoursAndDurationsAreValidationErrors()
    {
        var name = $"api-digits-{Guid.NewGuid():N}";
        var hours = new BusinessCalendarWeekDto(Periods("09:00-17:0١"));
        await AssertErrorAsync(
            HttpMethod.Post,
            "/api/business-calendars",
            new CreateBusinessCalendarRequest(name, "Etc/UTC", hours),
            "weeklyHours.monday[0].end '17:0١' must be a time in HH:mm form, or 24:00.");

        var calendar = await CreateCalendarAsync(name);
        await AssertErrorAsync(
            HttpMethod.Put,
            $"/api/business-calendars/{calendar.Id}",
            new UpdateBusinessCalendarRequest("Etc/UTC", hours, null, null, calendar.UpdatedAt),
            "weeklyHours.monday[0].end '17:0١' must be a time in HH:mm form, or 24:00.");
        await AssertErrorAsync(
            HttpMethod.Post,
            "/api/business-calendars/preview",
            new BusinessCalendarPreviewRequest("Etc/UTC", hours, null, "2026-11-02T10:00", "P1D"),
            "weeklyHours.monday[0].end '17:0١' must be a time in HH:mm form, or 24:00.");
        await AssertErrorAsync(
            HttpMethod.Post,
            "/api/business-calendars/preview",
            Preview("Etc/UTC", "2026-11-02T10:00", "P١D"),
            "duration 'P١D' cannot be counted in a business calendar; use whole working days and working time, such as P5D or P1DT4H.");
        await AssertErrorAsync(
            HttpMethod.Post,
            "/api/business-calendars/preview",
            Preview("Etc/UTC", "2026-11-02T10:00", "P36526D"),
            "duration 'P36526D' is longer than a business calendar can count; use at most 36,525 days, about 100 years.");

        await DeleteQuietlyAsync(calendar);
    }

    [Fact]
    public async Task PreviewCountsWorkingTimeAcrossTheLondonDaylightSavingChange()
    {
        using (var response = await SendAsync(
                   HttpMethod.Post,
                   "/api/business-calendars/preview",
                   Preview("Europe/London", "2026-03-27T14:00", "P1D")))
        {
            Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
            var preview = await ReadAsync<BusinessCalendarPreviewDto>(response);
            Assert.Equal(new DateTimeOffset(2026, 3, 27, 14, 0, 0, TimeSpan.Zero), preview.StartAt);
            Assert.Equal(new DateTimeOffset(2026, 3, 30, 13, 0, 0, TimeSpan.Zero), preview.DueAt);
            Assert.Equal(TimeSpan.FromHours(1), preview.DueAtLocal.Offset);
            Assert.Equal(new DateTime(2026, 3, 30, 14, 0, 0), preview.DueAtLocal.DateTime);
        }

        using (var weeks = await SendAsync(
                   HttpMethod.Post,
                   "/api/business-calendars/preview",
                   Preview("Europe/London", "2026-03-27T14:00", "P1W")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, weeks.StatusCode);
            Assert.Contains("cannot be counted in a business calendar", await weeks.Content.ReadAsStringAsync());
        }

        using var badStart = await SendAsync(
            HttpMethod.Post,
            "/api/business-calendars/preview",
            Preview("Europe/London", "27/03/2026", "P1D"));
        Assert.Equal(HttpStatusCode.BadRequest, badStart.StatusCode);
    }

    [Fact]
    public async Task TimeZonesListTheHostsIanaZonesInOrder()
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/business-calendars/time-zones");
        var zones = await ReadAsync<List<string>>(response);

        Assert.Contains("Europe/London", zones);
        Assert.Contains("Etc/UTC", zones);
        Assert.Equal(zones.Order(StringComparer.Ordinal), zones);
    }

    [Fact]
    public async Task ACalendarInUseCannotBeDeleted()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var calendar = await CreateCalendarAsync($"api-in-use-{suffix}");
        var model = TaskModel($"calendar-in-use-{suffix}", calendar.Name);
        var workflowId = await CreateWorkflowAsync(model, publish: true);

        await AssertDeleteConflictAsync(calendar, "1 published workflow definition(s) use it");

        // A default version cannot be unpublished, so a later version without the calendar becomes the default.
        var instanceId = await StartAsync(workflowId);
        model.FlowNodes.Single(node => node.Id == 2).DueDateCalendar = null;
        long laterId;
        using (var later = await SendAsync(HttpMethod.Put, $"/api/workflows/{workflowId}", new UpdateWorkflowRequest(model, true)))
        {
            Assert.True(later.StatusCode == HttpStatusCode.OK, await later.Content.ReadAsStringAsync());
            laterId = JsonNode.Parse(await later.Content.ReadAsStringAsync())!["id"]!.GetValue<long>();
        }

        foreach (var path in new[] { $"/api/workflows/{laterId}/set-default", $"/api/workflows/{workflowId}/unpublish" })
        {
            using var response = await SendAsync(HttpMethod.Post, path);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        }

        await AssertDeleteConflictAsync(calendar, "1 running workflow instance(s) use it");

        using (var cancel = await SendAsync(HttpMethod.Post, $"/api/instances/{instanceId}/cancel"))
        {
            Assert.Equal(HttpStatusCode.NoContent, cancel.StatusCode);
        }

        using var deleted = await DeleteAsync(calendar.Id, calendar.UpdatedAt);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
    }

    [Fact]
    public async Task ACalendarOnlyAnUnpublishedVersionUsesCanBeDeletedAndThatVersionThenCannotBePublished()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var calendar = await CreateCalendarAsync($"api-unpublished-{suffix}");
        var workflowId = await CreateWorkflowAsync(TaskModel($"calendar-unpublished-{suffix}", calendar.Name), publish: false);

        using (var deleted = await DeleteAsync(calendar.Id, calendar.UpdatedAt))
        {
            Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
        }

        using var publish = await SendAsync(HttpMethod.Post, $"/api/workflows/{workflowId}/publish");
        Assert.Equal(HttpStatusCode.BadRequest, publish.StatusCode);
        Assert.Contains($"names business calendar '{calendar.Name}', which does not exist", await publish.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task DefinitionsMustNameExistingCalendarsInTheirExactCasing()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var calendar = await CreateCalendarAsync($"api-casing-{suffix}");
        try
        {
            using (var missing = await SendAsync(
                       HttpMethod.Post,
                       "/api/workflows",
                       new CreateWorkflowRequest(TaskModel($"calendar-missing-{suffix}", $"missing-{suffix}"), true)))
            {
                Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
                Assert.Contains(
                    $"Flow node #2 names business calendar 'missing-{suffix}', which does not exist.",
                    await missing.Content.ReadAsStringAsync());
            }

            using var casing = await SendAsync(
                HttpMethod.Post,
                "/api/workflows",
                new CreateWorkflowRequest(TaskModel($"calendar-casing-{suffix}", calendar.Name.ToUpperInvariant()), true));
            Assert.Equal(HttpStatusCode.BadRequest, casing.StatusCode);
            Assert.Contains($"which must be written '{calendar.Name}'", await casing.Content.ReadAsStringAsync());
        }
        finally
        {
            await DeleteQuietlyAsync(calendar);
        }
    }

    internal static WorkflowModel TaskModel(string workflowKey, string calendar) => new()
    {
        Id = workflowKey,
        Name = workflowKey,
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Review",
                Type = BpmnFlowNodeTypes.UserTask,
                DueDateExpression = "PT10H",
                DueDateCalendar = calendar
            },
            new FlowNodeModel { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, Name = "Finish", SourceRef = 2, TargetRef = 3 }
        ]
    };

    internal static CreateBusinessCalendarRequest Weekdays(string name, string timeZone = "Etc/UTC", string hours = "09:00-17:00")
    {
        var weekday = Periods(hours);
        return new CreateBusinessCalendarRequest(
            name,
            timeZone,
            new BusinessCalendarWeekDto(weekday, weekday, weekday, weekday, weekday));
    }

    internal static IReadOnlyList<WorkingPeriodDto> Periods(params string[] periods) =>
        periods.Select(period => period.Split('-')).Select(parts => new WorkingPeriodDto(parts[0], parts[1])).ToList();

    private static IEnumerable<string> Text(IReadOnlyList<WorkingPeriodDto>? periods) =>
        (periods ?? []).Select(period => $"{period.Start}-{period.End}");

    private static BusinessCalendarPreviewRequest Preview(string timeZone, string start, string duration)
    {
        var weekday = Periods("09:00-17:00");
        return new BusinessCalendarPreviewRequest(
            timeZone,
            new BusinessCalendarWeekDto(weekday, weekday, weekday, weekday, weekday),
            null,
            start,
            duration);
    }

    // The response escapes characters outside ASCII, so the error text is read from the JSON.
    private async Task AssertErrorAsync(HttpMethod method, string path, object body, string error)
    {
        using var response = await SendAsync(method, path, body);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.BadRequest, text);
        Assert.Equal(error, JsonNode.Parse(text)!["error"]!.GetValue<string>());
    }

    private async Task AssertDeleteConflictAsync(BusinessCalendarDto calendar, string reason)
    {
        using var response = await DeleteAsync(calendar.Id, calendar.UpdatedAt);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains(reason, await response.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    private async Task<BusinessCalendarDto> CreateCalendarAsync(string name)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/business-calendars", Weekdays(name));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await ReadAsync<BusinessCalendarDto>(response);
    }

    private async Task DeleteQuietlyAsync(BusinessCalendarDto calendar)
    {
        using var response = await DeleteAsync(calendar.Id, calendar.UpdatedAt);
    }

    private async Task<long> CreateWorkflowAsync(WorkflowModel model, bool publish)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, publish));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return body["id"]!.GetValue<long>();
    }

    private async Task<long> StartAsync(long workflowId)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances",
            new StartInstanceRequest(workflowId, null, null, null));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync())!;
        return body["id"]!.GetValue<long>();
    }

    private Task<HttpResponseMessage> DeleteAsync(long id, DateTimeOffset expectedUpdatedAt) =>
        SendAsync(
            HttpMethod.Delete,
            $"/api/business-calendars/{id}?expectedUpdatedAt={Uri.EscapeDataString(expectedUpdatedAt.ToString("O", System.Globalization.CultureInfo.InvariantCulture))}");

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "settings-admin",
        string[]? roles = null,
        bool suppressAdmin = false)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        ApiTestAuth.Authorize(request, user, roles ?? []);
        if (suppressAdmin)
        {
            request.Headers.TryAddWithoutValidation("X-Test-Suppress-Admin", "true");
        }

        return await fixture.Client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("Response body was empty.");
}
