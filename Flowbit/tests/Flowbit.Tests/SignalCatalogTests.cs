using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flowbit.Infrastructure.Data.Migrations;
using Flowbit.Infrastructure.Entities;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Flowbit.Service.Ai;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// The signal catalog against PostgreSQL (EVENT-04F): saves and publications, the references
/// table, catalog writes against live versions, throws, cancels and retries, reactivation,
/// races on the name lock, and authorization. Entries and Signals.CatalogMode apply to the
/// whole shared database, so every test uses names of its own, restores the mode in finally,
/// and creates live versions through the API.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed partial class SignalCatalogTests(PostgresApiFixture fixture) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly SignalCatalogFieldDto VersionNumber = new("version", WorkflowVariableTypes.Number, false, true);

    public Task InitializeAsync() => Task.CompletedTask;

    // Some tests leave a broadcast delivering on purpose; later classes must not lease its jobs.
    public Task DisposeAsync() => SignalDeliveryJobLeftovers.RetireAsync(fixture);

    // ------------------------------------------------------------------ saves and publications

    [Fact]
    public async Task Draft_WithAMissingRequiredFieldSaves_AndItsPublicationIsRefused()
    {
        var name = UniqueName();
        await CreateEntryAsync(name, [
            new SignalCatalogFieldDto("reason", WorkflowVariableTypes.String, false, true),
            new SignalCatalogFieldDto("skus", WorkflowVariableTypes.String, true, true)
        ]);
        var model = ThrowerModel(name, Send("reason", "why", required: true));

        var draft = await CreateDefinitionAsync(model, publish: false);
        using (var publish = await SendAsync(HttpMethod.Post, $"/api/workflows/{draft.Id}/publish"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, publish.StatusCode);
            Assert.Equal(
                $"Signal throw event #2 payload does not send 'skus', which the catalog entry of '{name}' requires.",
                await ErrorAsync(publish));
        }

        model.Id = $"catalog-{Guid.NewGuid():N}";
        using var published = await PostDefinitionAsync(model, publish: true);
        Assert.Equal(HttpStatusCode.BadRequest, published.StatusCode);
        Assert.Contains("payload does not send 'skus'", await ErrorAsync(published), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Draft_WithAMemberThatIsNotAFieldOrAWrongType_IsRefused()
    {
        var name = UniqueName();
        await CreateEntryAsync(name, [new SignalCatalogFieldDto("reason", WorkflowVariableTypes.String, false, false)]);

        using (var unknown = await PostDefinitionAsync(ThrowerModel(name, Send("reson", "why")), publish: false))
        {
            Assert.Equal(HttpStatusCode.BadRequest, unknown.StatusCode);
            Assert.Equal(
                $"Signal throw event #2 payload sends 'reson', which is not a field of the catalog entry of '{name}'. Its fields: reason (string).",
                await ErrorAsync(unknown));
        }

        using var wrongType = await PostDefinitionAsync(ThrowerModel(name, Send("reason", "skuList")), publish: false);
        Assert.Equal(HttpStatusCode.BadRequest, wrongType.StatusCode);
        Assert.Equal(
            $"Signal throw event #2 payload entry 'reason' reads 'skuList', declared as string[], but the catalog entry of '{name}' defines it as string.",
            await ErrorAsync(wrongType));
    }

    [Fact]
    public async Task RequiredMode_DraftSaves_PublicationIsRefused_AndLiveNamesAreListed()
    {
        var draftName = UniqueName();
        var liveName = UniqueName();
        var live = await CreateDefinitionAsync(SignalRuntimeTests.CatchModel(liveName), publish: true);
        var previous = await SetEngineSettingAsync("Signals", "CatalogMode", "required");
        try
        {
            var draft = await CreateDefinitionAsync(SignalRuntimeTests.CatchModel(draftName), publish: false);
            using (var publish = await SendAsync(HttpMethod.Post, $"/api/workflows/{draft.Id}/publish"))
            {
                Assert.Equal(HttpStatusCode.BadRequest, publish.StatusCode);
                Assert.Equal(
                    $"Signal catch event #2 uses signal '{draftName}', which has no entry in the signal catalog; this deployment requires one before publication.",
                    await ErrorAsync(publish));
            }

            using var uncataloged = await SendAsync(HttpMethod.Get, "/api/signal-catalog/uncataloged-names");
            var list = await ReadAsync<UncatalogedSignalNameListDto>(uncataloged);
            Assert.Equal(SignalCatalogModes.Required, list.Mode);
            Assert.Contains(list.Items, item => item.Name == liveName && item.VersionCount == 1);
            Assert.DoesNotContain(list.Items, item => item.Name == draftName);
            Assert.NotEqual(0, live.Id);
        }
        finally
        {
            await SetEngineSettingAsync("Signals", "CatalogMode", previous);
        }
    }

    [Fact]
    public async Task SetDefault_RepeatsTheReadinessCheck()
    {
        var name = UniqueName();
        var first = await CreateDefinitionAsync(SignalRuntimeTests.CatchModel(UniqueName()), publish: true);
        var model = SignalRuntimeTests.CatchModel(name);
        model.Id = first.WorkflowKey;
        WorkflowDetailDto second;
        using (var put = await SendAsync(HttpMethod.Put, $"/api/workflows/{first.Id}", new UpdateWorkflowRequest(model, true)))
        {
            Assert.True(put.IsSuccessStatusCode, await put.Content.ReadAsStringAsync());
            second = await ReadAsync<WorkflowDetailDto>(put);
        }

        var previous = await SetEngineSettingAsync("Signals", "CatalogMode", "required");
        try
        {
            using var setDefault = await SendAsync(HttpMethod.Post, $"/api/workflows/{second.Id}/set-default");
            Assert.Equal(HttpStatusCode.BadRequest, setDefault.StatusCode);
            Assert.Contains("has no entry in the signal catalog", await ErrorAsync(setDefault), StringComparison.Ordinal);
        }
        finally
        {
            await SetEngineSettingAsync("Signals", "CatalogMode", previous);
        }
    }

    [Fact]
    public async Task DefinitionWithoutSignalEvents_RunsNoCatalogStatement()
    {
        var model = SignalRuntimeTests.CatchModel(UniqueName());
        model.FlowNodes.RemoveAll(node => node.Id == 2);
        model.SequenceFlows.RemoveAll(flow => flow.SourceRef == 2 || flow.TargetRef == 2);
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 102, SourceRef = 1, TargetRef = 3 });

        fixture.CommandCounter.Reset(captureReaderCommandTexts: true);
        try
        {
            var created = await CreateDefinitionAsync(model, publish: false);
            using var publish = await SendAsync(HttpMethod.Post, $"/api/workflows/{created.Id}/publish");
            Assert.True(publish.IsSuccessStatusCode, await publish.Content.ReadAsStringAsync());
            AssertNoCatalogStatement();
        }
        finally
        {
            fixture.CommandCounter.Reset();
        }
    }

    [Fact]
    public async Task ArchivedName_IsRefusedForADraft()
    {
        var name = UniqueName();
        var entry = await CreateEntryAsync(name, [VersionNumber]);
        await ArchiveAsync(entry);
        using var draft = await PostDefinitionAsync(SignalRuntimeTests.CatchModel(name), publish: false);
        Assert.Equal(HttpStatusCode.BadRequest, draft.StatusCode);
        Assert.Equal($"Signal catch event #2 uses signal '{name}', whose catalog entry is archived.", await ErrorAsync(draft));
    }

    [Fact]
    public async Task AiProposal_ListsSaveStageProblemsAsSaveBlockersAndTheRestAsPublicationBlockers()
    {
        var unknown = UniqueName();
        var missing = UniqueName();
        await CreateEntryAsync(unknown, [new SignalCatalogFieldDto("reason", WorkflowVariableTypes.String)]);
        await CreateEntryAsync(missing, [
            new SignalCatalogFieldDto("reason", WorkflowVariableTypes.String),
            new SignalCatalogFieldDto("skus", WorkflowVariableTypes.String, true, true)
        ]);
        var model = ThrowerModel(unknown, Send("reson", "why"));
        var second = ThrowerModel(missing, Send("reason", "why")).FlowNodes.Single(node => node.Id == 2);
        second.Id = 5;
        model.FlowNodes.Add(second);
        model.FlowNodes.Add(new FlowNodeModel { Id = 6, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 302, SourceRef = 3, TargetRef = 5, Name = "Again" });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 6 });

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var validation = await scope.ServiceProvider.GetRequiredService<IWorkflowAiAuthoringService>()
            .ValidateAsync(JsonSerializer.SerializeToElement(model, JsonOptions), CancellationToken.None);
        Assert.True(validation.IsValid, string.Join(" ", validation.Errors));
        Assert.False(validation.CanSave);
        Assert.False(validation.CanPublish);
        Assert.Equal(
            [$"Signal throw event #2 payload sends 'reson', which is not a field of the catalog entry of '{unknown}'. Its fields: reason (string)."],
            validation.SaveBlockers);
        Assert.Equal(
            [$"Signal throw event #5 payload does not send 'skus', which the catalog entry of '{missing}' requires."],
            validation.PublicationBlockers);
    }

    // ------------------------------------------------------------------ references

    [Fact]
    public async Task References_OneRowPerSignalEvent_AndADeletedVersionTakesThem()
    {
        var a = UniqueName();
        var b = UniqueName();
        var c = UniqueName();
        var model = SignalRuntimeTests.CatchModel(a);
        model.FlowNodes.RemoveAll(node => node.Id == 4);
        model.SequenceFlows.RemoveAll(flow => flow.Id == 301);
        model.FlowNodes.AddRange(
        [
            new FlowNodeModel
            {
                Id = 6, Name = "Tell", Type = BpmnFlowNodeTypes.IntermediateSignalThrowEvent,
                Signal = new SignalEventModel { Name = b, Scope = new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows }, Payload = [] }
            },
            new FlowNodeModel
            {
                Id = 4, Name = "End and tell", Type = BpmnFlowNodeTypes.SignalEndEvent,
                Signal = new SignalEventModel { Name = a, Scope = new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.ThisInstance }, Payload = [] }
            },
            new FlowNodeModel
            {
                Id = 5, Name = "Stop", Type = BpmnFlowNodeTypes.SignalBoundaryEvent, AttachedToRef = 3, CancelActivity = true,
                Signal = new SignalEventModel { Name = c, OutputMappings = [] }
            },
            new FlowNodeModel { Id = 7, Name = "Stopped", Type = BpmnFlowNodeTypes.EndEvent }
        ]);
        model.SequenceFlows.AddRange(
        [
            new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 6, Name = "Done" },
            new SequenceFlowModel { Id = 601, SourceRef = 6, TargetRef = 4 },
            new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 7 }
        ]);

        var draft = await CreateDefinitionAsync(model, publish: false);
        await using (var db = fixture.CreateDbContext())
        {
            var rows = await db.WorkflowDefinitionSignalNames.AsNoTracking()
                .Where(row => row.WorkflowDefinitionId == draft.Id)
                .OrderBy(row => row.NodeId)
                .Select(row => new { row.NodeId, row.SignalName, row.Usage })
                .ToListAsync();
            Assert.Equal(
                [(2, a, "catch"), (4, a, "end"), (5, c, "boundary"), (6, b, "throw")],
                rows.Select(row => (row.NodeId, row.SignalName, row.Usage)));
        }

        using (var delete = await SendAsync(HttpMethod.Delete, $"/api/workflows/{draft.Id}"))
        {
            Assert.True(delete.IsSuccessStatusCode, await delete.Content.ReadAsStringAsync());
        }

        await using (var db = fixture.CreateDbContext())
        {
            Assert.False(await db.WorkflowDefinitionSignalNames.AnyAsync(row => row.WorkflowDefinitionId == draft.Id));
        }
    }

    [Fact]
    public async Task Backfill_EqualsTheRowsSavingWritesForEveryExampleWithSignalEvents()
    {
        var ids = new List<long>();
        foreach (var path in ExampleWorkflowData.RelativePaths)
        {
            var json = JsonNode.Parse(ExampleWorkflowData.Read(path))!.AsObject();
            var nodes = json["flowNodes"]?.AsArray() ?? [];
            if (!nodes.Any(node => SignalNameUsages.Of(node?["type"]?.GetValue<string>()) is not null))
            {
                continue;
            }

            json["id"] = $"catalog-backfill-{Guid.NewGuid():N}";
            using var response = await SendAsync(HttpMethod.Post, "/api/workflows",
                new JsonObject { ["definition"] = json, ["publish"] = false });
            ids.Add((await ReadAsync<WorkflowDetailDto>(response)).Id);
        }

        Assert.True(ids.Count >= 5, $"Only {ids.Count} examples with signal events were found.");
        await using var db = fixture.CreateDbContext();
        var written = await ReferenceRowsAsync(db, ids);
        Assert.NotEmpty(written);

        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.WorkflowDefinitionSignalNames.Where(row => ids.Contains(row.WorkflowDefinitionId)).ExecuteDeleteAsync();
        Assert.Empty(await ReferenceRowsAsync(db, ids));
        // Braces escaped: ExecuteSqlRaw formats its text, and the name pattern has {0,99}.
        await db.Database.ExecuteSqlRawAsync(AddSignalCatalog.BackfillSql.Replace("{", "{{").Replace("}", "}}"));
        var backfilled = await ReferenceRowsAsync(db, ids);
        await transaction.RollbackAsync();

        Assert.Equal(written, backfilled);
    }

    // ------------------------------------------------------------------ catalog writes

    [Fact]
    public async Task Entries_CreateReadUpdateArchiveAndReactivate_WithOptimisticConcurrency()
    {
        var name = UniqueName();
        SignalCatalogEntryDto created;
        using (var response = await SendAsync(HttpMethod.Post, "/api/signal-catalog/entries",
                   new CreateSignalCatalogEntryRequest(
                       name,
                       "  A supplier recalled a product.  ",
                       [new SignalCatalogFieldDto("reason", WorkflowVariableTypes.String, false, true, "  Why. ")],
                       [" recall-manager ", "Recall-Manager", "auditor"],
                       AllowClients: false)))
        {
            Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
            Assert.Equal($"/api/signal-catalog/entries/{name}", response.Headers.Location?.OriginalString);
            created = await ReadAsync<SignalCatalogEntryDto>(response);
        }

        Assert.Equal(
            (name, "A supplier recalled a product.", SignalCatalogStatuses.Active, false, true, "catalog-admin", (DateTimeOffset?)null),
            (created.Name, created.Description, created.Status, created.AllowClients, created.AllowWorkflows, created.CreatedBy, created.ArchivedAt));
        Assert.Equal(["recall-manager", "auditor"], created.ThrowRoles);
        Assert.Equal("Why.", Assert.Single(created.Fields).Description);
        Assert.False(created.CanThrow);

        using (var taken = await SendAsync(HttpMethod.Post, "/api/signal-catalog/entries", new CreateSignalCatalogEntryRequest(name)))
        {
            Assert.Equal(HttpStatusCode.Conflict, taken.StatusCode);
            Assert.Equal($"Signal catalog entry '{name}' already exists.", await ErrorAsync(taken));
        }

        using (var read = await SendAsync(HttpMethod.Get, $"/api/signal-catalog/entries/{name}"))
        {
            Assert.Equal(JsonSerializer.Serialize(created, JsonOptions), JsonSerializer.Serialize(await ReadAsync<SignalCatalogEntryDto>(read), JsonOptions));
        }

        using (var list = await SendAsync(HttpMethod.Get, $"/api/signal-catalog/entries?name={name[..12]}&status=active&pageSize=200"))
        {
            var page = await ReadAsync<SignalCatalogListDto>(list);
            Assert.Contains(page.Items, item => item.Name == name);
            Assert.All(page.Items, item => Assert.StartsWith(name[..12], item.Name, StringComparison.Ordinal));
            Assert.Equal(SignalCatalogModes.Optional, page.Mode);
            Assert.Contains("admin", page.SignalRoles);
        }

        var updated = await UpdateEntryAsync(created, [VersionNumber], roles: [], allowClients: true);
        Assert.True(updated.UpdatedAt > created.UpdatedAt);
        Assert.Equal(created.CreatedAt, updated.CreatedAt);
        Assert.Empty(updated.ThrowRoles);
        Assert.Null(updated.Description);
        Assert.True(updated.CanThrow);

        using (var stale = await SendAsync(HttpMethod.Put, $"/api/signal-catalog/entries/{name}",
                   Change(created.UpdatedAt, VersionNumber)))
        {
            Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            Assert.Equal($"Signal catalog entry '{name}' was changed by another request; reload and try again.", await ErrorAsync(stale));
        }

        var archived = await ArchiveAsync(updated);
        Assert.Equal(SignalCatalogStatuses.Archived, archived.Status);
        Assert.NotNull(archived.ArchivedAt);
        Assert.False(archived.CanThrow);
        using (var again = await SendAsync(HttpMethod.Post, $"/api/signal-catalog/entries/{name}/archive",
                   new SignalCatalogLifecycleRequest(updated.UpdatedAt)))
        {
            Assert.Equal(HttpStatusCode.OK, again.StatusCode);
            Assert.Equal(archived.UpdatedAt, (await ReadAsync<SignalCatalogEntryDto>(again)).UpdatedAt);
        }

        using (var archivedUpdate = await SendAsync(HttpMethod.Put, $"/api/signal-catalog/entries/{name}",
                   Change(archived.UpdatedAt)))
        {
            Assert.Equal(HttpStatusCode.Conflict, archivedUpdate.StatusCode);
            Assert.Equal($"Signal catalog entry '{name}' is archived; reactivate it before changing it.", await ErrorAsync(archivedUpdate));
        }

        using (var archivedList = await SendAsync(HttpMethod.Get, $"/api/signal-catalog/entries?name={name}&status=active"))
        {
            Assert.Empty((await ReadAsync<SignalCatalogListDto>(archivedList)).Items);
        }

        using (var staleReactivate = await SendAsync(HttpMethod.Post, $"/api/signal-catalog/entries/{name}/reactivate",
                   new SignalCatalogLifecycleRequest(updated.UpdatedAt)))
        {
            Assert.Equal(HttpStatusCode.Conflict, staleReactivate.StatusCode);
        }

        using (var reactivate = await SendAsync(HttpMethod.Post, $"/api/signal-catalog/entries/{name}/reactivate",
                   new SignalCatalogLifecycleRequest(archived.UpdatedAt)))
        {
            Assert.True(reactivate.IsSuccessStatusCode, await reactivate.Content.ReadAsStringAsync());
            var active = await ReadAsync<SignalCatalogEntryDto>(reactivate);
            Assert.Equal((SignalCatalogStatuses.Active, (DateTimeOffset?)null), (active.Status, active.ArchivedAt));
        }
    }

    [Fact]
    public async Task Entries_RefuseInvalidRequestsAndUnknownNames()
    {
        var name = UniqueName();
        using (var invalid = await SendAsync(HttpMethod.Post, "/api/signal-catalog/entries",
                   new CreateSignalCatalogEntryRequest(name, Fields: [new SignalCatalogFieldDto("bad.name", "string")])))
        {
            Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
            Assert.Equal("Field 1 name must be 1 to 100 letters, digits, '_', or '-', starting with a letter or '_'.", await ErrorAsync(invalid));
        }

        using (var strict = await SendAsync(HttpMethod.Post, "/api/signal-catalog/entries",
                   new Dictionary<string, object?> { ["name"] = name, ["alowClients"] = false }))
        {
            Assert.Equal(HttpStatusCode.BadRequest, strict.StatusCode);
        }

        using (var upper = await SendAsync(HttpMethod.Post, "/api/signal-catalog/entries", new CreateSignalCatalogEntryRequest("Upper.Case")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, upper.StatusCode);
        }

        using (var status = await SendAsync(HttpMethod.Get, "/api/signal-catalog/entries?status=deleted"))
        {
            Assert.Equal(HttpStatusCode.BadRequest, status.StatusCode);
        }

        foreach (var (method, path, body) in new (HttpMethod, string, object?)[]
                 {
                     (HttpMethod.Get, $"/api/signal-catalog/entries/{name}", null),
                     (HttpMethod.Put, $"/api/signal-catalog/entries/{name}", Change(DateTimeOffset.UtcNow)),
                     (HttpMethod.Post, $"/api/signal-catalog/entries/{name}/archive", new SignalCatalogLifecycleRequest(DateTimeOffset.UtcNow)),
                     (HttpMethod.Get, $"/api/signal-catalog/entries/{name}/usage", null)
                 })
        {
            using var response = await SendAsync(method, path, body);
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }
    }

    [Fact]
    public async Task Change_IsRefusedWithTheLiveVersionsNamed_AndAllowedWhenOnlyADraftUsesTheName()
    {
        var name = UniqueName();
        var entry = await CreateEntryAsync(name, [VersionNumber]);
        var live = await CreateDefinitionAsync(SignalRuntimeTests.CatchModel(name), publish: true);

        var stringVersion = new SignalCatalogFieldDto("version", WorkflowVariableTypes.String, false, true);
        using (var refused = await SendAsync(HttpMethod.Put, $"/api/signal-catalog/entries/{name}",
                   Change(entry.UpdatedAt, stringVersion)))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal(
                $"Signal catalog entry '{name}' does not fit live workflow versions: {live.WorkflowKey} v1 (#{live.Id}): "
                + $"Signal catch event #2 mapping to 'policyVersion' is number, but the catalog entry of '{name}' defines 'version' as string.",
                await ErrorAsync(refused));
        }

        // Descriptions, roles, and switches cannot break a live version.
        await UpdateEntryAsync(entry, [VersionNumber with { Description = "The policy version." }], roles: ["clerk"], allowClients: false);

        var draftOnly = UniqueName();
        var draftEntry = await CreateEntryAsync(draftOnly, [VersionNumber]);
        await CreateDefinitionAsync(SignalRuntimeTests.CatchModel(draftOnly), publish: false);
        await UpdateEntryAsync(draftEntry, [stringVersion]);
    }

    [Fact]
    public async Task Change_IsNotBlockedByAnotherNamesProblemInTheSameVersion()
    {
        var first = UniqueName();
        var second = UniqueName();
        var firstEntry = await CreateEntryAsync(first, [VersionNumber]);
        await CreateEntryAsync(second, [VersionNumber]);
        var model = SignalRuntimeTests.CatchModel(first);
        model.FlowNodes.Add(new FlowNodeModel
        {
            Id = 5, Name = "Stop", Type = BpmnFlowNodeTypes.SignalBoundaryEvent, AttachedToRef = 3, CancelActivity = true,
            Signal = new SignalEventModel { Name = second, OutputMappings = [SignalCatalogContractTests.Mapping("other", "version", WorkflowVariableTypes.Number)] }
        });
        model.SequenceFlows.Add(new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 4 });
        await CreateDefinitionAsync(model, publish: true);

        // Only possible by writing the row directly: the second name's entry no longer fits.
        await using (var db = fixture.CreateDbContext())
        {
            await db.SignalCatalogEntries.Where(entry => entry.Name == second).ExecuteUpdateAsync(setters =>
                setters.SetProperty(entry => entry.Fields,
                    JsonSerializer.SerializeToElement(new[] { new SignalCatalogFieldDto("version", "string", false, true) }, JsonOptions)));
        }

        await UpdateEntryAsync(firstEntry, [VersionNumber with { Description = "Changed." }]);
    }

    [Fact]
    public async Task Creation_ForANameInUseIsCheckedAgainstItsLiveVersions()
    {
        var name = UniqueName();
        await CreateDefinitionAsync(SignalRuntimeTests.CatchModel(name), publish: true);
        using (var refused = await SendAsync(HttpMethod.Post, "/api/signal-catalog/entries",
                   new CreateSignalCatalogEntryRequest(name, Fields: [new SignalCatalogFieldDto("revision", WorkflowVariableTypes.Number, false, true)])))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Contains("mapping to 'policyVersion' reads 'version', which is not a field", await ErrorAsync(refused), StringComparison.Ordinal);
        }

        await CreateEntryAsync(name, [VersionNumber]);
    }

    [Fact]
    public async Task Write_StopsAtTheFifthProblem_WhileThePreviewListsThemAll()
    {
        var name = UniqueName();
        var versions = new List<long>();
        for (var index = 0; index < 6; index++)
        {
            versions.Add((await CreateDefinitionAsync(SignalRuntimeTests.CatchModel(name), publish: true)).Id);
        }

        var candidate = new CreateSignalCatalogEntryRequest(name, Fields: [new SignalCatalogFieldDto("version", WorkflowVariableTypes.String, false, true)]);
        using (var preview = await SendAsync(HttpMethod.Post, "/api/signal-catalog/preview", candidate))
        {
            var impact = await ReadAsync<SignalCatalogImpactDto>(preview);
            Assert.Equal((name, 6, false), (impact.Name, impact.LiveVersionCount, impact.Truncated));
            Assert.Equal(versions, impact.Problems.Select(problem => problem.WorkflowDefinitionId));
            Assert.All(impact.Problems, problem => Assert.Equal(2, problem.NodeId));
        }

        using var refused = await SendAsync(HttpMethod.Post, "/api/signal-catalog/entries", candidate);
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        var message = await ErrorAsync(refused);
        Assert.Equal(5, message.Split("Signal catch event #2", StringSplitOptions.None).Length - 1);
        Assert.Contains($"(#{versions[4]})", message, StringComparison.Ordinal);
        Assert.DoesNotContain($"(#{versions[5]})", message, StringComparison.Ordinal);
        Assert.EndsWith("There are more problems; review the entry to see them all.", message, StringComparison.Ordinal);

        using var fitting = await SendAsync(HttpMethod.Post, "/api/signal-catalog/preview", new CreateSignalCatalogEntryRequest(name, Fields: [VersionNumber]));
        var clean = await ReadAsync<SignalCatalogImpactDto>(fitting);
        Assert.Equal((6, 0), (clean.LiveVersionCount, clean.Problems.Count));
    }

    [Fact]
    public async Task Archive_IsRefusedWhileTheNameIsUsed_WithEachCount()
    {
        var name = UniqueName();
        var entry = await CreateEntryAsync(name, [VersionNumber]);
        var definition = await CreateDefinitionAsync(SignalRuntimeTests.CatchModel(name), publish: true);
        var instance = await StartAsync(definition.Id);
        var broadcast = await ThrowAsync(new ThrowSignalRequest(name, definition.WorkflowKey,
            Payload: JsonSerializer.SerializeToElement(new { version = 3 })));

        var usage = await UsageAsync(name);
        Assert.Equal((1L, 1L, 1L, 0L, 1L), (usage.PublishedVersionCount, usage.RunningVersionCount, usage.ActiveWaitCount,
            usage.StartingFamilyCount, usage.DeliveringBroadcastCount));
        var use = Assert.Single(usage.Versions.Items);
        Assert.Equal((definition.Id, definition.WorkflowKey, 1, true), (use.WorkflowDefinitionId, use.WorkflowKey, use.Version, use.IsPublished));
        Assert.Equal([2], use.NodeIds);

        using (var refused = await SendAsync(HttpMethod.Post, $"/api/signal-catalog/entries/{name}/archive",
                   new SignalCatalogLifecycleRequest(entry.UpdatedAt)))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.Equal(
                $"Signal catalog entry '{name}' cannot be archived: 1 published workflow version uses it; "
                + "1 workflow version with running instances uses it; 1 signal wait is open for it; 1 broadcast of it is still delivering.",
                await ErrorAsync(refused));
        }

        await new SignalRuntimeTests(fixture).ProcessDeliveryJobsAsync(broadcast.Id);
        var after = await UsageAsync(name);
        Assert.Equal((1L, 1L, 0L, 0L), (after.PublishedVersionCount, after.RunningVersionCount, after.ActiveWaitCount, after.DeliveringBroadcastCount));
        Assert.NotEqual(0, instance.Id);

        var startName = UniqueName();
        var startEntry = await CreateEntryAsync(startName, [VersionNumber]);
        await CreateDefinitionAsync(StartModel(startName), publish: true);
        Assert.Equal(1L, (await UsageAsync(startName)).StartingFamilyCount);
        using (var refused = await SendAsync(HttpMethod.Post, $"/api/signal-catalog/entries/{startName}/archive",
                   new SignalCatalogLifecycleRequest(startEntry.UpdatedAt)))
        {
            Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
            Assert.EndsWith("1 published workflow version uses it; 1 workflow family starts on it.", await ErrorAsync(refused), StringComparison.Ordinal);
        }

        var draftName = UniqueName();
        var draftEntry = await CreateEntryAsync(draftName, [VersionNumber]);
        await CreateDefinitionAsync(SignalRuntimeTests.CatchModel(draftName), publish: false);
        var unused = await UsageAsync(draftName);
        Assert.Equal((0L, 0L, 0L, 0L, 0L, 1L), (unused.PublishedVersionCount, unused.RunningVersionCount, unused.ActiveWaitCount,
            unused.StartingFamilyCount, unused.DeliveringBroadcastCount, unused.Versions.TotalCount));
        await ArchiveAsync(draftEntry);
    }

    [Fact]
    public async Task Reactivating_AnEntryIsCheckedAgainstTheLiveVersions()
    {
        var name = UniqueName();
        var entry = await CreateEntryAsync(name, [VersionNumber]);
        var archived = await ArchiveAsync(entry);

        // Only possible by writing directly: a published version that the archived entry does not fit.
        var model = SignalRuntimeTests.CatchModel(name);
        model.FlowNodes.Single(node => node.Id == 2).Signal!.OutputMappings[0].DataType = WorkflowVariableTypes.String;
        await using (var db = fixture.CreateDbContext())
        {
            await db.SignalCatalogEntries.Where(row => row.Name == name).ExecuteDeleteAsync();
        }

        var published = await CreateDefinitionAsync(model, publish: true);
        await using (var db = fixture.CreateDbContext())
        {
            db.SignalCatalogEntries.Add(new SignalCatalogEntryEntity
            {
                Name = name,
                Fields = JsonSerializer.SerializeToElement(new[] { VersionNumber }, JsonOptions),
                ThrowRoles = JsonSerializer.SerializeToElement(Array.Empty<string>()),
                Status = SignalCatalogStatuses.Archived,
                CreatedAt = archived.CreatedAt,
                UpdatedAt = archived.UpdatedAt,
                ArchivedAt = archived.ArchivedAt
            });
            await db.SaveChangesAsync();
        }

        using var refused = await SendAsync(HttpMethod.Post, $"/api/signal-catalog/entries/{name}/reactivate",
            new SignalCatalogLifecycleRequest(archived.UpdatedAt));
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains($"(#{published.Id}): Signal catch event #2 mapping to 'policyVersion' is string", await ErrorAsync(refused), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private static string UniqueName() => $"catalog-test.{Guid.NewGuid():N}";

    internal static SignalPayloadEntryModel Send(string name, string? variable = null, string? source = null, bool required = false) =>
        new() { Name = name, Variable = variable, Source = source, Required = required };

    /// <summary>A whole change of an entry: no description, no roles, and both switches on.</summary>
    internal static UpdateSignalCatalogEntryRequest Change(DateTimeOffset expectedUpdatedAt, params SignalCatalogFieldDto[] fields) =>
        new(expectedUpdatedAt, null, [.. fields], [], true, true);

    /// <summary>
    /// Start (asking for why and skuList) to a throw event of the name to all workflows, then a
    /// review and an end. The throw is node 2.
    /// </summary>
    internal static WorkflowModel ThrowerModel(string name, params SignalPayloadEntryModel[] payload) => new()
    {
        Id = $"catalog-thrower-{Guid.NewGuid():N}",
        Name = "Catalog thrower",
        InitialEventId = 1,
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent,
                Variables =
                [
                    new VariableModel { Id = 1, Name = "why", DataType = WorkflowVariableTypes.String, Required = true },
                    new VariableModel { Id = 2, Name = "skuList", DataType = WorkflowVariableTypes.String, IsArray = true, Required = true }
                ]
            },
            new FlowNodeModel
            {
                Id = 2, Name = "Tell", Type = BpmnFlowNodeTypes.IntermediateSignalThrowEvent,
                Signal = new SignalEventModel
                {
                    Name = name,
                    Scope = new SignalThrowScopeModel { Kind = SignalThrowScopeKinds.AllWorkflows },
                    Payload = [.. payload]
                }
            },
            new FlowNodeModel { Id = 3, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel { Id = 4, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
            new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4, Name = "Done" }
        ]
    };

    private static WorkflowModel StartModel(string name) => new()
    {
        Id = $"catalog-start-{Guid.NewGuid():N}",
        Name = "Catalog start",
        FlowNodes =
        [
            new FlowNodeModel
            {
                Id = 1, Name = "On signal", Type = BpmnFlowNodeTypes.SignalStartEvent,
                Signal = new SignalEventModel
                {
                    Name = name,
                    OutputMappings = [SignalCatalogContractTests.Mapping("policyVersion", "version", WorkflowVariableTypes.Number, required: true)]
                }
            },
            new FlowNodeModel { Id = 2, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
            new FlowNodeModel { Id = 3, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3, Name = "Done" }
        ]
    };

    private static async Task<List<(long, int, string, string)>> ReferenceRowsAsync(
        Flowbit.Infrastructure.Data.AppDbContext db,
        List<long> ids) =>
        (await db.WorkflowDefinitionSignalNames.AsNoTracking()
            .Where(row => ids.Contains(row.WorkflowDefinitionId))
            .OrderBy(row => row.WorkflowDefinitionId).ThenBy(row => row.NodeId)
            .Select(row => new { row.WorkflowDefinitionId, row.NodeId, row.SignalName, row.Usage })
            .ToListAsync())
        .Select(row => (row.WorkflowDefinitionId, row.NodeId, row.SignalName, row.Usage))
        .ToList();

    private void AssertNoCatalogStatement()
    {
        var statements = fixture.CommandCounter.ReaderCommandTexts.Concat(fixture.CommandCounter.NonQueryCommandTexts).ToList();
        Assert.NotEmpty(statements);
        Assert.DoesNotContain(statements, text =>
            text.Contains("signal_catalog_entries", StringComparison.Ordinal)
            || text.Contains("signal-name", StringComparison.Ordinal)
            || text.Contains("workflow_definition_signal_names", StringComparison.Ordinal));
    }

    private async Task<SignalCatalogEntryDto> CreateEntryAsync(
        string name,
        IReadOnlyList<SignalCatalogFieldDto> fields,
        IReadOnlyList<string>? roles = null,
        bool allowClients = true,
        bool allowWorkflows = true)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/signal-catalog/entries",
            new CreateSignalCatalogEntryRequest(name, null, [.. fields], roles?.Cast<string?>().ToList(), allowClients, allowWorkflows));
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await ReadAsync<SignalCatalogEntryDto>(response);
    }

    private async Task<SignalCatalogEntryDto> UpdateEntryAsync(
        SignalCatalogEntryDto entry,
        IReadOnlyList<SignalCatalogFieldDto> fields,
        IReadOnlyList<string>? roles = null,
        bool allowClients = true,
        bool allowWorkflows = true)
    {
        using var response = await SendAsync(HttpMethod.Put, $"/api/signal-catalog/entries/{entry.Name}",
            new UpdateSignalCatalogEntryRequest(entry.UpdatedAt, null, [.. fields], roles?.Cast<string?>().ToList(), allowClients, allowWorkflows));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await ReadAsync<SignalCatalogEntryDto>(response);
    }

    private async Task<SignalCatalogEntryDto> ArchiveAsync(SignalCatalogEntryDto entry)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/api/signal-catalog/entries/{entry.Name}/archive",
            new SignalCatalogLifecycleRequest(entry.UpdatedAt));
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await ReadAsync<SignalCatalogEntryDto>(response);
    }

    private async Task<SignalCatalogUsageDto> UsageAsync(string name)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/signal-catalog/entries/{name}/usage");
        return await ReadAsync<SignalCatalogUsageDto>(response);
    }

    private async Task<HttpResponseMessage> PostDefinitionAsync(WorkflowModel model, bool publish) =>
        await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, publish));

    private async Task<WorkflowDetailDto> CreateDefinitionAsync(WorkflowModel model, bool publish)
    {
        using var response = await PostDefinitionAsync(model, publish);
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await ReadAsync<WorkflowDetailDto>(response);
    }

    private async Task<InstanceDetailDto> StartAsync(long workflowId, Dictionary<string, JsonElement>? variables = null)
    {
        using var start = await SendAsync(HttpMethod.Post, "/api/instances?detail=full",
            new StartInstanceRequest(workflowId, null, null, variables));
        Assert.True(start.StatusCode == HttpStatusCode.Created, await start.Content.ReadAsStringAsync());
        return await ReadAsync<InstanceDetailDto>(start);
    }

    private async Task<SignalBroadcastDetailDto> ThrowAsync(ThrowSignalRequest request, string user = "catalog-admin", string[]? roles = null)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts", request, user, roles);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return await ReadAsync<SignalBroadcastDetailDto>(response);
    }

    private async Task<string?> SetEngineSettingAsync(string settingNamespace, string key, string? value)
    {
        await using var db = fixture.CreateDbContext();
        var setting = await db.EngineSettings.SingleOrDefaultAsync(item =>
            item.Namespace == settingNamespace && item.Key == key);
        var previous = setting?.Value;
        if (value is null)
        {
            if (setting is not null) db.EngineSettings.Remove(setting);
        }
        else if (setting is null)
        {
            db.EngineSettings.Add(new EngineSettingEntity { Namespace = settingNamespace, Key = key, Value = value });
        }
        else
        {
            setting.Value = value;
        }

        await db.SaveChangesAsync();
        return previous;
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "catalog-admin",
        string[]? roles = null,
        bool suppressAdmin = false)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        ApiTestAuth.Authorize(request, user, roles ?? []);
        if (suppressAdmin)
        {
            request.Headers.TryAddWithoutValidation("X-Test-Suppress-Admin", "true");
        }

        return await fixture.Client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendAsClientAsync(
        HttpMethod method, string path, string clientId, string secret, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }

        request.Headers.Add("X-Client-Id", clientId);
        request.Headers.Add("X-Client-Secret", secret);
        return await fixture.Client.SendAsync(request);
    }

    private async Task<(string Id, string Secret)> CreateClientAsync(params string[] scopes)
    {
        var clientId = $"catalog-client-{Guid.NewGuid():N}"[..40];
        using var create = await SendAsync(HttpMethod.Post, "/api/shared-variable-clients",
            new CreateSharedVariableClientRequest(clientId, "Catalog test client", scopes));
        Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        return (clientId, (await create.Content.ReadFromJsonAsync<CreateSharedVariableClientResult>(JsonOptions))!.ClientSecret);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response)
    {
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {await response.Content.ReadAsStringAsync()}");
        return await response.Content.ReadFromJsonAsync<T>(JsonOptions)
            ?? throw new InvalidOperationException("Response body was empty.");
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(text);
        return document.RootElement.GetProperty("error").GetString()!;
    }
}
