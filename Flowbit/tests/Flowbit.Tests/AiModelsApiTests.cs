using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

/// <summary>The AI models API over PostgreSQL: settings-administrator access, write-only keys, and the hidden engine-settings row.</summary>
[Collection(PostgresApiCollection.Name)]
public sealed class AiModelsApiTests(PostgresApiFixture fixture)
{
    private const string Key = "sk-api-test-provider-key-0123456789";
    private const string RotatedKey = "sk-api-rotated-provider-key-9876543210";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task ManagingModelsRequiresTheSettingsAdministratorRole()
    {
        using (var anonymous = await fixture.Client.GetAsync("/api/ai-models"))
            Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        using (var viewer = await SendAsync(HttpMethod.Get, "/api/ai-models", roles: ["viewer"], suppressAdmin: true))
            Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);
        using (var viewer = await SendAsync(HttpMethod.Post, "/api/ai-models",
                   new CreateAiModelRequest("Flash", "https://opencode.example/zen/v1", "glm-5.3-flash", "high", Key), roles: ["viewer"], suppressAdmin: true))
            Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);
    }

    [Fact]
    public async Task ModelsAreManagedWithWriteOnlyKeysAndHiddenFromEngineSettings()
    {
        await RemoveModelsRowAsync();
        try
        {
            AiModelSettingDto model;
            using (var created = await SendAsync(HttpMethod.Post, "/api/ai-models",
                       new CreateAiModelRequest(" Flash ", "https://opencode.example/zen/v1", "glm-5.3-flash", "high", Key)))
            {
                var body = await created.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.Created, created.StatusCode);
                Assert.DoesNotContain(Key, body);
                Assert.DoesNotContain("apiKey", body);
                model = JsonSerializer.Deserialize<AiModelSettingDto>(body, JsonOptions)!;
                Assert.Equal($"/api/ai-models/{model.Id}", created.Headers.Location?.OriginalString);
                Assert.True(created.Headers.CacheControl!.NoStore);
            }
            Assert.Equal(("Flash", "https://opencode.example/zen/v1/", "glm-5.3-flash", "high", 1L),
                (model.Name, model.Endpoint, model.Model, model.ReasoningEffort, model.Revision));

            // The models are one JSON array in engine_settings, keys included.
            var row = await ReadModelsRowAsync();
            Assert.Equal(Key, StoredKey(row.Value));

            // The generic engine-settings API neither shows nor changes the row.
            using (var list = await SendAsync(HttpMethod.Get, "/api/engine-settings"))
            {
                var body = await list.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.OK, list.StatusCode);
                Assert.DoesNotContain("AiAuthoring", body);
                Assert.DoesNotContain(Key, body);
            }
            using (var generic = await SendAsync(HttpMethod.Post, "/api/engine-settings", new CreateEngineSettingRequest("aiauthoring", "models", "[]")))
                Assert.Equal(HttpStatusCode.BadRequest, generic.StatusCode);
            using (var generic = await SendAsync(HttpMethod.Put, $"/api/engine-settings/{row.Id}", new UpdateEngineSettingRequest("[]", null, row.UpdatedAt)))
                Assert.Equal(HttpStatusCode.BadRequest, generic.StatusCode);
            using (var generic = await SendAsync(HttpMethod.Delete,
                       $"/api/engine-settings/{row.Id}?expectedUpdatedAt={Uri.EscapeDataString(row.UpdatedAt.ToString("O", CultureInfo.InvariantCulture))}"))
                Assert.Equal(HttpStatusCode.BadRequest, generic.StatusCode);
            Assert.Equal(row.Value, (await ReadModelsRowAsync()).Value);

            // The assistant lists ids and names only, and refuses a model that is not configured.
            using (var assistant = await SendAsync(HttpMethod.Get, "/api/workflows/ai/models"))
            {
                var body = await assistant.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.OK, assistant.StatusCode);
                Assert.Equal(new AiModelDto(model.Id, "Flash"), Assert.Single(JsonSerializer.Deserialize<List<AiModelDto>>(body, JsonOptions)!));
                Assert.DoesNotContain("opencode.example", body);
                Assert.DoesNotContain(Key, body);
            }
            using (var turn = await SendAsync(HttpMethod.Post, "/api/workflows/ai/turn", new AiTurnRequestDto
                   { ModelId = "not-configured", ConversationId = Guid.NewGuid().ToString(), Message = "Build a review", SnapshotId = "snapshot-1" }))
            {
                Assert.Equal(HttpStatusCode.BadRequest, turn.StatusCode);
                Assert.Contains("unsupported_model", await turn.Content.ReadAsStringAsync());
            }

            // A blank key keeps the stored key; a new key replaces it. Neither is returned.
            using (var renamed = await SendAsync(HttpMethod.Put, $"/api/ai-models/{model.Id}",
                       new UpdateAiModelRequest("Flash renamed", model.Endpoint, model.Model, null, null, model.Revision)))
            {
                Assert.Equal(HttpStatusCode.OK, renamed.StatusCode);
                model = (await renamed.Content.ReadFromJsonAsync<AiModelSettingDto>(JsonOptions))!;
            }
            Assert.Equal(("Flash renamed", (string?)null, 2L), (model.Name, model.ReasoningEffort, model.Revision));
            Assert.Equal(Key, StoredKey((await ReadModelsRowAsync()).Value));
            using (var rekeyed = await SendAsync(HttpMethod.Put, $"/api/ai-models/{model.Id}",
                       new UpdateAiModelRequest(model.Name, model.Endpoint, model.Model, "low", RotatedKey, model.Revision)))
            {
                var body = await rekeyed.Content.ReadAsStringAsync();
                Assert.Equal(HttpStatusCode.OK, rekeyed.StatusCode);
                Assert.DoesNotContain(RotatedKey, body);
                model = JsonSerializer.Deserialize<AiModelSettingDto>(body, JsonOptions)!;
            }
            Assert.Equal(3L, model.Revision);
            Assert.Equal(RotatedKey, StoredKey((await ReadModelsRowAsync()).Value));

            // Validation, duplicates, stale revisions and unknown models.
            using (var invalid = await SendAsync(HttpMethod.Post, "/api/ai-models",
                       new CreateAiModelRequest("Remote", "http://remote.example/v1", "m", null, Key)))
            {
                Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
                Assert.DoesNotContain(Key, await invalid.Content.ReadAsStringAsync());
            }
            using (var duplicate = await SendAsync(HttpMethod.Post, "/api/ai-models",
                       new CreateAiModelRequest("FLASH RENAMED", "https://other.example/v1", "m", null, Key)))
                Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
            using (var stale = await SendAsync(HttpMethod.Put, $"/api/ai-models/{model.Id}",
                       new UpdateAiModelRequest("Stale", model.Endpoint, model.Model, null, null, 1)))
                Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
            using (var unknown = await SendAsync(HttpMethod.Put, "/api/ai-models/unknown",
                       new UpdateAiModelRequest("Unknown", model.Endpoint, model.Model, null, null, 1)))
                Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            using (var unknown = await SendAsync(HttpMethod.Delete, "/api/ai-models/unknown?expectedRevision=1"))
                Assert.Equal(HttpStatusCode.NotFound, unknown.StatusCode);
            using (var stale = await SendAsync(HttpMethod.Delete, $"/api/ai-models/{model.Id}?expectedRevision=1"))
                Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

            using (var deleted = await SendAsync(HttpMethod.Delete, $"/api/ai-models/{model.Id}?expectedRevision={model.Revision}"))
                Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);
            using (var list = await SendAsync(HttpMethod.Get, "/api/ai-models"))
                Assert.Empty((await list.Content.ReadFromJsonAsync<List<AiModelSettingDto>>(JsonOptions))!);
            Assert.Equal("[]", (await ReadModelsRowAsync()).Value);
        }
        finally
        {
            await RemoveModelsRowAsync();
        }
    }

    private async Task<(long Id, string Value, DateTimeOffset UpdatedAt)> ReadModelsRowAsync()
    {
        await using var db = fixture.CreateDbContext();
        var row = await db.EngineSettings.AsNoTracking().SingleAsync(setting => setting.Namespace == "AiAuthoring" && setting.Key == "Models");
        return (row.Id, row.Value, row.UpdatedAt);
    }

    private async Task RemoveModelsRowAsync()
    {
        await using var db = fixture.CreateDbContext();
        await db.EngineSettings.Where(setting => setting.Namespace == "AiAuthoring" && setting.Key == "Models").ExecuteDeleteAsync();
    }

    private static string? StoredKey(string value)
    {
        using var stored = JsonDocument.Parse(value);
        return Assert.Single(stored.RootElement.EnumerateArray()).GetProperty("apiKey").GetString();
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null,
        string user = "settings-admin", string[]? roles = null, bool suppressAdmin = false)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null) request.Content = JsonContent.Create(body, options: JsonOptions);
        ApiTestAuth.Authorize(request, user, roles ?? []);
        if (suppressAdmin) request.Headers.TryAddWithoutValidation("X-Test-Suppress-Admin", "true");
        return await fixture.Client.SendAsync(request);
    }
}
