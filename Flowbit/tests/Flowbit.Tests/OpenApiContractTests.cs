using System.Net;
using System.Text.Json;
using Flowbit.Api.Endpoints;
using Flowbit.Api.OpenApi;
using Flowbit.Service.Abstractions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Flowbit.Tests;

[Collection(InstanceDetailApiContractCollection.Name)]
public sealed class OpenApiContractTests
{
    private static readonly HashSet<string> HttpMethods =
        new(StringComparer.OrdinalIgnoreCase) { "get", "post", "put", "patch", "delete" };

    [Fact]
    public async Task InstanceDetailRetainsItsProductionOpenApiContract()
    {
        await using var factory = new WorkflowInstanceDetailEndpointTests.DetailApiFactory();
        using var client = factory.CreateClient();
        await using var scope = factory.Services.CreateAsyncScope();
        // Generate with Program's actual transformers, including bearer security,
        // even though the Testing environment does not expose the document route.
        var provider = scope.ServiceProvider.GetRequiredKeyedService<IOpenApiDocumentProvider>("v1");
        var document = await provider.GetOpenApiDocumentAsync();
        var operation = document.Paths["/api/instances/{id}"].Operations![HttpMethod.Get];

        Assert.Equal("getInstancesById", operation.OperationId);
        Assert.Null(operation.RequestBody);
        var parameter = Assert.Single(operation.Parameters!);
        Assert.Equal("id", parameter.Name);
        Assert.Equal(Microsoft.OpenApi.ParameterLocation.Path, parameter.In);
        Assert.True(parameter.Required);
        Assert.Equal(["200", "401", "404"], operation.Responses!.Keys.Order().ToArray());
        var schema = operation.Responses["200"].Content!["application/json"].Schema;
        Assert.Equal("InstanceDetailDto", Assert.IsType<Microsoft.OpenApi.OpenApiSchemaReference>(schema).Reference.Id);
        var security = Assert.Single(operation.Security!);
        Assert.Equal("Bearer", Assert.Single(security).Key.Reference.Id);
        Assert.DoesNotContain(document.Components!.Schemas!.Keys,
            name => name.Contains("IWorkflow", StringComparison.Ordinal));
        Assert.Empty(factory.Projection.Reads);
    }

