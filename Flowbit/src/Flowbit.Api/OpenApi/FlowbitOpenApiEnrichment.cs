using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.OpenApi;
using Microsoft.OpenApi;

namespace Flowbit.Api.OpenApi;

/// <summary>
/// Applies the machine-readable contract conventions that make the Flowbit
/// document suitable for Swagger UI, SDK generators, and AI coding tools.
/// </summary>
internal static partial class FlowbitOpenApiEnrichment
{
    private const string MessageClientIdScheme = "MessageClientId";
    private const string MessageClientSecretScheme = "MessageClientSecret";
    private const string TaskDistributorIdScheme = "TaskDistributorClientId";
    private const string TaskDistributorSecretScheme = "TaskDistributorClientSecret";

    private static readonly IReadOnlyDictionary<string, string> PropertyDescriptions =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["id"] = "The stable numeric identifier of this resource.",
            ["workflowId"] = "The database identifier of one exact workflow-definition version.",
            ["workflowKey"] = "The stable, case-sensitive key shared by every version in a workflow family.",
            ["instanceId"] = "The numeric identifier of the workflow instance.",
            ["taskId"] = "The numeric identifier of the user-task work item.",
            ["executionId"] = "The numeric identifier of the runtime execution or activation.",
            ["nodeId"] = "The authored integer identifier of a flow node.",
            ["flowId"] = "The authored integer identifier of a sequence flow.",
            ["batchId"] = "The numeric identifier of the durable batch.",
            ["jobId"] = "The numeric identifier of the durable workflow job.",
            ["incidentId"] = "The numeric identifier of the workflow incident.",
            ["name"] = "The human-readable name of this resource.",
            ["description"] = "Human-readable operational context for developers and administrators.",
            ["status"] = "The current server-authoritative lifecycle status.",
            ["version"] = "The immutable workflow-definition version number.",
            ["createdAt"] = "The UTC timestamp at which the resource was created.",
            ["updatedAt"] = "The UTC timestamp of the most recent committed change; where documented, use this value for optimistic concurrency.",
            ["completedAt"] = "The UTC timestamp at which processing completed, or null while incomplete.",
            ["page"] = "The one-based result page number.",
            ["pageSize"] = "The maximum number of items requested or returned in one page.",
            ["totalCount"] = "The exact number of matching records before paging.",
            ["nextCursor"] = "An opaque server-issued cursor for the next page; null or omitted when no later page exists.",
            ["cursor"] = "An opaque server-issued pagination cursor. Do not decode or construct it.",
            ["items"] = "The records in the current result page, in server-authoritative order.",
            ["variables"] = "A JSON object keyed by workflow variable name.",
            ["variableFilter"] = "A bounded typed predicate over each instance's latest variable values.",
            ["sort"] = "Ordered sort criteria applied before deterministic paging.",
            ["roles"] = "Normalized role names used by the documented authorization or visibility rule.",
            ["user"] = "The canonical actor identity resolved by the server.",
            ["revision"] = "The current optimistic concurrency revision.",
            ["expectedRevision"] = "The exact revision previously read by the caller; stale values produce a conflict.",
            ["expectedUpdatedAt"] = "The exact UTC updatedAt value previously read by the caller; stale values produce a conflict.",
            ["clientId"] = "The public identifier of a machine API client.",
            ["clientSecret"] = "Secret machine credential. Store it securely; secret-returning operations expose it only as documented.",
            ["scopes"] = "The complete set of capabilities granted to the machine client.",
            ["code"] = "A stable machine-readable outcome or error code when this response type defines one.",
            ["error"] = "A human-readable diagnostic. Branch on HTTP status and stable code fields instead of parsing this text.",
            ["value"] = "The JSON value carried by this contract.",
            ["values"] = "Submitted or recorded values keyed by their authored names.",
            ["isPublished"] = "Whether this exact workflow version is available for new starts.",
            ["isDefault"] = "Whether this exact published version is selected by workflow-key starts.",
            ["isActive"] = "Whether this resource is currently active.",
            ["hasValue"] = "Whether a current value exists; this distinguishes no value from an explicit JSON null.",
            ["location"] = "The URI of the created, accepted, or conflicting resource when supplied by the server."
        };

    public static void Configure(OpenApiOptions options)
    {
        options.AddSchemaTransformer((schema, context, _) =>
        {
            if (string.IsNullOrWhiteSpace(schema.Description))
            {
                schema.Description = context.JsonPropertyInfo is { } property
                    ? DescribeSchemaProperty(property.DeclaringType, property.Name)
                    : $"JSON representation of {HumanizeTypeName(context.JsonTypeInfo.Type.Name)}.";
            }

            return Task.CompletedTask;
        });

        options.AddOperationTransformer((operation, context, _) =>
        {
            if (!FlowbitOperationCatalog.TryGet(
                    context.Description.HttpMethod,
                    context.Description.RelativePath,
                    out var documentation))
            {
                throw new InvalidOperationException(
                    $"OpenAPI documentation is missing for {context.Description.HttpMethod} " +
                    $"/{context.Description.RelativePath}. Add the route to {nameof(FlowbitOperationCatalog)}.");
            }

            operation.OperationId = documentation.OperationId;
            operation.Summary = documentation.Summary;
            if (string.IsNullOrWhiteSpace(operation.Description)
                || operation.Description.Length < documentation.Description.Length)
            {
                operation.Description = documentation.Description;
            }

            DescribeParameters(operation, documentation);
            DescribeRequestBody(operation, documentation);
            DescribeResponses(operation, documentation);
            ApplyMachineClientSecurity(operation, context, documentation);

            var endpointMetadata = context.Description.ActionDescriptor.EndpointMetadata;
            var requiresAuthorization = endpointMetadata?.OfType<IAuthorizeData>().Any() == true
                && endpointMetadata.All(item => item is not IAllowAnonymous);
            operation.Responses ??= new OpenApiResponses();
            if (requiresAuthorization && !operation.Responses.ContainsKey("401"))
            {
                operation.Responses["401"] = new OpenApiResponse
                {
                    Description = "Authentication failed or a bearer token was not supplied. The response can be empty or contain a diagnostic error object."
                };
            }

            return Task.CompletedTask;
        });

        options.AddDocumentTransformer((document, _, _) =>
        {
            document.Tags ??= new HashSet<OpenApiTag>();
            AddOrReplaceTag(document.Tags, "API Documentation", "Discovery entry point for the development Swagger UI.");
            AddOrReplaceTag(document.Tags, "Authentication", "Inspect the canonical actor identity and roles resolved from a bearer token.");
            AddOrReplaceTag(document.Tags, "User Tasks", "Personal task discovery, claim, assignment, role management, and completion operations.");
            AddOrReplaceTag(document.Tags, "Administrative Actions", "Workflow-administrator overrides for direct and durable batch task actions.");
            AddOrReplaceTag(document.Tags, "Instance Version Change Batches", "Durable preparation and execution of bulk running-instance version changes.");
            AddOrReplaceTag(document.Tags, "Instance Variable Update Batches", "Durable preparation and execution of bulk administrative variable updates.");
            AddOrReplaceTag(document.Tags, "Retention", "Retention policy inspection, preview, and cleanup-run control.");

            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes[MessageClientIdScheme] = ApiKeyScheme(
                "X-Client-Id",
                "Client identifier configured on the selected message start or catch event. Use with X-Client-Secret.");
            document.Components.SecuritySchemes[MessageClientSecretScheme] = ApiKeyScheme(
                "X-Client-Secret",
                "Client secret configured on the selected message start or catch event. Use with X-Client-Id.");
            document.Components.SecuritySchemes[TaskDistributorIdScheme] = ApiKeyScheme(
                "X-Client-Id",
                "Workflow-family task-distributor client identifier. Use with X-Client-Secret.");
            document.Components.SecuritySchemes[TaskDistributorSecretScheme] = ApiKeyScheme(
                "X-Client-Secret",
                "Workflow-family task-distributor client secret. Use with X-Client-Id.");
            EnsureComponentSchemaDescriptions(document.Components);

            return Task.CompletedTask;
        });
    }

    private static void DescribeParameters(OpenApiOperation operation, OperationDocumentation documentation)
    {
        if (operation.Parameters is null)
        {
            return;
        }

        foreach (var parameter in operation.Parameters.OfType<OpenApiParameter>())
        {
            if (!string.IsNullOrWhiteSpace(parameter.Description))
            {
                continue;
            }

            parameter.Description = DescribeParameter(
                documentation.Key,
                parameter.Name ?? "parameter",
                parameter.In?.ToString() ?? "request");
        }
    }

    private static void DescribeRequestBody(OpenApiOperation operation, OperationDocumentation documentation)
    {
        if (operation.RequestBody is not OpenApiRequestBody requestBody
            || !string.IsNullOrWhiteSpace(requestBody.Description))
        {
            return;
        }

        requestBody.Description = documentation.Key.EndsWith("/message", StringComparison.Ordinal)
            || documentation.Key.EndsWith("/message-start", StringComparison.Ordinal)
                ? "The external JSON payload consumed by the selected event's authored output mappings and validation rules. Its business shape is defined by that immutable workflow definition."
                : $"JSON input for this operation. The referenced schema defines required fields, nullability, formats, and nested contracts. Purpose: {documentation.Description}";
    }

    private static void DescribeResponses(OpenApiOperation operation, OperationDocumentation documentation)
    {
        operation.Responses ??= new OpenApiResponses();
        foreach (var responseEntry in operation.Responses)
        {
            if (responseEntry.Value is not OpenApiResponse response)
            {
                continue;
            }

            var hasBody = response.Content?.Count > 0;
            response.Description = responseEntry.Key switch
            {
                "200" => hasBody
                    ? $"The operation succeeded. The response body contains the documented result for: {documentation.Summary}."
                    : $"The operation succeeded: {documentation.Summary}.",
                "201" => "The resource was created. The response body contains the created representation and the Location header identifies it when documented.",
                "202" => "The durable operation was accepted or joined. The response reports current preparation or execution state; acceptance does not mean every item has completed.",
                "204" => "The operation succeeded and returns no response body.",
                "302" => "Redirect to the Development Swagger UI at /swagger. This is documentation discovery, not a health check.",
                "400" => "The request is malformed or violates a Flowbit domain rule. The response normally contains a human-readable error diagnostic; framework binding failures can use Problem Details.",
                "401" => "Authentication failed or required bearer or machine credentials were not supplied. The response can be empty or contain a diagnostic error object.",
                "403" => "The authenticated caller lacks the required administrator, manager, role, or scope permission. The response can be empty or contain a diagnostic error object.",
                "404" => "The resource does not exist or is intentionally hidden by actor-scoped authorization.",
                "409" => "Current server state conflicts with the command, such as stale work, optimistic concurrency, idempotency, or incompatible runtime state. Refresh the referenced resource before retrying.",
                "413" => "The request body exceeds this endpoint's configured payload limit.",
                "415" => "A non-empty request body was sent with an unsupported media type; use application/json.",
                _ => string.IsNullOrWhiteSpace(response.Description)
                    ? $"HTTP {responseEntry.Key} response for {documentation.Summary}."
                    : response.Description
            };
        }
    }

    private static void ApplyMachineClientSecurity(
        OpenApiOperation operation,
        OpenApiOperationTransformerContext context,
        OperationDocumentation documentation)
    {
        var path = FlowbitOperationCatalog.NormalizePath(context.Description.RelativePath);
        var document = context.Document
            ?? throw new InvalidOperationException("The OpenAPI operation transformer did not receive its owning document.");
        if (path.StartsWith("/api/task-distribution/", StringComparison.Ordinal))
        {
            operation.Security = [SecurityPair(document, TaskDistributorIdScheme, TaskDistributorSecretScheme)];
            operation.Description += " Authenticate with exactly one X-Client-Id and X-Client-Secret pair configured for this workflow family; bearer JWTs are not used.";
        }
        else if (path.EndsWith("/message", StringComparison.Ordinal)
                 || path.EndsWith("/message-start", StringComparison.Ordinal))
        {
            operation.Security = [SecurityPair(document, MessageClientIdScheme, MessageClientSecretScheme)];
            operation.Description += " Authenticate with the selected event's X-Client-Id and X-Client-Secret. The authored event also defines its correlation header and can define an idempotency header; those dynamic header names cannot be fixed in this document.";
        }
    }

    private static OpenApiSecurityRequirement SecurityPair(
        OpenApiDocument document,
        string idScheme,
        string secretScheme) =>
        new()
        {
            [new OpenApiSecuritySchemeReference(idScheme, document)] = [],
            [new OpenApiSecuritySchemeReference(secretScheme, document)] = []
        };

    private static OpenApiSecurityScheme ApiKeyScheme(string name, string description) =>
        new()
        {
            Type = SecuritySchemeType.ApiKey,
            In = ParameterLocation.Header,
            Name = name,
            Description = description
        };

    private static void AddOrReplaceTag(ISet<OpenApiTag> tags, string name, string description)
    {
        var existing = tags.FirstOrDefault(tag => string.Equals(tag.Name, name, StringComparison.Ordinal));
        if (existing is not null)
        {
            existing.Description = description;
            return;
        }

        tags.Add(new OpenApiTag { Name = name, Description = description });
    }

    private static void EnsureComponentSchemaDescriptions(OpenApiComponents components)
    {
        if (components.Schemas is null)
        {
            return;
        }

        foreach (var schemaEntry in components.Schemas)
        {
            SetSchemaDescription(
                schemaEntry.Value,
                $"JSON representation of {HumanizeTypeName(schemaEntry.Key)}.");

            if (schemaEntry.Value.Properties is null)
            {
                continue;
            }

            foreach (var property in schemaEntry.Value.Properties)
            {
                SetSchemaDescription(
                    property.Value,
                    PropertyDescriptions.TryGetValue(property.Key, out var description)
                        ? description
                        : $"{Humanize(property.Key)} for the {HumanizeTypeName(schemaEntry.Key)} contract.");
            }
        }
    }

    private static void SetSchemaDescription(IOpenApiSchema schema, string description)
    {
        switch (schema)
        {
            case OpenApiSchema concrete when string.IsNullOrWhiteSpace(concrete.Description):
                concrete.Description = description;
                break;
            case OpenApiSchemaReference reference when string.IsNullOrWhiteSpace(reference.Description):
                reference.Description = description;
                break;
        }
    }

    private static string DescribeParameter(string operationKey, string name, string location)
    {
        return name switch
        {
            "id" when operationKey.Contains("/workflows/", StringComparison.Ordinal) =>
                "The database identifier of one exact immutable workflow-definition version.",
            "id" when operationKey.Contains("/instances/", StringComparison.Ordinal) =>
                "The numeric identifier of the workflow instance.",
            "id" when operationKey.Contains("/shared-variable-clients/", StringComparison.Ordinal) =>
                "The numeric identifier of the managed shared-variable API client.",
            "id" => "The numeric identifier of the resource named by this route.",
            "workflowId" => "The database identifier of one exact immutable workflow-definition version.",
            "workflowKey" => "The stable, case-sensitive key shared by every version in a workflow family.",
            "taskId" => "The numeric identifier of the exact user-task work item.",
            "executionId" => "The numeric identifier of the active multi-instance execution.",
            "flowId" => "The authored integer identifier of the sequence flow to take.",
            "sourceNodeId" => "The authored integer identifier of the administrative action's source node.",
            "batchId" => "The numeric identifier of the durable batch.",
            "jobId" => "The numeric identifier of the durable workflow job.",
            "incidentId" => "The numeric identifier of the workflow incident.",
            "key" => "The deployment-wide, case-sensitive shared-variable key.",
            "category" => "The supported retention-data category whose policy is being addressed.",
            "page" => "One-based page number. Values beyond the result set return an empty items array with the exact total count.",
            "pageSize" => "Maximum records to return. Endpoint defaults and upper bounds are documented in the API guide.",
            "cursor" => "Opaque cursor returned by the preceding page. Do not decode, alter, or combine it with changed filters or sorts.",
            "sort" => "Repeatable field:direction criterion. Criteria are applied in order and the server adds an ID tie-breaker.",
            "var" => "Repeatable name:value filter over an instance variable's latest scalar value. Matching is exact and case-insensitive; entries are AND-combined.",
            "includeVariables" => "When true, include a variables object containing the latest value of every instance variable.",
            "detail" => "Set to full to return InstanceDetailDto; omit it for the smaller start result.",
            "startEvent" => "Optional authored message-start event name or identifier. Required when event selection is otherwise ambiguous.",
            "catchEvent" => "Optional authored intermediate message-catch event name or identifier. Required when event selection is otherwise ambiguous.",
            "expectedUpdatedAt" => "Exact updatedAt value previously returned by the server for optimistic concurrency.",
            _ => $"{Humanize(name)} supplied in the {location.ToLowerInvariant()}. See the operation description and schema constraints."
        };
    }

    private static string DescribeSchemaProperty(Type declaringType, string propertyName)
    {
        if (PropertyDescriptions.TryGetValue(propertyName, out var description))
        {
            return description;
        }

        return $"{Humanize(propertyName)} for the {HumanizeTypeName(declaringType.Name)} contract.";
    }

    private static string HumanizeTypeName(string name) =>
        Humanize(TypeSuffixRegex().Replace(name, string.Empty));

    private static string Humanize(string value)
    {
        var words = CamelCaseRegex().Replace(value, "$1 $2").Replace('_', ' ').Trim();
        return words.Length == 0 ? "Value" : char.ToUpperInvariant(words[0]) + words[1..];
    }

    [GeneratedRegex("(Dto|Request|Response|Result)$", RegexOptions.CultureInvariant)]
    private static partial Regex TypeSuffixRegex();

    [GeneratedRegex("([a-z0-9])([A-Z])", RegexOptions.CultureInvariant)]
    private static partial Regex CamelCaseRegex();
}
