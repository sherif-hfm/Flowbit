using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
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

public sealed class LocalBearerAuthenticationTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("override")]
    [InlineData("issuer")]
    [InlineData("audience")]
    [InlineData("signature")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("missing_actor")]
    [InlineData("ambiguous_actor")]
    [InlineData("numeric_actor")]
    [InlineData("numeric_role")]
    [InlineData("malformed")]
    [InlineData("algorithm")]
    public async Task Production_local_bearer_uses_explicit_trust_and_identity_without_a_directory(string scenario)
    {
        var key = new string('x', 64);
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        TestWebHostDefaults.IsolateFromMachine(builder);
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        { ["Authentication:Mode"] = "Local", ["Authentication:Local:SigningKey"] = key });
        builder.Services.AddFlowbitAuthentication(builder.Configuration, builder.Environment);
        var identity = new ActorIdentityConfiguration();
        identity.Initialize(scenario == "override" ? new EngineSettingRecord(1, "Authentication", "UserIdentityClaim", "login", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow) : null);
        builder.Services.AddSingleton(identity);
        builder.Services.AddSingleton<IActorContextResolver, ActorContextResolver>();
        builder.Services.AddSingleton<IEngineSettingsService, EmptySettings>();
        builder.Services.AddSingleton<ISharedVariableClientService>(_ => throw new NotSupportedException());
        await using var app = builder.Build();
        app.UseAuthentication(); app.UseAuthorization();
        app.MapGet("/context", (HttpContext http, IActorContextResolver actors) => Results.Ok(actors.Resolve(http.User).User)).RequireAuthorization();
        await app.StartAsync();
        var claims = new List<Claim> { new("roles", "Reviewer") };
        if (scenario is not ("missing_actor" or "numeric_actor")) claims.Add(new(scenario == "override" ? "login" : "Username", "external-user"));
        if (scenario == "numeric_actor") claims.Add(new("Username", "12", ClaimValueTypes.Integer));
        if (scenario == "ambiguous_actor") claims.Add(new("Username", "other-user"));
        if (scenario == "numeric_role") claims.Add(new("roles", "12", ClaimValueTypes.Integer));
        var token = new JwtSecurityToken(scenario == "issuer" ? "other" : "flowbit-local", scenario == "audience" ? "other" : "flowbit-api", claims,
            scenario == "future" ? DateTime.UtcNow.AddMinutes(5) : DateTime.UtcNow.AddHours(-1),
            scenario == "expired" ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow.AddMinutes(15),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(scenario == "signature" ? new string('y', 64) : key)),
                scenario == "algorithm" ? SecurityAlgorithms.HmacSha384 : SecurityAlgorithms.HmacSha256));
        using var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", scenario == "malformed" ? "broken.token" : new JwtSecurityTokenHandler().WriteToken(token));
        using var response = await client.GetAsync("/context");
        Assert.Equal(scenario is "valid" or "override" ? HttpStatusCode.OK : HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Null(response.Headers.Location);
    }
    [Theory]
    [InlineData("")]
    [InlineData("too-short")]
    public void Local_requires_its_own_explicit_key(string key)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        var options = new FlowbitAuthenticationOptions { Mode = "Local", Local = new() { SigningKey = key } };
        Assert.Throws<InvalidOperationException>(() => options.Validate(builder.Environment));
    }
    private sealed class EmptySettings : IEngineSettingsService
    {
        public Task<EngineSettingRecord?> GetByKeyAsync(string key, CancellationToken ct) => Task.FromResult<EngineSettingRecord?>(null);
        public Task<IReadOnlyList<EngineSettingRecord>> SearchAsync(string pattern, CancellationToken ct) => throw new NotSupportedException();
        public Task<EngineSettingRecord> SetAsync(string key, string value, CancellationToken ct) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string key, CancellationToken ct) => throw new NotSupportedException();
    }
}
