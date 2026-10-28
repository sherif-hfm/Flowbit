extern alias FlowbitUi;

using System.Collections;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using BusinessCalendarText = FlowbitUi::Flowbit.Ui.Components.Shared.BusinessCalendarText;
using BusinessCalendarsPage = FlowbitUi::Flowbit.Ui.Components.Pages.BusinessCalendars;
using TokenState = FlowbitUi::Flowbit.Ui.Auth.TokenState;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using Xunit;

namespace Flowbit.Tests;

public sealed class BusinessCalendarsPageStateTests
{
    private static readonly IReadOnlyList<WorkingPeriodDto> Lunch =
        [new("09:00", "12:30"), new("13:30", "17:30")];

    private static readonly BusinessCalendarDto Calendar = new(
        7,
        "uk-office",
        "London office",
        "Europe/London",
        new BusinessCalendarWeekDto(Lunch, Lunch, Lunch, Lunch, [new("09:00", "13:00")], [], []),
        [
            new BusinessCalendarHolidayDto(new DateOnly(2026, 12, 24), "Christmas Eve", [new("09:00", "12:00")]),
            new BusinessCalendarHolidayDto(new DateOnly(2026, 12, 25), "Christmas Day", [])
        ],
        DateTimeOffset.Parse("2026-10-01T00:00:00Z"),
        DateTimeOffset.Parse("2026-10-02T08:30:00.123456Z"));

    [Fact]
    public async Task AddingACalendarSendsItsWeekAndHolidaysAndReloads()
    {
        using var handler = new RecordingHandler(
            Response(Calendar, HttpStatusCode.Created),
            Response(new[] { Calendar }),
            Response(new[] { "Etc/UTC", "Europe/London" }));
        var page = Page(handler);

        Invoke(page, "OpenCreate");
        Assert.Equal("Etc/UTC", Get<string>(page, "formTimeZone"));
        Assert.Equal(["09:00-17:00", "09:00-17:00", "09:00-17:00", "09:00-17:00", "09:00-17:00", "", ""],
            Get<string[]>(page, "formDays"));
        Set(page, "formName", " uk-office ");
        Set(page, "formTimeZone", "Europe/London");
        Set(page, "formDays", new[] { "13:30-17:30, 09:00–12:30", "", "", "", "", "", "" });
        Invoke(page, "AddHoliday");
        var holiday = Get<IList>(page, "formHolidays")[0]!;
        SetProperty(holiday, "DateText", "2026-12-24");
        SetProperty(holiday, "Name", "Christmas Eve");
        SetProperty(holiday, "Hours", "09:00-12:00");
        await InvokeAsync(page, "SaveAsync");

        var create = handler.Requests[0];
        Assert.Equal((HttpMethod.Post, "/api/business-calendars"), (create.Method, create.Path));
        using (var body = JsonDocument.Parse(create.Body!))
        {
            var root = body.RootElement;
            Assert.Equal(" uk-office ", root.GetProperty("name").GetString());
            Assert.Equal("Europe/London", root.GetProperty("timeZone").GetString());
            var monday = root.GetProperty("weeklyHours").GetProperty("monday");
            Assert.Equal("09:00", monday[0].GetProperty("start").GetString());
            Assert.Equal("13:30", monday[1].GetProperty("start").GetString());
            Assert.Equal(0, root.GetProperty("weeklyHours").GetProperty("tuesday").GetArrayLength());
            var savedHoliday = root.GetProperty("holidays")[0];
            Assert.Equal("2026-12-24", savedHoliday.GetProperty("date").GetString());
            Assert.Equal("12:00", savedHoliday.GetProperty("workingHours")[0].GetProperty("end").GetString());
        }

        Assert.Equal((HttpMethod.Get, "/api/business-calendars"), (handler.Requests[1].Method, handler.Requests[1].Path));
        Assert.Equal("/api/business-calendars/time-zones", handler.Requests[2].Path);
        Assert.False(Get<bool>(page, "formOpen"));
        Assert.Contains("uk-office added", Get<string>(page, "success"));
        Assert.Equal(["Etc/UTC", "Europe/London"], Get<IReadOnlyList<string>>(page, "timeZones"));
    }

