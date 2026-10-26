extern alias FlowbitUi;

using System.Net;
using System.Reflection;
using System.Text.RegularExpressions;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using DiagramComponent = FlowbitUi::Flowbit.Ui.Components.Shared.InstanceDiagram;
using Xunit;

namespace Flowbit.Tests;

public sealed class InstanceDiagramComponentTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-10-26T09:00:00Z");

    [Fact]
    public async Task RendersTheLegendAndAnEmptyViewerElementWithoutASelection()
    {
        var html = await RenderAsync(RunningReview(), selectedNodeId: null, executions: null);

        Assert.Contains("id=\"diagram\"", html, StringComparison.Ordinal);
        Assert.Contains("data-diagram-ready=\"false\"", html, StringComparison.Ordinal);
        Assert.Contains("Workflow version 3.", html, StringComparison.Ordinal);
        Assert.Contains("Active or waiting", html, StringComparison.Ordinal);
        Assert.Contains("Ended here", html, StringComparison.Ordinal);
        Assert.Contains("Faulted or cancelled here", html, StringComparison.Ordinal);
        Assert.Matches(new Regex("<div class=\"flowbit-editor instance-diagram-canvas\"[^>]*></div>"), html);
        Assert.Contains("Select a highlighted node", html, StringComparison.Ordinal);
        Assert.DoesNotContain("user-tasks/", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SelectedUserTaskLinksToItsTaskAndVisibleNodeExecution()
    {
        var html = await RenderAsync(RunningReview(), selectedNodeId: 3,
        [
            Execution(9001, token: 1, node: 3, status: "active", userTask: 501),
            Execution(8000, token: 1, node: 3, status: "completed", userTask: 400),
            Execution(9100, token: 1, node: 4, status: "active")
        ]);

        Assert.Contains("Review request", html, StringComparison.Ordinal);
        Assert.Contains("Node #3 · User task · TASK_REVIEW", html, StringComparison.Ordinal);
        Assert.Contains("Waiting for user work", html, StringComparison.Ordinal);
        Assert.Contains("href=\"user-tasks/501\"", html, StringComparison.Ordinal);
        Assert.Contains("Open task #501", html, StringComparison.Ordinal);
        Assert.Contains("href=\"node-executions/9001\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("node-executions/8000", html, StringComparison.Ordinal);
        Assert.DoesNotContain("node-executions/9100", html, StringComparison.Ordinal);
        Assert.DoesNotContain("are available to you", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutVisibleExecutionsOnlyTheTaskLinkRemains()
    {
        var hidden = await RenderAsync(RunningReview(), selectedNodeId: 3, executions: []);
        Assert.Contains("href=\"user-tasks/501\"", hidden, StringComparison.Ordinal);
        Assert.DoesNotContain("node-executions/", hidden, StringComparison.Ordinal);
        Assert.Contains("No node execution details for this node are available to you.", hidden, StringComparison.Ordinal);

        var loading = await RenderAsync(RunningReview(), selectedNodeId: 3, executions: null, loading: true);
        Assert.Contains("Loading execution details", loading, StringComparison.Ordinal);
        Assert.DoesNotContain("are available to you", loading, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MultiInstanceNodeListsItsFirstVisibleItemsAndProgress()
    {
        var instance = InstanceDiagramOverlayTests.Instance("running",
            [InstanceDiagramOverlayTests.Position(1, 3, BpmnFlowNodeTypes.UserTask, "active", task: 600, multiInstance: 77)],
            multiInstances: [InstanceDiagramOverlayTests.Progress(77, total: 7, completed: 1, active: 6, pending: 0)],
            definition: Definition());
        var items = Enumerable.Range(0, 7)
            .Select(index => Execution(9000 + index, token: 1, node: 3, status: index == 0 ? "completed" : "active",
                userTask: 600 + index, multiInstance: 77, itemIndex: index, owner: $"reviewer{index}"))
            .Reverse()
            .ToList();

        var html = await RenderAsync(instance, selectedNodeId: 3, items);

        Assert.Contains("1 of 7 completed · 6 active", html, StringComparison.Ordinal);
        Assert.Contains("Item 0 · reviewer0", html, StringComparison.Ordinal);
        Assert.Contains("href=\"node-executions/9004\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"user-tasks/604\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("node-executions/9005", html, StringComparison.Ordinal);
        Assert.Contains("and 2 more visible work items", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Open a work item", html, StringComparison.Ordinal);

        // A lookup cut short by its page size cannot tell how many more there are.
        var truncated = await RenderAsync(instance, selectedNodeId: 3, items, total: 60);
        Assert.Contains("and at least 2 more visible work items", truncated, StringComparison.Ordinal);

        var withoutItems = await RenderAsync(instance, selectedNodeId: 3, executions: []);
        Assert.Contains("href=\"user-tasks/600\"", withoutItems, StringComparison.Ordinal);
        Assert.Contains("Open a work item (task #600)", withoutItems, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnAsyncAfterWaitLinksTheVisitItFollows()
    {
        // The node's work is done and its visit completed; the token waits for a background job.
        var instance = InstanceDiagramOverlayTests.Instance("running",
            [InstanceDiagramOverlayTests.Position(1, 4, BpmnFlowNodeTypes.ServiceTask, "active", wait: "asyncAfter")],
            definition: Definition());

        var html = await RenderAsync(instance, selectedNodeId: 4,
        [
            Execution(9100, token: 1, node: 4, status: "completed"),
            Execution(8100, token: 1, node: 4, status: "completed")
        ]);

        Assert.Contains("Queued for background processing", html, StringComparison.Ordinal);
        Assert.Contains("href=\"node-executions/9100\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("node-executions/8100", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AViewerThatFailsToMountShowsTheErrorOnceAndMountsWhenTheInstanceChanges()
    {
        var js = new ViewerJs { FailMounts = true };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IJSRuntime>(js);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new InteractiveRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var diagram = await renderer.RenderAsync<DiagramComponent>(Parameters(RunningReview()));
        await renderer.Dispatcher.InvokeAsync(() => { });

        Assert.Equal((1, 0), (js.Mounts, js.Loads));
        Assert.StartsWith("The diagram could not be displayed. Could not load editor asset",
            Field<string?>(diagram, "viewerError"), StringComparison.Ordinal);

        // A later page render with the same instance does not try again.
        await renderer.Dispatcher.InvokeAsync(() => diagram.SetParametersAsync(Parameters(RunningReview())));
        Assert.Equal((1, 0), (js.Mounts, js.Loads));

        // Once the instance moves on, the viewer mounts, loads, and the error clears.
        js.FailMounts = false;
        var moved = InstanceDiagramOverlayTests.Instance("running",
            [InstanceDiagramOverlayTests.Position(1, 4, BpmnFlowNodeTypes.ServiceTask, "active", wait: "asyncBefore")],
            definition: Definition());
        await renderer.Dispatcher.InvokeAsync(() => diagram.SetParametersAsync(Parameters(moved)));

        Assert.Equal((2, 1), (js.Mounts, js.Loads));
        Assert.Null(Field<string?>(diagram, "viewerError"));
        Assert.True(Field<bool>(diagram, "viewerReady"));
        Assert.Empty(renderer.Errors);
    }

    [Fact]
    public async Task AViewerThatFailsToLoadShowsTheErrorOnceAndRetriesWhenTheInstanceChanges()
    {
        var js = new ViewerJs { FailLoads = true };
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IJSRuntime>(js);
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new InteractiveRenderer(provider, provider.GetRequiredService<ILoggerFactory>());

        var diagram = await renderer.RenderAsync<DiagramComponent>(Parameters(RunningReview()));
        await renderer.Dispatcher.InvokeAsync(() => { });

        Assert.Equal(1, js.Loads);
        Assert.Equal("The diagram could not be displayed. Expected a workflow document with flowNodes or legacy steps.",
            Field<string?>(diagram, "viewerError"));

        // A later page render with the same instance does not retry the failed load.
        await renderer.Dispatcher.InvokeAsync(() => diagram.SetParametersAsync(Parameters(RunningReview())));
        Assert.Equal(1, js.Loads);

        // Once the instance moves on, the viewer tries again and the error clears.
        js.FailLoads = false;
        var moved = InstanceDiagramOverlayTests.Instance("running",
            [InstanceDiagramOverlayTests.Position(1, 4, BpmnFlowNodeTypes.ServiceTask, "active", wait: "asyncBefore")],
            definition: Definition());
        await renderer.Dispatcher.InvokeAsync(() => diagram.SetParametersAsync(Parameters(moved)));

        Assert.Equal(2, js.Loads);
        Assert.Null(Field<string?>(diagram, "viewerError"));
        Assert.True(Field<bool>(diagram, "viewerReady"));
        Assert.Empty(renderer.Errors);
    }

    [Fact]
    public async Task UnmarkedAndFinishedNodesDescribeTheirState()
    {
        var unmarked = await RenderAsync(RunningReview(), selectedNodeId: 9, executions: []);
        Assert.Contains("Approved", unmarked, StringComparison.Ordinal);
        Assert.Contains("This node has no current execution position.", unmarked, StringComparison.Ordinal);

        var completed = InstanceDiagramOverlayTests.Instance("completed",
            [InstanceDiagramOverlayTests.Position(4, 9, BpmnFlowNodeTypes.EndEvent, "completed", reason: "normalEnd")],
            definition: Definition());
        var ended = await RenderAsync(completed, selectedNodeId: 9, [Execution(9500, token: 4, node: 9, status: "completed")]);
        Assert.Contains("Ended here", ended, StringComparison.Ordinal);
        Assert.Contains("href=\"node-executions/9500\"", ended, StringComparison.Ordinal);
    }

    private static InstanceDetailDto RunningReview() =>
        InstanceDiagramOverlayTests.Instance("running",
            [InstanceDiagramOverlayTests.Position(1, 3, BpmnFlowNodeTypes.UserTask, "active", task: 501)],
            definition: Definition());

    private static WorkflowModel Definition() => new()
    {
        Id = "purchase-review",
        Name = "Purchase review",
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Submitted", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel { Id = 3, Name = "Review request", Type = BpmnFlowNodeTypes.UserTask, ExternalId = "TASK_REVIEW" },
            new FlowNodeModel { Id = 4, Name = "Notify", Type = BpmnFlowNodeTypes.ServiceTask },
            new FlowNodeModel { Id = 9, Name = "Approved", Type = BpmnFlowNodeTypes.EndEvent }
        ]
    };

    internal static NodeExecutionSummaryDto Execution(
        long id,
        long token,
        int node,
        string status,
        long? userTask = null,
        long? multiInstance = null,
        int? itemIndex = null,
        string? owner = null) => new()
    {
        Id = id,
        InstanceId = 42,
        WorkflowId = 17,
        WorkflowKey = "purchase-review",
        WorkflowName = "Purchase review",
        WorkflowVersion = 3,
        TokenId = token,
        UserTaskId = userTask,
        MultiInstanceExecutionId = multiInstance,
        ItemIndex = itemIndex,
        ExecutionKind = multiInstance is null ? "node" : "userTaskItem",
        NodeId = node,
        NodeName = $"Node {node}",
        NodeType = BpmnFlowNodeTypes.UserTask,
        Status = status,
        InstanceStatus = "running",
        IsMultiInstance = multiInstance is not null,
        Owner = owner,
        CreatedAt = Now,
        UpdatedAt = Now,
        IsCutoverSeeded = false
    };

    private static async Task<string> RenderAsync(
        InstanceDetailDto instance,
        int? selectedNodeId,
        IReadOnlyList<NodeExecutionSummaryDto>? executions,
        bool loading = false,
        long? total = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IJSRuntime>(new UnusedJsRuntime());
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<DiagramComponent>(
                Parameters(instance, selectedNodeId, executions, loading, total ?? executions?.Count ?? 0));
            // Expression output is HTML-encoded (for example "·"); assert on the text people read.
            return WebUtility.HtmlDecode(component.ToHtmlString());
        });
    }

    private static ParameterView Parameters(
        InstanceDetailDto instance,
        int? selectedNodeId = null,
        IReadOnlyList<NodeExecutionSummaryDto>? executions = null,
        bool loading = false,
        long total = 0) =>
        ParameterView.FromDictionary(new Dictionary<string, object?>
        {
            [nameof(DiagramComponent.Instance)] = instance,
            [nameof(DiagramComponent.SelectedNodeId)] = selectedNodeId,
            [nameof(DiagramComponent.NodeExecutions)] = executions,
            [nameof(DiagramComponent.NodeExecutionsTotal)] = total,
            [nameof(DiagramComponent.NodeExecutionsLoading)] = loading
        });

    private static T Field<T>(object component, string name) =>
        (T)component.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(component)!;

    // Unlike static rendering, this renderer completes every render, so OnAfterRenderAsync runs.
    // A renderer can only be written against the framework's render-tree types.
#pragma warning disable BL0006
    private sealed class InteractiveRenderer(IServiceProvider services, ILoggerFactory loggerFactory)
        : Renderer(services, loggerFactory)
    {
        public List<Exception> Errors { get; } = [];

        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public Task<TComponent> RenderAsync<TComponent>(ParameterView parameters) where TComponent : IComponent =>
            Dispatcher.InvokeAsync(async () =>
            {
                var component = (TComponent)InstantiateComponent(typeof(TComponent));
                await RenderRootComponentAsync(AssignRootComponentId(component), parameters);
                return component;
            });

        protected override void HandleException(Exception exception) => Errors.Add(exception);

        protected override Task UpdateDisplayAsync(in RenderBatch renderBatch) => Task.CompletedTask;
    }
#pragma warning restore BL0006

    // Stands in for the module, the adapter, and the viewer it returns.
    private sealed class ViewerJs : IJSRuntime, IJSObjectReference
    {
        public bool FailMounts { get; set; }

        public bool FailLoads { get; set; }

        public int Mounts { get; private set; }

        public int Loads { get; private set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "import") return ValueTask.FromResult((TValue)(object)this);
            if (identifier == "createViewer")
            {
                Mounts++;
                // A retry loop gives up after a few calls instead of overflowing the stack.
                if (FailMounts && Mounts <= 5)
                    throw new JSException("Could not load editor asset: editor/flowbit-editor.js.");
                return ValueTask.FromResult((TValue)(object)this);
            }
            if (identifier == "load")
            {
                Loads++;
                // A retry loop gives up after a few calls instead of overflowing the stack.
                if (FailLoads && Loads <= 5)
                    throw new JSException("Expected a workflow document with flowNodes or legacy steps.");
            }
            return ValueTask.FromResult(default(TValue)!);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    // Static rendering never runs OnAfterRenderAsync, so the viewer is never mounted.
    private sealed class UnusedJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            throw new InvalidOperationException($"Unexpected JavaScript call: {identifier}");

        public ValueTask<TValue> InvokeAsync<TValue>(
            string identifier,
            CancellationToken cancellationToken,
            object?[]? args) =>
            throw new InvalidOperationException($"Unexpected JavaScript call: {identifier}");
    }
}
