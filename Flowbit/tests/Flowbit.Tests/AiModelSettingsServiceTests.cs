using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Ai;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Flowbit.Tests;

/// <summary>The AI models kept as one engine-setting array: write-only keys, per-model revisions and the row's optimistic check.</summary>
public sealed class AiModelSettingsServiceTests
{
    private const string Key = "sk-service-test-provider-key-0123456789";

    [Fact]
    public async Task TheFirstModelCreatesTheRowAndOnlyTheRuntimeSeesItsKey()
    {
        var repository = new InMemoryEngineSettings();
        var service = Service(repository);

        var created = await service.CreateAsync(Create(" Flash "), CancellationToken.None);

        Assert.Equal(("Flash", "https://opencode.example/zen/v1/", "glm-5.3-flash", "high", 1L),
            (created.Name, created.Endpoint, created.Model, created.ReasoningEffort, created.Revision));
        Assert.Equal(32, created.Id.Length);
        var row = repository.Row!;
        Assert.Equal(("AiAuthoring", "Models", AiModelSettingRules.SettingDescription), (row.Namespace, row.Key, row.Description));
        using (var stored = JsonDocument.Parse(row.Value))
            Assert.Equal(Key, Assert.Single(stored.RootElement.EnumerateArray()).GetProperty("apiKey").GetString());
        var listed = await service.ListAsync(CancellationToken.None);
        Assert.Equal(created, Assert.Single(listed));
        Assert.DoesNotContain(Key, JsonSerializer.Serialize(listed));
        var connection = Assert.Single(await service.GetConnectionsAsync(CancellationToken.None));
        Assert.Equal((created.Id, "glm-5.3-flash", Key), (connection.Id, connection.Model, connection.ApiKey));
    }

    [Fact]
    public async Task ABlankKeyKeepsTheStoredKeyAndANewKeyReplacesIt()
    {
        var service = Service(new InMemoryEngineSettings());
        var created = await service.CreateAsync(Create("Flash"), CancellationToken.None);

        var renamed = await service.UpdateAsync(created.Id, Update("Flash renamed", created.Revision, apiKey: "  "), CancellationToken.None);
        Assert.Equal(("Flash renamed", 2L, (string?)null), (renamed!.Name, renamed.Revision, renamed.ReasoningEffort));
        Assert.Equal(Key, Assert.Single(await service.GetConnectionsAsync(CancellationToken.None)).ApiKey);

        const string rotated = "sk-rotated-service-key-9876543210";
        var rekeyed = await service.UpdateAsync(created.Id, Update("Flash renamed", renamed.Revision, apiKey: rotated), CancellationToken.None);
        Assert.Equal(3L, rekeyed!.Revision);
        Assert.Equal(rotated, Assert.Single(await service.GetConnectionsAsync(CancellationToken.None)).ApiKey);
    }

