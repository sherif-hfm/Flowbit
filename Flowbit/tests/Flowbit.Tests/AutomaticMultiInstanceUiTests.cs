extern alias FlowbitUi;

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ResultsComponent = FlowbitUi::Flowbit.Ui.Components.Shared.InstanceDetails.InstanceAutomaticMultiInstanceResults;
using TokenState = FlowbitUi::Flowbit.Ui.Auth.TokenState;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using Xunit;

namespace Flowbit.Tests;

public sealed class AutomaticMultiInstanceUiTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-11T12:00:00Z");

    [Fact]
    public async Task ProgressDoesNotFetchPayloadsOrOfferHumanActions()
    {
        using var handler = new PendingHandler();
        using var client = new WorkflowApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://example.test") });
        var services = new ServiceCollection().AddLogging().AddSingleton(client).AddSingleton(new TokenState())
            .AddSingleton<Microsoft.JSInterop.IJSRuntime, UnusedJsRuntime>();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<ResultsComponent>(ParameterView.FromDictionary(new Dictionary<string, object?>
            {
                [nameof(ResultsComponent.InstanceId)] = 42L,
                [nameof(ResultsComponent.Executions)] = new[] { new AutomaticMultiInstanceProgressDto(
                    7, 8, 2, "serviceTask", "parallel", "active", "waiting", 1000, 400, 3, 596, 1, 0, 4,
                    true, "itemIncident", Now, Now, null)
                { ControlJobId = 71, ControlIncidentId = 72, ControlIncidentType = "output_version_conflict" } }
            }));
            return rendered.ToHtmlString();
        });
        Assert.Contains("400 / 1000 completed", html);
        Assert.Contains("Admission paused by an item incident", html);
        Assert.Contains("Concurrency 4", html);
        Assert.Contains("Needs attention", html);
        Assert.Contains("output_version_conflict", html);
        Assert.Contains("Control job #71", html);
        Assert.Contains("Incident #72", html);
        Assert.DoesNotContain("Claim", html);
        Assert.DoesNotContain("Submitted data", html);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task OutOfOrderPagesCannotReplaceCurrentPage()
    {
        using var handler = new PendingHandler();
        using var client = new WorkflowApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://example.test") });
        using var component = Component(client, new TokenState());
        var first = Invoke(component, "LoadItemsAsync", 7L, 1);
        var second = Invoke(component, "LoadItemsAsync", 7L, 2);
        Assert.Equal("/api/multi-instance-executions/7/items?page=2&pageSize=50", handler.Requests[1].Path);
        handler.Requests[1].Reply.SetResult(Response(new PagedResult<AutomaticMultiInstanceItemSummaryDto>([Item(51, 50)], 2, 50, 100)));
        await second;
        handler.Requests[0].Reply.SetResult(Response(new PagedResult<AutomaticMultiInstanceItemSummaryDto>([Item(1, 0)], 1, 50, 100)));
        await first;
        Assert.Equal(2, Field<PagedResult<AutomaticMultiInstanceItemSummaryDto>>(component, "items")!.Page);
    }

    [Fact]
    public async Task IdentityChangeDropsPayloadAndUnauthorizedSummaryNeverRequestsIt()
    {
        using var handler = new PendingHandler();
        using var client = new WorkflowApiClient(new HttpClient(handler) { BaseAddress = new Uri("http://example.test") });
        var token = new TokenState();
        token.Set("first-identity");
        using var component = Component(client, token);
        typeof(ResultsComponent).GetField("selectedExecutionId", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, 7L);
        await Invoke(component, "LoadDetailAsync", Item(1, 0) with { CanReadPayload = false });
        Assert.Empty(handler.Requests);
        var pending = Invoke(component, "LoadDetailAsync", Item(1, 0));
        token.Set("replacement-identity");
        handler.Requests[0].Reply.SetResult(Response(new AutomaticMultiInstanceItemDetailDto(
            Item(1, 0), JsonSerializer.SerializeToElement("protected item"), JsonSerializer.SerializeToElement(new { receipt = "protected output" }), null, null)));
        await pending;
        Assert.Null(Field<AutomaticMultiInstanceItemDetailDto>(component, "detail"));
    }

    private static AutomaticMultiInstanceItemSummaryDto Item(long id, int index) =>
        new(id, 42, 7, index, "serviceTask", "completed", "completed", Now, Now, Now, Now, 99, null, 100)
        { CanReadPayload = true };

    private static ResultsComponent Component(WorkflowApiClient client, TokenState token)
    {
        var component = new ResultsComponent { InstanceId = 42 };
        typeof(ResultsComponent).GetProperty("Api", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, client);
        typeof(ResultsComponent).GetProperty("Token", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, token);
        return component;
    }
    private static Task Invoke(ResultsComponent component, string method, params object[] args) =>
        (Task)typeof(ResultsComponent).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, args)!;
    private static T? Field<T>(ResultsComponent component, string name) where T : class =>
        (T?)typeof(ResultsComponent).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component);
    private static HttpResponseMessage Response<T>(T body) => new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };

    private sealed class PendingHandler : HttpMessageHandler
    {
        public List<(string Path, TaskCompletionSource<HttpResponseMessage> Reply)> Requests { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var reply = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
            Requests.Add((request.RequestUri!.PathAndQuery, reply));
            return reply.Task;
        }
    }
    private sealed class UnusedJsRuntime : Microsoft.JSInterop.IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => throw new NotSupportedException();
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => throw new NotSupportedException();
    }
}
