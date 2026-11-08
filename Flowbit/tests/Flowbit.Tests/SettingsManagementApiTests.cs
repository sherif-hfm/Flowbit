using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Api.Auth;
using Flowbit.Service.Abstractions;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class SettingsManagementApiTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("/api/engine-settings")]
    [InlineData("/api/workflow-settings")]
    public async Task SettingsManagementRequiresAuthentication(string path)
    {
        using var response = await fixture.Client.GetAsync(path);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Theory]
    [InlineData("/api/engine-settings")]
    [InlineData("/api/workflow-settings")]
    public async Task SettingsManagementRejectsActorsWithoutTheRequiredRole(string path)
    {
        using var response = await SendAsync(
            HttpMethod.Get,
            path,
            user: "settings-viewer",
            roles: ["viewer"],
            suppressAdmin: true);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task EngineSettingsCrudPreservesIdentifiersAndEnforcesOptimisticConcurrency()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var settingNamespace = $"api-engine-{suffix}";
        const string key = "ExecutionMode";
        long? createdId = null;

        try
        {
            EngineSettingDto created;
            using (var response = await SendAsync(
                       HttpMethod.Post,
                       "/api/engine-settings",
                       new CreateEngineSettingRequest(
                           $"  {settingNamespace}  ",
                           $"  {key}  ",
                           "  initial  ",
                           "  Initial engine setting description.  ")))
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                Assert.Equal(
                    $"/api/engine-settings/{await ReadIdAsync(response)}",
                    response.Headers.Location?.OriginalString);
                created = await ReadAsync<EngineSettingDto>(response);
            }

            createdId = created.Id;
            Assert.Equal(settingNamespace, created.Namespace);
            Assert.Equal(key, created.Key);
            Assert.Equal("  initial  ", created.Value);
            Assert.Equal("Initial engine setting description.", created.Description);
            Assert.Equal(created.CreatedAt, created.UpdatedAt);
            Assert.Equal("settings-admin", created.CreatedBy);
            Assert.Equal("settings-admin", created.UpdatedBy);

            using (var response = await SendAsync(HttpMethod.Get, "/api/engine-settings"))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var listed = await ReadAsync<List<EngineSettingDto>>(response);
                var item = Assert.Single(listed, setting => setting.Id == created.Id);
                Assert.Equal(created, item);
            }

            EngineSettingDto updated;
            using (var response = await SendAsync(
                       HttpMethod.Put,
                       $"/api/engine-settings/{created.Id}",
                       new Dictionary<string, object?>
                       {
                           ["namespace"] = "attempted-rename",
                           ["key"] = "AttemptedRename",
                           ["value"] = "  updated  ",
                           ["description"] = "  Updated engine setting description.  ",
                           ["createdBy"] = "forged-creator",
                           ["updatedBy"] = "forged-editor",
                           ["expectedUpdatedAt"] = created.UpdatedAt
                       }, user: "settings-editor"))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                updated = await ReadAsync<EngineSettingDto>(response);
            }

            Assert.Equal(created.Id, updated.Id);
            Assert.Equal(created.Namespace, updated.Namespace);
            Assert.Equal(created.Key, updated.Key);
            Assert.Equal("  updated  ", updated.Value);
            Assert.Equal("Updated engine setting description.", updated.Description);
            Assert.True(updated.UpdatedAt > created.UpdatedAt);
            Assert.Equal("settings-admin", updated.CreatedBy);
            Assert.Equal("settings-editor", updated.UpdatedBy);

            using (var response = await SendAsync(
                       HttpMethod.Put,
                       $"/api/engine-settings/{created.Id}",
                       new UpdateEngineSettingRequest(
                           "stale-update",
                           "This update must not win.",
                           created.UpdatedAt)))
            {
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            }

            using (var response = await SendAsync(HttpMethod.Get, "/api/engine-settings"))
            {
                var listed = await ReadAsync<List<EngineSettingDto>>(response);
                Assert.Equal(updated, Assert.Single(listed, setting => setting.Id == updated.Id));
            }

            using (var response = await SendAsync(
                       HttpMethod.Delete,
                       DeletePath("/api/engine-settings", created.Id, created.UpdatedAt)))
            {
                Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
            }

            var missingId = long.MaxValue;
            var missingTimestamp = DateTimeOffset.UtcNow;
            using (var response = await SendAsync(
                       HttpMethod.Put,
                       $"/api/engine-settings/{missingId}",
                       new UpdateEngineSettingRequest(
                           "missing",
                           null,
                           missingTimestamp)))
            {
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }

            using (var response = await SendAsync(
                       HttpMethod.Delete,
                       DeletePath("/api/engine-settings", missingId, missingTimestamp)))
            {
                Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            }

            using (var response = await SendAsync(
                       HttpMethod.Delete,
                       DeletePath("/api/engine-settings", updated.Id, updated.UpdatedAt)))
            {
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            }

            createdId = null;
        }
        finally
        {
            await DeleteEngineTestRowsAsync(settingNamespace, createdId);
        }
    }

    [Fact]
    public async Task EngineSettingCanonicalIdentifierConflictsWithLegacyDottedKey()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var settingNamespace = $"api-legacy-{suffix}";
        const string key = "Limit";
        long? legacyId = null;

        try
        {
            using (var response = await SendAsync(
                       HttpMethod.Post,
                       "/api/engine-settings",
                       new CreateEngineSettingRequest(
                           null,
                           $"{settingNamespace}.{key}",
                           "legacy-value",
                           "Legacy dotted-key representation.")))
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                legacyId = (await ReadAsync<EngineSettingDto>(response)).Id;
            }

            using var duplicate = await SendAsync(
                HttpMethod.Post,
                "/api/engine-settings",
                new CreateEngineSettingRequest(
                    settingNamespace,
                    key,
                    "canonical-value",
                    "Canonical representation."));

            Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
        }
        finally
        {
            await DeleteEngineTestRowsAsync(settingNamespace, legacyId);
        }
    }

    [Fact]
    public async Task WorkflowSettingsCrudPreservesEveryJsonRootTypeAndRejectsCaseInsensitiveDuplicates()
    {
        var settingNamespace = $"api-workflow-{Guid.NewGuid():N}";
        var inputs = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["value-string"] = Json("\"typed value\""),
            ["value-number"] = Json("12.5"),
            ["value-boolean"] = Json("true"),
            ["value-null"] = Json("null"),
            ["value-array"] = Json("""[1,"two",false,null,{"nested":true}]"""),
            ["value-object"] = Json(
                """{"name":"policy","limit":3,"enabled":true,"fallback":null,"items":[1,"two"]}""")
        };
        var created = new Dictionary<string, WorkflowSettingDto>(StringComparer.Ordinal);

        try
        {
            foreach (var input in inputs)
            {
                using var response = await SendAsync(
                    HttpMethod.Post,
                    "/api/workflow-settings",
                    new CreateWorkflowSettingRequest(
                        settingNamespace,
                        input.Key,
                        input.Value,
                        $"  Description for {input.Key}.  "));
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);

                var item = await ReadAsync<WorkflowSettingDto>(response);
                created.Add(input.Key, item);
                Assert.Equal(settingNamespace, item.Namespace);
                Assert.Equal(input.Key, item.Name);
                Assert.Equal($"Description for {input.Key}.", item.Description);
                AssertJsonEqual(input.Value, item.Value);
            }

            using (var response = await SendAsync(HttpMethod.Get, "/api/workflow-settings"))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var listed = (await ReadAsync<List<WorkflowSettingDto>>(response))
                    .Where(setting => setting.Namespace == settingNamespace)
                    .ToDictionary(setting => setting.Name, StringComparer.Ordinal);
                Assert.Equal(inputs.Count, listed.Count);
                foreach (var input in inputs)
                {
                    AssertJsonEqual(input.Value, listed[input.Key].Value);
                    Assert.Equal(created[input.Key].Description, listed[input.Key].Description);
                }
            }

            using (var duplicate = await SendAsync(
                       HttpMethod.Post,
                       "/api/workflow-settings",
                       new CreateWorkflowSettingRequest(
                           settingNamespace.ToUpperInvariant(),
                           "VALUE-STRING",
                           Json("false"),
                           "Case-insensitive duplicate.")))
            {
                Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
            }

            var original = created["value-object"];
            var replacement = Json(
                """["updated",42,true,null,{"deep":{"values":[1,2,3]}}]""");
            using (var response = await SendAsync(
                       HttpMethod.Put,
                       $"/api/workflow-settings/{original.Id}",
                       new UpdateWorkflowSettingRequest(
                           replacement,
                           "  Updated typed JSON value.  ",
                           original.UpdatedAt)))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var updated = await ReadAsync<WorkflowSettingDto>(response);
                Assert.Equal(original.Namespace, updated.Namespace);
                Assert.Equal(original.Name, updated.Name);
                Assert.Equal("Updated typed JSON value.", updated.Description);
                Assert.True(updated.UpdatedAt > original.UpdatedAt);
                AssertJsonEqual(replacement, updated.Value);
                created[original.Name] = updated;
            }

            foreach (var item in created.Values)
            {
                using var response = await SendAsync(
                    HttpMethod.Delete,
                    DeletePath("/api/workflow-settings", item.Id, item.UpdatedAt));
                Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
            }

            created.Clear();
        }
        finally
        {
            await DeleteWorkflowTestRowsAsync(settingNamespace, created.Values.Select(item => item.Id));
        }
    }

    [Fact]
    public async Task RequiredRoleChangeAppliesToTheNextRequest()
    {
        var snapshot = await ReadRequiredRoleSnapshotAsync();
        var requiredRole = $"settings-manager-{Guid.NewGuid():N}";

        try
        {
            using (var response = await SendAsync(
                       HttpMethod.Put,
                       $"/api/engine-settings/{snapshot.Id}",
                       new UpdateEngineSettingRequest(
                           requiredRole,
                           snapshot.Description,
                           snapshot.UpdatedAt)))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            using (var response = await SendAsync(
                       HttpMethod.Get,
                       "/api/workflow-settings",
                       user: "dynamic-settings-manager",
                       roles: [requiredRole.ToUpperInvariant()],
                       suppressAdmin: true))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            using (var response = await SendAsync(
                       HttpMethod.Get,
                       "/api/engine-settings",
                       user: "former-settings-admin",
                       roles: ["admin"],
                       suppressAdmin: true))
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }
        }
        finally
        {
            await RestoreRequiredRoleAsync(snapshot);
        }
    }

    [Fact]
    public async Task LifecycleCaptureIsAnEditableBooleanSettingWithProtectedIdentityAndDeletion()
    {
        using var list = await SendAsync(HttpMethod.Get, "/api/engine-settings");
        var original = Assert.Single(await ReadAsync<List<EngineSettingDto>>(list),
            setting => setting.Namespace == "Webhooks" && setting.Key == "CaptureEnabled");
        var current = original;
        var actor = $"capture-{Guid.NewGuid():N}";
        var metadataActors = new[] { $"description-{Guid.NewGuid():N}", $"same-value-{Guid.NewGuid():N}" };
        var rejectedActor = $"rejected-{Guid.NewGuid():N}";
        var enabled = original.Value != "true";
        try
        {
            using (var update = await SendAsync(HttpMethod.Put, $"/api/engine-settings/{original.Id}",
                       new UpdateEngineSettingRequest(enabled ? "\t TrUe \u00a0" : "\u00a0 FaLsE \n", original.Description, current.UpdatedAt),
                       user: actor))
            {
                Assert.Equal(HttpStatusCode.OK, update.StatusCode);
                current = await ReadAsync<EngineSettingDto>(update);
                Assert.Equal(enabled ? "true" : "false", current.Value);
                Assert.Equal(original.CreatedBy, current.CreatedBy);
                Assert.Equal(actor, current.UpdatedBy);
            }

            using (var catalog = await SendAsync(HttpMethod.Get, "/api/webhook-event-types"))
            {
                Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
                var body = await ReadAsync<JsonElement>(catalog);
                Assert.Equal(enabled, body.GetProperty("capture").GetProperty("enabled").GetBoolean());
                Assert.Equal(actor, body.GetProperty("capture").GetProperty("changedBy").GetString());
            }

            var description = $"Capture description changed: {Guid.NewGuid():N}";
            foreach (var metadataActor in metadataActors)
            {
                var before = current;
                using var unchanged = await SendAsync(HttpMethod.Put, $"/api/engine-settings/{original.Id}",
                    new UpdateEngineSettingRequest(current.Value, description, current.UpdatedAt), user: metadataActor);
                Assert.Equal(HttpStatusCode.OK, unchanged.StatusCode);
                current = await ReadAsync<EngineSettingDto>(unchanged);
                Assert.Equal(before.Value, current.Value);
                Assert.Equal(original.CreatedAt, current.CreatedAt);
                Assert.Equal(original.CreatedBy, current.CreatedBy);
                Assert.Equal(metadataActor, current.UpdatedBy);
                Assert.True(current.UpdatedAt > before.UpdatedAt);

                using var catalog = await SendAsync(HttpMethod.Get, "/api/webhook-event-types");
                Assert.Equal(HttpStatusCode.OK, catalog.StatusCode);
                var capture = (await ReadAsync<WebhookEventCatalogDto>(catalog)).Capture;
                Assert.Equal(enabled, capture.Enabled);
                Assert.Equal(current.UpdatedAt, capture.ChangedAt);
                Assert.Equal(metadataActor, capture.ChangedBy);
            }
            long captureAuditCount;
            await using (var db = fixture.CreateDbContext())
            {
                var audits = await db.WebhookManagementAudit.AsNoTracking().Where(item => item.Actor == actor).ToListAsync();
                Assert.Equal(enabled ? "captureEnabled" : "captureDisabled", Assert.Single(audits).Operation);
                Assert.False(await db.WebhookManagementAudit.AnyAsync(item => metadataActors.Contains(item.Actor)));
                captureAuditCount = await db.WebhookManagementAudit.LongCountAsync(item =>
                    item.Operation == "captureEnabled" || item.Operation == "captureDisabled");
            }

            foreach (var invalid in new[]
                     {
                         "", "yes", "1", "null", "\0true", "true\0", "\0true\0",
                         "\0false", "false\0", "\0false\0", " \0TrUe\0\t", "\n\0FaLsE\0 "
                     })
            {
                using var response = await SendAsync(HttpMethod.Put, $"/api/engine-settings/{original.Id}",
                    new UpdateEngineSettingRequest(invalid, original.Description, current.UpdatedAt), user: rejectedActor);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);

                using var latestList = await SendAsync(HttpMethod.Get, "/api/engine-settings");
                Assert.Equal(current, Assert.Single(await ReadAsync<List<EngineSettingDto>>(latestList), item => item.Id == current.Id));
                await using var db = fixture.CreateDbContext();
                Assert.Equal(captureAuditCount, await db.WebhookManagementAudit.LongCountAsync(item =>
                    item.Operation == "captureEnabled" || item.Operation == "captureDisabled"));
            }

            foreach (var alias in new[]
                     {
                         new CreateEngineSettingRequest(null, "Webhooks.CaptureEnabled", "true"),
                         new CreateEngineSettingRequest("webhooks", "CaptureEnabled", "true"),
                         new CreateEngineSettingRequest("Webhooks", "captureenabled", "true")
                     })
            {
                using var response = await SendAsync(HttpMethod.Post, "/api/engine-settings", alias);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }

            using (var duplicate = await SendAsync(HttpMethod.Post, "/api/engine-settings",
                       new CreateEngineSettingRequest("Webhooks", "CaptureEnabled", "false")))
                Assert.Equal(HttpStatusCode.Conflict, duplicate.StatusCode);
            using (var deletion = await SendAsync(HttpMethod.Delete,
                       DeletePath("/api/engine-settings", current.Id, current.UpdatedAt)))
                Assert.Equal(HttpStatusCode.BadRequest, deletion.StatusCode);
            using (var stale = await SendAsync(HttpMethod.Put, $"/api/engine-settings/{original.Id}",
                       new UpdateEngineSettingRequest("false", original.Description, original.UpdatedAt)))
                Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);

            using var finalList = await SendAsync(HttpMethod.Get, "/api/engine-settings");
            Assert.Equal(current, Assert.Single(await ReadAsync<List<EngineSettingDto>>(finalList), item => item.Id == current.Id));
            await using (var db = fixture.CreateDbContext())
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT flowbit.set_lifecycle_event_capture({!enabled}, {"sql-capture-test"})");
            using var staleAfterSql = await SendAsync(HttpMethod.Put, $"/api/engine-settings/{original.Id}",
                new UpdateEngineSettingRequest(current.Value, current.Description, current.UpdatedAt), user: actor);
            Assert.Equal(HttpStatusCode.Conflict, staleAfterSql.StatusCode);
        }
        finally
        {
            using var latestResponse = await SendAsync(HttpMethod.Get, "/api/engine-settings");
            var latest = Assert.Single(await ReadAsync<List<EngineSettingDto>>(latestResponse), item => item.Id == original.Id);
            using var restored = await SendAsync(HttpMethod.Put, $"/api/engine-settings/{original.Id}",
                new UpdateEngineSettingRequest(original.Value == "true" ? "\t TrUe \u00a0" : "\u00a0 FaLsE \n",
                    original.Description, latest.UpdatedAt), user: "test-cleanup");
            restored.EnsureSuccessStatusCode();
            Assert.Equal(original.Value, (await ReadAsync<EngineSettingDto>(restored)).Value);
        }
    }

    [Theory]
    [InlineData("POST", "/api/engine-settings")]
    [InlineData("PUT", "/api/engine-settings/1")]
    [InlineData("DELETE", "/api/engine-settings/1?expectedUpdatedAt=2026-01-01T00:00:00Z")]
    [InlineData("POST", "/api/ai-models")]
    [InlineData("PUT", "/api/ai-models/unknown")]
    [InlineData("DELETE", "/api/ai-models/unknown?expectedRevision=1")]
    public async Task SettingsWritesRequireAnActorEvenForAnAuthenticatedAdministrator(string method, string path)
    {
        await using var factory = fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
            services.Replace(ServiceDescriptor.Singleton<IActorContextResolver>(new MissingActorResolver()))));
        using var client = factory.CreateClient();
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        ApiTestAuth.Authorize(request);
        if (method != "DELETE")
            request.Content = JsonContent.Create(path.Contains("ai-models", StringComparison.Ordinal)
                ? (object)new { name = "test", endpoint = "https://example.com/v1", model = "model", apiKey = "test-key-at-least-sixteen", expectedRevision = 1 }
                : new { key = "test", value = "true", expectedUpdatedAt = DateTimeOffset.UtcNow });
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    private sealed class MissingActorResolver : IActorContextResolver
    {
        public ActorContext Resolve(System.Security.Claims.ClaimsPrincipal principal) => new(null, ["admin"], new Dictionary<string, string>());
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "settings-admin",
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

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("Response body was empty.");

    private static async Task<long> ReadIdAsync(HttpResponseMessage response)
    {
        var content = await response.Content.ReadAsStringAsync();
        using var document = JsonDocument.Parse(content);
        return document.RootElement.GetProperty("id").GetInt64();
    }

    private static string DeletePath(
        string basePath,
        long id,
        DateTimeOffset expectedUpdatedAt) =>
        $"{basePath}/{id}?expectedUpdatedAt={Uri.EscapeDataString(expectedUpdatedAt.ToString("O", CultureInfo.InvariantCulture))}";

    private async Task DeleteEngineTestRowsAsync(string marker, long? id)
    {
        await using var db = fixture.CreateDbContext();
        await db.EngineSettings
            .Where(setting =>
                (id.HasValue && setting.Id == id.Value)
                || setting.Namespace == marker
                || ((setting.Namespace == null || setting.Namespace == string.Empty)
                    && EF.Functions.Like(setting.Key, marker + ".%")))
            .ExecuteDeleteAsync();
    }

    private async Task DeleteWorkflowTestRowsAsync(
        string settingNamespace,
        IEnumerable<long> ids)
    {
        var idArray = ids.Distinct().ToArray();
        await using var db = fixture.CreateDbContext();
        await db.WorkflowSettings
            .Where(setting =>
                setting.Namespace == settingNamespace
                || idArray.Contains(setting.Id))
            .ExecuteDeleteAsync();
    }

    private async Task<RequiredRoleSnapshot> ReadRequiredRoleSnapshotAsync()
    {
        await using var db = fixture.CreateDbContext();
        var setting = await db.EngineSettings
            .AsNoTracking()
            .SingleAsync(candidate =>
                (candidate.Namespace == "Settings" && candidate.Key == "RequiredRole")
                || ((candidate.Namespace == null || candidate.Namespace == string.Empty)
                    && candidate.Key == "Settings.RequiredRole"));
        return new RequiredRoleSnapshot(
            setting.Id,
            setting.Namespace,
            setting.Key,
            setting.Value,
            setting.Description,
            setting.CreatedAt,
            setting.UpdatedAt);
    }

    private async Task RestoreRequiredRoleAsync(RequiredRoleSnapshot snapshot)
    {
        await using var db = fixture.CreateDbContext();
        var restored = await db.EngineSettings
            .Where(setting => setting.Id == snapshot.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(setting => setting.Namespace, snapshot.Namespace)
                .SetProperty(setting => setting.Key, snapshot.Key)
                .SetProperty(setting => setting.Value, snapshot.Value)
                .SetProperty(setting => setting.Description, snapshot.Description)
                .SetProperty(setting => setting.CreatedAt, snapshot.CreatedAt)
                .SetProperty(setting => setting.UpdatedAt, snapshot.UpdatedAt));
        Assert.Equal(1, restored);
    }

    private static JsonElement Json(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private static void AssertJsonEqual(JsonElement expected, JsonElement actual)
    {
        Assert.Equal(expected.ValueKind, actual.ValueKind);
        switch (expected.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var expectedProperties = expected.EnumerateObject().ToArray();
                var actualProperties = actual.EnumerateObject().ToArray();
                Assert.Equal(expectedProperties.Length, actualProperties.Length);
                foreach (var property in expectedProperties)
                {
                    Assert.True(actual.TryGetProperty(property.Name, out var actualValue));
                    AssertJsonEqual(property.Value, actualValue);
                }
                break;
            }
            case JsonValueKind.Array:
            {
                var expectedItems = expected.EnumerateArray().ToArray();
                var actualItems = actual.EnumerateArray().ToArray();
                Assert.Equal(expectedItems.Length, actualItems.Length);
                for (var index = 0; index < expectedItems.Length; index++)
                {
                    AssertJsonEqual(expectedItems[index], actualItems[index]);
                }
                break;
            }
            case JsonValueKind.String:
                Assert.Equal(expected.GetString(), actual.GetString());
                break;
            case JsonValueKind.Number:
                Assert.Equal(expected.GetDecimal(), actual.GetDecimal());
                break;
            case JsonValueKind.True:
            case JsonValueKind.False:
                Assert.Equal(expected.GetBoolean(), actual.GetBoolean());
                break;
            case JsonValueKind.Null:
                break;
            default:
                throw new InvalidOperationException($"Unexpected JSON kind {expected.ValueKind}.");
        }
    }

    private sealed record RequiredRoleSnapshot(
        long Id,
        string? Namespace,
        string Key,
        string Value,
        string? Description,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}
