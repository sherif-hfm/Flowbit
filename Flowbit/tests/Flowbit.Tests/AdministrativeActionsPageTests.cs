extern alias FlowbitUi;

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using BatchPage = FlowbitUi::Flowbit.Ui.Components.Pages.AdministrativeActions;
using TokenState = FlowbitUi::Flowbit.Ui.Auth.TokenState;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// Assembled `/administrative-actions` page characterizations: request order
/// and counts, displayed concurrency fences, identity/disposal guards, and the
/// three-second poll lifecycle. The original inline-page characterizations
/// are extended with rendered polling and identity/disposal race regressions.
/// </summary>
public sealed class AdministrativeActionsPageTests
{
    private static readonly DateTimeOffset BatchUpdatedAt =
        DateTimeOffset.Parse("2026-09-10T10:00:00Z");

    [Fact]
    public async Task OpenBatchResetsItemStateAndIssuesDetailThenItemsRequestsOnly()
    {
        await using var harness = await CreateHarnessAsync();
        var component = await harness.RenderPageAsync();

        harness.Record.Clear();
        await harness.SetAsync("batchItemStatus", "failed");
        await harness.SetAsync("batchItemPage", 3);
        await harness.SetAsync("cancellationReason", "stale cancellation");
        await harness.CallAsync("OpenBatchAsync", 98L);

        Assert.Equal(
            [
                "/api/administrative-action-batches/98",
                "/api/administrative-action-batches/98/items?page=1&pageSize=50",
            ],
            harness.Record.Paths);
        Assert.Null(await harness.ValueAsync<string?>("batchItemStatus"));
        Assert.Equal(1, await harness.ValueAsync<int>("batchItemPage"));
        Assert.Null(await harness.ValueAsync<string?>("cancellationReason"));
        Assert.Equal(98L, (await harness.ValueAsync<AdministrativeActionBatchDetailDto?>("currentBatch"))!.Summary.Id);
        Assert.Contains("Preparation and execution", await harness.HtmlAsync(component), StringComparison.Ordinal);
        // The recent-batch row for the opened batch is highlighted after render.
        Assert.Contains("table-active", await harness.HtmlAsync(component), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RefreshCurrentBatchIssuesDetailItemsListOnceAndHoldsInFlightDuplicates()
    {
        await using var harness = await CreateHarnessAsync();
        await harness.RenderPageAsync();
        await harness.CallAsync("OpenBatchAsync", 98L);

        harness.Record.Clear();
        harness.Record.ArmDetailGate();
        var firstRefresh = harness.CallAsync("RefreshCurrentBatchAsync");
        await harness.Record.DetailStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // The in-flight guard rejects a second refresh while the first runs.
        await harness.CallAsync("RefreshCurrentBatchAsync");
        harness.Record.ReleaseDetailGate(BatchDetail(98, "completed", completedAt: BatchUpdatedAt));
        await firstRefresh;

        Assert.Equal(
            [
                "/api/administrative-action-batches/98",
                "/api/administrative-action-batches/98/items?page=1&pageSize=50",
                "/api/administrative-action-batches?page=1&pageSize=25",
            ],
            harness.Record.Paths);
        Assert.Equal(2, harness.Record.DetailCount);
        Assert.Equal(2, harness.Record.ItemsCount);
        Assert.Equal(2, harness.Record.ListCount);
    }

    [Fact]
    public async Task ItemAndListFiltersResetOnlyTheirOwnPageAndIssueOneRequestEach()
    {
        await using var harness = await CreateHarnessAsync();
        var component = await harness.RenderPageAsync();
        await harness.CallAsync("OpenBatchAsync", 98L);

        // The item pager moves to page 2 first (the fixture exposes 3 pages).
        await harness.CallAsync("NextBatchItemPageAsync");

        // The item filter resets only the item page.
        harness.Record.Clear();
        await harness.CallAsync("OnBatchItemStatusChangedAsync", new ChangeEventArgs { Value = "failed" });
        Assert.Equal(
            ["/api/administrative-action-batches/98/items?page=1&pageSize=50&status=failed"],
            harness.Record.Paths);
        Assert.Equal(1, await harness.ValueAsync<int>("batchItemPage"));
        Assert.Equal(1, await harness.ValueAsync<int>("batchListPage"));

        // The list filter resets only the list page and keeps the item filter.
        harness.Record.Clear();
        await harness.CallAsync("OnBatchListStatusChangedAsync", new ChangeEventArgs { Value = "completed" });
        Assert.Equal(
            ["/api/administrative-action-batches?page=1&pageSize=25&status=completed"],
            harness.Record.Paths);
        Assert.Equal("failed", await harness.ValueAsync<string?>("batchItemStatus"));
        Assert.Equal(1, await harness.ValueAsync<int>("batchListPage"));

        // Each pager handler issues exactly one list request.
        harness.Record.Clear();
        await harness.CallAsync("NextBatchListPageAsync");
        Assert.Equal(
            ["/api/administrative-action-batches?page=2&pageSize=25&status=completed"],
            harness.Record.Paths);
        harness.Record.Clear();
        await harness.CallAsync("PreviousBatchListPageAsync");
        Assert.Equal(
            ["/api/administrative-action-batches?page=1&pageSize=25&status=completed"],
            harness.Record.Paths);

        // A rerender alone issues no requests.
        harness.Record.Clear();
        await harness.SetAsync("error", "Rerender marker");
        Assert.DoesNotContain("Rerender marker", await harness.HtmlAsync(component), StringComparison.Ordinal);
        await harness.RerenderAsync();
        Assert.Contains("Rerender marker", await harness.HtmlAsync(component), StringComparison.Ordinal);
        Assert.Empty(harness.Record.Paths);
    }

    [Fact]
    public async Task ConfirmCarriesDisplayedEligibleCountAffectedTasksAndTimestamp()
    {
        await using var harness = await CreateHarnessAsync();
        harness.Record.SetDetail(BatchDetail(98, "ready"));
        await harness.RenderPageAsync();
        await harness.CallAsync("OpenBatchAsync", 98L);

        harness.Record.Clear();
        await harness.CallAsync("ConfirmCurrentBatchAsync");

        var post = Assert.Single(harness.Record.Posts);
        Assert.Equal("/api/administrative-action-batches/98/confirm", post.Path);
        Assert.Equal(3, post.Body.GetProperty("expectedEligibleItemCount").GetInt32());
        Assert.Equal(5, post.Body.GetProperty("expectedAffectedTaskCount").GetInt32());
        Assert.Equal(BatchUpdatedAt, post.Body.GetProperty("expectedBatchUpdatedAt").GetDateTimeOffset());
        Assert.Equal(
            [
                "/api/administrative-action-batches/98/confirm",
                "/api/administrative-action-batches/98/items?page=1&pageSize=50",
                "/api/administrative-action-batches?page=1&pageSize=25",
            ],
            harness.Record.Paths);
        Assert.Contains(
            "was confirmed",
            await harness.ValueAsync<string?>("success") ?? string.Empty,
            StringComparison.Ordinal);
        Assert.False(await harness.ValueAsync<bool>("batchOperation"));
    }

    [Fact]
    public async Task CancelTrimsOptionalReasonAndTargetsCurrentBatch()
    {
        await using var harness = await CreateHarnessAsync();
        harness.Record.SetDetail(BatchDetail(98, "ready"));
        await harness.RenderPageAsync();
        await harness.CallAsync("OpenBatchAsync", 98L);

        await harness.SetAsync("cancellationReason", "  urgent fix  ");
        harness.Record.Clear();
        await harness.CallAsync("CancelCurrentBatchAsync");

        var post = Assert.Single(harness.Record.Posts);
        Assert.Equal("/api/administrative-action-batches/98/cancel", post.Path);
        Assert.Equal("urgent fix", post.Body.GetProperty("reason").GetString());
        Assert.Contains(
            "was cancelled",
            await harness.ValueAsync<string?>("success") ?? string.Empty,
            StringComparison.Ordinal);

        // A whitespace-only reason is sent as null. The first cancel left the
        // batch completed, so reopen it and re-arm a cancellable state.
        harness.Record.SetDetail(BatchDetail(98, "ready"));
        await harness.CallAsync("OpenBatchAsync", 98L);
        await harness.SetAsync("cancellationReason", "   ");
        harness.Record.Posts.Clear();
        await harness.CallAsync("CancelCurrentBatchAsync");
        post = Assert.Single(harness.Record.Posts);
        Assert.Equal("/api/administrative-action-batches/98/cancel", post.Path);
        Assert.Equal(JsonValueKind.Null, post.Body.GetProperty("reason").ValueKind);
    }

    [Fact]
    public async Task PendingMutationCannotIssueASecondPost()
    {
        await using var harness = await CreateHarnessAsync();
        harness.Record.SetDetail(BatchDetail(98, "ready"));
        await harness.RenderPageAsync();
        await harness.CallAsync("OpenBatchAsync", 98L);

        harness.Record.Clear();
        harness.Record.ArmPostGate();
        var confirm = harness.CallAsync("ConfirmCurrentBatchAsync");
        await harness.Record.PostStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await harness.CallAsync("ConfirmCurrentBatchAsync");
        harness.Record.ReleasePostGate();
        await confirm;

        Assert.Single(harness.Record.Posts);
        Assert.False(await harness.ValueAsync<bool>("batchOperation"));
        Assert.Contains(
            "was confirmed",
            await harness.ValueAsync<string?>("success") ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedMutationShowsTheErrorAndIssuesNoAutomaticRetry()
    {
        await using var harness = await CreateHarnessAsync();
        var component = await harness.RenderPageAsync();
        harness.Record.SetDetail(BatchDetail(98, "ready"));
        await harness.CallAsync("OpenBatchAsync", 98L);

        harness.Record.Clear();
        harness.Record.PostFailure = true;
        await harness.CallAsync("ConfirmCurrentBatchAsync");

        // Exactly one POST; no retry chain and no follow-up items/list refresh.
        Assert.Single(harness.Record.Posts);
        Assert.Equal(
            ["/api/administrative-action-batches/98/confirm"],
            harness.Record.Paths);
        Assert.Equal("ready", (await harness.ValueAsync<AdministrativeActionBatchDetailDto?>("currentBatch"))!.Summary.Status);
        Assert.Null(await harness.ValueAsync<string?>("success"));
        Assert.Contains(
            "Confirm failed (simulated)",
            await harness.ValueAsync<string?>("error") ?? string.Empty,
            StringComparison.Ordinal);
        Assert.False(await harness.ValueAsync<bool>("batchOperation"));
        var html = await harness.HtmlAsync(component);
        Assert.Contains("Preparation and execution", html, StringComparison.Ordinal);
        Assert.DoesNotContain("was confirmed", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PagersRenderOnlyForPopulatedViewsAndHonorTheirOwnFilters()
    {
        await using var harness = await CreateHarnessAsync();
        var component = await harness.RenderPageAsync();
        await harness.CallAsync("OpenBatchAsync", 98L);

        // Populated views render both pagers with the correct page copies
        // (120 items / 50 and 51 batches / 25) and disabled Previous on page 1.
        var html = await harness.HtmlAsync(component);
        Assert.Contains(">Page 1 of 3</span>", html, StringComparison.Ordinal);
        Assert.Contains(">Page 1 of 3 · 51 batches</span>", html, StringComparison.Ordinal);
        Assert.Equal(2, CountDisabled(html, ">Previous</button>"));
        Assert.Equal(0, CountDisabled(html, ">Next</button>"));

        // An item status filter with no matches hides the item pager while the
        // list pager stays visible.
        await harness.CallAsync("OnBatchItemStatusChangedAsync", new ChangeEventArgs { Value = "failed" });
        var filteredHtml = await harness.HtmlAsync(component);
        Assert.Contains("No items in this view", filteredHtml, StringComparison.Ordinal);
        Assert.DoesNotContain(">Page 1 of 3</span>", filteredHtml, StringComparison.Ordinal);
        Assert.Contains(">Page 1 of 3 · 51 batches</span>", filteredHtml, StringComparison.Ordinal);

        // Restoring the filter brings the item pager back on page 1.
        await harness.CallAsync("OnBatchItemStatusChangedAsync", new ChangeEventArgs { Value = "" });
        var restoredHtml = await harness.HtmlAsync(component);
        Assert.Contains(">Page 1 of 3</span>", restoredHtml, StringComparison.Ordinal);
        Assert.Equal(1, await harness.ValueAsync<int>("batchItemPage"));
    }

    private static int CountDisabled(string html, string buttonSuffix)
    {
        var count = 0;
        var offset = 0;
        while ((offset = html.IndexOf(buttonSuffix, offset, StringComparison.Ordinal)) >= 0)
        {
            var buttonStart = html.LastIndexOf("<button", offset, StringComparison.Ordinal);
            if (buttonStart >= 0 && html[buttonStart..offset].Contains("disabled", StringComparison.Ordinal))
            {
                count++;
            }
            offset += buttonSuffix.Length;
        }
        return count;
    }

    [Theory]
    [InlineData("detail")]
    [InlineData("items")]
    [InlineData("confirm")]
    public async Task OldHeldResponsesAfterIdentityChangeCannotRestoreOrClearData(string stage)
    {
        await using var harness = await CreateHarnessAsync();
        var component = await harness.RenderPageAsync();
        // The confirm variant needs a ready batch; the detail/items variants
        // only use the ready state as a safe non-live fixture.
        harness.Record.SetDetail(BatchDetail(98, "ready"));
        await harness.CallAsync("OpenBatchAsync", 98L);

        Task pending;
        harness.Record.Clear();
        if (stage == "detail")
        {
            harness.Record.ArmDetailGate();
            pending = harness.CallAsync("OpenBatchAsync", 98L);
            await harness.Record.DetailStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        else if (stage == "items")
        {
            harness.Record.ArmItemsGate();
            pending = harness.CallAsync("LoadBatchItemsAsync");
            await harness.Record.ItemsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        else
        {
            harness.Record.ArmPostGate();
            pending = harness.CallAsync("ConfirmCurrentBatchAsync");
            await harness.Record.PostStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        // Replace the identity while the old response is still in flight, then
        // release it. Only the new identity's cleared state may survive.
        await harness.DispatchAsync(() => harness.Token.Set("other-token"));
        await harness.WaitLoadingAsync();
        harness.Record.ReleaseStage(stage);
        await pending;

        Assert.Null(await harness.ValueAsync<AdministrativeActionBatchDetailDto?>("currentBatch"));
        Assert.Null(await harness.ValueAsync<PagedResult<AdministrativeActionBatchItemDto>?>("batchItems"));
        Assert.Null(await harness.ValueAsync<string?>("success"));
        Assert.Null(await harness.ValueAsync<string?>("error"));
        Assert.False(await harness.ValueAsync<bool>("batchOperation"));
        Assert.False(await harness.ValueAsync<bool>("authorizationDenied"));
        var html = await harness.HtmlAsync(component);
        Assert.DoesNotContain("Preparation and execution", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeniedBatchDetailClearsAdministrativeDataAndControls()
    {
        await using var harness = await CreateHarnessAsync();
        var component = await harness.RenderPageAsync();
        harness.Record.DenyDetail = true;
        await harness.CallAsync("OpenBatchAsync", 98L);

        Assert.True(await harness.ValueAsync<bool>("authorizationDenied"));
        Assert.Null(await harness.ValueAsync<AdministrativeActionBatchDetailDto?>("currentBatch"));
        var html = await harness.HtmlAsync(component);
        Assert.Contains("Workflow administrator permission is required", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Preparation and execution", html, StringComparison.Ordinal);
    }

    public static TheoryData<string, HttpStatusCode> StaleBatchResponses
    {
        get
        {
            var cases = new TheoryData<string, HttpStatusCode>();
            foreach (var stage in new[] { "detail", "items", "list", "confirm" })
            foreach (var status in new[] { HttpStatusCode.OK, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden })
                cases.Add(stage, status);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(StaleBatchResponses))]
    public async Task StaleBatchResponsePreservesPopulatedReplacementAndPendingFlags(string stage, HttpStatusCode status)
    {
        await using var harness = await CreateHarnessAsync();
        var component = await harness.RenderPageAsync();
        harness.Record.SetDetail(BatchDetail(98, "ready"));
        await harness.CallAsync("OpenBatchAsync", 98L);

        var oldGate = harness.Record.ArmStage(stage);
        var oldRequest = harness.CallWithoutRenderAsync(StageMethod(stage));
        await harness.Record.StageStarted(stage).WaitAsync(TimeSpan.FromSeconds(5));
        // The old handler retains its gate; detach it so the new actor can load.
        harness.Record.DetachStage(stage);
        var replacement = BatchDetail(99, "ready") with
        {
            Summary = BatchSummary(99, "ready", null) with { SourceNodeName = "Replacement audit" }
        };
        harness.Record.SetDetail(replacement);
        harness.Record.ItemsResult = new([BatchItem(99) with { BatchId = 99, ErrorDescription = "Replacement item" }], 1, 50, 1);
        harness.Record.ListResult = new([replacement.Summary with { WorkflowKey = "replacement-history" }], 1, 25, 1);
        await harness.DispatchAsync(() => harness.Token.Set("replacement-token"));
        await harness.WaitLoadingAsync();
        await harness.CallAsync("OpenBatchAsync", 99L);

        // Keep a real new-identity request pending while the old request ends.
        var pendingStage = stage == "items" ? "detail" : stage;
        var newGate = harness.Record.ArmStage(pendingStage);
        var newRequest = harness.CallWithoutRenderAsync(StageMethod(pendingStage));
        await harness.Record.StageStarted(pendingStage).WaitAsync(TimeSpan.FromSeconds(5));
        await harness.RerenderAsync();
        var detailBefore = await harness.ValueAsync<AdministrativeActionBatchDetailDto>("currentBatch");
        var itemsBefore = await harness.ValueAsync<PagedResult<AdministrativeActionBatchItemDto>>("batchItems");
        var listBefore = await harness.ValueAsync<PagedResult<AdministrativeActionBatchSummaryDto>>("batchList");
        var htmlBefore = await harness.HtmlAsync(component);
        Assert.Contains("Replacement audit", htmlBefore, StringComparison.Ordinal);
        Assert.Contains("Replacement item", htmlBefore, StringComparison.Ordinal);
        Assert.Contains("replacement-history", htmlBefore, StringComparison.Ordinal);
        var busyField = pendingStage switch { "confirm" => "batchOperation", "list" => "loadingBatches", _ => "refreshingBatch" };
        Assert.True(await harness.ValueAsync<bool>(busyField));
        harness.Record.Clear();

        try
        {
            oldGate.SetResult(status == HttpStatusCode.OK
                ? StageResponse(stage, BatchDetail(98, "completed", BatchUpdatedAt))
                : Response(status, new { error = "Old actor denied" }));
            await oldRequest.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Same(detailBefore, await harness.ValueAsync<AdministrativeActionBatchDetailDto>("currentBatch"));
            Assert.Same(itemsBefore, await harness.ValueAsync<PagedResult<AdministrativeActionBatchItemDto>>("batchItems"));
            Assert.Same(listBefore, await harness.ValueAsync<PagedResult<AdministrativeActionBatchSummaryDto>>("batchList"));
            Assert.True(await harness.ValueAsync<bool>(busyField));
            Assert.False(await harness.ValueAsync<bool>("authorizationDenied"));
            Assert.Null(await harness.ValueAsync<string?>("success"));
            Assert.Null(await harness.ValueAsync<string?>("error"));
            Assert.Equal(htmlBefore, await harness.HtmlAsync(component));
            Assert.Empty(harness.Record.Paths);
        }
        finally
        {
            oldGate.TrySetResult(StageResponse(stage, BatchDetail(98, "ready")));
            newGate.TrySetResult(StageResponse(pendingStage, replacement));
            await Task.WhenAll(oldRequest, newRequest).WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.False(await harness.ValueAsync<bool>(busyField));
    }

    private static string StageMethod(string stage) => stage switch
    {
        "detail" => "RefreshCurrentBatchAsync",
        "items" => "LoadBatchItemsAsync",
        "list" => "LoadBatchListAsync",
        _ => "ConfirmCurrentBatchAsync"
    };

    private static HttpResponseMessage StageResponse(string stage, AdministrativeActionBatchDetailDto detail) =>
        Response(HttpStatusCode.OK, stage switch
        {
            "items" => new PagedResult<AdministrativeActionBatchItemDto>([BatchItem(98)], 1, 50, 1),
            "list" => new PagedResult<AdministrativeActionBatchSummaryDto>([detail.Summary], 1, 25, 1),
            _ => (object)detail
        });

    [Theory]
    [InlineData("preparing")]
    [InlineData("queued")]
    [InlineData("running")]
    [InlineData("cancelled")]
    public async Task LivePollCompletesOneOrderedCycleAndRendersReplacedSections(string status)
    {
        await using var harness = await CreateHarnessAsync();
        var component = await harness.RenderPageAsync();
        var record = harness.Record;
        await harness.SetBatchAsync(BatchDetail(98, status));
        var before = await harness.HtmlAsync(component);
        record.Clear();
        record.ArmDetailGate();
        record.ArmItemsGate();
        record.ArmListGate();
        await record.DetailStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await harness.ValueAsync<bool>("refreshingBatch"));
        await harness.CallWithoutRenderAsync("RefreshCurrentBatchAsync");
        Assert.Equal(["/api/administrative-action-batches/98"], record.Paths);

        var terminal = BatchDetail(98, "completed", BatchUpdatedAt) with
        {
            Summary = BatchSummary(98, "completed", BatchUpdatedAt) with { SourceNodeName = "Audit after poll", SucceededItemCount = 12 }
        };
        record.ReleaseDetailGate(terminal);
        await record.ItemsStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(record.ListStarted.Task.IsCompleted);
        record.ItemsGate!.SetResult(Response(HttpStatusCode.OK,
            new PagedResult<AdministrativeActionBatchItemDto>([BatchItem(101) with { ErrorDescription = "Item after poll" }], 1, 50, 1)));
        await record.ListStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        record.ListGate!.SetResult(Response(HttpStatusCode.OK,
            new PagedResult<AdministrativeActionBatchSummaryDto>([terminal.Summary with { WorkflowKey = "history-after-poll" }], 1, 25, 1)));

        // Read existing output, without forcing StateHasChanged from the harness.
        await WaitUntilAsync(async () => (await harness.HtmlAsync(component)).Contains("history-after-poll", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        var after = await harness.HtmlAsync(component);
        Assert.NotEqual(before, after);
        Assert.Contains("Audit after poll", after, StringComparison.Ordinal);
        Assert.Contains("Item after poll", after, StringComparison.Ordinal);
        Assert.Contains("Completed</span>", after, StringComparison.Ordinal);
        Assert.Contains(">12</strong>", after, StringComparison.Ordinal);
        Assert.False(await harness.ValueAsync<bool>("refreshingBatch"));
        Assert.Equal(
            ["/api/administrative-action-batches/98", "/api/administrative-action-batches/98/items?page=1&pageSize=50", "/api/administrative-action-batches?page=1&pageSize=25"], record.Paths);
    }

    [Theory]
    [InlineData("ready")]
    [InlineData("completed")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task NonLiveBatchDoesNotPoll(string status)
    {
        await using var harness = await CreateHarnessAsync();
        await harness.RenderPageAsync();
        await harness.SetBatchAsync(BatchDetail(98, status, status == "ready" ? null : BatchUpdatedAt));
        harness.Record.Clear();
        await Task.Delay(TimeSpan.FromSeconds(3.5));
        Assert.Empty(harness.Record.Paths);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task PollPermissionLossRendersClearedAdministrativeData(HttpStatusCode status)
    {
        await using var harness = await CreateHarnessAsync();
        var component = await harness.RenderPageAsync();
        await harness.SetBatchAsync(BatchDetail(98, "queued"));
        harness.Record.ArmDetailGate();
        await harness.Record.DetailStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        harness.Record.DetailGate!.SetResult(Response(status, new { error = "denied" }));
        await WaitUntilAsync(async () => (await harness.HtmlAsync(component)).Contains("Workflow administrator permission is required", StringComparison.Ordinal), TimeSpan.FromSeconds(5));
        Assert.Null(await harness.ValueAsync<AdministrativeActionBatchDetailDto?>("currentBatch"));
        Assert.Null(await harness.ValueAsync<PagedResult<AdministrativeActionBatchItemDto>?>("batchItems"));
        Assert.Null(await harness.ValueAsync<PagedResult<AdministrativeActionBatchSummaryDto>?>("batchList"));
        Assert.DoesNotContain("Preparation and execution", await harness.HtmlAsync(component), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("detail")]
    [InlineData("items")]
    public async Task DisposalAwaitsHeldPollAndStopsFollowUpRequestsAndTokenEvents(string stage)
    {
        await using var harness = await CreateHarnessAsync();
        await harness.RenderPageAsync();
        await harness.SetBatchAsync(BatchDetail(98, "queued"));
        var gate = harness.Record.ArmStage(stage);
        await harness.Record.StageStarted(stage).WaitAsync(TimeSpan.FromSeconds(10));
        var detailBefore = await harness.ValueAsync<AdministrativeActionBatchDetailDto>("currentBatch");
        var itemsBefore = await harness.ValueAsync<PagedResult<AdministrativeActionBatchItemDto>?>("batchItems");
        var pathsBefore = harness.Record.Paths.ToArray();
        var dispose = harness.DisposeRendererAsync();
        Assert.True(await harness.ValueAsync<bool>("disposed"));
        Assert.False(dispose.IsCompleted);
        gate.SetResult(StageResponse(stage, BatchDetail(98, "completed", BatchUpdatedAt)));
        await dispose.WaitAsync(TimeSpan.FromSeconds(5));
        var poll = await harness.ValueAsync<Task>("pollTask");
        Assert.True(poll.IsCompletedSuccessfully);
        Assert.Same(detailBefore, await harness.ValueAsync<AdministrativeActionBatchDetailDto>("currentBatch"));
        Assert.Same(itemsBefore, await harness.ValueAsync<PagedResult<AdministrativeActionBatchItemDto>?>("batchItems"));
        harness.Token.Set("after-disposal-token");
        await Task.Delay(TimeSpan.FromSeconds(3.5));
        Assert.Equal(pathsBefore, harness.Record.Paths);
        Assert.Empty(harness.Record.Posts);
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

    // --- Fixture builders ---

    private static AdministrativeActionBatchDetailDto BatchDetail(
        long id,
        string status,
        DateTimeOffset? completedAt = null) =>
        new(
            BatchSummary(id, status, completedAt),
            BatchAction(),
            new Dictionary<string, JsonElement>(),
            JsonSerializer.SerializeToElement(new { }),
            ["admin"],
            null,
            null,
            null,
            null,
            null,
            null,
            BatchUpdatedAt,
            null,
            null,
            null);

    private static AdministrativeActionBatchSummaryDto BatchSummary(
        long id,
        string status,
        DateTimeOffset? completedAt) =>
        new(
            id, "approval", 8, 3, 7, "Approval", AdministrativeActionKinds.DirectFlow,
            14, null, null, null, status, "operator", null,
            1, 5, 3, 0, 0, 1, 0, 0, 0,
            BatchUpdatedAt, BatchUpdatedAt, completedAt);

    private static AdministrativeActionSummaryDto BatchAction() =>
        new(8, 3, AdministrativeActionKinds.DirectFlow, 14, null,
            "Approve", 7, "Approval", 9, "Finished", BpmnFlowNodeTypes.EndEvent, []);

    private static AdministrativeActionBatchItemDto BatchItem(int id) =>
        new(id, 98, AdministrativeActionPositionKinds.UserTask, 74, 42, 74, null, 12, Guid.NewGuid(), 8, 7, 14,
            BatchUpdatedAt, null, null, null, null, null, 1, "succeeded",
            null, null, null, null, BatchUpdatedAt, BatchUpdatedAt, BatchUpdatedAt, BatchUpdatedAt, null);

    private static Task<Harness> CreateHarnessAsync() => Harness.CreateAsync();

    // --- Harness ---

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly HtmlRenderer renderer;
        private readonly CapturingActivator activator = new();
        private readonly HttpClient http;
        public TokenState Token { get; }
        public Record Record { get; } = new();

        private Harness(TokenState token)
        {
            Token = token;
            var http = new HttpClient(new Handler(Record)) { BaseAddress = new Uri("https://flowbit.test") };
            this.http = http;
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(new WorkflowApiClient(http));
            services.AddSingleton(token);
            services.AddSingleton<IComponentActivator>(activator);
            provider = services.BuildServiceProvider();
            renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        }

        public static Task<Harness> CreateAsync()
        {
            var token = new TokenState();
            token.Set("admin-token");
            return Task.FromResult(new Harness(token));
        }

        public Task<HtmlRootComponent> RenderPageAsync() =>
            renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<BatchPage>(ParameterView.Empty));

        public Task<string> HtmlAsync(HtmlRootComponent component) =>
            renderer.Dispatcher.InvokeAsync(component.ToHtmlString);

        public Task RerenderAsync() => DispatchAsync(() =>
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(activator.Batch, null));

        public Task<T> DispatcherInvokeAsync<T>(Func<T> action) => renderer.Dispatcher.InvokeAsync(action);

        public Task DispatchAsync(Action action) => renderer.Dispatcher.InvokeAsync(action);

        public Task CallAsync(string method, params object?[] arguments) => InvokeMethodAsync(method, arguments, invokeStateHasChanged: true);

        /// <summary>
        /// Invokes a page method without forcing a rerender afterwards, for
        /// calls whose continuation may still run after renderer disposal.
        /// </summary>
        public Task CallWithoutRenderAsync(string method, params object?[] arguments) =>
            InvokeMethodAsync(method, arguments, invokeStateHasChanged: false);

        private Task InvokeMethodAsync(string method, object?[] arguments, bool invokeStateHasChanged) =>
            renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = typeof(BatchPage).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(activator.Batch, arguments);
            if (result is Task task) await task;
            if (invokeStateHasChanged)
            {
                typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(activator.Batch, null);
            }
        });

        public Task SetAsync(string field, object? value) => DispatchAsync(() =>
            typeof(BatchPage).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(activator.Batch, value));

        public Task<T> ValueAsync<T>(string field) => renderer.Dispatcher.InvokeAsync(() =>
            (T)(typeof(BatchPage).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(activator.Batch))!);

        /// <summary>
        /// Assigns the page's current batch directly through its field so the
        /// poll observes the staged state without issuing additional requests.
        /// </summary>
        public Task SetBatchAsync(AdministrativeActionBatchDetailDto batch) => DispatchAsync(() =>
        {
            Record.SetDetail(batch);
            typeof(BatchPage).GetField("currentBatch", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(activator.Batch, batch);
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(activator.Batch, null);
        });

        public Task WaitLoadingAsync() => WaitUntilConditionAsync(
            async () => !await DispatcherInvokeAsync(() =>
                (bool)(typeof(BatchPage).GetField("loading", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(activator.Batch)!)),
            TimeSpan.FromSeconds(10));

        public async Task DisposeRendererAsync() => await renderer.DisposeAsync();

        public async ValueTask DisposeAsync()
        {
            await renderer.DisposeAsync();
            await provider.DisposeAsync();
        }

        private static async Task WaitUntilConditionAsync(Func<Task<bool>> condition, TimeSpan timeout)
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
    }

    private sealed class CapturingActivator : IComponentActivator
    {
        public BatchPage? Batch { get; private set; }
        public IComponent CreateInstance(Type componentType)
        {
            var component = (IComponent)Activator.CreateInstance(componentType)!;
            if (component is BatchPage batch) Batch = batch;
            return component;
        }
    }

    private sealed class Handler(Record record) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.PathAndQuery;
            record.Paths.Add(path);
            if (request.Method == HttpMethod.Post)
            {
                var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                record.Posts.Add(new RecordedPost(path, body));
                record.PostStarted.TrySetResult();
                if (record.PostGate is { } gate)
                {
                    return await gate.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                }
                if (record.PostFailure)
                {
                    return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                    {
                        Content = JsonContent.Create(new { error = "Confirm failed (simulated)" })
                    };
                }
                return record.PostResultFactory();
            }

            if (path is "/api/administrative-action-batches/98" or "/api/administrative-action-batches/99")
            {
                record.IncrementDetail();
                record.DetailStarted.TrySetResult();
                if (record.DenyDetail)
                {
                    return new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = JsonContent.Create(new { error = "denied" })
                    };
                }
                if (record.DetailGate is { } gate)
                {
                    return await gate.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                }
                return Response(HttpStatusCode.OK, record.DetailResult);
            }

            if (path.StartsWith("/api/administrative-action-batches/98/items", StringComparison.Ordinal)
                || path.StartsWith("/api/administrative-action-batches/99/items", StringComparison.Ordinal))
            {
                record.IncrementItems();
                record.ItemsStarted.TrySetResult();
                if (record.ItemsGate is { } gate)
                {
                    return await gate.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                }
                if (path.Contains("status=failed", StringComparison.Ordinal))
                {
                    return Response(HttpStatusCode.OK,
                        new PagedResult<AdministrativeActionBatchItemDto>([], 1, 50, 0));
                }
                if (record.ItemsResult is { } items) return Response(HttpStatusCode.OK, items);
                return Response(HttpStatusCode.OK,
                    new PagedResult<AdministrativeActionBatchItemDto>(
                        [BatchItem(record.ItemsCount)], 1, 50, 120));
            }

            if (path.StartsWith("/api/administrative-action-batches?", StringComparison.Ordinal))
            {
                record.IncrementList();
                record.ListStarted.TrySetResult();
                if (record.ListGate is { } gate)
                {
                    return await gate.Task.WaitAsync(TimeSpan.FromSeconds(10), cancellationToken);
                }
                return Response(HttpStatusCode.OK, record.ListResult);
            }

            if (path == "/api/administrative-actions/workflows")
            {
                return Response(HttpStatusCode.OK,
                    new[] { new WorkflowSummaryDto(8, "Approval", "approval", 3, true, true, BatchUpdatedAt) });
            }

            if (path.EndsWith("/administrative-actions/nodes", StringComparison.Ordinal))
            {
                return Response(HttpStatusCode.OK,
                    new[] { new AdministrativeActionSourceNodeDto(8, 3, 7, "Approval", null, false) });
            }

            if (path.Contains("/nodes/7/administrative-actions", StringComparison.Ordinal))
            {
                return Response(HttpStatusCode.OK, new[] { BatchAction() });
            }

            return Response(HttpStatusCode.NotFound, new { error = "unexpected request" });
        }
    }

    private static HttpResponseMessage Response(HttpStatusCode code, object body) =>
        new(code) { Content = JsonContent.Create(body) };

    private sealed record RecordedPost(string Path, JsonElement Body);

    private sealed class Record
    {
        public List<string> Paths { get; } = [];
        public List<RecordedPost> Posts { get; } = [];
        public TaskCompletionSource DetailStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ItemsStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ListStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource PostStarted { get; private set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<HttpResponseMessage>? DetailGate { get; set; }
        public TaskCompletionSource<HttpResponseMessage>? ItemsGate { get; set; }
        public TaskCompletionSource<HttpResponseMessage>? ListGate { get; set; }
        public TaskCompletionSource<HttpResponseMessage>? PostGate { get; set; }

        public volatile AdministrativeActionBatchDetailDto DetailResult = BatchDetail(98, "completed", completedAt: BatchUpdatedAt);
        public PagedResult<AdministrativeActionBatchItemDto>? ItemsResult { get; set; }
        public PagedResult<AdministrativeActionBatchSummaryDto> ListResult { get; set; } =
            new([BatchSummary(98, "completed", BatchUpdatedAt)], 1, 25, 51);
        public bool DenyDetail { get; set; }
        public bool PostFailure { get; set; }
        public Func<HttpResponseMessage> PostResultFactory { get; set; } = () =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(BatchDetail(98, "completed", completedAt: BatchUpdatedAt))
            };

        private int detailCount;
        private int itemsCount;
        private int listCount;
        public int DetailCount => Volatile.Read(ref detailCount);
        public int ItemsCount => Volatile.Read(ref itemsCount);
        public int ListCount => Volatile.Read(ref listCount);

        public void SetDetail(AdministrativeActionBatchDetailDto batch) => DetailResult = batch;
        public void ArmDetailGate() { DetailGate = new(TaskCreationOptions.RunContinuationsAsynchronously); DetailStarted = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public void ArmItemsGate() { ItemsGate = new(TaskCreationOptions.RunContinuationsAsynchronously); ItemsStarted = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public void ArmListGate() { ListGate = new(TaskCreationOptions.RunContinuationsAsynchronously); ListStarted = new(TaskCreationOptions.RunContinuationsAsynchronously); }
        public void ArmPostGate() { PostGate = new(TaskCreationOptions.RunContinuationsAsynchronously); PostStarted = new(TaskCreationOptions.RunContinuationsAsynchronously); }

        public TaskCompletionSource<HttpResponseMessage> ArmStage(string stage)
        {
            switch (stage)
            {
                case "detail": ArmDetailGate(); return DetailGate!;
                case "items": ArmItemsGate(); return ItemsGate!;
                case "list": ArmListGate(); return ListGate!;
                default: ArmPostGate(); return PostGate!;
            }
        }

        public Task StageStarted(string stage) => stage switch
        {
            "detail" => DetailStarted.Task, "items" => ItemsStarted.Task,
            "list" => ListStarted.Task, _ => PostStarted.Task
        };

        public void DetachStage(string stage)
        {
            switch (stage)
            {
                case "detail": DetailGate = null; break;
                case "items": ItemsGate = null; break;
                case "list": ListGate = null; break;
                default: PostGate = null; break;
            }
        }

        public void IncrementDetail() => Interlocked.Increment(ref detailCount);
        public void IncrementItems() => Interlocked.Increment(ref itemsCount);
        public void IncrementList() => Interlocked.Increment(ref listCount);

        public void Clear() => Paths.Clear();

        public void ReleaseDetailGate(AdministrativeActionBatchDetailDto batch) =>
            DetailGate?.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(batch)
            });

        public void ReleasePostGate() =>
            PostGate?.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(BatchDetail(98, "completed", completedAt: BatchUpdatedAt))
            });

        public void ReleaseStage(string stage)
        {
            switch (stage)
            {
                case "detail": ReleaseDetailGate(BatchDetail(98, "queued", completedAt: null)); break;
                case "items": ItemsGate?.TrySetResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = JsonContent.Create(new PagedResult<AdministrativeActionBatchItemDto>(
                        [], 1, 50, 0))
                }); break;
                case "confirm": ReleasePostGate(); break;
            }
        }
    }
}
