using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// Signal start events (EVENT-04C): a family listens through its published default, a throw scoped to the family or
/// to all workflows freezes one start per listening family, and the Worker creates each instance from the default
/// that is published when it runs, or ends the delivery without one.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed partial class SignalStartRuntimeTests(PostgresApiFixture fixture) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => SignalDeliveryJobLeftovers.RetireAndAssertNoneAsync(fixture);

    // Subscriptions

    /// <summary>
    /// The family's rows follow its published default through every operation that changes it: saving a first
    /// version as published, a published version that is not the default, a default change that keeps the name (the
    /// row is updated in place), one that drops it, and the deletion of the default, which moves the rows to the
    /// promoted successor before the version is removed.
    /// </summary>
    [Fact]
    public async Task Subscriptions_FollowThePublishedDefault()
    {
        var signal = UniqueSignal();
        var other = UniqueSignal();
        var model = RecallModel(signal);
        var v1 = await CreateAsync(model);

        var first = Assert.Single(await StartRowsAsync(model.Id));
        Assert.Equal((signal, v1, 1, "Recall announced"),
            (first.SignalName, first.WorkflowDefinitionId, first.SignalNodeId, first.SignalNodeName));

        var renamed = RecallModel(signal, model.Id);
        renamed.FlowNodes[0].Name = "Recall published";
        var v2 = await NewVersionAsync(v1, renamed);
        Assert.Equal(v1, Assert.Single(await StartRowsAsync(model.Id)).WorkflowDefinitionId);

        await SetDefaultAsync(v2);
        var moved = Assert.Single(await StartRowsAsync(model.Id));
        Assert.Equal((first.Id, v2, "Recall published"), (moved.Id, moved.WorkflowDefinitionId, moved.SignalNodeName));

        var v3 = await NewVersionAsync(v2, RecallModel(other, model.Id));
        await SetDefaultAsync(v3);
        var replaced = Assert.Single(await StartRowsAsync(model.Id));
        Assert.Equal((other, v3), (replaced.SignalName, replaced.WorkflowDefinitionId));

        using (var delete = await SendAsync(HttpMethod.Delete, $"/api/workflows/{v3}"))
        {
            Assert.True(delete.IsSuccessStatusCode, await delete.Content.ReadAsStringAsync());
        }
        var restored = Assert.Single(await StartRowsAsync(model.Id));
        Assert.Equal((signal, v2), (restored.SignalName, restored.WorkflowDefinitionId));

        var draft = RecallModel(UniqueSignal(), model.Id);
        await NewVersionAsync(v2, draft, publish: false);
        Assert.Equal(v2, Assert.Single(await StartRowsAsync(model.Id)).WorkflowDefinitionId);
    }

    /// <summary>A draft first version listens once it is published, and not before.</summary>
    [Fact]
    public async Task PublishingTheDefault_StartsListening()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal);
        var id = await CreateAsync(model, publish: false);
        Assert.Empty(await StartRowsAsync(model.Id));

        using (var publish = await SendAsync(HttpMethod.Post, $"/api/workflows/{id}/publish"))
        {
            Assert.True(publish.IsSuccessStatusCode, await publish.Content.ReadAsStringAsync());
        }

        Assert.Equal(id, Assert.Single(await StartRowsAsync(model.Id)).WorkflowDefinitionId);

        // Deleting the family's only version leaves no published default, and no row.
        using (var delete = await SendAsync(HttpMethod.Delete, $"/api/workflows/{id}"))
        {
            Assert.True(delete.IsSuccessStatusCode, await delete.Content.ReadAsStringAsync());
        }
        Assert.Empty(await StartRowsAsync(model.Id));
    }

    // Starts

    /// <summary>
    /// A throw to the family freezes one start beside nothing else, and its delivery creates an instance as the actor
    /// signal: the payload's values become its start variables, with the default for a missing optional one; its
    /// history names the broadcast; its start event's visit completes as signalReceived;
    /// and the delivery names the instance it created.
    /// </summary>
    [Fact]
    public async Task FamilyThrow_StartsAnInstanceWithItsVariablesAndHistory()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal);
        var definitionId = await CreateAsync(model);

        var broadcast = await ThrowApiAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: Payload(new { recallNumber = "R-1", product = new { name = "Kettle" } })));

        Assert.Equal((1, 1, SignalBroadcastStatuses.Delivering), (broadcast.TotalDeliveryCount, broadcast.StartCount, broadcast.Status));
        var family = Assert.Single(broadcast.RecipientsByWorkflowKey);
        Assert.Equal((model.Id, 1, 1), (family.WorkflowKey, family.Count, family.StartCount));
        var pending = Assert.Single(await DeliveriesAsync(broadcast.Id));
        Assert.Equal((SignalDeliveryKinds.Start, (long?)null, definitionId, 1, (int?)null, false),
            (pending.Kind, pending.InstanceId, pending.WorkflowDefinitionId, pending.SignalNodeId, pending.AttachedToNodeId, pending.CancelActivity));

        await ProcessDeliveryJobsAsync(broadcast.Id);

        var delivery = Assert.Single(await DeliveriesAsync(broadcast.Id));
        Assert.Equal((SignalDeliveryStatuses.Delivered, SignalDeliveryResultCodes.Started), (delivery.Status, delivery.ResultCode));
        var instanceId = delivery.InstanceId!.Value;
        await using var db = fixture.CreateDbContext();
        var instance = await db.WorkflowInstances.AsNoTracking().SingleAsync(item => item.Id == instanceId);
        Assert.Equal((definitionId, "signal", WorkflowInstanceStatuses.Running), (instance.WorkflowDefinitionId, instance.StartedBy, instance.Status));
        var variables = await db.InstanceVariableCurrentValues.AsNoTracking()
            .Where(item => item.InstanceId == instanceId)
            .ToDictionaryAsync(item => item.VariableName, item => item.ValueJson.RootElement.GetString());
        Assert.Equal("R-1", variables["recallNumber"]);
        Assert.Equal("Kettle", variables["product"]);
        Assert.Equal("Not stated", variables["reason"]);
        Assert.Equal(new[] { 2 }, await ActiveTaskNodesAsync(instanceId));

        var received = await db.InstanceHistory.AsNoTracking()
            .SingleAsync(item => item.InstanceId == instanceId && item.Note == InstanceHistoryNotes.SignalReceived);
        Assert.Equal((1, 1, (long?)null), (received.FromStepId, received.ToStepId, received.ActionId));
        var payload = received.Payload!.RootElement;
        Assert.Equal(broadcast.Id, payload.GetProperty("broadcastId").GetInt64());
        Assert.Equal("start", payload.GetProperty("eventKind").GetString());
        Assert.Equal(delivery.Id, payload.GetProperty("deliveryId").GetInt64());
        Assert.Equal(SignalThrowerKinds.User, payload.GetProperty("thrownByKind").GetString());
        var left = await db.InstanceHistory.AsNoTracking()
            .SingleAsync(item => item.InstanceId == instanceId && item.Note == InstanceHistoryNotes.SignalStart);
        Assert.Equal((1, 2), (left.FromStepId, left.ToStepId));
        Assert.Equal(NodeExecutionCompletionReasons.SignalReceived, await db.NodeExecutions.AsNoTracking()
            .Where(item => item.InstanceId == instanceId && item.NodeId == 1)
            .Select(item => item.CompletionReason).SingleAsync());

        var finished = await BroadcastAsync(broadcast.Id);
        Assert.Equal((SignalBroadcastStatuses.Completed, 1), (finished.Status, finished.DeliveredCount));
    }

    /// <summary>
    /// A throw to all workflows starts each listening family once, beside the waits of the same name. A throw to one
    /// business key or one instance names existing instances, freezes no start, and its delivery list filters by kind.
    /// </summary>
    [Fact]
    public async Task AllWorkflowsThrow_StartsEveryListeningFamilyAndNarrowScopesStartNothing()
    {
        var signal = UniqueSignal();
        var first = RecallModel(signal);
        var second = RecallModel(signal);
        await CreateAsync(first);
        await CreateAsync(second);
        var waiter = WaiterModel(signal);
        var waiting = await StartAsync(await CreateAsync(waiter));

        var instanceScoped = await ThrowApiAsync(new ThrowSignalRequest(signal, InstanceId: waiting.Id));
        Assert.Equal((1, 0), (instanceScoped.TotalDeliveryCount, instanceScoped.StartCount));
        await ProcessDeliveryJobsAsync(instanceScoped.Id);

        var rewaiting = await StartAsync(await DefinitionIdAsync(waiter.Id));
        var all = await ThrowApiAsync(new ThrowSignalRequest(
            signal, AllWorkflows: true, Payload: Payload(new { recallNumber = "R-2", product = new { name = "Lamp" } })));
        Assert.Equal((3, 2), (all.TotalDeliveryCount, all.StartCount));
        Assert.Equal(2, (await DeliveriesAsync(all.Id, SignalDeliveryKinds.Start)).Count);
        Assert.Equal(rewaiting.Id, Assert.Single(await DeliveriesAsync(all.Id, SignalDeliveryKinds.Wait)).InstanceId);

        using (var badKind = await SendAsync(HttpMethod.Get, $"/api/signal-broadcasts/{all.Id}/deliveries?kind=both"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, badKind.StatusCode);
        }

        await ProcessDeliveryJobsAsync(all.Id);

        var deliveries = await DeliveriesAsync(all.Id);
        Assert.All(deliveries, delivery => Assert.Equal(SignalDeliveryStatuses.Delivered, delivery.Status));
        Assert.Equal(
            new[] { first.Id, second.Id }.Order(StringComparer.Ordinal),
            deliveries.Where(delivery => delivery.Kind == SignalDeliveryKinds.Start)
                .Select(delivery => delivery.WorkflowKey).Order(StringComparer.Ordinal));
    }

    /// <summary>
    /// A family with a business key starts one instance per key: a second broadcast with the same key ends as skipped,
    /// naming the owner and not the key, and affects no other delivery.
    /// </summary>
    [Fact]
    public async Task DuplicateBusinessKey_SkipsTheSecondStart()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal, businessKey: true);
        await CreateAsync(model);

        var first = await ThrowApiAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: Payload(new { recallNumber = " R-3 ", product = new { name = "Fan" } })));
        await ProcessDeliveryJobsAsync(first.Id);
        var owner = Assert.Single(await DeliveriesAsync(first.Id)).InstanceId!.Value;
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal("R-3", await db.WorkflowInstances.AsNoTracking()
                .Where(item => item.Id == owner).Select(item => item.BusinessKey).SingleAsync());
        }

        var second = await ThrowApiAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: Payload(new { recallNumber = "R-3", product = new { name = "Fan" } })));
        await ProcessDeliveryJobsAsync(second.Id);

        var skipped = Assert.Single(await DeliveriesAsync(second.Id));
        Assert.Equal((SignalDeliveryStatuses.Skipped, SignalDeliveryResultCodes.BusinessKeyConflict, (long?)null),
            (skipped.Status, skipped.ResultCode, skipped.InstanceId));
        Assert.Equal($"Instance #{owner} already has this start's business key.", skipped.ResultDescription);
        Assert.DoesNotContain("R-3", skipped.ResultDescription!, StringComparison.Ordinal);
        Assert.Equal(SignalBroadcastStatuses.Completed, (await BroadcastAsync(second.Id)).Status);
    }

    /// <summary>
    /// A payload that fails a required mapping fails the delivery and creates nothing. Retried once the family's
    /// default no longer starts on the name, the delivery ends as skipped, and the broadcast completes without a job.
    /// </summary>
    [Fact]
    public async Task PayloadFailingAMapping_FailsAndARetryAfterTheFamilyStoppedListeningSkips()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal);
        var v1 = await CreateAsync(model);

        var broadcast = await ThrowApiAsync(new ThrowSignalRequest(signal, model.Id, Payload: Payload(new { recallNumber = "R-4" })));
        await ProcessDeliveryJobsAsync(broadcast.Id);

        var failed = Assert.Single(await DeliveriesAsync(broadcast.Id));
        Assert.Equal((SignalDeliveryStatuses.Failed, SignalDeliveryResultCodes.PayloadInvalid, (long?)null),
            (failed.Status, failed.ResultCode, failed.InstanceId));
        await using (var db = fixture.CreateDbContext())
        {
            Assert.False(await db.WorkflowInstances.AnyAsync(item => item.WorkflowDefinitionId == v1));
        }

        var finished = await BroadcastAsync(broadcast.Id);
        Assert.Equal(SignalBroadcastStatuses.CompletedWithIssues, finished.Status);

        var v2 = await NewVersionAsync(v1, RecallModel(UniqueSignal(), model.Id));
        await SetDefaultAsync(v2);
        using (var retry = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{broadcast.Id}/retry-failed",
                   new RetrySignalDeliveriesRequest(finished.UpdatedAt)))
        {
            Assert.True(retry.IsSuccessStatusCode, await retry.Content.ReadAsStringAsync());
            var retried = (await retry.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
            Assert.Equal((SignalBroadcastStatuses.Completed, 1, 0), (retried.Status, retried.SkippedCount, retried.FailedCount));
        }

        var skipped = Assert.Single(await DeliveriesAsync(broadcast.Id));
        Assert.Equal((SignalDeliveryStatuses.Skipped, SignalDeliveryResultCodes.SubscriptionEnded), (skipped.Status, skipped.ResultCode));
    }

    /// <summary>
    /// A frozen start belongs to the family: when the default changes before the delivery runs, the delivery starts
    /// the new default when it still starts on the name, and is skipped when it does not.
    /// </summary>
    [Fact]
    public async Task DefaultChangedBeforeTheDelivery_StartsTheNewDefaultOrSkips()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal);
        var v1 = await CreateAsync(model);
        var started = await ThrowApiAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: Payload(new { recallNumber = "R-5", product = new { name = "Iron" } })));
        var renamed = RecallModel(signal, model.Id);
        renamed.FlowNodes[1].Name = "Write the new notice";
        var v2 = await NewVersionAsync(v1, renamed);
        await SetDefaultAsync(v2);

        await ProcessDeliveryJobsAsync(started.Id);

        var delivery = Assert.Single(await DeliveriesAsync(started.Id));
        Assert.Equal((SignalDeliveryStatuses.Delivered, v2), (delivery.Status, delivery.WorkflowDefinitionId));
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal(v2, await db.WorkflowInstances.AsNoTracking()
                .Where(item => item.Id == delivery.InstanceId).Select(item => item.WorkflowDefinitionId).SingleAsync());
        }

        var missed = await ThrowApiAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: Payload(new { recallNumber = "R-6", product = new { name = "Iron" } })));
        var v3 = await NewVersionAsync(v2, RecallModel(UniqueSignal(), model.Id));
        await SetDefaultAsync(v3);

        await ProcessDeliveryJobsAsync(missed.Id);

        var skipped = Assert.Single(await DeliveriesAsync(missed.Id));
        Assert.Equal((SignalDeliveryStatuses.Skipped, SignalDeliveryResultCodes.SubscriptionEnded, (long?)null),
            (skipped.Status, skipped.ResultCode, skipped.InstanceId));
    }

    /// <summary>The recipient limit counts starts, and a refused throw keeps nothing.</summary>
    [Fact]
    public async Task RecipientLimit_CountsStarts()
    {
        var signal = UniqueSignal();
        await CreateAsync(RecallModel(signal));
        await CreateAsync(RecallModel(signal));
        var previous = await SetEngineSettingAsync("Signals", "MaxSubscriptionsPerBroadcast", "1");
        try
        {
            using var response = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts",
                new ThrowSignalRequest(signal, AllWorkflows: true));

            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("would reach more than 1 recipients", await response.Content.ReadAsStringAsync());
            await using var db = fixture.CreateDbContext();
            Assert.False(await db.SignalBroadcasts.AnyAsync(item => item.SignalName == signal));
        }
        finally
        {
            await RestoreEngineSettingAsync("Signals", "MaxSubscriptionsPerBroadcast", previous);
        }
    }

    /// <summary>
    /// The subscription summary lists a family that only starts on a name with a wait count of 0 and a start count of
    /// 1, and a business-key or instance filter counts no start.
    /// </summary>
    [Fact]
    public async Task Summary_CountsStartsBesideWaits()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal);
        await CreateAsync(model);

        using (var response = await SendAsync(HttpMethod.Get, $"/api/signal-subscriptions/summary?name={signal}"))
        {
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var summary = (await response.Content.ReadFromJsonAsync<SignalSubscriptionSummaryDto>(JsonOptions))!;
            var row = Assert.Single(summary.Items);
            Assert.Equal((signal, model.Id, 0, 1), (row.Name, row.WorkflowKey, row.Count, row.StartCount));
        }

        using (var response = await SendAsync(HttpMethod.Get, $"/api/signal-subscriptions/summary?name={signal}&instanceId=1"))
        {
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var summary = (await response.Content.ReadFromJsonAsync<SignalSubscriptionSummaryDto>(JsonOptions))!;
            Assert.Empty(summary.Items);
        }
    }

    /// <summary>
    /// Two families that start each other, each on the signal the other throws, stop when the chain reaches the limit:
    /// each start continues the broadcast's count, and the throw that reaches the limit fails its start's delivery.
    /// A throw through the API starts a new chain.
    /// </summary>
    [Fact]
    public async Task LoopGuard_TwoFamiliesThatStartEachOtherStopAtTheLimit()
    {
        var ping = UniqueSignal();
        var pong = UniqueSignal();
        var pinger = EchoStartModel(ping, pong, $"signal-start-pong-{Guid.NewGuid():N}");
        var ponger = EchoStartModel(pong, ping, pinger.Id);
        pinger.FlowNodes.Single(node => node.Id == 2).Signal!.Scope!.WorkflowKey = ponger.Id;
        // Each throws to the other's family, which must exist to publish, so one starts as a draft.
        var pingerId = await CreateAsync(pinger, publish: false);
        await CreateAsync(ponger);
        using (var publish = await SendAsync(HttpMethod.Post, $"/api/workflows/{pingerId}/publish"))
        {
            Assert.True(publish.IsSuccessStatusCode, await publish.Content.ReadAsStringAsync());
        }
        var previous = await SetEngineSettingAsync("Workflow.Async", "MaxConsecutiveAutomaticActivations", "3");
        try
        {
            var first = await ThrowApiAsync(new ThrowSignalRequest(ping, pinger.Id));
            Assert.Equal(0, (await BroadcastEntityAsync(first.Id)).AutomaticActivationCount);

            for (var round = 0; round < 12; round++)
            {
                if (!await ProcessOpenStartDeliveriesAsync(ping, pong))
                {
                    break;
                }
            }

            await using var db = fixture.CreateDbContext();
            var broadcasts = await db.SignalBroadcasts.AsNoTracking()
                .Where(item => item.SignalName == ping || item.SignalName == pong)
                .OrderBy(item => item.Id)
                .ToListAsync();
            Assert.True(broadcasts.Count <= 4, $"The loop threw {broadcasts.Count} broadcasts.");
            Assert.Equal(
                broadcasts.Select(item => item.AutomaticActivationCount).Order().ToArray(),
                broadcasts.Select(item => item.AutomaticActivationCount).ToArray());
            var refused = await db.SignalDeliveries.AsNoTracking()
                .Where(item => broadcasts.Select(broadcast => broadcast.Id).Contains(item.BroadcastId)
                    && item.Status == SignalDeliveryStatuses.Failed)
                .SingleAsync();
            Assert.Equal(SignalDeliveryResultCodes.DeliveryRejected, refused.ResultCode);
            Assert.Contains(WorkflowAutomaticActivationGuard.SettingKey, refused.ResultDescription, StringComparison.Ordinal);
            Assert.Null(refused.InstanceId);
        }
        finally
        {
            await RestoreEngineSettingAsync("Workflow.Async", "MaxConsecutiveAutomaticActivations", previous);
        }
    }

    // Races

    /// <summary>
    /// A throw that has frozen a family's start holds its subscription row until it commits: a default change waits
    /// for it and then finds the row as the throw froze it, and the delivery starts the new default. A throw never
    /// waits for an instance.
    /// </summary>
    [Fact]
    public async Task Race_ADefaultChangeWaitsForAThrowInFlightAndTheStartRunsTheNewDefault()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal);
        var v1 = await CreateAsync(model);
        var renamed = RecallModel(signal, model.Id);
        renamed.FlowNodes[1].Name = "Write the newer notice";
        var v2 = await NewVersionAsync(v1, renamed);

        long broadcastId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);
            var written = await scope.ServiceProvider.GetRequiredService<SignalBroadcastWriter>().WriteAsync(
                new SignalBroadcastWrite(
                    new NewSignalBroadcastRecord(
                        signal,
                        SignalBroadcastScope.Family(model.Id),
                        JsonSerializer.Serialize(new { recallNumber = "R-7", product = new { name = "Clock" } }),
                        SignalThrowerKinds.User,
                        "race-test",
                        "[]",
                        null,
                        null,
                        null,
                        DateTimeOffset.UtcNow),
                    10,
                    "race-test"),
                CancellationToken.None);
            broadcastId = written.BroadcastId;
            Assert.Equal(1, written.StartCount);

            var change = SetDefaultAsync(v2);
            await Task.Delay(TimeSpan.FromSeconds(1));
            Assert.False(change.IsCompleted, "The default change did not wait for the throw that froze its start.");

            await transaction.CommitAsync(CancellationToken.None);
            await change;
        }

        Assert.Equal(v2, Assert.Single(await StartRowsAsync(model.Id)).WorkflowDefinitionId);
        await ProcessDeliveryJobsAsync(broadcastId);
        var delivery = Assert.Single(await DeliveriesAsync(broadcastId));
        Assert.Equal((SignalDeliveryStatuses.Delivered, v2), (delivery.Status, delivery.WorkflowDefinitionId));
    }

    /// <summary>
    /// A deletion of the default waits for a throw that froze its start, and then refuses with 409 because the start
    /// is pending, so no broadcast is left with a delivery that no job can run.
    /// </summary>
    [Fact]
    public async Task Race_ADeletionWaitsForAThrowInFlightAndThenRefuses()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal);
        var v1 = await CreateAsync(model);

        long broadcastId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            await using var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);
            var written = await scope.ServiceProvider.GetRequiredService<SignalBroadcastWriter>().WriteAsync(
                new SignalBroadcastWrite(
                    new NewSignalBroadcastRecord(
                        signal,
                        SignalBroadcastScope.Family(model.Id),
                        JsonSerializer.Serialize(new { recallNumber = "R-8", product = new { name = "Clock" } }),
                        SignalThrowerKinds.User,
                        "race-test",
                        "[]",
                        null,
                        null,
                        null,
                        DateTimeOffset.UtcNow),
                    10,
                    "race-test"),
                CancellationToken.None);
            broadcastId = written.BroadcastId;

            var deletion = SendAsync(HttpMethod.Delete, $"/api/workflows/{v1}");
            await Task.Delay(TimeSpan.FromSeconds(1));
            Assert.False(deletion.IsCompleted, "The deletion did not wait for the throw that froze its start.");

            await transaction.CommitAsync(CancellationToken.None);
            using var response = await deletion;
            Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        }

        await ProcessDeliveryJobsAsync(broadcastId);
        Assert.Equal(SignalDeliveryStatuses.Delivered, Assert.Single(await DeliveriesAsync(broadcastId)).Status);
    }

    // Cost

    /// <summary>
    /// A definition whose only signal event is a start runs no signal statement in an instance transition, and a throw
    /// scoped to one instance runs no statement on the start subscriptions.
    /// </summary>
    [Fact]
    public async Task Cost_AStartOnlyDefinitionRunsNoSignalStatementAndANarrowThrowReadsNoStart()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal);
        await CreateAsync(model);
        var broadcast = await ThrowApiAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: Payload(new { recallNumber = "R-9", product = new { name = "Desk" } })));
        await ProcessDeliveryJobsAsync(broadcast.Id);
        var instanceId = Assert.Single(await DeliveriesAsync(broadcast.Id)).InstanceId!.Value;

        fixture.CommandCounter.Reset(captureReaderCommandTexts: true);
        try
        {
            await TakeAsync(instanceId, 2, 201);
            var statements = fixture.CommandCounter.ReaderCommandTexts
                .Concat(fixture.CommandCounter.NonQueryCommandTexts)
                .ToArray();
            Assert.NotEmpty(statements);
            Assert.DoesNotContain(statements, text => text.Contains("signal_", StringComparison.Ordinal));
        }
        finally
        {
            fixture.CommandCounter.Reset();
        }

        var waiting = await StartAsync(await CreateAsync(WaiterModel(signal)));
        fixture.CommandCounter.Reset(captureReaderCommandTexts: true);
        try
        {
            var narrow = await ThrowApiAsync(new ThrowSignalRequest(signal, InstanceId: waiting.Id));
            Assert.Equal(0, narrow.StartCount);
            Assert.DoesNotContain(
                fixture.CommandCounter.ReaderCommandTexts.Concat(fixture.CommandCounter.NonQueryCommandTexts),
                text => text.Contains("signal_start_subscriptions", StringComparison.Ordinal));
            await ProcessDeliveryJobsAsync(narrow.Id);
        }
        finally
        {
            fixture.CommandCounter.Reset();
        }
    }

    // Models

    private static string UniqueSignal() => $"test.start-{Guid.NewGuid():N}";

    private static JsonElement Payload(object value) => JsonSerializer.SerializeToElement(value, JsonOptions);

    /// <summary>
    /// Signal start #1 on the signal with the required string mappings recallNumber and product (path product.name)
    /// and the optional reason (default 'Not stated'), task #2, and end #3, joined by flows 101 and 201.
    /// </summary>
    private static WorkflowModel RecallModel(string signal, string? key = null, bool businessKey = false) => new()
    {
        Id = key ?? $"signal-start-{Guid.NewGuid():N}",
        Name = "Recall notice",
        InitialEventId = null,
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 1, Name = "Recall announced", Type = BpmnFlowNodeTypes.SignalStartEvent,
                Signal = new SignalEventModel
                {
                    Name = signal,
                    OutputMappings =
                    [
                        Mapping("recallNumber", "recallNumber", required: true),
                        Mapping("product", "product.name", required: true),
                        Mapping("reason", "reason", required: false, defaultValue: "Not stated")
                    ]
                },
                BusinessKey = businessKey
                    ? new BusinessKeyModel { Variable = "recallNumber", Uniqueness = BusinessKeyUniqueness.All }
                    : null
            },
            new FlowNodeModel { Id = 2, Name = "Write the notice", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3, Name = "Done" }
        ]
    };

    /// <summary>Start #1, signal catch #2 on the signal, task #3, and end #4.</summary>
    private static WorkflowModel WaiterModel(string signal) => new()
    {
        Id = $"signal-start-waiter-{Guid.NewGuid():N}",
        Name = "Signal waiter",
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel
            {
                Id = 2, Name = "Wait", Type = BpmnFlowNodeTypes.IntermediateSignalCatchEvent,
                Signal = new SignalEventModel { Name = signal }
            },
            new FlowNodeModel { Id = 3, Name = "React", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel { Id = 4, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
            new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4, Name = "Close" }
        ]
    };

    /// <summary>Signal start #1 on the caught signal, then signal end #2 throwing the other signal to a named family.</summary>
    private static WorkflowModel EchoStartModel(string caught, string thrown, string targetKey) => new()
    {
        Id = $"signal-start-echo-{Guid.NewGuid():N}",
        Name = "Echo start",
        InitialEventId = null,
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 1, Name = "Started", Type = BpmnFlowNodeTypes.SignalStartEvent,
                Signal = new SignalEventModel { Name = caught }
            },
            new FlowNodeModel
            {
                Id = 2, Name = "Answer", Type = BpmnFlowNodeTypes.SignalEndEvent,
                Signal = new SignalEventModel
                {
                    Name = thrown,
                    Scope = new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.Workflow, WorkflowKey = targetKey },
                    Payload = []
                }
            }
        ],
        SequenceFlows = [new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 }]
    };

    private static MessageOutputMappingModel Mapping(string variable, string path, bool required, string? defaultValue = null) => new()
    {
        Variable = variable,
        Path = path,
        DataType = WorkflowVariableTypes.String,
        IsArray = false,
        Required = required,
        DefaultValue = defaultValue is null ? null : JsonSerializer.SerializeToElement(defaultValue)
    };

    // HTTP and database helpers

    private async Task<long> CreateAsync(WorkflowModel model, bool publish = true)
    {
        using var create = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, publish));
        Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        return (await create.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions))!.Id;
    }

    private async Task<long> NewVersionAsync(long id, WorkflowModel model, bool publish = true)
    {
        using var update = await SendAsync(HttpMethod.Put, $"/api/workflows/{id}", new UpdateWorkflowRequest(model, publish));
        Assert.True(update.IsSuccessStatusCode, await update.Content.ReadAsStringAsync());
        return (await update.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions))!.Id;
    }

    private async Task SetDefaultAsync(long id)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/api/workflows/{id}/set-default");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<long> DefinitionIdAsync(string workflowKey)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowDefinitions.Where(item => item.WorkflowKey == workflowKey && item.IsDefault)
            .Select(item => item.Id).SingleAsync();
    }

    private async Task<InstanceDetailDto> StartAsync(long workflowId)
    {
        using var start = await SendAsync(HttpMethod.Post, "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, null));
        Assert.True(start.StatusCode == HttpStatusCode.Created, await start.Content.ReadAsStringAsync());
        return (await start.Content.ReadFromJsonAsync<InstanceDetailDto>(JsonOptions))!;
    }

    private async Task<SignalBroadcastDetailDto> ThrowApiAsync(ThrowSignalRequest request)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts", request);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
    }

    private async Task<SignalBroadcastDetailDto> BroadcastAsync(long id)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/signal-broadcasts/{id}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
    }

    private async Task<SignalBroadcastEntity> BroadcastEntityAsync(long id)
    {
        await using var db = fixture.CreateDbContext();
        return await db.SignalBroadcasts.AsNoTracking().SingleAsync(item => item.Id == id);
    }

    private async Task<IReadOnlyList<SignalDeliveryDto>> DeliveriesAsync(long broadcastId, string? kind = null)
    {
        using var response = await SendAsync(HttpMethod.Get,
            $"/api/signal-broadcasts/{broadcastId}/deliveries?pageSize=200" + (kind is null ? "" : $"&kind={kind}"));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PagedResult<SignalDeliveryDto>>(JsonOptions))!.Items;
    }

    private async Task<List<SignalStartSubscriptionEntity>> StartRowsAsync(string workflowKey)
    {
        await using var db = fixture.CreateDbContext();
        return await db.SignalStartSubscriptions.AsNoTracking()
            .Where(item => item.WorkflowKey == workflowKey)
            .OrderBy(item => item.Id)
            .ToListAsync();
    }

    private async Task<int[]> ActiveTaskNodesAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.UserTasks.Where(task => task.InstanceId == instanceId && task.Status == UserTaskStatuses.Active)
            .Select(task => task.NodeId).OrderBy(id => id).ToArrayAsync();
    }

    private async Task TakeAsync(long instanceId, int nodeId, int flowId)
    {
        long taskId;
        await using (var db = fixture.CreateDbContext())
        {
            taskId = await db.UserTasks.Where(task => task.InstanceId == instanceId && task.NodeId == nodeId
                && task.Status == UserTaskStatuses.Active).Select(task => task.Id).FirstAsync();
        }

        using var action = await SendAsync(HttpMethod.Post, $"/api/user-tasks/{taskId}/flows/{flowId}", new TakeFlowRequest(null));
        Assert.True(action.StatusCode == HttpStatusCode.OK, await action.Content.ReadAsStringAsync());
    }

    private async Task<string?> SetEngineSettingAsync(string ns, string key, string value)
    {
        await using var db = fixture.CreateDbContext();
        var setting = await db.EngineSettings.SingleOrDefaultAsync(item => item.Namespace == ns && item.Key == key);
        var previous = setting?.Value;
        if (setting is null)
        {
            db.EngineSettings.Add(new EngineSettingEntity { Namespace = ns, Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
            setting.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
        return previous;
    }

    private async Task RestoreEngineSettingAsync(string ns, string key, string? previous)
    {
        await using var db = fixture.CreateDbContext();
        var setting = await db.EngineSettings.SingleAsync(item => item.Namespace == ns && item.Key == key);
        if (previous is null)
        {
            db.EngineSettings.Remove(setting);
        }
        else
        {
            setting.Value = previous;
            setting.UpdatedAt = DateTimeOffset.UtcNow;
        }

        await db.SaveChangesAsync();
    }

    /// <summary>Runs one round of the open delivery jobs of the broadcasts of either signal; false when none was open.</summary>
    private async Task<bool> ProcessOpenStartDeliveriesAsync(string first, string second)
    {
        List<long> open;
        await using (var db = fixture.CreateDbContext())
        {
            open = await db.SignalBroadcasts.AsNoTracking()
                .Where(item => (item.SignalName == first || item.SignalName == second)
                    && item.Status == SignalBroadcastStatuses.Delivering)
                .Select(item => item.Id)
                .ToListAsync();
        }

        foreach (var id in open)
        {
            await ProcessDeliveryJobsAsync(id);
        }

        return open.Count > 0;
    }

    private async Task ProcessDeliveryJobsAsync(long broadcastId, IServiceProvider? services = null)
    {
        for (var round = 0; round < 20; round++)
        {
            await using (var db = fixture.CreateDbContext())
            {
                var open = await db.SignalBroadcastJobs.AsNoTracking()
                    .Where(link => link.BroadcastId == broadcastId && link.JobId != null)
                    .Join(db.WorkflowJobs, link => link.JobId, job => job.Id, (_, job) => job)
                    .Where(job => job.Status == WorkflowJobStatuses.Queued || job.Status == WorkflowJobStatuses.Retry)
                    .Select(job => job.Id)
                    .ToListAsync();
                if (open.Count == 0)
                {
                    return;
                }

                foreach (var job in await db.WorkflowJobs.Where(job => open.Contains(job.Id)).ToListAsync())
                {
                    job.Priority = 1_000_000;
                    job.DueAt = DateTimeOffset.UtcNow.AddMinutes(-1);
                }
                await db.SaveChangesAsync();
            }

            await LeaseAndProcessOneAsync(services);
        }

        Assert.Fail($"The delivery jobs of signal broadcast #{broadcastId} did not finish.");
    }

    private async Task LeaseAndProcessOneAsync(IServiceProvider? services = null)
    {
        await using var scope = (services ?? fixture.Factory.Services).CreateAsyncScope();
        var leases = await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().LeaseRunnableAsync(
            new WorkflowJobLeaseRequest($"signal-start-test-{Guid.NewGuid():N}", 1, 1, 1, TimeSpan.FromMinutes(2)),
            CancellationToken.None);
        foreach (var lease in leases)
        {
            await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>()
                .ProcessAsync(lease, CancellationToken.None);
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, "signal-start", ["admin"]);
        return await fixture.Client.SendAsync(request);
    }
}
