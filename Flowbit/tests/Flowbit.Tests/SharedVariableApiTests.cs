using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Api.Auth;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class SharedVariableApiTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task SharedIncidentResponsesAreNoStoreAndOnlyDetailGetDisclosesDiagnostics()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var retryKey = $"tests.api-incident-retry.{suffix}";
        var resolveKey = $"tests.api-incident-resolve.{suffix}";
        const string retryDetails =
            "Legacy diagnostic containing database.internal and a sensitive-looking token.";
        const string resolveDetails = "Second bounded diagnostic.";

        try
        {
            var retryIncidentId = await SeedExpansionIncidentAsync(retryKey, retryDetails);
            var resolveIncidentId = await SeedExpansionIncidentAsync(resolveKey, resolveDetails);

            using (var listRequest = JwtRequest(
                       HttpMethod.Get,
                       $"/api/shared-variable-incidents?status=open&sharedKey={Uri.EscapeDataString(retryKey)}"))
            using (var listResponse = await fixture.Client.SendAsync(listRequest))
            {
                Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
                AssertNoStore(listResponse);
                var page = await ReadAsync<PagedResult<SharedVariableIncidentDto>>(listResponse);
                var incident = Assert.Single(page.Items);
                Assert.Equal(retryIncidentId, incident.Id);
                Assert.Null(incident.Details);
            }

            using (var detailRequest = JwtRequest(
                       HttpMethod.Get,
                       $"/api/shared-variable-incidents/{retryIncidentId}"))
            using (var detailResponse = await fixture.Client.SendAsync(detailRequest))
            {
                Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
                AssertNoStore(detailResponse);
                var incident = await ReadAsync<SharedVariableIncidentDto>(detailResponse);
                Assert.Equal(retryDetails, incident.Details);
            }

            using (var retryRequest = JwtRequest(
                       HttpMethod.Post,
                       $"/api/shared-variable-incidents/{retryIncidentId}/retry"))
            using (var retryResponse = await fixture.Client.SendAsync(retryRequest))
            {
                Assert.Equal(HttpStatusCode.OK, retryResponse.StatusCode);
                AssertNoStore(retryResponse);
                Assert.Null((await ReadAsync<SharedVariableIncidentDto>(retryResponse)).Details);
            }

            using (var resolveRequest = JwtRequest(
                       HttpMethod.Post,
                       $"/api/shared-variable-incidents/{resolveIncidentId}/resolve",
                       new ResolveSharedVariableIncidentRequest("Operator accepted cancellation.")))
            using (var resolveResponse = await fixture.Client.SendAsync(resolveRequest))
            {
                Assert.Equal(HttpStatusCode.OK, resolveResponse.StatusCode);
                AssertNoStore(resolveResponse);
                Assert.Null((await ReadAsync<SharedVariableIncidentDto>(resolveResponse)).Details);
            }
        }
        finally
        {
            await DeleteSharedVariableTestDataAsync(retryKey, resolveKey);
        }
    }

    [Fact]
    public async Task NullableNullSkipsCustomValidationThroughHttpValueRoute()
    {
        var key = $"tests.api-nullable.{Guid.NewGuid():N}";
        try
        {
            SharedVariableMetadataDto created;
            using (var createRequest = JwtRequest(
                       HttpMethod.Post,
                       "/api/shared-variables",
                       new CreateSharedVariableRequest(
                           key,
                           "number",
                           IsArray: false,
                           Nullable: true,
                           HasValue: true,
                           Value: JsonSerializer.SerializeToElement(1),
                           Validation: "value > 0")))
            using (var createResponse = await fixture.Client.SendAsync(createRequest))
            {
                Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
                created = await ReadAsync<SharedVariableMetadataDto>(createResponse);
            }

            var jsonNull = JsonSerializer.SerializeToElement<object?>(null);
            using (var updateRequest = JwtRequest(
                       HttpMethod.Put,
                       ValuePath(key),
                       new UpdateSharedVariableValueRequest(jsonNull, created.Revision)))
            using (var updateResponse = await fixture.Client.SendAsync(updateRequest))
            {
                Assert.Equal(HttpStatusCode.OK, updateResponse.StatusCode);
                var updated = await ReadAsync<SharedVariableValueDto>(updateResponse);
                Assert.True(updated.HasValue);
                Assert.Equal(JsonValueKind.Null, updated.Value?.ValueKind);
                Assert.True(updated.ValueRevision > created.ValueRevision);
            }

            using var getRequest = JwtRequest(HttpMethod.Get, ValuePath(key));
            using var getResponse = await fixture.Client.SendAsync(getRequest);
            Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);
            var current = await ReadAsync<SharedVariableValueDto>(getResponse);
            Assert.True(current.HasValue);
            Assert.Equal(JsonValueKind.Null, current.Value?.ValueKind);
        }
        finally
        {
            await using var db = fixture.CreateDbContext();
            var variableIds = await db.SharedVariables
                .Where(variable => variable.Key == key)
                .Select(variable => variable.Id)
                .ToArrayAsync();
            var wakeIds = await db.SharedVariableWakes
                .Where(wake => variableIds.Contains(wake.SharedVariableId))
                .Select(wake => wake.Id)
                .ToArrayAsync();
            await db.SharedVariableWakeIncidents
                .Where(incident => variableIds.Contains(incident.SharedVariableId))
                .ExecuteDeleteAsync();
            await db.SharedVariableWakeDeliveries
                .Where(delivery => wakeIds.Contains(delivery.WakeId))
                .ExecuteDeleteAsync();
            await db.SharedVariableWakes
                .Where(wake => variableIds.Contains(wake.SharedVariableId))
                .ExecuteDeleteAsync();
            await db.SharedVariableCurrentValues
                .Where(value => variableIds.Contains(value.SharedVariableId))
                .ExecuteDeleteAsync();
            await db.SharedVariableRequests
                .Where(item => variableIds.Contains(item.SharedVariableId))
                .ExecuteDeleteAsync();
            await db.SharedVariableRevisions
                .Where(revision => variableIds.Contains(revision.SharedVariableId))
                .ExecuteDeleteAsync();
            await db.SharedVariables
                .Where(variable => variableIds.Contains(variable.Id))
                .ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task OpenApiUsesBearerOrCompleteClientPairOnlyOnDataOperations()
    {
        await using var developmentFactory = fixture.Factory.WithWebHostBuilder(builder =>
            builder.UseEnvironment("Development"));
        using var client = developmentFactory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using var response = await client.GetAsync("/openapi/v1.json");
        response.EnsureSuccessStatusCode();
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement;

        var schemes = root.GetProperty("components").GetProperty("securitySchemes");
        Assert.Equal(
            SharedVariableClientAuthenticationDefaults.ClientIdHeader,
            schemes.GetProperty("SharedVariableClientId").GetProperty("name").GetString());
        Assert.Equal(
            SharedVariableClientAuthenticationDefaults.ClientSecretHeader,
            schemes.GetProperty("SharedVariableClientSecret").GetProperty("name").GetString());

        var paths = root.GetProperty("paths");
        AssertBearerOrClientPair(paths
            .GetProperty("/api/shared-variables/{key}/value")
            .GetProperty("get"));
        AssertBearerOrClientPair(paths
            .GetProperty("/api/shared-variables/{key}/value")
            .GetProperty("put"));
        AssertBearerOnly(paths
            .GetProperty("/api/shared-variables/{key}")
            .GetProperty("patch"));
        AssertBearerOnly(paths
            .GetProperty("/api/shared-variable-clients")
            .GetProperty("post"));
        AssertBearerOnly(paths
            .GetProperty("/api/shared-variable-clients/{id}")
            .GetProperty("put"));
        AssertBearerOnly(paths
            .GetProperty("/api/shared-variable-incidents")
            .GetProperty("get"));
        AssertBearerOnly(paths
            .GetProperty("/api/shared-variable-incidents/{incidentId}/retry")
            .GetProperty("post"));
        AssertBearerOnly(paths
            .GetProperty("/api/shared-variable-incidents/{incidentId}/resolve")
            .GetProperty("post"));
    }

    [Fact]
    public async Task JwtAdminCreatesRotatesAndRevokesClientUsedByUnifiedDataRoutes()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var clientId = $"shared-api-{suffix}";
        var key = $"finance.exchangeRate.{suffix}";

        try
        {
            CreateSharedVariableClientResult createdClient;
            using (var request = JwtRequest(
                       HttpMethod.Post,
                       "/api/shared-variable-clients",
                       new CreateSharedVariableClientRequest(
                           clientId,
                           "Finance API integration",
                           [SharedVariableClientScopes.Read])))
            using (var response = await fixture.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                createdClient = await ReadAsync<CreateSharedVariableClientResult>(response);
                Assert.Equal(clientId, createdClient.Client.ClientId);
                Assert.StartsWith("fsv_", createdClient.ClientSecret, StringComparison.Ordinal);
                Assert.True(createdClient.ClientSecret.Length >= 40);
                Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
                Assert.Contains("no-cache", response.Headers.Pragma.ToString());
            }

            using (var request = JwtRequest(HttpMethod.Get, "/api/shared-variable-clients"))
            using (var response = await fixture.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.DoesNotContain(
                    createdClient.ClientSecret,
                    await response.Content.ReadAsStringAsync(),
                    StringComparison.Ordinal);
            }

            using (var request = ClientRequest(
                       HttpMethod.Get,
                       "/api/shared-variable-clients",
                       clientId,
                       createdClient.ClientSecret))
            using (var response = await fixture.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }

            using (var request = ClientRequest(
                       HttpMethod.Get,
                       "/api/shared-variable-clients",
                       clientId,
                       createdClient.ClientSecret))
            {
                ApiTestAuth.Authorize(request, "mixed-admin", "admin");
                using var response = await fixture.Client.SendAsync(request);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }

            SharedVariableMetadataDto createdVariable;
            using (var request = JwtRequest(
                       HttpMethod.Post,
                       "/api/shared-variables",
                       new CreateSharedVariableRequest(
                           key,
                           "string",
                           IsArray: false,
                           Nullable: false,
                           HasValue: true,
                           Value: JsonSerializer.SerializeToElement("3.75"))))
            using (var response = await fixture.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.Created, response.StatusCode);
                createdVariable = await ReadAsync<SharedVariableMetadataDto>(response);
                Assert.Equal(key, createdVariable.Key);
                Assert.True(createdVariable.HasValue);
                Assert.DoesNotContain(
                    "\"value\":",
                    await response.Content.ReadAsStringAsync(),
                    StringComparison.OrdinalIgnoreCase);
            }

            using (var request = ClientRequest(
                       HttpMethod.Get,
                       ValuePath(key),
                       clientId.ToUpperInvariant(),
                       createdClient.ClientSecret))
            using (var response = await fixture.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }

            using (var request = ClientRequest(
                       HttpMethod.Get,
                       MetadataPath(key),
                       clientId,
                       createdClient.ClientSecret))
            using (var response = await fixture.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.DoesNotContain(
                    "\"value\":",
                    await response.Content.ReadAsStringAsync(),
                    StringComparison.OrdinalIgnoreCase);
            }

            using (var request = ClientRequest(
                       HttpMethod.Get,
                       ValuePath(key),
                       clientId,
                       createdClient.ClientSecret))
            using (var response = await fixture.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var value = await ReadAsync<SharedVariableValueDto>(response);
                Assert.True(value.HasValue);
                Assert.Equal("3.75", value.Value?.GetString());
            }

            using (var request = ClientRequest(
                       HttpMethod.Put,
                       ValuePath(key),
                       clientId,
                       createdClient.ClientSecret,
                       new UpdateSharedVariableValueRequest(
                           JsonSerializer.SerializeToElement("3.76"),
                           createdVariable.Revision)))
            using (var response = await fixture.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            }

            SharedVariableClientDto updatedClient;
            using (var request = JwtRequest(
                       HttpMethod.Put,
                       $"/api/shared-variable-clients/{createdClient.Client.Id}",
                       new UpdateSharedVariableClientRequest(
                           "Finance API integration (updated)",
                           [SharedVariableClientScopes.Read, SharedVariableClientScopes.Write],
                           ExpiresAt: null,
                           ExpectedRevision: createdClient.Client.Revision)))
            using (var response = await fixture.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                updatedClient = await ReadAsync<SharedVariableClientDto>(response);
                Assert.Equal(clientId, updatedClient.ClientId);
                Assert.Equal("Finance API integration (updated)", updatedClient.DisplayName);
                Assert.Equal(
                    [SharedVariableClientScopes.Read, SharedVariableClientScopes.Write],
                    updatedClient.Scopes);
            }

            using (var request = ClientRequest(
                       HttpMethod.Put,
                       ValuePath(key),
                       clientId,
                       createdClient.ClientSecret,
                       new UpdateSharedVariableValueRequest(
                           JsonSerializer.SerializeToElement("3.76"),
                           createdVariable.Revision)))
            using (var response = await fixture.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var value = await ReadAsync<SharedVariableValueDto>(response);
                Assert.Equal("3.76", value.Value?.GetString());
            }

            using (var request = ClientRequest(
                       HttpMethod.Get,
                       ValuePath(key),
                       clientId,
                       createdClient.ClientSecret))
            {
                ApiTestAuth.Authorize(request, "mixed-admin", "admin");
                using var response = await fixture.Client.SendAsync(request);
                Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            }

            RotateSharedVariableClientSecretResult rotated;
            using (var request = JwtRequest(
                       HttpMethod.Post,
                       $"/api/shared-variable-clients/{createdClient.Client.Id}/rotate",
                       new RotateSharedVariableClientSecretRequest(
                           updatedClient.Revision,
                           GracePeriodHours: 0)))
            using (var response = await fixture.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                rotated = await ReadAsync<RotateSharedVariableClientSecretResult>(response);
                Assert.NotEqual(createdClient.ClientSecret, rotated.ClientSecret);
                Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
                Assert.Contains("no-cache", response.Headers.Pragma.ToString());
            }

            using (var request = ClientRequest(
                       HttpMethod.Get,
                       ValuePath(key),
                       clientId,
                       createdClient.ClientSecret))
            using (var response = await fixture.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }

            using (var request = ClientRequest(
                       HttpMethod.Get,
                       ValuePath(key),
                       clientId,
                       rotated.ClientSecret))
            using (var response = await fixture.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }

            using (var request = JwtRequest(
                       HttpMethod.Post,
                       $"/api/shared-variable-clients/{createdClient.Client.Id}/revoke",
                       new RevokeSharedVariableClientRequest(
                           rotated.Client.Revision,
                           "Integration retired.")))
            using (var response = await fixture.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                var revoked = await ReadAsync<SharedVariableClientDto>(response);
                Assert.Equal(SharedVariableClientStatuses.Revoked, revoked.Status);
            }

            using (var request = ClientRequest(
                       HttpMethod.Get,
                       ValuePath(key),
                       clientId,
                       rotated.ClientSecret))
            using (var response = await fixture.Client.SendAsync(request))
            {
                Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            }
        }
        finally
        {
            await using var db = fixture.CreateDbContext();
            var variableIds = await db.SharedVariables
                .Where(variable => variable.Key == key)
                .Select(variable => variable.Id)
                .ToArrayAsync();
            var wakeIds = await db.SharedVariableWakes
                .Where(wake => variableIds.Contains(wake.SharedVariableId))
                .Select(wake => wake.Id)
                .ToArrayAsync();
            await db.SharedVariableWakeDeliveries
                .Where(delivery => wakeIds.Contains(delivery.WakeId))
                .ExecuteDeleteAsync();
            await db.SharedVariableWakes
                .Where(wake => variableIds.Contains(wake.SharedVariableId))
                .ExecuteDeleteAsync();
            await db.SharedVariableCurrentValues
                .Where(value => variableIds.Contains(value.SharedVariableId))
                .ExecuteDeleteAsync();
            await db.SharedVariableRequests
                .Where(item => variableIds.Contains(item.SharedVariableId))
                .ExecuteDeleteAsync();
            await db.SharedVariableRevisions
                .Where(revision => variableIds.Contains(revision.SharedVariableId))
                .ExecuteDeleteAsync();
            await db.SharedVariables
                .Where(variable => variableIds.Contains(variable.Id))
                .ExecuteDeleteAsync();
            var clientRecordIds = await db.SharedVariableClients
                .Where(client => client.ClientId == clientId)
                .Select(client => client.Id)
                .ToArrayAsync();
            await db.SharedVariableClientSecrets
                .Where(secret => clientRecordIds.Contains(secret.ClientId))
                .ExecuteDeleteAsync();
            await db.SharedVariableClients
                .Where(client => clientRecordIds.Contains(client.Id))
                .ExecuteDeleteAsync();
        }
    }

    private static HttpRequestMessage JwtRequest(
        HttpMethod method,
        string path,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        return ApiTestAuth.Authorize(request, "shared-variable-admin", "admin");
    }

    private async Task<long> SeedExpansionIncidentAsync(string key, string details)
    {
        using (var createRequest = JwtRequest(
                   HttpMethod.Post,
                   "/api/shared-variables",
                   new CreateSharedVariableRequest(
                       key,
                       "string",
                       IsArray: false,
                       Nullable: false,
                       HasValue: true,
                       Value: JsonSerializer.SerializeToElement("seed"),
                       Validation: null)))
        using (var createResponse = await fixture.Client.SendAsync(createRequest))
        {
            Assert.Equal(HttpStatusCode.Created, createResponse.StatusCode);
        }

        await using var db = fixture.CreateDbContext();
        var variable = await db.SharedVariables.SingleAsync(item => item.Key == key);
        var revision = await db.SharedVariableRevisions.SingleAsync(item =>
            item.SharedVariableId == variable.Id
            && item.Revision == variable.CurrentRevision);
        var now = DateTimeOffset.UtcNow;
        var wake = new SharedVariableWakeEntity
        {
            SharedVariableId = variable.Id,
            RevisionId = revision.Id,
            Revision = revision.Revision,
            Status = SharedVariableWakeStatuses.Incident,
            AttemptCount = 25,
            MaxAttempts = 25,
            AvailableAt = now,
            LastError = details,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.SharedVariableWakes.Add(wake);
        await db.SaveChangesAsync();

        var incident = new SharedVariableWakeIncidentEntity
        {
            WorkKind = SharedVariableWakeWorkKinds.Expansion,
            WakeId = wake.Id,
            OriginalWakeId = wake.Id,
            SharedVariableId = variable.Id,
            SharedKey = variable.Key,
            Revision = wake.Revision,
            Type = "syntheticFailure",
            Status = SharedVariableWakeIncidentStatuses.Open,
            Summary = "Synthetic wake expansion failure.",
            Details = details,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.SharedVariableWakeIncidents.Add(incident);
        await db.SaveChangesAsync();
        return incident.Id;
    }

    private async Task DeleteSharedVariableTestDataAsync(params string[] keys)
    {
        await using var db = fixture.CreateDbContext();
        var variableIds = await db.SharedVariables
            .Where(variable => keys.Contains(variable.Key))
            .Select(variable => variable.Id)
            .ToArrayAsync();
        var wakeIds = await db.SharedVariableWakes
            .Where(wake => variableIds.Contains(wake.SharedVariableId))
            .Select(wake => wake.Id)
            .ToArrayAsync();
        await db.SharedVariableWakeIncidents
            .Where(incident => variableIds.Contains(incident.SharedVariableId))
            .ExecuteDeleteAsync();
        await db.SharedVariableWakeDeliveries
            .Where(delivery => wakeIds.Contains(delivery.WakeId))
            .ExecuteDeleteAsync();
        await db.SharedVariableWakes
            .Where(wake => wakeIds.Contains(wake.Id))
            .ExecuteDeleteAsync();
        await db.SharedVariableCurrentValues
            .Where(value => variableIds.Contains(value.SharedVariableId))
            .ExecuteDeleteAsync();
        await db.SharedVariableRequests
            .Where(request => variableIds.Contains(request.SharedVariableId))
            .ExecuteDeleteAsync();
        await db.SharedVariableRevisions
            .Where(revision => variableIds.Contains(revision.SharedVariableId))
            .ExecuteDeleteAsync();
        await db.SharedVariables
            .Where(variable => variableIds.Contains(variable.Id))
            .ExecuteDeleteAsync();
    }

    private static void AssertNoStore(HttpResponseMessage response)
    {
        Assert.Contains("no-store", response.Headers.CacheControl?.ToString());
        Assert.Contains("no-cache", response.Headers.Pragma.ToString());
    }

    private static HttpRequestMessage ClientRequest(
        HttpMethod method,
        string path,
        string clientId,
        string clientSecret,
        object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }
        request.Headers.Add(SharedVariableClientAuthenticationDefaults.ClientIdHeader, clientId);
        request.Headers.Add(SharedVariableClientAuthenticationDefaults.ClientSecretHeader, clientSecret);
        return request;
    }

    private static string MetadataPath(string key) =>
        $"/api/shared-variables/{Uri.EscapeDataString(key)}";

    private static string ValuePath(string key) => $"{MetadataPath(key)}/value";

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>()
        ?? throw new InvalidOperationException("Response body was empty.");

    private static void AssertBearerOnly(JsonElement operation)
    {
        var requirements = operation.GetProperty("security").EnumerateArray().ToArray();
        var requirement = Assert.Single(requirements);
        Assert.Equal(["Bearer"], requirement.EnumerateObject().Select(item => item.Name));
    }

    private static void AssertBearerOrClientPair(JsonElement operation)
    {
        var requirements = operation.GetProperty("security").EnumerateArray().ToArray();
        Assert.Equal(2, requirements.Length);
        Assert.Contains(requirements, requirement =>
            requirement.EnumerateObject().Select(item => item.Name)
                .SequenceEqual(["Bearer"]));
        Assert.Contains(requirements, requirement =>
            requirement.EnumerateObject().Select(item => item.Name)
                .ToHashSet(StringComparer.Ordinal)
                .SetEquals(["SharedVariableClientId", "SharedVariableClientSecret"]));
    }
}
