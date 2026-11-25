using System.Net;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// Changing, archiving, and reactivating entries: each archive blocker on its own, archiving once
/// the blockers end, stale timestamps, and a change that must state everything it replaces.
/// </summary>
public sealed partial class SignalCatalogTests
{
    [Fact]
    public async Task Archive_APublishedVersionAlone_ThenARunningInstanceAlone_BlockIt_UntilBothEnd()
    {
        var name = UniqueName();
        var entry = await CreateEntryAsync(name, [VersionNumber]);
        var first = await CreateDefinitionAsync(SignalRuntimeTests.CatchModel(name), publish: true);

        await AssertUsageAsync(name, published: 1);
        await AssertArchiveRefusedAsync(entry, "1 published workflow version uses it.");

        // The instance passes its wait, so only the version it runs on uses the name.
        var instance = await StartAsync(first.Id);
        var broadcast = await ThrowAsync(new ThrowSignalRequest(name, first.WorkflowKey,
            Payload: JsonSerializer.SerializeToElement(new { version = 1 })));
        await new SignalRuntimeTests(fixture).ProcessDeliveryJobsAsync(broadcast.Id);
        await RetireVersionAsync(first);

        await AssertUsageAsync(name, running: 1);
        await AssertArchiveRefusedAsync(entry, "1 workflow version with running instances uses it.");

        await TakeTaskAsync(instance.Id, 3, 301);
        await AssertUsageAsync(name);
        var archived = await ArchiveAsync(entry);
        Assert.Equal(SignalCatalogStatuses.Archived, archived.Status);
    }

    [Fact]
    public async Task Archive_ABroadcastStillDeliveringAloneBlocksIt_UntilItsDeliveriesEnd()
    {
        var name = UniqueName();
        var entry = await CreateEntryAsync(name, [VersionNumber]);
        var first = await CreateDefinitionAsync(SignalRuntimeTests.CatchModel(name), publish: true);
        var instance = await StartAsync(first.Id);
        await RetireVersionAsync(first);
        await AssertUsageAsync(name, running: 1, waits: 1);

        // The broadcast freezes the wait, then the instance is cancelled: only the broadcast is left.
        var broadcast = await ThrowAsync(new ThrowSignalRequest(name, first.WorkflowKey,
            Payload: JsonSerializer.SerializeToElement(new { version = 1 })));
        using (var cancelled = await SendAsync(HttpMethod.Post, $"/api/instances/{instance.Id}/cancel", new { }))
        {
            Assert.True(cancelled.IsSuccessStatusCode, await cancelled.Content.ReadAsStringAsync());
        }

        await AssertUsageAsync(name, delivering: 1);
        await AssertArchiveRefusedAsync(entry, "1 broadcast of it is still delivering.");

        await new SignalRuntimeTests(fixture).ProcessDeliveryJobsAsync(broadcast.Id);
        Assert.NotEqual(SignalBroadcastStatuses.Delivering, (await BroadcastAsync(broadcast.Id)).Status);
        await AssertUsageAsync(name);
        await ArchiveAsync(entry);
    }

    [Fact]
    public async Task Archive_WithAStaleTimestampIsAConflict()
    {
        var name = UniqueName();
        var created = await CreateEntryAsync(name, [VersionNumber]);
        await UpdateEntryAsync(created, [VersionNumber with { Description = "Changed." }]);

        using var stale = await SendAsync(HttpMethod.Post, $"/api/signal-catalog/entries/{name}/archive",
            new SignalCatalogLifecycleRequest(created.UpdatedAt));
        Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal($"Signal catalog entry '{name}' was changed by another request; reload and try again.", await ErrorAsync(stale));
    }

    [Fact]
    public async Task Change_MustStateTheFieldsTheRolesAndBothSwitches()
    {
        var name = UniqueName();
        var entry = await CreateEntryAsync(name, [VersionNumber], roles: ["recall-manager"], allowClients: false, allowWorkflows: false);
        var complete = new Dictionary<string, object?>
        {
            ["expectedUpdatedAt"] = entry.UpdatedAt,
            ["description"] = "Changed.",
            ["fields"] = new[] { VersionNumber },
            ["throwRoles"] = new[] { "recall-manager" },
            ["allowClients"] = false,
            ["allowWorkflows"] = false
        };

        // Leaving one out would widen who may throw the name, or empty its contract, by accident.
        foreach (var member in new[] { "fields", "throwRoles", "allowClients", "allowWorkflows" })
        {
            var partial = complete.Where(pair => pair.Key != member).ToDictionary(pair => pair.Key, pair => pair.Value);
            using var refused = await SendAsync(HttpMethod.Put, $"/api/signal-catalog/entries/{name}", partial);
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains($"Missing required properties: '{member}'", await ErrorAsync(refused), StringComparison.Ordinal);
        }

        using (var read = await SendAsync(HttpMethod.Get, $"/api/signal-catalog/entries/{name}"))
        {
            var unchanged = await ReadAsync<SignalCatalogEntryDto>(read);
            Assert.Equal((entry.UpdatedAt, false, false), (unchanged.UpdatedAt, unchanged.AllowClients, unchanged.AllowWorkflows));
            Assert.Equal(["recall-manager"], unchanged.ThrowRoles);
        }

        // The description alone may be left out, which removes it.
        var withoutDescription = complete.Where(pair => pair.Key != "description").ToDictionary(pair => pair.Key, pair => pair.Value);
        using var changed = await SendAsync(HttpMethod.Put, $"/api/signal-catalog/entries/{name}", withoutDescription);
        var result = await ReadAsync<SignalCatalogEntryDto>(changed);
        Assert.Null(result.Description);
        Assert.Equal(["recall-manager"], result.ThrowRoles);
    }

    /// <summary>
    /// Makes a version of another signal name the family's published default and unpublishes
    /// the given version, so the given version is live only while an instance runs on it.
    /// </summary>
    private async Task RetireVersionAsync(WorkflowDetailDto version)
    {
        var replacement = SignalRuntimeTests.CatchModel(UniqueName());
        replacement.Id = version.WorkflowKey;
        WorkflowDetailDto second;
        using (var put = await SendAsync(HttpMethod.Put, $"/api/workflows/{version.Id}", new UpdateWorkflowRequest(replacement, true)))
        {
            second = await ReadAsync<WorkflowDetailDto>(put);
        }

        foreach (var path in new[] { $"/api/workflows/{second.Id}/set-default", $"/api/workflows/{version.Id}/unpublish" })
        {
            using var response = await SendAsync(HttpMethod.Post, path);
            Assert.True(response.IsSuccessStatusCode, $"{path}: {await response.Content.ReadAsStringAsync()}");
        }
    }

    private async Task AssertUsageAsync(
        string name, long published = 0, long running = 0, long waits = 0, long starts = 0, long delivering = 0)
    {
        var usage = await UsageAsync(name);
        Assert.Equal(
            (published, running, waits, starts, delivering),
            (usage.PublishedVersionCount, usage.RunningVersionCount, usage.ActiveWaitCount,
                usage.StartingFamilyCount, usage.DeliveringBroadcastCount));
    }

    private async Task AssertArchiveRefusedAsync(SignalCatalogEntryDto entry, string blockers)
    {
        using var refused = await SendAsync(HttpMethod.Post, $"/api/signal-catalog/entries/{entry.Name}/archive",
            new SignalCatalogLifecycleRequest(entry.UpdatedAt));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Equal($"Signal catalog entry '{entry.Name}' cannot be archived: {blockers}", await ErrorAsync(refused));
    }
}
