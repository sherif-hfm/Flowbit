extern alias FlowbitUi;

using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.Components.Web.HtmlRendering;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.JSInterop;
using Xunit;
using BrowserTimeZone = FlowbitUi::Flowbit.Ui.Components.Shared.BrowserTimeZone;
using DeliveryDetailPage = FlowbitUi::Flowbit.Ui.Components.Pages.WebhookDeliveryDetail;
using TokenState = FlowbitUi::Flowbit.Ui.Auth.TokenState;
using UiAuthenticationUnavailableException = FlowbitUi::Flowbit.Ui.Auth.UiAuthenticationUnavailableException;
using UiSessionExpiredException = FlowbitUi::Flowbit.Ui.Auth.UiSessionExpiredException;
using WebhookDeliverySearch = FlowbitUi::Flowbit.Ui.Clients.WebhookDeliverySearch;
using WebhookFormatting = FlowbitUi::Flowbit.Ui.Components.Shared.WebhookFormatting;
using WebhooksPage = FlowbitUi::Flowbit.Ui.Components.Pages.Webhooks;
using WorkflowApiClient = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiClient;
using WorkflowApiException = FlowbitUi::Flowbit.Ui.Clients.WorkflowApiException;

namespace Flowbit.Tests;

public sealed class WebhooksUiTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-11-07T09:00:00Z");
    private static readonly BrowserTimeZone Tokyo = BrowserTimeZone.FromBrowser("Asia/Tokyo");
    private static readonly Guid SubscriptionId = Guid.Parse("019a5e2d-6d40-7000-8000-000000000001");
    private static readonly Guid DeliveryId = Guid.Parse("019a5e2d-6d40-7000-8000-0000000000aa");
    private static readonly Guid OtherDeliveryId = Guid.Parse("019a5e2d-6d40-7000-8000-0000000000bb");

    [Fact]
    public async Task ClientSendsFiltersAsQueryParametersAndRevisionsInBodies()
    {
        var subscriptionId = Guid.Parse("019a5e2d-6d40-7000-8000-000000000001");
        var deliveryId = Guid.Parse("019a5e2d-6d40-7000-8000-0000000000aa");
        using var handler = new RecordingHandler(
            Response(new PagedResult<WebhookDeliveryDto>([], 2, 25, 0)),
            Response(new WebhookDeliveryActionResultDto(deliveryId, "pending", 4, 2), HttpStatusCode.Accepted),
            Response(Subscription(subscriptionId)),
            Response(new WebhookBulkDeliveryResultDto(3, 2, 1, true)));
        var client = Client(handler);

        await client.GetWebhookDeliveriesAsync(new WebhookDeliverySearch(
            subscriptionId, null, "task.assigned", "purchase review", 42, 7, "failed", "receiver_rejected",
            Now, Now.AddDays(1), 2, 25));
        await client.RetryWebhookDeliveryAsync(deliveryId, 3);
        await client.PauseWebhookSubscriptionAsync(subscriptionId);
        await client.BulkDiscardWebhookDeliveriesAsync(subscriptionId, new WebhookBulkDeliveryRequest(null, null, null, 7, null, null, null));

        Assert.Equal(
            "/api/webhook-deliveries?page=2&pageSize=25&subscriptionId=019a5e2d-6d40-7000-8000-000000000001&eventType=task.assigned&workflowKey=purchase%20review&instanceId=42&revisionId=7&state=failed&failureCode=receiver_rejected&from=2026-11-07T09%3A00%3A00.0000000%2B00%3A00&to=2026-11-08T09%3A00%3A00.0000000%2B00%3A00",
            handler.Requests[0].Path);
        Assert.Equal("/api/webhook-deliveries/019a5e2d-6d40-7000-8000-0000000000aa/retry", handler.Requests[1].Path);
        Assert.Contains("\"expectedRevision\":3", handler.Requests[1].Body, StringComparison.Ordinal);
        Assert.Equal(HttpMethod.Post, handler.Requests[2].Method);
        Assert.Null(handler.Requests[2].Body);
        Assert.Contains("\"revisionId\":7", handler.Requests[3].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ClientSendsTimeFiltersAsUtcInQueriesAndBulkBodies()
    {
        var from = DateTimeOffset.Parse("2026-11-07T11:00:00+02:00", CultureInfo.InvariantCulture);
        var to = DateTimeOffset.Parse("2026-11-07T04:30:00-05:00", CultureInfo.InvariantCulture);
        using var handler = new RecordingHandler(
            Response(new PagedResult<WebhookDeliveryDto>([], 1, 25, 0)),
            Response(new WebhookBulkDeliveryResultDto(0, 0, 0, false)),
            Response(new WebhookBulkDeliveryResultDto(0, 0, 0, false)));
        var client = Client(handler);

        await client.GetWebhookDeliveriesAsync(new WebhookDeliverySearch(From: from, To: to, PageSize: 25));
        await client.BulkRetryWebhookDeliveriesAsync(SubscriptionId, new WebhookBulkDeliveryRequest(null, null, null, null, null, from, to));
        await client.BulkDiscardWebhookDeliveriesAsync(SubscriptionId, new WebhookBulkDeliveryRequest(null, null, null, null, null, from, to));

        Assert.EndsWith(
            "&from=2026-11-07T09%3A00%3A00.0000000%2B00%3A00&to=2026-11-07T09%3A30%3A00.0000000%2B00%3A00",
            handler.Requests[0].Path, StringComparison.Ordinal);
        foreach (var bulk in handler.Requests.Skip(1))
        {
            Assert.Contains("\"from\":\"2026-11-07T09:00:00+00:00\"", bulk.Body, StringComparison.Ordinal);
            Assert.Contains("\"to\":\"2026-11-07T09:30:00+00:00\"", bulk.Body, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task ClientSurfacesConflictsAndKeyOutagesAsTypedErrors()
    {
        using var handler = new RecordingHandler(
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable) { Content = JsonContent.Create(new { error = "Webhook key protection is not configured." }) });
        var error = await Assert.ThrowsAsync<WorkflowApiException>(() => Client(handler).CreateWebhookSubscriptionAsync(
            new CreateWebhookSubscriptionRequest("n", null, "https://r.example.com", ["instance.started"], null, null)));
        Assert.Equal(HttpStatusCode.ServiceUnavailable, error.StatusCode);
        Assert.Equal("Webhook key protection is not configured.", error.Message);
    }

    [Fact]
    public void FormattingDescribesStatesOutcomesAndHealth()
    {
        Assert.Equal("Paused: receiver gone (410)", WebhookFormatting.SubscriptionStateLabel("paused", "receiverGone"));
        Assert.Equal("danger", WebhookFormatting.SubscriptionStateTone("paused", "receiverUnavailable"));
        Assert.Equal("warning", WebhookFormatting.SubscriptionStateTone("paused", "administrator"));
        Assert.Equal("Retry scheduled", WebhookFormatting.DeliveryStateLabel("retryScheduled"));
        Assert.Equal("Receiver rejected (4xx) · HTTP 400", WebhookFormatting.OutcomeWithStatus("receiver_rejected", 400));
        Assert.Equal("Task assigned", WebhookFormatting.EventTypeLabel("task.assigned"));
        Assert.Equal("No deliveries yet", WebhookFormatting.HealthSummary(new WebhookReceiverHealthDto(0, null, null, null, null, null, null, null), Now, Tokyo));
        Assert.Equal($"Backing off until {new DateTime(2026, 11, 7, 18, 5, 0):g} after 6 failures in a row", WebhookFormatting.HealthSummary(
            new WebhookReceiverHealthDto(6, Now, Now.AddMinutes(5), null, Now, "timeout", null, Now), Now, Tokyo));
        Assert.Equal($"Backing off until {new DateTime(2026, 11, 7, 9, 5, 0):g} UTC after 6 failures in a row", WebhookFormatting.HealthSummary(
            new WebhookReceiverHealthDto(6, Now, Now.AddMinutes(5), null, Now, "timeout", null, Now), Now, BrowserTimeZone.Pending));
        Assert.StartsWith("Probing one request", WebhookFormatting.HealthSummary(
            new WebhookReceiverHealthDto(6, Now, Now.AddMinutes(-1), null, Now, "timeout", null, Now), Now, Tokyo));
        Assert.StartsWith("Healthy · last success", WebhookFormatting.HealthSummary(
            new WebhookReceiverHealthDto(0, null, null, Now, Now.AddMinutes(-1), "succeeded", 204, Now), Now, Tokyo));
        Assert.StartsWith("Latest request failed · last success", WebhookFormatting.HealthSummary(
            new WebhookReceiverHealthDto(0, null, null, Now.AddMinutes(-1), Now, "receiver_gone", 410, Now), Now, Tokyo));
        Assert.Equal("No successful delivery yet", WebhookFormatting.HealthSummary(
            new WebhookReceiverHealthDto(0, null, null, null, Now, "receiver_rejected", 400, Now), Now, Tokyo));
        Assert.Equal("{\n  \"a\": 1\n}".Replace("\n", Environment.NewLine), WebhookFormatting.PrettyBody("{\"a\":1}"));
        Assert.Equal("not json", WebhookFormatting.PrettyBody("not json"));
    }

    [Fact]
    public void ThePrettyBodyIndentsWithoutEscapingAnyCharacterAgain()
    {
        const string body = """{"type":"task.assigned","data":{"name":"Müller & <Söhne>","note":"say \"hi\" \u00e9","emoji":"😀","values":[1,2.50,true,null],"empty":{},"none":[]}}""";
        string[] expected =
        [
            "{",
            "  \"type\": \"task.assigned\",",
            "  \"data\": {",
            "    \"name\": \"Müller & <Söhne>\",",
            "    \"note\": \"say \\\"hi\\\" \\u00e9\",",
            "    \"emoji\": \"😀\",",
            "    \"values\": [",
            "      1,",
            "      2.50,",
            "      true,",
            "      null",
            "    ],",
            "    \"empty\": {},",
            "    \"none\": []",
            "  }",
            "}"
        ];

        Assert.Equal(string.Join(Environment.NewLine, expected), WebhookFormatting.PrettyBody(body));
        Assert.Equal("[]", WebhookFormatting.PrettyBody("[]"));
        Assert.Equal(string.Empty, WebhookFormatting.PrettyBody(string.Empty));
        Assert.Equal("{\"a\":", WebhookFormatting.PrettyBody("{\"a\":"));
        Assert.Equal("{} {}", WebhookFormatting.PrettyBody("{} {}"));
    }

    [Fact]
    public void FailuresAreWordedForAdministratorsAndAConflictClaimsTheCurrentStateOnlyAfterAReload()
    {
        var conflict = new WorkflowApiException(HttpStatusCode.Conflict, "The subscription changed.");
        Assert.Equal("The subscription changed. The page shows the current state.", WebhookFormatting.ActionFailure(conflict, reloaded: true));
        Assert.Equal("The subscription changed.", WebhookFormatting.ActionFailure(conflict, reloaded: false));
        Assert.Equal("Bad filter.", WebhookFormatting.ActionFailure(new WorkflowApiException(HttpStatusCode.BadRequest, "Bad filter."), reloaded: true));
        Assert.Equal("Sign in again to manage webhooks.", WebhookFormatting.ErrorMessage(new WorkflowApiException(HttpStatusCode.Unauthorized, "x")));
        Assert.Equal("This identity does not have the settings administrator role required for webhooks.",
            WebhookFormatting.ErrorMessage(new WorkflowApiException(HttpStatusCode.Forbidden, "x")));
        Assert.Equal("Connection refused", WebhookFormatting.ErrorMessage(new HttpRequestException("Connection refused")));
        Assert.True(WebhookFormatting.IsPermissionFailure(new WorkflowApiException(HttpStatusCode.Forbidden, "x")));
        Assert.True(WebhookFormatting.IsPermissionFailure(new WorkflowApiException(HttpStatusCode.Unauthorized, "x")));
        Assert.False(WebhookFormatting.IsPermissionFailure(new UiSessionExpiredException()));
        Assert.Null(WebhookFormatting.WithReloadFailure(null, null));
        Assert.Equal("Changed.", WebhookFormatting.WithReloadFailure("Changed.", null));
        Assert.Equal("The page could not be refreshed: Connection refused", WebhookFormatting.WithReloadFailure(null, "Connection refused"));
        Assert.Equal("Connection refused", WebhookFormatting.WithReloadFailure("Connection refused", "Connection refused"));
        Assert.Equal("Changed. The page could not be refreshed: Connection refused", WebhookFormatting.WithReloadFailure("Changed.", "Connection refused"));
    }

    [Fact]
    public void ARevealedSecretSaysWhenTheReceiverMustHaveIt()
    {
        var created = Subscription(Guid.CreateVersion7());
        Assert.Equal("before activating the subscription", WebhookFormatting.SecretInstallDeadline(created, Tokyo));

        var rotated = created with
        {
            State = WebhookSubscriptionStates.Active,
            PauseReason = null,
            SigningKeys =
            [
                .. created.SigningKeys,
                new WebhookSigningKeyDto(Guid.CreateVersion7(), WebhookSigningKeyStatuses.Previous, Now, "admin", Now.AddHours(1), null, null, null)
            ]
        };
        Assert.StartsWith("before the previous key stops signing at", WebhookFormatting.SecretInstallDeadline(rotated, Tokyo));

        var replacedAtOnce = rotated with { SigningKeys = created.SigningKeys };
        Assert.Equal("now: requests are already signed with this key alone", WebhookFormatting.SecretInstallDeadline(replacedAtOnce, Tokyo));
    }

    [Theory]
    [InlineData("", "https://r.example.com", 1, 4, "Name is required.")]
    [InlineData("n", "not a url", 1, 4, "Enter the receiver's absolute HTTPS URL.")]
    [InlineData("n", "https://r.example.com", 0, 4, "Select at least one event type.")]
    [InlineData("n", "https://r.example.com", 1, 17, "Maximum concurrency must be between 1 and 16.")]
    [InlineData("n", "https://r.example.com", 1, 16, null)]
    public void FormValidationExplainsTheFirstProblem(string name, string url, int events, int concurrency, string? expected) =>
        Assert.Equal(expected, WebhookFormatting.ValidateForm(
            name, url, Enumerable.Repeat("instance.started", events).ToArray(), concurrency));

    [Fact]
    public async Task PageWarnsWhenCaptureIsOffAndNeverRendersSecretsAfterIdentityLoss()
    {
        var subscription = Subscription(Guid.CreateVersion7());
        using var handler = new RecordingHandler(
            Response(Catalog(captureEnabled: false)),
            Response(new PagedResult<WebhookSubscriptionDto>([subscription], 1, 25, 1)),
            Response(new PagedResult<WebhookDeliveryDto>([], 1, 25, 0)));
        var token = AdminToken();
        await using var page = await Harness<WebhooksPage>.RenderAsync(handler, token);
        var html = await page.WaitForAsync(text => text.Contains("CRM feed", StringComparison.Ordinal));

        Assert.Contains("Lifecycle capture is off", html, StringComparison.Ordinal);
        Assert.Contains(subscription.Name, html, StringComparison.Ordinal);
        Assert.Contains("https://receiver.example.com:443", html, StringComparison.Ordinal);
        Assert.DoesNotContain("token=secret", html, StringComparison.Ordinal);
        Assert.Contains("Paused", html, StringComparison.Ordinal);

        // A one-time secret lives only in component state and is cleared when the identity changes.
        page.Set("oneTimeSecret", "whsec_AAAA");
        page.Set("secretSubscription", subscription);
        await page.Renderer.Dispatcher.InvokeAsync(token.Clear);
        Assert.Null(page.Get<string?>("oneTimeSecret"));
        var cleared = await page.HtmlAsync();
        Assert.DoesNotContain("whsec_", cleared, StringComparison.Ordinal);
        Assert.Contains("An administrative identity is required", cleared, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnActionsConflictAndSuccessSurviveTheReloadAfterIt()
    {
        var subscription = Subscription(SubscriptionId);
        using var handler = new RecordingHandler();
        ReplyPageLoad(handler, subscription);
        handler.Reply(Problem(HttpStatusCode.Conflict, "The subscription changed."));
        ReplyPageLoad(handler, subscription with { Revision = 4 });
        handler.Reply(Response(new WebhookTestResultDto(DeliveryId, Guid.CreateVersion7(), "pending"), HttpStatusCode.Accepted));
        ReplyPageLoad(handler, subscription);
        await using var page = await Harness<WebhooksPage>.RenderAsync(handler, AdminToken());

        await page.CallAsync("ResumeAsync", subscription);
        var html = await page.HtmlAsync();
        Assert.Contains("The subscription changed. The page shows the current state.", html, StringComparison.Ordinal);
        Assert.Equal(4, page.Get<PagedResult<WebhookSubscriptionDto>>("subscriptions").Items.Single().Revision);
        Assert.False(page.Get<bool>("busy"));

        await page.CallAsync("SendTestAsync", subscription);
        html = await page.HtmlAsync();
        Assert.Contains("Queued a signed test event for CRM feed", html, StringComparison.Ordinal);
        Assert.DoesNotContain("The subscription changed", html, StringComparison.Ordinal);
        Assert.Equal(11, handler.Requests.Count);
    }

    [Fact]
    public async Task AConflictDoesNotClaimTheCurrentStateWhenTheReloadFails()
    {
        var subscription = Subscription(SubscriptionId);
        using var handler = new RecordingHandler();
        ReplyPageLoad(handler, subscription);
        handler.Reply(Problem(HttpStatusCode.Conflict, "The subscription changed."));
        handler.Fail(new HttpRequestException("Connection refused"));
        await using var page = await Harness<WebhooksPage>.RenderAsync(handler, AdminToken());

        await page.CallAsync("ResumeAsync", subscription);

        Assert.Equal("The subscription changed. The page could not be refreshed: Connection refused", page.Get<string?>("error"));
        Assert.Contains("CRM feed", await page.HtmlAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EditingASubscriptionThatIsGoneSaysSoAfterTheReload()
    {
        var subscription = Subscription(SubscriptionId);
        using var handler = new RecordingHandler();
        ReplyPageLoad(handler, subscription);
        handler.Reply(Problem(HttpStatusCode.NotFound, "Not found."));
        ReplyPageLoad(handler);
        await using var page = await Harness<WebhooksPage>.RenderAsync(handler, AdminToken());

        await page.CallAsync("OpenEditAsync", subscription);

        var html = await page.HtmlAsync();
        Assert.Contains("The subscription no longer exists.", html, StringComparison.Ordinal);
        Assert.Contains("No webhook subscriptions", html, StringComparison.Ordinal);
        Assert.False(page.Get<bool>("formOpen"));
    }

    [Fact]
    public async Task ANetworkFailureDuringAnActionIsReportedAndKeepsTheList()
    {
        var subscription = Subscription(SubscriptionId);
        using var handler = new RecordingHandler();
        ReplyPageLoad(handler, subscription);
        handler.Fail(new HttpRequestException("Connection refused"));
        handler.Fail(new HttpRequestException("Connection refused"));
        await using var page = await Harness<WebhooksPage>.RenderAsync(handler, AdminToken());

        await page.CallAsync("PauseAsync", subscription);

        var html = await page.HtmlAsync();
        Assert.Equal("Connection refused", page.Get<string?>("error"));
        Assert.Contains("CRM feed", html, StringComparison.Ordinal);
        Assert.False(page.Get<bool>("busy"));
    }

    [Fact]
    public async Task AFailedCopyKeepsTheSecretAndAsksForAManualCopy()
    {
        using var handler = new RecordingHandler();
        ReplyPageLoad(handler, Subscription(SubscriptionId));
        var js = new TestJsRuntime { ClipboardFailure = new JSException("Write permission denied.") };
        await using var page = await Harness<WebhooksPage>.RenderAsync(handler, AdminToken(), js);
        page.Set("oneTimeSecret", "whsec_COPYME");
        page.Set("secretSubscription", Subscription(SubscriptionId));

        await page.CallAsync("CopySecretAsync");

        var html = await page.HtmlAsync();
        Assert.Equal("whsec_COPYME", page.Get<string?>("oneTimeSecret"));
        Assert.Contains("whsec_COPYME", html, StringComparison.Ordinal);
        Assert.Contains("Select it above and copy it manually.", html, StringComparison.Ordinal);

        js.ClipboardFailure = null;
        await page.CallAsync("CopySecretAsync");
        Assert.Contains("Copied the signing secret.", await page.HtmlAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("network", "Connection refused")]
    [InlineData("timeout", "The request timed out.")]
    [InlineData("session", "Your session has ended. Sign in again to continue.")]
    [InlineData("authentication", "Authentication is temporarily unavailable. Try again shortly.")]
    public async Task AFailureRightAfterCreatingKeepsTheRevealedSecret(string failure, string message)
    {
        using var handler = new RecordingHandler();
        ReplyPageLoad(handler);
        handler.Reply(Response(Secret("whsec_CREATED"), HttpStatusCode.Created));
        handler.Fail(failure switch
        {
            "network" => new HttpRequestException(message),
            "timeout" => new TaskCanceledException(message),
            "session" => new UiSessionExpiredException(),
            _ => new UiAuthenticationUnavailableException()
        });
        await using var page = await Harness<WebhooksPage>.RenderAsync(handler, AdminToken());
        await FillCreateFormAsync(page);

        await page.CallAsync("SaveAsync");

        var html = await page.HtmlAsync();
        Assert.Contains("whsec_CREATED", html, StringComparison.Ordinal);
        Assert.Contains("Created CRM feed paused.", html, StringComparison.Ordinal);
        Assert.Equal($"The page could not be refreshed: {message}", page.Get<string?>("error"));
        Assert.False(page.Get<bool>("busy"));
    }

    [Fact]
    public async Task OnlyAPermissionLossClearsASecretRevealedJustBefore()
    {
        var subscription = Subscription(SubscriptionId);
        using var handler = new RecordingHandler();
        ReplyPageLoad(handler, subscription);
        handler.Reply(Response(Secret("whsec_ROTATED")));
        handler.Fail(new HttpRequestException("Connection refused"));
        await using var page = await Harness<WebhooksPage>.RenderAsync(handler, AdminToken());
        await page.CallAsync("OpenRotate", subscription);

        await page.CallAsync("RotateAsync");

        var html = await page.HtmlAsync();
        Assert.Contains("whsec_ROTATED", html, StringComparison.Ordinal);
        Assert.Contains("Rotated the signing secret of CRM feed.", html, StringComparison.Ordinal);
        Assert.Contains("The page could not be refreshed: Connection refused", html, StringComparison.Ordinal);

        // A 401 or 403 is permission loss, which clears restricted views and the secret alike.
        handler.Reply(Problem(HttpStatusCode.Forbidden, "denied"));
        await page.CallAsync("RefreshAsync");
        html = await page.HtmlAsync();
        Assert.Null(page.Get<string?>("oneTimeSecret"));
        Assert.DoesNotContain("whsec_", html, StringComparison.Ordinal);
        Assert.Contains("This identity does not have the settings administrator role required for webhooks.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("webhook-subscriptions-heading", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "This identity does not have the settings administrator role required for webhooks.")]
    [InlineData(HttpStatusCode.Unauthorized, "Sign in again to manage webhooks.")]
    public async Task ANonAdministratorSeesOnlyThePermissionMessage(HttpStatusCode status, string message)
    {
        using var handler = new RecordingHandler(Problem(status, "denied"));
        ReplyPageLoad(handler, Subscription(SubscriptionId));
        await using var page = await Harness<WebhooksPage>.RenderAsync(handler, AdminToken());

        var html = await page.HtmlAsync();
        Assert.Contains(message, html, StringComparison.Ordinal);
        Assert.DoesNotContain("webhook-subscriptions-heading", html, StringComparison.Ordinal);
        Assert.DoesNotContain("webhook-delivery-filter-heading", html, StringComparison.Ordinal);
        Assert.DoesNotContain("webhook-delivery-results-heading", html, StringComparison.Ordinal);
        Assert.DoesNotContain("skeleton", html, StringComparison.Ordinal);
        Assert.DoesNotContain("unavailable", html, StringComparison.Ordinal);
        Assert.Single(handler.Requests);

        // Once the role is granted, Refresh shows the page again.
        await page.CallAsync("RefreshAsync");
        html = await page.HtmlAsync();
        Assert.Contains("CRM feed", html, StringComparison.Ordinal);
        Assert.DoesNotContain(message, html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeliveriesThatFailToLoadSaySoInsteadOfLoadingForever()
    {
        using var handler = new RecordingHandler(
            Response(Catalog(captureEnabled: true)),
            Response(new PagedResult<WebhookSubscriptionDto>([Subscription(SubscriptionId)], 1, 25, 1)));
        handler.Fail(new HttpRequestException("Connection refused"));
        await using var page = await Harness<WebhooksPage>.RenderAsync(handler, AdminToken());

        var html = await page.HtmlAsync();
        Assert.Contains("Deliveries unavailable", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Loading deliveries", html, StringComparison.Ordinal);
        Assert.Contains("CRM feed", html, StringComparison.Ordinal);
        Assert.Contains("Connection refused", html, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden)]
    [InlineData(HttpStatusCode.OK)]
    public async Task AReplyThatArrivesAfterAnIdentityChangeIsIgnored(HttpStatusCode status)
    {
        var subscription = Subscription(SubscriptionId);
        using var handler = new RecordingHandler();
        ReplyPageLoad(handler, subscription);
        var pause = handler.Hold();
        ReplyPageLoad(handler, subscription with { Name = "Second admin feed" });
        var token = AdminToken();
        await using var page = await Harness<WebhooksPage>.RenderAsync(handler, token);

        var pending = page.CallAsync("PauseAsync", subscription);
        await pause.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await page.Renderer.Dispatcher.InvokeAsync(() => token.Set("second-admin-token"));
        await page.WaitForAsync(text => text.Contains("Second admin feed", StringComparison.Ordinal));
        pause.Reply.SetResult(status == HttpStatusCode.OK ? Response(subscription) : Problem(status, "denied"));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));

        var html = await page.HtmlAsync();
        Assert.Contains("Second admin feed", html, StringComparison.Ordinal);
        Assert.Null(page.Get<string?>("error"));
        Assert.Null(page.Get<string?>("success"));
        Assert.False(page.Get<bool>("permissionDenied"));
        Assert.Equal(7, handler.Requests.Count);
    }

    [Theory]
    [InlineData("SaveAsync")]
    [InlineData("RotateAsync")]
    public async Task ASecretIssuedBeforeAnIdentityChangeIsNeverShown(string action)
    {
        var subscription = Subscription(SubscriptionId);
        using var handler = new RecordingHandler();
        ReplyPageLoad(handler, subscription);
        var issue = handler.Hold();
        ReplyPageLoad(handler);
        var token = AdminToken();
        await using var page = await Harness<WebhooksPage>.RenderAsync(handler, token);
        if (action == "SaveAsync") await FillCreateFormAsync(page);
        else await page.CallAsync("OpenRotate", subscription);

        var pending = page.CallAsync(action);
        await issue.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await page.Renderer.Dispatcher.InvokeAsync(() => token.Set("second-admin-token"));
        await page.WaitForAsync(text => text.Contains("No webhook subscriptions", StringComparison.Ordinal));
        issue.Reply.SetResult(Response(Secret("whsec_EARLIER"), HttpStatusCode.Created));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));

        var html = await page.HtmlAsync();
        Assert.Null(page.Get<string?>("oneTimeSecret"));
        Assert.Null(page.Get<string?>("success"));
        Assert.DoesNotContain("whsec_", html, StringComparison.Ordinal);
        Assert.Equal(7, handler.Requests.Count);
    }

    [Theory]
    [InlineData("SaveAsync", "/api/webhook-subscriptions")]
    [InlineData("PauseAsync", "/api/webhook-subscriptions/019a5e2d-6d40-7000-8000-000000000001/pause")]
    public async Task ASecondClickWhileARequestRunsSendsNothing(string action, string path)
    {
        var subscription = Subscription(SubscriptionId);
        using var handler = new RecordingHandler();
        ReplyPageLoad(handler, subscription);
        var first = handler.Hold();
        ReplyPageLoad(handler, subscription);
        await using var page = await Harness<WebhooksPage>.RenderAsync(handler, AdminToken());
        object?[] arguments = action == "SaveAsync" ? [] : [subscription];
        if (action == "SaveAsync") await FillCreateFormAsync(page);

        var pending = page.CallAsync(action, arguments);
        await first.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await page.CallAsync(action, arguments);
        first.Reply.SetResult(action == "SaveAsync" ? Response(Secret("whsec_ONCE"), HttpStatusCode.Created) : Response(subscription));
        await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Single(handler.Requests, request => request.Method == HttpMethod.Post && request.Path == path);
        Assert.Equal(7, handler.Requests.Count);
        Assert.False(page.Get<bool>("busy"));
    }

    [Fact]
    public async Task ARefusedRotationRetriesWithTheCurrentRevision()
    {
        var subscription = Subscription(SubscriptionId);
        var current = subscription with { Revision = 5 };
        using var handler = new RecordingHandler();
        ReplyPageLoad(handler, subscription);
        handler.Reply(Problem(HttpStatusCode.Conflict, "The subscription changed."));
        handler.Reply(Response(new WebhookSubscriptionDetailDto(current, [])));
        ReplyPageLoad(handler, current);
        handler.Reply(Response(Secret("whsec_ROTATED")));
        ReplyPageLoad(handler, current);
        await using var page = await Harness<WebhooksPage>.RenderAsync(handler, AdminToken());
        await page.CallAsync("OpenRotate", subscription);

        await page.CallAsync("RotateAsync");

        Assert.Equal("The subscription changed. The page shows the current state.", page.Get<string?>("formError"));
        Assert.Equal(5, page.Get<WebhookSubscriptionDto>("rotating").Revision);
        Assert.Contains("\"expectedRevision\":3", handler.Requests[3].Body, StringComparison.Ordinal);

        await page.CallAsync("RotateAsync");

        Assert.Equal("/api/webhook-subscriptions/019a5e2d-6d40-7000-8000-000000000001/rotate-secret", handler.Requests[8].Path);
        Assert.Contains("\"expectedRevision\":5", handler.Requests[8].Body, StringComparison.Ordinal);
        Assert.Contains("whsec_ROTATED", await page.HtmlAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BulkConfirmationCountsWhatTheRouteSelectsAndSaysStateDoesNotApply()
    {
        var subscription = Subscription(SubscriptionId);
        using var handler = new RecordingHandler();
        ReplyPageLoad(handler, subscription);
        handler.Reply(Response(new PagedResult<WebhookDeliveryDto>([], 1, 25, 0)));
        handler.Reply(Response(new PagedResult<WebhookDeliveryDto>([], 1, 1, 2)));
        handler.Reply(Response(new PagedResult<WebhookDeliveryDto>([], 1, 1, 1)));
        handler.Reply(Response(new PagedResult<WebhookDeliveryDto>([], 1, 1, 4)));
        await using var page = await Harness<WebhooksPage>.RenderAsync(handler, AdminToken());
        page.Set("filterSubscription", SubscriptionId.ToString("D"));
        page.Set("filterState", WebhookDeliveryStates.Succeeded);
        await page.CallAsync("ApplyDeliveryFiltersAsync");

        await page.CallAsync("PrepareBulkAsync", "discard");

        var html = await page.HtmlAsync();
        Assert.Contains("Discard 7 pending, scheduled, and failed deliveries of <strong>CRM feed</strong> matching the current filters except State (bulk actions do not filter by state)?", html, StringComparison.Ordinal);
        var counts = handler.Requests.Skip(4).Select(request => request.Path).ToArray();
        Assert.Equal(3, counts.Length);
        Assert.All(counts, path => Assert.Contains("pageSize=1&subscriptionId=019a5e2d-6d40-7000-8000-000000000001", path, StringComparison.Ordinal));
        Assert.EndsWith("&state=pending", counts[0], StringComparison.Ordinal);
        Assert.EndsWith("&state=retryScheduled", counts[1], StringComparison.Ordinal);
        Assert.EndsWith("&state=failed", counts[2], StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscardingAnOldDestinationCountsItsQueuedAndFailedDeliveriesAfresh()
    {
        var subscription = Subscription(SubscriptionId) with
        {
            Destination = Subscription(SubscriptionId).Destination with { RevisionId = 8, ConfigurationRevision = 2 }
        };
        var old = new WebhookDestinationRevisionDto(
            new WebhookDestinationDto(7, 1, "https://old.example.com/hook", "https://old.example.com:443", [LifecycleEventTypes.TaskAssigned], null, Now, "admin"),
            false, 9);
        using var handler = new RecordingHandler();
        ReplyPageLoad(handler, subscription);
        handler.Reply(Response(new PagedResult<WebhookDeliveryDto>([], 1, 1, 1)));
        handler.Reply(Response(new PagedResult<WebhookDeliveryDto>([], 1, 1, 0)));
        handler.Reply(Response(new PagedResult<WebhookDeliveryDto>([], 1, 1, 2)));
        handler.Reply(Response(new WebhookBulkDeliveryResultDto(3, 3, 0, false)));
        ReplyPageLoad(handler, subscription);
        await using var page = await Harness<WebhooksPage>.RenderAsync(handler, AdminToken());

        await page.CallAsync("PrepareRevisionDiscardAsync", subscription, old);

        var html = await page.HtmlAsync();
        Assert.Contains("Discard 3 pending, scheduled, and failed deliveries of <strong>CRM feed</strong> captured for destination revision 1?", html, StringComparison.Ordinal);
        Assert.All(handler.Requests.Skip(3).Take(3), request => Assert.Contains("&revisionId=7&state=", request.Path, StringComparison.Ordinal));

        await page.CallAsync("RunBulkAsync");

        Assert.Equal("/api/webhook-subscriptions/019a5e2d-6d40-7000-8000-000000000001/deliveries/discard", handler.Requests[6].Path);
        Assert.Contains("\"revisionId\":7", handler.Requests[6].Body, StringComparison.Ordinal);
        Assert.Contains("Discarded 3 of 3 matching deliveries.", await page.HtmlAsync(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDeliveryFiltersSendBrowserTimesAsUtcAndAppliedFiltersChangeOnlyWhenAccepted()
    {
        using var handler = new RecordingHandler();
        ReplyPageLoad(handler);
        handler.Reply(Problem(HttpStatusCode.BadRequest, "Unknown failure code 'bogus'."));
        handler.Reply(Response(new PagedResult<WebhookDeliveryDto>([], 1, 25, 0)));
        await using var page = await Harness<WebhooksPage>.RenderAsync(
            handler, AdminToken(), zone: BrowserTimeZone.FromBrowser("America/New_York"));
        Assert.Contains("From (America/New_York)", await page.HtmlAsync(), StringComparison.Ordinal);

        page.Set("filterFailureCode", "bogus");
        await page.CallAsync("ApplyDeliveryFiltersAsync");
        Assert.Equal("Unknown failure code 'bogus'.", page.Get<string?>("filterError"));
        Assert.Null(page.Get<WebhookDeliverySearch>("appliedSearch").FailureCode);

        // New York left daylight-saving time on 1 November 2026, so these are UTC-05:00.
        page.Set("filterFailureCode", null);
        page.Set("filterFrom", "2026-11-07T10:00");
        page.Set("filterTo", "2026-11-07T12:30");
        await page.CallAsync("ApplyDeliveryFiltersAsync");

        var from = DateTimeOffset.Parse("2026-11-07T15:00:00Z", CultureInfo.InvariantCulture);
        var to = DateTimeOffset.Parse("2026-11-07T17:30:00Z", CultureInfo.InvariantCulture);
        var applied = page.Get<WebhookDeliverySearch>("appliedSearch");
        Assert.Equal(TimeSpan.Zero, applied.From!.Value.Offset);
        Assert.Equal(from, applied.From);
        Assert.Equal(to, applied.To);
        Assert.EndsWith(
            $"&from={Uri.EscapeDataString(from.ToString("O", CultureInfo.InvariantCulture))}&to={Uri.EscapeDataString(to.ToString("O", CultureInfo.InvariantCulture))}",
            handler.Requests[^1].Path, StringComparison.Ordinal);
        Assert.Null(page.Get<string?>("filterError"));

        // 02:30 on 8 March 2026 is skipped in New York: the filter is refused before any request.
        var requests = handler.Requests.Count;
        page.Set("filterFrom", "2026-03-08T02:30");
        await page.CallAsync("ApplyDeliveryFiltersAsync");
        Assert.Equal(
            "From: 2026-03-08 02:30 does not exist in America/New_York because of a daylight-saving change.",
            page.Get<string?>("filterError"));
        Assert.Equal(requests, handler.Requests.Count);
        Assert.Equal(from, page.Get<WebhookDeliverySearch>("appliedSearch").From);
    }

    [Fact]
    public async Task ADeliveryDetailLoadsANewIdAfterANotFound()
    {
        using var handler = new RecordingHandler(
            Problem(HttpStatusCode.NotFound, "Not found."),
            Response(DeliveryDetail(OtherDeliveryId)),
            Response(new PagedResult<WebhookDeliveryAttemptDto>([], 1, 25, 0)));
        await using var page = await Harness<DeliveryDetailPage>.RenderAsync(handler, AdminToken(), id: DeliveryId);
        Assert.Contains("Delivery not found", await page.HtmlAsync(), StringComparison.Ordinal);

        await page.ShowAsync(OtherDeliveryId);

        var html = await page.WaitForAsync(text => text.Contains("Task assigned to CRM feed", StringComparison.Ordinal));
        Assert.Contains("Task assigned to CRM feed", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Delivery not found", html, StringComparison.Ordinal);
        string[] paths =
        [
            "/api/webhook-deliveries/019a5e2d-6d40-7000-8000-0000000000aa",
            "/api/webhook-deliveries/019a5e2d-6d40-7000-8000-0000000000bb",
            "/api/webhook-deliveries/019a5e2d-6d40-7000-8000-0000000000bb/attempts?page=1&pageSize=25"
        ];
        Assert.Equal(paths, handler.Requests.Select(request => request.Path));
    }

    [Fact]
    public async Task ADeliveryDetailReportsAConflictAfterReloadingAndShowsTheExactBody()
    {
        const string body = """{"type":"task.assigned","data":{"name":"Müller & <Söhne>"}}""";
        using var handler = new RecordingHandler(
            Response(DeliveryDetail(DeliveryId, body)),
            Response(new PagedResult<WebhookDeliveryAttemptDto>([], 1, 25, 0)),
            Problem(HttpStatusCode.Conflict, "The delivery changed."),
            Response(DeliveryDetail(DeliveryId, body)),
            Response(new PagedResult<WebhookDeliveryAttemptDto>([], 1, 25, 0)));
        await using var page = await Harness<DeliveryDetailPage>.RenderAsync(handler, AdminToken(), id: DeliveryId);

        await page.CallAsync("RetryAsync");

        var html = WebUtility.HtmlDecode(await page.HtmlAsync());
        Assert.Contains("The delivery changed. The page shows the current state.", html, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"Müller & <Söhne>\"", html, StringComparison.Ordinal);
        Assert.Contains("Exact signed body", html, StringComparison.Ordinal);
        Assert.Contains(body, html, StringComparison.Ordinal);
        Assert.Equal(5, handler.Requests.Count);
    }

    [Theory]
    [InlineData(HttpStatusCode.Forbidden, "This identity does not have the settings administrator role required for webhooks.")]
    [InlineData(HttpStatusCode.Unauthorized, "Sign in again to manage webhooks.")]
    public async Task ADeliveryDetailShowsOnlyThePermissionMessageToANonAdministrator(HttpStatusCode status, string message)
    {
        using var handler = new RecordingHandler(Problem(status, "denied"));
        await using var page = await Harness<DeliveryDetailPage>.RenderAsync(handler, AdminToken(), id: DeliveryId);

        var html = await page.HtmlAsync();
        Assert.Contains(message, html, StringComparison.Ordinal);
        Assert.DoesNotContain("Delivery unavailable", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Delivery not found", html, StringComparison.Ordinal);
        Assert.DoesNotContain("skeleton", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnArchivedSubscriptionOffersNoRecoveryActionsBecauseTheWorkerSettlesItsDeliveries()
    {
        var archived = Subscription(SubscriptionId) with { State = WebhookSubscriptionStates.Archived, PauseReason = null };
        var failed = new WebhookDeliveryDto(
            DeliveryId, Guid.CreateVersion7(), SubscriptionId, "CRM feed", 7, 1, "https://receiver.example.com:443",
            LifecycleEventTypes.TaskAssigned, false, null, 42, Now, WebhookDeliveryStates.Failed, null, 1, 3, 3,
            WebhookOutcomeCodes.ReceiverRejected, 400, WebhookOutcomeCodes.ReceiverRejected, Now, null, 6, Now, Now, false, false);
        using var handler = new RecordingHandler();
        handler.Reply(Response(Catalog(captureEnabled: true)))
            .Reply(Response(new PagedResult<WebhookSubscriptionDto>([archived], 1, 25, 1)))
            .Reply(Response(new PagedResult<WebhookDeliveryDto>([failed], 1, 25, 1)))
            .Reply(Response(new PagedResult<WebhookDeliveryDto>([failed], 1, 25, 1)));
        await using var page = await Harness<WebhooksPage>.RenderAsync(handler, AdminToken());

        var html = await page.WaitForAsync(markup => markup.Contains("receiver_rejected", StringComparison.Ordinal));
        Assert.Contains("receiver_rejected", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">Retry</button>", html, StringComparison.Ordinal);
        Assert.DoesNotContain(">Discard</button>", html, StringComparison.Ordinal);

        page.Set("filterSubscription", SubscriptionId.ToString("D"));
        await page.CallAsync("ApplyDeliveryFiltersAsync");
        html = await page.HtmlAsync();
        Assert.Contains("This subscription is archived: the Worker discards its outstanding deliveries automatically.", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Retry failed", html, StringComparison.Ordinal);
        Assert.DoesNotContain("Discard queued and failed", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EachDeliveryOffersTheActionsTheApiWouldAcceptWhereverItsSubscriptionIsListed()
    {
        // None of these subscriptions is on the loaded subscription page.
        using var handler = new RecordingHandler();
        handler.Reply(Response(Catalog(captureEnabled: true)))
            .Reply(Response(new PagedResult<WebhookSubscriptionDto>([Subscription(Guid.CreateVersion7()) with { Name = "Billing feed" }], 1, 25, 26)))
            .Reply(Response(new PagedResult<WebhookDeliveryDto>(
            [
                FailedDelivery(DeliveryId, SubscriptionId, "CRM feed", canRetry: true, canDiscard: true),
                FailedDelivery(OtherDeliveryId, Guid.CreateVersion7(), "Paused feed", canRetry: false, canDiscard: true),
                FailedDelivery(Guid.CreateVersion7(), Guid.CreateVersion7(), "Archived feed", canRetry: false, canDiscard: false)
            ], 1, 25, 3)));
        await using var page = await Harness<WebhooksPage>.RenderAsync(handler, AdminToken());

        var html = await page.WaitForAsync(markup => markup.Contains("Archived feed", StringComparison.Ordinal));
        Assert.Equal(1, Occurrences(html, ">Retry</button>"));
        Assert.Equal(2, Occurrences(html, ">Discard</button>"));
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task AnAppliedSubscriptionFilterKeepsItsBulkActionsWhileTheSubscriptionListPages()
    {
        var filtered = Subscription(SubscriptionId);
        var delivery = FailedDelivery(DeliveryId, SubscriptionId, filtered.Name, canRetry: true, canDiscard: true);
        using var handler = new RecordingHandler();
        handler.Reply(Response(Catalog(captureEnabled: true)))
            .Reply(Response(new PagedResult<WebhookSubscriptionDto>([filtered], 1, 25, 26)))
            .Reply(Response(new PagedResult<WebhookDeliveryDto>([delivery], 1, 25, 1)))
            // The filter is applied while its subscription is on the loaded page.
            .Reply(Response(new PagedResult<WebhookDeliveryDto>([delivery], 1, 25, 1)))
            // The second subscription page does not list it, so the page asks for it.
            .Reply(Response(Catalog(captureEnabled: true)))
            .Reply(Response(new PagedResult<WebhookSubscriptionDto>([Subscription(Guid.CreateVersion7()) with { Name = "Billing feed" }], 2, 25, 26)))
            .Reply(Response(new PagedResult<WebhookDeliveryDto>([delivery], 1, 25, 1)))
            .Reply(Response(new WebhookSubscriptionDetailDto(filtered, [])));
        await using var page = await Harness<WebhooksPage>.RenderAsync(handler, AdminToken());
        await page.WaitForAsync(markup => markup.Contains("receiver_rejected", StringComparison.Ordinal));

        page.Set("filterSubscription", SubscriptionId.ToString("D"));
        await page.CallAsync("ApplyDeliveryFiltersAsync");
        Assert.Contains("Retry failed…", await page.HtmlAsync(), StringComparison.Ordinal);
        Assert.Equal(4, handler.Requests.Count);

        await page.CallAsync("ChangeSubscriptionPageAsync", 1);
        var html = await page.HtmlAsync();
        Assert.Contains("Billing feed", html, StringComparison.Ordinal);
        Assert.Contains("Retry failed…", html, StringComparison.Ordinal);
        Assert.Contains("Discard queued and failed…", html, StringComparison.Ordinal);
        // The subscription filter still offers the subscription it applies, though the list no longer shows it.
        Assert.Matches($"<option value=\"{SubscriptionId:D}\"[^>]*>CRM feed</option>", html);
        Assert.Equal($"/api/webhook-subscriptions/{SubscriptionId:D}", handler.Requests[^1].Path);
        Assert.Equal(8, handler.Requests.Count);
    }

    private static async Task FillCreateFormAsync(Harness<WebhooksPage> page)
    {
        await page.CallAsync("OpenCreate");
        page.Set("formName", "CRM feed");
        page.Set("formUrl", "https://receiver.example.com/hook");
        page.Get<HashSet<string>>("formEvents").Add(LifecycleEventTypes.TaskAssigned);
    }

    // The three requests of a page load with no expanded subscription: catalog, subscriptions, then deliveries.
    private static void ReplyPageLoad(RecordingHandler handler, params WebhookSubscriptionDto[] items) =>
        handler.Reply(Response(Catalog(captureEnabled: true)))
            .Reply(Response(new PagedResult<WebhookSubscriptionDto>(items, 1, 25, items.Length)))
            .Reply(Response(new PagedResult<WebhookDeliveryDto>([], 1, 25, 0)));

    private static TokenState AdminToken()
    {
        var token = new TokenState();
        token.Set("test-admin-token");
        return token;
    }

    private static WebhookEventCatalogDto Catalog(bool captureEnabled) => new(
        1,
        [],
        [.. LifecycleEventTypes.Subscribable.Select(type => new LifecycleEventTypeDto(type, type, true, []))],
        new LifecycleEventCaptureDto(captureEnabled, Now, "operator"));

    private static WebhookSubscriptionDto Subscription(Guid id) => new(
        id,
        "CRM feed",
        "Sends task events",
        WebhookSubscriptionStates.Paused,
        WebhookPauseReasons.Administrator,
        4,
        3,
        new WebhookDestinationDto(7, 1, "https://receiver.example.com/hook?token=secret", "https://receiver.example.com:443",
            [LifecycleEventTypes.TaskAssigned], null, Now, "admin"),
        [new WebhookSigningKeyDto(Guid.CreateVersion7(), WebhookSigningKeyStatuses.Current, Now, "admin", null, null, null, null)],
        new WebhookReceiverHealthDto(0, null, null, null, null, null, null, null),
        new WebhookQueueCountsDto(1, 0, 0, 2),
        Now,
        "admin",
        Now,
        "admin",
        null,
        null,
        null);

    private static WebhookSecretResultDto Secret(string secret) => new(Subscription(SubscriptionId), Guid.CreateVersion7(), secret);

    private static WebhookDeliveryDto FailedDelivery(Guid id, Guid subscriptionId, string subscriptionName, bool canRetry, bool canDiscard) => new(
        id, Guid.CreateVersion7(), subscriptionId, subscriptionName, 7, 1, "https://receiver.example.com:443",
        LifecycleEventTypes.TaskAssigned, false, null, 42, Now, WebhookDeliveryStates.Failed, null, 1, 3, 3,
        WebhookOutcomeCodes.ReceiverRejected, 400, WebhookOutcomeCodes.ReceiverRejected, Now, null, 6, Now, Now, canRetry, canDiscard);

    private static int Occurrences(string text, string value) => text.Split(value).Length - 1;

    private static WebhookDeliveryDetailDto DeliveryDetail(Guid id, string body = """{"type":"task.assigned"}""") => new(
        new WebhookDeliveryDto(
            id, Guid.CreateVersion7(), SubscriptionId, "CRM feed", 7, 1, "https://receiver.example.com:443", LifecycleEventTypes.TaskAssigned,
            false, "purchase-review", 42, Now, WebhookDeliveryStates.Failed, null, 1, 3, 3, WebhookOutcomeCodes.ReceiverRejected, 400,
            WebhookOutcomeCodes.ReceiverRejected, Now, null, 6, Now, Now, true, true),
        "https://receiver.example.com/hook?token=secret",
        body,
        WebhookSubscriptionStates.Active,
        Now,
        Now.AddHours(72),
        WebhookFormatting.MaxAttemptsPerGeneration,
        true,
        true);

    private static WorkflowApiClient Client(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://flowbit.test") });

    private static HttpResponseMessage Response<T>(T body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = JsonContent.Create(body) };

    private static HttpResponseMessage Problem(HttpStatusCode status, string message) =>
        new(status) { Content = JsonContent.Create(new { error = message }) };

    /// <summary>The route id the host passes on, and the page it rendered, so a test can change one and drive the other.</summary>
    private sealed class HostState
    {
        public Guid? Id { get; set; }
        public object? Page { get; set; }
        public Action? Render { get; set; }
    }

    /// <summary>Renders a page and captures its instance, so a test can call its handlers and inspect its state.</summary>
    private sealed class Host<TPage> : ComponentBase where TPage : IComponent
    {
        [Parameter] public HostState State { get; set; } = default!;

        protected override void OnInitialized() => State.Render = StateHasChanged;

        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
            builder.OpenComponent<TPage>(0);
            if (State.Id is { } id)
            {
                builder.AddComponentParameter(1, "Id", id);
            }
            builder.AddComponentReferenceCapture(2, page => State.Page = page);
            builder.CloseComponent();
        }
    }

    /// <summary>A page rendered with a scripted API, calling its private handlers on the renderer's dispatcher.</summary>
    private sealed class Harness<TPage> : IAsyncDisposable where TPage : IComponent
    {
        private static readonly MethodInfo StateHasChanged =
            typeof(ComponentBase).GetMethod("StateHasChanged", BindingFlags.Instance | BindingFlags.NonPublic)!;

        private readonly ServiceProvider provider;
        private readonly HostState state;
        private HtmlRootComponent root;

        private Harness(ServiceProvider provider, HtmlRenderer renderer, HostState state)
        {
            this.provider = provider;
            this.state = state;
            Renderer = renderer;
        }

        public HtmlRenderer Renderer { get; }

        private object Page => state.Page ?? throw new InvalidOperationException("The page has not rendered.");

        public static async Task<Harness<TPage>> RenderAsync(
            HttpMessageHandler handler, TokenState token, IJSRuntime? js = null, Guid? id = null, BrowserTimeZone? zone = null)
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddSingleton(Client(handler));
            services.AddSingleton(token);
            services.AddSingleton(js ?? new TestJsRuntime());
            if (zone is not null)
            {
                services.AddCascadingValue(_ => zone);
            }
            var provider = services.BuildServiceProvider();
            var renderer = new HtmlRenderer(provider, provider.GetRequiredService<ILoggerFactory>());
            var harness = new Harness<TPage>(provider, renderer, new HostState { Id = id });
            harness.root = await renderer.Dispatcher.InvokeAsync(() => renderer.RenderComponentAsync<Host<TPage>>(
                ParameterView.FromDictionary(new Dictionary<string, object?> { [nameof(Host<TPage>.State)] = harness.state })));
            return harness;
        }

        /// <summary>Calls a handler as a click would; the task completes when the handler does.</summary>
        public Task CallAsync(string method, params object?[] arguments) => Renderer.Dispatcher.InvokeAsync(() =>
            Page.GetType().GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Page, arguments) as Task ?? Task.CompletedTask);

        /// <summary>Gives the page another route id, as navigating between two deliveries does.</summary>
        public Task ShowAsync(Guid id)
        {
            state.Id = id;
            return Renderer.Dispatcher.InvokeAsync(state.Render!);
        }

        /// <summary>Renders the page's current state.</summary>
        public async Task<string> HtmlAsync()
        {
            await Renderer.Dispatcher.InvokeAsync(() => StateHasChanged.Invoke(Page, null));
            return await Renderer.Dispatcher.InvokeAsync(() => root.ToHtmlString());
        }

        public async Task<string> WaitForAsync(Func<string, bool> shown)
        {
            var html = await HtmlAsync();
            for (var attempt = 0; attempt < 200 && !shown(html); attempt++)
            {
                await Task.Delay(10);
                html = await HtmlAsync();
            }
            return html;
        }

        public T Get<T>(string name) => (T)Field(name).GetValue(Page)!;

        public void Set(string name, object? value) => Field(name).SetValue(Page, value);

        private FieldInfo Field(string name) =>
            Page.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"{Page.GetType().Name} has no field {name}.");

        public async ValueTask DisposeAsync()
        {
            await Renderer.DisposeAsync();
            await provider.DisposeAsync();
        }
    }

    private sealed record CapturedRequest(HttpMethod Method, string Path, string? Body);

    /// <summary>A request the test answers later, to act while the page waits for it.</summary>
    private sealed class Gate
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<HttpResponseMessage> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>Answers requests in order with queued replies, failures, or held requests; then with 403.</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Lock sync = new();
        private readonly Queue<Func<Task<HttpResponseMessage>>> replies = new();

        public RecordingHandler(params HttpResponseMessage[] responses)
        {
            foreach (var response in responses)
            {
                Reply(response);
            }
        }

        public List<CapturedRequest> Requests { get; } = [];

        public RecordingHandler Reply(HttpResponseMessage response)
        {
            lock (sync) replies.Enqueue(() => Task.FromResult(response));
            return this;
        }

        public RecordingHandler Fail(Exception exception)
        {
            lock (sync) replies.Enqueue(() => Task.FromException<HttpResponseMessage>(exception));
            return this;
        }

        public Gate Hold()
        {
            var held = new Gate();
            lock (sync)
            {
                replies.Enqueue(() =>
                {
                    held.Started.TrySetResult();
                    return held.Reply.Task;
                });
            }
            return held;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            Func<Task<HttpResponseMessage>>? reply;
            lock (sync)
            {
                Requests.Add(new(request.Method, request.RequestUri!.PathAndQuery, body));
                replies.TryDequeue(out reply);
            }
            return reply is null
                ? new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = JsonContent.Create(new { error = "denied" }) }
                : await reply();
        }
    }

    /// <summary>Answers <c>confirm</c> with <see cref="ConfirmResult"/> and can refuse the clipboard.</summary>
    private sealed class TestJsRuntime : IJSRuntime
    {
        public bool ConfirmResult { get; set; } = true;

        public Exception? ClipboardFailure { get; set; }

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args)
        {
            if (identifier == "navigator.clipboard.writeText" && ClipboardFailure is { } failure)
            {
                return ValueTask.FromException<TValue>(failure);
            }
            return ValueTask.FromResult(identifier == "confirm" ? (TValue)(object)ConfirmResult : default(TValue)!);
        }
    }
}
