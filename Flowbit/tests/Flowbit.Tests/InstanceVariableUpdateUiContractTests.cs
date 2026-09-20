extern alias FlowbitUi;

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using InstanceDetailPage = FlowbitUi::Flowbit.Ui.Components.Pages.InstanceDetail;
using TokenState = FlowbitUi::Flowbit.Ui.Auth.TokenState;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using Xunit;

namespace Flowbit.Tests;

[Collection("instance-refresh-timing")]
public sealed class InstanceVariableUpdateUiContractTests
{
    [Fact]
    public async Task InstanceDetailRendersAdministrativeAuditAndBatchReopenLink()
    {
        var instance = InstanceWithAdministrativeUpdate();
        using var handler = new InstanceHandler(instance);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new WorkflowApiClient(http));
        services.AddSingleton(new TokenState());
        services.AddSingleton<NavigationManager>(new StubNavigationManager());
        services.AddSingleton<IJSRuntime>(new StubJsRuntime());
        services.AddSingleton<IWebHostEnvironment>(new StubEnvironment());
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<InstanceDetailPage>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(InstanceDetailPage.InstanceId)] = 42L
                }));
            return component.ToHtmlString();
        });

        Assert.Contains("/api/instances/42", handler.Paths);
        Assert.Contains("/api/instances/42/flows", handler.Paths);
        Assert.Contains("id=\"variable-updates\"", html, StringComparison.Ordinal);
        Assert.Contains("Administrative variable updates", html, StringComparison.Ordinal);
        Assert.Contains("Operation #81", html, StringComparison.Ordinal);
        Assert.Contains("Correct imported approval state", html, StringComparison.Ordinal);
        Assert.Contains("reviewContext", html, StringComparison.Ordinal);
        Assert.Contains("href=\"instance-variable-updates?batchId=91\"", html, StringComparison.Ordinal);
        Assert.Contains("Batch #91", html, StringComparison.Ordinal);
        Assert.Contains("href=\"#variable-updates\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InstanceDetailTreatsHiddenAvailableFlowsAsNoActions()
    {
        var instance = InstanceWithAdministrativeUpdate();
        using var handler = new InstanceHandler(instance, flowsNotFound: true);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new WorkflowApiClient(http));
        services.AddSingleton(new TokenState());
        services.AddSingleton<NavigationManager>(new StubNavigationManager());
        services.AddSingleton<IJSRuntime>(new StubJsRuntime());
        services.AddSingleton<IWebHostEnvironment>(new StubEnvironment());
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<InstanceDetailPage>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(InstanceDetailPage.InstanceId)] = 42L
                }));
            return component.ToHtmlString();
        });

        Assert.Contains("/api/instances/42", handler.Paths);
        Assert.Contains("/api/instances/42/flows", handler.Paths);
        Assert.Contains("Instance #42", html, StringComparison.Ordinal);
        Assert.Contains("No user actions are available.", html, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Response status code does not indicate success: 404",
            html,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdentityChangeRefreshesDisplayedActorAndPersonalActions()
    {
        var token = new TokenState();
        token.Set("test-token");
        token.ApplyResolvedContext(new ActorContextDto("admin-verifier", ["admin"]));
        using var handler = new InstanceHandler(InstanceWithAdministrativeUpdate(), availableFlows: () =>
            token.CurrentUser == "ordinary-verifier"
                ? [new SequenceFlowModel { Id = 14, SourceRef = 7, TargetRef = 9, Name = "Ordinary approval" }]
                : []);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new WorkflowApiClient(http));
        services.AddSingleton(token);
        services.AddSingleton<NavigationManager>(new StubNavigationManager());
        services.AddSingleton<IJSRuntime>(new StubJsRuntime());
        services.AddSingleton<IWebHostEnvironment>(new StubEnvironment());
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var component = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<InstanceDetailPage>(
            ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(InstanceDetailPage.InstanceId)] = 42L })));
        Assert.Contains("admin-verifier", await renderer.Dispatcher.InvokeAsync(component.ToHtmlString), StringComparison.Ordinal);

        await renderer.Dispatcher.InvokeAsync(() => token.ApplyResolvedContext(new ActorContextDto("ordinary-verifier", ["User"])));
        var html = await renderer.Dispatcher.InvokeAsync(component.ToHtmlString);

        Assert.Contains("ordinary-verifier", html, StringComparison.Ordinal);
        Assert.Contains("Ordinary approval", html, StringComparison.Ordinal);
        Assert.DoesNotContain("admin-verifier", html, StringComparison.Ordinal);
        Assert.True(handler.Paths.Count(path => path == "/api/instances/42/flows") >= 2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InFlightActionDiscoveryResponseCannotRevivePriorActorActions(bool replacementFails)
    {
        var token = new TokenState();
        token.Set("a-token");
        token.ApplyResolvedContext(new ActorContextDto("admin-verifier", ["admin"]));
        using var handler = new InFlightFlowsHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new WorkflowApiClient(http));
        services.AddSingleton(token);
        services.AddSingleton<NavigationManager>(new StubNavigationManager());
        services.AddSingleton<IJSRuntime>(new StubJsRuntime());
        services.AddSingleton<IWebHostEnvironment>(new StubEnvironment());
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var component = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<InstanceDetailPage>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(InstanceDetailPage.InstanceId)] = 42L
            })));

        Assert.Contains("Existing admin approval", await renderer.Dispatcher.InvokeAsync(component.ToHtmlString));

        // Refresh an already-rendered actor, then hold that refresh in flight.
        await renderer.Dispatcher.InvokeAsync(
            () => token.ApplyResolvedContext(new ActorContextDto("admin-verifier", ["admin"])));
        await handler.FlowsRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));

        // Replace the identity while that request is still in flight, then
        // release A's stale response. Only B's presentation and actions may
        // survive; the epoch guard must discard the in-flight response.
        await renderer.Dispatcher.InvokeAsync(
            () => token.ApplyResolvedContext(new ActorContextDto("ordinary-verifier", ["User"])));
        Assert.DoesNotContain("Existing admin approval", await renderer.Dispatcher.InvokeAsync(component.ToHtmlString));
        handler.ReleaseFlowsResponse([new SequenceFlowModel
        {
            Id = 88,
            SourceRef = 7,
            TargetRef = 9,
            Name = "Stale admin approval"
        }]);

        await handler.ReplacementRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var pendingHtml = await renderer.Dispatcher.InvokeAsync(component.ToHtmlString);
        Assert.DoesNotContain("Stale admin approval", pendingHtml);
        Assert.DoesNotContain("Existing admin approval", pendingHtml);
        Assert.Contains("Refreshing actions", pendingHtml);

        // A failed replacement must not expose a stale response cached while
        // pending. A successful replacement must display only B's own action.
        handler.ReleaseReplacement(replacementFails);
        await WaitUntilAsync(async () => !(await renderer.Dispatcher.InvokeAsync(component.ToHtmlString))
            .Contains("Refreshing actions", StringComparison.Ordinal), TimeSpan.FromSeconds(30));
        var html = await renderer.Dispatcher.InvokeAsync(component.ToHtmlString);

        Assert.Contains("ordinary-verifier", html, StringComparison.Ordinal);
        Assert.DoesNotContain("admin-verifier", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Stale admin approval", html, StringComparison.Ordinal);
        if (replacementFails)
            Assert.Contains("No user actions are available.", html, StringComparison.Ordinal);
        else
            Assert.Contains("Ordinary approval", html, StringComparison.Ordinal);

        // Initial discovery, A's held refresh, and B's replacement refresh,
        // with no extra discovery calls.
        Assert.Equal(3, handler.FlowsRequestCount);
    }

    [Fact]
    public async Task PollEligibleInstanceRefreshesOncePerTickAndStopsAfterDispose()
    {
        var token = new TokenState();
        token.Set("a-token");
        token.ApplyResolvedContext(new ActorContextDto("reviewer", ["Reviewer"]));
        using var handler = new PollCountingHandler(InstanceWithActiveMultiInstance());
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new WorkflowApiClient(http));
        services.AddSingleton(token);
        services.AddSingleton<NavigationManager>(new StubNavigationManager());
        services.AddSingleton<IJSRuntime>(new StubJsRuntime());
        services.AddSingleton<IWebHostEnvironment>(new StubEnvironment());
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var component = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<InstanceDetailPage>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(InstanceDetailPage.InstanceId)] = 42L
            })));

        Assert.Equal(1, handler.InstanceGetCount);
        Assert.Equal(1, handler.InterruptFlowGetCount);

        await WaitUntilAsync(async () => await renderer.Dispatcher.InvokeAsync(() =>
            handler.InstanceGetCount >= 2 && handler.InterruptFlowGetCount >= 2), TimeSpan.FromSeconds(30));
        Assert.Equal(2, handler.InstanceGetCount);
        Assert.Equal(2, handler.InterruptFlowGetCount);

        await renderer.DisposeAsync();
        var frozenInstanceGets = handler.InstanceGetCount;
        var frozenInterruptGets = handler.InterruptFlowGetCount;
        await Task.Delay(TimeSpan.FromSeconds(6));
        Assert.Equal(frozenInstanceGets, handler.InstanceGetCount);
        Assert.Equal(frozenInterruptGets, handler.InterruptFlowGetCount);
    }

    private static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException($"Condition was not met within {timeout.TotalSeconds:0}s.");
            }
            await Task.Delay(50);
        }
    }

    [Fact]
    public async Task NavigatingAwayDuringInitialLoadDoesNotStartPollingAfterDisposal()
    {
        using var handler = new DelayedInstanceHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new WorkflowApiClient(http));
        services.AddSingleton(new TokenState());
        services.AddSingleton<NavigationManager>(new StubNavigationManager());
        services.AddSingleton<IJSRuntime>(new StubJsRuntime());
        services.AddSingleton<IWebHostEnvironment>(new StubEnvironment());
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        NavigationProbe? probe = null;
        var rendering = renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<NavigationProbe>(
            ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(NavigationProbe.Capture)] = (Action<NavigationProbe>)(value => probe = value)
            })));
        await handler.InitialRequestStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await renderer.Dispatcher.InvokeAsync(() => probe!.NavigateAway());
        handler.InitialResponse.SetResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(InstanceWithAdministrativeUpdate())
        });
        var component = await rendering.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal("Left instance", await renderer.Dispatcher.InvokeAsync(component.ToHtmlString));
    }

    private static InstanceDetailDto InstanceWithAdministrativeUpdate()
    {
        var now = DateTimeOffset.Parse("2026-08-10T12:00:00Z");
        var value = JsonSerializer.SerializeToElement(new { approved = true });
        var outcome = new InstanceVariableUpdateOutcomeDto("reviewContext", "updated", 501, value);
        var audit = new InstanceVariableUpdateAuditDto(
            81,
            42,
            17,
            "operator",
            ["admin"],
            "Correct imported approval state",
            [outcome],
            now,
            "direct-retry",
            91,
            701);
        var workflow = new WorkflowDetailDto(
            17,
            "Purchase request",
            "purchase-request",
            2,
            true,
            false,
            now.AddDays(-1),
            new WorkflowModel { Id = "purchase-request", Name = "Purchase request" });
        var variable = new InstanceVariableDto(
            501,
            "reviewContext",
            null,
            "operator",
            value,
            now)
        {
            InstanceVariableUpdateAuditId = 81
        };

        return new InstanceDetailDto(
            42,
            workflow,
            0,
            "Completed",
            null,
            "completed",
            "PR-42",
            null,
            "starter",
            now.AddHours(-1),
            now,
            [variable],
            [],
            null,
            null,
            null)
        {
            VariableUpdates = [audit]
        };
    }

    private static InstanceDetailDto InstanceWithActiveMultiInstance()
    {
        var completed = InstanceWithAdministrativeUpdate();
        var progress = new MultiInstanceProgressDto(
            7,
            "parallel",
            "active",
            3,
            1,
            2,
            0,
            0,
            null,
            null,
            []);
        return completed with
        {
            CurrentNodeId = 2,
            CurrentNodeName = "Parallel review",
            Status = "running",
            FinishedAt = null,
            MultiInstance = progress,
            MultiInstances = [progress],
            UserTasks = new UserTaskWorkSummaryDto(true, 2, 0, 0, 0, null, null)
            {
                MultiInstanceTaskCount = 2
            }
        };
    }

    private sealed class InstanceHandler(
        InstanceDetailDto instance,
        bool flowsNotFound = false,
        Func<IReadOnlyList<SequenceFlowModel>>? availableFlows = null) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            Paths.Add(path);
            var response = path switch
            {
                "/api/instances/42" => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(instance) },
                "/api/instances/42/flows" when flowsNotFound =>
                    new HttpResponseMessage(HttpStatusCode.NotFound),
                "/api/instances/42/flows" => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(availableFlows?.Invoke() ?? []) },
                "/api/instances/42/administrative-actions?page=1&pageSize=25" => new HttpResponseMessage(HttpStatusCode.Forbidden),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
            return Task.FromResult(response);
        }
    }

    private sealed class PollCountingHandler(InstanceDetailDto instance) : HttpMessageHandler
    {
        private int instanceGets;
        private int interruptGets;

        public int InstanceGetCount => Volatile.Read(ref instanceGets);

        public int InterruptFlowGetCount => Volatile.Read(ref interruptGets);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path == "/api/instances/42")
            {
                Interlocked.Increment(ref instanceGets);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(instance)
                });
            }

            if (path == "/api/instances/42/flows")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(Array.Empty<SequenceFlowModel>())
                });
            }

            if (path.StartsWith("/api/multi-instance-executions/7/flows", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref interruptGets);
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(Array.Empty<SequenceFlowModel>())
                });
            }

            if (path.Contains("/versions", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(Array.Empty<WorkflowSummaryDto>())
                });
            }

            if (path.Contains("administrative-actions", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class InFlightFlowsHandler : HttpMessageHandler
    {
        public TaskCompletionSource FlowsRequestStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<HttpResponseMessage> FlowsResponse { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int FlowsRequestCount => Volatile.Read(ref flowsRequestCount);
        private int flowsRequestCount;
        public TaskCompletionSource ReplacementRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private TaskCompletionSource<HttpResponseMessage> ReplacementResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseReplacement(bool fails) => ReplacementResponse.TrySetResult(fails
            ? new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("Replacement failed") }
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new[]
                { new SequenceFlowModel { Id = 99, SourceRef = 7, TargetRef = 9, Name = "Ordinary approval" } }) });

        public void ReleaseFlowsResponse(IReadOnlyList<SequenceFlowModel> flows) =>
            FlowsResponse.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(flows)
            });

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            if (path == "/api/instances/42")
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(InstanceWithAdministrativeUpdate())
                });
            }

            if (path == "/api/instances/42/flows")
            {
                Interlocked.Increment(ref flowsRequestCount);
                if (FlowsRequestCount == 2)
                {
                    FlowsRequestStarted.TrySetResult();
                    return FlowsResponse.Task;
                }
                if (FlowsRequestCount == 3)
                {
                    ReplacementRequestStarted.TrySetResult();
                    return ReplacementResponse.Task;
                }

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new[]
                    { new SequenceFlowModel { Id = 77, SourceRef = 7, TargetRef = 9, Name = "Existing admin approval" } })
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class StubNavigationManager : NavigationManager
    {
        public StubNavigationManager() => Initialize("https://flowbit.test/", "https://flowbit.test/instances/42");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
        protected override void NavigateToCore(string uri, NavigationOptions options) { }
    }

    private sealed class NavigationProbe : ComponentBase
    {
        [Parameter] public Action<NavigationProbe>? Capture { get; set; }
        private bool showInstance = true;
        protected override void OnInitialized() => Capture?.Invoke(this);
        public void NavigateAway() { showInstance = false; StateHasChanged(); }
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            if (showInstance)
            {
                builder.OpenComponent<InstanceDetailPage>(0);
                builder.AddAttribute(1, nameof(InstanceDetailPage.InstanceId), 42L);
                builder.CloseComponent();
            }
            else builder.AddContent(2, "Left instance");
        }
    }

    private sealed class DelayedInstanceHandler : HttpMessageHandler
    {
        public TaskCompletionSource InitialRequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<HttpResponseMessage> InitialResponse { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.AbsolutePath == "/api/instances/42")
            {
                InitialRequestStarted.TrySetResult();
                return InitialResponse.Task;
            }
            return Task.FromResult(request.RequestUri.AbsolutePath.EndsWith("/flows", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(Array.Empty<SequenceFlowModel>()) }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class StubJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);
    }

    private sealed class StubEnvironment : IWebHostEnvironment
    {
        public string ApplicationName { get; set; } = "Flowbit.Tests";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string WebRootPath { get; set; } = string.Empty;
        public string EnvironmentName { get; set; } = "Development";
        public string ContentRootPath { get; set; } = string.Empty;
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
