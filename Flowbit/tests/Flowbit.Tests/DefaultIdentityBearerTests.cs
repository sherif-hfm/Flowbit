using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using Flowbit.Api.Auth;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Flowbit.Tests;

public sealed class DefaultIdentityBearerTests
{
    public static IEnumerable<object?[]> IdentityCases()
    {
        yield return ["username", new Claim[] { new("Username", " Alice "), new("Username", "alice"), new(ClaimTypes.Name, "other-name"), new("sub", "other-subject") }, "Alice"];
        yield return ["ignored-invalid-sub", new Claim[] { new("Username", "alice"), new(ClaimTypes.Name, " "), new("sub", "42", ClaimValueTypes.Integer) }, "alice"];
        yield return ["name", new Claim[] { new(ClaimTypes.Name, "name-user"), new("sub", "other-subject") }, "name-user"];
        yield return ["sub", new Claim[] { new("sub", "subject-user"), new("name", "Display Name"), new("preferred_username", "other") }, "subject-user"];
        yield return ["mapped-sub", new Claim[] { new(ClaimTypes.NameIdentifier, "subject-user") }, "subject-user"];
        yield return ["equivalent-sub-aliases", new Claim[] { new("sub", " Alice "), new(ClaimTypes.NameIdentifier, "alice") }, "Alice"];
        yield return ["no-identity", Array.Empty<Claim>(), null];
        yield return ["display-name-only", new Claim[] { new("name", "Display Name") }, null];
        yield return ["preferred-only", new Claim[] { new("preferred_username", "other") }, null];
        yield return ["legacy-only", new Claim[] { new("flowbit_user", "legacy") }, null];
        yield return ["blank-username", new Claim[] { new("Username", " "), new(ClaimTypes.Name, "valid"), new("sub", "valid") }, null];
        yield return ["numeric-username", new Claim[] { new("Username", "42", ClaimValueTypes.Integer), new("sub", "valid") }, null];
        yield return ["ambiguous-username", new Claim[] { new("Username", "alice"), new("Username", "bob"), new("sub", "valid") }, null];
        yield return ["oversized-username", new Claim[] { new("Username", new string('x', 301)), new("sub", "valid") }, null];
        yield return ["blank-name", new Claim[] { new(ClaimTypes.Name, " "), new("sub", "valid") }, null];
        yield return ["numeric-name", new Claim[] { new(ClaimTypes.Name, "42", ClaimValueTypes.Integer), new("sub", "valid") }, null];
        yield return ["ambiguous-name", new Claim[] { new(ClaimTypes.Name, "alice"), new(ClaimTypes.Name, "bob"), new("sub", "valid") }, null];
        yield return ["oversized-name", new Claim[] { new(ClaimTypes.Name, new string('x', 301)), new("sub", "valid") }, null];
        yield return ["blank-sub", new Claim[] { new("sub", " ") }, null];
        yield return ["numeric-sub", new Claim[] { new("sub", "42", ClaimValueTypes.Integer) }, null];
        yield return ["boolean-sub", new Claim[] { new("sub", "true", ClaimValueTypes.Boolean) }, null];
        yield return ["object-sub", new Claim[] { new("sub", "{\"user\":\"alice\"}", JsonClaimValueTypes.Json) }, null];
        yield return ["numeric-mapped-sub", new Claim[] { new(ClaimTypes.NameIdentifier, "42", ClaimValueTypes.Integer) }, null];
        yield return ["ambiguous-sub-alias", new Claim[] { new("sub", "alice"), new(ClaimTypes.NameIdentifier, "bob") }, null];
        yield return ["oversized-sub", new Claim[] { new("sub", new string('x', 301)) }, null];
        yield return ["actor-limit", new Claim[] { new("Username", new string('x', 300)) }, new string('x', 300)];
    }

    public static IEnumerable<object?[]> SymmetricCases()
    {
        foreach (var mode in new[] { "Local", "DevelopmentToken" })
            foreach (var test in IdentityCases()) yield return [mode, .. test];
    }

    [Theory]
    [MemberData(nameof(SymmetricCases))]
    public async Task Symmetric_bearer_modes_use_the_same_identity_chain(string mode, string scenario, Claim[] claims, string? expected)
        => await Verify(mode, claims, expected, scenario: scenario);

    [Theory]
    [InlineData("Local", "Username", false)]
    [InlineData("DevelopmentToken", "Username", false)]
    [InlineData("Local", "employee_id", true)]
    [InlineData("DevelopmentToken", "employee_id", true)]
    public async Task Database_override_remains_authoritative_and_strict(string mode, string claimType, bool supplied)
    {
        List<Claim> claims = [new("sub", "fallback-user")];
        if (supplied) { claims.Add(new("Username", "default-user")); claims.Add(new(claimType, "custom-user")); }
        await Verify(mode, claims, supplied ? "custom-user" : null, claimType);
    }

    private static async Task Verify(string mode, IEnumerable<Claim> claims, string? expected, string? overrideClaim = null, string? scenario = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        TestWebHostDefaults.IsolateFromMachine(builder);
        builder.WebHost.UseTestServer();
        var key = new string('x', 64);
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Mode"] = mode, ["Authentication:Local:SigningKey"] = key,
            ["Jwt:Key"] = key, ["Jwt:Issuer"] = "flowbit-local", ["Jwt:Audience"] = "flowbit-api"
        });
        builder.Services.AddFlowbitAuthentication(builder.Configuration, builder.Environment);
        var identity = new ActorIdentityConfiguration();
        identity.Initialize(overrideClaim is null ? null : new EngineSettingRecord(1, "Authentication", "UserIdentityClaim", overrideClaim, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        builder.Services.AddSingleton(identity);
        builder.Services.AddSingleton<IActorContextResolver, ActorContextResolver>();
        builder.Services.AddSingleton<IEngineSettingsService, EmptySettings>();
        builder.Services.AddSingleton<ISharedVariableClientService>(_ => throw new NotSupportedException());
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization();
        app.MapGet("/actor", (HttpContext http, IActorContextResolver resolver) => Results.Ok(resolver.Resolve(http.User).User)).RequireAuthorization();
        await app.StartAsync();
        var token = new JwtSecurityToken("flowbit-local", "flowbit-api", claims, DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256));
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", new JwtSecurityTokenHandler().WriteToken(token));
        using var response = await client.GetAsync("/actor");
        Assert.True(response.StatusCode == (expected is null ? HttpStatusCode.Unauthorized : HttpStatusCode.OK), $"{mode}/{scenario}: unexpected {response.StatusCode}");
        if (expected is not null) Assert.Equal(expected, await response.Content.ReadFromJsonAsync<string>());
        Assert.Null(response.Headers.Location);
    }

    private sealed class EmptySettings : IEngineSettingsService
    {
        public Task<EngineSettingRecord?> GetByKeyAsync(string key, CancellationToken ct) => Task.FromResult<EngineSettingRecord?>(null);
        public Task<IReadOnlyList<EngineSettingRecord>> SearchAsync(string pattern, CancellationToken ct) => throw new NotSupportedException();
        public Task<EngineSettingRecord> SetAsync(string key, string value, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string key, CancellationToken ct) => throw new NotSupportedException();
    }
}
