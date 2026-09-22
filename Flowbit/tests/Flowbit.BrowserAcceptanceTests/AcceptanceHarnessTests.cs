using System.Net;
using Flowbit.BrowserTests.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Flowbit.BrowserAcceptanceTests;

public sealed class AcceptanceHarnessTests
{
    [Fact]
    public async Task DefaultSmokeStackRejectsWorkerWithoutStartingResources()
    {
        var smoke = new BrowserStackFixture();
        Assert.False(smoke.IsAcceptance);
        Assert.Equal(TimeSpan.FromMinutes(2), smoke.ScenarioBudget);
        await Assert.ThrowsAsync<InvalidOperationException>(smoke.StartWorkerAsync);
        Assert.True(BrowserStackFixture.CreateForAcceptance(new()).IsAcceptance);
    }

    [Fact]
    public async Task ProxyIgnoresAmbientKestrelEndpoints()
    {
        await using var host = await StartEchoAsync();
        const string setting = "Kestrel__Endpoints__Unexpected__Url";
        var previous = Environment.GetEnvironmentVariable(setting);
        try
        {
            // This invalid production-style endpoint must never reach this in-process host.
            Environment.SetEnvironmentVariable(setting, "http://0.0.0.0:1");
            await using var proxy = new RecordingApiProxy();
            await proxy.StartAsync(host.Urls.Single());
            Assert.Equal("127.0.0.1", new Uri(proxy.BaseAddress).Host);
            Assert.NotEqual(1, new Uri(proxy.BaseAddress).Port);
            using var client = new HttpClient();
            using var response = await client.GetAsync(proxy.BaseAddress + "/echo");
            Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        }
        finally { Environment.SetEnvironmentVariable(setting, previous); }
    }

    [Fact]
    public async Task GateHoldsOneRealResponseAndPreservesTransportWithoutRecordingSecrets()
    {
        await using var host = await StartEchoAsync();
        await using var proxy = new RecordingApiProxy();
        await proxy.StartAsync(host.Urls.Single());
        using var client = new HttpClient { BaseAddress = new Uri(proxy.BaseAddress) };
        using var gate = proxy.GateNextResponse(r => r.PathAndQuery == "/echo?item=1");
        using var request = new HttpRequestMessage(HttpMethod.Post, "/echo?item=1");
        request.Headers.Authorization = new("Bearer", "fixture-secret");
        request.Content = new StringContent("real upstream body");
        var pending = client.SendAsync(request);
        var entered = await gate.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(201, entered.StatusCode);
        Assert.False(pending.IsCompleted);
        using var other = await client.GetAsync("/echo?item=2");
        Assert.Equal(HttpStatusCode.Created, other.StatusCode);
        Assert.Equal("Bearer fixture-secret", ((IApplicationBuilder)host).Properties["authorization"]);
        gate.Release();
        using var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("real upstream body", await response.Content.ReadAsStringAsync());
        Assert.Equal("kept", response.Headers.GetValues("X-Acceptance-Test").Single());
        Assert.Contains(proxy.Requests, r => r.PathAndQuery == "/echo?item=1" && r.CompletedAt is not null);
        Assert.DoesNotContain("fixture-secret", System.Text.Json.JsonSerializer.Serialize(proxy.Requests));
    }

    [Fact]
    public async Task ReleasingAllGatesDrainsHeldRequestsAndDisarmsUnusedGates()
    {
        await using var host = await StartEchoAsync();
        await using var proxy = new RecordingApiProxy();
        await proxy.StartAsync(host.Urls.Single());
        using var client = new HttpClient { BaseAddress = new Uri(proxy.BaseAddress) };
        using var held = proxy.GateNextResponse(r => r.PathAndQuery == "/echo?held");
        using var unused = proxy.GateNextResponse(r => r.PathAndQuery == "/echo?unused");
        var pending = client.GetAsync("/echo?held");
        await held.Entered.WaitAsync(TimeSpan.FromSeconds(5));
        proxy.ReleaseAllGates();
        using var released = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        using var next = await client.GetAsync("/echo?unused").WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(HttpStatusCode.Created, released.StatusCode);
        Assert.Equal(HttpStatusCode.Created, next.StatusCode);
        Assert.False(unused.Entered.IsCompleted);
    }

    private static async Task<WebApplication> StartEchoAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration.Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        app.MapMethods("/echo", ["GET", "POST"], async context =>
        {
            if (context.Request.Headers.Authorization.Count > 0)
                ((IApplicationBuilder)app).Properties["authorization"] = context.Request.Headers.Authorization.ToString();
            context.Response.StatusCode = 201;
            context.Response.Headers["X-Acceptance-Test"] = "kept";
            using var reader = new StreamReader(context.Request.Body);
            await context.Response.WriteAsync(await reader.ReadToEndAsync());
        });
        await app.StartAsync();
        return app;
    }
}
