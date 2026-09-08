extern alias FlowbitUi;

using System.Net;
using System.Net.Http.Json;
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

public sealed class InstanceReactivationUiContractTests
{
    [Fact]
    public async Task TerminalInstanceRendersAuthorizedReactivationPreviewAndFriendlyHistory()
    {
        var now = DateTimeOffset.Parse("2026-08-30T12:00:00Z");
        var instance = CreateTerminalInstance(now, includeReactivationHistory: true);
        var preview = new InstanceReactivationPreviewDto(
            instance.Id,
            instance.Workflow.Id,
            instance.Status,
            true,
            [new InstanceReactivationTargetDto(12, "Manager review", "manager-review", now.AddMinutes(-20))],
            [],
            [new InstanceReactivationIssueDto(
                "RetainedState",
                "Current variables and prior side effects will be retained.",
                12)],
            instance.UpdatedAt);
        using var handler = new InstanceHandler(instance, preview, HttpStatusCode.OK);

        var html = await RenderAsync(handler);

        Assert.Contains("/api/instances/42/reactivation", handler.Paths);
        Assert.Contains("id=\"reactivation\"", html, StringComparison.Ordinal);
        Assert.Contains("Reactivate instance", html, StringComparison.Ordinal);
        Assert.Contains("Variables, flow evidence, and history are preserved", html, StringComparison.Ordinal);
        Assert.Contains("Manager review", html, StringComparison.Ordinal);
        Assert.Contains("manager-review", html, StringComparison.Ordinal);
        Assert.Contains("Current variables and prior side effects will be retained.", html, StringComparison.Ordinal);
        Assert.Contains("id=\"reactivation-reason\"", html, StringComparison.Ordinal);
        Assert.Contains("id=\"reactivation-reason-help\"", html, StringComparison.Ordinal);
        Assert.Contains(
            "aria-describedby=\"reactivation-reason-help reactivation-retained-state-warning\"",
            html,
            StringComparison.Ordinal);
        Assert.Contains("Confirm reactivation", html, StringComparison.Ordinal);
        Assert.Contains(
            "Instance reactivated from completed at user task #12 (new work item #501)",
            html,
            StringComparison.Ordinal);
        Assert.Contains("Retry after correction", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TerminalInstanceRendersPreviewBlockersAndNoEligibleTargetMessage()
    {
        var now = DateTimeOffset.Parse("2026-08-30T12:00:00Z");
        var instance = CreateTerminalInstance(now, status: "cancelled");
        var preview = new InstanceReactivationPreviewDto(
            instance.Id,
            instance.Workflow.Id,
            instance.Status,
            false,
            [],
            [new InstanceReactivationIssueDto(
                "OpenRuntimeState",
                "An unresolved durable job still exists.",
                StateId: 73)],
            [],
            instance.UpdatedAt);
        using var handler = new InstanceHandler(instance, preview, HttpStatusCode.OK);

        var html = await RenderAsync(handler);

        Assert.Contains("Reactivation is blocked.", html, StringComparison.Ordinal);
        Assert.Contains("id=\"reactivation-blockers\"", html, StringComparison.Ordinal);
        Assert.Contains("OpenRuntimeState", html, StringComparison.Ordinal);
        Assert.Contains("An unresolved durable job still exists.", html, StringComparison.Ordinal);
        Assert.Contains("runtime state #73", html, StringComparison.Ordinal);
        Assert.Contains("No previously visited user task is eligible", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrunedInstance_ShowsDeletionNoticeAndFinishTimeWithoutReactivationControls()
    {
        var now = DateTimeOffset.Parse("2026-09-07T12:00:00Z");
        var instance = CreateTerminalInstance(now) with
        {
            FinishedAt = now.AddDays(-10),
            HistoryPrunedAt = now.AddHours(-1)
        };
        var preview = new InstanceReactivationPreviewDto(instance.Id, instance.Workflow.Id, instance.Status,
            false, [], [new InstanceReactivationIssueDto("HistoryPruned", "History was deleted.")], [], instance.UpdatedAt);
        using var handler = new InstanceHandler(instance, preview, HttpStatusCode.OK);

        var html = await RenderAsync(handler);

        Assert.Contains("id=\"instance-history-retention-notice\"", html);
        Assert.Contains("Some history was permanently deleted", html);
        Assert.Contains("This instance cannot be reactivated", html);
        Assert.Contains("<span>Finished</span>", html);
        Assert.DoesNotContain("id=\"reactivation\"", html);
        Assert.DoesNotContain("Variables, flow evidence, and history are preserved", html);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task UnauthorizedReactivationPreviewIsHiddenWithoutPageError(HttpStatusCode statusCode)
    {
        var now = DateTimeOffset.Parse("2026-08-30T12:00:00Z");
        var instance = CreateTerminalInstance(now);
        using var handler = new InstanceHandler(instance, null, statusCode);

        var html = await RenderAsync(handler);

        Assert.Contains("Instance #42", html, StringComparison.Ordinal);
        Assert.Contains("/api/instances/42/reactivation", handler.Paths);
        Assert.DoesNotContain("id=\"reactivation\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("administrator role is required", html, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string> RenderAsync(InstanceHandler handler)
    {
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

        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<InstanceDetailPage>(
                ParameterView.FromDictionary(new Dictionary<string, object?>
                {
                    [nameof(InstanceDetailPage.InstanceId)] = 42L
                }));
            return component.ToHtmlString();
        });
    }

    private static InstanceDetailDto CreateTerminalInstance(
        DateTimeOffset now,
        bool includeReactivationHistory = false,
        string status = "completed")
    {
        var workflow = new WorkflowDetailDto(
            17,
            "Purchase request",
            "purchase-request",
            2,
            true,
            false,
            now.AddDays(-1),
            new WorkflowModel { Id = "purchase-request", Name = "Purchase request" });
        IReadOnlyList<InstanceHistoryDto> history = includeReactivationHistory
            ? new[]
            {
                new InstanceHistoryDto(
                    91,
                    301,
                    501,
                    null,
                    null,
                    null,
                    0,
                    12,
                    "operator",
                    new Dictionary<string, JsonElement>
                    {
                        ["sourceStatus"] = JsonSerializer.SerializeToElement("completed"),
                        ["targetNodeId"] = JsonSerializer.SerializeToElement(12),
                        ["newUserTaskId"] = JsonSerializer.SerializeToElement(501),
                        ["reason"] = JsonSerializer.SerializeToElement("Retry after correction")
                    },
                    "instanceReactivated",
                    now.AddMinutes(-30))
            }
            : [];

        return new InstanceDetailDto(
            42,
            workflow,
            99,
            status.Equals("cancelled", StringComparison.OrdinalIgnoreCase) ? "Cancelled" : "Completed",
            null,
            status,
            "PR-42",
            null,
            "starter",
            now.AddHours(-1),
            now,
            [],
            history,
            null,
            null,
            null);
    }

    private sealed class InstanceHandler(
        InstanceDetailDto instance,
        InstanceReactivationPreviewDto? preview,
        HttpStatusCode previewStatus) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri?.PathAndQuery ?? string.Empty;
            Paths.Add(path);
            var response = path switch
            {
                "/api/instances/42" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(instance)
                },
                "/api/instances/42/flows" => new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(Array.Empty<SequenceFlowModel>())
                },
                "/api/instances/42/reactivation" when previewStatus == HttpStatusCode.OK =>
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = JsonContent.Create(preview)
                    },
                "/api/instances/42/reactivation" => new HttpResponseMessage(previewStatus)
                {
                    Content = JsonContent.Create(new { error = "The administrator role is required." })
                },
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
            return Task.FromResult(response);
        }
    }

    private sealed class StubNavigationManager : NavigationManager
    {
        public StubNavigationManager() => Initialize(
            "https://flowbit.test/",
            "https://flowbit.test/instances/42");

        protected override void NavigateToCore(string uri, bool forceLoad) { }
        protected override void NavigateToCore(string uri, NavigationOptions options) { }
    }

    private sealed class StubJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            ValueTask.FromResult(default(TValue)!);

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) => ValueTask.FromResult(default(TValue)!);
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
