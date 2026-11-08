using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class WebhookApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly WebhookBulkDeliveryRequest AllDeliveries = new(null, null, null, null, null, null, null);

    [Fact]
    public async Task CreateReturnsAPausedSubscriptionAndItsSecretOnceWithoutCaching()
    {
        var created = await CreateAsync();
        try
        {
            Assert.Equal(WebhookSubscriptionStates.Paused, created.Subscription.State);
            Assert.Equal(WebhookPauseReasons.Administrator, created.Subscription.PauseReason);
            Assert.StartsWith("whsec_", created.Secret, StringComparison.Ordinal);
            Assert.Equal(32, Convert.FromBase64String(created.Secret["whsec_".Length..]).Length);
            Assert.Equal("https://receiver.example.com:443", created.Subscription.Destination.Origin);
            Assert.Equal(1, created.Subscription.Destination.ConfigurationRevision);
            var key = Assert.Single(created.Subscription.SigningKeys);
            Assert.Equal(created.KeyId, key.Id);
            Assert.Equal(WebhookSigningKeyStatuses.Current, key.Status);

            using var detail = await SendAsync(HttpMethod.Get, $"/api/webhook-subscriptions/{created.Subscription.Id}");
            var detailText = await detail.Content.ReadAsStringAsync();
            Assert.DoesNotContain(created.Secret, detailText, StringComparison.Ordinal);
            Assert.DoesNotContain("whsec_", detailText, StringComparison.Ordinal);
            using var list = await SendAsync(HttpMethod.Get, "/api/webhook-subscriptions?page=1&pageSize=200");
            Assert.DoesNotContain("whsec_", await list.Content.ReadAsStringAsync(), StringComparison.Ordinal);

            // The stored secret is a Data Protection payload, never the raw key.
            await using var connection = await fixture.DataSource.OpenConnectionAsync();
            await using var command = new NpgsqlCommand(
                "SELECT \"ProtectedSecret\" FROM flowbit.webhook_signing_keys WHERE \"Id\" = @id", connection);
            command.Parameters.AddWithValue("id", created.KeyId);
            var stored = (byte[])(await command.ExecuteScalarAsync())!;
            Assert.False(stored.AsSpan().SequenceEqual(Convert.FromBase64String(created.Secret["whsec_".Length..])));
            // The audit names the destination origin only.
            Assert.Equal(0, await WebhookTestDatabase.CountEventsAsync(
                fixture.DataSource,
                "SELECT count(*) FROM flowbit.webhook_management_audit WHERE \"SubscriptionId\" = @id AND \"ChangesJson\"::text LIKE '%token=%'",
                ("id", created.Subscription.Id)));
        }
        finally
        {
            await ArchiveQuietlyAsync(created.Subscription);
        }
    }

    [Fact]
    public async Task CreateResponseIsNotCacheable()
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/webhook-subscriptions", CreateRequest());
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
        var created = await ReadAsync<WebhookSecretResultDto>(response);
        Assert.Equal($"/api/webhook-subscriptions/{created.Subscription.Id}", response.Headers.Location?.ToString());
        await ArchiveQuietlyAsync(created.Subscription);
    }

    [Theory]
    [InlineData("http://receiver.example.com/hook", "HTTPS")]
    [InlineData("https://receiver.example.com:8443/hook", "port 443")]
    [InlineData("https://user:pass@receiver.example.com/hook", "user information")]
    [InlineData("https://receiver.example.com/hook#fragment", "fragment")]
    [InlineData("https://10.0.0.5/hook", "not a public address")]
    [InlineData("https://169.254.169.254/latest/meta-data", "not a public address")]
    [InlineData("https://[::ffff:127.0.0.1]/hook", "not a public address")]
    [InlineData("https://localhost/hook", "loopback")]
    [InlineData("receiver.example.com/hook", "absolute HTTPS")]
    public async Task CreateRejectsUnsafeDestinations(string url, string expected)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/webhook-subscriptions", CreateRequest(url: url));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CreateRejectsUnsupportedEventsMissingFamiliesAndBadConcurrency()
    {
        using (var events = await SendAsync(HttpMethod.Post, "/api/webhook-subscriptions",
                   CreateRequest(eventTypes: ["instance.started", "webhook.test"])))
        {
            Assert.Equal(HttpStatusCode.BadRequest, events.StatusCode);
        }
        using (var empty = await SendAsync(HttpMethod.Post, "/api/webhook-subscriptions", CreateRequest(eventTypes: [])))
        {
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        }
        using (var family = await SendAsync(HttpMethod.Post, "/api/webhook-subscriptions",
                   CreateRequest(workflowKey: "missing-family-" + Guid.NewGuid().ToString("N"))))
        {
            Assert.Equal(HttpStatusCode.BadRequest, family.StatusCode);
            Assert.Contains("existing workflow family", await family.Content.ReadAsStringAsync(), StringComparison.Ordinal);
        }
        using (var concurrency = await SendAsync(HttpMethod.Post, "/api/webhook-subscriptions",
                   CreateRequest(maxConcurrency: 17)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, concurrency.StatusCode);
        }
        using (var sort = await SendAsync(HttpMethod.Get, "/api/webhook-subscriptions?sort=name"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, sort.StatusCode);
        }
        using (var pageSize = await SendAsync(HttpMethod.Get, "/api/webhook-deliveries?pageSize=500"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, pageSize.StatusCode);
        }
    }

    [Fact]
    public async Task EveryRouteRequiresTheSettingsRole()
    {
        var model = await CreateWorkflowAsync();
        var created = await CreateAsync(workflowKey: model);
        try
        {
            var id = created.Subscription.Id;
            var delivery = Assert.Single(await InsertDeliveriesAsync(
                id, created.Subscription.Destination.RevisionId, model, WebhookDeliveryStates.Failed));
            var deliveryRevision = (await DeliveryAsync(delivery)).Delivery.Revision;
            var routes = AllRoutes(created.Subscription, delivery, deliveryRevision);
            Assert.Equal(18, routes.Length);
            var actor = "operator-" + Guid.NewGuid().ToString("N")[..8];
            foreach (var (method, path, body) in routes)
            {
                using var forbidden = await SendAsync(method, path, body, user: actor, roles: "User", suppressAdmin: true);
                Assert.True(HttpStatusCode.Forbidden == forbidden.StatusCode, $"{method} {path}: {(int)forbidden.StatusCode}");
                using var anonymous = new HttpRequestMessage(method, path) { Content = Json(body) };
                using var unauthenticated = await fixture.Client.SendAsync(anonymous);
                Assert.True(
                    HttpStatusCode.Unauthorized == unauthenticated.StatusCode,
                    $"{method} {path}: {(int)unauthenticated.StatusCode}");
            }

            // Nothing ran: no audit entry names the operator, and the subscription and delivery are unchanged.
            Assert.Equal(0, await CountAsync(
                "SELECT count(*) FROM flowbit.webhook_management_audit WHERE \"Actor\" = @actor", ("actor", actor)));
            var after = await SubscriptionAsync(id);
            Assert.Equal(created.Subscription.Revision, after.Subscription.Revision);
            Assert.Equal(WebhookSubscriptionStates.Paused, after.Subscription.State);
            Assert.Equal(deliveryRevision, (await DeliveryAsync(delivery)).Delivery.Revision);
        }
        finally
        {
            await ArchiveQuietlyAsync(created.Subscription);
        }
    }

    [Fact]
    public async Task AWorkflowFamilyFilterGrantsNoWebhookAuthority()
    {
        const string managerRole = "webhook-family-manager";
        var model = UserTaskModel(managerRole);
        var workflow = await CreateWorkflowAsync(model);
        var created = await CreateAsync(workflowKey: model.Id);
        try
        {
            var instance = await StartAsync(workflow.Id);
            var delivery = Assert.Single(await InsertDeliveriesAsync(
                created.Subscription.Id, created.Subscription.Destination.RevisionId, model.Id, WebhookDeliveryStates.Failed));
            var deliveryRevision = (await DeliveryAsync(delivery)).Delivery.Revision;

            // The family's assignment-manager role manages its tasks...
            using (var tasks = await SendAsync(HttpMethod.Get, $"/api/user-tasks/manage?instanceId={instance.Id}",
                       user: "family-manager", roles: managerRole, suppressAdmin: true))
            {
                Assert.Equal(HttpStatusCode.OK, tasks.StatusCode);
                Assert.Single((await ReadAsync<PagedResult<ManagedUserTaskDto>>(tasks)).Items);
            }

            // ...but grants nothing over the subscriptions and deliveries that filter on the family.
            var routes = AllRoutes(created.Subscription, delivery, deliveryRevision)
                .Append((HttpMethod.Get, $"/api/webhook-deliveries?workflowKey={model.Id}", null));
            foreach (var (method, path, body) in routes)
            {
                using var forbidden = await SendAsync(method, path, body,
                    user: "family-manager", roles: managerRole, suppressAdmin: true);
                Assert.True(HttpStatusCode.Forbidden == forbidden.StatusCode, $"{method} {path}: {(int)forbidden.StatusCode}");
            }
            Assert.Equal(created.Subscription.Revision, (await SubscriptionAsync(created.Subscription.Id)).Subscription.Revision);
        }
        finally
        {
            await ArchiveQuietlyAsync(created.Subscription);
        }
    }

    [Fact]
    public async Task UnknownSubscriptionsAndDeliveriesReturn404()
    {
        var template = (await CreateAsync()).Subscription;
        try
        {
            var missingSubscription = template with { Id = Guid.CreateVersion7() };
            var missingDelivery = Guid.CreateVersion7();
            var routes = AllRoutes(missingSubscription, missingDelivery, 1)
                .Where(route => route.Path.Contains(missingSubscription.Id.ToString(), StringComparison.Ordinal)
                    || route.Path.Contains(missingDelivery.ToString(), StringComparison.Ordinal))
                .ToArray();
            Assert.Equal(14, routes.Length);
            foreach (var (method, path, body) in routes)
            {
                using var response = await SendAsync(method, path, body);
                Assert.True(HttpStatusCode.NotFound == response.StatusCode, $"{method} {path}: {(int)response.StatusCode}");
            }
        }
        finally
        {
            await ArchiveQuietlyAsync(template);
        }
    }

    [Fact]
    public async Task EventCatalogListsTheElevenSubscribableTypesAndTheCaptureSwitch()
    {
        using var response = await SendAsync(HttpMethod.Get, "/api/webhook-event-types");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var catalog = await ReadAsync<WebhookEventCatalogDto>(response);
        Assert.Equal(1, catalog.SchemaVersion);
        Assert.Equal(LifecycleEventTypes.Subscribable, catalog.EventTypes.Where(type => type.Subscribable).Select(type => type.Type));
        Assert.Contains(catalog.EventTypes, type => type.Type == LifecycleEventTypes.WebhookTest && !type.Subscribable);
        Assert.Equal("eventId", catalog.EnvelopeFields[1].Name);
        Assert.NotNull(catalog.Capture);
    }

    [Fact]
    public async Task UpdatesUseRevisionsAndOnlyDestinationChangesCreateConfigurationRevisions()
    {
        var created = await CreateAsync();
        try
        {
            var subscription = created.Subscription;
            using (var stale = await SendAsync(HttpMethod.Put, $"/api/webhook-subscriptions/{subscription.Id}",
                       UpdateRequest(subscription, subscription.Revision + 5)))
            {
                Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            }
            using (var rename = await SendAsync(HttpMethod.Put, $"/api/webhook-subscriptions/{subscription.Id}",
                       UpdateRequest(subscription, subscription.Revision, name: "Renamed")))
            {
                subscription = await ReadAsync<WebhookSubscriptionDto>(rename);
            }
            Assert.Equal(1, subscription.Destination.ConfigurationRevision);
            Assert.Equal("Renamed", subscription.Name);
            using (var move = await SendAsync(HttpMethod.Put, $"/api/webhook-subscriptions/{subscription.Id}",
                       UpdateRequest(subscription, subscription.Revision, url: "https://other.example.com/hook?token=abc")))
            {
                Assert.Equal(HttpStatusCode.OK, move.StatusCode);
                subscription = await ReadAsync<WebhookSubscriptionDto>(move);
            }
            Assert.Equal(2, subscription.Destination.ConfigurationRevision);
            Assert.Equal("https://other.example.com/hook?token=abc", subscription.Destination.EndpointUrl);
            using var detailResponse = await SendAsync(HttpMethod.Get, $"/api/webhook-subscriptions/{subscription.Id}");
            var detail = await ReadAsync<WebhookSubscriptionDetailDto>(detailResponse);
            Assert.Equal([2, 1], detail.Revisions.Select(revision => revision.Destination.ConfigurationRevision));
            Assert.True(detail.Revisions[0].IsCurrent);
            // Audit entries keep the origin and never the path or query.
            Assert.Equal(0, await WebhookTestDatabase.CountEventsAsync(
                fixture.DataSource,
                "SELECT count(*) FROM flowbit.webhook_management_audit WHERE \"SubscriptionId\" = @id AND \"ChangesJson\"::text LIKE '%token%'",
                ("id", subscription.Id)));
        }
        finally
        {
            await ArchiveQuietlyAsync(created.Subscription);
        }
    }

    [Fact]
    public async Task PauseResumeTestRotateRevokeAndArchiveFollowTheirStateRules()
    {
        var created = await CreateAsync();
        var id = created.Subscription.Id;
        try
        {
            var resumed = await PostAsync<WebhookSubscriptionDto>($"/api/webhook-subscriptions/{id}/resume",
                new WebhookRevisionRequest(created.Subscription.Revision));
            Assert.Equal(WebhookSubscriptionStates.Active, resumed.State);
            Assert.Null(resumed.PauseReason);
            using (var staleResume = await SendAsync(HttpMethod.Post, $"/api/webhook-subscriptions/{id}/resume",
                       new WebhookRevisionRequest(created.Subscription.Revision)))
            {
                Assert.Equal(HttpStatusCode.Conflict, staleResume.StatusCode);
            }

            var paused = await PostAsync<WebhookSubscriptionDto>($"/api/webhook-subscriptions/{id}/pause", null);
            Assert.Equal(WebhookSubscriptionStates.Paused, paused.State);
            var pausedAgain = await PostAsync<WebhookSubscriptionDto>($"/api/webhook-subscriptions/{id}/pause", null);
            Assert.Equal(paused.Revision, pausedAgain.Revision);

            // Tests are delivered while paused; one may be outstanding at a time.
            using (var test = await SendAsync(HttpMethod.Post, $"/api/webhook-subscriptions/{id}/test"))
            {
                Assert.Equal(HttpStatusCode.Accepted, test.StatusCode);
                var queued = await ReadAsync<WebhookTestResultDto>(test);
                using var delivery = await SendAsync(HttpMethod.Get, $"/api/webhook-deliveries/{queued.DeliveryId}");
                var detail = await ReadAsync<WebhookDeliveryDetailDto>(delivery);
                Assert.True(detail.Delivery.IsTest);
                Assert.Equal(WebhookDeliveryStates.Pending, detail.Delivery.State);
                var body = LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<WebhookTestEventDataDto>>(detail.Body);
                Assert.Equal(queued.EventId, body.EventId);
                Assert.Equal(id, body.Data.SubscriptionId);
                Assert.Null(body.WorkflowKey);
                Assert.Null(body.InstanceId);
            }
            using (var outstanding = await SendAsync(HttpMethod.Post, $"/api/webhook-subscriptions/{id}/test"))
            {
                Assert.Equal(HttpStatusCode.Conflict, outstanding.StatusCode);
            }

            var rotated = await PostAsync<WebhookSecretResultDto>($"/api/webhook-subscriptions/{id}/rotate-secret",
                new RotateWebhookSecretRequest(pausedAgain.Revision, 3600));
            Assert.NotEqual(created.Secret, rotated.Secret);
            Assert.Equal(
                [WebhookSigningKeyStatuses.Current, WebhookSigningKeyStatuses.Previous],
                rotated.Subscription.SigningKeys.Select(key => key.Status));
            using (var again = await SendAsync(HttpMethod.Post, $"/api/webhook-subscriptions/{id}/rotate-secret",
                       new RotateWebhookSecretRequest(rotated.Subscription.Revision, 3600)))
            {
                Assert.Equal(HttpStatusCode.Conflict, again.StatusCode);
            }
            var revoked = await PostAsync<WebhookSubscriptionDto>($"/api/webhook-subscriptions/{id}/revoke-previous-secret",
                new WebhookRevisionRequest(rotated.Subscription.Revision));
            Assert.Equal([WebhookSigningKeyStatuses.Current], revoked.SigningKeys.Select(key => key.Status));
            var rotatedAgain = await PostAsync<WebhookSecretResultDto>($"/api/webhook-subscriptions/{id}/rotate-secret",
                new RotateWebhookSecretRequest(revoked.Revision, 0));
            Assert.Single(rotatedAgain.Subscription.SigningKeys);
            // Overlap again, so revocation has a previous key to refuse once archived.
            var overlapping = await PostAsync<WebhookSecretResultDto>($"/api/webhook-subscriptions/{id}/rotate-secret",
                new RotateWebhookSecretRequest(rotatedAgain.Subscription.Revision, 3600));

            WebhookSubscriptionDto archivedSubscription;
            using (var archive = await SendAsync(HttpMethod.Post, $"/api/webhook-subscriptions/{id}/archive",
                       new WebhookRevisionRequest(overlapping.Subscription.Revision)))
            {
                Assert.Equal(HttpStatusCode.Accepted, archive.StatusCode);
                archivedSubscription = await ReadAsync<WebhookSubscriptionDto>(archive);
                Assert.Equal(WebhookSubscriptionStates.Archived, archivedSubscription.State);
            }
            using (var repeat = await SendAsync(HttpMethod.Post, $"/api/webhook-subscriptions/{id}/archive",
                       new WebhookRevisionRequest(1)))
            {
                Assert.Equal(HttpStatusCode.Accepted, repeat.StatusCode);
                Assert.Equal(archivedSubscription.Revision, (await ReadAsync<WebhookSubscriptionDto>(repeat)).Revision);
            }
            // A repeat still needs a positive revision, even though it is not compared.
            using (var invalid = await SendAsync(HttpMethod.Post, $"/api/webhook-subscriptions/{id}/archive",
                       new WebhookRevisionRequest(0)))
            {
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            }

            // At the current revision, so the archived state, not a stale revision, refuses each change.
            var current = archivedSubscription.Revision;
            foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                     {
                         (HttpMethod.Put, $"/api/webhook-subscriptions/{id}", UpdateRequest(created.Subscription, current, name: "Edited")),
                         (HttpMethod.Post, $"/api/webhook-subscriptions/{id}/pause", null),
                         (HttpMethod.Post, $"/api/webhook-subscriptions/{id}/resume", new WebhookRevisionRequest(current)),
                         (HttpMethod.Post, $"/api/webhook-subscriptions/{id}/test", null),
                         (HttpMethod.Post, $"/api/webhook-subscriptions/{id}/rotate-secret", new RotateWebhookSecretRequest(current, 0)),
                         (HttpMethod.Post, $"/api/webhook-subscriptions/{id}/revoke-previous-secret", new WebhookRevisionRequest(current))
                     })
            {
                using var rejected = await SendAsync(method, path, body);
                var text = await ErrorAsync(rejected);
                Assert.True(HttpStatusCode.Conflict == rejected.StatusCode, $"{method} {path}: {(int)rejected.StatusCode} {text}");
                Assert.Contains("archived", text, StringComparison.Ordinal);
                Assert.DoesNotContain("changed", text, StringComparison.Ordinal);
            }
            using var archived = await SendAsync(HttpMethod.Get, $"/api/webhook-subscriptions/{id}");
            Assert.Equal(HttpStatusCode.OK, archived.StatusCode);
            var unchanged = (await ReadAsync<WebhookSubscriptionDetailDto>(archived)).Subscription;
            Assert.Equal(current, unchanged.Revision);
            Assert.Equal(archivedSubscription.Name, unchanged.Name);
        }
        finally
        {
            await ArchiveQuietlyAsync(created.Subscription);
        }
    }

    [Fact]
    public async Task ArchivedSubscriptionsLeaveTheirDeliveriesToTheArchivalCoordinator()
    {
        var model = await CreateWorkflowAsync();
        var created = await CreateAsync(workflowKey: model);
        var id = created.Subscription.Id;
        try
        {
            var active = await PostAsync<WebhookSubscriptionDto>($"/api/webhook-subscriptions/{id}/resume",
                new WebhookRevisionRequest(created.Subscription.Revision));
            var revisionId = active.Destination.RevisionId;
            var failed = Assert.Single(await InsertDeliveriesAsync(id, revisionId, model, WebhookDeliveryStates.Failed));
            var pending = Assert.Single(await InsertDeliveriesAsync(id, revisionId, model, WebhookDeliveryStates.Pending));
            Assert.True((await DeliveryAsync(failed)).CanDiscard);
            using (var archive = await SendAsync(HttpMethod.Post, $"/api/webhook-subscriptions/{id}/archive",
                       new WebhookRevisionRequest(active.Revision)))
            {
                Assert.Equal(HttpStatusCode.Accepted, archive.StatusCode);
            }

            // The Worker's archival coordinator discards them; nothing else may change them meanwhile.
            var failedDetail = await DeliveryAsync(failed);
            var pendingDetail = await DeliveryAsync(pending);
            foreach (var detail in new[] { failedDetail, pendingDetail })
            {
                Assert.Equal(WebhookSubscriptionStates.Archived, detail.SubscriptionState);
                Assert.False(detail.CanRetry);
                Assert.False(detail.CanDiscard);
            }
            foreach (var (path, body) in new (string, object)[]
                     {
                         ($"/api/webhook-deliveries/{failed}/discard", new WebhookRevisionRequest(failedDetail.Delivery.Revision)),
                         ($"/api/webhook-deliveries/{pending}/discard", new WebhookRevisionRequest(pendingDetail.Delivery.Revision)),
                         ($"/api/webhook-deliveries/{failed}/retry", new WebhookRevisionRequest(failedDetail.Delivery.Revision)),
                         ($"/api/webhook-subscriptions/{id}/deliveries/discard", AllDeliveries),
                         ($"/api/webhook-subscriptions/{id}/deliveries/retry", AllDeliveries)
                     })
            {
                using var rejected = await SendAsync(HttpMethod.Post, path, body);
                var text = await ErrorAsync(rejected);
                Assert.True(HttpStatusCode.Conflict == rejected.StatusCode, $"{path}: {(int)rejected.StatusCode} {text}");
                Assert.Contains("discards its outstanding deliveries automatically", text, StringComparison.Ordinal);
            }
            Assert.Equal(failedDetail.Delivery, (await DeliveryAsync(failed)).Delivery);
            Assert.Equal(pendingDetail.Delivery, (await DeliveryAsync(pending)).Delivery);
            Assert.Equal(0, await CountAsync(
                """
                SELECT count(*) FROM flowbit.webhook_management_audit
                WHERE "SubscriptionId" = @id AND "Operation" IN ('deliveryRetried', 'deliveryDiscarded', 'deliveriesBulkRetried', 'deliveriesBulkDiscarded')
                """,
                ("id", id)));
        }
        finally
        {
            await ArchiveQuietlyAsync(created.Subscription);
        }
    }

    [Fact]
    public async Task FailedDeliveriesSupportRevisionProtectedSingleAndBulkRecovery()
    {
        var model = await CreateWorkflowAsync();
        var created = await CreateAsync(workflowKey: model);
        var id = created.Subscription.Id;
        try
        {
            var active = await PostAsync<WebhookSubscriptionDto>($"/api/webhook-subscriptions/{id}/resume",
                new WebhookRevisionRequest(created.Subscription.Revision));
            var deliveries = await InsertDeliveriesAsync(id, active.Destination.RevisionId, model, WebhookDeliveryStates.Failed, count: 3);

            using var listResponse = await SendAsync(HttpMethod.Get,
                $"/api/webhook-deliveries?subscriptionId={id}&state=failed&failureCode=receiver_rejected&pageSize=2");
            var page = await ReadAsync<PagedResult<WebhookDeliveryDto>>(listResponse);
            Assert.Equal(3, page.TotalCount);
            Assert.Equal(2, page.Items.Count);

            var first = await DeliveryAsync(deliveries[0]);
            Assert.True(first.CanRetry);
            Assert.True(first.CanDiscard);
            var retried = await PostAsync<WebhookDeliveryActionResultDto>(
                $"/api/webhook-deliveries/{deliveries[0]}/retry",
                new WebhookRevisionRequest(first.Delivery.Revision),
                HttpStatusCode.Accepted);
            Assert.Equal(WebhookDeliveryStates.Pending, retried.State);
            Assert.Equal(2, retried.RetryGeneration);
            // A double click with the old revision cannot enqueue another generation.
            using (var doubleClick = await SendAsync(HttpMethod.Post, $"/api/webhook-deliveries/{deliveries[0]}/retry",
                       new WebhookRevisionRequest(first.Delivery.Revision)))
            {
                Assert.Equal(HttpStatusCode.Conflict, doubleClick.StatusCode);
            }
            var afterRetry = await DeliveryAsync(deliveries[0]);
            Assert.Null(afterRetry.DeadlineAt);
            Assert.Equal(0, afterRetry.Delivery.GenerationAttempts);
            Assert.Equal(first.Delivery.EventId, afterRetry.Delivery.EventId);

            var discarded = await PostAsync<WebhookDeliveryActionResultDto>(
                $"/api/webhook-deliveries/{deliveries[1]}/discard",
                new WebhookRevisionRequest((await DeliveryAsync(deliveries[1])).Delivery.Revision));
            Assert.Equal(WebhookDeliveryStates.Discarded, discarded.State);
            using (var discardAgain = await SendAsync(HttpMethod.Post, $"/api/webhook-deliveries/{deliveries[1]}/discard",
                       new WebhookRevisionRequest(discarded.Revision)))
            {
                Assert.Equal(HttpStatusCode.Conflict, discardAgain.StatusCode);
            }

            var bulkRetry = await PostAsync<WebhookBulkDeliveryResultDto>(
                $"/api/webhook-subscriptions/{id}/deliveries/retry",
                new WebhookBulkDeliveryRequest(null, null, null, null, "receiver_rejected", null, null));
            Assert.Equal(1, bulkRetry.Changed);
            Assert.False(bulkRetry.HasMore);
            var bulkDiscard = await PostAsync<WebhookBulkDeliveryResultDto>(
                $"/api/webhook-subscriptions/{id}/deliveries/discard", AllDeliveries);
            Assert.Equal(2, bulkDiscard.Changed);
            var repeat = await PostAsync<WebhookBulkDeliveryResultDto>(
                $"/api/webhook-subscriptions/{id}/deliveries/discard", AllDeliveries);
            Assert.Equal(0, repeat.Changed);
            Assert.Equal(3, await WebhookTestDatabase.CountEventsAsync(
                fixture.DataSource,
                "SELECT count(*) FROM flowbit.webhook_management_audit WHERE \"SubscriptionId\" = @id AND \"Operation\" LIKE 'deliveriesBulk%' AND \"Actor\" = 'test-admin'",
                ("id", id)));
        }
        finally
        {
            await ArchiveQuietlyAsync(created.Subscription);
        }
    }

    [Fact]
    public async Task DiscardLeavesDeliveriesBeingSentToTheirWorker()
    {
        var model = await CreateWorkflowAsync();
        var created = await CreateAsync(workflowKey: model);
        var id = created.Subscription.Id;
        try
        {
            var revisionId = created.Subscription.Destination.RevisionId;
            var leased = await InsertDeliveriesAsync(id, revisionId, model, WebhookDeliveryStates.Delivering, count: 2);
            var pending = await InsertDeliveriesAsync(id, revisionId, model, WebhookDeliveryStates.Pending, count: 2);
            var before = await DeliveryAsync(leased[0]);
            Assert.False(before.CanDiscard);
            using (var single = await SendAsync(HttpMethod.Post, $"/api/webhook-deliveries/{leased[0]}/discard",
                       new WebhookRevisionRequest(before.Delivery.Revision)))
            {
                Assert.Equal(HttpStatusCode.Conflict, single.StatusCode);
                Assert.Contains("delivering this event now", await ErrorAsync(single), StringComparison.Ordinal);
            }

            var bulk = await PostAsync<WebhookBulkDeliveryResultDto>(
                $"/api/webhook-subscriptions/{id}/deliveries/discard", AllDeliveries);
            Assert.Equal(new WebhookBulkDeliveryResultDto(4, 2, 2, false), bulk);
            foreach (var lease in leased)
            {
                var detail = await DeliveryAsync(lease);
                Assert.Equal(WebhookDeliveryStates.Delivering, detail.Delivery.State);
                Assert.Equal(before.Delivery.Revision, detail.Delivery.Revision);
            }
            foreach (var queued in pending)
            {
                Assert.Equal(WebhookDeliveryStates.Discarded, (await DeliveryAsync(queued)).Delivery.State);
            }
            Assert.Equal(1, await CountAsync(
                """
                SELECT count(*) FROM flowbit.webhook_management_audit
                WHERE "SubscriptionId" = @id AND "Operation" = 'deliveriesBulkDiscarded'
                  AND ("ChangesJson"->>'skipped')::int = 2 AND ("ChangesJson"->>'changed')::int = 2
                """,
                ("id", id)));
        }
        finally
        {
            await ArchiveQuietlyAsync(created.Subscription);
        }
    }

    [Fact]
    public async Task BulkRecoveryActsOnAtMostOneThousandDeliveriesOldestFirst()
    {
        var model = await CreateWorkflowAsync();
        var created = await CreateAsync(workflowKey: model);
        var id = created.Subscription.Id;
        try
        {
            var active = await PostAsync<WebhookSubscriptionDto>($"/api/webhook-subscriptions/{id}/resume",
                new WebhookRevisionRequest(created.Subscription.Revision));
            var limit = WebhookConstraints.MaxBulkDeliveries;
            var failed = await InsertDeliveriesAsync(id, active.Destination.RevisionId, model, WebhookDeliveryStates.Failed,
                count: limit + 1);

            var firstRetry = await PostAsync<WebhookBulkDeliveryResultDto>(
                $"/api/webhook-subscriptions/{id}/deliveries/retry", AllDeliveries);
            Assert.Equal(new WebhookBulkDeliveryResultDto(limit, limit, 0, true), firstRetry);
            Assert.Equal(WebhookDeliveryStates.Pending, (await DeliveryAsync(failed[0])).Delivery.State);
            Assert.Equal(WebhookDeliveryStates.Failed, (await DeliveryAsync(failed[^1])).Delivery.State);
            var secondRetry = await PostAsync<WebhookBulkDeliveryResultDto>(
                $"/api/webhook-subscriptions/{id}/deliveries/retry", AllDeliveries);
            Assert.Equal(new WebhookBulkDeliveryResultDto(1, 1, 0, false), secondRetry);

            var firstDiscard = await PostAsync<WebhookBulkDeliveryResultDto>(
                $"/api/webhook-subscriptions/{id}/deliveries/discard", AllDeliveries);
            Assert.Equal(new WebhookBulkDeliveryResultDto(limit, limit, 0, true), firstDiscard);
            Assert.Equal(WebhookDeliveryStates.Discarded, (await DeliveryAsync(failed[0])).Delivery.State);
            Assert.Equal(WebhookDeliveryStates.Pending, (await DeliveryAsync(failed[^1])).Delivery.State);
            var secondDiscard = await PostAsync<WebhookBulkDeliveryResultDto>(
                $"/api/webhook-subscriptions/{id}/deliveries/discard", AllDeliveries);
            Assert.Equal(new WebhookBulkDeliveryResultDto(1, 1, 0, false), secondDiscard);
        }
        finally
        {
            await ArchiveQuietlyAsync(created.Subscription);
        }
    }

    [Fact]
    public async Task InvalidBulkFiltersReturn400()
    {
        var created = await CreateAsync();
        var id = created.Subscription.Id;
        try
        {
            var instant = DateTimeOffset.UtcNow;
            var invalid = new (object Body, string Expected)[]
            {
                (AllDeliveries with { EventType = "instance.begun" }, "not a lifecycle event type"),
                (AllDeliveries with { WorkflowKey = " " }, "Workflow key filter"),
                (AllDeliveries with { InstanceId = 0 }, "Instance id must be positive"),
                (AllDeliveries with { RevisionId = -1 }, "Destination revision id must be positive"),
                (AllDeliveries with { FailureCode = "receiver_rejeted" }, "receiver_rejected"),
                (AllDeliveries with { FailureCode = WebhookOutcomeCodes.Timeout }, "is not a delivery failure code"),
                (AllDeliveries with { From = instant, To = instant }, "'from' must be earlier than 'to'"),
                (new Dictionary<string, object?> { ["failureCod"] = "receiver_rejected" }, "Unknown property 'failureCod'")
            };
            foreach (var route in new[] { "retry", "discard" })
            {
                foreach (var (body, expected) in invalid)
                {
                    using var response = await SendAsync(HttpMethod.Post, $"/api/webhook-subscriptions/{id}/deliveries/{route}", body);
                    var text = await ErrorAsync(response);
                    Assert.True(HttpStatusCode.BadRequest == response.StatusCode, $"{route} {expected}: {(int)response.StatusCode} {text}");
                    Assert.Contains(expected, text, StringComparison.Ordinal);
                }
            }
            Assert.Equal(0, await CountAsync(
                "SELECT count(*) FROM flowbit.webhook_management_audit WHERE \"SubscriptionId\" = @id AND \"Operation\" LIKE 'deliveriesBulk%'",
                ("id", id)));
        }
        finally
        {
            await ArchiveQuietlyAsync(created.Subscription);
        }
    }

    [Fact]
    public async Task DateFiltersAcceptAnyOffsetWithAnInclusiveFromAndAnExclusiveTo()
    {
        var model = await CreateWorkflowAsync();
        var created = await CreateAsync(workflowKey: model);
        var id = created.Subscription.Id;
        try
        {
            // 10:00, 11:00, and 12:00 UTC.
            var deliveries = await InsertDeliveriesAsync(id, created.Subscription.Destination.RevisionId, model,
                WebhookDeliveryStates.Failed, count: 3,
                firstOccurredAt: new DateTimeOffset(2026, 3, 1, 10, 0, 0, TimeSpan.Zero), spacing: TimeSpan.FromHours(1));
            // 11:00 UTC written as 13:00+02:00, and 12:00 UTC as 07:00-05:00.
            const string from = "2026-03-01T13:00:00+02:00";
            const string to = "2026-03-01T07:00:00-05:00";

            using (var list = await SendAsync(HttpMethod.Get,
                       $"/api/webhook-deliveries?subscriptionId={id}&from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}"))
            {
                Assert.Equal(HttpStatusCode.OK, list.StatusCode);
                var page = await ReadAsync<PagedResult<WebhookDeliveryDto>>(list);
                Assert.Equal(1, page.TotalCount);
                Assert.Equal(deliveries[1], Assert.Single(page.Items).Id);
            }

            var bulk = await PostAsync<WebhookBulkDeliveryResultDto>(
                $"/api/webhook-subscriptions/{id}/deliveries/discard",
                AllDeliveries with
                {
                    From = DateTimeOffset.Parse(from, CultureInfo.InvariantCulture),
                    To = DateTimeOffset.Parse(to, CultureInfo.InvariantCulture)
                });
            Assert.Equal(new WebhookBulkDeliveryResultDto(1, 1, 0, false), bulk);
            Assert.Equal(
                [WebhookDeliveryStates.Failed, WebhookDeliveryStates.Discarded, WebhookDeliveryStates.Failed],
                await Task.WhenAll(deliveries.Select(async delivery => (await DeliveryAsync(delivery)).Delivery.State)));

            // Equal instants written with different offsets are an empty range.
            using var empty = await SendAsync(HttpMethod.Get,
                $"/api/webhook-deliveries?subscriptionId={id}&from={Uri.EscapeDataString(from)}&to=2026-03-01T11:00:00Z");
            Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
            Assert.Contains("'from' must be earlier than 'to'", await ErrorAsync(empty), StringComparison.Ordinal);
        }
        finally
        {
            await ArchiveQuietlyAsync(created.Subscription);
        }
    }

    [Fact]
    public async Task ListsUseTheDefaultPageSizeAndTheirFixedOrder()
    {
        var model = await CreateWorkflowAsync();
        var older = await CreateAsync(workflowKey: model);
        var newer = await CreateAsync(workflowKey: model);
        try
        {
            using (var list = await SendAsync(HttpMethod.Get, "/api/webhook-subscriptions"))
            {
                var page = await ReadAsync<PagedResult<WebhookSubscriptionDto>>(list);
                Assert.Equal(1, page.Page);
                Assert.Equal(50, page.PageSize);
                Assert.InRange(page.Items.Count, 2, 50);
                Assert.Equal([newer.Subscription.Id, older.Subscription.Id], page.Items.Take(2).Select(item => item.Id));
                Assert.Equal(await CountAsync("SELECT count(*) FROM flowbit.webhook_subscriptions"), page.TotalCount);
            }

            var deliveries = await InsertDeliveriesAsync(newer.Subscription.Id, newer.Subscription.Destination.RevisionId,
                model, WebhookDeliveryStates.Failed, count: 3);
            using (var list = await SendAsync(HttpMethod.Get, $"/api/webhook-deliveries?subscriptionId={newer.Subscription.Id}"))
            {
                var page = await ReadAsync<PagedResult<WebhookDeliveryDto>>(list);
                Assert.Equal(1, page.Page);
                Assert.Equal(50, page.PageSize);
                Assert.Equal(3, page.TotalCount);
                Assert.Equal(Enumerable.Reverse(deliveries), page.Items.Select(item => item.Id));
            }

            await WebhookTestDatabase.ExecuteAsync(fixture.DataSource, """
                INSERT INTO flowbit.webhook_delivery_attempts
                    ("DeliveryId", "AttemptNumber", "RetryGeneration", "LeaseGeneration", "SigningKeyIds", "AcquiredAt",
                     "SendIntentAt", "CompletedAt", "Outcome", "HttpStatus", "DurationMilliseconds")
                SELECT @delivery, number, 1, number, '{}'::uuid[], now(), now(), now(), 'receiver_rejected', 400, 5
                FROM unnest(ARRAY[3, 1, 2]) AS attempt(number)
                """, ("delivery", deliveries[0]));
            using (var list = await SendAsync(HttpMethod.Get, $"/api/webhook-deliveries/{deliveries[0]}/attempts"))
            {
                var page = await ReadAsync<PagedResult<WebhookDeliveryAttemptDto>>(list);
                Assert.Equal(1, page.Page);
                Assert.Equal(50, page.PageSize);
                Assert.Equal(3, page.TotalCount);
                Assert.Equal([1, 2, 3], page.Items.Select(item => item.AttemptNumber));
            }
            using (var second = await SendAsync(HttpMethod.Get, $"/api/webhook-deliveries/{deliveries[0]}/attempts?page=2&pageSize=2"))
            {
                var page = await ReadAsync<PagedResult<WebhookDeliveryAttemptDto>>(second);
                Assert.Equal(3, Assert.Single(page.Items).AttemptNumber);
            }
        }
        finally
        {
            await ArchiveQuietlyAsync(older.Subscription);
            await ArchiveQuietlyAsync(newer.Subscription);
        }
    }

    [Fact]
    public async Task PagesWhoseOffsetDoesNotFitReturn400()
    {
        foreach (var path in new[]
                 {
                     "/api/webhook-subscriptions?page=2147483647&pageSize=200",
                     "/api/webhook-subscriptions?page=10737420&pageSize=200",
                     "/api/webhook-deliveries?page=2147483647&pageSize=200",
                     $"/api/webhook-deliveries/{Guid.CreateVersion7()}/attempts?page=2147483647&pageSize=50"
                 })
        {
            using var response = await SendAsync(HttpMethod.Get, path);
            var text = await ErrorAsync(response);
            Assert.True(HttpStatusCode.BadRequest == response.StatusCode, $"{path}: {(int)response.StatusCode} {text}");
            Assert.Contains("is too large", text, StringComparison.Ordinal);
        }

        // The largest offsets that fit still answer, with an empty page and the exact total.
        foreach (var path in new[]
                 {
                     "/api/webhook-subscriptions?page=2147483647&pageSize=1",
                     "/api/webhook-subscriptions?page=10737419&pageSize=200"
                 })
        {
            using var response = await SendAsync(HttpMethod.Get, path);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var page = await ReadAsync<PagedResult<WebhookSubscriptionDto>>(response);
            Assert.Empty(page.Items);
            Assert.True(page.TotalCount > 0);
        }
    }

    [Fact]
    public async Task UnknownFailureCodesAndQueryParametersReturn400()
    {
        using (var known = await SendAsync(HttpMethod.Get, "/api/webhook-deliveries?failureCode=receiver_rejected&pageSize=1"))
        {
            Assert.Equal(HttpStatusCode.OK, known.StatusCode);
        }
        // Parameter names match without regard to case, as binding does.
        using (var cased = await SendAsync(HttpMethod.Get, "/api/webhook-deliveries?FailureCode=attempts_exhausted&PAGESIZE=1"))
        {
            Assert.Equal(HttpStatusCode.OK, cased.StatusCode);
        }
        foreach (var (code, expected) in new[]
                 {
                     ("receiver_rejeted", "Failure code 'receiver_rejeted' is not a delivery failure code"),
                     ("timeout", "attempts_exhausted, deadline_exceeded, retry_after_beyond_deadline"),
                     ("Receiver_Rejected", "is not a delivery failure code")
                 })
        {
            using var response = await SendAsync(HttpMethod.Get, $"/api/webhook-deliveries?failureCode={code}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(expected, await ErrorAsync(response), StringComparison.Ordinal);
        }

        var id = Guid.CreateVersion7();
        foreach (var (path, expected) in new[]
                 {
                     ("/api/webhook-event-types?verbose=true", "'verbose'"),
                     ("/api/webhook-subscriptions?state=active", "'state'"),
                     ($"/api/webhook-subscriptions/{id}?include=keys", "'include'"),
                     ($"/api/webhook-deliveries?subscription={id}", "'subscription'"),
                     ($"/api/webhook-deliveries/{id}?fields=body", "'fields'"),
                     ($"/api/webhook-deliveries/{id}/attempts?outcome=timeout", "'outcome'"),
                     ("/api/webhook-deliveries?state=failed&State=pending", "may appear only once"),
                     ("/api/webhook-deliveries?sort=occurredAt", "Sorting is fixed"),
                     ($"/api/webhook-deliveries/{id}/attempts?sort=attemptNumber", "Sorting is fixed"),
                     ($"/api/webhook-deliveries/{id}?sort=id", "'sort'")
                 })
        {
            using var response = await SendAsync(HttpMethod.Get, path);
            var text = await ErrorAsync(response);
            Assert.True(HttpStatusCode.BadRequest == response.StatusCode, $"{path}: {(int)response.StatusCode} {text}");
            Assert.Contains(expected, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task KeyProtectionOutageReturns503ForSecretRoutes()
    {
        var model = await CreateWorkflowAsync();
        var created = await CreateAsync(workflowKey: model);
        var id = created.Subscription.Id;
        try
        {
            var failed = Assert.Single(await InsertDeliveriesAsync(
                id, created.Subscription.Destination.RevisionId, model, WebhookDeliveryStates.Failed));
            var deliveryRevision = (await DeliveryAsync(failed)).Delivery.Revision;
            await using var factory = fixture.Factory.WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IWebhookSecretProtector>();
                    services.AddSingleton<IWebhookSecretProtector, UnavailableProtector>();
                }));
            using var client = factory.CreateClient();
            foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                     {
                         (HttpMethod.Post, "/api/webhook-subscriptions", CreateRequest()),
                         (HttpMethod.Post, $"/api/webhook-subscriptions/{id}/resume", new WebhookRevisionRequest(created.Subscription.Revision)),
                         (HttpMethod.Post, $"/api/webhook-subscriptions/{id}/test", null),
                         (HttpMethod.Post, $"/api/webhook-subscriptions/{id}/rotate-secret", new RotateWebhookSecretRequest(created.Subscription.Revision, null)),
                         (HttpMethod.Post, $"/api/webhook-subscriptions/{id}/deliveries/retry", AllDeliveries),
                         (HttpMethod.Post, $"/api/webhook-deliveries/{failed}/retry", new WebhookRevisionRequest(deliveryRevision))
                     })
            {
                using var request = ApiTestAuth.Authorize(new HttpRequestMessage(method, path) { Content = Json(body) });
                using var response = await client.SendAsync(request);
                var text = await ErrorAsync(response);
                Assert.True(HttpStatusCode.ServiceUnavailable == response.StatusCode, $"{method} {path}: {(int)response.StatusCode} {text}");
                Assert.Contains("certificate", text, StringComparison.Ordinal);
            }

            // Reads still work, and nothing changed: no activation, key, test event, or retry.
            using var read = ApiTestAuth.Authorize(new HttpRequestMessage(HttpMethod.Get, $"/api/webhook-subscriptions/{id}"));
            using var readResponse = await client.SendAsync(read);
            Assert.Equal(HttpStatusCode.OK, readResponse.StatusCode);
            var after = (await ReadAsync<WebhookSubscriptionDetailDto>(readResponse)).Subscription;
            Assert.Equal(created.Subscription.Revision, after.Revision);
            Assert.Equal(WebhookSubscriptionStates.Paused, after.State);
            Assert.Equal(created.KeyId, Assert.Single(after.SigningKeys).Id);
            Assert.Equal(deliveryRevision, (await DeliveryAsync(failed)).Delivery.Revision);
            Assert.Equal(0, await CountAsync(
                "SELECT count(*) FROM flowbit.webhook_deliveries WHERE \"SubscriptionId\" = @id AND \"IsTest\"", ("id", id)));
        }
        finally
        {
            await ArchiveQuietlyAsync(created.Subscription);
        }
    }

    [Fact]
    public async Task RotationIsNotCacheableAndBoundsItsOverlap()
    {
        var created = await CreateAsync();
        var id = created.Subscription.Id;
        try
        {
            foreach (var overlap in new[] { WebhookConstraints.MaxOverlapSeconds + 1, -1 })
            {
                using var invalid = await SendAsync(HttpMethod.Post, $"/api/webhook-subscriptions/{id}/rotate-secret",
                    new RotateWebhookSecretRequest(created.Subscription.Revision, overlap));
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                Assert.Contains("86400", await ErrorAsync(invalid), StringComparison.Ordinal);
            }

            // A null overlap means one hour.
            using var response = await SendAsync(HttpMethod.Post, $"/api/webhook-subscriptions/{id}/rotate-secret",
                new RotateWebhookSecretRequest(created.Subscription.Revision, null));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("no-store", response.Headers.CacheControl?.ToString(), StringComparison.Ordinal);
            Assert.Contains("no-cache", response.Headers.Pragma.ToString(), StringComparison.Ordinal);
            var rotated = await ReadAsync<WebhookSecretResultDto>(response);
            AssertOverlap(rotated, created.KeyId, WebhookConstraints.DefaultOverlapSeconds);

            // The maximum is accepted.
            var revoked = await PostAsync<WebhookSubscriptionDto>($"/api/webhook-subscriptions/{id}/revoke-previous-secret",
                new WebhookRevisionRequest(rotated.Subscription.Revision));
            var longest = await PostAsync<WebhookSecretResultDto>($"/api/webhook-subscriptions/{id}/rotate-secret",
                new RotateWebhookSecretRequest(revoked.Revision, WebhookConstraints.MaxOverlapSeconds));
            AssertOverlap(longest, rotated.KeyId, WebhookConstraints.MaxOverlapSeconds);
        }
        finally
        {
            await ArchiveQuietlyAsync(created.Subscription);
        }

        static void AssertOverlap(WebhookSecretResultDto result, Guid previousKeyId, int seconds)
        {
            var current = Assert.Single(result.Subscription.SigningKeys, key => key.Status == WebhookSigningKeyStatuses.Current);
            var previous = Assert.Single(result.Subscription.SigningKeys, key => key.Status == WebhookSigningKeyStatuses.Previous);
            Assert.Equal(result.KeyId, current.Id);
            Assert.Equal(previousKeyId, previous.Id);
            // One transaction stamps both, so the difference is exactly the overlap.
            Assert.Equal(TimeSpan.FromSeconds(seconds), previous.OverlapExpiresAt - current.CreatedAt);
        }
    }

    [Fact]
    public async Task TheNonArchivedSubscriptionCapIsAtomicAndIgnoresArchivedSubscriptions()
    {
        var cap = WebhookConstraints.MaxNonArchivedSubscriptions;
        var owned = new List<Guid>();
        try
        {
            const string nonArchived = "SELECT count(*) FROM flowbit.webhook_subscriptions WHERE \"State\" <> 'archived'";
            var existing = await CountAsync(nonArchived);
            Assert.True(existing < cap, $"{existing} non-archived subscriptions are left over from other tests.");
            // Archived subscriptions never count.
            for (var index = 0; index < 3; index++)
            {
                var (archivedId, _) = await WebhookTestDatabase.CreateSubscriptionAsync(
                    fixture.DataSource, [LifecycleEventTypes.InstanceStarted], null, state: "paused", name: "cap archived");
                owned.Add(archivedId);
                await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, archivedId, "archived");
            }
            for (var count = existing; count < cap - 1; count++)
            {
                var (fillerId, _) = await WebhookTestDatabase.CreateSubscriptionAsync(
                    fixture.DataSource, [LifecycleEventTypes.InstanceStarted], null, state: "paused", name: "cap filler");
                owned.Add(fillerId);
            }
            Assert.Equal(cap - 1, await CountAsync(nonArchived));

            // Two creations racing for the last place: exactly one wins.
            var race = await Task.WhenAll(
                SendAsync(HttpMethod.Post, "/api/webhook-subscriptions", CreateRequest()),
                SendAsync(HttpMethod.Post, "/api/webhook-subscriptions", CreateRequest()));
            try
            {
                Assert.Equal([HttpStatusCode.Created, HttpStatusCode.Conflict], race.Select(response => response.StatusCode).Order());
                owned.Add((await ReadAsync<WebhookSecretResultDto>(race.Single(response => response.StatusCode == HttpStatusCode.Created)))
                    .Subscription.Id);
                Assert.Contains($"At most {cap} non-archived",
                    await ErrorAsync(race.Single(response => response.StatusCode == HttpStatusCode.Conflict)),
                    StringComparison.Ordinal);
            }
            finally
            {
                foreach (var response in race)
                {
                    response.Dispose();
                }
            }
            Assert.Equal(cap, await CountAsync(nonArchived));
            using (var full = await SendAsync(HttpMethod.Post, "/api/webhook-subscriptions", CreateRequest()))
            {
                Assert.Equal(HttpStatusCode.Conflict, full.StatusCode);
            }

            // Archiving one frees its place.
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, owned[^1], "archived");
            using var freed = await SendAsync(HttpMethod.Post, "/api/webhook-subscriptions", CreateRequest());
            Assert.Equal(HttpStatusCode.Created, freed.StatusCode);
            owned.Add((await ReadAsync<WebhookSecretResultDto>(freed)).Subscription.Id);
            Assert.Equal(cap, await CountAsync(nonArchived));
        }
        finally
        {
            foreach (var id in owned)
            {
                await ArchiveQuietlyAsync(id);
            }
        }
    }

    [Fact]
    public async Task ArchiveRacingAnEditLetsExactlyOneWin()
    {
        var created = await CreateAsync();
        var id = created.Subscription.Id;
        var revision = created.Subscription.Revision;
        try
        {
            var responses = await Task.WhenAll(
                SendAsync(HttpMethod.Post, $"/api/webhook-subscriptions/{id}/archive", new WebhookRevisionRequest(revision)),
                SendAsync(HttpMethod.Put, $"/api/webhook-subscriptions/{id}", UpdateRequest(created.Subscription, revision, name: "Raced edit")));
            try
            {
                var archived = responses[0].StatusCode == HttpStatusCode.Accepted;
                var edited = responses[1].StatusCode == HttpStatusCode.OK;
                Assert.True(archived ^ edited, $"archive {(int)responses[0].StatusCode}, edit {(int)responses[1].StatusCode}");
                Assert.Equal(HttpStatusCode.Conflict, archived ? responses[1].StatusCode : responses[0].StatusCode);

                // The loser changed nothing: one revision step, and the winner's change only.
                var after = (await SubscriptionAsync(id)).Subscription;
                Assert.Equal(revision + 1, after.Revision);
                Assert.Equal(archived ? WebhookSubscriptionStates.Archived : WebhookSubscriptionStates.Paused, after.State);
                Assert.Equal(archived ? created.Subscription.Name : "Raced edit", after.Name);
                Assert.Equal(1, await CountAsync(
                    """
                    SELECT count(*) FROM flowbit.webhook_management_audit
                    WHERE "SubscriptionId" = @id AND "Operation" IN ('subscriptionArchived', 'subscriptionUpdated')
                    """,
                    ("id", id)));
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }
        }
        finally
        {
            await ArchiveQuietlyAsync(created.Subscription);
        }
    }

    [Fact]
    public async Task ConcurrentRotationsInstallExactlyOneKey()
    {
        var created = await CreateAsync();
        var id = created.Subscription.Id;
        var revision = created.Subscription.Revision;
        try
        {
            var responses = await Task.WhenAll(
                SendAsync(HttpMethod.Post, $"/api/webhook-subscriptions/{id}/rotate-secret", new RotateWebhookSecretRequest(revision, 3600)),
                SendAsync(HttpMethod.Post, $"/api/webhook-subscriptions/{id}/rotate-secret", new RotateWebhookSecretRequest(revision, 3600)));
            try
            {
                Assert.Equal([HttpStatusCode.OK, HttpStatusCode.Conflict], responses.Select(response => response.StatusCode).Order());
                var winner = await ReadAsync<WebhookSecretResultDto>(responses.Single(response => response.StatusCode == HttpStatusCode.OK));

                // Every key ever stored: the original, now previous, and the winner's only.
                var after = (await SubscriptionAsync(id)).Subscription;
                Assert.Equal(revision + 1, after.Revision);
                Assert.Equal(
                    [(winner.KeyId, WebhookSigningKeyStatuses.Current), (created.KeyId, WebhookSigningKeyStatuses.Previous)],
                    after.SigningKeys.Select(key => (key.Id, key.Status)).OrderBy(key => key.Status, StringComparer.Ordinal));
                Assert.Equal(2, await CountAsync(
                    "SELECT count(*) FROM flowbit.webhook_signing_keys WHERE \"SubscriptionId\" = @id", ("id", id)));
                Assert.Equal(1, await CountAsync(
                    "SELECT count(*) FROM flowbit.webhook_management_audit WHERE \"SubscriptionId\" = @id AND \"Operation\" = 'secretRotated'",
                    ("id", id)));
            }
            finally
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }
            }
        }
        finally
        {
            await ArchiveQuietlyAsync(created.Subscription);
        }
    }

    [Fact]
    public async Task ReceiversReadEventDetailsThroughTheCallbackRoutesWithAServiceIdentity()
    {
        const string receiverRole = "webhook-receiver";
        const string service = "svc-webhook-receiver";
        var model = UserTaskModel(receiverRole);
        var workflow = await CreateWorkflowAsync(model);
        var (subscriptionId, revisionId) = await WebhookTestDatabase.CreateSubscriptionAsync(
            fixture.DataSource, LifecycleEventTypes.Subscribable, model.Id);
        try
        {
            await WebhookTestDatabase.SetCaptureAsync(fixture.DataSource, true);
            await StartAsync(workflow.Id);
            using (var grant = await SendAsync(HttpMethod.Post, "/api/user-delegations",
                       new CreateUserDelegationRequest("callback-deputy", [model.Id],
                           DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1)),
                       user: "callback-delegator"))
            {
                Assert.Equal(HttpStatusCode.Created, grant.StatusCode);
            }
            await InsertIncidentAsync(workflow.Id, model.Id);

            var events = await WebhookTestDatabase.ReadEventsAsync(fixture.DataSource, revisionId);
            var instanceId = LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<InstanceLifecycleEventDataDto>>(
                Assert.Single(events, item => item.Type == LifecycleEventTypes.InstanceStarted).Body).InstanceId!.Value;
            var task = LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<TaskLifecycleEventDataDto>>(
                Assert.Single(events, item => item.Type == LifecycleEventTypes.TaskCreated).Body).Data;
            var delegationId = LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<DelegationGrantedEventDataDto>>(
                Assert.Single(events, item => item.Type == LifecycleEventTypes.DelegationGranted).Body).Data.DelegationId;
            var incidentId = LifecycleEventBodies.Deserialize<LifecycleEventEnvelopeDto<IncidentOpenedEventDataDto>>(
                Assert.Single(events, item => item.Type == LifecycleEventTypes.IncidentOpened).Body).Data.IncidentId;

            // A non-human identity holding only each route's documented role: the family's assignment-manager
            // role for instance, task, and node-execution reads, and the default admin role that
            // Delegation.AdminRoles and WorkflowJobs.RequiredRole require.
            var instance = await ReadAsServiceAsync<InstanceDetailDto>($"/api/instances/{instanceId}", receiverRole);
            Assert.Equal(instanceId, instance.Id);
            var tasks = await ReadAsServiceAsync<PagedResult<ManagedUserTaskDto>>(
                $"/api/user-tasks/manage?taskId={task.UserTaskId}", receiverRole);
            Assert.Equal(task.UserTaskId, Assert.Single(tasks.Items).UserTaskId);
            var execution = await ReadAsServiceAsync<NodeExecutionDetailDto>(
                $"/api/node-executions/{task.NodeExecutionId!.Value}", receiverRole);
            Assert.Equal(task.UserTaskId, execution.UserTaskId);
            var grants = await ReadAsServiceAsync<PagedResult<UserDelegationDto>>(
                $"/api/user-delegations/manage?delegationId={delegationId}", "admin");
            Assert.Equal(model.Id, Assert.Single(grants.Items).WorkflowKey);
            var incident = await ReadAsServiceAsync<IncidentDetailDto>($"/api/incidents/{incidentId}", "admin");
            Assert.Equal(incidentId, incident.Summary.Id);

            // The receiver's role still grants no webhook management.
            using var webhooks = await SendAsync(HttpMethod.Get, $"/api/webhook-deliveries?subscriptionId={subscriptionId}",
                user: service, roles: receiverRole, suppressAdmin: true);
            Assert.Equal(HttpStatusCode.Forbidden, webhooks.StatusCode);
        }
        finally
        {
            await ArchiveQuietlyAsync(subscriptionId);
            await WebhookTestDatabase.RestoreCaptureAsync(fixture.DataSource);
        }

        async Task<T> ReadAsServiceAsync<T>(string path, string role)
        {
            using var response = await SendAsync(HttpMethod.Get, path, user: service, roles: role, suppressAdmin: true);
            Assert.True(HttpStatusCode.OK == response.StatusCode,
                $"{path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
            return await ReadAsync<T>(response);
        }
    }

    [Fact]
    public async Task OpenApiDescribesWebhookIdsAsUuidsAndTheDelegationSearchByGrantId()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var provider = scope.ServiceProvider.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
        var document = await provider.GetOpenApiDocumentAsync();
        foreach (var (path, method, resource) in new[]
                 {
                     ("/api/webhook-subscriptions/{id}", HttpMethod.Get, "subscription"),
                     ("/api/webhook-subscriptions/{id}", HttpMethod.Put, "subscription"),
                     ("/api/webhook-subscriptions/{id}/archive", HttpMethod.Post, "subscription"),
                     ("/api/webhook-subscriptions/{id}/deliveries/discard", HttpMethod.Post, "subscription"),
                     ("/api/webhook-deliveries/{id}", HttpMethod.Get, "delivery"),
                     ("/api/webhook-deliveries/{id}/attempts", HttpMethod.Get, "delivery"),
                     ("/api/webhook-deliveries/{id}/discard", HttpMethod.Post, "delivery")
                 })
        {
            var parameter = Assert.Single(document.Paths[path].Operations![method].Parameters!, item => item.Name == "id");
            Assert.Equal($"The server-generated UUID of the webhook {resource}.", parameter.Description);
            Assert.Equal("uuid", parameter.Schema!.Format);
        }
        // Every webhook GET route documents the 400 for an unsupported query parameter.
        foreach (var path in new[]
                 {
                     "/api/webhook-event-types", "/api/webhook-subscriptions", "/api/webhook-subscriptions/{id}",
                     "/api/webhook-deliveries", "/api/webhook-deliveries/{id}", "/api/webhook-deliveries/{id}/attempts"
                 })
        {
            Assert.Contains("400", document.Paths[path].Operations![HttpMethod.Get].Responses!.Keys);
        }
        Assert.Equal(
            "The stable server-generated UUID of this webhook resource.",
            document.Components!.Schemas!["WebhookSubscriptionDto"].Properties!["id"].Description);

        var search = document.Paths["/api/user-delegations/manage"].Operations![HttpMethod.Get];
        Assert.Contains("by grant id", search.Description, StringComparison.Ordinal);
        Assert.Contains(search.Parameters!, parameter => parameter.Name == "delegationId");
    }

    [Fact]
    public async Task DelegationManagementSearchFiltersByDelegationId()
    {
        var key = await CreateWorkflowAsync();
        using var created = await SendAsync(HttpMethod.Post, "/api/user-delegations",
            new CreateUserDelegationRequest("deputy-" + Guid.NewGuid().ToString("N")[..8], [key],
                DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddDays(1)), user: "delegator");
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var grant = Assert.Single(await ReadAsync<List<UserDelegationDto>>(created));
        using var search = await SendAsync(HttpMethod.Get, $"/api/user-delegations/manage?delegationId={grant.Id}");
        var page = await ReadAsync<PagedResult<UserDelegationDto>>(search);
        Assert.Equal(grant.Id, Assert.Single(page.Items).Id);
        using var invalid = await SendAsync(HttpMethod.Get, "/api/user-delegations/manage?delegationId=0");
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
    }

    /// <summary>Every webhook management route, with a valid body for the given subscription and delivery.</summary>
    private static (HttpMethod Method, string Path, object? Body)[] AllRoutes(
        WebhookSubscriptionDto subscription,
        Guid deliveryId,
        long deliveryRevision)
    {
        var id = subscription.Id;
        var revision = new WebhookRevisionRequest(subscription.Revision);
        return
        [
            (HttpMethod.Get, "/api/webhook-event-types", null),
            (HttpMethod.Get, "/api/webhook-subscriptions", null),
            (HttpMethod.Post, "/api/webhook-subscriptions", CreateRequest()),
            (HttpMethod.Get, $"/api/webhook-subscriptions/{id}", null),
            (HttpMethod.Put, $"/api/webhook-subscriptions/{id}", UpdateRequest(subscription, subscription.Revision, name: "Route check")),
            (HttpMethod.Post, $"/api/webhook-subscriptions/{id}/pause", null),
            (HttpMethod.Post, $"/api/webhook-subscriptions/{id}/resume", revision),
            (HttpMethod.Post, $"/api/webhook-subscriptions/{id}/test", null),
            (HttpMethod.Post, $"/api/webhook-subscriptions/{id}/rotate-secret", new RotateWebhookSecretRequest(subscription.Revision, 0)),
            (HttpMethod.Post, $"/api/webhook-subscriptions/{id}/revoke-previous-secret", revision),
            (HttpMethod.Post, $"/api/webhook-subscriptions/{id}/archive", revision),
            (HttpMethod.Post, $"/api/webhook-subscriptions/{id}/deliveries/retry", AllDeliveries),
            (HttpMethod.Post, $"/api/webhook-subscriptions/{id}/deliveries/discard", AllDeliveries),
            (HttpMethod.Get, "/api/webhook-deliveries", null),
            (HttpMethod.Get, $"/api/webhook-deliveries/{deliveryId}", null),
            (HttpMethod.Get, $"/api/webhook-deliveries/{deliveryId}/attempts", null),
            (HttpMethod.Post, $"/api/webhook-deliveries/{deliveryId}/retry", new WebhookRevisionRequest(deliveryRevision)),
            (HttpMethod.Post, $"/api/webhook-deliveries/{deliveryId}/discard", new WebhookRevisionRequest(deliveryRevision))
        ];
    }

    /// <summary>
    /// Inserts deliveries of one subscription directly, each with its own event, a second apart unless told
    /// otherwise, and returns their ids oldest first. A delivering row holds a live lease.
    /// </summary>
    private async Task<Guid[]> InsertDeliveriesAsync(
        Guid subscriptionId,
        long revisionId,
        string workflowKey,
        string state,
        int count = 1,
        DateTimeOffset? firstOccurredAt = null,
        TimeSpan? spacing = null)
    {
        var gap = spacing ?? TimeSpan.FromSeconds(1);
        var first = (firstOccurredAt ?? DateTimeOffset.UtcNow.AddMinutes(-1) - (gap * count)).ToUniversalTime();
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            WITH recorded AS (
                SELECT occurred_at,
                       flowbit.record_lifecycle_event(
                           'instance.started', occurred_at, 'workflow_instances', 1, @key, NULL, 1, NULL,
                           '{"status":"running"}', '{}'::text[], ARRAY[@revision]::bigint[], now()) AS id
                FROM (SELECT @first + n * @gap AS occurred_at FROM generate_series(0, @count - 1) AS series(n)) times)
            INSERT INTO flowbit.webhook_deliveries
                ("Id", "EventId", "SubscriptionId", "RevisionId", "IsTest", "EventType", "WorkflowKey", "InstanceId",
                 "OccurredAt", "State", "DueAt", "RetryGeneration", "GenerationAttempts", "LifetimeAttempts",
                 "LeaseGeneration", "LeaseOwner", "LeaseToken", "LeaseExpiresAt", "LeasedFromState",
                 "FailureCode", "LastOutcome", "LastHttpStatus", "Revision", "CreatedAt", "UpdatedAt",
                 "GenerationStartedAt", "DeadlineAt")
            SELECT flowbit.uuid_v7(), recorded.id, @subscription, @revision, FALSE, 'instance.started', @key, 1,
                   recorded.occurred_at, @state, now(), 1, 1, 1, 1,
                   CASE WHEN @state = 'delivering' THEN 'webhook-api-tests' END,
                   CASE WHEN @state = 'delivering' THEN gen_random_uuid() END,
                   CASE WHEN @state = 'delivering' THEN now() + interval '1 hour' END,
                   CASE WHEN @state = 'delivering' THEN 'pending' END,
                   CASE WHEN @state = 'failed' THEN 'receiver_rejected' END,
                   CASE WHEN @state = 'failed' THEN 'receiver_rejected' END,
                   CASE WHEN @state = 'failed' THEN 400 END,
                   1, now(), now(), now(), now() + interval '72 hours'
            FROM recorded
            RETURNING "Id", "OccurredAt"
            """, connection);
        command.Parameters.AddWithValue("key", workflowKey);
        command.Parameters.AddWithValue("revision", revisionId);
        command.Parameters.AddWithValue("subscription", subscriptionId);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("count", count);
        command.Parameters.AddWithValue("first", first);
        command.Parameters.AddWithValue("gap", gap);
        var rows = new List<(Guid Id, DateTimeOffset OccurredAt)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetGuid(0), reader.GetFieldValue<DateTimeOffset>(1)));
        }
        Assert.Equal(count, rows.Count);
        return [.. rows.OrderBy(row => row.OccurredAt).Select(row => row.Id)];
    }

    /// <summary>Records an open incident for a timer-start job, as the Worker's incident paths do.</summary>
    private async Task InsertIncidentAsync(long workflowId, string workflowKey)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            WITH job AS (
                INSERT INTO flowbit.workflow_jobs
                    ("InstanceId", "WorkflowDefinitionId", "WorkflowKey", "ActivationId", "NodeId", "NodeName", "NodeType",
                     "Kind", "QueueClass", "Phase", "Status", "Priority", "AttemptCount", "MaxAttempts", "FailureHandling",
                     "RetryDelays", "DueAt", "LeaseGeneration", "CreatedAt", "UpdatedAt")
                VALUES (NULL, @definition, @key, gen_random_uuid(), 2, 'Review', 'userTask', 'timerStart', 'control',
                        'execute', 'incident', 0, 3, 3, 'boundaryFirst', '{}', now(), 0, now(), now())
                RETURNING "Id")
            INSERT INTO flowbit.workflow_incidents
                ("JobId", "OriginalJobId", "InstanceId", "WorkflowDefinitionId", "WorkflowKey", "NodeId", "NodeName",
                 "Type", "Status", "Summary", "CreatedAt", "UpdatedAt")
            SELECT job."Id", job."Id", NULL, @definition, @key, 2, 'Review', 'lease_exhausted', 'open', 'Lease exhausted', now(), now()
            FROM job
            """, connection);
        command.Parameters.AddWithValue("definition", workflowId);
        command.Parameters.AddWithValue("key", workflowKey);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private async Task<WebhookDeliveryDetailDto> DeliveryAsync(Guid id)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/webhook-deliveries/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<WebhookDeliveryDetailDto>(response);
    }

    private async Task<WebhookSubscriptionDetailDto> SubscriptionAsync(Guid id)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/webhook-subscriptions/{id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await ReadAsync<WebhookSubscriptionDetailDto>(response);
    }

    private Task<long> CountAsync(string sql, params (string Name, object Value)[] parameters) =>
        WebhookTestDatabase.CountEventsAsync(fixture.DataSource, sql, parameters);

    private async Task<string> CreateWorkflowAsync()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var model = new WorkflowModel
        {
            Id = "webhook-api-" + suffix,
            Name = "Webhook API " + suffix,
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows = [new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 }]
        };
        await CreateWorkflowAsync(model);
        return model.Id;
    }

    private async Task<WorkflowDetailDto> CreateWorkflowAsync(WorkflowModel model)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<WorkflowDetailDto>(response);
    }

    /// <summary>Start, one user task, and an end; the roles manage the family's tasks.</summary>
    private static WorkflowModel UserTaskModel(params string[] taskAssignmentRoles)
    {
        var suffix = Guid.NewGuid().ToString("N");
        return new WorkflowModel
        {
            Id = "webhook-api-task-" + suffix,
            Name = "Webhook API task " + suffix,
            InitialEventId = 1,
            TaskAssignmentRoles = [.. taskAssignmentRoles],
            FlowNodes =
            [
                new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
                new FlowNodeModel { Id = 2, Name = "Review", ExternalId = "REVIEW", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, Name = "Finish", SourceRef = 2, TargetRef = 3 }
            ]
        };
    }

    private async Task<InstanceDetailDto> StartAsync(long workflowId)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, null),
            roles: "User");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var instance = await ReadAsync<InstanceDetailDto>(response);
        Assert.Equal("running", instance.Status);
        return instance;
    }

    private async Task<WebhookSecretResultDto> CreateAsync(string? workflowKey = null)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/webhook-subscriptions", CreateRequest(workflowKey: workflowKey));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<WebhookSecretResultDto>(response);
    }

    private Task ArchiveQuietlyAsync(WebhookSubscriptionDto subscription) => ArchiveQuietlyAsync(subscription.Id);

    // Archives directly and settles what the Worker's archival coordinator would discard, so a coordinator run in
    // a later test never inherits this test's work. Keys stay as they are, like every directly archived row.
    private async Task ArchiveQuietlyAsync(Guid subscriptionId)
    {
        await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscriptionId, "archived");
        await WebhookTestDatabase.ExecuteAsync(fixture.DataSource, """
            UPDATE flowbit.webhook_deliveries
               SET "State" = 'discarded', "SettledAt" = now(), "LeaseOwner" = NULL, "LeaseToken" = NULL,
                   "LeaseExpiresAt" = NULL, "LeasedFromState" = NULL, "Revision" = "Revision" + 1, "UpdatedAt" = now()
             WHERE "SubscriptionId" = @id AND "State" IN ('pending', 'retryScheduled', 'delivering', 'failed');

            UPDATE flowbit.webhook_subscriptions SET "ArchiveSettledAt" = now()
             WHERE "Id" = @id AND "ArchiveSettledAt" IS NULL;
            """, ("id", subscriptionId));
    }

    private async Task<T> PostAsync<T>(string path, object? body, HttpStatusCode expected = HttpStatusCode.OK)
    {
        using var response = await SendAsync(HttpMethod.Post, path, body);
        Assert.True(expected == response.StatusCode, $"{path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return await ReadAsync<T>(response);
    }

    private static CreateWebhookSubscriptionRequest CreateRequest(
        string url = "https://receiver.example.com/flowbit/events?token=secret-token",
        IReadOnlyList<string>? eventTypes = null,
        string? workflowKey = null,
        int? maxConcurrency = null) =>
        new("API test " + Guid.NewGuid().ToString("N")[..8], "Created by tests", url,
            eventTypes ?? [LifecycleEventTypes.InstanceStarted, LifecycleEventTypes.TaskAssigned],
            workflowKey, maxConcurrency);

    private static UpdateWebhookSubscriptionRequest UpdateRequest(
        WebhookSubscriptionDto subscription,
        long expectedRevision,
        string? name = null,
        string? url = null) =>
        new(name ?? subscription.Name, subscription.Description, url ?? subscription.Destination.EndpointUrl,
            subscription.Destination.EventTypes, subscription.Destination.WorkflowKey, subscription.MaxConcurrency,
            expectedRevision);

    private static JsonContent? Json(object? body) =>
        body is null ? null : JsonContent.Create(body, options: JsonOptions);

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "test-admin",
        string roles = "admin",
        bool suppressAdmin = false)
    {
        var request = new HttpRequestMessage(method, path) { Content = Json(body) };
        ApiTestAuth.Authorize(request, user, roles.Split(','));
        if (suppressAdmin)
        {
            request.Headers.TryAddWithoutValidation("X-Test-Suppress-Admin", "true");
        }
        return await fixture.Client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("Response body was empty.");

    // The unescaped error text (the API's JSON encoder escapes quotes), or the raw body when it has none.
    private static async Task<string> ErrorAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        try
        {
            using var document = JsonDocument.Parse(text);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("error", out var error)
                && error.ValueKind == JsonValueKind.String
                    ? error.GetString()!
                    : text;
        }
        catch (JsonException)
        {
            return text;
        }
    }

    private sealed class UnavailableProtector : IWebhookSecretProtector
    {
        public bool IsAvailable => false;
        public string UnavailableReason => "Webhook key protection is not configured: set the certificate.";
        public byte[] Protect(byte[] secret) => throw new InvalidOperationException();
        public byte[] Unprotect(byte[] protectedSecret) => throw new InvalidOperationException();
    }
}
