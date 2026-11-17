extern alias FlowbitUi;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SignalsPage = FlowbitUi::Flowbit.Ui.Components.Pages.Signals;
using TokenState = FlowbitUi::Flowbit.Ui.Auth.TokenState;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// The `/signals` page: a reviewed throw sends one request with a draft idempotency key,
/// the page opens the new broadcast and its deliveries, a permission failure clears every
/// signal value, and the three-second poll refreshes only a broadcast that still delivers.
/// </summary>
public sealed class SignalsPageTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-16T10:00:00Z");

    [Fact]
    public async Task ReviewedThrow_SendsTheScopeAndADraftKeyThenOpensTheBroadcast()
    {
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        Assert.Contains("Choose a signal and its scope", await harness.HtmlAsync(component), StringComparison.Ordinal);

        await harness.SetAsync("signalName", "claims.policy-changed");
        await harness.SetAsync("workflowKey", "claims");
        await harness.SetAsync("payloadText", "{ \"version\": 7 }");
        await harness.CallAsync("ReviewAsync");
        var html = await harness.HtmlAsync(component);
        Assert.Contains("Waiting now", html, StringComparison.Ordinal);
        Assert.Contains("3 waits", html, StringComparison.Ordinal);

        await harness.CallAsync("ThrowAsync");
        var post = Assert.Single(harness.Posts);
        Assert.Equal("/api/signal-broadcasts", post.Path);
        Assert.Equal("claims.policy-changed", post.Body.GetProperty("name").GetString());
        Assert.Equal("claims", post.Body.GetProperty("workflowKey").GetString());
        Assert.False(post.Body.GetProperty("allWorkflows").GetBoolean());
        Assert.Equal(7, post.Body.GetProperty("payload").GetProperty("version").GetInt32());
        Assert.StartsWith("ui-", post.Body.GetProperty("idempotencyKey").GetString(), StringComparison.Ordinal);

        Assert.Equal(41L, (await harness.ValueAsync<SignalBroadcastDetailDto?>("currentBroadcast"))!.Id);
        Assert.Contains(harness.Paths, path => path.StartsWith("/api/signal-broadcasts/41/deliveries", StringComparison.Ordinal));
        Assert.Contains("Broadcast #41 froze 3 recipients", await harness.HtmlAsync(component), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_RefusesAPayloadThatIsNotAJsonObject()
    {
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        await harness.SetAsync("signalName", "claims.policy-changed");
        await harness.SetAsync("allWorkflows", true);
        await harness.SetAsync("payloadText", "[1, 2]");
        await harness.CallAsync("ReviewAsync");

        Assert.Contains("The payload must be a JSON object", await harness.HtmlAsync(component), StringComparison.Ordinal);
        Assert.Null(await harness.ValueAsync<object?>("review"));
        Assert.Empty(harness.Posts);
    }

    [Fact]
    public async Task Review_ShowsThePayloadAndAnEditEndsIt()
    {
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        await harness.SetAsync("signalName", "claims.policy-changed");
        await harness.SetAsync("allWorkflows", true);
        await harness.SetAsync("payloadText", "{ \"version\": 7 }");
        await harness.CallAsync("ReviewAsync");
        var html = await harness.HtmlAsync(component);
        Assert.Contains("signal-review-payload", html, StringComparison.Ordinal);
        Assert.Contains("&quot;version&quot;: 7", html, StringComparison.Ordinal);

        // The text area's change event runs OnPayloadEdited after binding the new text.
        await harness.SetAsync("payloadText", "{ \"version\": 8 }");
        await harness.CallAsync("OnPayloadEdited");
        Assert.Null(await harness.ValueAsync<object?>("review"));
        Assert.Null(await harness.ValueAsync<string?>("draftIdempotencyKey"));
        Assert.Contains("Review signal", await harness.HtmlAsync(component), StringComparison.Ordinal);
        Assert.Empty(harness.Posts);
    }

    [Fact]
    public async Task Throw_RefusesAPayloadChangedSinceTheReview()
    {
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        await harness.SetAsync("signalName", "claims.policy-changed");
        await harness.SetAsync("allWorkflows", true);
        await harness.SetAsync("payloadText", "{ \"version\": 7 }");
        await harness.CallAsync("ReviewAsync");

        // A click that reaches the server before the text area's change event.
        await harness.SetAsync("payloadText", "{ \"version\": 8 }");
        await harness.CallAsync("ThrowAsync");

        Assert.Empty(harness.Posts);
        Assert.Null(await harness.ValueAsync<object?>("review"));
        Assert.Contains("The payload changed after the review", await harness.HtmlAsync(component), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PermissionFailure_ClearsSignalDataAndShowsThePermissionMessage()
    {
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        await harness.SetAsync("signalName", "claims.policy-changed");
        await harness.SetAsync("workflowKey", "claims");
        await harness.SetAsync("payloadText", "{ \"version\": 7 }");
        await harness.CallAsync("OpenBroadcastAsync", 41L);
        Assert.NotNull(await harness.ValueAsync<SignalBroadcastDetailDto?>("currentBroadcast"));

        harness.Deny = true;
        await harness.CallAsync("RefreshCurrentBroadcastAsync");

        Assert.True(await harness.ValueAsync<bool>("authorizationDenied"));
        Assert.Null(await harness.ValueAsync<SignalBroadcastDetailDto?>("currentBroadcast"));
        Assert.Null(await harness.ValueAsync<PagedResult<SignalBroadcastSummaryDto>?>("history"));
        Assert.Equal(string.Empty, await harness.ValueAsync<string>("signalName"));
        Assert.Equal(string.Empty, await harness.ValueAsync<string>("workflowKey"));
        Assert.Null(await harness.ValueAsync<string?>("payloadText"));
        var html = await harness.HtmlAsync(component);
        Assert.Contains("Signal permission is required", html, StringComparison.Ordinal);
        Assert.DoesNotContain("claims.policy-changed", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdentityChange_ClearsTheDraft()
    {
        await using var harness = new Harness();
        await harness.RenderAsync();
        await harness.SetAsync("signalName", "claims.policy-changed");
        await harness.SetAsync("allWorkflows", true);
        await harness.SetAsync("payloadText", "{ \"version\": 7 }");
        await harness.CallAsync("ReviewAsync");
        Assert.NotNull(await harness.ValueAsync<object?>("review"));

        await harness.ChangeIdentityAsync("other-token");

        Assert.Equal(string.Empty, await harness.ValueAsync<string>("signalName"));
        Assert.False(await harness.ValueAsync<bool>("allWorkflows"));
        Assert.Null(await harness.ValueAsync<string?>("payloadText"));
        Assert.Null(await harness.ValueAsync<object?>("review"));
    }

    [Fact]
    public async Task LateRefreshOfAnEarlierBroadcast_DoesNotReplaceTheOneOpenedSince()
    {
        await using var harness = new Harness();
        await harness.RenderAsync();
        harness.DetailStatus = SignalBroadcastStatuses.Completed;
        await harness.CallAsync("OpenBroadcastAsync", 41L);

        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.HoldDetail41 = held.Task;
        var before = harness.Paths.Count(path => path == "/api/signal-broadcasts/41");
        var refreshing = harness.CallAsync("RefreshCurrentBroadcastAsync");
        await Harness.WaitForAsync(() => harness.Paths.Count(path => path == "/api/signal-broadcasts/41") > before);

        await harness.CallAsync("OpenBroadcastAsync", 42L);
        held.SetResult();
        await refreshing;

        Assert.Equal(42L, (await harness.ValueAsync<SignalBroadcastDetailDto?>("currentBroadcast"))!.Id);
        Assert.Equal(42L, (await harness.ValueAsync<PagedResult<SignalDeliveryDto>?>("deliveries"))!.TotalCount);
    }

    [Fact]
    public async Task LateDeliveriesForAnEarlierFilter_DoNotReplaceTheNewerPage()
    {
        await using var harness = new Harness();
        await harness.RenderAsync();
        harness.DetailStatus = SignalBroadcastStatuses.Completed;
        await harness.CallAsync("OpenBroadcastAsync", 41L);

        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.HoldUnfilteredDeliveries = held.Task;
        var before = harness.Paths.Count(IsUnfilteredDeliveries);
        var loading = harness.CallAsync("ReloadDeliveriesAsync");
        await Harness.WaitForAsync(() => harness.Paths.Count(IsUnfilteredDeliveries) > before);

        await harness.CallAsync("OnDeliveryStatusChangedAsync", new ChangeEventArgs { Value = SignalDeliveryStatuses.Failed });
        held.SetResult();
        await loading;

        Assert.Equal(7L, (await harness.ValueAsync<PagedResult<SignalDeliveryDto>?>("deliveries"))!.TotalCount);

        static bool IsUnfilteredDeliveries(string path) =>
            path.StartsWith("/api/signal-broadcasts/41/deliveries", StringComparison.Ordinal)
            && !path.Contains("status=", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Poll_RefreshesOnlyABroadcastThatStillDelivers()
    {
        await using var harness = new Harness();
        await harness.RenderAsync();
        harness.DetailStatus = SignalBroadcastStatuses.Completed;
        await harness.CallAsync("OpenBroadcastAsync", 41L);
        var before = harness.Paths.Count(path => path == "/api/signal-broadcasts/41");
        await Task.Delay(TimeSpan.FromSeconds(3.5));
        Assert.Equal(before, harness.Paths.Count(path => path == "/api/signal-broadcasts/41"));

        harness.DetailStatus = SignalBroadcastStatuses.Delivering;
        await harness.CallAsync("OpenBroadcastAsync", 41L);
        var live = harness.Paths.Count(path => path == "/api/signal-broadcasts/41");
        await Task.Delay(TimeSpan.FromSeconds(3.5));
        Assert.True(harness.Paths.Count(path => path == "/api/signal-broadcasts/41") > live);
    }

    private static SignalBroadcastDetailDto Detail(long id, string status) =>
        new(id, "claims.policy-changed", "claims", false, status, 3,
            status == SignalBroadcastStatuses.Delivering ? 3 : 0,
            status == SignalBroadcastStatuses.Delivering ? 0 : 3, 0, 0, 0,
            "admin", SignalThrowerKinds.User, ["admin"],
            JsonSerializer.SerializeToElement(new { version = 7 }),
            [new SignalRecipientCountDto("claims", 3)], false, null, null, Now, Now,
            status == SignalBroadcastStatuses.Delivering ? null : Now, null);

    private sealed record RecordedPost(string Path, JsonElement Body);

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly HtmlRenderer renderer;
        private readonly Activator activator = new();

        public ConcurrentQueue<string> PathLog { get; } = new();
        public IReadOnlyList<string> Paths => PathLog.ToArray();
        public List<RecordedPost> Posts { get; } = [];
        public volatile bool Deny;
        public string DetailStatus { get; set; } = SignalBroadcastStatuses.Delivering;

        /// <summary>When set, a request for broadcast 41's detail waits for this task.</summary>
        public Task? HoldDetail41 { get; set; }

        /// <summary>When set, a request for broadcast 41's deliveries with no filter waits for this task.</summary>
        public Task? HoldUnfilteredDeliveries { get; set; }

        private readonly TokenState token = new();

        public Harness()
        {
            var http = new HttpClient(new Handler(this)) { BaseAddress = new Uri("https://flowbit.test") };
            token.Set("signal-token");
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(new WorkflowApiClient(http));
            services.AddSingleton(token);
            services.AddSingleton<IComponentActivator>(activator);
            provider = services.BuildServiceProvider();
            renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        }

        public Task<HtmlRootComponent> RenderAsync() =>
            renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<SignalsPage>(ParameterView.Empty));

        public Task<string> HtmlAsync(HtmlRootComponent component) =>
            renderer.Dispatcher.InvokeAsync(component.ToHtmlString);

        public Task CallAsync(string method, params object?[] arguments) => renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = typeof(SignalsPage).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(activator.Page, arguments);
            if (result is Task task) await task;
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(activator.Page, null);
        });

        public Task SetAsync(string field, object? value) => renderer.Dispatcher.InvokeAsync(() =>
            typeof(SignalsPage).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(activator.Page, value));

        public Task<T> ValueAsync<T>(string field) => renderer.Dispatcher.InvokeAsync(() =>
            (T)typeof(SignalsPage).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(activator.Page)!);

        /// <summary>Sets another token; the page clears its data on the dispatcher before it reloads.</summary>
        public Task ChangeIdentityAsync(string newToken) =>
            renderer.Dispatcher.InvokeAsync(() => token.Set(newToken));

        public static async Task WaitForAsync(Func<bool> condition)
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                if (condition()) return;
                await Task.Delay(25);
            }
            Assert.Fail("The condition was not reached.");
        }

        public async ValueTask DisposeAsync()
        {
            await renderer.DisposeAsync();
            await provider.DisposeAsync();
        }

        private sealed class Activator : IComponentActivator
        {
            public SignalsPage? Page { get; private set; }

            public IComponent CreateInstance(Type componentType)
            {
                var component = (IComponent)System.Activator.CreateInstance(componentType)!;
                if (component is SignalsPage page) Page = page;
                return component;
            }
        }

        private sealed class Handler(Harness harness) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.PathAndQuery;
                harness.PathLog.Enqueue(path);
                if (harness.Deny)
                {
                    return new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = JsonContent.Create(new { error = "denied" })
                    };
                }

                if (request.Method == HttpMethod.Post)
                {
                    var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                    lock (harness.Posts) harness.Posts.Add(new RecordedPost(path, body));
                    return Json(HttpStatusCode.Accepted, Detail(41, SignalBroadcastStatuses.Delivering));
                }

                if (path.StartsWith("/api/signal-subscriptions/summary", StringComparison.Ordinal))
                {
                    return Json(HttpStatusCode.OK, new SignalSubscriptionSummaryDto(
                        [new SignalSubscriptionCountDto("claims.policy-changed", "claims", 3)], false));
                }

                if (path.StartsWith("/api/signal-broadcasts/41/deliveries", StringComparison.Ordinal))
                {
                    if (path.Contains("status=failed", StringComparison.Ordinal))
                    {
                        return Json(HttpStatusCode.OK, new PagedResult<SignalDeliveryDto>([], 1, 50, 7));
                    }
                    if (harness.HoldUnfilteredDeliveries is { } hold && !path.Contains("status=", StringComparison.Ordinal))
                    {
                        await hold.WaitAsync(cancellationToken);
                    }
                    return Json(HttpStatusCode.OK, new PagedResult<SignalDeliveryDto>([], 1, 50, 0));
                }

                if (path.StartsWith("/api/signal-broadcasts/42/deliveries", StringComparison.Ordinal))
                {
                    return Json(HttpStatusCode.OK, new PagedResult<SignalDeliveryDto>([], 1, 50, 42));
                }

                if (path == "/api/signal-broadcasts/41")
                {
                    if (harness.HoldDetail41 is { } hold)
                    {
                        await hold.WaitAsync(cancellationToken);
                    }
                    return Json(HttpStatusCode.OK, Detail(41, harness.DetailStatus));
                }

                if (path == "/api/signal-broadcasts/42")
                {
                    return Json(HttpStatusCode.OK, Detail(42, SignalBroadcastStatuses.Completed));
                }

                if (path.StartsWith("/api/signal-broadcasts?", StringComparison.Ordinal))
                {
                    return Json(HttpStatusCode.OK, new PagedResult<SignalBroadcastSummaryDto>([], 1, 25, 0));
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            private static HttpResponseMessage Json<T>(HttpStatusCode status, T value) =>
                new(status) { Content = JsonContent.Create(value) };
        }
    }
}
