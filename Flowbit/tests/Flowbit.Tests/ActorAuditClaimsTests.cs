using System.Security.Claims;
using Flowbit.Api.Auth;
using Flowbit.Service.Abstractions;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Xunit;

namespace Flowbit.Tests;

public sealed class ActorAuditClaimsTests
{
    [Fact]
    public void Disabled_capture_keeps_expression_claims_but_has_no_audit_snapshot()
    {
        var actor = Resolver().Resolve(Principal(new Claim("department", "Finance")));

        Assert.Null(actor.AuditClaims);
        Assert.Equal("Finance", actor.Claims["department"]);
    }

    [Fact]
    public void Selected_claims_preserve_all_values_order_and_duplicates()
    {
        var actor = Resolver(" department ", "DEPARTMENT").Resolve(Principal(
            new Claim("department", " Finance "),
            new Claim("DEPARTMENT", "HR"),
            new Claim("department", " Finance "),
            new Claim("unselected", "private")));

        var snapshot = Assert.IsAssignableFrom<IReadOnlyDictionary<string, string[]>>(actor.AuditClaims);
        Assert.Equal("department", Assert.Single(snapshot).Key);
        Assert.Equal([" Finance ", "HR", " Finance "], snapshot["department"]);
        Assert.Equal(" Finance ", actor.Claims["department"]);
        Assert.Equal("private", actor.Claims["unselected"]);
    }

    [Fact]
    public void Enabled_capture_without_selected_claims_produces_empty_snapshot()
    {
        var actor = Resolver("department").Resolve(Principal(new Claim("other", "value")));

        Assert.NotNull(actor.AuditClaims);
        Assert.Empty(actor.AuditClaims);
    }

    [Fact]
    public void Known_inbound_mappings_are_supported_without_matching_arbitrary_uri_suffixes()
    {
        var actor = Resolver("sub", "email", "department").Resolve(Principal(
            new Claim(ClaimTypes.NameIdentifier, "subject-id"),
            new Claim("sub", "other-subject"),
            new Claim(ClaimTypes.Email, "person@example.test"),
            new Claim("https://unrelated.example/department", "unselected")));

        Assert.Equal(["subject-id", "other-subject"], actor.AuditClaims!["sub"]);
        Assert.Equal(["person@example.test"], actor.AuditClaims["email"]);
        Assert.False(actor.AuditClaims.ContainsKey("department"));
    }

    [Fact]
    public void Non_bearer_or_unauthenticated_identities_have_no_jwt_snapshot()
    {
        var resolver = Resolver("department");
        var claim = new Claim("department", "Finance");

        Assert.Null(resolver.Resolve(new ClaimsPrincipal(
            new ClaimsIdentity([claim], "SharedVariableClient"))).AuditClaims);
        Assert.Null(resolver.Resolve(new ClaimsPrincipal(
            new ClaimsIdentity([claim]))).AuditClaims);
    }

    [Fact]
    public void Capture_reads_only_validated_bearer_identity_claims()
    {
        var principal = Principal(new Claim("department", "Finance"));
        principal.AddIdentity(new ClaimsIdentity(
            [new Claim("department", "other-scheme")], "OtherAuthentication"));

        Assert.Equal(["Finance"], Resolver("department").Resolve(principal).AuditClaims!["department"]);
    }

    [Fact]
    public void Snapshot_copy_preserves_null_empty_and_detaches_mutable_arrays()
    {
        Assert.Null(ActorContext.CopyAuditClaims(null));
        Assert.Empty(ActorContext.CopyAuditClaims(new Dictionary<string, string[]>())!);
        var original = new Dictionary<string, string[]> { ["department"] = ["Finance", "HR"] };
        var copy = ActorContext.CopyAuditClaims(original)!;

        original["department"][0] = "changed";
        original["new"] = ["new"];

        Assert.Equal(["Finance", "HR"], copy["department"]);
        Assert.Single(copy);
    }

    private static ActorContextResolver Resolver(params string[] names)
    {
        var identity = new ActorIdentityConfiguration();
        identity.Initialize(null);
        return new ActorContextResolver(identity, new WorkflowAuditOptions { AllowedClaims = [.. names] });
    }

    private static ClaimsPrincipal Principal(params Claim[] claims) =>
        new(new ClaimsIdentity(claims, JwtBearerDefaults.AuthenticationScheme));
}
