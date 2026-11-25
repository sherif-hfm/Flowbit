using System.Net;
using System.Text.Json;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

/// <summary>Throws, cancels, and retries through the API against the catalog, and a runtime that never reads it.</summary>
public sealed partial class SignalCatalogTests
{
    [Fact]
    public async Task Throw_APayloadThatDoesNotFit_Returns400AndWritesNoBroadcast()
    {
        var name = UniqueName();
        await CreateEntryAsync(name, [new SignalCatalogFieldDto("reason", WorkflowVariableTypes.String, false, true)]);

        using (var missing = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts",
                   new ThrowSignalRequest(name, AllWorkflows: true, Payload: JsonSerializer.SerializeToElement(new { reson = "patient-4711" }))))
        {
            Assert.Equal(HttpStatusCode.BadRequest, missing.StatusCode);
            var error = await ErrorAsync(missing);
            Assert.Equal($"Signal '{name}' payload is missing the required field 'reason'.", error);
        }

        using (var unknown = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts",
                   new ThrowSignalRequest(name, AllWorkflows: true,
                       Payload: JsonSerializer.SerializeToElement(new { reason = "mould", reson = "patient-4711" }))))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
            var error = await ErrorAsync(unknown);
            Assert.Equal($"Signal '{name}' payload member 'reson' is not a field of its catalog entry. Its fields: reason (string).", error);
            Assert.DoesNotContain("patient-4711", error, StringComparison.Ordinal);
        }

        await using var db = fixture.CreateDbContext();
        Assert.False(await db.SignalBroadcasts.AnyAsync(broadcast => broadcast.SignalName == name));

        var thrown = await ThrowAsync(new ThrowSignalRequest(name, AllWorkflows: true,
            Payload: JsonSerializer.SerializeToElement(new { reason = "mould" })));
        Assert.Equal(SignalBroadcastStatuses.Completed, thrown.Status);
    }

    [Fact]
    public async Task Throw_AnOptionalFieldSentAsNull_Returns400()
    {
        var name = UniqueName();
        await CreateEntryAsync(name, [VersionNumber, new SignalCatalogFieldDto("note", WorkflowVariableTypes.String)]);

        using (var refused = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts",
                   new ThrowSignalRequest(name, AllWorkflows: true,
                       Payload: JsonDocument.Parse("""{"version":1,"note":null}""").RootElement.Clone())))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal(
                $"Signal '{name}' payload field 'note' is null; leave out an optional field instead of sending null.",
                await ErrorAsync(refused));
        }

        await using (var db = fixture.CreateDbContext())
        {
            Assert.False(await db.SignalBroadcasts.AnyAsync(broadcast => broadcast.SignalName == name));
        }

        await ThrowAsync(new ThrowSignalRequest(name, AllWorkflows: true, Payload: JsonSerializer.SerializeToElement(new { version = 1 })));
    }

    [Fact]
    public async Task Retry_OfAnArchivedNameIsRefused_WhileCancelIgnoresTheArchive()
    {
        var name = UniqueName();
        var definition = await CreateDefinitionAsync(SignalRuntimeTests.CatchModel(name), publish: true);
        await StartAsync(definition.Id);
        var failedBroadcast = await ThrowAsync(new ThrowSignalRequest(name, definition.WorkflowKey,
            Payload: JsonSerializer.SerializeToElement(new { version = "bad" })));
        await new SignalRuntimeTests(fixture).ProcessDeliveryJobsAsync(failedBroadcast.Id);
        var failed = await BroadcastAsync(failedBroadcast.Id);
        Assert.Equal(SignalBroadcastStatuses.CompletedWithIssues, failed.Status);
        // A payload that failed a mapping leaves the wait, so this one is left delivering to it.
        var delivering = await ThrowAsync(new ThrowSignalRequest(name, definition.WorkflowKey,
            Payload: JsonSerializer.SerializeToElement(new { version = 2 })), "alice", ["admin"]);

        await CreateEntryAsync(name, [VersionNumber]);
        // Only possible by writing directly: the version and the broadcast still use the name.
        await using (var db = fixture.CreateDbContext())
        {
            await ArchiveDirectlyAsync(db, name);
        }

        using (var retry = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{failed.Id}/retry-failed",
                   new RetrySignalDeliveriesRequest(failed.UpdatedAt)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, retry.StatusCode);
            Assert.Equal($"Signal '{name}' is archived in the signal catalog and cannot be thrown.", await ErrorAsync(retry));
        }

        using var cancel = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{delivering.Id}/cancel", new { }, "bob", ["admin"]);
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
        Assert.Equal(SignalBroadcastStatuses.Cancelled, (await ReadAsync<SignalBroadcastDetailDto>(cancel)).Status);
    }

    [Fact]
    public async Task Throw_AtTheOpenBroadcastLimit_StillReportsAPayloadThatDoesNotFit()
    {
        var waiting = UniqueName();
        var definition = await CreateDefinitionAsync(SignalRuntimeTests.CatchModel(waiting), publish: true);
        await StartAsync(definition.Id);
        // Left delivering, so the limit of one is reached.
        await ThrowAsync(new ThrowSignalRequest(waiting, definition.WorkflowKey, Payload: JsonSerializer.SerializeToElement(new { version = 1 })));

        var name = UniqueName();
        await CreateEntryAsync(name, [VersionNumber]);
        var previous = await SetEngineSettingAsync("Signals", "MaxOpenBroadcasts", "1");
        try
        {
            using (var misfit = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts",
                       new ThrowSignalRequest(name, AllWorkflows: true, Payload: JsonSerializer.SerializeToElement(new { version = "one" }))))
            {
                Assert.Equal(HttpStatusCode.BadRequest, misfit.StatusCode);
                Assert.Equal($"Signal '{name}' payload field 'version' must be a number.", await ErrorAsync(misfit));
            }

            using var fits = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts",
                new ThrowSignalRequest(name, AllWorkflows: true, Payload: JsonSerializer.SerializeToElement(new { version = 1 })));
            Assert.Equal(HttpStatusCode.Conflict, fits.StatusCode);
            Assert.Contains("signal broadcasts are still delivering", await ErrorAsync(fits), StringComparison.Ordinal);
        }
        finally
        {
            await SetEngineSettingAsync("Signals", "MaxOpenBroadcasts", previous);
        }
    }

    [Fact]
    public async Task Throw_NeedsTheEntrysRolesAndAllowClients()
    {
        var name = UniqueName();
        await CreateEntryAsync(name, [], roles: ["recall-manager"], allowClients: false);
        var request = new ThrowSignalRequest(name, AllWorkflows: true);

        using (var refused = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts", request, "bob", ["admin"]))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            using var body = JsonDocument.Parse(await refused.Content.ReadAsStringAsync());
            Assert.Equal(SignalCatalogAccessCode, body.RootElement.GetProperty("code").GetString());
            Assert.Equal($"Throwing signal '{name}' needs one of the roles its catalog entry lists: recall-manager.",
                body.RootElement.GetProperty("error").GetString());
        }

        var thrown = await ThrowAsync(request, "carol", ["admin", "Recall-Manager"]);
        Assert.Equal("carol", thrown.ThrownBy);

        var (clientId, secret) = await CreateClientAsync(SharedVariableClientScopes.SignalsThrow);
        using (var client = await SendAsClientAsync(HttpMethod.Post, "/api/signal-broadcasts", clientId, secret, request))
        {
            Assert.Equal(HttpStatusCode.Forbidden, client.StatusCode);
            using var body = JsonDocument.Parse(await client.Content.ReadAsStringAsync());
            Assert.Equal(SignalCatalogAccessCode, body.RootElement.GetProperty("code").GetString());
            Assert.Equal($"Signal '{name}' cannot be thrown by an API client; its catalog entry does not allow clients.",
                body.RootElement.GetProperty("error").GetString());
        }

        // A policy refusal keeps its empty body.
        using var policy = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts", request, "dave", ["viewer"], suppressAdmin: true);
        Assert.Equal(HttpStatusCode.Forbidden, policy.StatusCode);
        Assert.Equal(string.Empty, await policy.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Throw_AnArchivedNameIsRefused()
    {
        var name = UniqueName();
        await ArchiveAsync(await CreateEntryAsync(name, []));
        using var refused = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts", new ThrowSignalRequest(name, AllWorkflows: true));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Equal($"Signal '{name}' is archived in the signal catalog and cannot be thrown.", await ErrorAsync(refused));
    }

    [Fact]
    public async Task Throw_RequiredModeRefusesANameWithoutAnEntry_OptionalThrowsAsToday()
    {
        var name = UniqueName();
        await ThrowAsync(new ThrowSignalRequest(name, AllWorkflows: true, Payload: JsonSerializer.SerializeToElement(new { any = 1 })));
        var previous = await SetEngineSettingAsync("Signals", "CatalogMode", " REQUIRED ");
        try
        {
            using var refused = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts", new ThrowSignalRequest(name, AllWorkflows: true));
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Equal($"Signal '{name}' has no entry in the signal catalog; this deployment requires one.", await ErrorAsync(refused));

            await CreateEntryAsync(name, []);
            await ThrowAsync(new ThrowSignalRequest(name, AllWorkflows: true));
        }
        finally
        {
            await SetEngineSettingAsync("Signals", "CatalogMode", previous);
        }
    }

    [Fact]
    public async Task Throw_ARepeatedKeyReplaysABroadcastThrownBeforeItsEntryExisted()
    {
        var name = UniqueName();
        var request = new ThrowSignalRequest(name, AllWorkflows: true,
            Payload: JsonSerializer.SerializeToElement(new { free = "text" }), IdempotencyKey: $"key-{Guid.NewGuid():N}");
        var first = await ThrowAsync(request);
        await CreateEntryAsync(name, [VersionNumber]);

        using var replay = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts", request);
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        Assert.Equal(first.Id, (await ReadAsync<SignalBroadcastDetailDto>(replay)).Id);

        using var fresh = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts", request with { IdempotencyKey = $"key-{Guid.NewGuid():N}" });
        Assert.Equal(HttpStatusCode.BadRequest, fresh.StatusCode);
    }

    [Fact]
    public async Task Retry_FollowsTheThrowRules()
    {
        var name = UniqueName();
        var definition = await CreateDefinitionAsync(SignalRuntimeTests.CatchModel(name), publish: true);
        await StartAsync(definition.Id);
        var broadcast = await ThrowAsync(new ThrowSignalRequest(name, definition.WorkflowKey,
            Payload: JsonSerializer.SerializeToElement(new { version = "bad" })));
        await new SignalRuntimeTests(fixture).ProcessDeliveryJobsAsync(broadcast.Id);
        var failed = await BroadcastAsync(broadcast.Id);
        Assert.Equal(SignalBroadcastStatuses.CompletedWithIssues, failed.Status);

        await CreateEntryAsync(name, [VersionNumber], roles: ["recall-manager"]);
        using (var refused = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{broadcast.Id}/retry-failed",
                   new RetrySignalDeliveriesRequest(failed.UpdatedAt)))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal($"Retrying signal '{name}' needs one of the roles its catalog entry lists: recall-manager.", await ErrorAsync(refused));
        }

        using var retried = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{broadcast.Id}/retry-failed",
            new RetrySignalDeliveriesRequest(failed.UpdatedAt), "erin", ["admin", "recall-manager"]);
        Assert.Equal(HttpStatusCode.OK, retried.StatusCode);
    }

    [Fact]
    public async Task Cancel_TheThrowerMayAlways_OthersNeedTheEntrysRoles()
    {
        var name = UniqueName();
        var entry = await CreateEntryAsync(name, [VersionNumber]);
        var definition = await CreateDefinitionAsync(SignalRuntimeTests.CatchModel(name), publish: true);
        await StartAsync(definition.Id);
        var request = new ThrowSignalRequest(name, definition.WorkflowKey, Payload: JsonSerializer.SerializeToElement(new { version = 2 }));
        var byAlice = await ThrowAsync(request, "alice", ["admin"]);
        var byFrank = await ThrowAsync(request, "frank", ["admin"]);
        await UpdateEntryAsync(entry, [VersionNumber], roles: ["recall-manager"]);

        using (var refused = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{byAlice.Id}/cancel", new { }, "bob", ["admin"]))
        {
            Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
            Assert.Equal($"Cancelling signal '{name}' needs one of the roles its catalog entry lists: recall-manager.", await ErrorAsync(refused));
        }

        using (var own = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{byAlice.Id}/cancel", new { }, "alice", ["admin"]))
        {
            Assert.Equal(HttpStatusCode.OK, own.StatusCode);
            Assert.Equal(SignalBroadcastStatuses.Cancelled, (await ReadAsync<SignalBroadcastDetailDto>(own)).Status);
        }

        using var manager = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{byFrank.Id}/cancel", new { }, "bob", ["admin", "recall-manager"]);
        Assert.Equal(HttpStatusCode.OK, manager.StatusCode);
    }

    [Fact]
    public async Task Cancel_AClientMayCancelItsOwnBroadcastAfterItsEntryStopsAllowingClients()
    {
        var name = UniqueName();
        var entry = await CreateEntryAsync(name, [VersionNumber]);
        var definition = await CreateDefinitionAsync(SignalRuntimeTests.CatchModel(name), publish: true);
        await StartAsync(definition.Id);
        var (clientId, secret) = await CreateClientAsync(SharedVariableClientScopes.SignalsThrow, SharedVariableClientScopes.SignalsRead);
        SignalBroadcastDetailDto thrown;
        using (var response = await SendAsClientAsync(HttpMethod.Post, "/api/signal-broadcasts", clientId, secret,
                   new ThrowSignalRequest(name, definition.WorkflowKey, Payload: JsonSerializer.SerializeToElement(new { version = 2 }))))
        {
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
            thrown = await ReadAsync<SignalBroadcastDetailDto>(response);
        }

        await UpdateEntryAsync(entry, [VersionNumber], allowClients: false);
        using (var again = await SendAsClientAsync(HttpMethod.Post, "/api/signal-broadcasts", clientId, secret,
                   new ThrowSignalRequest(name, definition.WorkflowKey, Payload: JsonSerializer.SerializeToElement(new { version = 2 }))))
        {
            Assert.Equal(HttpStatusCode.Forbidden, again.StatusCode);
        }

        using var cancel = await SendAsClientAsync(HttpMethod.Post, $"/api/signal-broadcasts/{thrown.Id}/cancel", clientId, secret, new { });
        Assert.Equal(HttpStatusCode.OK, cancel.StatusCode);
    }

    [Fact]
    public async Task Runtime_DeliveriesStartsAndWorkflowThrowsReadNoCatalog_EvenInRequiredMode()
    {
        var name = UniqueName();
        await CreateEntryAsync(name, [VersionNumber]);
        var catcher = await CreateDefinitionAsync(SignalRuntimeTests.CatchModel(name), publish: true);
        var waiting = await StartAsync(catcher.Id);
        await CreateDefinitionAsync(StartModel(name), publish: true);
        var thrower = ThrowerModel(name, Send("version", "count", required: true));
        thrower.FlowNodes.Single(node => node.Id == 1).Variables!.Add(
            new VariableModel { Id = 3, Name = "count", DataType = WorkflowVariableTypes.Number, Required = true });
        var throwing = await CreateDefinitionAsync(thrower, publish: true);

        var previous = await SetEngineSettingAsync("Signals", "CatalogMode", "required");
        fixture.CommandCounter.Reset(captureReaderCommandTexts: true);
        try
        {
            var instance = await StartAsync(throwing.Id, new Dictionary<string, JsonElement>
            {
                ["why"] = JsonSerializer.SerializeToElement("recall"),
                ["skuList"] = JsonSerializer.SerializeToElement(new[] { "a" }),
                ["count"] = JsonSerializer.SerializeToElement(4)
            });
            // The workflow's throw ran inside that start, in required mode.
            AssertNoCatalogStatement();
            long broadcastId;
            await using (var db = fixture.CreateDbContext())
            {
                broadcastId = await db.SignalBroadcasts.Where(broadcast => broadcast.ThrowerInstanceId == instance.Id)
                    .Select(broadcast => broadcast.Id).SingleAsync();
            }

            fixture.CommandCounter.Reset(captureReaderCommandTexts: true);
            await new SignalRuntimeTests(fixture).ProcessDeliveryJobsAsync(broadcastId);
            AssertNoCatalogStatement();

            var finished = await BroadcastAsync(broadcastId);
            Assert.Equal((SignalBroadcastStatuses.Completed, 2), (finished.Status, finished.DeliveredCount));
            await using var check = fixture.CreateDbContext();
            Assert.Equal(4, (await check.InstanceVariableCurrentValues.AsNoTracking()
                .SingleAsync(value => value.InstanceId == waiting.Id && value.VariableName == "policyVersion")).ValueJson.RootElement.GetInt32());
        }
        finally
        {
            fixture.CommandCounter.Reset();
            await SetEngineSettingAsync("Signals", "CatalogMode", previous);
        }
    }

    [Fact]
    public async Task Runtime_AWorkflowThrowReadsNoCatalog()
    {
        var name = UniqueName();
        await CreateEntryAsync(name, [VersionNumber]);
        var thrower = ThrowerModel(name, Send("version", "count", required: true));
        thrower.FlowNodes.Single(node => node.Id == 1).Variables!.Add(
            new VariableModel { Id = 3, Name = "count", DataType = WorkflowVariableTypes.Number, Required = true });
        var throwing = await CreateDefinitionAsync(thrower, publish: true);

        fixture.CommandCounter.Reset(captureReaderCommandTexts: true);
        try
        {
            await StartAsync(throwing.Id, new Dictionary<string, JsonElement>
            {
                ["why"] = JsonSerializer.SerializeToElement("recall"),
                ["skuList"] = JsonSerializer.SerializeToElement(new[] { "a" }),
                ["count"] = JsonSerializer.SerializeToElement(4)
            });
            AssertNoCatalogStatement();
        }
        finally
        {
            fixture.CommandCounter.Reset();
        }
    }

    private const string SignalCatalogAccessCode = Flowbit.Service.Services.SignalCatalogAccess.ForbiddenCode;

    private async Task<SignalBroadcastDetailDto> BroadcastAsync(long id)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/signal-broadcasts/{id}");
        return await ReadAsync<SignalBroadcastDetailDto>(response);
    }
}
