extern alias FlowbitUi;

using System.Globalization;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Xunit;
using BrowserTimeZone = FlowbitUi::Flowbit.Ui.Components.Shared.BrowserTimeZone;
using BrowserTimeZoneProvider = FlowbitUi::Flowbit.Ui.Components.Shared.BrowserTimeZoneProvider;
using LocalTimeComponent = FlowbitUi::Flowbit.Ui.Components.Shared.LocalTime;
using NodeActivityPage = FlowbitUi::Flowbit.Ui.Components.Pages.NodeActivity;
using SharedVariableClientsPage = FlowbitUi::Flowbit.Ui.Components.Pages.SharedVariableClients;
using TimeZoneNoteComponent = FlowbitUi::Flowbit.Ui.Components.Shared.TimeZoneNote;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;

namespace Flowbit.Tests;

/// <summary>
/// Flowbit.Ui renders on the server, so every time is shown and read in the browser's
/// zone (BrowserTimeZone), or in UTC with a UTC label until that zone is known.
/// </summary>
public sealed class BrowserTimeZoneUiTests
{
    private const string Skipped = "does not exist in {0} because of a daylight-saving change.";

    [Theory]
    [InlineData("Asia/Tokyo")]
    [InlineData("America/New_York")]
    [InlineData("Europe/Berlin")]
    [InlineData("Asia/Kathmandu")]
    [InlineData("UTC")]
    public void A_zone_the_server_knows_is_used_and_named(string name)
    {
        var zone = BrowserTimeZone.FromBrowser(name);

        Assert.False(zone.IsUtcFallback);
        Assert.True(zone.Reported);
        Assert.Equal(name, zone.Name);
        Assert.Equal(name, zone.DisplayName);
        Assert.Equal($"Times use your browser's time zone, {name}.", zone.Description);
    }

    [Fact]
    public void Before_the_browser_answers_times_use_labelled_UTC()
    {
        var zone = BrowserTimeZone.Pending;

        Assert.True(zone.IsUtcFallback);
        Assert.False(zone.Reported);
        Assert.Equal("UTC", zone.DisplayName);
        Assert.Equal(TimeZoneInfo.Utc, zone.Zone);
        Assert.Equal("Times use UTC until your browser's time zone is known.", zone.Description);
    }

    [Fact]
    public void A_well_formed_zone_the_server_cannot_find_falls_back_to_UTC_and_is_named()
    {
        var zone = BrowserTimeZone.FromBrowser("Mars/Olympus_Mons");

        Assert.True(zone.IsUtcFallback);
        Assert.True(zone.Reported);
        Assert.Equal("Mars/Olympus_Mons", zone.UnknownName);
        Assert.Equal(TimeZoneInfo.Utc, zone.Zone);
        Assert.Equal(
            "Times use UTC because the server does not recognize your browser's time zone, Mars/Olympus_Mons.",
            zone.Description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("../../etc/localtime")]
    [InlineData("Europe/Berlin\u0000")]
    [InlineData("Pacific Standard Time")]
    [InlineData("<b>Europe/Berlin</b>")]
    [InlineData("A/B/C/D/E")]
    public void A_missing_or_malformed_name_never_reaches_the_lookup_or_the_page(string? name)
    {
        var zone = BrowserTimeZone.FromBrowser(name);

        Assert.True(zone.IsUtcFallback);
        Assert.True(zone.Reported);
        Assert.Null(zone.UnknownName);
        Assert.Equal("Times use UTC because your browser did not report a time zone.", zone.Description);
    }

    [Fact]
    public void An_overlong_name_is_not_a_zone() =>
        Assert.True(BrowserTimeZone.FromBrowser("Europe/" + new string('A', 300)).IsUtcFallback);

    [Theory]
    [InlineData("Asia/Tokyo", "2026-11-08T09:00:00Z", "2026-11-08T18:00:00")]
    [InlineData("Asia/Tokyo", "2026-11-08T12:00:00+03:00", "2026-11-08T18:00:00")]
    [InlineData("America/New_York", "2026-01-15T15:30:00Z", "2026-01-15T10:30:00")]
    [InlineData("America/New_York", "2026-07-01T14:30:00Z", "2026-07-01T10:30:00")]
    [InlineData("Asia/Kolkata", "2026-11-08T09:00:00Z", "2026-11-08T14:30:00")]
    [InlineData("Asia/Kathmandu", "2026-11-08T09:00:00Z", "2026-11-08T14:45:00")]
    [InlineData("Australia/Sydney", "2026-01-15T00:00:00Z", "2026-01-15T11:00:00")]
    [InlineData("Australia/Sydney", "2026-07-15T00:00:00Z", "2026-07-15T10:00:00")]
    // Berlin's clocks go forward at 01:00 UTC on 29 March and back at 01:00 UTC on 25 October 2026.
    [InlineData("Europe/Berlin", "2026-03-29T00:59:00Z", "2026-03-29T01:59:00")]
    [InlineData("Europe/Berlin", "2026-03-29T01:00:00Z", "2026-03-29T03:00:00")]
    [InlineData("Europe/Berlin", "2026-10-25T00:30:00Z", "2026-10-25T02:30:00")]
    [InlineData("Europe/Berlin", "2026-10-25T01:30:00Z", "2026-10-25T02:30:00")]
    public void Times_are_shown_in_the_browsers_zone(string name, string instant, string local)
    {
        var zone = BrowserTimeZone.FromBrowser(name);
        var expected = DateTime.Parse(local, CultureInfo.InvariantCulture);
        var value = DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture);

        Assert.Equal(expected, zone.ToLocal(value));
        Assert.Equal(expected.ToString("g"), zone.Format(value));
        Assert.Equal(expected.ToString("G"), zone.Format(value, "G"));
        Assert.Equal(expected.ToString("MMM d"), zone.Format(value, "MMM d"));
    }

