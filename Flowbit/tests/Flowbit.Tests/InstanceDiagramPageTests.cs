extern alias FlowbitUi;

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
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

public sealed class InstanceDiagramPageTests
{
    [Fact]
    public async Task InstanceDetailRendersTheDiagramWithoutRequestingNodeExecutions()
    {
        var instance = InstanceDiagramOverlayTests.Instance("running",
            [InstanceDiagramOverlayTests.Position(1, 3, BpmnFlowNodeTypes.UserTask, "active", task: 501)],
            definition: new WorkflowModel
            {
                Id = "purchase-review",
                Name = "Purchase review",
                FlowNodes = [new FlowNodeModel { Id = 3, Name = "Review request", Type = BpmnFlowNodeTypes.UserTask }]
            });
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

        Assert.Contains("id=\"diagram\"", html, StringComparison.Ordinal);
        Assert.Contains("Select a highlighted node", html, StringComparison.Ordinal);
        Assert.True(
            html.IndexOf("id=\"instance-summary-heading\"", StringComparison.Ordinal)
                < html.IndexOf("id=\"diagram\"", StringComparison.Ordinal),
            "The diagram follows the instance summary.");
        Assert.Contains("/api/instances/42", handler.Paths);
        Assert.DoesNotContain(handler.Paths, path => path.StartsWith("/api/node-executions", StringComparison.Ordinal));
    }

    // Every status, also while running: an async-after wait follows a completed visit.
    [Theory]
    [InlineData("running")]
    [InlineData("completed")]
    public async Task SelectingANodeLooksUpTheExecutionsTheCallerMayRead(string status)
    {
        var handler = new SearchHandler();
        handler.Respond(3, () => Found(60, InstanceDiagramComponentTests.Execution(9001, token: 1, node: 3, status: "active")));
        var page = Page(handler, status);

        await SelectAsync(page, 3);

        using var body = JsonDocument.Parse(Assert.Single(handler.Bodies));
        var request = body.RootElement;
        Assert.Equal(42, request.GetProperty("instanceId").GetInt64());
        Assert.Equal(3, request.GetProperty("nodeId").GetInt32());
        Assert.Equal(JsonValueKind.Null, request.GetProperty("statuses").ValueKind);
        var sort = Assert.Single(request.GetProperty("sort").EnumerateArray());
        Assert.Equal(("id", "desc"), (sort.GetProperty("field").GetString(), sort.GetProperty("direction").GetString()));
        Assert.Equal((1, 50), (request.GetProperty("page").GetInt32(), request.GetProperty("pageSize").GetInt32()));

        Assert.Equal(9001, Assert.Single(Get<IReadOnlyList<NodeExecutionSummaryDto>>(page, "diagramNodeExecutions")).Id);
        Assert.Equal(60, Get<long>(page, "diagramNodeExecutionTotal"));
        Assert.False(Get<bool>(page, "diagramNodeExecutionsLoading"));
    }

    [Fact]
    public async Task AFailedLookupOnlyLeavesTheDiagramWithoutExecutionLinks()
    {
        var handler = new SearchHandler();
        handler.Respond(3, () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));
        var page = Page(handler, "running");

        await SelectAsync(page, 3);

        Assert.Empty(Get<IReadOnlyList<NodeExecutionSummaryDto>>(page, "diagramNodeExecutions"));
        Assert.Equal(0, Get<long>(page, "diagramNodeExecutionTotal"));
        Assert.False(Get<bool>(page, "diagramNodeExecutionsLoading"));
    }

    [Fact]
    public async Task AnAnswerForAnEarlierSelectionIsIgnored()
    {
        var handler = new SearchHandler();
        var earlier = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.Respond(3, () => earlier.Task);
        handler.Respond(4, () => Found(1, InstanceDiagramComponentTests.Execution(9400, token: 2, node: 4, status: "active")));
        var page = Page(handler, "running");

        var first = SelectAsync(page, 3);
        Assert.True(Get<bool>(page, "diagramNodeExecutionsLoading"));
        await SelectAsync(page, 4);
        earlier.SetResult(await Found(1, InstanceDiagramComponentTests.Execution(9001, token: 1, node: 3, status: "active")));
        await first;

        Assert.Equal(4, Get<int?>(page, "selectedDiagramNodeId"));
        Assert.Equal(9400, Assert.Single(Get<IReadOnlyList<NodeExecutionSummaryDto>>(page, "diagramNodeExecutions")).Id);
        Assert.False(Get<bool>(page, "diagramNodeExecutionsLoading"));
    }

    private static InstanceDetailPage Page(HttpMessageHandler handler, string status)
    {
        var page = new InstanceDetailPage();
        page.GetType().GetProperty("Api", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(page, new WorkflowApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") }));
        Field(page, "instance").SetValue(page, InstanceDiagramOverlayTests.Instance(status, []));
        return page;
    }

    private static Task SelectAsync(InstanceDetailPage page, int? nodeId) =>
        (Task)page.GetType().GetMethod("OnDiagramNodeSelectedAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(page, [nodeId])!;

    private static FieldInfo Field(object page, string name) =>
        page.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static T Get<T>(object page, string name) => (T)Field(page, name).GetValue(page)!;

    private static Task<HttpResponseMessage> Found(long total, params NodeExecutionSummaryDto[] items) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new PagedResult<NodeExecutionSummaryDto>(items, 1, 50, total))
        });

    // Answers node-execution searches per selected node and records each request body.
    private sealed class SearchHandler : HttpMessageHandler
    {
        private readonly Dictionary<int, Func<Task<HttpResponseMessage>>> responses = [];

        public List<string> Bodies { get; } = [];

        public void Respond(int nodeId, Func<Task<HttpResponseMessage>> response) => responses[nodeId] = response;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = await request.Content!.ReadAsStringAsync(cancellationToken);
            // An earlier request may still be running on another thread.
            lock (Bodies) Bodies.Add(body);
            using var json = JsonDocument.Parse(body);
            return await responses[json.RootElement.GetProperty("nodeId").GetInt32()]();
        }
    }

    private sealed class InstanceHandler(InstanceDetailDto instance) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.AbsolutePath ?? string.Empty;
            Paths.Add(path);
            return Task.FromResult(path switch
            {
                "/api/instances/42" => new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(instance) },
                "/api/instances/42/flows" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(Array.Empty<SequenceFlowModel>())
                },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            });
        }
    }

    private sealed class StubNavigationManager : NavigationManager
    {
        public StubNavigationManager() => Initialize("https://flowbit.test/", "https://flowbit.test/instances/42");
        protected override void NavigateToCore(string uri, bool forceLoad) { }
        protected override void NavigateToCore(string uri, NavigationOptions options) { }
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
