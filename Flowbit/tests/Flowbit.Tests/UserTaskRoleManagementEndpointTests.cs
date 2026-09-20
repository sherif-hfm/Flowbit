using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Flowbit.Api.Auth;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Flowbit.Tests;

[Collection(InstanceDetailApiContractCollection.Name)]
public sealed class UserTaskRoleManagementEndpointTests
{
    private static readonly UserTaskRolePolicyDto Policy = new(
        9, 8, 2, "Review", 5, null, 11, ["Worker"],
        [new UserTaskFlowRolesDto(201, "Complete", ["Reviewer"])], 1, 0);

    [Theory]
    [InlineData("/api/user-tasks/5/roles", "GetUserTaskRolesAsync", false)]
    [InlineData("/api/multi-instance-executions/7/roles", "GetMultiInstanceRolesAsync", false)]
    [InlineData("/api/user-tasks/5/roles", "ChangeUserTaskRolesAsync", true)]
    [InlineData("/api/multi-instance-executions/7/roles", "ChangeMultiInstanceRolesAsync", true)]
    public async Task RoleRoutesDispatchToTheFocusedServiceWithResolvedActor(string path, string method, bool post)
    {
        await using var factory = new RoleApiFactory();
        factory.Roles.Policy = Policy;
        factory.Roles.Change = new UserTaskRolesChangeAckDto(true, Policy);
        using var client = factory.CreateClient();
        using var request = ApiTestAuth.AuthorizeWithClaims(
            new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get, path),
            "manager",
            [new Claim("department", "Legal"), new Claim("department", "Audit"), new Claim("unselected", "private")],
            "RoleManager");
        if (post)
        {
            request.Content = JsonContent.Create(new ChangeUserTaskRolesRequest(
                11, ["Legal"], [new(201, ["LegalAction"])], "Coverage changed"));
        }

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var call = Assert.Single(factory.Roles.Calls);
        Assert.Equal(method, call.Method);
        Assert.Equal(path.Contains("user-tasks", StringComparison.Ordinal) ? 5 : 7, call.Id);
        Assert.Equal("manager", call.Actor.User);
        Assert.Contains("RoleManager", call.Actor.Roles);
        Assert.Null(call.Actor.ActingFor);
        Assert.NotNull(call.Actor.AuditClaims);
        Assert.Equal("department", Assert.Single(call.Actor.AuditClaims).Key);
        Assert.Equal(["Legal", "Audit"], call.Actor.AuditClaims["department"]);
        Assert.True(call.Token.CanBeCanceled);
        Assert.False(call.Token.IsCancellationRequested);
        if (post)
        {
            Assert.NotNull(call.Request);
            Assert.Equal(11, call.Request.ExpectedRolePolicyId);
            Assert.Equal(["Legal"], call.Request.Roles);
            var flow = Assert.Single(call.Request.Flows);
            Assert.Equal(201, flow.FlowId);
            Assert.Equal(["LegalAction"], flow.Roles);
            Assert.Equal("Coverage changed", call.Request.Reason);
            var actual = await response.Content.ReadFromJsonAsync<UserTaskRolesChangeAckDto>();
            Assert.Equal(JsonSerializer.Serialize(factory.Roles.Change), JsonSerializer.Serialize(actual));
        }
        else
        {
            Assert.Null(call.Request);
            var actual = await response.Content.ReadFromJsonAsync<UserTaskRolePolicyDto>();
            Assert.Equal(JsonSerializer.Serialize(Policy), JsonSerializer.Serialize(actual));
        }
    }

    [Theory]
    [InlineData("/api/user-tasks/5/roles", false)]
    [InlineData("/api/multi-instance-executions/7/roles", false)]
    [InlineData("/api/user-tasks/5/roles", true)]
    [InlineData("/api/multi-instance-executions/7/roles", true)]
    public async Task MissingRoleScopeMapsToNotFound(string path, bool post)
    {
        await using var factory = new RoleApiFactory();
        using var client = factory.CreateClient();
        using var request = ApiTestAuth.Authorize(
            new HttpRequestMessage(post ? HttpMethod.Post : HttpMethod.Get, path),
            "manager", "RoleManager");
        if (post)
        {
            request.Content = JsonContent.Create(new ChangeUserTaskRolesRequest(1, [], [], null));
        }

        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Single(factory.Roles.Calls);
    }

    [Theory]
    [InlineData("/api/user-tasks/5/roles")]
    [InlineData("/api/multi-instance-executions/7/roles")]
    public async Task AnonymousRoleReadsAreRejectedByBearerMiddleware(string path)
    {
        await using var factory = new RoleApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
        Assert.Empty(factory.Roles.Calls);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("blank")]
    [InlineData("conflicting")]
    public async Task InvalidCanonicalIdentityIsRejectedBeforeRoleService(string identity)
    {
        await using var factory = new RoleApiFactory("preferred_username");
        using var client = factory.CreateClient();
        Claim[] claims = identity switch
        {
            "missing" => [],
            "blank" => [new("preferred_username", " ")],
            _ => [new("preferred_username", "alice"), new("preferred_username", "bob")]
        };
        using var request = ApiTestAuth.AuthorizeWithClaims(
            new HttpRequestMessage(HttpMethod.Get, "/api/user-tasks/5/roles"),
            "reader", claims, "RoleManager");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("preferred_username", await response.Content.ReadAsStringAsync());
        Assert.Empty(factory.Roles.Calls);
    }

    [Fact]
    public async Task PostWithoutABodyIsRejectedByRequestBinding()
    {
        await using var factory = new RoleApiFactory();
        using var client = factory.CreateClient();
        using var request = ApiTestAuth.Authorize(
            new HttpRequestMessage(HttpMethod.Post, "/api/user-tasks/5/roles"),
            "manager", "RoleManager");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Empty(factory.Roles.Calls);
    }

    public class RecordingRoles : DispatchProxy
    {
        public UserTaskRolePolicyDto? Policy { get; set; }
        public UserTaskRolesChangeAckDto? Change { get; set; }
        public List<(string Method, long Id, ChangeUserTaskRolesRequest? Request, ActorContext Actor, CancellationToken Token)> Calls { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            var method = targetMethod?.Name ?? string.Empty;
            var post = method.StartsWith("Change", StringComparison.Ordinal);
            var id = (long)args![0]!;
            var request = post ? (ChangeUserTaskRolesRequest)args[1]! : null;
            var actor = (ActorContext)args[post ? 2 : 1]!;
            var token = (CancellationToken)args[post ? 3 : 2]!;
            Calls.Add((method, id, request, actor, token));
            return post
                ? Task.FromResult(Change)
                : Task.FromResult(Policy);
        }
    }

    internal sealed class RoleApiFactory(string? claimType = null) : WebApplicationFactory<Program>
    {
        private readonly IUserTaskRoleManagementService roles =
            DispatchProxy.Create<IUserTaskRoleManagementService, RecordingRoles>();

        public RecordingRoles Roles => (RecordingRoles)(object)roles;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Flowbit"] =
                        "Host=127.0.0.1;Port=1;Database=role_contract;Username=test;Password=test;Timeout=1",
                    ["Jwt:Issuer"] = ApiTestAuth.Issuer,
                    ["Jwt:Audience"] = ApiTestAuth.Audience,
                    ["Jwt:Key"] = ApiTestAuth.Key,
                    ["WorkflowAudit:AllowedClaims:0"] = "department",
                    ["Serilog:WriteTo:0:Name"] = "Console"
                }));
            builder.ConfigureTestServices(services =>
            {
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.TokenValidationParameters.ValidIssuer = ApiTestAuth.Issuer;
                    options.TokenValidationParameters.ValidAudience = ApiTestAuth.Audience;
                    options.TokenValidationParameters.IssuerSigningKey =
                        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ApiTestAuth.Key));
                });
                services.RemoveAll<IUserTaskRoleManagementService>();
                services.AddSingleton(roles);
                services.RemoveAll<IWorkflowEngineService>();
                services.AddScoped<IWorkflowEngineService>(_ =>
                    throw new InvalidOperationException("Role routes must not resolve the engine."));
                services.RemoveAll<IEngineSettingsService>();
                services.AddSingleton<IEngineSettingsService>(new IdentitySettings(claimType));
            });
        }
    }

    private sealed class IdentitySettings(string? claimType) : IEngineSettingsService
    {
        public Task<EngineSettingRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken)
        {
            Assert.Equal(ActorIdentityConfiguration.SettingKey, key);
            return Task.FromResult(claimType is null ? null : new EngineSettingRecord(
                1, "Authentication", "UserIdentityClaim", claimType,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        }

        public Task<IReadOnlyList<EngineSettingRecord>> SearchAsync(string pattern, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<EngineSettingRecord> SetAsync(string key, string value, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }
}
