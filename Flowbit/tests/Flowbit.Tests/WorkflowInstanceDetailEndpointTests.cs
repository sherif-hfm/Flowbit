using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Flowbit.Api.Auth;
using Flowbit.Api.Endpoints;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.IdentityModel.Tokens;
using Xunit;

namespace Flowbit.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InstanceDetailApiContractCollection
{
    // Program owns a process-wide Serilog bootstrap logger. These production
    // hosts must not start alongside another WebApplicationFactory<Program>.
    public const string Name = "instance-detail-api-contract";
}

[Collection(InstanceDetailApiContractCollection.Name)]
public sealed class WorkflowInstanceDetailEndpointTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HandlerPreservesDetailResultIdAndCancellationToken(bool found)
    {
        var service = DispatchProxy.Create<IWorkflowInstanceProjectionService, RecordingProjection>();
        var recorder = (RecordingProjection)(object)service;
        recorder.Detail = found ? Detail() : null;
        var configuration = new ActorIdentityConfiguration();
        configuration.Initialize(null);
        using var cancellation = new CancellationTokenSource();

        var result = await WorkflowInstanceEndpoints.GetInstance(
            5, new ClaimsPrincipal(new ClaimsIdentity([], "test")),
            new ActorContextResolver(configuration), service, cancellation.Token);

        Assert.Equal((5L, cancellation.Token), Assert.Single(recorder.Reads));
        if (found)
            Assert.Same(recorder.Detail, Assert.IsType<Ok<InstanceDetailDto>>(result).Value);
        else
            Assert.IsType<NotFound>(result);
    }

    [Theory]
    [InlineData(null, true)]
    [InlineData("preferred_username", true)]
    [InlineData("preferred_username", false)]
    public async Task AuthenticatedDetailUsesProjectionWithoutResolvingEngine(string? claimType, bool found)
    {
        await using var factory = new DetailApiFactory(claimType);
        factory.Projection.Detail = found ? Detail() : null;
        using var client = factory.CreateClient();
        using var request = ApiTestAuth.AuthorizeWithClaims(
            new HttpRequestMessage(HttpMethod.Get, "/api/instances/5"), "reader",
            [new Claim("preferred_username", "canonical-reader")], "unrelated-role");
        using var response = await client.SendAsync(request);

        Assert.Equal(found ? HttpStatusCode.OK : HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(5, Assert.Single(factory.Projection.Reads).Id);
        if (found)
        {
            var actual = await response.Content.ReadFromJsonAsync<InstanceDetailDto>();
            Assert.Equal(JsonSerializer.Serialize(factory.Projection.Detail), JsonSerializer.Serialize(actual));
        }
    }

    [Fact]
    public async Task AnonymousDetailIsRejectedByBearerMiddlewareBeforeProjection()
    {
        await using var factory = new DetailApiFactory();
        using var client = factory.CreateClient();
        using var response = await client.GetAsync("/api/instances/5");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains(response.Headers.WwwAuthenticate, header => header.Scheme == "Bearer");
        Assert.Empty(factory.Projection.Reads);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("blank")]
    [InlineData("conflicting")]
    public async Task InvalidCanonicalIdentityIsRejectedBeforeProjection(string identity)
    {
        await using var factory = new DetailApiFactory("preferred_username");
        using var client = factory.CreateClient();
        Claim[] claims = identity switch
        {
            "missing" => [],
            "blank" => [new("preferred_username", " ")],
            _ => [new("preferred_username", "alice"), new("preferred_username", "bob")]
        };
        using var request = ApiTestAuth.AuthorizeWithClaims(
            new HttpRequestMessage(HttpMethod.Get, "/api/instances/5"), "reader", claims, "unrelated-role");
        using var response = await client.SendAsync(request);
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("preferred_username", await response.Content.ReadAsStringAsync());
        Assert.Empty(factory.Projection.Reads);
    }

    private static InstanceDetailDto Detail() => new(
        5, new WorkflowDetailDto(9, "Review", "review", 1, true, true,
            DateTimeOffset.UnixEpoch, new WorkflowModel()),
        3, "Review", "REVIEW", "running", null, null, "alice",
        DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, [], [], null, null);

    public class RecordingProjection : DispatchProxy
    {
        public InstanceDetailDto? Detail { get; set; }
        public List<(long Id, CancellationToken Token)> Reads { get; } = [];

        protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
        {
            Assert.Equal(nameof(IWorkflowInstanceProjectionService.GetDetailAsync), targetMethod?.Name);
            Reads.Add(((long)args![0]!, (CancellationToken)args[1]!));
            return Task.FromResult(Detail);
        }
    }

    internal sealed class DetailApiFactory(string? claimType = null) : WebApplicationFactory<Program>
    {
        private readonly IWorkflowInstanceProjectionService projection =
            DispatchProxy.Create<IWorkflowInstanceProjectionService, RecordingProjection>();

        public RecordingProjection Projection => (RecordingProjection)(object)projection;

        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Flowbit"] =
                        "Host=127.0.0.1;Port=1;Database=detail_contract;Username=test;Password=test;Timeout=1",
                    ["Jwt:Issuer"] = ApiTestAuth.Issuer,
                    ["Jwt:Audience"] = ApiTestAuth.Audience,
                    ["Jwt:Key"] = ApiTestAuth.Key,
                    ["Serilog:WriteTo:0:Name"] = "Console"
                }));
            builder.ConfigureTestServices(services =>
            {
                // Program captures JWT settings before the factory's configuration
                // callback. Keep real bearer validation with isolated test keys.
                services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
                {
                    options.TokenValidationParameters.ValidIssuer = ApiTestAuth.Issuer;
                    options.TokenValidationParameters.ValidAudience = ApiTestAuth.Audience;
                    options.TokenValidationParameters.IssuerSigningKey =
                        new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ApiTestAuth.Key));
                });
                services.RemoveAll<IWorkflowInstanceProjectionService>();
                services.AddSingleton(projection);
                services.RemoveAll<IWorkflowEngineService>();
                services.AddScoped<IWorkflowEngineService>(_ =>
                    throw new InvalidOperationException("Detail GET must not resolve the engine."));
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