    [Fact]
    public async Task APeriodThatCannotBeReadStopsTheSaveBeforeAnyRequest()
    {
        using var handler = new RecordingHandler();
        var page = Page(handler);

        Invoke(page, "OpenCreate");
        Set(page, "formName", "uk-office");
        Get<string[]>(page, "formDays")[2] = "9 to 5";
        await InvokeAsync(page, "SaveAsync");

        Assert.Empty(handler.Requests);
        Assert.Equal("Wednesday: '9 to 5' is not a period such as 09:00-17:00.", Get<string>(page, "formError"));
        Assert.True(Get<bool>(page, "formOpen"));
    }

    [Fact]
    public async Task AHolidayDateLeftIncompleteStopsTheSaveBeforeAnyRequest()
    {
        using var handler = new RecordingHandler();
        var page = Page(handler);

        Invoke(page, "OpenCreate");
        Set(page, "formName", "uk-office");
        Invoke(page, "AddHoliday");
        // A date input reports no value while a segment is only partly typed.
        SetProperty(Get<IList>(page, "formHolidays")[0]!, "DateText", string.Empty);
        await InvokeAsync(page, "SaveAsync");

        Assert.Empty(handler.Requests);
        Assert.Equal("Holiday 1 needs a date.", Get<string>(page, "formError"));
    }

    [Fact]
    public async Task EditingShowsLoadedHolidayDatesAndSavesWhatTheirInputsReport()
    {
        using var handler = new RecordingHandler(
            Response(Calendar),
            Response(new[] { Calendar }),
            Response(new[] { "Europe/London" }));
        var page = Page(handler);

        Invoke(page, "OpenEdit", Calendar);
        var holidays = Get<IList>(page, "formHolidays");
        Assert.Equal(["2026-12-24", "2026-12-25"], holidays.Cast<object>().Select(row => GetProperty<string>(row, "ShownDate")));
        Assert.Equal(["2026-12-24", "2026-12-25"], holidays.Cast<object>().Select(row => GetProperty<string>(row, "DateText")));
        SetProperty(holidays[1]!, "DateText", "2027-01-04");
        await InvokeAsync(page, "SaveAsync");

        using var body = JsonDocument.Parse(handler.Requests[0].Body!);
        var saved = body.RootElement.GetProperty("holidays");
        Assert.Equal("2026-12-24", saved[0].GetProperty("date").GetString());
        Assert.Equal("2027-01-04", saved[1].GetProperty("date").GetString());
        Assert.Contains("uk-office updated", Get<string>(page, "success"));
    }

