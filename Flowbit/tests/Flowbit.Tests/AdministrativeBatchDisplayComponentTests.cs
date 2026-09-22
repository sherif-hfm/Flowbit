extern alias FlowbitUi;

using System.Net;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using AuditSection = FlowbitUi::Flowbit.Ui.Components.Shared.AdministrativeBatches.AdministrativeBatchAudit;
using HistorySection = FlowbitUi::Flowbit.Ui.Components.Shared.AdministrativeBatches.AdministrativeBatchHistory;
using ItemsSection = FlowbitUi::Flowbit.Ui.Components.Shared.AdministrativeBatches.AdministrativeBatchItems;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// Rendering matrix for the administrative batch display components
/// (`AdministrativeBatchAudit`, later items and history). Assertions target
/// meaningful output and unchanged DTO values, not generated markup details.
/// Entity-encoded content is decoded for text assertions; escaping assertions
/// inspect the raw output so Razor encoding stays observable.
/// </summary>
public sealed class AdministrativeBatchDisplayComponentTests
{
    private static readonly DateTimeOffset BatchUpdatedAt =
        DateTimeOffset.Parse("2026-08-10T12:00:00Z");

    // --- Audit ---

    [Fact]
    public async Task AuditRendersAllNineCountLabelsInOrderWithDistinctValues()
    {
        var summary = BatchSummary(status: "completed") with
        {
            TotalItemCount = 11,
            TotalAffectedTaskCount = 22,
            EligibleItemCount = 3,
            IneligibleItemCount = 4,
            SucceededItemCount = 5,
            QueuedItemCount = 6,
            SkippedItemCount = 7,
            FailedItemCount = 8,
            CancelledItemCount = 9,
        };
        var html = await RenderAuditAsync(Detail(summary));
        var decoded = WebUtility.HtmlDecode(html);

        Assert.Contains(">Positions</span>", decoded, StringComparison.Ordinal);
        Assert.Contains(">Affected tasks</span>", decoded, StringComparison.Ordinal);
        Assert.Contains(">Eligible</span>", decoded, StringComparison.Ordinal);
        Assert.Contains(">Ineligible</span>", decoded, StringComparison.Ordinal);
        Assert.Contains(">Succeeded</span>", decoded, StringComparison.Ordinal);
        Assert.Contains(">Queued</span>", decoded, StringComparison.Ordinal);
        Assert.Contains(">Skipped</span>", decoded, StringComparison.Ordinal);
        Assert.Contains(">Failed</span>", decoded, StringComparison.Ordinal);
        Assert.Contains(">Cancelled</span>", decoded, StringComparison.Ordinal);
        var order = new[] { "Positions", "Affected tasks", "Eligible", "Ineligible", "Succeeded", "Queued", "Skipped", "Failed", "Cancelled" };
        var values = new[] { 11, 22, 3, 4, 5, 6, 7, 8, 9 };
        for (var index = 0; index < order.Length; index++)
        {
            var label = decoded.IndexOf($">{order[index]}</span>", StringComparison.Ordinal);
            Assert.True(label >= 0);
            var end = decoded.IndexOf("</div>", label, StringComparison.Ordinal);
            Assert.Contains($">{values[index]:N0}</strong>", decoded[label..end], StringComparison.Ordinal);
        }
        var positions = order.Select(label => decoded.IndexOf($">{label}</span>", StringComparison.Ordinal)).ToArray();
        Assert.All(positions, position => Assert.True(position >= 0));
        for (var index = 1; index < positions.Length; index++)
        {
            Assert.True(positions[index - 1] < positions[index], "Count labels must keep their documented order.");
        }
    }

