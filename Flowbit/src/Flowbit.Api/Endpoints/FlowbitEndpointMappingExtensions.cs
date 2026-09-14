namespace Flowbit.Api.Endpoints;

/// <summary>
/// Maps the complete public Flowbit HTTP API surface.
/// </summary>
public static class FlowbitEndpointMappingExtensions
{
    /// <summary>
    /// Maps every endpoint that is included in the Flowbit OpenAPI document.
    /// Keeping the registrations behind one method lets contract tests host the
    /// real route surface without starting the database-backed application.
    /// </summary>
    public static IEndpointRouteBuilder MapFlowbitApiEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapGet("/", () => Results.Redirect("/swagger"))
            .WithTags("API Documentation")
            .WithSummary("Open the interactive API documentation")
            .WithDescription("Redirects to Swagger UI when the development documentation surface is enabled.")
            .Produces(StatusCodes.Status302Found);

        app.MapAuthenticationEndpoints();
        app.MapWorkflowDefinitionEndpoints();
        app.MapWorkflowInstanceEndpoints();
        app.MapUserTaskEndpoints();
        app.MapAdministrativeActionEndpoints();
        app.MapInstanceAdministrativeActionEndpoints();
        app.MapInstanceVersionChangeBatchEndpoints();
        app.MapInstanceVariableUpdateEndpoints();
        app.MapInstanceVariableUpdateBatchEndpoints();
        app.MapTaskDistributionEndpoints();
        app.MapMultiInstanceExecutionEndpoints();
        app.MapNodeExecutionEndpoints();
        app.MapWorkflowJobEndpoints();
        app.MapUserDelegationEndpoints();
        app.MapSettingsEndpoints();
        app.MapRetentionEndpoints();
        app.MapSharedVariableEndpoints();
        app.MapSharedVariableClientEndpoints();

        return app;
    }
}
