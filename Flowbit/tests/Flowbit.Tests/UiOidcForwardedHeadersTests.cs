extern alias FlowbitUi;

using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;
using Ui = FlowbitUi::Flowbit.Ui.Auth;

namespace Flowbit.Tests;

public sealed class UiOidcForwardedHeadersTests
{
    [Fact]
    public async Task Configured_host_allowlist_also_restricts_forwarded_host_values()
    {
        await using var app = CreateApp("10.0.0.2");
        var options = app.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        Assert.Equal(["ui.flowbit.test", "public.flowbit.test"], options.AllowedHosts);
        Assert.Contains(IPAddress.Parse("10.0.0.2"), options.KnownProxies);
    }

    [Theory]
    [InlineData("10.0.0.2", "public.flowbit.test", "public.flowbit.test", "https")]
    [InlineData("10.0.0.2", "attacker.test", "ui.flowbit.test", "https")]
    [InlineData("10.0.0.99", "public.flowbit.test", "ui.flowbit.test", "http")]
    public async Task Only_trusted_proxy_and_allowed_host_can_change_the_public_request_origin(
        string remoteAddress, string forwardedHost, string expectedHost, string expectedScheme)
    {
        await using var app = CreateApp(remoteAddress);
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://ui.flowbit.test/origin");
        request.Headers.Add("X-Forwarded-Host", forwardedHost);
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "203.0.113.10");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var origin = await response.Content.ReadFromJsonAsync<RequestOrigin>();
        Assert.Equal(expectedHost, origin!.Host);
        Assert.Equal(expectedScheme, origin.Scheme);
    }

    private static WebApplication CreateApp(string remoteAddress)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Development" });
        TestWebHostDefaults.IsolateFromMachine(builder);
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Authentication:Mode"] = "OpenIdConnect",
            ["Authentication:Oidc:Authority"] = "https://identity.flowbit.test",
            ["Authentication:Oidc:ClientId"] = "flowbit-ui",
            ["Authentication:Oidc:ClientSecret"] = "synthetic-test-secret",
            ["AllowedHosts"] = " ui.flowbit.test ; public.flowbit.test ",
            ["ReverseProxy:KnownProxies:0"] = "10.0.0.2"
        });
        Ui.UiAuthentication.AddFlowbitUiAuthentication(builder);
        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);
            return next(context);
        });
        app.UseForwardedHeaders();
        app.MapGet("/origin", (HttpContext context) => Results.Json(new RequestOrigin(context.Request.Host.Value ?? "", context.Request.Scheme)));
        return app;
    }

    private sealed record RequestOrigin(string Host, string Scheme);
}
