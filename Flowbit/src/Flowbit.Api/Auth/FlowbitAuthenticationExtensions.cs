using System.Text;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using Flowbit.Service.Services;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using JsonWebToken = Microsoft.IdentityModel.JsonWebTokens.JsonWebToken;

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
                if (settings.Mode == FlowbitAuthenticationOptions.LocalMode)
                {
                    options.MapInboundClaims = false;
                    options.IncludeErrorDetails = false;
                    options.TokenValidationParameters.ValidIssuer = settings.Local.Issuer;
                    options.TokenValidationParameters.ValidAudience = settings.Local.Audience;
                    options.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(settings.Local.SigningKey));
                    options.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.HmacSha256];
                    options.TokenValidationParameters.RoleClaimType = settings.Local.RoleClaimType;
                    options.Events = new JwtBearerEvents { OnTokenValidated = context => ValidateActor(context) };
                    return;
                }
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
                    options.Events = new JwtBearerEvents { OnTokenValidated = context => ValidateActor(context, validateRoles: false) };
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
                    OnTokenValidated = async context =>
                    {
                        await ValidateActor(context);
                        if (context.Result?.Failure is not null) FlowbitAuthenticationDiagnostics.InvalidToken();
                        else FlowbitAuthenticationDiagnostics.ValidToken();
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

    private static Task ValidateActor(TokenValidatedContext context, bool validateRoles = true)
    {
        try
        {
            PreserveSubjectValueType(context);
            if (validateRoles && context.Principal!.Identities
                .SelectMany(identity => identity.FindAll(identity.RoleClaimType))
                .Any(claim => claim.ValueType != ClaimValueTypes.String || string.IsNullOrWhiteSpace(claim.Value)))
            {
                context.Fail("Access token roles must be nonblank strings.");
                return Task.CompletedTask;
            }
            var actor = context.HttpContext.RequestServices.GetRequiredService<IActorContextResolver>().Resolve(context.Principal!);
            if (string.IsNullOrWhiteSpace(actor.User)) context.Fail("The access token does not identify a workflow actor.");
        }
        catch (WorkflowUnauthorizedException)
        {
            context.Fail("The access token does not contain one valid workflow actor identity.");
        }
        return Task.CompletedTask;
    }

    private static void PreserveSubjectValueType(TokenValidatedContext context)
    {
        // The token handlers coerce the registered "sub" claim to a string even
        // when its signed JSON value was numeric. Preserve that distinction so
        // the resolver can reject it if this fallback/override is selected.
        var payload = context.SecurityToken switch
        {
            JsonWebToken token => token.EncodedPayload,
            JwtSecurityToken token => token.RawPayload,
            _ => null
        };
        if (string.IsNullOrEmpty(payload)) return;
        using var document = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(payload));
        if (!document.RootElement.TryGetProperty("sub", out var subject)
            || subject.ValueKind == JsonValueKind.String
            || (subject.ValueKind == JsonValueKind.Array && subject.EnumerateArray().All(value => value.ValueKind == JsonValueKind.String))) return;
        foreach (var identity in context.Principal!.Identities)
        {
            foreach (var claim in identity.Claims.Where(claim => claim.Type == "sub"
                || (context.Options.MapInboundClaims && claim.Type == ClaimTypes.NameIdentifier)).ToArray())
            {
                identity.RemoveClaim(claim);
                var replacement = new Claim(claim.Type, claim.Value, JsonClaimValueTypes.Json, claim.Issuer, claim.OriginalIssuer);
                foreach (var property in claim.Properties) replacement.Properties.Add(property);
                identity.AddClaim(replacement);
            }
        }
    }

    private static TokenValidationParameters ValidationParameters() => new()
    {
        AuthenticationType = JwtBearerDefaults.AuthenticationScheme,
        NameClaimType = ActorIdentityConfiguration.DefaultClaimType,
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
            || settings.Mode == FlowbitAuthenticationOptions.LocalMode
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