    [Fact]
    public async Task EditingSendsTheVersionAndAConflictKeepsTheForm()
    {
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent(
                "{\"error\":\"Business calendar 'uk-office' was changed by another request; reload and try again.\"}",
                Encoding.UTF8,
                "application/json")
        });
        var page = Page(handler);

        Invoke(page, "OpenEdit", Calendar);
        Assert.Equal("09:00-12:30, 13:30-17:30", Get<string[]>(page, "formDays")[0]);
        Assert.Equal("09:00-13:00", Get<string[]>(page, "formDays")[4]);
        Assert.Equal(string.Empty, Get<string[]>(page, "formDays")[5]);
        Assert.Equal(2, Get<IList>(page, "formHolidays").Count);
        Set(page, "formDescription", "Changed");
        await InvokeAsync(page, "SaveAsync");

        var update = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Put, "/api/business-calendars/7"), (update.Method, update.Path));
        using (var body = JsonDocument.Parse(update.Body!))
        {
            Assert.Equal(Calendar.UpdatedAt, body.RootElement.GetProperty("expectedUpdatedAt").GetDateTimeOffset());
            Assert.Equal("Changed", body.RootElement.GetProperty("description").GetString());
            Assert.False(body.RootElement.TryGetProperty("name", out _));
        }

        Assert.True(Get<bool>(page, "formOpen"));
        Assert.True(Get<bool>(page, "editConflict"));
        Assert.Contains("changed by another request", Get<string>(page, "formError"));
        Assert.Equal("Changed", Get<string>(page, "formDescription"));
    }

    [Fact]
    public async Task AFailedReloadKeepsTheDraftAndASuccessfulOneReplacesIt()
    {
        var newer = Calendar with
        {
            Description = "Changed elsewhere",
            UpdatedAt = DateTimeOffset.Parse("2026-10-03T09:00:00Z")
        };
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent(
                    "{\"error\":\"Business calendar 'uk-office' was changed by another request; reload and try again.\"}",
                    Encoding.UTF8,
                    "application/json")
            },
            new HttpResponseMessage(HttpStatusCode.InternalServerError)
            {
                Content = new StringContent("{\"error\":\"The database is unavailable.\"}", Encoding.UTF8, "application/json")
            },
            Response(new[] { newer }),
            Response(new[] { "Europe/London" }));
        var page = Page(handler);

        // The list was loaded before, and the calendar was opened from it.
        Set(page, "calendars", new List<BusinessCalendarDto> { Calendar });
        Invoke(page, "OpenEdit", Calendar);
        Set(page, "formDescription", "My unsaved draft");
        await InvokeAsync(page, "SaveAsync");
        Assert.True(Get<bool>(page, "editConflict"));

        await InvokeAsync(page, "ReloadEditAsync");
        Assert.Equal("My unsaved draft", Get<string>(page, "formDescription"));
        Assert.True(Get<bool>(page, "editConflict"));
        Assert.Equal(Calendar.UpdatedAt, Get<DateTimeOffset?>(page, "expectedUpdatedAt"));
        Assert.Equal(
            "The latest version could not be loaded, so your changes are kept. Try Reload latest again.",
            Get<string>(page, "formError"));
        Assert.Equal("The database is unavailable.", Get<string>(page, "error"));

        await InvokeAsync(page, "ReloadEditAsync");
        Assert.Equal("Changed elsewhere", Get<string>(page, "formDescription"));
        Assert.False(Get<bool>(page, "editConflict"));
        Assert.Equal(newer.UpdatedAt, Get<DateTimeOffset?>(page, "expectedUpdatedAt"));
        Assert.Null(Get<string?>(page, "formError"));
        Assert.Equal(
            ["PUT /api/business-calendars/7", "GET /api/business-calendars", "GET /api/business-calendars", "GET /api/business-calendars/time-zones"],
            handler.Requests.Select(request => $"{request.Method} {request.Path}"));
    }

    [Fact]
    public async Task DeletingShowsWhyACalendarInUseCannotBeDeleted()
    {
        const string reason = "Business calendar 'uk-office' cannot be deleted: 1 published workflow definition(s) use it.";
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent($"{{\"error\":\"{reason}\"}}", Encoding.UTF8, "application/json")
        });
        var js = new ConfirmingJs();
        var page = Page(handler, js);

        await (Task)Invoke(page, "DeleteAsync", Calendar)!;

        Assert.Equal(["confirm"], js.Calls);
        Assert.Equal(
            (HttpMethod.Delete, "/api/business-calendars/7?expectedUpdatedAt=2026-10-02T08%3A30%3A00.1234560%2B00%3A00"),
            (handler.Requests[0].Method, handler.Requests[0].Path));
        Assert.Equal(reason, Get<string>(page, "error"));
    }

    [Fact]
    public async Task PreviewSendsTheDraftAndShowsTheDueTime()
    {
        using var handler = new RecordingHandler(Response(new BusinessCalendarPreviewDto(
            DateTimeOffset.Parse("2026-03-27T14:00:00Z"),
            DateTimeOffset.Parse("2026-03-30T13:00:00Z"),
            new DateTimeOffset(2026, 3, 30, 14, 0, 0, TimeSpan.FromHours(1)))));
        var page = Page(handler);

        Invoke(page, "OpenCreate");
        Set(page, "formTimeZone", "Europe/London");
        // Blazor adds seconds to the value a datetime-local input reports.
        Set(page, "previewStart", "2026-03-27T14:00:00");
        await InvokeAsync(page, "PreviewAsync");

        var preview = Assert.Single(handler.Requests);
        Assert.Equal((HttpMethod.Post, "/api/business-calendars/preview"), (preview.Method, preview.Path));
        using (var body = JsonDocument.Parse(preview.Body!))
        {
            Assert.Equal("2026-03-27T14:00", body.RootElement.GetProperty("start").GetString());
            Assert.Equal("P1D", body.RootElement.GetProperty("duration").GetString());
            Assert.Equal("Europe/London", body.RootElement.GetProperty("timeZone").GetString());
        }

        Assert.Equal(
            "Due Mon 30 Mar 2026 14:00 (UTC+01:00), 2026-03-30 13:00 UTC.",
            Get<string>(page, "previewResult"));
    }

    [Fact]
    public async Task APreviewWithoutAWholeStartAsksForOneAndReopeningTheFormStartsAFreshInput()
    {
        using var handler = new RecordingHandler();
        var page = Page(handler);

        Invoke(page, "OpenCreate");
        // A start input reports no value while a segment is only partly typed.
        Set(page, "previewStart", string.Empty);
        await InvokeAsync(page, "PreviewAsync");

        Assert.Empty(handler.Requests);
        Assert.Equal("Choose a start date and time.", Get<string>(page, "previewError"));

        // The input is uncontrolled, so the form renders a new one rather than keeping the old one's text.
        Set(page, "previewStart", "2026-03-27T14:00:00");
        var generation = Get<int>(page, "previewStartGeneration");
        Invoke(page, "OpenEdit", Calendar);
        Assert.Null(Get<string?>(page, "previewStart"));
        Assert.NotEqual(generation, Get<int>(page, "previewStartGeneration"));
    }

    [Fact]
    public async Task NothingIsRequestedWithoutAnIdentityAndAViewerSeesTheRoleMessage()
    {
        using (var handler = new RecordingHandler())
        {
            var page = Page(handler, token: new TokenState());
            await InvokeAsync(page, "LoadAsync");
            Assert.Empty(handler.Requests);
        }

        using var forbidden = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.Forbidden));
        var viewer = Page(forbidden);
        await InvokeAsync(viewer, "LoadAsync");
        Assert.Equal("This identity does not have the configured settings administrator role.", Get<string>(viewer, "error"));
        Assert.True(Get<bool>(viewer, "loadFailed"));
    }

    [Fact]
    public async Task TheListSummarizesEachCalendarsWeekAndHolidays()
    {
        using var handler = new RecordingHandler(Response(new[] { Calendar }), Response(new[] { "Europe/London" }));
        var token = new TokenState();
        token.Set("admin-token");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Client(handler));
        services.AddSingleton(token);
        services.AddSingleton<IJSRuntime>(new ConfirmingJs());
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var html = WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<BusinessCalendarsPage>(ParameterView.Empty)).ToHtmlString()));

        Assert.Contains("uk-office", html);
        Assert.Contains("Europe/London", html);
        Assert.Contains("Mon–Thu 09:00–12:30, 13:30–17:30 · Fri 09:00–13:00", html);
        Assert.Contains("2 holidays", html);
    }

    [Theory]
    [InlineData("", "")]
    [InlineData(" 13:30-17:30 , 09:00 – 12:30 ", "09:00-12:30, 13:30-17:30")]
    [InlineData("09:00-24:00", "09:00-24:00")]
    public void PeriodsAreReadInTimeOrder(string text, string expected)
    {
        Assert.True(BusinessCalendarText.TryParse(text, out var periods, out _));
        Assert.Equal(expected, BusinessCalendarText.Format(periods));
    }

    [Theory]
    [InlineData("09:00")]
    [InlineData("09:00-")]
    [InlineData("09:00-12:00-13:00")]
    public void PeriodsWithoutAStartAndAnEndAreRefused(string text)
    {
        Assert.False(BusinessCalendarText.TryParse(text, out _, out var problem));
        Assert.EndsWith("is not a period such as 09:00-17:00.", problem);
    }

    [Fact]
    public void HolidayDatesUseTheirInputsText()
    {
        Assert.Equal("2027-01-04", BusinessCalendarText.FormatDate(new DateOnly(2027, 1, 4)));
        Assert.Equal(new DateOnly(2027, 1, 4), BusinessCalendarText.ParseDate("2027-01-04"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("01/04/2027")]
    [InlineData("2027-1-4")]
    [InlineData("2027-01-04T00:00")]
    public void EmptyOrOtherHolidayDateTextIsNull(string? text) =>
        Assert.Null(BusinessCalendarText.ParseDate(text));

    [Fact]
    public void AWeekWithoutHoursSaysSo()
    {
        Assert.Equal("No working hours", BusinessCalendarText.WeekSummary(new BusinessCalendarWeekDto()));
        Assert.Equal(
            "Mon 08:00–16:00 · Wed 08:00–16:00",
            BusinessCalendarText.WeekSummary(new BusinessCalendarWeekDto(
                Monday: [new("08:00", "16:00")],
                Wednesday: [new("08:00", "16:00")])));
    }

    [Theory]
    [InlineData("09:00-17:00", "09:00-17:00", "09:00-17:00", "09:00-17:00", "09:00-17:00", "", "", "Mon–Fri 09:00–17:00")]
    [InlineData("07:30-15:30", "07:30-15:30", "07:30-15:30", "07:30-15:30", "", "", "07:30-15:30", "Sun–Thu 07:30–15:30")]
    [InlineData(
        "08:00-16:00", "08:00-16:00", "08:00-16:00", "08:00-12:00", "", "", "08:00-16:00",
        "Sun–Wed 08:00–16:00 · Thu 08:00–12:00")]
    [InlineData(
        "10:00-14:00", "09:00-17:00", "09:00-17:00", "09:00-17:00", "09:00-17:00", "10:00-14:00", "10:00-14:00",
        "Sat–Mon 10:00–14:00 · Tue–Fri 09:00–17:00")]
    [InlineData(
        "09:00-17:00", "09:00-17:00", "09:00-17:00", "09:00-17:00", "09:00-17:00", "09:00-17:00", "09:00-17:00",
        "Mon–Sun 09:00–17:00")]
    public void DaysWithTheSameHoursAreGroupedAcrossTheEndOfTheWeek(
        string monday, string tuesday, string wednesday, string thursday, string friday, string saturday, string sunday,
        string summary)
    {
        var days = new[] { monday, tuesday, wednesday, thursday, friday, saturday, sunday }
            .Select(text => BusinessCalendarText.TryParse(text, out var periods, out _) ? periods : throw new FormatException(text))
            .ToList();

        Assert.Equal(summary, BusinessCalendarText.WeekSummary(BusinessCalendarText.Week(days)));
    }

    private static BusinessCalendarsPage Page(HttpMessageHandler handler, IJSRuntime? js = null, TokenState? token = null)
    {
        var page = new BusinessCalendarsPage();
        SetPageProperty(page, "Api", Client(handler));
        if (token is null)
        {
            token = new TokenState();
            token.Set("admin-token");
        }
        SetPageProperty(page, "Token", token);
        SetPageProperty(page, "JS", js ?? new ConfirmingJs());
        return page;
    }

    private static WorkflowApiClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") });

    private static HttpResponseMessage Response<T>(T body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(body) };

    private static FieldInfo Field(object page, string name) =>
        page.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static T Get<T>(object page, string name) => (T)Field(page, name).GetValue(page)!;

    private static void Set(object page, string name, object? value) => Field(page, name).SetValue(page, value);

    private static object? Invoke(object page, string name, params object?[] args) =>
        page.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, args);

    private static Task InvokeAsync(object page, string name) => (Task)Invoke(page, name)!;

    private static void SetPageProperty(object page, string name, object value) =>
        page.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static void SetProperty(object target, string name, object? value) =>
        target.GetType().GetProperty(name)!.SetValue(target, value);

    private static T GetProperty<T>(object target, string name) => (T)target.GetType().GetProperty(name)!.GetValue(target)!;

    private sealed record CapturedRequest(HttpMethod Method, string Path, string? Body);

    private sealed class RecordingHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private readonly Queue<HttpResponseMessage> responses = new(responses);
        public List<CapturedRequest> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(new(request.Method, request.RequestUri!.PathAndQuery,
                request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken)));
            return responses.Dequeue();
        }
    }

    private sealed class ConfirmingJs : IJSRuntime
    {
        public List<string> Calls { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            Calls.Add(identifier);
            return ValueTask.FromResult((TValue)(object)true);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }
}
