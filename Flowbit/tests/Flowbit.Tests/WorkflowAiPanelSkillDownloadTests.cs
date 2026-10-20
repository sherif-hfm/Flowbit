extern alias FlowbitUi;

using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using Microsoft.JSInterop;
using WorkflowAiPanel = FlowbitUi::Flowbit.Ui.Components.Shared.WorkflowAiPanel;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using Xunit;

namespace Flowbit.Tests;

/// <summary>The panel's skill download is independent of the conversation: it never changes a run, its operation or its notices.</summary>
public sealed class WorkflowAiPanelSkillDownloadTests
{
    [Fact]
    public async Task DownloadHandsTheBrowserAZipAndLeavesARunAndItsNoticeAlone()
    {
        byte[] package = [0x50, 0x4b, 0x05, 0x06];
        var js = new RecordingJs();
        var panel = Panel(new SkillHandler(HttpStatusCode.OK, package), js);
        // A run is in progress and the panel shows a notice.
        Set(panel, "busy", true);
        Set(panel, "notice", "Applied to the editor.");
        Set(panel, "operationId", "run-1");

        await InvokeAsync(panel, "DownloadSkillAsync");

        var call = Assert.Single(js.Calls);
        Assert.Equal("flowbit.downloadFileFromStream", call.Identifier);
        Assert.Equal("flowbit-authoring.zip", call.Arguments[0]);
        Assert.Equal(package, call.Content);
        Assert.Equal("application/zip", call.Arguments[2]);
        Assert.True(Get<bool>(panel, "busy"));
        Assert.Equal("Applied to the editor.", Get<string?>(panel, "notice"));
        Assert.Equal("run-1", Get<string>(panel, "operationId"));
        Assert.Null(Get<string?>(panel, "skillError"));
        Assert.False(Get<bool>(panel, "downloadingSkill"));
    }

    [Fact]
    public async Task FailureIsReportedBesideTheButtonInsteadOfThePanelAlert()
    {
        var js = new RecordingJs();
        var panel = Panel(new SkillHandler(HttpStatusCode.Forbidden, []), js);
        Set(panel, "notice", "Applied to the editor.");

        await InvokeAsync(panel, "DownloadSkillAsync");

        Assert.Empty(js.Calls);
        Assert.Equal("Forbidden", Get<string?>(panel, "skillError"));
        Assert.Null(Get<string?>(panel, "error"));
        Assert.Equal("Applied to the editor.", Get<string?>(panel, "notice"));
        Assert.False(Get<bool>(panel, "downloadingSkill"));
    }

    private static WorkflowAiPanel Panel(HttpMessageHandler handler, IJSRuntime js)
    {
        var panel = new WorkflowAiPanel();
        SetProperty(panel, "Api", new WorkflowApiClient(new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") }));
        SetProperty(panel, "JS", js);
        return panel;
    }

    private static FieldInfo Field(object panel, string name) => panel.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static T Get<T>(object panel, string name) => (T)Field(panel, name).GetValue(panel)!;
    private static void Set(object panel, string name, object? value) => Field(panel, name).SetValue(panel, value);
    private static Task InvokeAsync(object panel, string name) =>
        (Task)panel.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(panel, [])!;
    private static void SetProperty(object panel, string name, object value) =>
        panel.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(panel, value);

    private sealed class SkillHandler(HttpStatusCode status, byte[] body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal((HttpMethod.Get, "/api/workflows/ai/skill"), (request.Method, request.RequestUri!.AbsolutePath));
            var response = new HttpResponseMessage(status);
            if (status == HttpStatusCode.OK)
            {
                response.Content = new ByteArrayContent(body);
                response.Content.Headers.ContentType = new MediaTypeHeaderValue("application/zip");
            }
            return Task.FromResult(response);
        }
    }

    private sealed record JsCall(string Identifier, object?[] Arguments, byte[]? Content);

    // Records each call and reads a streamed file before the panel disposes its stream.
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
