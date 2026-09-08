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
using RetentionPage = FlowbitUi::Flowbit.Ui.Components.Pages.Retention;
using TokenState = FlowbitUi::Flowbit.Ui.Auth.TokenState;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using WorkflowApiException = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiException;
using Xunit;

namespace Flowbit.Tests;

public sealed class RetentionUiTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-07T10:00:00Z");
    private static RetentionPolicyDto Policy => new(RetentionCategories.WorkflowHistory, null, 7, Now, "admin", true);
    private static RetentionRunDto Run => new(Guid.Parse("087a6c0d-b867-4c8b-96e3-6539c7ec7f0c"), "queued", Now, "admin", null, null, null, []);
    private static RetentionStatusDto Status => new([Policy], null, null, Now.AddHours(1), Now);

    [Fact]
    public async Task ClientContracts_PreserveDraftNullAndRevision_AndRunSendsNoPolicyOverrides()
    {
        var preview = new RetentionPreviewDto(RetentionCategories.WorkflowHistory, Now.AddDays(-5),
            [new("instance_history", 12, 3)], true, Now);
        using var handler = new RecordingHandler(Response(Status), Response(Policy), Response(preview), Response(Run, HttpStatusCode.Accepted));
        var client = Client(handler);

        Assert.Equal(Now, (await client.GetRetentionAsync()).WorkerLastSeenAt);
        Assert.Equal(Policy, await client.UpdateRetentionPolicyAsync(Policy.Category, new(null, Policy.Revision)));
        Assert.Equal(12, Assert.Single((await client.PreviewRetentionAsync(new(Policy.Category, 5))).Tables).EligibleCount);
        Assert.Equal(Run.Id, (await client.RequestRetentionRunAsync()).Id);

        Assert.Equal(4, handler.Requests.Count);
        Assert.Equal((HttpMethod.Get, "/api/retention"), (handler.Requests[0].Method, handler.Requests[0].Path));
        var update = handler.Requests[1];
        Assert.Equal((HttpMethod.Put, "/api/retention/policies/workflowHistory"), (update.Method, update.Path));
        using var updateBody = JsonDocument.Parse(update.Body!);
        Assert.Equal(JsonValueKind.Null, updateBody.RootElement.GetProperty("retentionDays").ValueKind);
        Assert.Equal(7, updateBody.RootElement.GetProperty("expectedRevision").GetInt64());
        var draft = handler.Requests[2];
        Assert.Equal((HttpMethod.Post, "/api/retention/preview"), (draft.Method, draft.Path));
        using var draftBody = JsonDocument.Parse(draft.Body!);
        Assert.Equal("workflowHistory", draftBody.RootElement.GetProperty("category").GetString());
        Assert.Equal(5, draftBody.RootElement.GetProperty("retentionDays").GetInt32());
        var run = handler.Requests[3];
        Assert.Equal((HttpMethod.Post, "/api/retention/runs"), (run.Method, run.Path));
        Assert.Null(run.Body);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.Conflict)]
    public async Task ClientErrors_RetainStatusAndServerDetail(HttpStatusCode status)
    {
        using var handler = new RecordingHandler(Response(new { detail = "Policy request rejected." }, status));
        var error = await Assert.ThrowsAsync<WorkflowApiException>(() =>
            Client(handler).UpdateRetentionPolicyAsync(Policy.Category, new(3, 7)));
        Assert.Equal(status, error.StatusCode);
        Assert.Equal("Policy request rejected.", error.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("1e2")]
    [InlineData("36501")]
    public async Task InvalidPeriod_DoesNotPreviewOrSave(string input)
    {
        using var handler = new RecordingHandler();
        var page = Page(handler);
        Invoke(page, "OpenEdit", Policy);
        Set(page, "retentionMode", "days");
        Set(page, "formDays", input);

        await InvokeAsync(page, "PreviewAsync");
        await InvokeAsync(page, "SaveAsync");

        Assert.Empty(handler.Requests);
        Assert.Contains("positive whole number", Get<string>(page, "formError"));
    }

    [Fact]
    public async Task Conflict_PreservesDraftAndRequiresExplicitReload()
    {
        using var handler = new RecordingHandler(Response(new { detail = "Changed" }, HttpStatusCode.Conflict));
        var page = Page(handler);
        Invoke(page, "OpenEdit", Policy);
        Set(page, "retentionMode", "days");
        Set(page, "formDays", "45");

        await InvokeAsync(page, "SaveAsync");
        await InvokeAsync(page, "SaveAsync");

        Assert.Single(handler.Requests);
        Assert.Equal(Policy.Category, Get<string>(page, "editingCategory"));
        Assert.Equal("45", Get<string>(page, "formDays"));
        Assert.Equal(7L, Get<long>(page, "expectedRevision"));
        Assert.True(Get<bool>(page, "editConflict"));
        Assert.Contains("edits are preserved", Get<string>(page, "formError"));
        Assert.False(Get<bool>(page, "submitting"));
    }

    [Fact]
    public async Task IdentityReset_ClearsSensitiveState_AndDiscardsInFlightPreview()
    {
        using var handler = new BlockingHandler();
        var page = Page(handler);
        Set(page, "status", Status);
        Invoke(page, "OpenEdit", Policy);
        var pending = InvokeAsync(page, "PreviewAsync");
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));

        Invoke(page, "ResetIdentityState");
        Set(page, "success", "new identity state");
        handler.Release.TrySetResult();
        await pending;

        Assert.Null(Get<object?>(page, "status"));
        Assert.Null(Get<object?>(page, "preview"));
        Assert.Null(Get<string?>(page, "editingCategory"));
        Assert.Equal("new identity state", Get<string>(page, "success"));
        Assert.False(Get<bool>(page, "previewing"));
    }

    [Fact]
    public void WorkerWarningUsesObservationTime_NotTimeSpentEditingAnOldSnapshot()
    {
        var page = new RetentionPage();
        Set(page, "status", Status);
        Set(page, "statusObservedAt", Now.AddSeconds(10));
        var warning = typeof(RetentionPage).GetProperty("WorkerWasStaleAtRefresh", BindingFlags.Instance | BindingFlags.NonPublic)!;

        Assert.False((bool)warning.GetValue(page)!);
        Set(page, "statusObservedAt", Now.AddSeconds(61));
        Assert.True((bool)warning.GetValue(page)!);
        Set(page, "statusObservedAt", null);
        Assert.False((bool)warning.GetValue(page)!);
    }

    [Fact]
    public async Task MissingIdentity_DoesNotLoadData()
    {
        using var handler = new RecordingHandler();
        var page = Page(handler);
        SetProperty(page, "Token", new TokenState());
        Set(page, "status", Status);
        await InvokeAsync(page, "LoadAsync");
        Assert.Empty(handler.Requests);
        Assert.Null(Get<object?>(page, "status"));
    }

    [Fact]
    public async Task RunNow_PreservesDraft_AndQueuesSavedPoliciesOnly()
    {
        using var handler = new RecordingHandler(Response(Run, HttpStatusCode.Accepted));
        var page = Page(handler);
        Set(page, "status", Status);
        Invoke(page, "OpenEdit", Policy);
        Set(page, "retentionMode", "days");
        Set(page, "formDays", "1");

        await InvokeAsync(page, "RunNowAsync");

        Assert.Equal("1", Get<string>(page, "formDays"));
        Assert.Null(Assert.Single(handler.Requests).Body);
        Assert.Equal(Run.Id, Get<RetentionStatusDto>(page, "status").CurrentRun!.Id);
        Assert.Contains("Unsaved edits are not included", Get<string>(page, "success"));
    }

    [Fact]
    public async Task TokenChangeRendersLoadingThenReloadedPoliciesWithoutAnotherUserInteraction()
    {
        using var handler = new BlockingStatusHandler();
        var token = new TokenState();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Client(handler));
        services.AddSingleton(token);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var component = await renderer.Dispatcher.InvokeAsync(() =>
            renderer.RenderComponentAsync<RetentionPage>(ParameterView.Empty));
        Assert.Contains("An administrative identity is required", await renderer.Dispatcher.InvokeAsync(component.ToHtmlString));

        await renderer.Dispatcher.InvokeAsync(() => token.Set("new-admin-token"));
        await handler.Started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        var loadingHtml = await renderer.Dispatcher.InvokeAsync(component.ToHtmlString);
        Assert.Contains("class=\"skeleton\"", loadingHtml);
        Assert.DoesNotContain("An administrative identity is required", loadingHtml);

        handler.Release.TrySetResult();
        string loadedHtml = string.Empty;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            loadedHtml = await renderer.Dispatcher.InvokeAsync(component.ToHtmlString);
            if (loadedHtml.Contains("Workflow events and flow history", StringComparison.Ordinal)) break;
            await Task.Delay(10);
        }
        Assert.Contains("Workflow events and flow history", loadedHtml);
        Assert.Contains("Keep forever", loadedHtml);
        Assert.DoesNotContain("class=\"skeleton\"", loadedHtml);

        await renderer.Dispatcher.InvokeAsync(token.Clear);
        var clearedHtml = await renderer.Dispatcher.InvokeAsync(component.ToHtmlString);
        Assert.Contains("An administrative identity is required", clearedHtml);
        Assert.DoesNotContain("Workflow events and flow history", clearedHtml);
    }

    [Fact]
    public async Task Render_ShowsPendingInitializationAndPermanentDeletionContract()
    {
        var policies = RetentionCategories.All.Select(category => new RetentionPolicyDto(category, null, 1, Now, null,
            category is not (RetentionCategories.CompletedJobs or RetentionCategories.ResolvedIncidents))).ToArray();
        using var handler = new RecordingHandler(Response(new RetentionStatusDto(policies, null, null, null, null)));
        var token = new TokenState();
        token.Set("test-admin-token");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(Client(handler));
        services.AddSingleton(token);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        var html = await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var rendered = await renderer.RenderComponentAsync<RetentionPage>(ParameterView.Empty);
            return rendered.ToHtmlString();
        });

        Assert.Contains("Deletion is permanent", html);
        Assert.Contains("cannot be reactivated", html);
        Assert.Contains("Keep forever", html);
        Assert.Contains("Awaiting Worker initialization", html);
        Assert.Contains("saved policies", html);
        Assert.Equal(7, html.Split("class=\"panel retention-policy\"", StringSplitOptions.None).Length - 1);
    }

    private static RetentionPage Page(HttpMessageHandler handler)
    {
        var page = new RetentionPage();
        SetProperty(page, "Api", Client(handler));
        var token = new TokenState();
        token.Set("admin-token");
        SetProperty(page, "Token", token);
        return page;
    }

    private static WorkflowApiClient Client(HttpMessageHandler handler) => new(new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") });
    private static HttpResponseMessage Response<T>(T body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = JsonContent.Create(body) };
    private static FieldInfo Field(object page, string name) => page.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static T Get<T>(object page, string name) => (T)Field(page, name).GetValue(page)!;
    private static void Set(object page, string name, object? value) => Field(page, name).SetValue(page, value);
    private static object? Invoke(object page, string name, params object?[] args) => page.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, args);
    private static Task InvokeAsync(object page, string name) => (Task)Invoke(page, name)!;
    private static void SetProperty(object page, string name, object value) => page.GetType().GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

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

    private sealed class BlockingHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return Response(new RetentionPreviewDto(RetentionCategories.WorkflowHistory, null, [new("instance_history", 12, 3)], false, Now));
        }
    }

    private sealed class BlockingStatusHandler : HttpMessageHandler
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Started.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return Response(Status);
        }
    }
}
