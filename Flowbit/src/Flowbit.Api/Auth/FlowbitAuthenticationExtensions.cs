using System.Text;
using System.Security.Claims;
using Flowbit.Service.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace Flowbit.Api.Auth;

public static class FlowbitAuthenticationExtensions
{
    public static IServiceCollection AddFlowbitAuthentication(
        this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<FlowbitAuthenticationOptions>()
            .Bind(configuration.GetSection(FlowbitAuthenticationOptions.SectionName))
            .Validate(settings => { settings.Validate(environment); return true; })
            .ValidateOnStart();
        services.AddSingleton(provider => provider.GetRequiredService<IOptions<FlowbitAuthenticationOptions>>().Value);

        var authentication = services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<FlowbitAuthenticationOptions>, IConfiguration>((options, configured, finalConfiguration) =>
            {
                var settings = configured.Value;
                options.TokenValidationParameters = ValidationParameters();
                if (settings.Mode == FlowbitAuthenticationOptions.DevelopmentTokenMode)
                {
                    var issuer = finalConfiguration["Jwt:Issuer"];
                    var audience = finalConfiguration["Jwt:Audience"];
                    var key = finalConfiguration["Jwt:Key"];
                    if (string.IsNullOrWhiteSpace(issuer) || string.IsNullOrWhiteSpace(audience)
                        || string.IsNullOrWhiteSpace(key) || Encoding.UTF8.GetByteCount(key) < 32)
                        throw new InvalidOperationException("DevelopmentToken requires Jwt:Issuer, Jwt:Audience, and a Jwt:Key of at least 32 UTF-8 bytes.");
                    options.TokenValidationParameters.ValidIssuer = issuer;
                    options.TokenValidationParameters.ValidAudience = audience;
                    options.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key));
                    options.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.HmacSha256];
                    return;
                }
                options.Authority = settings.Oidc.Authority;
                options.Audience = settings.Oidc.Audience;
                options.RequireHttpsMetadata = !settings.Oidc.AllowHttpMetadata;
                options.BackchannelTimeout = TimeSpan.FromSeconds(15);
                options.MapInboundClaims = false;
                options.SaveToken = false;
                options.IncludeErrorDetails = false;
                // Pin the trusted issuer as well as discovery: a compromised metadata
                // response must not silently select a different issuer.
                options.TokenValidationParameters.ValidIssuer = settings.Oidc.Authority;
                options.TokenValidationParameters.IssuerValidator = (issuer, _, _) =>
                    string.Equals(issuer, settings.Oidc.Authority, StringComparison.Ordinal)
                        ? issuer
                        : throw new SecurityTokenInvalidIssuerException("The token issuer is not trusted.");
                options.TokenValidationParameters.ValidAudience = settings.Oidc.Audience;
                options.TokenValidationParameters.NameClaimType = settings.Oidc.NameClaimType;
                options.TokenValidationParameters.RoleClaimType = settings.Oidc.RoleClaimType;
                options.TokenValidationParameters.ValidAlgorithms =
                [
                    SecurityAlgorithms.RsaSha256, SecurityAlgorithms.RsaSha384, SecurityAlgorithms.RsaSha512,
                    SecurityAlgorithms.RsaSsaPssSha256, SecurityAlgorithms.RsaSsaPssSha384, SecurityAlgorithms.RsaSsaPssSha512,
                    SecurityAlgorithms.EcdsaSha256, SecurityAlgorithms.EcdsaSha384, SecurityAlgorithms.EcdsaSha512
                ];
                options.Events = new JwtBearerEvents
                {
                    OnAuthenticationFailed = _ =>
                    {
                        FlowbitAuthenticationDiagnostics.InvalidToken();
                        return Task.CompletedTask;
                    },
                    OnTokenValidated = context =>
                    {
                        try
                        {
                            if (context.Principal!.Identities
                                .SelectMany(identity => identity.FindAll(identity.RoleClaimType))
                                .Any(claim => claim.ValueType != ClaimValueTypes.String
                                    || string.IsNullOrWhiteSpace(claim.Value)))
                            {
                                context.Fail("Access token roles must be nonblank strings.");
                                FlowbitAuthenticationDiagnostics.InvalidToken();
                                return Task.CompletedTask;
                            }
                            var actor = context.HttpContext.RequestServices
                                .GetRequiredService<IActorContextResolver>().Resolve(context.Principal!);
                            if (string.IsNullOrWhiteSpace(actor.User))
                                context.Fail("The access token does not identify a workflow actor.");
                        }
                        catch (WorkflowUnauthorizedException)
                        {
                            context.Fail("The access token does not contain one valid workflow actor identity.");
                        }
                        if (context.Result?.Failure is not null) FlowbitAuthenticationDiagnostics.InvalidToken();
                        else FlowbitAuthenticationDiagnostics.ValidToken();
                        return Task.CompletedTask;
                    }
                };
            }).ValidateOnStart();

        authentication.AddScheme<SharedVariableClientAuthenticationOptions, SharedVariableClientAuthenticationHandler>(
            SharedVariableClientAuthenticationDefaults.Scheme, options =>
            {
                options.MaxClientIdRunes = 300;
                options.MaxClientSecretBytes = 512;
            });
        services.AddAuthorization(options =>
        {
            options.DefaultPolicy = new AuthorizationPolicyBuilder()
                .RequireAuthenticatedUser()
                .AddRequirements(new FlowbitBearerScopeRequirement())
                .Build();
            SharedVariableAuthorizationPolicies.AddPolicies(options);
        });
        services.AddSingleton<IAuthorizationHandler, FlowbitBearerScopeHandler>();
        services.AddScoped<IAuthorizationHandler, SharedVariableAccessAuthorizationHandler>();
        services.AddScoped<ISharedVariableCallerResolver, SharedVariableCallerResolver>();
        return services;
    }

    private static TokenValidationParameters ValidationParameters() => new()
    {
        AuthenticationType = JwtBearerDefaults.AuthenticationScheme,
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateLifetime = true,
        ValidateIssuerSigningKey = true,
        RequireSignedTokens = true,
        RequireExpirationTime = true,
        ClockSkew = TimeSpan.FromSeconds(60)
    };
}

public sealed record FlowbitBearerScopeRequirement : IAuthorizationRequirement;

public sealed class FlowbitBearerScopeHandler(FlowbitAuthenticationOptions settings)
    : AuthorizationHandler<FlowbitBearerScopeRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, FlowbitBearerScopeRequirement requirement)
    {
        if (settings.Mode == FlowbitAuthenticationOptions.DevelopmentTokenMode
            || context.User.Identities.Any(identity => identity.IsAuthenticated
                && identity.AuthenticationType == SharedVariableClientAuthenticationDefaults.Scheme)
            || context.User.Identities.Where(identity => identity.IsAuthenticated
                    && identity.AuthenticationType == JwtBearerDefaults.AuthenticationScheme)
                .SelectMany(identity => identity.FindAll(settings.Oidc.ScopeClaimType))
                .Where(claim => claim.ValueType == ClaimValueTypes.String)
                .SelectMany(claim => claim.Value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                .Contains(settings.Oidc.RequiredScope, StringComparer.Ordinal))
            context.Succeed(requirement);
        return Task.CompletedTask;
    }
}
