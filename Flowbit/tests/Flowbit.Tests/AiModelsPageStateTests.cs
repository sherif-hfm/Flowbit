extern alias FlowbitUi;

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
using AiModelsPage = FlowbitUi::Flowbit.Ui.Components.Pages.AiModels;
using TokenState = FlowbitUi::Flowbit.Ui.Auth.TokenState;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using Xunit;

namespace Flowbit.Tests;

/// <summary>The AI models page: the key is typed once, sent once and never kept or shown.</summary>
public sealed class AiModelsPageStateTests
{
    private const string Key = "sk-page-test-provider-key-0123456789";
    private static readonly AiModelSettingDto Model = new("model-1", "Flash", "https://opencode.example/zen/v1/", "glm-5.3-flash", "high", 2,
        DateTimeOffset.Parse("2026-10-01T00:00:00Z"), DateTimeOffset.Parse("2026-10-02T00:00:00Z"));

    [Fact]
    public async Task AddingAModelSendsTheKeyOnceAndClearsIt()
    {
        using var handler = new RecordingHandler(Response(Model, HttpStatusCode.Created), Response(new[] { Model }));
        var page = Page(handler);

        Invoke(page, "OpenCreate");
        Set(page, "formName", " Flash ");
        Set(page, "formEndpoint", "https://opencode.example/zen/v1");
        Set(page, "formModel", "glm-5.3-flash");
        Set(page, "formReasoning", "high");
        Set(page, "formApiKey", Key);
        await InvokeAsync(page, "SaveAsync");

        var create = handler.Requests[0];
        Assert.Equal((HttpMethod.Post, "/api/ai-models"), (create.Method, create.Path));
        using (var body = JsonDocument.Parse(create.Body!))
        {
            Assert.Equal(Key, body.RootElement.GetProperty("apiKey").GetString());
            Assert.Equal("high", body.RootElement.GetProperty("reasoningEffort").GetString());
        }
        Assert.Equal((HttpMethod.Get, "/api/ai-models"), (handler.Requests[1].Method, handler.Requests[1].Path));
        Assert.Equal("", Get<string>(page, "formApiKey"));
        Assert.False(Get<bool>(page, "formOpen"));
        Assert.Contains("Flash added", Get<string>(page, "success"));
        Assert.Equal(Model, Assert.Single(Get<IReadOnlyList<AiModelSettingDto>>(page, "models")));
    }

    [Fact]
    public async Task EditingWithABlankKeySendsNoKeyAndAConflictKeepsTheForm()
    {
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.Conflict)
        {
            Content = new StringContent("{\"error\":\"The AI model was changed by another request; reload and try again.\"}", Encoding.UTF8, "application/json")
        });
        var page = Page(handler);

        Invoke(page, "OpenEdit", Model);
        Assert.Equal("", Get<string>(page, "formApiKey"));
        Set(page, "formName", "Flash renamed");
        await InvokeAsync(page, "SaveAsync");

        using (var body = JsonDocument.Parse(Assert.Single(handler.Requests).Body!))
        {
            Assert.Equal(JsonValueKind.Null, body.RootElement.GetProperty("apiKey").ValueKind);
            Assert.Equal(2, body.RootElement.GetProperty("expectedRevision").GetInt64());
        }
        Assert.Equal("/api/ai-models/model-1", handler.Requests[0].Path);
        Assert.True(Get<bool>(page, "formOpen"));
        Assert.True(Get<bool>(page, "editConflict"));
        Assert.Equal("Flash renamed", Get<string>(page, "formName"));
        Assert.Contains("changed by another request", Get<string>(page, "formError"));
    }

    [Fact]
    public async Task DeletingAsksForConfirmationAndSendsTheRevision()
    {
        using var handler = new RecordingHandler(new HttpResponseMessage(HttpStatusCode.NoContent), Response(Array.Empty<AiModelSettingDto>()));
        var js = new ConfirmingJs();
        var page = Page(handler, js);

        await (Task)Invoke(page, "DeleteAsync", Model)!;

        Assert.Equal(["confirm"], js.Calls);
        Assert.Equal((HttpMethod.Delete, "/api/ai-models/model-1?expectedRevision=2"), (handler.Requests[0].Method, handler.Requests[0].Path));
        Assert.Contains("Flash deleted", Get<string>(page, "success"));
    }

    [Fact]
    public async Task NothingIsRequestedWithoutAnIdentity()
    {
        using var handler = new RecordingHandler();
        var page = Page(handler, token: new TokenState());

        await InvokeAsync(page, "LoadAsync");

        Assert.Empty(handler.Requests);
        Assert.Null(Get<IReadOnlyList<AiModelSettingDto>?>(page, "models"));
    }

    [Fact]
    public async Task TheListShowsTheDefaultModelAndNoKey()
    {
        using var handler = new RecordingHandler(Response(new[] { Model, Model with { Id = "model-2", Name = "Second" } }));
        var token = new TokenState();
        token.Set("admin-token");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Client(handler));
        services.AddSingleton(token);
        services.AddSingleton<IJSRuntime>(new ConfirmingJs());
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var html = await renderer.Dispatcher.InvokeAsync(async () =>
            (await renderer.RenderComponentAsync<AiModelsPage>(ParameterView.Empty)).ToHtmlString());

        Assert.Contains("Flash", html);
        Assert.Contains("Second", html);
        Assert.Single(html.Split("Assistant default", StringSplitOptions.None)[1..]);
        Assert.Contains("https://opencode.example/zen/v1/", html);
        Assert.Contains("AiAuthoring.Models", html);
        Assert.DoesNotContain("apiKey", html);
        Assert.DoesNotContain(Key, html);
    }

    private static AiModelsPage Page(HttpMessageHandler handler, IJSRuntime? js = null, TokenState? token = null)
    {
        var page = new AiModelsPage();
        SetProperty(page, "Api", Client(handler));
        if (token is null)
        {
            token = new TokenState();
            token.Set("admin-token");
        }
        SetProperty(page, "Token", token);
        SetProperty(page, "JS", js ?? new ConfirmingJs());
        return page;
    }

    private static WorkflowApiClient Client(HttpMessageHandler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") });
    private static HttpResponseMessage Response<T>(T body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(body) };
    private static FieldInfo Field(object page, string name) => page.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static T Get<T>(object page, string name) => (T)Field(page, name).GetValue(page)!;
    private static void Set(object page, string name, object? value) => Field(page, name).SetValue(page, value);
    private static object? Invoke(object page, string name, params object?[] args) =>
        page.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, args);
    private static Task InvokeAsync(object page, string name) => (Task)Invoke(page, name)!;
    private static void SetProperty(object page, string name, object value) =>
        page.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

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