    [Theory]
    [InlineData(AdministrativeActionKinds.DirectFlow, "", null, "Flow #14 · Task action · Approval → Finished · flow #14")]
    [InlineData(AdministrativeActionKinds.TimerBoundary, "", null, "Timer boundary #9 · Timer boundary · Approval → Finished · flow #14")]
    [InlineData(AdministrativeActionKinds.TimerBoundary, "Wait for approval", null, "Wait for approval · Timer boundary · Approval → Finished · flow #14")]
    [InlineData(AdministrativeActionKinds.TimerBoundary, "Wait for approval", "Timeout", "Wait for approval · Timer boundary · Approval → Finished · flow #14")]
    [InlineData(AdministrativeActionKinds.DirectFlow, "Approve order", null, "Approve order · Task action · Approval → Finished · flow #14")]
    public async Task AuditActionLabelKeepsNameAndFallbackRules(string kind, string name, string? boundaryName, string expected)
    {
        var summary = BatchSummary(status: "completed");
        var action = BatchAction(kind, name, boundaryName);
        var html = await RenderAuditAsync(Detail(summary, action: action));

        Assert.Contains($">{expected}</dd>", WebUtility.HtmlDecode(html), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "Not applicable")]
    [InlineData(AdministrativeActionMultiInstanceModes.ForceParent, "Force parent")]
    [InlineData(AdministrativeActionMultiInstanceModes.CompleteAllChildren, "Complete all unfinished children")]
    public async Task AuditRendersNullAndBothMultiInstanceModes(string? mode, string expected)
    {
        var summary = BatchSummary(status: "completed", multiInstanceMode: mode);
        var html = await RenderAuditAsync(Detail(summary));

        Assert.Contains($">{expected}</dd>", WebUtility.HtmlDecode(html), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null, "Not provided")]
    [InlineData("", "Not provided")]
    [InlineData("   ", "Not provided")]
    [InlineData(" Imports corrected ", " Imports corrected ")]
    public async Task AuditReasonKeepsBlankFallbackAndExactText(string? reason, string expected)
    {
        var summary = BatchSummary(status: "completed", reason: reason);
        var html = await RenderAuditAsync(Detail(summary));

        Assert.Contains($">{expected}</dd>", WebUtility.HtmlDecode(html), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuditRendersUnconfirmedAndConfirmedActorSnapshots(bool confirmed)
    {
        var summary = BatchSummary(status: "completed", confirmedBy: confirmed ? "kim" : null);
        var roles = confirmed ? new[] { "admin", "auditor" } : null;
        var decoded = WebUtility.HtmlDecode(await RenderAuditAsync(Detail(summary, confirmedRoles: roles)));

        Assert.Contains(">operator (admin)</dd>", decoded, StringComparison.Ordinal);
        Assert.Contains(confirmed
            ? ">kim (admin, auditor)</dd>"
            : ">Not confirmed</dd>", decoded, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuditRoleSnapshotsRenderEmptyAndOrderedLists(bool populated)
    {
        var roles = populated ? new[] { "beta", "alpha" } : Array.Empty<string>();
        var summary = BatchSummary(status: "completed");
        var decoded = WebUtility.HtmlDecode(await RenderAuditAsync(Detail(summary, preparedRoles: roles)));

        Assert.Contains(populated
            ? ">operator (beta, alpha)</dd>"
            : ">operator (no roles)</dd>", decoded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AuditRendersCommonVariablesSelectionAndEscapesJson()
    {
        var summary = BatchSummary(status: "completed");
        var commonVariables = new Dictionary<string, JsonElement>
        {
            ["approvalNote"] = JsonSerializer.SerializeToElement("a<b & approved"),
            ["tags"] = JsonSerializer.SerializeToElement(new[] { "x", "y" }),
            ["flag"] = JsonSerializer.SerializeToElement(true),
        };
        var selection = JsonSerializer.SerializeToElement(new { mode = "explicit", positions = new[] { 74 } });
        var html = await RenderAuditAsync(Detail(summary, commonVariables: commonVariables, selection: selection));
        var decoded = WebUtility.HtmlDecode(html);

        Assert.Contains("Common variables", decoded, StringComparison.Ordinal);
        Assert.Contains("Selection snapshot", decoded, StringComparison.Ordinal);
        // The JSON serializer escapes HTML-sensitive characters and Razor
        // encodes the result, so the value can never become executable markup.
        Assert.Contains("a\\u003Cb \\u0026 approved", html, StringComparison.Ordinal);
        Assert.DoesNotContain("a<b", html, StringComparison.Ordinal);
        Assert.Contains("\"approvalNote\"", decoded, StringComparison.Ordinal);
        Assert.Contains("\"tags\"", decoded, StringComparison.Ordinal);
        Assert.Contains("\"mode\"", decoded, StringComparison.Ordinal);
        // Indented serialization distinguishes administrative JSON rendering.
        Assert.Contains($"{Environment.NewLine}  \"approvalNote\":", decoded, StringComparison.Ordinal);
        Assert.Contains($"{Environment.NewLine}  \"mode\": \"explicit\"", decoded, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AuditPreparationIssuesAreOptional(bool hasIssues)
    {
        var summary = BatchSummary(status: "completed");
        var issues = JsonSerializer.SerializeToElement(new[]
        {
            new { code = "unauthorized", message = "Position <73> vanished" },
        });
        var html = await RenderAuditAsync(Detail(summary, issues: hasIssues ? issues : null));

        if (hasIssues)
        {
            Assert.Contains("Batch preparation issues", html, StringComparison.Ordinal);
            // The JSON serializer escapes HTML-sensitive characters; Razor
            // encodes the rest, so the value stays inert text.
            Assert.Contains("Position \\u003C73\\u003E vanished", html, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("Batch preparation issues", html, StringComparison.Ordinal);
        }
    }

    // --- Items ---

    [Fact]
    public async Task ItemsRenderNullLoadingEmptyAndPopulatedBranches()
    {
        var loading = await RenderItemsAsync(null);
        Assert.Contains("skeleton", loading, StringComparison.Ordinal);
        Assert.DoesNotContain("No items in this view", loading, StringComparison.Ordinal);
        Assert.DoesNotContain("<table", loading, StringComparison.Ordinal);

        var empty = await RenderItemsAsync([]);
        Assert.Contains("No items in this view", empty, StringComparison.Ordinal);
        Assert.DoesNotContain("skeleton", empty, StringComparison.Ordinal);
        Assert.DoesNotContain("<table", empty, StringComparison.Ordinal);

        var populated = await RenderItemsAsync([BatchItem()]);
        Assert.Contains("<table", populated, StringComparison.Ordinal);
        Assert.DoesNotContain("No items in this view", populated, StringComparison.Ordinal);
        Assert.DoesNotContain("skeleton", populated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ItemsPreserveSuppliedOrderPositionLabelsLinksAndFences()
    {
        var first = BatchItem(id: 9, instanceId: 42, positionKind: AdministrativeActionPositionKinds.UserTask, positionId: 74);
        var second = BatchItem(id: 3, instanceId: 41, positionKind: AdministrativeActionPositionKinds.MultiInstanceExecution, positionId: 73);
        var html = await RenderItemsAsync([first, second]);
        var decoded = WebUtility.HtmlDecode(html);

        // Deliberately non-sorted input order is preserved.
        Assert.True(
            decoded.IndexOf(">Instance #42</a>", StringComparison.Ordinal) <
            decoded.IndexOf(">Instance #41</a>", StringComparison.Ordinal),
            "Items must render in the supplied order.");
        Assert.Contains("href=\"instances/42\"", html, StringComparison.Ordinal);
        Assert.Contains("href=\"instances/41\"", html, StringComparison.Ordinal);
        Assert.Contains(">User task #74 · token #12</span>", decoded, StringComparison.Ordinal);
        Assert.Contains(">MI execution #73 · token #12</span>", decoded, StringComparison.Ordinal);
        Assert.Contains(">Definition #8 · node #7<span", decoded, StringComparison.Ordinal);
        Assert.Contains(">Flow #102</span>", decoded, StringComparison.Ordinal);
        Assert.DoesNotContain("timer subscription", decoded, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ItemsRenderOptionalTimerSubscription(bool hasTimer)
    {
        var item = BatchItem(timerSubscriptionId: hasTimer ? 51 : null);
        var decoded = WebUtility.HtmlDecode(await RenderItemsAsync([item]));

        Assert.Equal(hasTimer, decoded.Contains(">Flow #102 · timer subscription #51</span>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ItemsRenderStatusAffectedCountAndLocalDate()
    {
        var updatedAt = DateTimeOffset.Parse("2026-08-10T12:00:00Z");
        var item = BatchItem(status: "failed", affectedTaskCount: 6, updatedAt: updatedAt);
        var html = await RenderItemsAsync([item]);
        var decoded = WebUtility.HtmlDecode(html);

        Assert.Contains(">6 task(s)</td>", decoded, StringComparison.Ordinal);
        Assert.Contains("status-badge status-danger", html, StringComparison.Ordinal);
        Assert.Contains("Failed</span>", decoded, StringComparison.Ordinal);
        // Local timestamps are derived with the same environment, so culture
        // and timezone differences cannot make this assertion brittle.
        Assert.Contains(updatedAt.ToLocalTime().ToString("g"), decoded, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("error")]
    [InlineData("issues")]
    [InlineData("issuesOverResult")]
    [InlineData("result")]
    [InlineData("empty")]
    [InlineData("jsonNull")]
    public async Task ItemsResultPrecedenceBranches(string branch)
    {
        var issues = JsonSerializer.SerializeToElement(new[] { new { code = "gate", message = "Complex gateway is <closed>" } });
        var result = JsonSerializer.SerializeToElement(new { instanceStatus = "completed" });
        var item = branch switch
        {
            "error" => BatchItem(errorDescription: "Escalation failed", issues: issues, result: result),
            "issues" => BatchItem(errorDescription: "   ", issues: issues, result: result),
            "issuesOverResult" => BatchItem(issues: issues, result: result),
            "result" => BatchItem(result: result),
            "jsonNull" => BatchItem(result: JsonSerializer.SerializeToElement< object?>(null)),
            _ => BatchItem(),
        };
        var html = await RenderItemsAsync([item]);
        var decoded = WebUtility.HtmlDecode(html);

        switch (branch)
        {
            case "error":
                Assert.Contains(">Escalation failed</span>", decoded, StringComparison.Ordinal);
                Assert.DoesNotContain(">Issues</summary>", html, StringComparison.Ordinal);
                Assert.DoesNotContain(">Result</summary>", html, StringComparison.Ordinal);
                break;
            case "issues":
                Assert.Contains(">Issues</summary>", html, StringComparison.Ordinal);
                Assert.DoesNotContain(">Result</summary>", html, StringComparison.Ordinal);
                break;
            case "issuesOverResult":
                Assert.Contains(">Issues</summary>", html, StringComparison.Ordinal);
                Assert.DoesNotContain(">Result</summary>", html, StringComparison.Ordinal);
                break;
            case "result":
                Assert.Contains(">Result</summary>", html, StringComparison.Ordinal);
                Assert.DoesNotContain(">Issues</summary>", html, StringComparison.Ordinal);
                break;
            case "jsonNull":
                // A present JSON null keeps the Result branch with its null text.
                Assert.Contains(">Result</summary>", html, StringComparison.Ordinal);
                Assert.Contains(">null</pre>", html, StringComparison.Ordinal);
                break;
            default:
                Assert.Contains("—</span>", decoded, StringComparison.Ordinal);
                Assert.DoesNotContain(">Issues</summary>", html, StringComparison.Ordinal);
                Assert.DoesNotContain(">Result</summary>", html, StringComparison.Ordinal);
                break;
        }
    }

    [Fact]
    public async Task ItemsErrorTextStaysEscapedAndInert()
    {
        var item = BatchItem(errorDescription: "Escalated <b>now</b> & done");
        var html = await RenderItemsAsync([item]);

        Assert.Contains("Escalated &lt;b&gt;now&lt;/b&gt; &amp; done", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Escalated <b", html, StringComparison.Ordinal);
    }


    [Fact]
    public async Task AuditParameterReplacementReplacesAllDisplayedValues()
    {
        await using var host = await AuditHost.CreateAsync(Detail(
            BatchSummary(status: "ready", reason: "First reason"),
            action: BatchAction(AdministrativeActionKinds.DirectFlow, "First action", null),
            preparedRoles: ["admin"],
            selection: JsonSerializer.SerializeToElement(new { mode = "explicit" }),
            commonVariables: new Dictionary<string, JsonElement>
            {
                ["approvalNote"] = JsonSerializer.SerializeToElement("first note"),
            }));
        var firstHtml = await host.HtmlAsync();
        var firstDecoded = WebUtility.HtmlDecode(firstHtml);
        Assert.Contains("First reason", firstDecoded, StringComparison.Ordinal);
        Assert.Contains("first note", firstDecoded, StringComparison.Ordinal);
        Assert.Contains("explicit", firstDecoded, StringComparison.Ordinal);

        await host.ReplaceAsync(Detail(
            BatchSummary(status: "completed", reason: "Second reason", confirmedBy: "kim"),
            action: BatchAction(AdministrativeActionKinds.TimerBoundary, "Second timer", "Timeout"),
            preparedRoles: ["auditor"],
            confirmedRoles: ["auditor"],
            selection: JsonSerializer.SerializeToElement(new { mode = "allMatching" }),
            commonVariables: new Dictionary<string, JsonElement>
            {
                ["approvalNote"] = JsonSerializer.SerializeToElement("second note"),
            }));
        var secondHtml = await host.HtmlAsync();
        var secondDecoded = WebUtility.HtmlDecode(secondHtml);

        Assert.Contains("Second reason", secondDecoded, StringComparison.Ordinal);
        Assert.Contains("second note", secondDecoded, StringComparison.Ordinal);
        Assert.Contains("Second timer", secondDecoded, StringComparison.Ordinal);
        Assert.Contains("kim (auditor)", secondDecoded, StringComparison.Ordinal);
        Assert.Contains("allMatching", secondDecoded, StringComparison.Ordinal);
        Assert.DoesNotContain("First reason", secondDecoded, StringComparison.Ordinal);
        Assert.DoesNotContain("first note", secondDecoded, StringComparison.Ordinal);
        Assert.DoesNotContain("First action", secondDecoded, StringComparison.Ordinal);
        Assert.DoesNotContain("explicit", secondDecoded, StringComparison.Ordinal);
    }

    // --- History ---

    [Fact]
    public async Task HistoryRenderNullLoadingEmptyAndPopulatedBranches()
    {
        var loading = await RenderHistoryAsync(null, currentBatchId: null);
        Assert.Contains("skeleton", loading, StringComparison.Ordinal);
        Assert.DoesNotContain("No administrative batches", loading, StringComparison.Ordinal);
        Assert.DoesNotContain("<table", loading, StringComparison.Ordinal);

        var empty = await RenderHistoryAsync([], currentBatchId: null);
        Assert.Contains("No administrative batches", empty, StringComparison.Ordinal);
        Assert.DoesNotContain("skeleton", empty, StringComparison.Ordinal);
        Assert.DoesNotContain("<table", empty, StringComparison.Ordinal);

        var populated = await RenderHistoryAsync(
            [BatchSummary(status: "completed"), BatchSummary(status: "ready", multiInstanceMode: AdministrativeActionMultiInstanceModes.CompleteAllChildren)],
            currentBatchId: 98);
        Assert.Contains("<table", populated, StringComparison.Ordinal);
        Assert.DoesNotContain("No administrative batches", populated, StringComparison.Ordinal);
        Assert.DoesNotContain("skeleton", populated, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HistoryPreservesReceivedRowOrderAndLabels()
    {
        var rows = new[]
        {
            BatchSummary(status: "ready", reason: null, multiInstanceMode: null) with { Id = 104, SucceededItemCount = 0, TotalItemCount = 2, TotalAffectedTaskCount = 9, FailedItemCount = 1 },
            BatchSummary(status: "completed", multiInstanceMode: AdministrativeActionMultiInstanceModes.CompleteAllChildren) with { Id = 3 },
        };
        var html = await RenderHistoryAsync(rows, currentBatchId: 104);
        var decoded = WebUtility.HtmlDecode(html);

        // Received order, not a re-sort.
        Assert.True(
            decoded.IndexOf(">#104</td>", StringComparison.Ordinal) <
            decoded.IndexOf(">#3</td>", StringComparison.Ordinal),
            "Rows must render in the received order.");
        Assert.Contains(">approval · v3<span", decoded, StringComparison.Ordinal);
        Assert.Contains(">Definition #8 · Approval (#7)</span>", decoded, StringComparison.Ordinal);
        Assert.Contains(">Task action · flow #14<span", decoded, StringComparison.Ordinal);
        Assert.Contains(">Standard position</span>", decoded, StringComparison.Ordinal);
        Assert.Contains(">Complete all unfinished children</span>", decoded, StringComparison.Ordinal);
        Assert.Contains(">operator</td>", decoded, StringComparison.Ordinal);
        Assert.Contains(">0 / 2<span", decoded, StringComparison.Ordinal);
        Assert.Contains(">9 affected · 1 failed</span>", decoded, StringComparison.Ordinal);
        // One Open control per row, exactly two in total.
        Assert.Contains("btn-sm btn-outline-primary", html, StringComparison.Ordinal);
        Assert.Equal(2, CountOccurrences(decoded, ">Open</button>"));
        // The current batch's row is highlighted; the other is not.
        Assert.True(RowHasClass(decoded, 104, "table-active"), "The current batch's row must be highlighted.");
        Assert.False(RowHasClass(decoded, 3, "table-active"), "The other row must not be highlighted.");
    }

    [Fact]
    public async Task HistoryRowDateAndStatusRenderWithLocalFormatting()
    {
        var updatedAt = DateTimeOffset.Parse("2026-08-10T12:00:00Z");
        var row = BatchSummary(status: "completed") with { UpdatedAt = updatedAt };
        var html = await RenderHistoryAsync([row], currentBatchId: null);
        var decoded = WebUtility.HtmlDecode(html);

        Assert.Contains("status-badge status-primary", html, StringComparison.Ordinal);
        Assert.Contains("Completed</span>", decoded, StringComparison.Ordinal);
        Assert.Contains(updatedAt.ToLocalTime().ToString("g"), decoded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task HistoryParameterReplacementMovesHighlightAndReplacesRows()
    {
        var first = new[] { BatchSummary(status: "completed") with { Id = 10 }, BatchSummary(status: "ready") with { Id = 11 } };
        await using var host = await HistoryHost.CreateAsync(first, currentBatchId: 10);
        var firstHtml = WebUtility.HtmlDecode(await host.HtmlAsync());
        Assert.Contains(">#10</td>", firstHtml, StringComparison.Ordinal);
        Assert.True(RowHasClass(firstHtml, 10, "table-active"), "Batch 10 must be highlighted initially.");
        Assert.False(RowHasClass(firstHtml, 11, "table-active"), "Batch 11 must not be highlighted.");

        var second = new[] { BatchSummary(status: "ready") with { Id = 10 }, BatchSummary(status: "completed") with { Id = 12 } };
        await host.ReplaceAsync(second, currentBatchId: 12);
        var secondHtml = WebUtility.HtmlDecode(await host.HtmlAsync());

        Assert.Contains(">#12</td>", secondHtml, StringComparison.Ordinal);
        Assert.True(RowHasClass(secondHtml, 12, "table-active"), "Batch 12 must be highlighted after replacement.");
        Assert.False(RowHasClass(secondHtml, 10, "table-active"), "Batch 10 must not stay highlighted.");
    }

    /// <summary>True when the row whose first cell shows the batch id carries
    /// the given class (the class sits on the row element).</summary>
    private static bool RowHasClass(string html, long batchId, string className)
    {
        var cell = html.IndexOf($"#{batchId}</td>", StringComparison.Ordinal);
        if (cell < 0) return false;
        var rowStart = html.LastIndexOf("<tr", cell, StringComparison.Ordinal);
        var rowEnd = html.IndexOf("</tr>", cell, StringComparison.Ordinal);
        var classIndex = html.IndexOf(className, rowStart, StringComparison.Ordinal);
        return classIndex >= 0 && classIndex < rowEnd;
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        var offset = 0;
        while ((offset = text.IndexOf(needle, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += needle.Length;
        }
        return count;
    }

    private static async Task<string> RenderAuditAsync(AdministrativeActionBatchDetailDto batch)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<AuditSection>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(AuditSection.Batch)] = batch }));
            return component.ToHtmlString();
        });
    }

    private static async Task<string> RenderHistoryAsync(
        IReadOnlyList<AdministrativeActionBatchSummaryDto>? batches,
        long? currentBatchId)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<HistorySection>(ParameterView.FromDictionary(
                new Dictionary<string, object?>
                {
                    [nameof(HistorySection.Batches)] = batches,
                    [nameof(HistorySection.CurrentBatchId)] = currentBatchId,
                }));
            return component.ToHtmlString();
        });
    }

    private static AdministrativeActionBatchSummaryDto BatchSummary(
        string status,
        string? reason = null,
        string? multiInstanceMode = null,
        string? confirmedBy = null,
        string actionKind = AdministrativeActionKinds.DirectFlow,
        string? boundaryName = null) =>
        new(
            98, "approval", 8, 3, 7, "Approval", actionKind,
            14, null, multiInstanceMode, reason, status, "operator", confirmedBy,
            1, 5, 3, 0, 0, 1, 0, 0, 0,
            BatchUpdatedAt, BatchUpdatedAt, status == "completed" ? BatchUpdatedAt : null);

    private static AdministrativeActionBatchItemDto BatchItem(
        long id = 1,
        long instanceId = 42,
        string positionKind = AdministrativeActionPositionKinds.UserTask,
        long positionId = 74,
        long? timerSubscriptionId = null,
        string status = "succeeded",
        int affectedTaskCount = 1,
        DateTimeOffset? updatedAt = null,
        JsonElement? issues = null,
        JsonElement? result = null,
        string? errorDescription = null) =>
        new(id, 98, positionKind, positionId, instanceId, 74, null, 12, Guid.NewGuid(), 8, 7, 102,
            BatchUpdatedAt, timerSubscriptionId, null, null, null, null, affectedTaskCount, status,
            issues, result, null, errorDescription, BatchUpdatedAt, updatedAt ?? BatchUpdatedAt, null, null, null);

    private static async Task<string> RenderItemsAsync(IReadOnlyList<AdministrativeActionBatchItemDto>? items)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        await using var provider = services.BuildServiceProvider();
        await using var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        return await renderer.Dispatcher.InvokeAsync(async () =>
        {
            var component = await renderer.RenderComponentAsync<ItemsSection>(ParameterView.FromDictionary(
                new Dictionary<string, object?> { [nameof(ItemsSection.Items)] = items }));
            return component.ToHtmlString();
        });
    }

    private static AdministrativeActionSummaryDto BatchAction(
        string actionKind,
        string name = "",
        string? boundaryName = null) =>
        new(8, 3, actionKind, 14, null, name, 7, "Approval", 9, "Finished", BpmnFlowNodeTypes.EndEvent, [])
        {
            BoundaryNodeId = actionKind == AdministrativeActionKinds.TimerBoundary ? 9 : null,
            BoundaryNodeName = boundaryName,
        };

    private static AdministrativeActionBatchDetailDto Detail(
        AdministrativeActionBatchSummaryDto? summary = null,
        AdministrativeActionSummaryDto? action = null,
        Dictionary<string, JsonElement>? commonVariables = null,
        JsonElement? selection = null,
        IReadOnlyList<string>? preparedRoles = null,
        IReadOnlyList<string>? confirmedRoles = null,
        JsonElement? issues = null) =>
        new(
            summary ?? BatchSummary("completed"),
            action ?? BatchAction(AdministrativeActionKinds.DirectFlow, "Approve", null),
            commonVariables ?? new Dictionary<string, JsonElement>(),
            selection ?? JsonSerializer.SerializeToElement(new { mode = "explicit" }),
            preparedRoles ?? ["admin"],
            confirmedRoles,
            issues,
            null,
            null,
            null,
            null,
            BatchUpdatedAt,
            null,
            null,
            null);

    /// <summary>Small test-only parent that owns mutable test parameters and
    /// rerenders the retained audit child on the dispatcher.</summary>
    private sealed class AuditHost : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly HtmlRenderer renderer;
        private HostComponent? host;
        private HtmlRootComponent rendered;

        private AuditHost()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            provider = services.BuildServiceProvider();
            renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        }

        public static async Task<AuditHost> CreateAsync(AdministrativeActionBatchDetailDto batch)
        {
            var instance = new AuditHost();
            instance.rendered = await instance.renderer.Dispatcher.InvokeAsync(
                () => instance.renderer.RenderComponentAsync<HostComponent>(
                    ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        [nameof(HostComponent.Capture)] = (Action<HostComponent>)(captured => instance.host = captured),
                        [nameof(HostComponent.Batch)] = batch,
                    })));
            return instance;
        }

        public Task<string> HtmlAsync() => renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);

        public Task ReplaceAsync(AdministrativeActionBatchDetailDto batch) => renderer.Dispatcher.InvokeAsync(() =>
        {
            host!.Batch = batch;
            typeof(ComponentBase)
                .GetMethod("StateHasChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .Invoke(host, null);
        });

        public async ValueTask DisposeAsync()
        {
            await renderer.DisposeAsync();
            await provider.DisposeAsync();
        }

        private sealed class HostComponent : ComponentBase
        {
            [Parameter] public Action<HostComponent>? Capture { get; set; }
            [Parameter] public AdministrativeActionBatchDetailDto? Batch { get; set; }

            protected override void OnInitialized() => Capture?.Invoke(this);

            protected override void BuildRenderTree(RenderTreeBuilder builder)
            {
                if (Batch is null)
                {
                    builder.AddContent(0, "loading");
                    return;
                }
                builder.OpenComponent<AuditSection>(0);
                builder.AddAttribute(1, nameof(AuditSection.Batch), Batch);
                builder.CloseComponent();
            }
        }
    }

    /// <summary>Small test-only parent that owns mutable history parameters
    /// and rerenders the retained history child on the dispatcher.</summary>
    private sealed class HistoryHost : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly HtmlRenderer renderer;
        private HostComponent? host;
        private HtmlRootComponent rendered;

        private HistoryHost()
        {
            var services = new ServiceCollection();
            services.AddLogging();
            provider = services.BuildServiceProvider();
            renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        }

        public static async Task<HistoryHost> CreateAsync(
            IReadOnlyList<AdministrativeActionBatchSummaryDto> batches,
            long? currentBatchId)
        {
            var instance = new HistoryHost();
            instance.rendered = await instance.renderer.Dispatcher.InvokeAsync(
                () => instance.renderer.RenderComponentAsync<HostComponent>(
                    ParameterView.FromDictionary(new Dictionary<string, object?>
                    {
                        [nameof(HostComponent.Capture)] = (Action<HostComponent>)(captured => instance.host = captured),
                        [nameof(HostComponent.Batches)] = batches,
                        [nameof(HostComponent.CurrentBatchId)] = currentBatchId,
                    })));
            return instance;
        }

        public Task<string> HtmlAsync() => renderer.Dispatcher.InvokeAsync(rendered.ToHtmlString);

        public Task ReplaceAsync(IReadOnlyList<AdministrativeActionBatchSummaryDto> batches, long? currentBatchId) =>
            renderer.Dispatcher.InvokeAsync(() =>
            {
                host!.Batches = batches;
                host.CurrentBatchId = currentBatchId;
                typeof(ComponentBase)
                    .GetMethod("StateHasChanged", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .Invoke(host, null);
            });

        public async ValueTask DisposeAsync()
        {
            await renderer.DisposeAsync();
            await provider.DisposeAsync();
        }

        private sealed class HostComponent : ComponentBase
        {
            [Parameter] public Action<HostComponent>? Capture { get; set; }
            [Parameter] public IReadOnlyList<AdministrativeActionBatchSummaryDto>? Batches { get; set; }
            [Parameter] public long? CurrentBatchId { get; set; }

            protected override void OnInitialized() => Capture?.Invoke(this);

            protected override void BuildRenderTree(RenderTreeBuilder builder)
            {
                builder.OpenComponent<HistorySection>(0);
                builder.AddAttribute(1, nameof(HistorySection.Batches), Batches);
                builder.AddAttribute(2, nameof(HistorySection.CurrentBatchId), CurrentBatchId);
                builder.CloseComponent();
            }
        }
    }
}
