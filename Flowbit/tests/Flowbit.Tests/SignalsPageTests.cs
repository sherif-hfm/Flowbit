extern alias FlowbitUi;

using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SignalScopeChoice = FlowbitUi::Flowbit.Ui.Components.Shared.Signals.SignalScopeChoice;
using SignalsPage = FlowbitUi::Flowbit.Ui.Components.Pages.Signals;
using TokenState = FlowbitUi::Flowbit.Ui.Auth.TokenState;
using TypedInputEventArgs = FlowbitUi::Flowbit.Ui.Components.Shared.TypedInputEventArgs;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// The `/signals` page: a reviewed throw sends one request with a draft idempotency key,
/// the page opens the new broadcast and its deliveries, a permission failure clears every
/// signal value, and the three-second poll refreshes only a broadcast that still delivers.
/// </summary>
public sealed class SignalsPageTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-16T10:00:00Z");

    [Fact]
    public async Task ReviewedThrow_SendsTheScopeAndADraftKeyThenOpensTheBroadcast()
    {
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        Assert.Contains("Choose a signal and its scope", await harness.HtmlAsync(component), StringComparison.Ordinal);

        await harness.SetAsync("signalName", "claims.policy-changed");
        await harness.SetAsync("workflowKey", "claims");
        await harness.SetAsync("payloadText", "{ \"version\": 7 }");
        await harness.CallAsync("ReviewAsync");
        var html = await harness.HtmlAsync(component);
        Assert.Contains("Reaches now", html, StringComparison.Ordinal);
        Assert.Contains("3 waits", html, StringComparison.Ordinal);

        await harness.CallAsync("ThrowAsync");
        var post = Assert.Single(harness.Posts);
        Assert.Equal("/api/signal-broadcasts", post.Path);
        Assert.Equal("claims.policy-changed", post.Body.GetProperty("name").GetString());
        Assert.Equal("claims", post.Body.GetProperty("workflowKey").GetString());
        Assert.False(post.Body.GetProperty("allWorkflows").GetBoolean());
        Assert.Equal(7, post.Body.GetProperty("payload").GetProperty("version").GetInt32());
        Assert.StartsWith("ui-", post.Body.GetProperty("idempotencyKey").GetString(), StringComparison.Ordinal);

        Assert.Equal(41L, (await harness.ValueAsync<SignalBroadcastDetailDto?>("currentBroadcast"))!.Id);
        Assert.Contains(harness.Paths, path => path.StartsWith("/api/signal-broadcasts/41/deliveries", StringComparison.Ordinal));
        Assert.Contains("Broadcast #41 froze 3 recipients", await harness.HtmlAsync(component), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Review_RefusesAPayloadThatIsNotAJsonObject()
    {
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        await harness.SetAsync("signalName", "claims.policy-changed");
        await harness.SetAsync("scopeChoice", SignalScopeChoice.AllWorkflows);
        await harness.SetAsync("payloadText", "[1, 2]");
        await harness.CallAsync("ReviewAsync");

        Assert.Contains("The payload must be a JSON object", await harness.HtmlAsync(component), StringComparison.Ordinal);
        Assert.Null(await harness.ValueAsync<object?>("review"));
        Assert.Empty(harness.Posts);
    }

    [Fact]
    public async Task Review_ShowsThePayloadAndAnEditEndsIt()
    {
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        await harness.SetAsync("signalName", "claims.policy-changed");
        await harness.SetAsync("scopeChoice", SignalScopeChoice.AllWorkflows);
        await harness.SetAsync("payloadText", "{ \"version\": 7 }");
        await harness.CallAsync("ReviewAsync");
        var html = await harness.HtmlAsync(component);
        Assert.Contains("signal-review-payload", html, StringComparison.Ordinal);
        Assert.Contains("&quot;version&quot;: 7", html, StringComparison.Ordinal);

        // The text area's change event runs OnPayloadEdited after binding the new text.
        await harness.SetAsync("payloadText", "{ \"version\": 8 }");
        await harness.CallAsync("OnPayloadEdited");
        Assert.Null(await harness.ValueAsync<object?>("review"));
        Assert.Null(await harness.ValueAsync<string?>("draftIdempotencyKey"));
        Assert.Contains("Review signal", await harness.HtmlAsync(component), StringComparison.Ordinal);
        Assert.Empty(harness.Posts);
    }

    [Fact]
    public async Task Throw_RefusesAPayloadChangedSinceTheReview()
    {
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        await harness.SetAsync("signalName", "claims.policy-changed");
        await harness.SetAsync("scopeChoice", SignalScopeChoice.AllWorkflows);
        await harness.SetAsync("payloadText", "{ \"version\": 7 }");
        await harness.CallAsync("ReviewAsync");

        // A click that reaches the server before the text area's change event.
        await harness.SetAsync("payloadText", "{ \"version\": 8 }");
        await harness.CallAsync("ThrowAsync");

        Assert.Empty(harness.Posts);
        Assert.Null(await harness.ValueAsync<object?>("review"));
        Assert.Contains("The payload changed after the review", await harness.HtmlAsync(component), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PermissionFailure_ClearsSignalDataAndShowsThePermissionMessage()
    {
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        await harness.SetAsync("signalName", "claims.policy-changed");
        await harness.SetAsync("workflowKey", "claims");
        await harness.SetAsync("payloadText", "{ \"version\": 7 }");
        await harness.CallAsync("OpenBroadcastAsync", 41L);
        Assert.NotNull(await harness.ValueAsync<SignalBroadcastDetailDto?>("currentBroadcast"));

        harness.Deny = true;
        await harness.CallAsync("RefreshCurrentBroadcastAsync");

        Assert.True(await harness.ValueAsync<bool>("authorizationDenied"));
        Assert.Null(await harness.ValueAsync<SignalBroadcastDetailDto?>("currentBroadcast"));
        Assert.Null(await harness.ValueAsync<PagedResult<SignalBroadcastSummaryDto>?>("history"));
        Assert.Equal(string.Empty, await harness.ValueAsync<string>("signalName"));
        Assert.Equal(string.Empty, await harness.ValueAsync<string>("workflowKey"));
        Assert.Null(await harness.ValueAsync<string?>("payloadText"));
        var html = await harness.HtmlAsync(component);
        Assert.Contains("Signal permission is required", html, StringComparison.Ordinal);
        Assert.DoesNotContain("claims.policy-changed", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdentityChange_ClearsTheDraft()
    {
        await using var harness = new Harness();
        await harness.RenderAsync();
        await harness.SetAsync("signalName", "claims.policy-changed");
        await harness.SetAsync("scopeChoice", SignalScopeChoice.AllWorkflows);
        await harness.SetAsync("payloadText", "{ \"version\": 7 }");
        await harness.CallAsync("ReviewAsync");
        Assert.NotNull(await harness.ValueAsync<object?>("review"));

        await harness.ChangeIdentityAsync("other-token");

        Assert.Equal(string.Empty, await harness.ValueAsync<string>("signalName"));
        Assert.Equal(SignalScopeChoice.Family, await harness.ValueAsync<SignalScopeChoice>("scopeChoice"));
        Assert.Null(await harness.ValueAsync<string?>("payloadText"));
        Assert.Null(await harness.ValueAsync<object?>("review"));
    }

    [Fact]
    public async Task LateRefreshOfAnEarlierBroadcast_DoesNotReplaceTheOneOpenedSince()
    {
        await using var harness = new Harness();
        await harness.RenderAsync();
        harness.DetailStatus = SignalBroadcastStatuses.Completed;
        await harness.CallAsync("OpenBroadcastAsync", 41L);

        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.HoldDetail41 = held.Task;
        var before = harness.Paths.Count(path => path == "/api/signal-broadcasts/41");
        var refreshing = harness.CallAsync("RefreshCurrentBroadcastAsync");
        await Harness.WaitForAsync(() => harness.Paths.Count(path => path == "/api/signal-broadcasts/41") > before);

        await harness.CallAsync("OpenBroadcastAsync", 42L);
        held.SetResult();
        await refreshing;

        Assert.Equal(42L, (await harness.ValueAsync<SignalBroadcastDetailDto?>("currentBroadcast"))!.Id);
        Assert.Equal(42L, (await harness.ValueAsync<PagedResult<SignalDeliveryDto>?>("deliveries"))!.TotalCount);
    }

    [Fact]
    public async Task LateDeliveriesForAnEarlierFilter_DoNotReplaceTheNewerPage()
    {
        await using var harness = new Harness();
        await harness.RenderAsync();
        harness.DetailStatus = SignalBroadcastStatuses.Completed;
        await harness.CallAsync("OpenBroadcastAsync", 41L);

        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.HoldUnfilteredDeliveries = held.Task;
        var before = harness.Paths.Count(IsUnfilteredDeliveries);
        var loading = harness.CallAsync("ReloadDeliveriesAsync");
        await Harness.WaitForAsync(() => harness.Paths.Count(IsUnfilteredDeliveries) > before);

        await harness.CallAsync("OnDeliveryStatusChangedAsync", new ChangeEventArgs { Value = SignalDeliveryStatuses.Failed });
        held.SetResult();
        await loading;

        Assert.Equal(7L, (await harness.ValueAsync<PagedResult<SignalDeliveryDto>?>("deliveries"))!.TotalCount);

        static bool IsUnfilteredDeliveries(string path) =>
            path.StartsWith("/api/signal-broadcasts/41/deliveries", StringComparison.Ordinal)
            && !path.Contains("status=", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Poll_RefreshesOnlyABroadcastThatStillDelivers()
    {
        await using var harness = new Harness();
        await harness.RenderAsync();
        harness.DetailStatus = SignalBroadcastStatuses.Completed;
        await harness.CallAsync("OpenBroadcastAsync", 41L);
        var before = harness.Paths.Count(path => path == "/api/signal-broadcasts/41");
        await Task.Delay(TimeSpan.FromSeconds(3.5));
        Assert.Equal(before, harness.Paths.Count(path => path == "/api/signal-broadcasts/41"));

        harness.DetailStatus = SignalBroadcastStatuses.Delivering;
        await harness.CallAsync("OpenBroadcastAsync", 41L);
        var live = harness.Paths.Count(path => path == "/api/signal-broadcasts/41");
        await Task.Delay(TimeSpan.FromSeconds(3.5));
        Assert.True(harness.Paths.Count(path => path == "/api/signal-broadcasts/41") > live);
    }

    [Fact]
    public async Task ReviewedThrow_WithABusinessKeySendsItWithItsWorkflowKey()
    {
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        await harness.SetAsync("signalName", "claims.policy-changed");
        await harness.CallAsync("OnWorkflowKeyChangedAsync", new ChangeEventArgs { Value = "claims" });
        await harness.CallAsync("OnBusinessKeyChangedAsync", new ChangeEventArgs { Value = "  ORD-2001 " });
        Assert.Contains("1 wait for &#x27;claims.policy-changed&#x27; with business key ORD-2001 in claims.",
            await harness.HtmlAsync(component), StringComparison.Ordinal);

        await harness.CallAsync("ReviewAsync");
        Assert.Contains(harness.Paths, path => path.StartsWith("/api/signal-subscriptions/summary?", StringComparison.Ordinal)
            && path.Contains("workflowKey=claims", StringComparison.Ordinal)
            && path.Contains("businessKey=ORD-2001", StringComparison.Ordinal));
        Assert.Contains("claims · business key ORD-2001",
            WebUtility.HtmlDecode(await harness.HtmlAsync(component)), StringComparison.Ordinal);

        await harness.CallAsync("ThrowAsync");
        var post = Assert.Single(harness.Posts);
        Assert.Equal("claims", post.Body.GetProperty("workflowKey").GetString());
        Assert.Equal("ORD-2001", post.Body.GetProperty("businessKey").GetString());
        Assert.False(post.Body.GetProperty("allWorkflows").GetBoolean());
        Assert.Equal(JsonValueKind.Null, post.Body.GetProperty("instanceId").ValueKind);
    }

    [Fact]
    public async Task ReviewedThrow_ForOneInstanceSendsOnlyItsId()
    {
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        await harness.SetAsync("signalName", "claims.policy-changed");
        // Fields typed under another choice stay out of the request.
        await harness.SetAsync("workflowKey", "claims");
        await harness.SetAsync("businessKey", "ORD-2001");
        await harness.CallAsync("SetScopeAsync", SignalScopeChoice.Instance);
        await harness.CallAsync("OnInstanceIdChangedAsync", new ChangeEventArgs { Value = "42" });

        await harness.CallAsync("ReviewAsync");
        Assert.Contains(harness.Paths, path => path.StartsWith("/api/signal-subscriptions/summary?", StringComparison.Ordinal)
            && path.Contains("instanceId=42", StringComparison.Ordinal)
            && !path.Contains("workflowKey=", StringComparison.Ordinal)
            && !path.Contains("businessKey=", StringComparison.Ordinal));
        Assert.Contains("Instance #42", await harness.HtmlAsync(component), StringComparison.Ordinal);

        await harness.CallAsync("ThrowAsync");
        var post = Assert.Single(harness.Posts);
        Assert.Equal(42, post.Body.GetProperty("instanceId").GetInt64());
        Assert.Equal(JsonValueKind.Null, post.Body.GetProperty("workflowKey").ValueKind);
        Assert.Equal(JsonValueKind.Null, post.Body.GetProperty("businessKey").ValueKind);
        Assert.False(post.Body.GetProperty("allWorkflows").GetBoolean());
    }

    [Fact]
    public async Task Review_RefusesAnInstanceScopeWithoutAnId()
    {
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        await harness.SetAsync("signalName", "claims.policy-changed");
        await harness.CallAsync("SetScopeAsync", SignalScopeChoice.Instance);
        await harness.CallAsync("OnInstanceIdChangedAsync", new ChangeEventArgs { Value = "-3" });

        // The status line says what the field needs while the box may still show the refused text.
        Assert.Contains("Enter an instance ID, a positive whole number, to see how many waits match.",
            await harness.HtmlAsync(component), StringComparison.Ordinal);

        await harness.CallAsync("ReviewAsync");
        Assert.Null(await harness.ValueAsync<long?>("instanceId"));
        Assert.Null(await harness.ValueAsync<object?>("review"));
        Assert.Contains("Enter an instance ID.", await harness.HtmlAsync(component), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FailedScopedCount_SaysSoInsteadOfCounting()
    {
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        await harness.SetAsync("signalName", "claims.policy-changed");
        await harness.SetAsync("workflowKey", "claims");
        harness.FailScopedMarker = "businessKey=ORD-FAIL";

        await harness.CallAsync("OnBusinessKeyChangedAsync", new ChangeEventArgs { Value = "ORD-FAIL" });
        var html = WebUtility.HtmlDecode(await harness.HtmlAsync(component));
        Assert.Contains("The matching waits could not be counted. Review signal counts them again.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Counting the matching waits", html, StringComparison.Ordinal);
        Assert.True(await harness.ValueAsync<bool>("scopedCountFailed"));

        // The next edit counts again.
        await harness.CallAsync("OnBusinessKeyChangedAsync", new ChangeEventArgs { Value = "ORD-2001" });
        Assert.False(await harness.ValueAsync<bool>("scopedCountFailed"));
        Assert.Contains("1 wait for 'claims.policy-changed' with business key ORD-2001 in claims.",
            WebUtility.HtmlDecode(await harness.HtmlAsync(component)), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScopeEdits_EndTheReview()
    {
        await using var harness = new Harness();
        await harness.RenderAsync();
        await harness.SetAsync("signalName", "claims.policy-changed");
        await harness.SetAsync("workflowKey", "claims");

        await ReviewAndEditAsync("OnBusinessKeyChangedAsync", new ChangeEventArgs { Value = "ORD-2001" });
        await ReviewAndEditAsync("OnBusinessKeyChangedAsync", new ChangeEventArgs { Value = "" });
        await ReviewAndEditAsync("SetScopeAsync", SignalScopeChoice.Instance);
        await harness.CallAsync("OnInstanceIdChangedAsync", new ChangeEventArgs { Value = "42" });
        await ReviewAndEditAsync("OnInstanceIdChangedAsync", new ChangeEventArgs { Value = "43" });
        await ReviewAndEditAsync("SetScopeAsync", SignalScopeChoice.AllWorkflows);
        Assert.Empty(harness.Posts);

        async Task ReviewAndEditAsync(string method, object argument)
        {
            await harness.CallAsync("ReviewAsync");
            Assert.NotNull(await harness.ValueAsync<object?>("review"));
            await harness.CallAsync(method, argument);
            Assert.Null(await harness.ValueAsync<object?>("review"));
            Assert.Null(await harness.ValueAsync<string?>("draftIdempotencyKey"));
        }
    }

    [Fact]
    public async Task IdentityChange_ClearsTheBusinessKeyAndTheInstance()
    {
        await using var harness = new Harness();
        await harness.RenderAsync();
        await harness.SetAsync("signalName", "claims.policy-changed");
        await harness.SetAsync("workflowKey", "claims");
        await harness.SetAsync("businessKey", "ORD-2001");
        await harness.SetAsync("instanceId", (long?)42);
        await harness.SetAsync("scopeChoice", SignalScopeChoice.Instance);

        await harness.ChangeIdentityAsync("other-token");

        Assert.Equal(string.Empty, await harness.ValueAsync<string>("businessKey"));
        Assert.Null(await harness.ValueAsync<long?>("instanceId"));
        Assert.Equal(SignalScopeChoice.Family, await harness.ValueAsync<SignalScopeChoice>("scopeChoice"));
        Assert.Null(await harness.ValueAsync<SignalSubscriptionSummaryDto?>("scopedSummary"));
    }

    [Fact]
    public async Task LateScopedCount_DoesNotReplaceANewerOne()
    {
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        await harness.SetAsync("signalName", "claims.policy-changed");
        await harness.SetAsync("workflowKey", "claims");

        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.HoldScopedMarker = "businessKey=ORD-A";
        harness.HoldScoped = held.Task;
        var counting = harness.CallAsync("OnBusinessKeyChangedAsync", new ChangeEventArgs { Value = "ORD-A" });
        await Harness.WaitForAsync(() => harness.Paths.Any(path => path.Contains("businessKey=ORD-A", StringComparison.Ordinal)));

        await harness.CallAsync("OnBusinessKeyChangedAsync", new ChangeEventArgs { Value = "ORD-B" });
        held.SetResult();
        await counting;

        Assert.Equal(2, (await harness.ValueAsync<SignalSubscriptionSummaryDto?>("scopedSummary"))!.Items.Single().Count);
        Assert.Contains("2 waits for &#x27;claims.policy-changed&#x27; with business key ORD-B in claims.",
            await harness.HtmlAsync(component), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewedThrow_WithAnExpirySendsItsIsoDuration()
    {
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        await harness.SetAsync("signalName", "claims.policy-changed");
        await harness.SetAsync("scopeChoice", SignalScopeChoice.AllWorkflows);
        await harness.CallAsync("OnExpiryAmountTypedAsync", new TypedInputEventArgs { Value = "10" });
        await harness.CallAsync("ReviewAsync");
        var html = await harness.HtmlAsync(component);
        Assert.Contains("10 minutes after the throw", html, StringComparison.Ordinal);

        harness.DetailExpiresAt = Now.AddMinutes(10);
        await harness.CallAsync("ThrowAsync");
        var post = Assert.Single(harness.Posts);
        Assert.Equal("PT10M", post.Body.GetProperty("expiresAfter").GetString());

        // The open broadcast shows its expiry; it has not passed yet, so no note.
        html = await harness.HtmlAsync(component);
        Assert.Contains("signal-expiry", html, StringComparison.Ordinal);
        Assert.DoesNotContain("The expiry has passed", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReviewedThrow_WithoutAnExpirySaysNeverAndSendsNone()
    {
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        await harness.SetAsync("signalName", "claims.policy-changed");
        await harness.SetAsync("scopeChoice", SignalScopeChoice.AllWorkflows);
        await harness.CallAsync("ReviewAsync");
        Assert.Matches("signal-review-expiry\"[^>]*>Never</dd>", await harness.HtmlAsync(component));

        await harness.CallAsync("ThrowAsync");
        var post = Assert.Single(harness.Posts);
        Assert.Equal(JsonValueKind.Null, post.Body.GetProperty("expiresAfter").ValueKind);
    }

    [Fact]
    public async Task ExpiryEdit_EndsTheReview()
    {
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        await harness.SetAsync("signalName", "claims.policy-changed");
        await harness.SetAsync("scopeChoice", SignalScopeChoice.AllWorkflows);
        await harness.CallAsync("OnExpiryAmountTypedAsync", new TypedInputEventArgs { Value = "10" });
        await harness.CallAsync("ReviewAsync");
        Assert.NotNull(await harness.ValueAsync<object?>("review"));

        // Changing the unit ends the review.
        await harness.CallAsync("OnExpiryUnitChangedAsync", new ChangeEventArgs { Value = "Hours" });
        Assert.Null(await harness.ValueAsync<object?>("review"));
        Assert.Null(await harness.ValueAsync<string?>("draftIdempotencyKey"));
        Assert.Contains("Review signal", await harness.HtmlAsync(component), StringComparison.Ordinal);

        await harness.CallAsync("ReviewAsync");
        Assert.Contains("10 hours after the throw", await harness.HtmlAsync(component), StringComparison.Ordinal);

        // So does changing the amount.
        await harness.CallAsync("OnExpiryAmountTypedAsync", new TypedInputEventArgs { Value = "11" });
        Assert.Null(await harness.ValueAsync<object?>("review"));
        Assert.Empty(harness.Posts);
    }

    [Theory]
    [InlineData("0", "Minutes", "Enter the expiry as a whole number of 1 or more, or leave it empty.")]
    [InlineData("2.5", "Minutes", "Enter the expiry as a whole number of 1 or more, or leave it empty.")]
    [InlineData("366", "Days", "A signal can expire after at most 365 days.")]
    public async Task Review_RefusesAnInvalidExpiryBeforeAnyRequest(string amount, string unit, string message)
    {
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        await harness.SetAsync("signalName", "claims.policy-changed");
        await harness.SetAsync("scopeChoice", SignalScopeChoice.AllWorkflows);
        await harness.CallAsync("OnExpiryUnitChangedAsync", new ChangeEventArgs { Value = unit });
        await harness.CallAsync("OnExpiryAmountTypedAsync", new TypedInputEventArgs { Value = amount });
        var requestsBefore = harness.Paths.Count;
        await harness.CallAsync("ReviewAsync");

        Assert.Null(await harness.ValueAsync<object?>("review"));
        Assert.Contains(message, await harness.HtmlAsync(component), StringComparison.Ordinal);
        Assert.Equal(requestsBefore, harness.Paths.Count);
        Assert.Empty(harness.Posts);
    }

    [Fact]
    public async Task Review_RefusesTextTheBrowserCouldNotReadInsteadOfSayingNever()
    {
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        await harness.SetAsync("signalName", "claims.policy-changed");
        await harness.SetAsync("scopeChoice", SignalScopeChoice.AllWorkflows);
        // A number box reports 5- or 10e as an empty value, with badInput set.
        await harness.CallAsync("OnExpiryAmountTypedAsync", new TypedInputEventArgs { Value = "", BadInput = true });
        var requestsBefore = harness.Paths.Count;
        await harness.CallAsync("ReviewAsync");

        Assert.Null(await harness.ValueAsync<object?>("review"));
        var html = await harness.HtmlAsync(component);
        Assert.Contains("Enter the expiry as a whole number of 1 or more, or leave it empty.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("signal-review-expiry", html, StringComparison.Ordinal);
        Assert.Equal(requestsBefore, harness.Paths.Count);

        // Emptying the box for real means no expiry again.
        await harness.CallAsync("OnExpiryAmountTypedAsync", new TypedInputEventArgs { Value = "" });
        await harness.CallAsync("ReviewAsync");
        Assert.Matches("signal-review-expiry\"[^>]*>Never</dd>", await harness.HtmlAsync(component));
        Assert.Empty(harness.Posts);
    }

    [Fact]
    public async Task ExpiryBox_LeavingItWithoutAChangeKeepsTheReview()
    {
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        await harness.SetAsync("signalName", "claims.policy-changed");
        await harness.SetAsync("scopeChoice", SignalScopeChoice.AllWorkflows);
        await harness.CallAsync("OnExpiryAmountTypedAsync", new TypedInputEventArgs { Value = "10" });
        await harness.CallAsync("ReviewAsync");
        var key = await harness.ValueAsync<string?>("draftIdempotencyKey");
        Assert.NotNull(key);

        // typedcommit on focus leaving the box reports the same text: the review stays, with its key.
        await harness.CallAsync("OnExpiryAmountTypedAsync", new TypedInputEventArgs { Value = " 10 " });
        Assert.NotNull(await harness.ValueAsync<object?>("review"));
        Assert.Equal(key, await harness.ValueAsync<string?>("draftIdempotencyKey"));

        // Text the browser cannot read is a change.
        await harness.CallAsync("OnExpiryAmountTypedAsync", new TypedInputEventArgs { Value = "", BadInput = true });
        Assert.Null(await harness.ValueAsync<object?>("review"));
        Assert.Contains("Review signal", await harness.HtmlAsync(component), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExpiryBox_IsUncontrolledAndReportsThroughTheTypedEvents()
    {
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        await harness.CallAsync("OnExpiryAmountTypedAsync", new TypedInputEventArgs { Value = "10" });
        var box = System.Text.RegularExpressions.Regex.Match(
            await harness.HtmlAsync(component), "<input id=\"signal-expiry-amount\"[^>]*>").Value;
        Assert.NotEmpty(box);
        // The page never writes a value back, so a re-render cannot wipe text the browser reports as empty.
        Assert.DoesNotContain(" value=", box, StringComparison.Ordinal);
        Assert.Contains("data-typed-input", box, StringComparison.Ordinal);
        Assert.Contains("type=\"number\"", box, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OpenBroadcast_ShowsTheNoteOnceTheExpiryIsBehindTheUisClock()
    {
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        harness.DetailStatus = SignalBroadcastStatuses.Delivering;
        harness.DetailExpiresAt = Now.AddSeconds(5);
        await harness.CallAsync("OpenBroadcastAsync", 41L);
        var html = await harness.HtmlAsync(component);
        Assert.Contains(">Expiry</dt>", html, StringComparison.Ordinal);
        Assert.DoesNotContain("The expiry has passed", html, StringComparison.Ordinal);

        harness.Clock.Now = Now.AddSeconds(6);
        await harness.CallAsync("RefreshCurrentBroadcastAsync");
        html = await harness.HtmlAsync(component);
        Assert.Contains("The expiry has passed. A Worker ends the pending deliveries when it runs.", html, StringComparison.Ordinal);
        // A delivering broadcast can still be cancelled.
        Assert.Contains("Cancel pending deliveries", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RetryFailed_IsHiddenWithItsReasonOnceTheExpiryHasPassed()
    {
        await using var harness = new Harness();
        var component = await harness.RenderAsync();
        harness.DetailStatus = SignalBroadcastStatuses.CompletedWithIssues;
        harness.DetailFailedCount = 2;
        harness.DetailExpiresAt = Now.AddSeconds(10);
        await harness.CallAsync("OpenBroadcastAsync", 41L);
        var html = await harness.HtmlAsync(component);
        Assert.Contains("Retry 2 failed", html, StringComparison.Ordinal);
        Assert.DoesNotContain("signal-retry-expired", html, StringComparison.Ordinal);

        harness.Clock.Now = Now.AddSeconds(10);
        await harness.CallAsync("RefreshCurrentBroadcastAsync");
        html = await harness.HtmlAsync(component);
        Assert.DoesNotContain("Retry 2 failed", html, StringComparison.Ordinal);
        Assert.Contains(
            "This broadcast has expired, so its failed deliveries cannot be retried. Throw the signal again to reach the waits that are still open.",
            html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task IdentityChange_ClearsTheExpiry()
    {
        await using var harness = new Harness();
        await harness.RenderAsync();
        await harness.CallAsync("OnExpiryUnitChangedAsync", new ChangeEventArgs { Value = "Days" });
        await harness.CallAsync("OnExpiryAmountTypedAsync", new TypedInputEventArgs { Value = "3" });

        await harness.ChangeIdentityAsync("other-token");

        Assert.Equal(string.Empty, await harness.ValueAsync<string>("expiryAmount"));
        Assert.Equal("Minutes", (await harness.ValueAsync<object>("expiryUnit")).ToString());
    }

    private static SignalBroadcastDetailDto Detail(long id, string status) =>
        new(id, "claims.policy-changed", "claims", false, null, null, status, 3,
            status == SignalBroadcastStatuses.Delivering ? 3 : 0,
            status == SignalBroadcastStatuses.Delivering ? 0 : 3, 0, 0, 0,
            "admin", SignalThrowerKinds.User, ["admin"],
            JsonSerializer.SerializeToElement(new { version = 7 }),
            [new SignalRecipientCountDto("claims", 3)], false, null, null, Now, Now,
            status == SignalBroadcastStatuses.Delivering ? null : Now, null);

    private sealed record RecordedPost(string Path, JsonElement Body);

    /// <summary>A clock a test sets by hand.</summary>
    private sealed class ManualClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Harness : IAsyncDisposable
    {
        private readonly ServiceProvider provider;
        private readonly HtmlRenderer renderer;
        private readonly Activator activator = new();

        public ConcurrentQueue<string> PathLog { get; } = new();
        public IReadOnlyList<string> Paths => PathLog.ToArray();
        public List<RecordedPost> Posts { get; } = [];
        public volatile bool Deny;
        public string DetailStatus { get; set; } = SignalBroadcastStatuses.Delivering;

        /// <summary>When set, a request for broadcast 41's detail waits for this task.</summary>
        public Task? HoldDetail41 { get; set; }

        /// <summary>When set, a request for broadcast 41's deliveries with no filter waits for this task.</summary>
        public Task? HoldUnfilteredDeliveries { get; set; }

        /// <summary>When set, a scoped summary request whose path contains <see cref="HoldScopedMarker"/> waits for this task.</summary>
        public Task? HoldScoped { get; set; }

        public string? HoldScopedMarker { get; set; }

        /// <summary>When set, a scoped summary request whose path contains it fails with 500.</summary>
        public string? FailScopedMarker { get; set; }

        private readonly TokenState token = new();

        /// <summary>The UI's clock; a test moves it to put a broadcast's expiry behind it.</summary>
        public ManualClock Clock { get; } = new(Now);

        /// <summary>When set, broadcast 41's detail carries this expiry.</summary>
        public DateTimeOffset? DetailExpiresAt { get; set; }

        /// <summary>Broadcast 41's failed deliveries.</summary>
        public int DetailFailedCount { get; set; }

        public Harness()
        {
            var http = new HttpClient(new Handler(this)) { BaseAddress = new Uri("https://flowbit.test") };
            token.Set("signal-token");
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(new WorkflowApiClient(http));
            services.AddSingleton(token);
            services.AddSingleton<TimeProvider>(Clock);
            services.AddSingleton<IComponentActivator>(activator);
            provider = services.BuildServiceProvider();
            renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
        }

        public Task<HtmlRootComponent> RenderAsync() =>
            renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<SignalsPage>(ParameterView.Empty));

        public Task<string> HtmlAsync(HtmlRootComponent component) =>
            renderer.Dispatcher.InvokeAsync(component.ToHtmlString);

        public Task CallAsync(string method, params object?[] arguments) => renderer.Dispatcher.InvokeAsync(async () =>
        {
            var result = typeof(SignalsPage).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(activator.Page, arguments);
            if (result is Task task) await task;
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(activator.Page, null);
        });

        public Task SetAsync(string field, object? value) => renderer.Dispatcher.InvokeAsync(() =>
            typeof(SignalsPage).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(activator.Page, value));

        public Task<T> ValueAsync<T>(string field) => renderer.Dispatcher.InvokeAsync(() =>
            (T)typeof(SignalsPage).GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(activator.Page)!);

        /// <summary>Sets another token; the page clears its data on the dispatcher before it reloads.</summary>
        public Task ChangeIdentityAsync(string newToken) =>
            renderer.Dispatcher.InvokeAsync(() => token.Set(newToken));

        public static async Task WaitForAsync(Func<bool> condition)
        {
            for (var attempt = 0; attempt < 200; attempt++)
            {
                if (condition()) return;
                await Task.Delay(25);
            }
            Assert.Fail("The condition was not reached.");
        }

        public async ValueTask DisposeAsync()
        {
            await renderer.DisposeAsync();
            await provider.DisposeAsync();
        }

        private sealed class Activator : IComponentActivator
        {
            public SignalsPage? Page { get; private set; }

            public IComponent CreateInstance(Type componentType)
            {
                var component = (IComponent)System.Activator.CreateInstance(componentType)!;
                if (component is SignalsPage page) Page = page;
                return component;
            }
        }

        private sealed class Handler(Harness harness) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken)
            {
                var path = request.RequestUri!.PathAndQuery;
                harness.PathLog.Enqueue(path);
                if (harness.Deny)
                {
                    return new HttpResponseMessage(HttpStatusCode.Forbidden)
                    {
                        Content = JsonContent.Create(new { error = "denied" })
                    };
                }

                if (request.Method == HttpMethod.Post)
                {
                    var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                    lock (harness.Posts) harness.Posts.Add(new RecordedPost(path, body));
                    return Json(HttpStatusCode.Accepted, Detail(41, SignalBroadcastStatuses.Delivering) with
                    {
                        ExpiresAt = harness.DetailExpiresAt
                    });
                }

                if (path.StartsWith("/api/signal-subscriptions/summary", StringComparison.Ordinal)
                    && (path.Contains("businessKey=", StringComparison.Ordinal)
                        || path.Contains("instanceId=", StringComparison.Ordinal)))
                {
                    if (harness.HoldScoped is { } hold && harness.HoldScopedMarker is { } marker
                        && path.Contains(marker, StringComparison.Ordinal))
                    {
                        await hold.WaitAsync(cancellationToken);
                    }
                    if (harness.FailScopedMarker is { } failing && path.Contains(failing, StringComparison.Ordinal))
                    {
                        return new HttpResponseMessage(HttpStatusCode.InternalServerError)
                        {
                            Content = JsonContent.Create(new { error = "The count failed." })
                        };
                    }
                    // One wait for a business key or an instance; two for the key ORD-B.
                    var count = path.Contains("businessKey=ORD-B", StringComparison.Ordinal) ? 2 : 1;
                    return Json(HttpStatusCode.OK, new SignalSubscriptionSummaryDto(
                        [new SignalSubscriptionCountDto("claims.policy-changed", "claims", count)], false));
                }

                if (path.StartsWith("/api/signal-subscriptions/summary", StringComparison.Ordinal))
                {
                    return Json(HttpStatusCode.OK, new SignalSubscriptionSummaryDto(
                        [new SignalSubscriptionCountDto("claims.policy-changed", "claims", 3)], false));
                }

                if (path.StartsWith("/api/signal-broadcasts/41/deliveries", StringComparison.Ordinal))
                {
                    if (path.Contains("status=failed", StringComparison.Ordinal))
                    {
                        return Json(HttpStatusCode.OK, new PagedResult<SignalDeliveryDto>([], 1, 50, 7));
                    }
                    if (harness.HoldUnfilteredDeliveries is { } hold && !path.Contains("status=", StringComparison.Ordinal))
                    {
                        await hold.WaitAsync(cancellationToken);
                    }
                    return Json(HttpStatusCode.OK, new PagedResult<SignalDeliveryDto>([], 1, 50, 0));
                }

                if (path.StartsWith("/api/signal-broadcasts/42/deliveries", StringComparison.Ordinal))
                {
                    return Json(HttpStatusCode.OK, new PagedResult<SignalDeliveryDto>([], 1, 50, 42));
                }

                if (path == "/api/signal-broadcasts/41")
                {
                    if (harness.HoldDetail41 is { } hold)
                    {
                        await hold.WaitAsync(cancellationToken);
                    }
                    return Json(HttpStatusCode.OK, Detail(41, harness.DetailStatus) with
                    {
                        ExpiresAt = harness.DetailExpiresAt,
                        FailedCount = harness.DetailFailedCount
                    });
                }

                if (path == "/api/signal-broadcasts/42")
                {
                    return Json(HttpStatusCode.OK, Detail(42, SignalBroadcastStatuses.Completed));
                }

                if (path.StartsWith("/api/signal-broadcasts?", StringComparison.Ordinal))
                {
                    return Json(HttpStatusCode.OK, new PagedResult<SignalBroadcastSummaryDto>([], 1, 25, 0));
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            private static HttpResponseMessage Json<T>(HttpStatusCode status, T value) =>
                new(status) { Content = JsonContent.Create(value) };
        }
    }
}
