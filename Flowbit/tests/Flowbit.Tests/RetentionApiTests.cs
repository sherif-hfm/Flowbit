using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Flowbit.Api.Auth;
using Flowbit.Api.Endpoints;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Xunit;

namespace Flowbit.Tests;

public sealed class RetentionApiTests
{
    [Theory]
    [InlineData("GET", "/api/retention")]
    [InlineData("PUT", "/api/retention/policies/workflowHistory")]
    [InlineData("POST", "/api/retention/preview")]
    [InlineData("POST", "/api/retention/runs")]
    public async Task EveryRetentionRouteRequiresAnAuthenticatedSettingsAdministrator(string method, string path)
    {
        await using var host = await TestHost.CreateAsync();
        using var anonymous = await host.SendAsync(method, path, authenticated: false);
        Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);

        using var viewer = await host.SendAsync(method, path, roles: "viewer");
        Assert.Equal(HttpStatusCode.Forbidden, viewer.StatusCode);
        Assert.Equal(0, host.Repository.Calls);
    }

    [Fact]
    public async Task DynamicSettingsRolesApplyCaseInsensitivelyToRetention()
    {
        await using var host = await TestHost.CreateAsync();
        host.Settings.RequiredRoles = " RetentionManager, Operations ";

        using var previousAdmin = await host.SendAsync("GET", "/api/retention", roles: "admin");
        Assert.Equal(HttpStatusCode.Forbidden, previousAdmin.StatusCode);
        using var allowed = await host.SendAsync("GET", "/api/retention", roles: "retentionmanager");
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        var status = await allowed.Content.ReadFromJsonAsync<RetentionStatusDto>();
        Assert.NotNull(status);
        Assert.Equal(RetentionCategories.WorkflowHistory, Assert.Single(status.Policies).Category);
        Assert.Equal(1, host.Repository.Calls);
    }

    [Fact]
    public async Task PolicyAndDraftPreviewPreserveTypedInputsAndOnlySavingMutatesThePolicy()
    {
        await using var host = await TestHost.CreateAsync();
        var previewRequest = new PreviewRetentionRequest(RetentionCategories.WorkflowHistory, 45);
        using var preview = await host.SendAsync("POST", "/api/retention/preview", previewRequest);
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        Assert.Equal(previewRequest, host.Repository.PreviewRequest);
        Assert.Null(host.Repository.UpdateRequest);
        Assert.Equal(0, host.Repository.RunRequests);
        var previewResult = await preview.Content.ReadFromJsonAsync<RetentionPreviewDto>();
        Assert.Equal(7, Assert.Single(previewResult!.Tables).EligibleCount);

        var updateRequest = new UpdateRetentionPolicyRequest(45, 3);
        using var update = await host.SendAsync(
            "PUT", "/api/retention/policies/workflowHistory", updateRequest);
        Assert.Equal(HttpStatusCode.OK, update.StatusCode);
        Assert.Equal(updateRequest, host.Repository.UpdateRequest);
        Assert.Equal(RetentionCategories.WorkflowHistory, host.Repository.UpdatedCategory);
        Assert.Equal("retention-admin", host.Repository.Actor);
        var updated = await update.Content.ReadFromJsonAsync<RetentionPolicyDto>();
        Assert.Equal(4, updated!.Revision);
        Assert.Equal(45, updated.RetentionDays);
        Assert.Equal(0, host.Repository.RunRequests);
    }

    [Fact]
    public async Task MalformedPolicyInputsAreRejectedBeforeTheRepositoryIsCalled()
    {
        await using var host = await TestHost.CreateAsync();
        using var update = await host.SendAsync("PUT", "/api/retention/policies/workflowHistory",
            new { retentionDays = "yesterday", expectedRevision = 3 });
        using var preview = await host.SendAsync("POST", "/api/retention/preview",
            new { category = RetentionCategories.WorkflowHistory, retentionDays = 1.5 });

        Assert.Equal(HttpStatusCode.BadRequest, update.StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        Assert.Equal(0, host.Repository.Calls);
    }

    [Fact]
    public async Task RunRequestOnlyQueuesSavedPoliciesAndNeverExecutesCleanupInsideTheApi()
    {
        await using var host = await TestHost.CreateAsync();
        using var response = await host.SendAsync("POST", "/api/retention/runs");

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("/api/retention", response.Headers.Location?.OriginalString);
        var run = await response.Content.ReadFromJsonAsync<RetentionRunDto>();
        Assert.Equal(host.Repository.Run.Id, run!.Id);
        Assert.Equal("queued", run.Status);
        Assert.Equal(1, host.Repository.RunRequests);
        Assert.Equal("retention-admin", host.Repository.Actor);
        Assert.Null(host.Repository.UpdateRequest);
        Assert.Null(host.Repository.PreviewRequest);
    }

    private sealed class TestHost(WebApplication app, HttpClient client, StubRepository repository,
        StubSettings settings) : IAsyncDisposable
    {
        public StubRepository Repository => repository;
        public StubSettings Settings => settings;

        public static async Task<TestHost> CreateAsync()
        {
            var builder = WebApplication.CreateBuilder();
            builder.WebHost.UseTestServer();
            var repository = new StubRepository();
            var settings = new StubSettings();
            var identity = new ActorIdentityConfiguration();
            identity.Initialize(null);
            builder.Services.AddSingleton(identity);
            builder.Services.AddSingleton<IActorContextResolver, ActorContextResolver>();
            builder.Services.AddSingleton<IRetentionRepository>(repository);
            builder.Services.AddSingleton<IEngineSettingsService>(settings);
            builder.Services.AddAuthorization();
            builder.Services.AddAuthentication("RetentionTests")
                .AddScheme<AuthenticationSchemeOptions, TestAuthenticationHandler>("RetentionTests", _ => { });
            var app = builder.Build();
            app.UseAuthentication();
            app.UseAuthorization();
            app.MapRetentionEndpoints();
            await app.StartAsync();
            return new TestHost(app, app.GetTestClient(), repository, settings);
        }

        public async Task<HttpResponseMessage> SendAsync(string method, string path, object? body = null,
            bool authenticated = true, string roles = "admin")
        {
            using var request = new HttpRequestMessage(new HttpMethod(method), path);
            if (authenticated)
            {
                request.Headers.Add("X-Retention-Test-User", "retention-admin");
                request.Headers.Add("X-Retention-Test-Roles", roles);
            }
            body ??= method == "PUT"
                ? new UpdateRetentionPolicyRequest(30, 3)
                : path.EndsWith("/preview", StringComparison.Ordinal)
                    ? new PreviewRetentionRequest(RetentionCategories.WorkflowHistory, 30)
                    : null;
            if (body is not null)
                request.Content = JsonContent.Create(body);
            return await client.SendAsync(request);
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.DisposeAsync();
        }
    }

    private sealed class StubRepository : IRetentionRepository
    {
        private readonly DateTimeOffset observedAt = DateTimeOffset.UtcNow;
        public int Calls { get; private set; }
        public int RunRequests { get; private set; }
        public string? Actor { get; private set; }
        public string? UpdatedCategory { get; private set; }
        public UpdateRetentionPolicyRequest? UpdateRequest { get; private set; }
        public PreviewRetentionRequest? PreviewRequest { get; private set; }
        public RetentionRunDto Run { get; } = new(Guid.NewGuid(), "queued", DateTimeOffset.UtcNow,
            "retention-admin", null, null, null, []);

        public Task<RetentionStatusDto> GetAsync(CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new RetentionStatusDto(
                [new(RetentionCategories.WorkflowHistory, null, 3, observedAt, null, true)],
                null, null, observedAt.AddHours(1), observedAt));
        }

        public Task<RetentionPolicyDto> UpdatePolicyAsync(string category, UpdateRetentionPolicyRequest request,
            string actor, CancellationToken cancellationToken)
        {
            Calls++;
            UpdatedCategory = category;
            UpdateRequest = request;
            Actor = actor;
            return Task.FromResult(new RetentionPolicyDto(category, request.RetentionDays,
                request.ExpectedRevision + 1, observedAt, actor, true));
        }

        public Task<RetentionPreviewDto> PreviewAsync(PreviewRetentionRequest request, CancellationToken cancellationToken)
        {
            Calls++;
            PreviewRequest = request;
            return Task.FromResult(new RetentionPreviewDto(request.Category, observedAt.AddDays(-45),
                [new("instance_history", 7, 2)], false, observedAt));
        }

        public Task<RetentionRunDto> RequestRunAsync(string actor, CancellationToken cancellationToken)
        {
            Calls++;
            RunRequests++;
            Actor = actor;
            return Task.FromResult(Run);
        }

        public Task InitializeAsync(int completedJobDays, int resolvedIncidentDays, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("API requests cannot initialize worker defaults.");
        public Task<RetentionTickResult> ProcessBatchAsync(RetentionExecutionOptions options, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("API requests cannot execute cleanup.");
    }

    private sealed class StubSettings : IEngineSettingsService
    {
        public string? RequiredRoles { get; set; }
        public Task<EngineSettingRecord?> GetByKeyAsync(string key, CancellationToken cancellationToken)
        {
            Assert.Equal("Settings.RequiredRole", key);
            return Task.FromResult(RequiredRoles is null ? null : new EngineSettingRecord(
                1, "Settings", "RequiredRole", RequiredRoles, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        }
        public Task<IReadOnlyList<EngineSettingRecord>> SearchAsync(string pattern, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<EngineSettingRecord> SetAsync(string key, string value, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<bool> DeleteAsync(string key, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class TestAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger, UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
        {
            var user = Request.Headers["X-Retention-Test-User"].FirstOrDefault();
            if (user is null)
                return Task.FromResult(AuthenticateResult.NoResult());
            var claims = new List<Claim> { new(ClaimTypes.Name, user) };
            foreach (var role in Request.Headers["X-Retention-Test-Roles"].ToString().Split(',', StringSplitOptions.RemoveEmptyEntries))
                claims.Add(new Claim(ClaimTypes.Role, role));
            return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(
                new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme.Name)), Scheme.Name)));
        }
    }
}
