extern alias FlowbitUi;

using Flowbit.Api.Auth;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Xunit;
using Ui = FlowbitUi::Flowbit.Ui.Auth;

namespace Flowbit.Tests;

public sealed class UiOidcHostingValidationTests
{
    [Theory]
    [InlineData("oidc")]
    [InlineData("session")]
    [InlineData("scopes")]
    [InlineData("empty")]
    [InlineData("null-scope")]
    [InlineData("combined")]
    [InlineData("whitespace")]
    public void Malformed_ui_options_have_actionable_validation_errors(string malformed)
    {
        var options = ValidOptions();
        switch (malformed)
        {
            case "oidc": options.Oidc = null!; break;
            case "session": options.Session = null!; break;
            case "scopes": options.Oidc.Scopes = null!; break;
            case "empty": options.Oidc.Scopes = []; break;
            case "null-scope": options.Oidc.Scopes = ["openid", null!]; break;
            case "combined": options.Oidc.Scopes = ["openid", "flowbit.api extra"]; break;
            case "whitespace": options.Oidc.Scopes = ["openid", " "]; break;
        }
        Assert.Contains("Authentication:", Assert.Throws<InvalidOperationException>(() => options.Validate(new Environment("Development"))).Message);
    }

    [Fact]
    public void Malformed_api_options_fail_cleanly()
    {
        var options = new FlowbitAuthenticationOptions { Oidc = null! };
        Assert.Contains("Authentication:Oidc", Assert.Throws<InvalidOperationException>(() => options.Validate(new Environment("Development"))).Message);
        options.Oidc = new() { Authority = "https://issuer.test", Audience = " api " };
        Assert.Contains("Audience", Assert.Throws<InvalidOperationException>(() => options.Validate(new Environment("Development"))).Message);
    }

    [Theory]
    [InlineData("Production", "http", "/auth/login", false, 400)]
    [InlineData("Production", "http", "/auth/logout", false, 400)]
    [InlineData("Production", "http", "/signin-oidc", false, 400)]
    [InlineData("Production", "http", "/signout-callback-oidc", false, 400)]
    [InlineData("Production", "http", "/_blazor/negotiate", false, 400)]
    [InlineData("Production", "http", "/workflows", true, 400)]
    [InlineData("Production", "https", "/workflows", true, 200)]
    [InlineData("Production", "http", "/health/live", false, 200)]
    [InlineData("Production", "http", "/_framework/blazor.web.js", false, 200)]
    [InlineData("Development", "http", "/auth/login", false, 200)]
    public async Task Https_boundary_protects_oidc_and_protected_routes_while_allowing_probes(
        string environment, string scheme, string path, bool authorized, int expectedStatus)
    {
        using var services = new ServiceCollection().AddLogging().BuildServiceProvider();
        var builder = new ApplicationBuilder(services);
        Ui.UiHttpsBoundary.UseFlowbitHttpsBoundary(builder, ValidOptions(), new Environment(environment));
        builder.Run(context => { context.Response.StatusCode = 200; return Task.CompletedTask; });
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Scheme = scheme;
        context.Request.Path = path;
        context.Response.Body = new MemoryStream();
        if (authorized) context.SetEndpoint(new Endpoint(_ => Task.CompletedTask,
            new EndpointMetadataCollection(new AuthorizeAttribute()), "protected"));

        await builder.Build()(context);

        Assert.Equal(expectedStatus, context.Response.StatusCode);
    }

    private static Ui.UiAuthenticationOptions ValidOptions() => new()
    {
        Oidc = new() { Authority = "https://issuer.test", ClientId = "ui", ClientSecret = "test" }
    };

    private sealed class Environment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }
}
