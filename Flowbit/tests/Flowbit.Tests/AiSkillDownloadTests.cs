extern alias FlowbitUi;

using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using AiSkillDownload = FlowbitUi::Flowbit.Ui.Components.Shared.AiSkillDownload;
using TokenState = FlowbitUi::Flowbit.Ui.Auth.TokenState;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using Xunit;

namespace Flowbit.Tests;

/// <summary>The skill download shared by the Workflows page and the editor's AI panel.</summary>
public sealed class AiSkillDownloadTests
{
    private static readonly byte[] Package = [0x50, 0x4b, 0x05, 0x06];

    [Fact]
    public async Task DownloadHandsTheBrowserAZip()
    {
        var js = new RecordingJs();
        var component = Component(new SkillHandler(HttpStatusCode.OK), js);

        await DownloadAsync(component);

        var call = Assert.Single(js.Calls);
        Assert.Equal("flowbit.downloadFileFromStream", call.Identifier);
        Assert.Equal("flowbit-authoring.zip", call.Arguments[0]);
        Assert.Equal(Package, call.Content);
        Assert.Equal("application/zip", call.Arguments[2]);
        Assert.Null(Get<string?>(component, "error"));
        Assert.False(Get<bool>(component, "downloading"));
    }

    [Fact]
    public async Task FailureIsReportedBesideTheButton()
    {
        var js = new RecordingJs();
        var component = Component(new SkillHandler(HttpStatusCode.Forbidden), js);

        await DownloadAsync(component);

        Assert.Empty(js.Calls);
        Assert.Equal("Forbidden", Get<string?>(component, "error"));
        Assert.False(Get<bool>(component, "downloading"));
    }

    [Fact]
    public async Task ADownloadRequestedByThePreviousIdentityNeverReachesTheBrowser()
    {
        var js = new RecordingJs();
        AiSkillDownload? component = null;
        // The identity changes while the API request is in flight.
        component = Component(new SkillHandler(HttpStatusCode.OK, () => Set(component!, "identityRevision", 1L)), js);

        await DownloadAsync(component);

        Assert.Empty(js.Calls);
        Assert.Null(Get<string?>(component, "error"));
    }

    [Fact]
    public async Task RendersTheContractBothPagesRelyOnAndUnsubscribesWhenDisposed()
    {
        var token = new TokenState();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(new WorkflowApiClient(new HttpClient(new SkillHandler(HttpStatusCode.OK)) { BaseAddress = new Uri("https://flowbit.test") }));
        services.AddSingleton<IJSRuntime>(new RecordingJs());
        services.AddSingleton(token);
        var changed = typeof(TokenState).GetField("Changed", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await using (var provider = services.BuildServiceProvider())
        {
            await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var html = await renderer.Dispatcher.InvokeAsync(async () =>
                (await renderer.RenderComponentAsync<AiSkillDownload>()).ToHtmlString());

            Assert.Contains("id=\"ai-skill-download\"", html);
            Assert.Contains("aria-describedby=\"ai-skill-help\"", html);
            Assert.Contains(">Download Flowbit AI skill</button>", html);
            Assert.Contains("id=\"ai-skill-help\"", html);
            Assert.Contains(".agents/skills", html);
            Assert.DoesNotContain("ai-skill-error", html);
            Assert.NotNull(changed.GetValue(token));
        }
        Assert.Null(changed.GetValue(token));
    }

    private static AiSkillDownload Component(HttpMessageHandler handler, IJSRuntime js)
    {
        var component = new AiSkillDownload();
        SetProperty(component, "Api", new WorkflowApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") }));
        SetProperty(component, "JS", js);
        return component;
    }

    private static Task DownloadAsync(AiSkillDownload component) =>
        (Task)typeof(AiSkillDownload).GetMethod("DownloadAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(component, [])!;
    private static FieldInfo Field(string name) => typeof(AiSkillDownload).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static T Get<T>(AiSkillDownload component, string name) => (T)Field(name).GetValue(component)!;
    private static void Set(AiSkillDownload component, string name, object? value) => Field(name).SetValue(component, value);
    private static void SetProperty(AiSkillDownload component, string name, object value) =>
        typeof(AiSkillDownload).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(component, value);

    private sealed class SkillHandler(HttpStatusCode status, Action? beforeResponse = null) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal((HttpMethod.Get, "/api/workflows/ai/skill"), (request.Method, request.RequestUri!.AbsolutePath));
            beforeResponse?.Invoke();
            var response = new HttpResponseMessage(status);
            if (status == HttpStatusCode.OK)
            {
                response.Content = new ByteArrayContent(Package);
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            }
            return Task.FromResult(response);
        }
    }

    private sealed record JsCall(string Identifier, object?[] Arguments, byte[]? Content);

    // Records each call and reads a streamed file before the component disposes its stream.
    private sealed class RecordingJs : IJSRuntime
    {
        public List<JsCall> Calls { get; } = [];

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args)
        {
            byte[]? content = null;
            if (args is [_, DotNetStreamReference reference, ..])
            {
                using var copy = new MemoryStream();
                reference.Stream.CopyTo(copy);
                content = copy.ToArray();
            }
            Calls.Add(new(identifier, args ?? [], content));
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            InvokeAsync<TValue>(identifier, args);
    }
}
