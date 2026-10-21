using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Flowbit.Api.Auth;
using Flowbit.Infrastructure.Data;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Tokens;
using Testcontainers.PostgreSql;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class LocalIdentityTests
{
    [Fact]
    public async Task Local_directory_tokens_refresh_and_administration_use_real_PostgreSQL_and_bearer_authentication()
    {
        await using var postgres = new PostgreSqlBuilder("postgres:17-alpine").WithDatabase("local_identity")
            .WithUsername("postgres").WithPassword(Guid.NewGuid().ToString("N")).Build();
        await postgres.StartAsync();
        await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(postgres.GetConnectionString(), FlowbitDatabase.ConfigureProvider).Options)) await db.Database.MigrateAsync();
        var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        await using var factory = new LocalFactory(postgres.GetConnectionString(), key);
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost"), AllowAutoRedirect = false });
        var admin = await Login(client, "admin", "a");
        client.DefaultRequestHeaders.Authorization = new("Bearer", admin.AccessToken);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(admin.AccessToken);
        Assert.Equal("admin", jwt.Claims.Single(c => c.Type == "Username").Value);
        Assert.Equal("admin", jwt.Subject);
        Assert.DoesNotContain(jwt.Claims, c => c.Type == "flowbit_user");
        Assert.InRange((admin.ExpiresAt - DateTimeOffset.UtcNow).TotalMinutes, 14, 16);
        Assert.Equal("admin", (await client.GetFromJsonAsync<ActorContextDto>("/api/auth/context"))!.User);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync("/api/users?page=2147483647&pageSize=200")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/users/1/status", new SetLocalUserStatusRequest(false, 1))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync("/api/users/1", new UpdateLocalUserRequest("Flowbit", "Admin", [], [], [], 1))).StatusCode);

        var field = await Post<UserAttributeDto>(client, "/api/user-attributes", new SaveUserAttributeRequest("department", "text", "Department", true, [], "depId"));
        await Post<UserAttributeDto>(client, "/api/user-attributes", new SaveUserAttributeRequest("amount", "number", "Amount", true, [], null));
        await Post<UserAttributeDto>(client, "/api/user-attributes", new SaveUserAttributeRequest("approved", "boolean", "Approved", true, [], null));
        await Post<UserAttributeDto>(client, "/api/user-attributes", new SaveUserAttributeRequest("birthday", "date", "Birthday", false, [], null));
        await Post<UserAttributeDto>(client, "/api/user-attributes", new SaveUserAttributeRequest("region", "dropdown", "Region", false, ["East", "West"], "region"));
        var values = new Dictionary<string, JsonElement> { ["department"] = JsonSerializer.SerializeToElement("Finance"),
            ["amount"] = JsonSerializer.SerializeToElement(0), ["approved"] = JsonSerializer.SerializeToElement(false),
            ["birthday"] = JsonSerializer.SerializeToElement("2000-01-01"), ["region"] = JsonSerializer.SerializeToElement("East") };
        var create = new CreateLocalUserRequest("Alice", "Alice", "Example", "a", true, ["Reviewer"],
            [new("team", "one"), new("team", "two")], values);
        foreach (var missingPassword in new string?[] { null, "" })
        {
            using var invalidPassword = await client.PostAsJsonAsync("/api/users", create with { Password = missingPassword! });
            Assert.Equal(HttpStatusCode.BadRequest, invalidPassword.StatusCode);
            Assert.Contains("Password is required.", await invalidPassword.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/token", new LocalLoginRequest("admin", missingPassword!))).StatusCode);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/users", create with { Attributes = [] })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/users", create with { Roles = Enumerable.Range(0, 100).Select(i => i + new string('r', 290)).ToArray() })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/users", create with { Username = string.Concat(Enumerable.Repeat("\U0001F600", 151)) })).StatusCode);
        var invalidValues = new Dictionary<string, JsonElement>(values) { ["approved"] = JsonSerializer.SerializeToElement("false") };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/users", create with { Attributes = invalidValues })).StatusCode);
        invalidValues = new(values) { ["birthday"] = JsonSerializer.SerializeToElement("2026-02-30") };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/users", create with { Attributes = invalidValues })).StatusCode);
        invalidValues = new(values) { ["region"] = JsonSerializer.SerializeToElement("Unknown") };
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/users", create with { Attributes = invalidValues })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/users", create with { Claims = [new("Username", "other")] })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync("/api/users", create with { Claims = [new("depId", "other")] })).StatusCode);
        var user = await Post<LocalUserDto>(client, "/api/users", create);
        Assert.Equal(HttpStatusCode.Conflict, (await client.PostAsJsonAsync("/api/users", create with { Username = "ALICE" })).StatusCode);
        var profileJson = await client.GetStringAsync($"/api/users/{user.Id}");
        Assert.DoesNotContain("password", profileJson, StringComparison.OrdinalIgnoreCase);
        var alice = await Login(client, "aLiCe", create.Password);
        var aliceJwt = new JwtSecurityTokenHandler().ReadJwtToken(alice.AccessToken);
        Assert.Equal("Alice", aliceJwt.Claims.Single(c => c.Type == "Username").Value);
        Assert.Equal("Finance", aliceJwt.Claims.Single(c => c.Type == "depId").Value);
        Assert.Equal(["one", "two"], aliceJwt.Claims.Where(c => c.Type == "team").Select(c => c.Value).ToArray());
        using (var viewer = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }))
        {
            viewer.DefaultRequestHeaders.Authorization = new("Bearer", alice.AccessToken);
            Assert.Equal(HttpStatusCode.Forbidden, (await viewer.GetAsync("/api/users")).StatusCode);
        }
        var edit = new UpdateLocalUserRequest("Alicia", user.LastName, ["Finance"], user.Claims, user.Attributes, user.Revision);
        var editedResponse = await client.PutAsJsonAsync($"/api/users/{user.Id}", edit); editedResponse.EnsureSuccessStatusCode();
        user = (await editedResponse.Content.ReadFromJsonAsync<LocalUserDto>())!;
        Assert.Equal(HttpStatusCode.Conflict, (await client.PutAsJsonAsync($"/api/users/{user.Id}", edit)).StatusCode);
        var rotated = await Post<LocalTokenPairDto>(client, "/api/auth/refresh", new LocalRefreshRequest(alice.RefreshToken));
        Assert.Contains(new JwtSecurityTokenHandler().ReadJwtToken(rotated.AccessToken).Claims, c => c.Type == "roles" && c.Value == "Finance");
        var sibling = await Login(client, "Alice", create.Password);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", new LocalRefreshRequest(alice.RefreshToken))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", new LocalRefreshRequest(rotated.RefreshToken))).StatusCode);
        sibling = await Post<LocalTokenPairDto>(client, "/api/auth/refresh", new LocalRefreshRequest(sibling.RefreshToken));
        user = await Post<LocalUserDto>(client, $"/api/users/{user.Id}/status", new SetLocalUserStatusRequest(false, user.Revision));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/token", new LocalLoginRequest("Alice", create.Password))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", new LocalRefreshRequest(sibling.RefreshToken))).StatusCode);
        using (var old = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }))
        {
            old.DefaultRequestHeaders.Authorization = new("Bearer", sibling.AccessToken);
            Assert.Equal(HttpStatusCode.OK, (await old.GetAsync("/api/auth/context")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await old.GetAsync("/api/auth/session")).StatusCode);
        }
        user = await Post<LocalUserDto>(client, $"/api/users/{user.Id}/status", new SetLocalUserStatusRequest(true, user.Revision));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PostAsJsonAsync($"/api/users/{user.Id}/password", new ResetLocalPasswordRequest("", user.Revision))).StatusCode);
        var longPassword = new string('x', 257);
        (await client.PostAsJsonAsync($"/api/users/{user.Id}/password", new ResetLocalPasswordRequest(longPassword, user.Revision))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/token", new LocalLoginRequest("Alice", create.Password))).StatusCode);
        alice = await Login(client, "Alice", longPassword);
        using (var own = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new("https://localhost") }))
        {
            own.DefaultRequestHeaders.Authorization = new("Bearer", alice.AccessToken);
            Assert.Equal(HttpStatusCode.BadRequest, (await own.PostAsJsonAsync("/api/auth/change-password", new ChangeLocalPasswordRequest(longPassword, ""))).StatusCode);
            (await own.PostAsJsonAsync("/api/auth/change-password", new ChangeLocalPasswordRequest(longPassword, "1"))).EnsureSuccessStatusCode();
            Assert.Equal(HttpStatusCode.OK, (await own.GetAsync("/api/auth/context")).StatusCode);
            Assert.Equal(HttpStatusCode.Unauthorized, (await own.GetAsync("/api/auth/session")).StatusCode);
        }
        alice = await Login(client, "Alice", "1");
        (await client.PostAsJsonAsync("/api/auth/logout", new LocalRefreshRequest(alice.RefreshToken))).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/refresh", new LocalRefreshRequest(alice.RefreshToken))).StatusCode);
        await Post<LocalUserDto>(client, "/api/users", create with { Username = "locked", Password = longPassword });
        _ = await Login(client, "locked", longPassword);
        for (var attempt = 0; attempt < 5; attempt++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/token", new LocalLoginRequest("locked", "wrong"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.PostAsJsonAsync("/api/auth/token", new LocalLoginRequest("locked", longPassword))).StatusCode);
        await Post<UserAttributeDto>(client, "/api/user-attributes", new SaveUserAttributeRequest("new_required", "text", "New required", true, [], null));
        _ = await Login(client, "Alice", "1");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/api/user-attributes/{field.Id}", new SaveUserAttributeRequest(field.Key, field.Type, field.Label, true, [], "Username", field.Revision))).StatusCode);

        // External tokens need no directory account and keep their own permissions.
        var external = new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("flowbit-local", "flowbit-api",
            [new("Username", "external-only"), new("roles", "Reviewer")], DateTime.UtcNow, DateTime.UtcNow.AddMinutes(5),
            new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256)));
        client.DefaultRequestHeaders.Authorization = new("Bearer", external);
        Assert.Equal("external-only", (await client.GetFromJsonAsync<ActorContextDto>("/api/auth/context"))!.User);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/session")).StatusCode);
        client.DefaultRequestHeaders.Authorization = new("Bearer", admin.AccessToken);
        await using var scope1 = factory.Services.CreateAsyncScope(); await using var scope2 = factory.Services.CreateAsyncScope();
        await Task.WhenAll(scope1.ServiceProvider.GetRequiredService<LocalIdentityService>().BootstrapAsync(default),
            scope2.ServiceProvider.GetRequiredService<LocalIdentityService>().BootstrapAsync(default));
        Assert.Equal(3, (await client.GetFromJsonAsync<PagedResult<LocalUserDto>>("/api/users"))!.TotalCount);
    }
    private static async Task<LocalTokenPairDto> Login(HttpClient client, string username, string password)
        => await Post<LocalTokenPairDto>(client, "/api/auth/token", new LocalLoginRequest(username, password));
    private static async Task<T> Post<T>(HttpClient client, string path, object request)
    {
        using var response = await client.PostAsync(path, JsonContent.Create(request, request.GetType()));
        Assert.True(response.IsSuccessStatusCode, $"{path}: {(int)response.StatusCode} {await response.Content.ReadAsStringAsync()}");
        return (await response.Content.ReadFromJsonAsync<T>())!;
    }
    private sealed class LocalFactory(string connection, string key) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Production");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = "Local", ["Authentication:Local:SigningKey"] = key,
                ["Authentication:Local:BootstrapUsername"] = "admin", ["Authentication:Local:BootstrapPassword"] = "a",
                // Old deployments may still supply this removed setting; it must not impose a policy.
                ["Authentication:Local:MinimumPasswordLength"] = "12",
                ["ConnectionStrings:Flowbit"] = connection, ["WorkflowAi:Enabled"] = "false", ["WorkflowDurableProcessing:PublicationEnabled"] = "false"
            }));
        }
    }
}
