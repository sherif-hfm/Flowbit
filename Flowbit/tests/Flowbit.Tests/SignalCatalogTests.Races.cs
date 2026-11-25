using System.Net;
using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// The name lock (EVENT-04F): a catalog write holds the exclusive lock of its name while it
/// changes the entry, so a publication, a throw, or a reactivation of the name waits and then
/// sees what the write committed. The tests hold the lock in a transaction of their own, change
/// the entry there, and release it, as a catalog write does.
/// </summary>
public sealed partial class SignalCatalogTests
{
    private static readonly SignalCatalogFieldDto VersionString = new("version", WorkflowVariableTypes.String, false, true);

    [Fact]
    public async Task Race_APublicationWaitsForACatalogWriteAndThenChecksTheNewEntry()
    {
        var name = UniqueName();
        await CreateEntryAsync(name, [VersionNumber]);
        var draft = await CreateDefinitionAsync(SignalRuntimeTests.CatchModel(name), publish: false);

        var publish = await WhileTheNameIsLockedAsync(
            name,
            () => SendAsync(HttpMethod.Post, $"/api/workflows/{draft.Id}/publish"),
            db => SetFieldsAsync(db, name, VersionString));

        using (publish)
        {
            Assert.Equal(HttpStatusCode.BadRequest, publish.StatusCode);
            Assert.Equal(
                $"Signal catch event #2 mapping to 'policyVersion' is number, but the catalog entry of '{name}' defines 'version' as string.",
                await ErrorAsync(publish));
        }
    }

    [Fact]
    public async Task Race_AnArchiveCommittedWhileAPublicationWaitsRefusesIt()
    {
        var name = UniqueName();
        await CreateEntryAsync(name, [VersionNumber]);
        var draft = await CreateDefinitionAsync(SignalRuntimeTests.CatchModel(name), publish: false);

        using var publish = await WhileTheNameIsLockedAsync(
            name,
            () => SendAsync(HttpMethod.Post, $"/api/workflows/{draft.Id}/publish"),
            db => ArchiveDirectlyAsync(db, name));
        Assert.Equal(HttpStatusCode.BadRequest, publish.StatusCode);
        Assert.Equal($"Signal catch event #2 uses signal '{name}', whose catalog entry is archived.", await ErrorAsync(publish));
    }

    [Fact]
    public async Task Race_AThrowWaitsForAnArchive()
    {
        var name = UniqueName();
        await CreateEntryAsync(name, [VersionNumber]);
        using var thrown = await WhileTheNameIsLockedAsync(
            name,
            () => SendAsync(HttpMethod.Post, "/api/signal-broadcasts",
                new ThrowSignalRequest(name, AllWorkflows: true, Payload: JsonSerializer.SerializeToElement(new { version = 3 }))),
            db => ArchiveDirectlyAsync(db, name));
        Assert.Equal(HttpStatusCode.BadRequest, thrown.StatusCode);
        Assert.Equal($"Signal '{name}' is archived in the signal catalog and cannot be thrown.", await ErrorAsync(thrown));
    }

    [Fact]
    public async Task Race_AThrowWaitsForAChangeAndIsCheckedAgainstIt()
    {
        var name = UniqueName();
        await CreateEntryAsync(name, [VersionNumber]);
        using var thrown = await WhileTheNameIsLockedAsync(
            name,
            () => SendAsync(HttpMethod.Post, "/api/signal-broadcasts",
                new ThrowSignalRequest(name, AllWorkflows: true, Payload: JsonSerializer.SerializeToElement(new { version = 3 }))),
            db => SetFieldsAsync(db, name, VersionString));
        Assert.Equal(HttpStatusCode.BadRequest, thrown.StatusCode);
        Assert.Equal($"Signal '{name}' payload field 'version' must be a string.", await ErrorAsync(thrown));
    }

