using System.Text.Json;
using Flowbit.Api.Auth;
using Flowbit.Api.Endpoints;
using Flowbit.Shared.Dtos;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Xunit;

namespace Flowbit.Tests;

public sealed class SharedVariableEndpointMetadataTests
{
    [Fact]
    public void RotationGraceDefaultsToTwentyFourHoursWhenOmitted()
    {
        var request = JsonSerializer.Deserialize<RotateSharedVariableClientSecretRequest>(
            """{"expectedRevision":7}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.NotNull(request);
        Assert.Equal(7, request.ExpectedRevision);
        Assert.Equal(24, request.GracePeriodHours);
    }

    [Fact]
    public void PublicDataRoutesUseClientCapablePoliciesAndContractRoutesAreAdministratorOnly()
    {
        var builder = WebApplication.CreateBuilder();
        using var app = builder.Build();
        app.MapSharedVariableEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        AssertPolicy(endpoints, "/api/shared-variables/", HttpMethods.Get,
            SharedVariableAuthorizationPolicies.Read);
        AssertPolicy(endpoints, "/api/shared-variables/", HttpMethods.Post,
            SharedVariableAuthorizationPolicies.Administrator);
        AssertPolicy(endpoints, "/api/shared-variables/{key}", HttpMethods.Get,
            SharedVariableAuthorizationPolicies.Read);
        AssertPolicy(endpoints, "/api/shared-variables/{key}", HttpMethods.Patch,
            SharedVariableAuthorizationPolicies.Administrator);
        AssertPolicy(endpoints, "/api/shared-variables/{key}/value", HttpMethods.Get,
            SharedVariableAuthorizationPolicies.Read);
        AssertPolicy(endpoints, "/api/shared-variables/{key}/value", HttpMethods.Put,
            SharedVariableAuthorizationPolicies.Write);
        AssertPolicy(endpoints, "/api/shared-variables/{key}/archive", HttpMethods.Post,
            SharedVariableAuthorizationPolicies.Administrator);
        AssertPolicy(endpoints, "/api/shared-variables/{key}/reactivate", HttpMethods.Post,
            SharedVariableAuthorizationPolicies.Administrator);
        AssertPolicy(endpoints, "/api/shared-variables/{key}/history", HttpMethods.Get,
            SharedVariableAuthorizationPolicies.Administrator);
        AssertPolicy(endpoints, "/api/shared-variables/{key}/lifecycle-blockers", HttpMethods.Get,
            SharedVariableAuthorizationPolicies.Administrator);
        Assert.DoesNotContain(endpoints, endpoint =>
            string.Equals(endpoint.RoutePattern.RawText, "/api/shared-variables/{key}", StringComparison.Ordinal)
            && endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(HttpMethods.Put) == true);
        Assert.All(endpoints, endpoint =>
            Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>()));
    }

    [Fact]
    public void MetadataAndValueContractsHaveAnExplicitDisclosureBoundary()
    {
        var metadataProperties = typeof(SharedVariableMetadataDto)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();
        Assert.Contains(nameof(SharedVariableMetadataDto.HasValue), metadataProperties);
        Assert.DoesNotContain("Value", metadataProperties);

        var valueProperties = typeof(SharedVariableValueDto)
            .GetProperties()
            .Select(property => property.Name)
            .Order(StringComparer.Ordinal)
            .ToArray();
        Assert.Equal(
            new[] { "HasValue", "Key", "Revision", "UpdatedAt", "Value" }
                .Order(StringComparer.Ordinal),
            valueProperties);
        Assert.DoesNotContain(
            nameof(UpdateSharedVariableRequest.Description),
            typeof(UpdateSharedVariableValueRequest)
                .GetProperties()
                .Select(property => property.Name));
    }

    [Fact]
    public void ClientLifecycleRoutesAreJwtAdministratorOnly()
    {
        var builder = WebApplication.CreateBuilder();
        using var app = builder.Build();
        app.MapSharedVariableClientEndpoints();

        var endpoints = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();

        Assert.NotEmpty(endpoints);
        AssertPolicy(
            endpoints,
            "/api/shared-variable-clients/{id:long}",
            HttpMethods.Put,
            SharedVariableAuthorizationPolicies.Administrator);
        Assert.All(endpoints, endpoint =>
        {
            var policies = endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>()
                .Select(metadata => metadata.Policy)
                .ToArray();
            Assert.Contains(SharedVariableAuthorizationPolicies.Administrator, policies);
            Assert.DoesNotContain(SharedVariableAuthorizationPolicies.Read, policies);
            Assert.DoesNotContain(SharedVariableAuthorizationPolicies.Write, policies);
            Assert.Null(endpoint.Metadata.GetMetadata<IAllowAnonymous>());
        });
        Assert.DoesNotContain(
            "ClientId",
            typeof(UpdateSharedVariableClientRequest)
                .GetProperties()
                .Select(property => property.Name));
    }

    private static void AssertPolicy(
        IReadOnlyCollection<RouteEndpoint> endpoints,
        string route,
        string method,
        string expectedPolicy)
    {
        var endpoint = Assert.Single(endpoints, candidate =>
            string.Equals(candidate.RoutePattern.RawText, route, StringComparison.Ordinal)
            && candidate.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(method) == true);
        Assert.Contains(
            endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>(),
            metadata => string.Equals(metadata.Policy, expectedPolicy, StringComparison.Ordinal));
    }
}
