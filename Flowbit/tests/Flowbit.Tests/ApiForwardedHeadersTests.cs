using System.Net;
using System.Net.Http.Json;
using Flowbit.Api.Hosting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace Flowbit.Tests;

public sealed class ApiForwardedHeadersTests
{
    [Fact]
    public async Task Configured_host_allowlist_restricts_forwarded_hosts_without_removing_proxy_defaults()
    {
        await using var app = CreateApp("10.0.0.2");
        var options = app.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        Assert.Equal(["api.flowbit.test", "public.flowbit.test"], options.AllowedHosts);
        Assert.Contains(IPAddress.Parse("10.0.0.2"), options.KnownProxies);
        Assert.NotEmpty(options.KnownIPNetworks);
    }

    [Theory]
    [InlineData("10.0.0.2", "public.flowbit.test", "public.flowbit.test", "https", "203.0.113.10")]
    [InlineData("10.0.0.2", "attacker.test", "api.flowbit.test", "https", "203.0.113.10")]
    [InlineData("10.0.0.99", "public.flowbit.test", "api.flowbit.test", "http", "10.0.0.99")]
    public async Task Only_trusted_proxies_can_change_origin_and_client_address(
        string remoteAddress, string forwardedHost, string expectedHost, string expectedScheme, string expectedAddress)
    {
        await using var app = CreateApp(remoteAddress);
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://api.flowbit.test/origin");
        request.Headers.Add("X-Forwarded-Host", forwardedHost);
        request.Headers.Add("X-Forwarded-Proto", "https");
        request.Headers.Add("X-Forwarded-For", "203.0.113.10");

        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var origin = await response.Content.ReadFromJsonAsync<RequestOrigin>();
        Assert.Equal(expectedHost, origin!.Host);
        Assert.Equal(expectedScheme, origin.Scheme);
        Assert.Equal(expectedAddress, origin.Address);
    }

    [Fact]
    public async Task Unlisted_direct_host_is_rejected()
    {
        await using var app = CreateApp("10.0.0.2");
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("http://attacker.test/origin");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Existing_wildcard_host_default_keeps_proxy_trust_restricted()
    {
        await using var app = CreateApp("10.0.0.99", allowedHosts: "*");
        await app.StartAsync();
        using var client = app.GetTestClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://api.flowbit.test/origin");
        request.Headers.Add("X-Forwarded-Host", "attacker.test");
        request.Headers.Add("X-Forwarded-Proto", "https");
        using var response = await client.SendAsync(request);
        var origin = await response.Content.ReadFromJsonAsync<RequestOrigin>();
        Assert.Equal("api.flowbit.test", origin!.Host);
        Assert.Equal("http", origin.Scheme);
    }

    [Fact]
    public async Task Malformed_proxy_configuration_fails_with_an_actionable_error()
    {
        await using var app = CreateApp("10.0.0.2", proxy: "https://proxy.test");
        var exception = Assert.Throws<InvalidOperationException>(() => app.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value);
        Assert.Contains("ReverseProxy:KnownProxies", exception.Message);
    }

    private static WebApplication CreateApp(string remoteAddress,
        string allowedHosts = " api.flowbit.test ; public.flowbit.test ", string proxy = "10.0.0.2")
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = "Production" });
        TestWebHostDefaults.IsolateFromMachine(builder);
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AllowedHosts"] = allowedHosts,
            ["ReverseProxy:KnownProxies:0"] = proxy
        });
        builder.Services.AddFlowbitForwardedHeaders(builder.Configuration);
        var app = builder.Build();
        app.Use((context, next) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteAddress);
            return next(context);
        });
        app.UseForwardedHeaders();
        app.MapGet("/origin", (HttpContext context) => Results.Json(new RequestOrigin(
            context.Request.Host.Value ?? "", context.Request.Scheme, context.Connection.RemoteIpAddress?.ToString() ?? "")));
        return app;
    }

    private sealed record RequestOrigin(string Host, string Scheme, string Address);
}
