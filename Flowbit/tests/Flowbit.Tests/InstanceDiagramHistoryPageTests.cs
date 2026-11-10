extern alias FlowbitUi;

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.JSInterop;
using Xunit;
using Page = FlowbitUi::Flowbit.Ui.Components.Pages.InstanceDetail;
using Client = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using ApiException = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiException;

namespace Flowbit.Tests;

public sealed class InstanceDiagramHistoryPageTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingInstanceClearsOldContentAndReportsMissingRatherThanIdentity(bool previouslyLoaded)
    {
        using var handler = new Handler();
        var page = CreatePage(handler);
        if (previouslyLoaded) await Toggle(page, true);
        else Set(page, "instance", null);
        handler.DetailResponse = () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

        await Call(page, "LoadAsync");

        Assert.Null(Get<InstanceDetailDto?>(page, "instance"));
        Assert.Equal("The workflow instance no longer exists.", Get<string>(page, "error"));
        Assert.DoesNotContain("identity", Get<string>(page, "instanceUnavailableDescription"));
        Assert.False(Get<bool>(page, "diagramHistoryEnabled"));
        Assert.Null(Get<InstanceDiagramHistoryDto?>(page, "diagramHistory"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task InstanceAccessFailureClearsOldContentAndPreservesTheIdentityDistinction(HttpStatusCode status)
    {
        using var handler = new Handler();
        var page = CreatePage(handler);
        await Toggle(page, true);
        handler.DetailResponse = () => Task.FromResult(new HttpResponseMessage(status));

        await Call(page, "LoadAsync");

        Assert.Null(Get<InstanceDetailDto?>(page, "instance"));
        Assert.Contains("identity", Get<string>(page, "instanceUnavailableDescription"));
        Assert.False(Get<bool>(page, "diagramHistoryEnabled"));
        Assert.Null(Get<InstanceDiagramHistoryDto?>(page, "diagramHistory"));
    }

    [Theory]
    [InlineData("detail")]
    [InlineData("actions")]
    [InlineData("afterAction")]
    public async Task FailedRefreshShowsHistoryRetryThatReloadsInstanceBeforeSummary(string failure)
    {
        using var handler = new Handler();
        var page = CreatePage(handler);
        await Toggle(page, true);
        handler.Instance = handler.Instance with { UpdatedAt = handler.Instance.UpdatedAt.AddSeconds(1) };
        if (failure == "detail")
            handler.DetailResponse = () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        else
            handler.AvailableFlowsResponse = () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));

        if (failure == "afterAction") await Call(page, "TakeFlowAsync", new SequenceFlowModel { Id = 201 });
        else await Call(page, "LoadAsync");

        Assert.NotNull(Get<string?>(page, "error"));
        Assert.True(Get<bool>(page, "diagramHistoryEnabled"));
        Assert.Null(Get<InstanceDiagramHistoryDto?>(page, "diagramHistory"));
        Assert.False(Get<bool>(page, "diagramHistoryLoading"));
        Assert.Contains("Retry", Get<string>(page, "diagramHistoryError"));
        Assert.True(Get<bool>(page, "diagramHistoryRequiresInstanceRefresh"));

        handler.DetailResponse = null;
        handler.AvailableFlowsResponse = null;
        var priorDetailReads = handler.DetailReads;
        await Call(page, "RetryDiagramHistoryAsync");

        Assert.Equal(priorDetailReads + 1, handler.DetailReads);
        Assert.Equal(handler.Instance.UpdatedAt, Get<InstanceDiagramHistoryDto>(page, "diagramHistory").InstanceUpdatedAt);
        Assert.Null(Get<string?>(page, "diagramHistoryError"));
        Assert.Null(Get<string?>(page, "error"));
        Assert.False(Get<bool>(page, "diagramHistoryRequiresInstanceRefresh"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task LosingInstancePermissionDuringHistoryRetryShowsUnavailableInsteadOfLoading(HttpStatusCode status)
    {
        using var handler = new Handler();
        var page = CreatePage(handler);
        await Toggle(page, true);
        handler.DetailResponse = () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await Call(page, "LoadAsync");
        Assert.True(Get<bool>(page, "diagramHistoryRequiresInstanceRefresh"));
        handler.DetailResponse = () => Task.FromResult(new HttpResponseMessage(status));

        await Call(page, "RetryDiagramHistoryAsync");

        Assert.Null(Get<InstanceDetailDto?>(page, "instance"));
        Assert.Contains("not available to this identity", Get<string>(page, "error"));
        Assert.Contains("identity", Get<string>(page, "instanceUnavailableDescription"));
        Assert.False(Get<bool>(page, "diagramHistoryEnabled"));
        Assert.Null(Get<InstanceDiagramHistoryDto?>(page, "diagramHistory"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateFailedRefreshCannotClearNewIdentityOrRouteHistory(bool identityChange)
    {
        using var handler = new Handler();
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var page = CreatePage(handler);
        await Toggle(page, true);
        handler.DetailResponse = () => pending.Task;
        var previousLoad = Call(page, "LoadAsync");
        if (identityChange) Set(page, "identityEpoch", 1L);
        else page.GetType().GetProperty(nameof(Page.InstanceId))!.SetValue(page, 43L);
        Invoke(page, "ResetDiagramState", true, true);
        handler.Instance = handler.Instance with { Id = identityChange ? 42 : 43, UpdatedAt = handler.Instance.UpdatedAt.AddSeconds(1) };
        Set(page, "instance", handler.Instance);
        await Toggle(page, true);
        var currentHistory = Get<InstanceDiagramHistoryDto>(page, "diagramHistory");
        pending.SetResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await previousLoad;

        Assert.Same(currentHistory, Get<InstanceDiagramHistoryDto>(page, "diagramHistory"));
        Assert.Null(Get<string?>(page, "diagramHistoryError"));
        Assert.Null(Get<string?>(page, "error"));
    }

    [Fact]
    public async Task SummaryIsLazySkipsUnchangedPollsAndHonorsForcedRefresh()
    {
        using var handler = new Handler();
        var page = CreatePage(handler);
        await Load(page);
        Assert.Equal(0, handler.SummaryReads);
        await Toggle(page, true);
        Assert.Equal(1, handler.SummaryReads);
        await Load(page);
        Assert.Equal(1, handler.SummaryReads);
        await Load(page, force: true);
        Assert.Equal(2, handler.SummaryReads);
        Assert.NotNull(Get<InstanceDiagramHistoryDto?>(page, "diagramHistory"));
        await Toggle(page, false);
        Assert.Null(Get<InstanceDiagramHistoryDto?>(page, "diagramHistory"));
    }

    [Theory]
    [InlineData("toggle")]
    [InlineData("identity")]
    [InlineData("version")]
    [InlineData("disposal")]
    public async Task ObsoleteSummaryCannotRestoreClearedData(string change)
    {
        using var handler = new Handler();
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.SummaryResponse = () => pending.Task;
        var page = CreatePage(handler);
        var loading = Toggle(page, true);
        Assert.True(Get<bool>(page, "diagramHistoryLoading"));
        if (change == "toggle") await Toggle(page, false);
        else if (change == "version") Set(page, "instance", handler.Instance with { Workflow = handler.Instance.Workflow with { Id = 99 } });
        else
        {
            Set(page, "identityEpoch", 1L);
            if (change == "disposal") Set(page, "disposed", true);
            Invoke(page, "ResetDiagramState", true, true);
        }
        pending.SetResult(Json(InstanceDiagramOverlayTests.DiagramHistory(handler.Instance)));
        await loading;
        Assert.Null(Get<InstanceDiagramHistoryDto?>(page, "diagramHistory"));
    }

    [Fact]
    public async Task SupersededRequestCannotOverwriteNewerCounts()
    {
        using var handler = new Handler();
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.SummaryResponse = () => pending.Task;
        var page = CreatePage(handler);
        var earlier = Toggle(page, true);
        handler.SummaryResponse = () => Task.FromResult(Json(InstanceDiagramOverlayTests.DiagramHistory(handler.Instance, flows: [new(101, 2)])));
        await Load(page, true);
        pending.SetResult(Json(InstanceDiagramOverlayTests.DiagramHistory(handler.Instance, flows: [new(101, 1)])));
        await earlier;
        Assert.Equal(2, Assert.Single(Get<InstanceDiagramHistoryDto>(page, "diagramHistory").Flows).TraversalCount);
    }

    [Fact]
    public async Task RevisionMismatchReconcilesOnlyOnceThenOffersRetry()
    {
        using var handler = new Handler();
        handler.SummaryResponse = () => Task.FromResult(Json(InstanceDiagramOverlayTests.DiagramHistory(handler.Instance)
            with { InstanceUpdatedAt = handler.Instance.UpdatedAt.AddSeconds(1) }));
        var page = CreatePage(handler);
        await Toggle(page, true);
        Assert.Equal(2, handler.SummaryReads);
        Assert.Equal(1, handler.DetailReads);
        Assert.Null(Get<InstanceDiagramHistoryDto?>(page, "diagramHistory"));
        Assert.Contains("changed", Get<string>(page, "diagramHistoryError"));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.InternalServerError)]
    public async Task FailedHistoryClearsCountsButPreservesCurrentLinks(HttpStatusCode status)
    {
        using var handler = new Handler();
        var page = CreatePage(handler);
        await Toggle(page, true);
        await Select(page, 3);
        handler.SummaryResponse = () => Task.FromResult(new HttpResponseMessage(status));
        await Load(page, true);
        Assert.Null(Get<InstanceDiagramHistoryDto?>(page, "diagramHistory"));
        Assert.NotNull(Get<string?>(page, "diagramHistoryError"));
        Assert.Equal(9001, Assert.Single(Get<IReadOnlyList<NodeExecutionSummaryDto>>(page, "diagramNodeExecutions")).Id);
    }

    [Fact]
    public async Task HistoricalPagingUsesExactVersionWithoutReplacingCurrentLinks()
    {
        using var handler = new Handler();
        var page = CreatePage(handler);
        await Toggle(page, true);
        await Select(page, 3);
        await Call(page, "OnDiagramHistoryPageChangedAsync", 2);
        Assert.Equal(9001, Assert.Single(Get<IReadOnlyList<NodeExecutionSummaryDto>>(page, "diagramNodeExecutions")).Id);
        Assert.Equal(8000, Assert.Single(Get<IReadOnlyList<NodeExecutionSummaryDto>>(page, "diagramHistoricalExecutions")).Id);
        Assert.Equal(60, Get<long>(page, "diagramHistoricalTotal"));
        Assert.Equal(4, handler.Searches.Count);
        using var latest = JsonDocument.Parse(handler.Searches[0]);
        Assert.Equal(JsonValueKind.Null, latest.RootElement.GetProperty("workflowId").ValueKind);
        using var history = JsonDocument.Parse(handler.Searches[2]);
        Assert.Equal(17, history.RootElement.GetProperty("workflowId").GetInt64());
        Assert.Equal(2, history.RootElement.GetProperty("page").GetInt32());
        Assert.Equal(50, history.RootElement.GetProperty("pageSize").GetInt32());
        Assert.Equal(JsonValueKind.Null, history.RootElement.GetProperty("statuses").ValueKind);
    }

    [Fact]
    public async Task ObservedHistoricalPermissionLossRemovesCurrentExecutionLinks()
    {
        using var handler = new Handler();
        var page = CreatePage(handler);
        await Toggle(page, true);
        await Select(page, 3);
        handler.HistoricalStatus = HttpStatusCode.Forbidden;
        handler.CurrentStatus = HttpStatusCode.Forbidden;
        await Call(page, "OnDiagramHistoryPageChangedAsync", 2);
        Assert.Empty(Get<IReadOnlyList<NodeExecutionSummaryDto>>(page, "diagramNodeExecutions"));
        Assert.Null(Get<IReadOnlyList<NodeExecutionSummaryDto>?>(page, "diagramHistoricalExecutions"));
        Assert.Contains("identity", Get<string>(page, "diagramHistoricalError"));
        Assert.NotNull(Get<InstanceDiagramHistoryDto?>(page, "diagramHistory"));
    }

    [Fact]
    public async Task EmptyRoleScopedSuccessOnPagingRevalidatesTheSeparateCurrentLookup()
    {
        using var handler = new Handler();
        var page = CreatePage(handler);
        await Toggle(page, true);
        await Select(page, 3);
        handler.HideExecutions = true;
        await Call(page, "OnDiagramHistoryPageChangedAsync", 2);
        Assert.Empty(Get<IReadOnlyList<NodeExecutionSummaryDto>>(page, "diagramNodeExecutions"));
        Assert.Empty(Get<IReadOnlyList<NodeExecutionSummaryDto>>(page, "diagramHistoricalExecutions"));
        Assert.Equal(1, Get<int>(page, "diagramHistoricalPage"));
        Assert.NotNull(Get<InstanceDiagramHistoryDto?>(page, "diagramHistory"));
    }

    [Fact]
    public async Task EmptyCurrentRecheckInvalidatesTheHistoricalPageFetchedBeforePermissionLoss()
    {
        using var handler = new Handler();
        var page = CreatePage(handler);
        await Toggle(page, true);
        await Select(page, 3);
        handler.HideCurrentExecutions = true;
        await Call(page, "OnDiagramHistoryPageChangedAsync", 2);
        Assert.Empty(Get<IReadOnlyList<NodeExecutionSummaryDto>>(page, "diagramNodeExecutions"));
        Assert.Empty(Get<IReadOnlyList<NodeExecutionSummaryDto>>(page, "diagramHistoricalExecutions"));
        Assert.Equal(0, Get<long>(page, "diagramHistoricalTotal"));
        Assert.Equal(1, Get<int>(page, "diagramHistoricalPage"));
        Assert.False(Get<bool>(page, "diagramHistoricalLoading"));
        Assert.Null(Get<string?>(page, "diagramHistoricalError"));
        Assert.Null(Get<string?>(page, "diagramNodeExecutionsError"));
        Assert.NotNull(Get<InstanceDiagramHistoryDto?>(page, "diagramHistory"));
    }

    [Fact]
    public async Task HistoricalResponseInFlightCannotRestoreRecordsAfterAnEmptyCurrentRecheck()
    {
        using var handler = new Handler();
        var page = CreatePage(handler);
        await Toggle(page, true);
        await Select(page, 3);
        var previous = Get<IReadOnlyList<NodeExecutionSummaryDto>>(page, "diagramHistoricalExecutions");
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.HistoricalResponse = () => pending.Task;
        var historical = Call(page, "LoadDiagramHistoricalExecutionsAsync", CancellationToken.None, true);
        Assert.True(Get<bool>(page, "diagramHistoricalLoading"));
        handler.HideCurrentExecutions = true;
        await Call(page, "LoadDiagramNodeExecutionsAsync", CancellationToken.None);
        pending.SetResult(Json(new PagedResult<NodeExecutionSummaryDto>(previous, 1, 50, 60)));
        await historical;
        Assert.Empty(Get<IReadOnlyList<NodeExecutionSummaryDto>>(page, "diagramHistoricalExecutions"));
        Assert.Equal(0, Get<long>(page, "diagramHistoricalTotal"));
        Assert.False(Get<bool>(page, "diagramHistoricalLoading"));
        Assert.Null(Get<string?>(page, "diagramHistoricalError"));
    }

    [Fact]
    public async Task SupersededEmptyCurrentResponseCannotEraseNewerHistoricalRecords()
    {
        using var handler = new Handler();
        var page = CreatePage(handler);
        await Toggle(page, true);
        await Select(page, 3);
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.CurrentResponse = () => pending.Task;
        var earlier = Call(page, "LoadDiagramNodeExecutionsAsync", CancellationToken.None);
        handler.CurrentResponse = null;
        await Call(page, "OnDiagramHistoryPageChangedAsync", 2);
        pending.SetResult(Json(new PagedResult<NodeExecutionSummaryDto>([], 1, 50, 0)));
        await earlier;
        Assert.Equal(8000, Assert.Single(Get<IReadOnlyList<NodeExecutionSummaryDto>>(page, "diagramHistoricalExecutions")).Id);
        Assert.Equal(60, Get<long>(page, "diagramHistoricalTotal"));
        Assert.Equal(2, Get<int>(page, "diagramHistoricalPage"));
    }

    [Fact]
    public async Task CurrentLookupFailureDoesNotMasqueradeAsAnEmptyAuthorizedResult()
    {
        using var handler = new Handler();
        var page = CreatePage(handler);
        await Toggle(page, true);
        await Select(page, 3);
        handler.CurrentStatus = HttpStatusCode.InternalServerError;
        await Call(page, "LoadDiagramNodeExecutionsAsync", CancellationToken.None);
        Assert.Single(Get<IReadOnlyList<NodeExecutionSummaryDto>>(page, "diagramHistoricalExecutions"));
        Assert.Equal(60, Get<long>(page, "diagramHistoricalTotal"));
        Assert.NotNull(Get<string?>(page, "diagramNodeExecutionsError"));
        Assert.Null(Get<string?>(page, "diagramHistoricalError"));
    }

    [Fact]
    public async Task AnInconsistentEmptyLastPageStopsWithRetryInsteadOfRecursing()
    {
        using var handler = new Handler();
        var page = CreatePage(handler);
        await Toggle(page, true);
        await Select(page, 3);
        handler.EmptyLastPage = true;
        await Call(page, "OnDiagramHistoryPageChangedAsync", 2);
        Assert.Equal(4, handler.Searches.Count);
        Assert.Null(Get<IReadOnlyList<NodeExecutionSummaryDto>?>(page, "diagramHistoricalExecutions"));
        Assert.Contains("changed", Get<string>(page, "diagramHistoricalError"));
    }

    [Fact]
    public async Task SuccessfulVersionChangeForcesHistoryAndClearsOldSelectionAndPage()
    {
        using var handler = new Handler();
        var page = CreatePage(handler);
        await Toggle(page, true);
        await Select(page, 3);
        Set(page, "diagramHistoricalPage", 2);
        var before = handler.Instance;
        var source = new WorkflowSummaryDto(17, "Review", "purchase-review", 3, true, true, before.CreatedAt);
        var target = source with { Id = 18, Version = 4 };
        Set(page, "versionPreview", new InstanceVersionChangePreviewDto(42, source, target, "upgrade", true, [], [], 17, before.UpdatedAt));
        Set(page, "selectedTargetWorkflowId", 18L);
        Set(page, "versionChangeReason", "Compatible upgrade");
        page.GetType().GetProperty("JS", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, new ConfirmJsRuntime());
        handler.ChangedInstance = before with { Workflow = before.Workflow with { Id = 18, Version = 4 }, UpdatedAt = before.UpdatedAt.AddSeconds(1) };
        await Call(page, "ChangeVersionAsync");
        Assert.Null(Get<string?>(page, "versionChangeError"));
        Assert.Equal(2, handler.SummaryReads);
        Assert.Equal(18, Get<InstanceDiagramHistoryDto>(page, "diagramHistory").WorkflowId);
        Assert.Null(Get<int?>(page, "selectedDiagramNodeId"));
        Assert.Null(Get<IReadOnlyList<NodeExecutionSummaryDto>?>(page, "diagramHistoricalExecutions"));
        Assert.Equal(1, Get<int>(page, "diagramHistoricalPage"));
    }

    [Fact]
    public async Task LateActionResponseCannotRestorePriorIdentityInstanceOrHistory()
    {
        using var handler = new Handler();
        var pending = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.ActionResponse = () => pending.Task;
        var page = CreatePage(handler);
        await Toggle(page, true);
        var action = Call(page, "TakeFlowAsync", new SequenceFlowModel { Id = 201 });
        Set(page, "identityEpoch", 1L);
        Invoke(page, "ResetDiagramState", true, true);
        Set(page, "instance", null);
        pending.SetResult(Json(handler.Instance));
        await action;
        Assert.Null(Get<InstanceDetailDto?>(page, "instance"));
        Assert.Null(Get<InstanceDiagramHistoryDto?>(page, "diagramHistory"));
        Assert.Equal(0, handler.DetailReads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LateAuxiliaryResponsesCannotRestorePreviousRouteOrIdentity(bool identityChange)
    {
        using var handler = new Handler();
        var pendingCalls = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pendingFlows = new TaskCompletionSource<HttpResponseMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        handler.CallsResponse = () => pendingCalls.Task;
        handler.AvailableFlowsResponse = () => pendingFlows.Task;
        var page = CreatePage(handler);
        Set(page, "instance", handler.Instance with { Calls = new CallCountsDto(1, 0) });
        var previousCalls = new PagedResult<CallActivityExecutionDto>([], 1, 25, 1);
        Set(page, "calls", previousCalls);
        Set(page, "availableFlows", new List<SequenceFlowModel> { new() { Id = 201 } });
        var callsRead = Call(page, "LoadCallsAsync", CancellationToken.None);
        var flowsRead = Call(page, "LoadAvailableActionsAsync", CancellationToken.None);

        if (identityChange) Set(page, "identityEpoch", 1L);
        else page.GetType().GetProperty("InstanceId")!.SetValue(page, 43L);
        Invoke(page, "ResetInstanceAuxiliaryState");
        Set(page, "instance", handler.Instance with { Id = identityChange ? 42 : 43, Calls = new CallCountsDto(1, 0) });
        pendingCalls.SetResult(Json(previousCalls));
        pendingFlows.SetResult(Json(new[] { new SequenceFlowModel { Id = 201 } }));
        await Task.WhenAll(callsRead, flowsRead);

        Assert.Null(Get<PagedResult<CallActivityExecutionDto>?>(page, "calls"));
        Assert.Empty(Get<IReadOnlyList<SequenceFlowModel>>(page, "availableFlows"));
        Assert.Equal(1, Get<int>(page, "callsPage"));
        handler.CallsResponse = () => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        await Call(page, "LoadCallsAsync", CancellationToken.None);
        Assert.Null(Get<PagedResult<CallActivityExecutionDto>?>(page, "calls"));
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.NotFound)]
    public async Task ClientPreservesStatusDistinctions(HttpStatusCode status)
    {
        using var handler = new Handler { SummaryResponse = () => Task.FromResult(new HttpResponseMessage(status)) };
        using var client = new Client(new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") });
        var error = await Assert.ThrowsAsync<ApiException>(() => client.GetInstanceDiagramHistoryAsync(42));
        Assert.Equal(status, error.StatusCode);
    }

    private static Page CreatePage(Handler handler)
    {
        var page = new Page();
        page.GetType().GetProperty("Api", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page,
            new Client(new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") }));
        page.GetType().GetProperty("InstanceId")!.SetValue(page, 42L);
        Set(page, "instance", handler.Instance);
        Set(page, "diagramDisplayedWorkflowId", 17L);
        return page;
    }

    private static Task Toggle(Page page, bool value) => Call(page, "OnDiagramHistoryEnabledChangedAsync", value);
    private static Task Select(Page page, int node) => Call(page, "OnDiagramNodeSelectedAsync", (int?)node);
    private static Task Load(Page page, bool force = false) => Call(page, "LoadDiagramHistoryAsync", force, CancellationToken.None);
    private static Task Call(Page page, string method, params object?[] args) => (Task)Invoke(page, method, args)!;
    private static object? Invoke(Page page, string method, params object?[] args) => page.GetType()
        .GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, args);
    private static void Set(Page page, string name, object? value) => page.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);
    private static T Get<T>(Page page, string name) => (T)page.GetType()
        .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
    private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };

    private sealed class Handler : HttpMessageHandler
    {
        public InstanceDetailDto Instance { get; set; } = InstanceDiagramOverlayTests.Instance("running", []);
        public Func<Task<HttpResponseMessage>>? DetailResponse { get; set; }
        public InstanceDetailDto? ChangedInstance { get; set; }
        public Func<Task<HttpResponseMessage>>? ActionResponse { get; set; }
        public Func<Task<HttpResponseMessage>>? SummaryResponse { get; set; }
        public Func<Task<HttpResponseMessage>>? HistoricalResponse { get; set; }
        public Func<Task<HttpResponseMessage>>? CurrentResponse { get; set; }
        public Func<Task<HttpResponseMessage>>? CallsResponse { get; set; }
        public Func<Task<HttpResponseMessage>>? AvailableFlowsResponse { get; set; }
        public HttpStatusCode HistoricalStatus { get; set; } = HttpStatusCode.OK;
        public HttpStatusCode CurrentStatus { get; set; } = HttpStatusCode.OK;
        public bool HideExecutions { get; set; }
        public bool HideCurrentExecutions { get; set; }
        public bool EmptyLastPage { get; set; }
        public int SummaryReads { get; private set; }
        public int DetailReads { get; private set; }
        public List<string> Searches { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            if (path.EndsWith("/diagram-history", StringComparison.Ordinal))
            {
                SummaryReads++;
                return SummaryResponse is null ? Json(InstanceDiagramOverlayTests.DiagramHistory(Instance)) : await SummaryResponse();
            }
            if (path == $"/api/instances/{Instance.Id}") { DetailReads++; return DetailResponse is null ? Json(Instance) : await DetailResponse(); }
            if (path == "/api/instances/42/flows/201") return ActionResponse is null ? Json(Instance) : await ActionResponse();
            if (path == "/api/instances/42/version-change")
            {
                Instance = ChangedInstance!;
                return Json(new ChangeInstanceVersionResultDto(Instance, null!));
            }
            if (path.StartsWith("/api/workflows/", StringComparison.Ordinal) && path.EndsWith("/versions", StringComparison.Ordinal))
                return Json(Array.Empty<WorkflowSummaryDto>());
            if (path == "/api/instances/42/flows") return AvailableFlowsResponse is null
                ? Json(Array.Empty<SequenceFlowModel>()) : await AvailableFlowsResponse();
            if (path.EndsWith("/calls", StringComparison.Ordinal) && CallsResponse is not null) return await CallsResponse();
            if (path == "/api/node-executions/search")
            {
                var body = await request.Content!.ReadAsStringAsync(cancellationToken);
                Searches.Add(body);
                using var json = JsonDocument.Parse(body);
                var historical = json.RootElement.GetProperty("workflowId").ValueKind == JsonValueKind.Number;
                if (historical && HistoricalStatus != HttpStatusCode.OK) return new(HistoricalStatus);
                if (!historical && CurrentStatus != HttpStatusCode.OK) return new(CurrentStatus);
                if (historical && HistoricalResponse is not null) return await HistoricalResponse();
                if (!historical && CurrentResponse is not null) return await CurrentResponse();
                var page = json.RootElement.GetProperty("page").GetInt32();
                if (HideExecutions || !historical && HideCurrentExecutions)
                    return Json(new PagedResult<NodeExecutionSummaryDto>([], page, 50, 0));
                if (EmptyLastPage && historical && page == 2) return Json(new PagedResult<NodeExecutionSummaryDto>([], page, 50, 60));
                return Json(new PagedResult<NodeExecutionSummaryDto>(
                    [InstanceDiagramComponentTests.Execution(page == 2 ? 8000 : 9001, 1, 3, "completed")], page, 50, 60));
            }
            return new(HttpStatusCode.NotFound);
        }
    }

    private sealed class ConfirmJsRuntime : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) => ValueTask.FromResult((TValue)(object)true);
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) => InvokeAsync<TValue>(identifier, args);
    }
}