    [Fact]
    public async Task Race_ACatalogWriteWaitsForAPublicationThatHoldsTheSharedLock()
    {
        var name = UniqueName();
        var entry = await CreateEntryAsync(name, [VersionNumber]);
        await using var db = fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock_shared(hashtext('signal-name'), hashtext({name}))");

        var write = SendAsync(HttpMethod.Put, $"/api/signal-catalog/entries/{name}",
            Change(entry.UpdatedAt, VersionString));
        await WaitForBlockedLockAsync(name);
        Assert.False(write.IsCompleted);

        // The publication the shared lock stands for: a published version the change no longer fits.
        var definition = SignalRuntimeTests.CatchModel(name);
        var published = new Flowbit.Infrastructure.Entities.WorkflowDefinitionEntity
        {
            Name = definition.Name, WorkflowKey = definition.Id, Version = 1, IsPublished = true, IsDefault = false,
            Definition = definition, CreatedAt = DateTimeOffset.UtcNow
        };
        db.WorkflowDefinitions.Add(published);
        await db.SaveChangesAsync();
        db.WorkflowDefinitionSignalNames.Add(new Flowbit.Infrastructure.Entities.WorkflowDefinitionSignalNameEntity
        {
            WorkflowDefinitionId = published.Id, NodeId = 2, SignalName = name, Usage = SignalNameUsages.Catch
        });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();

        using var response = await write;
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains($"(#{published.Id}): Signal catch event #2", await ErrorAsync(response), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Race_AReactivationWaitsForAChangeThatItsVersionNoLongerFits()
    {
        var name = UniqueName();
        var (instanceId, _) = await CompletedInstanceOnARetiredVersionAsync(name);
        var preview = await ReactivationPreviewAsync(instanceId);
        Assert.True(preview.CanReactivate, string.Join("; ", preview.Blockers.Select(blocker => blocker.Message)));

        using var commit = await WhileTheNameIsLockedAsync(
            name,
            () => SendAsync(HttpMethod.Post, $"/api/instances/{instanceId}/reactivation",
                new ReactivateInstanceRequest(3, preview.WorkflowId, preview.ExpectedUpdatedAt, "Catalog race")),
            db => SetFieldsAsync(db, name, VersionString));
        Assert.Equal(HttpStatusCode.Conflict, commit.StatusCode);
        Assert.Contains("signal_catalog_mismatch", await ErrorAsync(commit), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ reactivation

    [Fact]
    public async Task Reactivation_PreviewAndCommitRefuseAVersionThatNoLongerFits_AndAFittingOneReactivates()
    {
        var name = UniqueName();
        var (instanceId, entry) = await CompletedInstanceOnARetiredVersionAsync(name);

        // The retired version is not live, so the change succeeds; the live one reads json.
        var changed = await UpdateEntryAsync(entry, [VersionString]);
        var preview = await ReactivationPreviewAsync(instanceId);
        Assert.False(preview.CanReactivate);
        var blocker = Assert.Single(preview.Blockers, issue => issue.Code == "signal_catalog_mismatch");
        Assert.Equal(
            $"Signal catch event #2 mapping to 'policyVersion' is number, but the catalog entry of '{name}' defines 'version' as string.",
            blocker.Message);

        using (var refused = await SendAsync(HttpMethod.Post, $"/api/instances/{instanceId}/reactivation",
                   new ReactivateInstanceRequest(3, preview.WorkflowId, preview.ExpectedUpdatedAt, "Catalog check")))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("signal_catalog_mismatch", await ErrorAsync(refused), StringComparison.Ordinal);
        }

        await UpdateEntryAsync(changed, [VersionNumber]);
        var fitting = await ReactivationPreviewAsync(instanceId);
        Assert.True(fitting.CanReactivate, string.Join("; ", fitting.Blockers.Select(issue => issue.Message)));
        using var reactivated = await SendAsync(HttpMethod.Post, $"/api/instances/{instanceId}/reactivation",
            new ReactivateInstanceRequest(3, fitting.WorkflowId, fitting.ExpectedUpdatedAt, "Catalog fits again"));
        Assert.Equal(HttpStatusCode.OK, reactivated.StatusCode);
    }

    [Fact]
    public async Task Reactivation_OfAVersionWithoutSignalEventsReadsNoCatalog()
    {
        var model = SignalRuntimeTests.CatchModel(UniqueName());
        model.FlowNodes.RemoveAll(node => node.Id == 2);
        model.SequenceFlows.RemoveAll(flow => flow.SourceRef == 2 || flow.TargetRef == 2);
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 102, SourceRef = 1, TargetRef = 3 });
        var definition = await CreateDefinitionAsync(model, publish: true);
        var instance = await StartAsync(definition.Id);
        await TakeTaskAsync(instance.Id, 3, 301);

        fixture.CommandCounter.Reset(captureReaderCommandTexts: true);
        try
        {
            var preview = await ReactivationPreviewAsync(instance.Id);
            Assert.True(preview.CanReactivate);
            using var commit = await SendAsync(HttpMethod.Post, $"/api/instances/{instance.Id}/reactivation",
                new ReactivateInstanceRequest(3, preview.WorkflowId, preview.ExpectedUpdatedAt, "No signals"));
            Assert.Equal(HttpStatusCode.OK, commit.StatusCode);
            AssertNoCatalogStatement();
        }
        finally
        {
            fixture.CommandCounter.Reset();
        }
    }

    // ------------------------------------------------------------------ authorization

    [Fact]
    public async Task Readers_ComeFromAnyOfTheThreeRoleSettingsAndBothClientScopes()
    {
        var open = UniqueName();
        var narrowed = UniqueName();
        await CreateEntryAsync(open, []);
        await CreateEntryAsync(narrowed, [], roles: ["recall-manager"], allowClients: false);
        var previousSettings = await SetEngineSettingAsync("Settings", "RequiredRole", "admin,catalog-settings");
        var previousSignals = await SetEngineSettingAsync("Signals", "RequiredRole", "admin,catalog-signals");
        var previousWorkflow = await SetEngineSettingAsync("Workflow", "RequiredRole", "admin,catalog-workflow");
        try
        {
            foreach (var role in new[] { "catalog-settings", "catalog-signals", "catalog-workflow" })
            {
                using var read = await SendAsync(HttpMethod.Get, $"/api/signal-catalog/entries/{open}", null, "reader", [role], suppressAdmin: true);
                Assert.True(read.StatusCode == HttpStatusCode.OK, $"{role}: {read.StatusCode}");
            }

            using (var nobody = await SendAsync(HttpMethod.Get, "/api/signal-catalog/entries", null, "reader", ["viewer"], suppressAdmin: true))
            {
                Assert.Equal(HttpStatusCode.Forbidden, nobody.StatusCode);
            }

            using (var anonymous = await fixture.Client.GetAsync("/api/signal-catalog/entries"))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
            }

            foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                     {
                         (HttpMethod.Post, "/api/signal-catalog/entries", new CreateSignalCatalogEntryRequest(UniqueName())),
                         (HttpMethod.Put, $"/api/signal-catalog/entries/{open}", Change(DateTimeOffset.UtcNow)),
                         (HttpMethod.Post, $"/api/signal-catalog/entries/{open}/archive", new SignalCatalogLifecycleRequest(DateTimeOffset.UtcNow)),
                         (HttpMethod.Post, $"/api/signal-catalog/entries/{open}/reactivate", new SignalCatalogLifecycleRequest(DateTimeOffset.UtcNow)),
                         (HttpMethod.Post, "/api/signal-catalog/preview", new CreateSignalCatalogEntryRequest(UniqueName())),
                         (HttpMethod.Get, "/api/signal-catalog/uncataloged-names", null),
                         (HttpMethod.Get, $"/api/signal-catalog/entries/{open}/usage", null)
                     })
            {
                using var signalsOnly = await SendAsync(method, path, body, "reader", ["catalog-signals"], suppressAdmin: true);
                Assert.True(signalsOnly.StatusCode == HttpStatusCode.Forbidden, $"{method} {path}: {signalsOnly.StatusCode}");
            }

            using (var settingsAdmin = await SendAsync(HttpMethod.Post, "/api/signal-catalog/entries",
                       new CreateSignalCatalogEntryRequest(UniqueName()), "reader", ["catalog-settings"], suppressAdmin: true))
            {
                Assert.Equal(HttpStatusCode.Created, settingsAdmin.StatusCode);
            }

            async Task<bool> CanThrowAsUserAsync(string entryName, params string[] roles)
            {
                using var response = await SendAsync(HttpMethod.Get, $"/api/signal-catalog/entries/{entryName}", null, "reader", roles, suppressAdmin: true);
                return (await ReadAsync<SignalCatalogEntryDto>(response)).CanThrow;
            }

            Assert.True(await CanThrowAsUserAsync(open, "catalog-signals"));
            Assert.False(await CanThrowAsUserAsync(open, "catalog-workflow"));
            Assert.False(await CanThrowAsUserAsync(narrowed, "catalog-signals"));
            Assert.True(await CanThrowAsUserAsync(narrowed, "catalog-signals", "Recall-Manager"));
            Assert.False(await CanThrowAsUserAsync(narrowed, "catalog-workflow", "recall-manager"));

            var (thrower, throwerSecret) = await CreateClientAsync(SharedVariableClientScopes.SignalsThrow);
            var (reader, readerSecret) = await CreateClientAsync(SharedVariableClientScopes.SignalsRead);
            var (other, otherSecret) = await CreateClientAsync(SharedVariableClientScopes.Read);
            async Task<SignalCatalogEntryDto> ReadAsClientAsync(string entryName, string id, string secret)
            {
                using var response = await SendAsClientAsync(HttpMethod.Get, $"/api/signal-catalog/entries/{entryName}", id, secret);
                return await ReadAsync<SignalCatalogEntryDto>(response);
            }

            var asThrower = await ReadAsClientAsync(open, thrower, throwerSecret);
            Assert.True(asThrower.CanThrow);
            Assert.False((await ReadAsClientAsync(narrowed, thrower, throwerSecret)).CanThrow);
            Assert.False((await ReadAsClientAsync(open, reader, readerSecret)).CanThrow);

            // A client learns the contract, not which administrators wrote it or the user roles a throw needs.
            Assert.Null(asThrower.CreatedBy);
            Assert.Null(asThrower.UpdatedBy);
            using (var asUser = await SendAsync(HttpMethod.Get, $"/api/signal-catalog/entries/{open}", null, "reader", ["catalog-signals"], suppressAdmin: true))
            {
                var userView = await ReadAsync<SignalCatalogEntryDto>(asUser);
                Assert.Equal(("catalog-admin", "catalog-admin"), (userView.CreatedBy, userView.UpdatedBy));
            }

            using (var list = await SendAsClientAsync(HttpMethod.Get, $"/api/signal-catalog/entries?name={open}", reader, readerSecret))
            {
                Assert.Equal(HttpStatusCode.OK, list.StatusCode);
                var page = await ReadAsync<SignalCatalogListDto>(list);
                Assert.Empty(page.SignalRoles);
                var item = Assert.Single(page.Items);
                Assert.Null(item.CreatedBy);
                Assert.Null(item.UpdatedBy);
            }

            using (var wrongScope = await SendAsClientAsync(HttpMethod.Get, "/api/signal-catalog/entries", other, otherSecret))
            {
                Assert.Equal(HttpStatusCode.Forbidden, wrongScope.StatusCode);
            }

            using (var clientWrite = await SendAsClientAsync(HttpMethod.Post, "/api/signal-catalog/entries", thrower, throwerSecret,
                       new CreateSignalCatalogEntryRequest(UniqueName())))
            {
                Assert.True(clientWrite.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, clientWrite.StatusCode.ToString());
            }
        }
        finally
        {
            await SetEngineSettingAsync("Settings", "RequiredRole", previousSettings);
            await SetEngineSettingAsync("Signals", "RequiredRole", previousSignals);
            await SetEngineSettingAsync("Workflow", "RequiredRole", previousWorkflow);
        }
    }

    // ------------------------------------------------------------------ helpers

    /// <summary>
    /// An instance completed on version 1, which reads 'version' as a number and is no longer
    /// live: version 2, which reads it as json, is the published default, and version 1 is
    /// unpublished. The entry reads 'version' as a number.
    /// </summary>
    private async Task<(long InstanceId, SignalCatalogEntryDto Entry)> CompletedInstanceOnARetiredVersionAsync(string name)
    {
        var entry = await CreateEntryAsync(name, [VersionNumber]);
        var first = await CreateDefinitionAsync(SignalRuntimeTests.CatchModel(name), publish: true);
        var instance = await StartAsync(first.Id);
        var broadcast = await ThrowAsync(new ThrowSignalRequest(name, first.WorkflowKey,
            Payload: JsonSerializer.SerializeToElement(new { version = 1 })));
        await new SignalRuntimeTests(fixture).ProcessDeliveryJobsAsync(broadcast.Id);
        await TakeTaskAsync(instance.Id, 3, 301);

        var model = SignalRuntimeTests.CatchModel(name);
        model.Id = first.WorkflowKey;
        model.FlowNodes.Single(node => node.Id == 2).Signal!.OutputMappings[0].DataType = WorkflowVariableTypes.Json;
        WorkflowDetailDto second;
        using (var put = await SendAsync(HttpMethod.Put, $"/api/workflows/{first.Id}", new UpdateWorkflowRequest(model, true)))
        {
            second = await ReadAsync<WorkflowDetailDto>(put);
        }

        foreach (var path in new[] { $"/api/workflows/{second.Id}/set-default", $"/api/workflows/{first.Id}/unpublish" })
        {
            using var response = await SendAsync(HttpMethod.Post, path);
            Assert.True(response.IsSuccessStatusCode, $"{path}: {await response.Content.ReadAsStringAsync()}");
        }

        return (instance.Id, entry);
    }

    private async Task<InstanceReactivationPreviewDto> ReactivationPreviewAsync(long instanceId)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/instances/{instanceId}/reactivation");
        return await ReadAsync<InstanceReactivationPreviewDto>(response);
    }

    private async Task TakeTaskAsync(long instanceId, int nodeId, int flowId)
    {
        long taskId;
        await using (var db = fixture.CreateDbContext())
        {
            taskId = await db.UserTasks.Where(task => task.InstanceId == instanceId && task.NodeId == nodeId
                && task.Status == "active").Select(task => task.Id).FirstAsync();
        }

        using var action = await SendAsync(HttpMethod.Post, $"/api/user-tasks/{taskId}/flows/{flowId}", new TakeFlowRequest(null));
        Assert.True(action.StatusCode == HttpStatusCode.OK, await action.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Holds the exclusive name lock, starts the request, waits until it is blocked on the lock,
    /// changes the entry inside the held transaction, commits, and returns the request's response.
    /// </summary>
    private async Task<HttpResponseMessage> WhileTheNameIsLockedAsync(
        string name,
        Func<Task<HttpResponseMessage>> request,
        Func<AppDbContext, Task> change)
    {
        await using var db = fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT pg_advisory_xact_lock(hashtext('signal-name'), hashtext({name}))");
        var pending = request();
        await WaitForBlockedLockAsync(name);
        Assert.False(pending.IsCompleted);
        await change(db);
        await transaction.CommitAsync();
        return await pending.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private async Task WaitForBlockedLockAsync(string name)
    {
        await using var db = fixture.CreateDbContext();
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var waiting = await db.Database.SqlQuery<int>($"""
                SELECT count(*)::int AS "Value"
                FROM pg_locks
                WHERE locktype = 'advisory'
                  AND NOT granted
                  AND classid::bigint = (hashtext('signal-name')::bigint + 4294967296) % 4294967296
                  AND objid::bigint = (hashtext({name})::bigint + 4294967296) % 4294967296
                """).SingleAsync();
            if (waiting > 0)
            {
                return;
            }

            await Task.Delay(50);
        }

        Assert.Fail($"Nothing waited for the lock of '{name}'.");
    }

    private static Task SetFieldsAsync(AppDbContext db, string name, params SignalCatalogFieldDto[] fields) =>
        db.SignalCatalogEntries.Where(entry => entry.Name == name).ExecuteUpdateAsync(setters => setters
            .SetProperty(entry => entry.Fields, JsonSerializer.SerializeToElement(fields, JsonOptions))
            .SetProperty(entry => entry.UpdatedAt, DateTimeOffset.UtcNow));

    private static Task ArchiveDirectlyAsync(AppDbContext db, string name) =>
        db.SignalCatalogEntries.Where(entry => entry.Name == name).ExecuteUpdateAsync(setters => setters
            .SetProperty(entry => entry.Status, SignalCatalogStatuses.Archived)
            .SetProperty(entry => entry.ArchivedAt, DateTimeOffset.UtcNow));
}
