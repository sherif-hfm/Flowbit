extern alias FlowbitUi;

using System.Collections.Concurrent;
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
using SignalFormatting = FlowbitUi::Flowbit.Ui.Components.Shared.Signals.SignalFormatting;
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

    private const string CatalogRefusalMessage =
        "Throwing signal 'claims.policy-changed' needs one of the roles its catalog entry lists: recall-manager.";

    [Fact]
    public async Task CatalogPanel_ShowsTheEntrysFieldsAndWhetherTheUserMayThrowIt()
    {
        await using var harness = new Harness();
        harness.Catalog = CatalogList(entries: [CatalogEntry(), CatalogEntry("claims.other", description: null)]);
        var component = await harness.RenderAsync();

        // The catalog is read once with the page: active entries, one page of 200.
        var read = Assert.Single(harness.Paths, path => path.StartsWith("/api/signal-catalog/entries?", StringComparison.Ordinal));
        Assert.Contains("status=active", read, StringComparison.Ordinal);
        Assert.Contains("pageSize=200", read, StringComparison.Ordinal);

        // The summary's name comes first and gains the entry's description; the other entry follows.
        var html = await harness.HtmlAsync(component);
        var options = System.Text.RegularExpressions.Regex.Matches(html, "<option value=\"([^\"]+)\"( label=\"([^\"]*)\")?[^>]*>")
            .Where(match => match.Groups[1].Value.StartsWith("claims.", StringComparison.Ordinal))
            .Select(match => (match.Groups[1].Value, match.Groups[3].Value))
            .ToList();
        Assert.Equal([("claims.policy-changed", "A policy changed; open claims re-check it."), ("claims.other", "")], options);
        Assert.DoesNotContain("signal-catalog-panel", html, StringComparison.Ordinal);

        await harness.CallAsync("OnSignalNameChangedAsync", new ChangeEventArgs { Value = "claims.policy-changed" });
        html = WebUtility.HtmlDecode(await harness.HtmlAsync(component));
        Assert.Contains("signal-catalog-panel", html, StringComparison.Ordinal);
        Assert.Contains("A policy changed; open claims re-check it.", html, StringComparison.Ordinal);
        Assert.Contains("version</code>: a number, required", html, StringComparison.Ordinal);
        Assert.Contains("skus</code>: an array of strings, required", html, StringComparison.Ordinal);
        Assert.Contains("note</code>: a string, optional", html, StringComparison.Ordinal);
        Assert.Contains("Why it changed.", html, StringComparison.Ordinal);
        Assert.Contains(SignalFormatting.CatalogCanThrowText, html, StringComparison.Ordinal);
        Assert.Contains("Insert payload template", html, StringComparison.Ordinal);
        // An entry among the active ones needs no read of its own.
        Assert.DoesNotContain(harness.Paths, path => path.StartsWith("/api/signal-catalog/entries/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CatalogPanel_SaysWhyTheUserCannotThrowTheSignal()
    {
        await using var harness = new Harness();
        harness.Catalog = CatalogList(entries: [CatalogEntry(canThrow: false, throwRoles: ["recall-manager", "claims-lead"])]);
        var component = await harness.RenderAsync();
        await harness.CallAsync("OnSignalNameChangedAsync", new ChangeEventArgs { Value = "claims.policy-changed" });

        var html = WebUtility.HtmlDecode(await harness.HtmlAsync(component));
        Assert.Contains(
            "You cannot throw this signal: it needs one of the roles its catalog entry lists: recall-manager, claims-lead.",
            html, StringComparison.Ordinal);
        Assert.DoesNotContain(SignalFormatting.CatalogCanThrowText, html, StringComparison.Ordinal);
        Assert.Equal(
            "You cannot throw this signal: it needs a role from Signals.RequiredRole.",
            SignalFormatting.CatalogThrowText(CatalogEntry(canThrow: false)));
    }

    [Theory]
    [InlineData(SignalCatalogModes.Optional, "Not in the signal catalog.")]
    [InlineData(SignalCatalogModes.Required, "Not in the signal catalog. This deployment requires an entry before a throw.")]
    public async Task CatalogPanel_NotesANameWithoutAnEntryWithTheMode(string mode, string note)
    {
        await using var harness = new Harness();
        harness.Catalog = CatalogList(mode, [CatalogEntry()]);
        var component = await harness.RenderAsync();
        await harness.CallAsync("OnSignalNameChangedAsync", new ChangeEventArgs { Value = "claims.unknown" });

        // A name not among the active entries is read by itself, to tell an archived entry from none.
        Assert.Contains("/api/signal-catalog/entries/claims.unknown", harness.Paths);
        var html = WebUtility.HtmlDecode(await harness.HtmlAsync(component));
        Assert.Matches($"signal-catalog-note\"[^>]*>{System.Text.RegularExpressions.Regex.Escape(note)}</p>", html);
        Assert.DoesNotContain("Insert payload template", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatalogPanel_NotesAnArchivedEntry()
    {
        await using var harness = new Harness();
        harness.Catalog = CatalogList(entries: [CatalogEntry()]);
        harness.CatalogEntries["claims.retired"] = CatalogEntry("claims.retired", status: SignalCatalogStatuses.Archived);
        var component = await harness.RenderAsync();
        await harness.CallAsync("OnSignalNameChangedAsync", new ChangeEventArgs { Value = "claims.retired" });

        var html = WebUtility.HtmlDecode(await harness.HtmlAsync(component));
        Assert.Contains(SignalFormatting.CatalogArchivedNote, html, StringComparison.Ordinal);
        Assert.DoesNotContain("Insert payload template", html, StringComparison.Ordinal);
        Assert.DoesNotContain("You can throw", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PayloadTemplate_FillsOnlyAnEmptyBoxWithRequiredFieldsFirst()
    {
        await using var harness = new Harness();
        harness.Catalog = CatalogList(entries: [CatalogEntry()]);
        var component = await harness.RenderAsync();
        await harness.CallAsync("OnSignalNameChangedAsync", new ChangeEventArgs { Value = "claims.policy-changed" });
        await harness.SetAsync("scopeChoice", SignalScopeChoice.AllWorkflows);
        await harness.CallAsync("ReviewAsync");
        Assert.NotNull(await harness.ValueAsync<object?>("review"));

        await harness.CallAsync("InsertPayloadTemplate");
        var text = await harness.ValueAsync<string?>("payloadText");
        using (var template = JsonDocument.Parse(text!))
        {
            var members = template.RootElement.EnumerateObject().ToList();
            Assert.Equal(["version", "skus", "note", "effective", "changedAt", "urgent", "details"], members.Select(member => member.Name));
            Assert.Equal(0, members[0].Value.GetInt32());
            Assert.Equal(JsonValueKind.Array, members[1].Value.ValueKind);
            Assert.Equal(0, members[1].Value.GetArrayLength());
            Assert.Equal(string.Empty, members[2].Value.GetString());
            Assert.Equal("2026-01-01", members[3].Value.GetString());
            Assert.Equal(DateTimeOffset.Parse("2026-01-01T09:00:00Z"), members[4].Value.GetDateTimeOffset());
            Assert.False(members[5].Value.GetBoolean());
            // A json field gets an empty object, never null, which the catalog refuses.
            Assert.Equal(JsonValueKind.Object, members[6].Value.ValueKind);
            Assert.Empty(members[6].Value.EnumerateObject());
        }

        // The whole template, as inserted, fits the entry it was made from.
        var entry = CatalogEntry();
        Assert.Null(Flowbit.Service.Services.SignalCatalogContract.PayloadProblem(
            new Flowbit.Service.Models.SignalCatalogEntryRecord(
                1, entry.Name, entry.Description, entry.Status, entry.Fields, entry.ThrowRoles, entry.AllowClients,
                entry.AllowWorkflows, entry.CreatedAt, entry.CreatedBy, entry.UpdatedAt, entry.UpdatedBy, entry.ArchivedAt),
            JsonDocument.Parse(text!).RootElement.Clone()));

        // Inserting is a payload edit: it ends the review.
        Assert.Null(await harness.ValueAsync<object?>("review"));
        var button = System.Text.RegularExpressions.Regex.Match(
            await harness.HtmlAsync(component), "<button type=\"button\" id=\"signal-insert-template\"[^>]*>").Value;
        Assert.Contains("disabled", button, StringComparison.Ordinal);

        // A typed payload is never replaced.
        await harness.SetAsync("payloadText", "{ \"version\": 9 }");
        await harness.CallAsync("InsertPayloadTemplate");
        Assert.Equal("{ \"version\": 9 }", await harness.ValueAsync<string?>("payloadText"));
    }

    [Fact]
    public async Task Review_ShowsTheCatalogEntryOrNoEntry()
    {
        await using var harness = new Harness();
        harness.Catalog = CatalogList(entries: [CatalogEntry()]);
        var component = await harness.RenderAsync();
        await harness.SetAsync("scopeChoice", SignalScopeChoice.AllWorkflows);
        await harness.CallAsync("OnSignalNameChangedAsync", new ChangeEventArgs { Value = "claims.policy-changed" });
        await harness.CallAsync("ReviewAsync");
        Assert.Matches("signal-review-catalog\"[^>]*>claims.policy-changed</dd>", await harness.HtmlAsync(component));

        await harness.CallAsync("OnSignalNameChangedAsync", new ChangeEventArgs { Value = "claims.unknown" });
        Assert.Null(await harness.ValueAsync<object?>("review"));
        await harness.CallAsync("ReviewAsync");
        Assert.Matches("signal-review-catalog\"[^>]*>No entry</dd>", await harness.HtmlAsync(component));

        Assert.Equal("claims.retired (archived)", SignalFormatting.CatalogReviewText(
            CatalogEntry("claims.retired", status: SignalCatalogStatuses.Archived)));
    }

    [Fact]
    public async Task CatalogRead403_HidesTheCatalogAndKeepsThePage()
    {
        await using var harness = new Harness();
        harness.CatalogFailure = HttpStatusCode.Forbidden;
        var component = await harness.RenderAsync();
        await harness.SetAsync("scopeChoice", SignalScopeChoice.AllWorkflows);
        await harness.CallAsync("OnSignalNameChangedAsync", new ChangeEventArgs { Value = "claims.policy-changed" });
        await harness.CallAsync("ReviewAsync");

        Assert.False(await harness.ValueAsync<bool>("authorizationDenied"));
        Assert.Null(await harness.ValueAsync<SignalCatalogListDto?>("catalog"));
        Assert.Equal("claims.policy-changed", await harness.ValueAsync<string>("signalName"));
        Assert.NotNull(await harness.ValueAsync<PagedResult<SignalBroadcastSummaryDto>?>("history"));
        Assert.NotNull(await harness.ValueAsync<object?>("review"));
        var html = await harness.HtmlAsync(component);
        Assert.DoesNotContain("signal-catalog-panel", html, StringComparison.Ordinal);
        Assert.DoesNotContain("signal-review-catalog", html, StringComparison.Ordinal);
        Assert.Contains("Throw signal", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CancelPendingDeliveries_StaysOfferedToTheThrowerWhateverTheEntrySays()
    {
        await using var harness = new Harness();
        harness.Catalog = CatalogList(entries: [CatalogEntry(canThrow: false, throwRoles: ["recall-manager"])]);
        var component = await harness.RenderAsync();
        harness.DetailStatus = SignalBroadcastStatuses.Delivering;
        await harness.CallAsync("OpenBroadcastAsync", 41L);
        Assert.Contains("Cancel pending deliveries", await harness.HtmlAsync(component), StringComparison.Ordinal);

        await harness.CallAsync("CancelAsync");
        Assert.Contains(harness.Posts, post => post.Path == "/api/signal-broadcasts/41/cancel");
        Assert.Contains("Broadcast #41 was cancelled", await harness.HtmlAsync(component), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatalogRefusalOfAThrow_IsShownAndThePageKeepsItsData()
    {
        await using var harness = new Harness();
        harness.RefusePost = path => path == "/api/signal-broadcasts" ? CatalogRefusal(CatalogRefusalMessage) : null;
        var component = await harness.RenderAsync();
        await harness.SetAsync("signalName", "claims.policy-changed");
        await harness.SetAsync("scopeChoice", SignalScopeChoice.AllWorkflows);
        await harness.SetAsync("payloadText", "{ \"version\": 7 }");
        await harness.CallAsync("ReviewAsync");
        var historyReads = harness.Paths.Count(IsHistoryRead);

        await harness.CallAsync("ThrowAsync");

        Assert.Single(harness.Posts);
        Assert.False(await harness.ValueAsync<bool>("authorizationDenied"));
        Assert.Equal(CatalogRefusalMessage, await harness.ValueAsync<string?>("error"));
        Assert.Equal("claims.policy-changed", await harness.ValueAsync<string>("signalName"));
        Assert.Equal("{ \"version\": 7 }", await harness.ValueAsync<string?>("payloadText"));
        Assert.NotNull(await harness.ValueAsync<PagedResult<SignalBroadcastSummaryDto>?>("history"));
        // The broadcast list is read once more, to tell the refusal from a lost permission.
        Assert.Equal(historyReads + 1, harness.Paths.Count(IsHistoryRead));
        var html = WebUtility.HtmlDecode(await harness.HtmlAsync(component));
        Assert.Contains(CatalogRefusalMessage, html, StringComparison.Ordinal);
        Assert.DoesNotContain("Signal permission is required", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatalogRefusalOfCancelAndRetry_KeepsTheBroadcast()
    {
        const string cancelMessage =
            "Cancelling signal 'claims.policy-changed' needs one of the roles its catalog entry lists: recall-manager.";
        const string retryMessage =
            "Retrying signal 'claims.policy-changed' needs one of the roles its catalog entry lists: recall-manager.";
        await using var harness = new Harness();
        harness.RefusePost = path => path switch
        {
            "/api/signal-broadcasts/41/cancel" => CatalogRefusal(cancelMessage),
            "/api/signal-broadcasts/41/retry-failed" => CatalogRefusal(retryMessage),
            _ => null
        };
        var component = await harness.RenderAsync();
        harness.DetailStatus = SignalBroadcastStatuses.Delivering;
        await harness.CallAsync("OpenBroadcastAsync", 41L);

        await harness.CallAsync("CancelAsync");
        Assert.Equal(cancelMessage, await harness.ValueAsync<string?>("error"));
        Assert.Equal(41L, (await harness.ValueAsync<SignalBroadcastDetailDto?>("currentBroadcast"))!.Id);
        Assert.False(await harness.ValueAsync<bool>("authorizationDenied"));

        harness.DetailStatus = SignalBroadcastStatuses.CompletedWithIssues;
        harness.DetailFailedCount = 2;
        await harness.CallAsync("RefreshCurrentBroadcastAsync");
        await harness.CallAsync("RetryFailedAsync");
        Assert.Equal(retryMessage, await harness.ValueAsync<string?>("error"));
        Assert.Equal(41L, (await harness.ValueAsync<SignalBroadcastDetailDto?>("currentBroadcast"))!.Id);
        Assert.False(await harness.ValueAsync<bool>("authorizationDenied"));
        Assert.Contains("Retry 2 failed", await harness.HtmlAsync(component), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatalogRefusalFollowedByARefusedListRead_ClearsThePageAsALostIdentity()
    {
        await using var harness = new Harness();
        harness.RefusePost = path => path == "/api/signal-broadcasts" ? CatalogRefusal(CatalogRefusalMessage) : null;
        var component = await harness.RenderAsync();
        await harness.SetAsync("signalName", "claims.policy-changed");
        await harness.SetAsync("scopeChoice", SignalScopeChoice.AllWorkflows);
        await harness.CallAsync("ReviewAsync");
        harness.DenyHistory = true;

        await harness.CallAsync("ThrowAsync");

        Assert.True(await harness.ValueAsync<bool>("authorizationDenied"));
        Assert.Equal(string.Empty, await harness.ValueAsync<string>("signalName"));
        Assert.Null(await harness.ValueAsync<string?>("error"));
        var html = await harness.HtmlAsync(component);
        Assert.Contains("Signal permission is required", html, StringComparison.Ordinal);
        Assert.DoesNotContain("claims.policy-changed", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task PolicyRefusalWithoutABody_StillClearsThePageAtOnce()
    {
        await using var harness = new Harness();
        harness.RefusePost = path => path == "/api/signal-broadcasts" ? new HttpResponseMessage(HttpStatusCode.Forbidden) : null;
        var component = await harness.RenderAsync();
        await harness.SetAsync("signalName", "claims.policy-changed");
        await harness.SetAsync("scopeChoice", SignalScopeChoice.AllWorkflows);
        await harness.CallAsync("ReviewAsync");
        var historyReads = harness.Paths.Count(IsHistoryRead);

        await harness.CallAsync("ThrowAsync");

        Assert.True(await harness.ValueAsync<bool>("authorizationDenied"));
        Assert.Equal(historyReads, harness.Paths.Count(IsHistoryRead));
        Assert.Contains("Signal permission is required", await harness.HtmlAsync(component), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CatalogText_IsRenderedAsText()
    {
        const string markup = "<img src=x onerror=alert(1)>";
        await using var harness = new Harness();
        harness.Catalog = CatalogList(entries: [CatalogEntry(
            description: markup,
            throwRoles: [markup],
            canThrow: false,
            fields: [new SignalCatalogFieldDto("note", WorkflowVariableTypes.String, Description: markup)])]);
        var component = await harness.RenderAsync();
        await harness.CallAsync("OnSignalNameChangedAsync", new ChangeEventArgs { Value = "claims.policy-changed" });

        var html = await harness.HtmlAsync(component);
        Assert.DoesNotContain("<img", html, StringComparison.Ordinal);
        Assert.Contains("&lt;img src=x onerror=alert(1)&gt;", html, StringComparison.Ordinal);
    }

    [Fact]
    public void NameOptions_ListTheSummaryNamesFirstAndShortenLongDescriptions()
    {
        var longDescription = new string('a', 130);
        var options = SignalFormatting.NameOptions(
            ["b.wait", "a.cataloged"],
            [
                CatalogEntry("a.cataloged", description: "Cataloged and awaited."),
                CatalogEntry("c.long", description: longDescription),
                CatalogEntry("d.archived", status: SignalCatalogStatuses.Archived)
            ]);

        Assert.Equal(["b.wait", "a.cataloged", "c.long"], options.Select(option => option.Value));
        Assert.Null(options[0].Label);
        Assert.Equal("Cataloged and awaited.", options[1].Label);
        Assert.Equal(SignalFormatting.MaxNameOptionLabelLength, options[2].Label!.Length);
        Assert.EndsWith("…", options[2].Label, StringComparison.Ordinal);
    }

    private static bool IsHistoryRead(string path) =>
        path.StartsWith("/api/signal-broadcasts?", StringComparison.Ordinal);

    private static HttpResponseMessage CatalogRefusal(string message) =>
        new(HttpStatusCode.Forbidden)
        {
            Content = JsonContent.Create(new { code = "signal_catalog_forbidden", error = message })
        };

    private static SignalCatalogEntryDto CatalogEntry(
        string name = "claims.policy-changed",
        bool canThrow = true,
        IReadOnlyList<string>? throwRoles = null,
        string status = SignalCatalogStatuses.Active,
        string? description = "A policy changed; open claims re-check it.",
        IReadOnlyList<SignalCatalogFieldDto>? fields = null) =>
        new(name, description, status,
            fields ??
            [
                new SignalCatalogFieldDto("note", WorkflowVariableTypes.String, Description: "Why it changed."),
                new SignalCatalogFieldDto("version", WorkflowVariableTypes.Number, Required: true),
                new SignalCatalogFieldDto("skus", WorkflowVariableTypes.String, IsArray: true, Required: true),
                new SignalCatalogFieldDto("effective", WorkflowVariableTypes.Date),
                new SignalCatalogFieldDto("changedAt", WorkflowVariableTypes.DateTime),
                new SignalCatalogFieldDto("urgent", WorkflowVariableTypes.Boolean),
                new SignalCatalogFieldDto("details", WorkflowVariableTypes.Json)
            ],
            throwRoles ?? [], true, true, canThrow, Now, "admin", Now, "admin",
            status == SignalCatalogStatuses.Archived ? Now : null);

    private static SignalCatalogListDto CatalogList(
        string mode = SignalCatalogModes.Optional,
        IReadOnlyList<SignalCatalogEntryDto>? entries = null) =>
        new(entries ?? [], 1, 200, entries?.Count ?? 0, mode, ["admin"]);

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

        /// <summary>The active catalog entries the page reads; null answers 404, as before the catalog.</summary>
        public SignalCatalogListDto? Catalog { get; set; }

        /// <summary>When set, every catalog read answers this status.</summary>
        public HttpStatusCode? CatalogFailure { get; set; }

        /// <summary>The entries a read of one name finds; any other name answers 404.</summary>
        public ConcurrentDictionary<string, SignalCatalogEntryDto> CatalogEntries { get; } = new(StringComparer.Ordinal);

        /// <summary>When it returns a response for a POST path, the POST answers it instead of succeeding.</summary>
        public Func<string, HttpResponseMessage?>? RefusePost { get; set; }

        /// <summary>When set, the broadcast list answers 403 without a body.</summary>
        public volatile bool DenyHistory;

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

                if (path.StartsWith("/api/signal-catalog/entries", StringComparison.Ordinal))
                {
                    if (harness.CatalogFailure is { } failure)
                    {
                        return new HttpResponseMessage(failure);
                    }
                    if (path.StartsWith("/api/signal-catalog/entries?", StringComparison.Ordinal))
                    {
                        // No catalog configured: the route answers 404, as before EVENT-04F.
                        return harness.Catalog is { } catalog
                            ? Json(HttpStatusCode.OK, catalog)
                            : new HttpResponseMessage(HttpStatusCode.NotFound);
                    }
                    var name = Uri.UnescapeDataString(path["/api/signal-catalog/entries/".Length..]);
                    return harness.CatalogEntries.TryGetValue(name, out var entry)
                        ? Json(HttpStatusCode.OK, entry)
                        : new HttpResponseMessage(HttpStatusCode.NotFound);
                }

                if (request.Method == HttpMethod.Post)
                {
                    var body = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                    lock (harness.Posts) harness.Posts.Add(new RecordedPost(path, body));
                    if (harness.RefusePost?.Invoke(path) is { } refusal)
                    {
                        return refusal;
                    }
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
                    if (harness.DenyHistory)
                    {
                        // An authorization policy's refusal carries no body.
                        return new HttpResponseMessage(HttpStatusCode.Forbidden);
                    }
                    return Json(HttpStatusCode.OK, new PagedResult<SignalBroadcastSummaryDto>([], 1, 25, 0));
                }

                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            private static HttpResponseMessage Json<T>(HttpStatusCode status, T value) =>
                new(status) { Content = JsonContent.Create(value) };
        }
    }
}