    [Fact]
    public async Task EveryEndpointHasGeneratorReadyDocumentation()
    {
        await using var app = await CreateDocumentationHostAsync();
        using var client = app.GetTestClient();
        using var response = await client.GetAsync("/openapi/v1.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = json.RootElement;
        var operationIds = new HashSet<string>(StringComparer.Ordinal);
        var operationCount = 0;

        foreach (var path in root.GetProperty("paths").EnumerateObject())
        {
            foreach (var candidate in path.Value.EnumerateObject().Where(item => HttpMethods.Contains(item.Name)))
            {
                operationCount++;
                var operation = candidate.Value;
                AssertNonBlank(operation, "operationId", $"{candidate.Name.ToUpperInvariant()} {path.Name}");
                AssertNonBlank(operation, "summary", $"{candidate.Name.ToUpperInvariant()} {path.Name}");
                AssertNonBlank(operation, "description", $"{candidate.Name.ToUpperInvariant()} {path.Name}");
                Assert.True(
                    operationIds.Add(operation.GetProperty("operationId").GetString()!),
                    $"Duplicate operationId on {candidate.Name.ToUpperInvariant()} {path.Name}.");

                if (operation.TryGetProperty("parameters", out var parameters))
                {
                    foreach (var parameter in parameters.EnumerateArray())
                    {
                        AssertNonBlank(
                            parameter,
                            "description",
                            $"parameter on {candidate.Name.ToUpperInvariant()} {path.Name}");
                    }
                }

                if (operation.TryGetProperty("requestBody", out var requestBody))
                {
                    AssertNonBlank(
                        requestBody,
                        "description",
                        $"request body on {candidate.Name.ToUpperInvariant()} {path.Name}");
                }

                foreach (var documentedResponse in operation.GetProperty("responses").EnumerateObject())
                {
                    AssertNonBlank(
                        documentedResponse.Value,
                        "description",
                        $"response {documentedResponse.Name} on {candidate.Name.ToUpperInvariant()} {path.Name}");
                }
            }
        }

        Assert.Equal(FlowbitOperationCatalog.Count, operationCount);
        Assert.Equal(operationCount, operationIds.Count);
        AssertSchemaDescriptions(root);
        AssertMachineSecurity(root);
    }

    private static async Task<WebApplication> CreateDocumentationHostAsync()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Development"
        });
        TestWebHostDefaults.IsolateFromMachine(builder);
        builder.WebHost.UseTestServer();
        builder.Services.AddAuthorization();
        RegisterHandlerServiceParameters(builder.Services);
        builder.Services.AddOpenApi(FlowbitOpenApiEnrichment.Configure);

        var app = builder.Build();
        app.MapOpenApi();
        app.MapFlowbitApiEndpoints();
        await app.StartAsync();
        return app;
    }

    private static void RegisterHandlerServiceParameters(IServiceCollection services)
    {
        var serviceInterfaces = typeof(IWorkflowEngineService).Assembly.GetTypes()
            .Where(type => type.IsInterface && type.Namespace == "Flowbit.Service.Abstractions")
            .Concat(typeof(WorkflowDefinitionEndpoints).Assembly.GetTypes()
                .Where(type => type.IsInterface && type.Namespace == "Flowbit.Api.Auth"));

        foreach (var serviceInterface in serviceInterfaces)
        {
            services.AddSingleton(serviceInterface, _ =>
                throw new InvalidOperationException("Documentation hosts never invoke workflow handlers."));
        }
    }

    private static void AssertSchemaDescriptions(JsonElement root)
    {
        var schemas = root.GetProperty("components").GetProperty("schemas");
        Assert.NotEmpty(schemas.EnumerateObject());
        foreach (var schemaEntry in schemas.EnumerateObject())
        {
            var schema = schemaEntry.Value;
            AssertNonBlank(schema, "description", $"schema {schemaEntry.Name}");
            if (!schema.TryGetProperty("properties", out var properties))
            {
                continue;
            }

            foreach (var property in properties.EnumerateObject())
            {
                Assert.True(
                    HasNonBlank(property.Value, "description") || property.Value.TryGetProperty("$ref", out _),
                    $"Schema property {schemaEntry.Name}.{property.Name} needs a description or documented reference.");
            }
        }
    }

    private static void AssertMachineSecurity(JsonElement root)
    {
        var schemes = root.GetProperty("components").GetProperty("securitySchemes");
        Assert.Equal("X-Client-Id", schemes.GetProperty("MessageClientId").GetProperty("name").GetString());
        Assert.Equal("X-Client-Secret", schemes.GetProperty("MessageClientSecret").GetProperty("name").GetString());
        Assert.Equal("X-Client-Id", schemes.GetProperty("TaskDistributorClientId").GetProperty("name").GetString());
        Assert.Equal("X-Client-Secret", schemes.GetProperty("TaskDistributorClientSecret").GetProperty("name").GetString());

        AssertSecurityPair(
            root,
            "/api/workflows/{workflowKey}/message-start",
            "post",
            "MessageClientId",
            "MessageClientSecret");
        AssertSecurityPair(
            root,
            "/api/task-distribution/workflows/{workflowKey}/tasks",
            "get",
            "TaskDistributorClientId",
            "TaskDistributorClientSecret");
    }

    private static void AssertSecurityPair(
        JsonElement root,
        string path,
        string method,
        string idScheme,
        string secretScheme)
    {
        var security = root.GetProperty("paths").GetProperty(path).GetProperty(method).GetProperty("security");
        var requirement = Assert.Single(security.EnumerateArray());
        Assert.True(requirement.TryGetProperty(idScheme, out _));
        Assert.True(requirement.TryGetProperty(secretScheme, out _));
    }

    private static void AssertNonBlank(JsonElement element, string propertyName, string subject) =>
        Assert.True(HasNonBlank(element, propertyName), $"The {subject} has no {propertyName}.");

    private static bool HasNonBlank(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var value)
        && value.ValueKind == JsonValueKind.String
        && !string.IsNullOrWhiteSpace(value.GetString());
}