    [Fact]
    public void The_UTC_fallback_says_UTC_after_every_time()
    {
        var value = DateTimeOffset.Parse("2026-11-08T12:00:00+03:00", CultureInfo.InvariantCulture);
        var utc = new DateTime(2026, 11, 8, 9, 0, 0);

        Assert.Equal(utc.ToString("g") + " UTC", BrowserTimeZone.Pending.Format(value));
        Assert.Equal(utc.ToString("T") + " UTC", BrowserTimeZone.FromBrowser("Mars/Olympus_Mons").Format(value, "T"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-11-08")]
    [InlineData("11/08/2026 10:00")]
    public void Empty_or_incomplete_text_is_no_time(string? text)
    {
        Assert.True(BrowserTimeZone.FromBrowser("America/New_York").TryReadInput(text, out var instant, out var problem));
        Assert.Null(instant);
        Assert.Null(problem);
    }

    [Theory]
    [InlineData("America/New_York", "2026-01-15T10:30", "2026-01-15T15:30:00Z")]
    [InlineData("America/New_York", "2026-07-01T10:30:00", "2026-07-01T14:30:00Z")]
    [InlineData("Asia/Tokyo", "2026-11-08T18:00:30", "2026-11-08T09:00:30Z")]
    [InlineData("Asia/Tokyo", "2026-11-08T18:00:00.000", "2026-11-08T09:00:00Z")]
    [InlineData("Asia/Kathmandu", "2026-11-08T14:45", "2026-11-08T09:00:00Z")]
    [InlineData("UTC", "2026-11-08T09:00", "2026-11-08T09:00:00Z")]
    // A repeated time means its first occurrence, the offset in force before the clocks went back.
    [InlineData("America/New_York", "2026-11-01T01:30", "2026-11-01T05:30:00Z")]
    [InlineData("Europe/Berlin", "2026-10-25T02:30", "2026-10-25T00:30:00Z")]
    [InlineData("Australia/Sydney", "2026-04-05T02:30", "2026-04-04T15:30:00Z")]
    // The minutes either side of a gap are ordinary times.
    [InlineData("America/New_York", "2026-03-08T01:59", "2026-03-08T06:59:00Z")]
    [InlineData("America/New_York", "2026-03-08T03:00", "2026-03-08T07:00:00Z")]
    public void Typed_times_are_read_in_the_browsers_zone(string name, string text, string instant)
    {
        Assert.True(BrowserTimeZone.FromBrowser(name).TryReadInput(text, out var value, out var problem));
        Assert.Null(problem);
        Assert.Equal(DateTimeOffset.Parse(instant, CultureInfo.InvariantCulture), value);
        Assert.Equal(TimeSpan.Zero, value!.Value.Offset);
    }

    [Fact]
    public void Before_the_browser_answers_typed_times_are_read_as_UTC()
    {
        Assert.True(BrowserTimeZone.Pending.TryReadInput("2026-11-07T10:00", out var value, out _));
        Assert.Equal(DateTimeOffset.Parse("2026-11-07T10:00:00Z", CultureInfo.InvariantCulture), value);
    }

    [Theory]
    [InlineData("America/New_York", "2026-03-08T02:30", "2026-03-08 02:30")]
    [InlineData("America/New_York", "2026-03-08T02:00", "2026-03-08 02:00")]
    [InlineData("Europe/Berlin", "2026-03-29T02:30", "2026-03-29 02:30")]
    [InlineData("Australia/Sydney", "2026-10-04T02:15", "2026-10-04 02:15")]
    public void A_time_a_daylight_saving_change_skips_is_refused(string name, string text, string shown)
    {
        Assert.False(BrowserTimeZone.FromBrowser(name).TryReadInput(text, out var value, out var problem));
        Assert.Null(value);
        Assert.Equal($"{shown} {string.Format(CultureInfo.InvariantCulture, Skipped, name)}", problem);
    }

    [Theory]
    [InlineData("Asia/Tokyo", "0001-01-01T00:00", "0001-01-01 00:00")]
    [InlineData("America/New_York", "9999-12-31T23:59", "9999-12-31 23:59")]
    public void A_time_outside_the_calendar_in_UTC_is_refused(string name, string text, string shown)
    {
        Assert.False(BrowserTimeZone.FromBrowser(name).TryReadInput(text, out var value, out var problem));
        Assert.Null(value);
        Assert.Equal($"{shown} in {name} is outside the supported range of dates.", problem);
    }

    [Fact]
    public void Input_text_shows_an_instant_in_the_browsers_zone()
    {
        var tokyo = BrowserTimeZone.FromBrowser("Asia/Tokyo");

        Assert.Equal("2026-11-08T18:00", tokyo.FormatInput(DateTimeOffset.Parse("2026-11-08T09:00:00Z", CultureInfo.InvariantCulture)));
        Assert.Equal("2026-11-08T09:00:15", BrowserTimeZone.Pending.FormatInput(DateTimeOffset.Parse("2026-11-08T09:00:15Z", CultureInfo.InvariantCulture)));
    }

    [Fact]
    public void Every_quarter_hour_across_both_changes_reads_back_as_shown()
    {
        var berlin = BrowserTimeZone.FromBrowser("Europe/Berlin");
        foreach (var day in new[] { "2026-03-28T12:00:00Z", "2026-10-24T12:00:00Z" })
        {
            var start = DateTimeOffset.Parse(day, CultureInfo.InvariantCulture);
            for (var instant = start; instant < start.AddDays(2); instant = instant.AddMinutes(15))
            {
                Assert.True(berlin.TryReadInput(berlin.FormatInput(instant), out var read, out var problem), problem);
                // The second pass through the repeated hour reads as its first occurrence, an hour earlier.
                var repeated = instant.UtcDateTime is { Month: 10 } utc && utc >= new DateTime(2026, 10, 25, 1, 0, 0) && utc < new DateTime(2026, 10, 25, 2, 0, 0);
                Assert.Equal(repeated ? instant.AddHours(-1) : instant, read);
            }
        }
    }

    [Fact]
    public async Task LocalTime_shows_the_instant_in_the_cascaded_zone_and_keeps_the_exact_instant_as_its_tooltip()
    {
        var value = DateTimeOffset.Parse("2026-11-08T09:00:00Z", CultureInfo.InvariantCulture);

        var tokyo = await RenderLocalTimeAsync(value, BrowserTimeZone.FromBrowser("Asia/Tokyo"));
        Assert.Equal(
            $"<time datetime=\"2026-11-08T09:00:00.000Z\" title=\"2026-11-08T09:00:00.0000000+00:00\">{new DateTime(2026, 11, 8, 18, 0, 0):g}</time>",
            tokyo);

        var fallback = await RenderLocalTimeAsync(value, null);
        Assert.Contains($">{new DateTime(2026, 11, 8, 9, 0, 0):g} UTC</time>", fallback, StringComparison.Ordinal);

        Assert.Equal("Never", await RenderLocalTimeAsync(null, null, emptyText: "Never"));
        Assert.Equal(string.Empty, await RenderLocalTimeAsync(null, null));
        Assert.Contains($">{new DateTime(2026, 11, 8, 18, 0, 0):G}</time>", await RenderLocalTimeAsync(value, BrowserTimeZone.FromBrowser("Asia/Tokyo"), "G"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_circuit_asks_the_browser_once_and_rerenders_every_time_in_its_zone()
    {
        var js = new ZoneJsRuntime("Asia/Tokyo");
        var services = new ServiceCollection().AddLogging();
        services.AddScoped<BrowserTimeZoneProvider>();
        services.AddCascadingValue(provider => provider.GetRequiredService<BrowserTimeZoneProvider>().Source);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var value = DateTimeOffset.Parse("2026-11-08T09:00:00Z", CultureInfo.InvariantCulture);
        var (time, note) = await renderer.Dispatcher.InvokeAsync(async () => (
            await renderer.RenderComponentAsync<LocalTimeComponent>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Value"] = (DateTimeOffset?)value })),
            await renderer.RenderComponentAsync<TimeZoneNoteComponent>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Id"] = "zone" }))));

        Assert.Contains(" UTC</time>", await HtmlAsync(renderer, time), StringComparison.Ordinal);
        Assert.Contains(">Times use UTC until your browser's time zone is known.</div>", await HtmlAsync(renderer, note), StringComparison.Ordinal);

        var zones = provider.GetRequiredService<BrowserTimeZoneProvider>();
        await renderer.Dispatcher.InvokeAsync(() => zones.ReadBrowserZoneAsync(js));
        await renderer.Dispatcher.InvokeAsync(() => zones.ReadBrowserZoneAsync(js));

        Assert.Equal(1, js.Calls);
        Assert.Contains($">{new DateTime(2026, 11, 8, 18, 0, 0):g}</time>", await HtmlAsync(renderer, time), StringComparison.Ordinal);
        var shown = await HtmlAsync(renderer, note);
        Assert.Contains("id=\"zone\"", shown, StringComparison.Ordinal);
        Assert.Contains(">Times use your browser's time zone, Asia/Tokyo.</div>", shown, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_browser_without_the_zone_function_keeps_the_labelled_UTC_fallback()
    {
        var js = new ZoneJsRuntime(new JSException("flowbit.timeZone is not a function"));
        var services = new ServiceCollection().AddLogging();
        services.AddScoped<BrowserTimeZoneProvider>();
        services.AddCascadingValue(provider => provider.GetRequiredService<BrowserTimeZoneProvider>().Source);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var note = await renderer.Dispatcher.InvokeAsync(() =>
            renderer.RenderComponentAsync<TimeZoneNoteComponent>(ParameterView.FromDictionary(new Dictionary<string, object?> { ["Id"] = "zone" })));

        await renderer.Dispatcher.InvokeAsync(() => provider.GetRequiredService<BrowserTimeZoneProvider>().ReadBrowserZoneAsync(js));

        Assert.Contains(
            ">Times use UTC because your browser did not report a time zone.</div>",
            await HtmlAsync(renderer, note), StringComparison.Ordinal);
    }

    private static async Task<string> HtmlAsync(HtmlRenderer renderer, HtmlRootComponent component) =>
        WebUtility.HtmlDecode(await renderer.Dispatcher.InvokeAsync(component.ToHtmlString));

    [Fact]
    public async Task Activity_sends_its_time_filters_as_the_UTC_instants_they_name_in_the_browsers_zone()
    {
        var handler = new CapturingHandler("{\"items\":[],\"page\":1,\"pageSize\":50,\"totalCount\":0}");
        var page = new NodeActivityPage();
        SetProperty(page, "Api", Client(handler));
        SetProperty(page, "TimeZone", BrowserTimeZone.FromBrowser("America/New_York"));
        SetField(page, "createdFrom", "2026-11-07T10:00");
        SetField(page, "updatedTo", "2026-07-01T10:30");

        await InvokeAsync(page, "LoadAsync");

        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        var createdFrom = body.RootElement.GetProperty("createdFrom").GetDateTimeOffset();
        Assert.Equal(DateTimeOffset.Parse("2026-11-07T15:00:00Z", CultureInfo.InvariantCulture), createdFrom);
        Assert.Equal(TimeSpan.Zero, createdFrom.Offset);
        Assert.Equal(
            DateTimeOffset.Parse("2026-07-01T14:30:00Z", CultureInfo.InvariantCulture),
            body.RootElement.GetProperty("updatedTo").GetDateTimeOffset());
        Assert.True(
            !body.RootElement.TryGetProperty("startedFrom", out var startedFrom) || startedFrom.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public async Task Activity_refuses_a_skipped_time_before_searching()
    {
        var handler = new CapturingHandler("{\"items\":[],\"page\":1,\"pageSize\":50,\"totalCount\":0}");
        var page = new NodeActivityPage();
        SetProperty(page, "Api", Client(handler));
        SetProperty(page, "TimeZone", BrowserTimeZone.FromBrowser("America/New_York"));
        SetField(page, "startedFrom", "2026-03-08T02:30");

        await InvokeAsync(page, "LoadAsync");

        Assert.Empty(handler.Bodies);
        Assert.True(GetField<bool>(page, "loadFailed"));
        Assert.False(GetField<bool>(page, "loading"));
        Assert.Equal(
            $"Started from: 2026-03-08 02:30 {string.Format(CultureInfo.InvariantCulture, Skipped, "America/New_York")}",
            GetField<string?>(page, "error"));
    }

    [Fact]
    public void A_client_expiry_is_shown_and_read_in_the_browsers_zone()
    {
        var page = new SharedVariableClientsPage();
        SetProperty(page, "TimeZone", BrowserTimeZone.FromBrowser("Asia/Tokyo"));
        var stored = DateTimeOffset.Parse("2099-01-01T00:00:00.1234567Z", CultureInfo.InvariantCulture);
        var client = new SharedVariableClientDto(
            7, "erp", "ERP", [SharedVariableClientScopes.Read], "active", 3, stored,
            DateTimeOffset.Parse("2026-11-08T09:00:00Z", CultureInfo.InvariantCulture),
            DateTimeOffset.Parse("2026-11-08T09:00:00Z", CultureInfo.InvariantCulture),
            null);

        Invoke(page, "SetClientForm", client);
        Assert.Equal("2099-01-01T09:00", GetField<string?>(page, "formExpiresShown"));
        Assert.Equal("2099-01-01T09:00", GetField<string?>(page, "formExpires"));
        // An untouched field keeps the stored instant, fraction included.
        Assert.Equal((true, stored, null), TryExpires(page, "2099-01-01T09:00"));

        var (read, expires, message) = TryExpires(page, "2099-07-01T09:30");
        Assert.True(read, message);
        Assert.Equal(DateTimeOffset.Parse("2099-07-01T00:30:00Z", CultureInfo.InvariantCulture), expires);
        Assert.Equal(TimeSpan.Zero, expires!.Value.Offset);

        var (future, _, past) = TryExpires(page, "2020-01-01T09:00");
        Assert.False(future);
        Assert.Equal("Expiration must be in the future.", past);
        Assert.Equal((true, null, null), TryExpires(page, null));

        SetProperty(page, "TimeZone", BrowserTimeZone.FromBrowser("America/New_York"));
        Assert.Equal(
            (false, null, $"Expiration: 2026-03-08 02:30 {string.Format(CultureInfo.InvariantCulture, Skipped, "America/New_York")}"),
            TryExpires(page, "2026-03-08T02:30"));
    }

    private static (bool Read, DateTimeOffset? Expires, string? Message) TryExpires(SharedVariableClientsPage page, string? text)
    {
        SetField(page, "formExpires", text);
        var arguments = new object?[] { null, null };
        var read = (bool)typeof(SharedVariableClientsPage)
            .GetMethod("TryExpires", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, arguments)!;
        return (read, (DateTimeOffset?)arguments[0], (string?)arguments[1]);
    }

    private static async Task<string> RenderLocalTimeAsync(
        DateTimeOffset? value, BrowserTimeZone? zone, string format = "g", string? emptyText = null)
    {
        var services = new ServiceCollection().AddLogging();
        if (zone is not null)
        {
            services.AddCascadingValue(_ => zone);
        }
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<LocalTimeComponent>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["Value"] = value,
                    ["Format"] = format,
                    ["EmptyText"] = emptyText
                }));
            return component.ToHtmlString();
        });
        return WebUtility.HtmlDecode(html).Trim();
    }

    private static WorkflowApiClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://flowbit.test") });

    private static async Task InvokeAsync(object target, string name) =>
        await Assert.IsAssignableFrom<Task>(Invoke(target, name));

    private static object? Invoke(object target, string name, params object?[] arguments) =>
        (target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method '{name}' was not found.")).Invoke(target, arguments);

    private static T GetField<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void SetField(object target, string name, object? value) =>
        (target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{name}' was not found.")).SetValue(target, value);

    private static void SetProperty(object target, string name, object value) =>
        (target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Property '{name}' was not found.")).SetValue(target, value);

    private sealed class ZoneJsRuntime : IJSRuntime
    {
        private readonly string? zone;
        private readonly Exception? failure;

        public ZoneJsRuntime(string zone) => this.zone = zone;

        public ZoneJsRuntime(Exception failure) => this.failure = failure;

        public int Calls { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            Assert.Equal("flowbit.timeZone", identifier);
            Calls++;
            return failure is not null
                ? ValueTask.FromException<TValue>(failure)
                : ValueTask.FromResult((TValue)(object)zone!);
        }
    }

    private sealed class CapturingHandler(string response) : HttpMessageHandler
    {
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Bodies.Add(request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            };
        }
    }
}
