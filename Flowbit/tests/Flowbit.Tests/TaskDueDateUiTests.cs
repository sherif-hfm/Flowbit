extern alias FlowbitUi;

using System.Collections;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using InboxPage = FlowbitUi::Flowbit.Ui.Components.Pages.Inbox;
using TaskDueDateComponent = FlowbitUi::Flowbit.Ui.Components.Shared.TaskDueDate;
using TokenState = FlowbitUi::Flowbit.Ui.Auth.TokenState;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using Xunit;

namespace Flowbit.Tests;

public sealed class TaskDueDateUiTests
{
    private static readonly DateTimeOffset DueAt = new(2030, 1, 31, 15, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(UserTaskDueStatuses.Overdue, "status-danger", "Overdue")]
    [InlineData(UserTaskDueStatuses.DueSoon, "status-warning", "Due soon")]
    public async Task OverdueAndDueSoonShowABadgeAndTheExactTimeAsTooltip(
        string dueStatus,
        string tone,
        string text)
    {
        var html = await RenderAsync(DueAt, dueStatus, null);

        Assert.Contains("title=\"2030-01-31T15:00:00.0000000+00:00\"", html, StringComparison.Ordinal);
        Assert.Contains(DueAt.ToLocalTime().ToString("g"), html, StringComparison.Ordinal);
        Assert.Contains(tone, html, StringComparison.Ordinal);
        Assert.Contains(text, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UpcomingShowsOnlyTheTimeAndMissingTimesShowTheEmptyText()
    {
        var upcoming = await RenderAsync(DueAt, UserTaskDueStatuses.Upcoming, "—");
        Assert.Contains(DueAt.ToLocalTime().ToString("g"), upcoming, StringComparison.Ordinal);
        Assert.DoesNotContain("status-badge", upcoming, StringComparison.Ordinal);
        Assert.DoesNotContain("—", upcoming, StringComparison.Ordinal);

        Assert.Contains("—", await RenderAsync(null, null, "—"), StringComparison.Ordinal);
        Assert.Equal(string.Empty, (await RenderAsync(null, null, null)).Trim());
    }

    [Fact]
    public async Task MyWorkSendsDueAndPriorityFiltersAndCountsThem()
    {
        var handler = new CapturingHandler();
        var page = CreateInboxPage(handler);
        SetField(page, "dueStatus", UserTaskDueStatuses.Overdue);
        SetField(page, "minPriority", 50);
        SetField(page, "maxPriority", 90);

        await InvokeAsync(page, "LoadAsync");

        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        Assert.Equal(
            [UserTaskDueStatuses.Overdue],
            body.RootElement.GetProperty("dueStatuses").EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(50, body.RootElement.GetProperty("minPriority").GetInt32());
        Assert.Equal(90, body.RootElement.GetProperty("maxPriority").GetInt32());
        Assert.Equal(3, GetProperty<int>(page, "ActiveFilterCount"));

        await InvokeAsync(page, "ClearFiltersAsync");

        Assert.Null(GetField<string?>(page, "dueStatus"));
        Assert.Null(GetField<int?>(page, "minPriority"));
        Assert.Null(GetField<int?>(page, "maxPriority"));
        using var cleared = JsonDocument.Parse(handler.Bodies[^1]);
        Assert.True(
            !cleared.RootElement.TryGetProperty("dueStatuses", out var statuses)
            || statuses.ValueKind == JsonValueKind.Null);
    }

    [Fact]
    public void MyWorkOffersDueDateAndPrioritySorting()
    {
        var options = (IEnumerable)typeof(InboxPage)
            .GetField("InboxSortOptions", BindingFlags.Static | BindingFlags.NonPublic)!
            .GetValue(null)!;
        var values = options.Cast<object>()
            .Select(option => (string)option.GetType().GetProperty("Value")!.GetValue(option)!)
            .ToArray();

        Assert.Contains("dueAt", values);
        Assert.Contains("priority", values);
    }

    // Returns decoded markup, because the renderer encodes characters such as +.
    private static async Task<string> RenderAsync(DateTimeOffset? dueAt, string? dueStatus, string? emptyText)
    {
        await using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        await using var renderer = new HtmlRenderer(services, services.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<TaskDueDateComponent>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    ["DueAt"] = dueAt,
                    ["DueStatus"] = dueStatus,
                    ["EmptyText"] = emptyText
                }));
            return component.ToHtmlString();
        });
        return WebUtility.HtmlDecode(html);
    }

    private static InboxPage CreateInboxPage(HttpMessageHandler handler)
    {
        var page = new InboxPage();
        var token = new TokenState();
        token.Set("test-token");
        SetProperty(page, "Token", token);
        SetProperty(page, "Api", new WorkflowApiClient(
            new HttpClient(handler) { BaseAddress = new Uri("http://flowbit.test") }));
        return page;
    }

    private static async Task InvokeAsync(object target, string name)
    {
        var method = target.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Method '{name}' was not found.");
        await Assert.IsAssignableFrom<Task>(method.Invoke(target, null));
    }

    private static T GetField<T>(object target, string name) =>
        (T)target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void SetField(object target, string name, object? value) =>
        (target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Field '{name}' was not found.")).SetValue(target, value);

    private static T GetProperty<T>(object target, string name) =>
        (T)target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(target)!;

    private static void SetProperty(object target, string name, object value) =>
        (target.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"Property '{name}' was not found.")).SetValue(target, value);

    private sealed class CapturingHandler : HttpMessageHandler
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
                Content = new StringContent(
                    "{\"items\":[],\"page\":1,\"pageSize\":50,\"totalCount\":0}",
                    Encoding.UTF8,
                    "application/json")
            };
        }
    }
}