    [Fact]
    public async Task StaleRevisionsDuplicateNamesAndUnknownModelsAreRefused()
    {
        var repository = new InMemoryEngineSettings();
        var service = Service(repository);
        var created = await service.CreateAsync(Create("Flash"), CancellationToken.None);
        var updated = await service.UpdateAsync(created.Id, Update("Flash", created.Revision), CancellationToken.None);
        var writes = repository.Writes;

        await Assert.ThrowsAsync<WorkflowConflictException>(() => service.UpdateAsync(created.Id, Update("Stale", created.Revision), CancellationToken.None));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => service.DeleteAsync(created.Id, created.Revision, CancellationToken.None));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => service.CreateAsync(Create("FLASH"), CancellationToken.None));
        await Assert.ThrowsAsync<WorkflowDomainException>(() => service.CreateAsync(Create("Other") with { ApiKey = "" }, CancellationToken.None));
        await Assert.ThrowsAsync<WorkflowDomainException>(() => service.UpdateAsync(created.Id, Update("Flash", 0), CancellationToken.None));
        Assert.Null(await service.UpdateAsync("unknown", Update("Other", 1), CancellationToken.None));
        Assert.False(await service.DeleteAsync("unknown", 1, CancellationToken.None));
        Assert.Equal(writes, repository.Writes);

        Assert.True(await service.DeleteAsync(created.Id, updated!.Revision, CancellationToken.None));
        Assert.Empty(await service.ListAsync(CancellationToken.None));
        Assert.Equal("[]", repository.Row!.Value);
    }

    [Fact]
    public async Task AWriteThatLosesTheRowRaceIsAppliedAgainToTheLatestList()
    {
        var repository = new InMemoryEngineSettings();
        var service = Service(repository);
        var first = await service.CreateAsync(Create("First"), CancellationToken.None);
        var interfered = false;
        repository.BeforeWrite = () =>
        {
            if (interfered) return;
            interfered = true;
            // Another administrator adds a model after this write read the row.
            Service(repository).CreateAsync(Create("Second"), CancellationToken.None).GetAwaiter().GetResult();
        };

        var renamed = await service.UpdateAsync(first.Id, Update("First renamed", first.Revision), CancellationToken.None);

        Assert.Equal(2L, renamed!.Revision);
        Assert.Equal(["First renamed", "Second"], (await service.ListAsync(CancellationToken.None)).Select(model => model.Name));
    }

    [Fact]
    public async Task ARowThatKeepsChangingEndsInAConflict()
    {
        var repository = new InMemoryEngineSettings();
        var service = Service(repository);
        var created = await service.CreateAsync(Create("Flash"), CancellationToken.None);
        repository.BeforeWrite = () => repository.Row = repository.Row! with { UpdatedAt = repository.Row.UpdatedAt.AddTicks(10) };

        var error = await Assert.ThrowsAsync<WorkflowConflictException>(() =>
            service.UpdateAsync(created.Id, Update("Never saved", created.Revision), CancellationToken.None));

        Assert.Contains("reload", error.Message);
        Assert.Equal(3, repository.Attempts - 1);
    }

    [Fact]
    public async Task ACorruptStoredListFailsClosed()
    {
        var repository = new InMemoryEngineSettings
        {
            Row = new(7, "AiAuthoring", "Models", "{\"not\":\"an array\"}", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow)
        };
        var service = Service(repository);

        await Assert.ThrowsAsync<WorkflowConflictException>(() => service.ListAsync(CancellationToken.None));
        await Assert.ThrowsAsync<WorkflowConflictException>(() => service.CreateAsync(Create("Flash"), CancellationToken.None));
        var runtime = await Assert.ThrowsAsync<WorkflowAiException>(() => service.GetConnectionsAsync(CancellationToken.None));
        Assert.Equal(("provider_configuration", 503), (runtime.Code, runtime.StatusCode));
    }

    [Fact]
    public async Task TheEngineSettingsServiceHidesAndRefusesTheModelsRow()
    {
        var repository = new InMemoryEngineSettings();
        await Service(repository).CreateAsync(Create("Flash"), CancellationToken.None);
        var reserved = repository.Row!;
        repository.Others.Add(new(2, "Settings", "RequiredRole", "admin", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var settings = new EngineSettingsService(repository, NullLogger<EngineSettingsService>.Instance);

        Assert.Equal(["RequiredRole"], (await settings.ListAsync(CancellationToken.None)).Select(setting => setting.Key));
        Assert.Equal(["RequiredRole"], (await settings.SearchAsync("%", CancellationToken.None)).Select(setting => setting.Key));
        await Assert.ThrowsAsync<WorkflowDomainException>(() => settings.CreateAsync(" aiauthoring ", " MODELS ", "[]", null, CancellationToken.None));
        await Assert.ThrowsAsync<WorkflowDomainException>(() => settings.CreateAsync(null, "AiAuthoring.Models", "[]", null, CancellationToken.None));
        await Assert.ThrowsAsync<WorkflowDomainException>(() => settings.UpdateAsync(reserved.Id, "[]", null, reserved.UpdatedAt, CancellationToken.None));
        await Assert.ThrowsAsync<WorkflowDomainException>(() => settings.DeleteByIdAsync(reserved.Id, reserved.UpdatedAt, CancellationToken.None));
        await Assert.ThrowsAsync<WorkflowDomainException>(() => settings.SetAsync("AiAuthoring.Models", "[]", CancellationToken.None));
        await Assert.ThrowsAsync<WorkflowDomainException>(() => settings.DeleteAsync("AiAuthoring.Models", CancellationToken.None));
        Assert.Equal(reserved, repository.Row);
    }

    private static AiModelSettingsService Service(InMemoryEngineSettings repository) =>
        new(repository, TimeProvider.System, NullLogger<AiModelSettingsService>.Instance);

    private static CreateAiModelRequest Create(string name) => new(name, "https://opencode.example/zen/v1", "glm-5.3-flash", "high", Key);

    private static UpdateAiModelRequest Update(string name, long expectedRevision, string? apiKey = null) =>
        new(name, "https://opencode.example/zen/v1/", "glm-5.3-flash", null, apiKey, expectedRevision);

    /// <summary>One engine-settings row with the repository's optimistic check, plus other rows for the engine-settings guard.</summary>
    private sealed class InMemoryEngineSettings : IEngineSettingsRepository
    {
        public EngineSettingRecord? Row { get; set; }
        public List<EngineSettingRecord> Others { get; } = [];
        public Action? BeforeWrite { get; set; }
        public int Writes { get; private set; }
        public int Attempts { get; private set; }

        private IEnumerable<EngineSettingRecord> All => Row is null ? Others : [.. Others, Row];

        public Task<EngineSettingRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken) =>
            Task.FromResult(All.FirstOrDefault(setting => (setting.Namespace is null ? setting.Key : setting.Namespace + "." + setting.Key) == key));

        public Task<IReadOnlyList<EngineSettingRecord>> SearchAsync(string pattern, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EngineSettingRecord>>(All.ToList());

        public Task<IReadOnlyList<EngineSettingRecord>> ListAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<EngineSettingRecord>>(All.ToList());

        public Task<EngineSettingRecord?> GetByIdAsync(long id, CancellationToken cancellationToken) =>
            Task.FromResult(All.FirstOrDefault(setting => setting.Id == id));

        public Task<EngineSettingRecord> SetAsync(string key, string value, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The engine-settings guard must refuse this write.");

        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("The engine-settings guard must refuse this write.");

        public Task<EngineSettingRecord> CreateAsync(string? settingNamespace, string key, string value, string? description,
            CancellationToken cancellationToken)
        {
            Attempts++;
            BeforeWrite?.Invoke();
            if (Row is not null) throw new WorkflowConflictException("Engine setting 'AiAuthoring.Models' already exists.");
            Writes++;
            var now = DateTimeOffset.UtcNow;
            Row = new(1, settingNamespace, key, value, now, now, description);
            return Task.FromResult(Row);
        }

        public Task<EngineSettingRecord?> UpdateAsync(long id, string value, string? description, DateTimeOffset expectedUpdatedAt,
            CancellationToken cancellationToken)
        {
            Attempts++;
            BeforeWrite?.Invoke();
            if (Row is null || Row.Id != id) return Task.FromResult<EngineSettingRecord?>(null);
            if (Row.UpdatedAt != expectedUpdatedAt) throw new WorkflowConflictException("Engine setting 1 was changed by another request.");
            Writes++;
            Row = Row with { Value = value, Description = description, UpdatedAt = Row.UpdatedAt.AddTicks(10) };
            return Task.FromResult<EngineSettingRecord?>(Row);
        }
    }
}
