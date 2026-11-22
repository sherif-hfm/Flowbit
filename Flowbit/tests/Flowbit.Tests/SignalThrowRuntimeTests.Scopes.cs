using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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
/// What each scope of a throw from a workflow reaches, what its payload holds, who may see
/// and manage its broadcast, and which budget it counts against.
/// </summary>
public sealed partial class SignalThrowRuntimeTests
{
    // Scopes

    /// <summary>
    /// <c>thisWorkflow</c> names the instance's family at run time, so it reaches the waits of
    /// every version of the family, the thrower's own included.
    /// </summary>
    [Fact]
    public async Task ThisWorkflow_ReachesTheWaitsOfEveryVersionOfTheFamily()
    {
        var signal = UniqueSignal();
        var model = OrderModel(signal);
        model.FlowNodes.Single(node => node.Id == 8).Signal!.Scope = new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.ThisWorkflow };
        var versionOne = await CreateAsync(model);
        var onVersionOne = await StartAsync(versionOne);
        model.Name = "Signal order, version two";
        long versionTwo;
        using (var update = await SendAsync(HttpMethod.Put, $"/api/workflows/{versionOne}", new UpdateWorkflowRequest(model, true)))
        {
            Assert.True(update.IsSuccessStatusCode, await update.Content.ReadAsStringAsync());
            versionTwo = (await update.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions))!.Id;
        }

        Assert.NotEqual(versionOne, versionTwo);
        var onVersionTwo = await StartAsync(versionTwo);
        var thrower = await StartAsync(versionTwo);

        await TakeAsync(thrower.Id, 7, 701);

        var broadcast = Assert.Single(await BroadcastsThrownByAsync(thrower.Id));
        Assert.Equal((model.Id, 3), (broadcast.WorkflowKey, broadcast.TotalDeliveryCount));
        await ProcessDeliveryJobsAsync(broadcast.Id);
        foreach (var instance in new[] { onVersionOne, onVersionTwo, thrower })
        {
            Assert.Contains(5, await ActiveTaskNodesAsync(instance.Id));
        }
    }

    /// <summary><c>workflow</c> with the thrower's own key reaches its family; with another key, only that family.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Workflow_ReachesOnlyTheNamedFamily(bool ownKey)
    {
        var signal = UniqueSignal();
        var receiver = ReceiverModel(signal);
        var waiting = await StartAsync(await CreateAsync(receiver), OrderNumber("ORD-W"));
        var model = OrderModel(signal);
        model.FlowNodes.Single(node => node.Id == 8).Signal!.Scope = new SignalThrowScopeModel
        {
            Kind = SignalThrowScopeKinds.Workflow,
            WorkflowKey = ownKey ? model.Id : receiver.Id
        };
        var thrower = await StartAsync(await CreateAsync(model));

        await TakeAsync(thrower.Id, 7, 701);

        var broadcast = Assert.Single(await BroadcastsThrownByAsync(thrower.Id));
        Assert.Equal(ownKey ? model.Id : receiver.Id, broadcast.WorkflowKey);
        Assert.Equal(1, broadcast.TotalDeliveryCount);
        await ProcessDeliveryJobsAsync(broadcast.Id);
        Assert.Equal(ownKey ? new[] { 5 } : new[] { 3 }, await ActiveTaskNodesAsync(thrower.Id));
        Assert.Equal(ownKey ? Array.Empty<int>() : new[] { 3 }, await ActiveTaskNodesAsync(waiting.Id));
    }

    /// <summary><c>ownBusinessKey</c> reaches the instance with the thrower's own business key in the named family.</summary>
    [Fact]
    public async Task OwnBusinessKey_ReachesTheInstanceWithTheThrowersKeyInTheNamedFamily()
    {
        var signal = UniqueSignal();
        var receiver = ReceiverModel(signal);
        var receiverId = await CreateAsync(receiver);
        var same = await StartAsync(receiverId, OrderNumber("ORD-OWN-1"));
        var other = await StartAsync(receiverId, OrderNumber("ORD-OWN-2"));
        var thrower = EndThrowerModel(signal, new SignalThrowScopeModel
        {
            Kind = SignalThrowScopeKinds.OwnBusinessKey,
            WorkflowKey = receiver.Id
        });
        thrower.FlowNodes.Single(node => node.Id == 1).BusinessKey =
            new BusinessKeyModel { Variable = "orderNumber", Uniqueness = BusinessKeyUniqueness.Active };
        var decision = await StartAsync(await CreateAsync(thrower), OrderNumber("ORD-OWN-1"));

        await TakeAsync(decision.Id, 2, 201);

        var broadcast = Assert.Single(await BroadcastsThrownByAsync(decision.Id));
        Assert.Equal((receiver.Id, "ORD-OWN-1", 1), (broadcast.WorkflowKey, broadcast.BusinessKey, broadcast.TotalDeliveryCount));
        await ProcessDeliveryJobsAsync(broadcast.Id);
        Assert.Equal(new[] { 3 }, await ActiveTaskNodesAsync(same.Id));
        Assert.Empty(await ActiveTaskNodesAsync(other.Id));
    }

    /// <summary><c>allWorkflows</c> reaches the waits of every family.</summary>
    [Fact]
    public async Task AllWorkflows_ReachesTheWaitsOfEveryFamily()
    {
        var signal = UniqueSignal();
        var first = await StartAsync(await CreateAsync(ReceiverModel(signal)), OrderNumber("ORD-ALL-1"));
        var second = await StartAsync(await CreateAsync(ReceiverModel(signal)), OrderNumber("ORD-ALL-2"));
        var thrower = EndThrowerModel(signal, new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows });
        var decision = await StartAsync(await CreateAsync(thrower), OrderNumber("ORD-ALL"));

        await TakeAsync(decision.Id, 2, 201);

        var broadcast = Assert.Single(await BroadcastsThrownByAsync(decision.Id));
        Assert.Null(broadcast.WorkflowKey);
        Assert.Equal(2, broadcast.TotalDeliveryCount);
        await ProcessDeliveryJobsAsync(broadcast.Id);
        Assert.Equal(new[] { 3 }, await ActiveTaskNodesAsync(first.Id));
        Assert.Equal(new[] { 3 }, await ActiveTaskNodesAsync(second.Id));
    }

    /// <summary>
    /// The engine looks no family up: a throw to a family without business keys, or to one
    /// whose last definition was deleted after publication, completes with no delivery.
    /// </summary>
    [Fact]
    public async Task AFamilyWithoutBusinessKeysOrWithoutADefinitionCompletesWithNoDelivery()
    {
        var signal = UniqueSignal();
        var plain = ReceiverModel(signal);
        plain.FlowNodes.Single(node => node.Id == 1).BusinessKey = null;
        await StartAsync(await CreateAsync(plain), OrderNumber("ORD-PLAIN"));
        var byKey = EndThrowerModel(signal, new SignalThrowScopeModel
        {
            Kind = SignalThrowScopeKinds.BusinessKey,
            WorkflowKey = plain.Id,
            BusinessKeyVariable = "orderNumber"
        });
        var keyed = await StartAsync(await CreateAsync(byKey), OrderNumber("ORD-PLAIN"));

        var gone = ReceiverModel(signal);
        var goneDraft = await CreateDraftAsync(gone);
        var toGone = EndThrowerModel(signal, new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.Workflow, WorkflowKey = gone.Id });
        var toGoneId = await CreateAsync(toGone);
        using (var delete = await SendAsync(HttpMethod.Delete, $"/api/workflows/{goneDraft}"))
        {
            Assert.True(delete.IsSuccessStatusCode, await delete.Content.ReadAsStringAsync());
        }

        var orphan = await StartAsync(toGoneId, OrderNumber("ORD-GONE"));

        await TakeAsync(keyed.Id, 2, 201);
        await TakeAsync(orphan.Id, 2, 201);

        foreach (var instance in new[] { keyed, orphan })
        {
            var broadcast = Assert.Single(await BroadcastsThrownByAsync(instance.Id));
            Assert.Equal((0, SignalBroadcastStatuses.Completed), (broadcast.TotalDeliveryCount, broadcast.Status));
        }
    }

    /// <summary>
    /// A named family without a definition refuses set-default as it refuses publication, and
    /// the assistant reports it as a publication blocker, never a save blocker.
    /// </summary>
    [Fact]
    public async Task AMissingFamilyBlocksSetDefaultAndIsOnlyAPublicationBlockerForTheAssistant()
    {
        var thrower = EndThrowerModel(UniqueSignal(), new SignalThrowScopeModel
        {
            Kind = SignalThrowScopeKinds.Workflow,
            WorkflowKey = $"missing-family-{Guid.NewGuid():N}"
        });
        var draft = await CreateDraftAsync(thrower);
        var expected = $"Signal end event #3 throws to workflow '{thrower.Signal3Key()}', which has no definition.";

        using (var setDefault = await SendAsync(HttpMethod.Post, $"/api/workflows/{draft}/set-default"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, setDefault.StatusCode);
            Assert.Contains(expected, await setDefault.Content.ReadAsStringAsync());
        }

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var assistant = scope.ServiceProvider.GetRequiredService<Flowbit.Service.Ai.IWorkflowAiAuthoringService>();
        var result = await assistant.ValidateAsync(
            JsonSerializer.SerializeToElement(thrower, JsonOptions), CancellationToken.None);
        Assert.True(result.CanSave, string.Join(" ", result.SaveBlockers));
        Assert.False(result.CanPublish);
        Assert.Contains(expected, result.PublicationBlockers);
    }

    // Payload

    /// <summary>
    /// Entries are copied whole and in order: arrays and objects as they are stored, the
    /// instance's ID and business key from the instance row, and an optional entry without a
    /// value left out. A list whose only entries have no value sends <c>{}</c>.
    /// </summary>
    [Fact]
    public async Task Payload_CopiesWholeValuesAndBothSourcesAndLeavesOutAnOptionalEntryWithoutAValue()
    {
        var thrower = EndThrowerModel(UniqueSignal(), new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows });
        thrower.FlowNodes.Single(node => node.Id == 1).BusinessKey =
            new BusinessKeyModel { Variable = "orderNumber", Uniqueness = BusinessKeyUniqueness.Active };
        thrower.FlowNodes.Single(node => node.Id == 1).Variables.Add(
            new VariableModel { Id = 12, Name = "note", DataType = WorkflowVariableTypes.String });
        thrower.Variables =
        [
            new VariableModel
            {
                Id = 1, Name = "tags", DataType = WorkflowVariableTypes.String, IsArray = true,
                DefaultValue = JsonSerializer.SerializeToElement(new[] { "a", "b" })
            },
            new VariableModel
            {
                Id = 2, Name = "details", DataType = WorkflowVariableTypes.Json,
                DefaultValue = JsonSerializer.SerializeToElement(new { k = 1, nested = new[] { true } })
            },
            new VariableModel { Id = 3, Name = "nothing", DataType = WorkflowVariableTypes.String, Nullable = true }
        ];
        thrower.FlowNodes.Single(node => node.Id == 3).Signal!.Payload =
        [
            new SignalPayloadEntryModel { Name = "tags", Variable = "tags", Required = true },
            new SignalPayloadEntryModel { Name = "details", Variable = "details" },
            new SignalPayloadEntryModel { Name = "note", Variable = "note" },
            // An optional entry whose variable holds null sends null; only a missing value is left out.
            new SignalPayloadEntryModel { Name = "nothing", Variable = "nothing" },
            new SignalPayloadEntryModel { Name = "id", Source = SignalPayloadSources.InstanceId },
            new SignalPayloadEntryModel { Name = "key", Source = SignalPayloadSources.BusinessKey, Required = true }
        ];
        var decision = await StartAsync(await CreateAsync(thrower), OrderNumber("ORD-PAYLOAD"));

        await TakeAsync(decision.Id, 2, 201);

        var broadcast = Assert.Single(await BroadcastsThrownByAsync(decision.Id));
        // jsonb keeps its own key order, so the object is compared by value.
        Assert.True(System.Text.Json.Nodes.JsonNode.DeepEquals(
            System.Text.Json.Nodes.JsonNode.Parse(
                $$"""{"tags":["a","b"],"details":{"k":1,"nested":[true]},"nothing":null,"id":{{decision.Id}},"key":"ORD-PAYLOAD"}"""),
            System.Text.Json.Nodes.JsonNode.Parse(broadcast.PayloadJson!)), broadcast.PayloadJson);

        var empty = EndThrowerModel(UniqueSignal(), new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows });
        empty.FlowNodes.Single(node => node.Id == 1).Variables.Add(
            new VariableModel { Id = 12, Name = "note", DataType = WorkflowVariableTypes.String });
        empty.FlowNodes.Single(node => node.Id == 3).Signal!.Payload =
            [new SignalPayloadEntryModel { Name = "note", Variable = "note" }];
        var unset = await StartAsync(await CreateAsync(empty), OrderNumber("ORD-EMPTY"));
        await TakeAsync(unset.Id, 2, 201);
        Assert.Equal("{}", Compact(Assert.Single(await BroadcastsThrownByAsync(unset.Id)).PayloadJson));
    }

    /// <summary>A broadcast outlives its thrower: cancelling the instance after the commit leaves it delivering.</summary>
    [Fact]
    public async Task CancellingTheThrowerAfterTheCommitLeavesItsBroadcastDelivering()
    {
        var signal = UniqueSignal();
        var receiver = ReceiverModel(signal);
        var waiting = await StartAsync(await CreateAsync(receiver), OrderNumber("ORD-OUTLIVE"));
        var model = OrderModel(signal);
        model.FlowNodes.Single(node => node.Id == 8).Signal!.Scope =
            new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.Workflow, WorkflowKey = receiver.Id };
        var thrower = await StartAsync(await CreateAsync(model));
        await TakeAsync(thrower.Id, 7, 701);

        using (var cancel = await SendAsync(HttpMethod.Post, $"/api/instances/{thrower.Id}/cancel"))
        {
            Assert.True(cancel.IsSuccessStatusCode, await cancel.Content.ReadAsStringAsync());
        }

        var broadcast = Assert.Single(await BroadcastsThrownByAsync(thrower.Id));
        Assert.Equal(SignalBroadcastStatuses.Delivering, broadcast.Status);
        await ProcessDeliveryJobsAsync(broadcast.Id);
        Assert.Equal(SignalBroadcastStatuses.Completed, (await BroadcastAsync(broadcast.Id)).Status);
        Assert.Equal(new[] { 3 }, await ActiveTaskNodesAsync(waiting.Id));
    }

    // Visibility and management

    /// <summary>A managed client sees only the broadcasts it threw, so one thrown by an instance is hidden.</summary>
    [Fact]
    public async Task ManagedClient_CannotSeeABroadcastThrownByAnInstance()
    {
        var signal = UniqueSignal();
        var model = OrderModel(signal);
        var order = await StartAsync(await CreateAsync(model));
        await TakeAsync(order.Id, 7, 701);
        var broadcast = Assert.Single(await BroadcastsThrownByAsync(order.Id));
        var clientId = $"signal-viewer-{Guid.NewGuid():N}"[..40];
        string secret;
        using (var create = await SendAsync(HttpMethod.Post, "/api/shared-variable-clients",
                   new CreateSharedVariableClientRequest(clientId, "Signal viewer", [SharedVariableClientScopes.SignalsRead])))
        {
            Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
            secret = (await create.Content.ReadFromJsonAsync<CreateSharedVariableClientResult>(JsonOptions))!.ClientSecret;
        }

        async Task<HttpResponseMessage> AsClientAsync(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Add("X-Client-Id", clientId);
            request.Headers.Add("X-Client-Secret", secret);
            return await fixture.Client.SendAsync(request);
        }

        using (var detail = await AsClientAsync($"/api/signal-broadcasts/{broadcast.Id}"))
        {
            Assert.Equal(HttpStatusCode.NotFound, detail.StatusCode);
        }

        using (var list = await AsClientAsync($"/api/signal-broadcasts?name={signal}&thrownByKind=instance"))
        {
            Assert.True(list.IsSuccessStatusCode, await list.Content.ReadAsStringAsync());
            Assert.Empty((await list.Content.ReadFromJsonAsync<PagedResult<SignalBroadcastSummaryDto>>(JsonOptions))!.Items);
        }

        await ProcessDeliveryJobsAsync(broadcast.Id);
    }

    /// <summary>A user with the signal role cancels and retries a broadcast thrown by an instance as any other.</summary>
    [Fact]
    public async Task AUserCancelsAndRetriesABroadcastThrownByAnInstance()
    {
        var signal = UniqueSignal();
        var receiver = ReceiverModel(signal);
        var receiverId = await CreateAsync(receiver);
        await StartAsync(receiverId, OrderNumber("ORD-CANCEL"));
        var thrower = EndThrowerModel(signal, new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.Workflow, WorkflowKey = receiver.Id });
        var throwerId = await CreateAsync(thrower);
        var first = await StartAsync(throwerId, OrderNumber("ORD-T1"));
        await TakeAsync(first.Id, 2, 201);
        var cancelled = Assert.Single(await BroadcastsThrownByAsync(first.Id));
        using (var cancel = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{cancelled.Id}/cancel",
                   new CancelSignalBroadcastRequest("Not needed")))
        {
            Assert.True(cancel.IsSuccessStatusCode, await cancel.Content.ReadAsStringAsync());
        }

        Assert.Equal(SignalBroadcastStatuses.Cancelled, (await BroadcastAsync(cancelled.Id)).Status);
        await AssertGaugeMatchesPendingAsync();
        // A cancelled broadcast's delivery job stays queued until a Worker finds nothing to do.
        await ProcessDeliveryJobsAsync(cancelled.Id);
        await using (var db = fixture.CreateDbContext())
        {
            Assert.All(await db.SignalDeliveries.AsNoTracking().Where(item => item.BroadcastId == cancelled.Id).ToListAsync(),
                delivery => Assert.Equal(SignalDeliveryStatuses.Cancelled, delivery.Status));
        }

        // A required mapping the payload does not satisfy fails the delivery, which can then be retried.
        var strict = ReceiverModel(UniqueSignal());
        strict.FlowNodes.Single(node => node.Id == 2).Signal!.OutputMappings =
        [
            new MessageOutputMappingModel
            {
                Variable = "reason", Path = "reason", DataType = WorkflowVariableTypes.String, IsArray = false, Required = true
            }
        ];
        strict.Variables = [new VariableModel { Id = 1, Name = "reason", DataType = WorkflowVariableTypes.String, Nullable = true }];
        await StartAsync(await CreateAsync(strict), OrderNumber("ORD-STRICT"));
        var failing = EndThrowerModel(strict.FlowNodes.Single(node => node.Id == 2).Signal!.Name,
            new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.Workflow, WorkflowKey = strict.Id });
        var second = await StartAsync(await CreateAsync(failing), OrderNumber("ORD-T2"));
        await TakeAsync(second.Id, 2, 201);
        var retried = Assert.Single(await BroadcastsThrownByAsync(second.Id));
        await ProcessDeliveryJobsAsync(retried.Id);
        var finished = await BroadcastAsync(retried.Id);
        Assert.Equal(SignalBroadcastStatuses.CompletedWithIssues, finished.Status);
        using (var retry = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{retried.Id}/retry-failed",
                   new RetrySignalDeliveriesRequest(finished.UpdatedAt)))
        {
            Assert.True(retry.IsSuccessStatusCode, await retry.Content.ReadAsStringAsync());
        }

        Assert.Equal(SignalBroadcastStatuses.Delivering, (await BroadcastAsync(retried.Id)).Status);
        await AssertGaugeMatchesPendingAsync();
        await ProcessDeliveryJobsAsync(retried.Id);
        await AssertGaugeMatchesPendingAsync();
    }

    // Budgets

    /// <summary>
    /// The workflow budget counts only broadcasts thrown by workflows: an open API broadcast
    /// does not use it up, and a throw that reaches nobody finishes at once and counts for nothing.
    /// </summary>
    [Fact]
    public async Task WorkflowBudget_CountsOnlyOpenBroadcastsThrownByWorkflows()
    {
        var signal = UniqueSignal();
        var receiver = ReceiverModel(signal);
        await StartAsync(await CreateAsync(receiver), OrderNumber("ORD-BUDGET"));
        var api = await ThrowApiAsync(new ThrowSignalRequest(signal, receiver.Id));
        Assert.Equal(SignalBroadcastStatuses.Delivering, api.Status);
        var nowhere = EndThrowerModel(UniqueSignal(), new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows });
        var nowhereId = await CreateAsync(nowhere);
        var toReceiver = EndThrowerModel(signal, new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.Workflow, WorkflowKey = receiver.Id });
        var toReceiverId = await CreateAsync(toReceiver);
        var empty = await StartAsync(nowhereId, OrderNumber("ORD-N1"));
        var counted = await StartAsync(toReceiverId, OrderNumber("ORD-C1"));
        var refused = await StartAsync(toReceiverId, OrderNumber("ORD-C2"));

        int open;
        await using (var db = fixture.CreateDbContext())
        {
            open = await db.SignalBroadcasts.CountAsync(item =>
                item.Status == SignalBroadcastStatuses.Delivering && item.ThrownByKind == SignalThrowerKinds.Instance);
        }

        // One more than the workflow broadcasts already open: the API broadcast would use it up if it counted.
        var previous = await SetEngineSettingAsync("Signals", BudgetKey, (open + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        try
        {
            await TakeAsync(empty.Id, 2, 201);
            Assert.Equal(SignalBroadcastStatuses.Completed, Assert.Single(await BroadcastsThrownByAsync(empty.Id)).Status);
            await TakeAsync(counted.Id, 2, 201);
            using var response = await TakeResponseAsync(refused.Id, 2, 201);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("signal broadcasts thrown by workflows are still delivering", await response.Content.ReadAsStringAsync());
        }
        finally
        {
            await RestoreEngineSettingAsync("Signals", BudgetKey, previous);
        }

        await ProcessDeliveryJobsAsync(api.Id);
        await ProcessDeliveryJobsAsync(Assert.Single(await BroadcastsThrownByAsync(counted.Id)).Id);
    }

    /// <summary>The API's budget counts only broadcasts thrown through the API.</summary>
    [Fact]
    public async Task ApiBudget_CountsOnlyOpenBroadcastsThrownThroughTheApi()
    {
        var blocker = await OpenBlockingBroadcastAsync();
        var signal = UniqueSignal();
        var receiver = ReceiverModel(signal);
        await StartAsync(await CreateAsync(receiver), OrderNumber("ORD-API"));
        int open;
        await using (var db = fixture.CreateDbContext())
        {
            open = await db.SignalBroadcasts.CountAsync(item => item.Status == SignalBroadcastStatuses.Delivering
                && (item.ThrownByKind == SignalThrowerKinds.User || item.ThrownByKind == SignalThrowerKinds.Client));
        }

        var previous = await SetEngineSettingAsync("Signals", "MaxOpenBroadcasts", (open + 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
        SignalBroadcastDetailDto accepted;
        try
        {
            accepted = await ThrowApiAsync(new ThrowSignalRequest(signal, receiver.Id));
            using var refused = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts", new ThrowSignalRequest(signal, receiver.Id));
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        }
        finally
        {
            await RestoreEngineSettingAsync("Signals", "MaxOpenBroadcasts", previous);
        }

        await ProcessDeliveryJobsAsync(accepted.Id);
        await ProcessDeliveryJobsAsync(blocker);
    }

    [Theory]
    [InlineData(null, 10_000)]
    [InlineData("", 10_000)]
    [InlineData("many", 10_000)]
    [InlineData("0", 10_000)]
    [InlineData("-3", 10_000)]
    [InlineData(" 25 ", 25)]
    [InlineData("100000", 100_000)]
    [InlineData("250000", 100_000)]
    public void WorkflowBudgetSetting_ParsesAPositiveWholeNumberUpToItsCap(string? value, int expected) =>
        Assert.Equal(expected, SignalSettings.ParseLimit(
            value,
            SignalSettings.DefaultMaxOpenWorkflowBroadcasts,
            SignalSettings.MaxOpenWorkflowBroadcastsCap));

    // Infrastructure

    /// <summary>
    /// The writer deletes a broadcast over the recipient limit in the caller's transaction, so
    /// a caller that catches the refusal and commits keeps no row.
    /// </summary>
    [Fact]
    public async Task RecipientLimit_TheWriterKeepsNothingForACallerThatCommits()
    {
        var signal = UniqueSignal();
        var receiver = ReceiverModel(signal);
        var receiverId = await CreateAsync(receiver);
        await StartAsync(receiverId, OrderNumber("ORD-L1"));
        await StartAsync(receiverId, OrderNumber("ORD-L2"));

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var writer = scope.ServiceProvider.GetRequiredService<SignalBroadcastWriter>();
            await using var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);
            var result = await writer.WriteAsync(
                new SignalBroadcastWrite(
                    new NewSignalBroadcastRecord(signal, SignalBroadcastScope.Family(receiver.Id), null,
                        SignalThrowerKinds.User, "writer-test", "[]", null, null, null, DateTimeOffset.UtcNow),
                    1,
                    "writer-test"),
                CancellationToken.None);
            Assert.True(result.RecipientLimitExceeded);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            await transaction.CommitAsync(CancellationToken.None);
        }

        await using var db = fixture.CreateDbContext();
        Assert.False(await db.SignalBroadcasts.AnyAsync(item => item.SignalName == signal));
        Assert.False(await db.SignalDeliveries.AnyAsync(delivery =>
            !db.SignalBroadcasts.Any(broadcast => broadcast.Id == delivery.BroadcastId)));
        Assert.False(await db.SignalBroadcastJobs.AnyAsync(link =>
            !db.SignalBroadcasts.Any(broadcast => broadcast.Id == link.BroadcastId)));
    }

    private async Task AssertGaugeMatchesPendingAsync()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var backlog = await scope.ServiceProvider.GetRequiredService<ISignalBroadcastRepository>()
            .GetBacklogAsync(CancellationToken.None);
        await using var db = fixture.CreateDbContext();
        var open = db.SignalBroadcasts.Where(item => item.Status == SignalBroadcastStatuses.Delivering).Select(item => item.Id);
        var pending = await db.SignalDeliveries.CountAsync(item =>
            item.Status == SignalDeliveryStatuses.Pending && open.Contains(item.BroadcastId));
        Assert.Equal(pending, backlog.PendingDeliveries);
    }
}
