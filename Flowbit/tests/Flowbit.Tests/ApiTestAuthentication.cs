using System.Text;
using Flowbit.Api.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.IdentityModel.Tokens;

namespace Flowbit.Tests;

/// <summary>
/// Explicit authentication replacement for existing domain/endpoint fixtures.
/// Production authentication is exercised separately by OidcBearerAuthenticationTests.
/// </summary>
internal static class ApiTestAuthentication
{
    public static void Configure(IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Authentication:Mode"] = FlowbitAuthenticationOptions.OpenIdConnectMode,
                ["Authentication:Oidc:Authority"] = "https://identity.flowbit.test",
                // Development-host fixtures still use this synthetic HTTPS provider.
                ["Authentication:Oidc:AllowHttpMetadata"] = "false",
                ["Authentication:Oidc:Audience"] = ApiTestAuth.Audience
            }));
        builder.ConfigureTestServices(services =>
        {
            // Scope validation is not the purpose of the historical test-principal
            // fixtures. This replacement is confined to their test service provider.
            services.AddSingleton(new FlowbitAuthenticationOptions
            {
                Mode = FlowbitAuthenticationOptions.DevelopmentTokenMode
            });
            services.PostConfigure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, options =>
            {
                options.Authority = null;
                options.MetadataAddress = "";
                options.MapInboundClaims = true;
                options.Events = new JwtBearerEvents();
                var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(ApiTestAuth.Key));
                var metadata = new OpenIdConnectConfiguration { Issuer = ApiTestAuth.Issuer };
                metadata.SigningKeys.Add(key);
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(metadata);
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    AuthenticationType = JwtBearerDefaults.AuthenticationScheme,
                    ValidateIssuer = true,
                    ValidIssuer = ApiTestAuth.Issuer,
                    ValidateAudience = true,
                    ValidAudience = ApiTestAuth.Audience,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    IssuerSigningKey = key,
                    ValidAlgorithms = [SecurityAlgorithms.HmacSha256]
                };
            });
        });
    }
}
