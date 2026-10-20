using Flowbit.Service.Abstractions;
using Flowbit.Shared.Dtos;

namespace Flowbit.Api.Endpoints;

/// <summary>
/// Settings administrators' management of the editor assistant's AI models, stored in engine setting AiAuthoring.Models.
/// Requests may carry a provider key; responses never do.
/// </summary>
public static class AiModelEndpoints
{
    public static IEndpointRouteBuilder MapAiModelEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/ai-models")
            .WithTags("AI Models")
            .RequireAuthorization()
            .AddEndpointFilter<SettingsAuthorizationFilter>();

        group.MapGet(string.Empty, ListAiModels)
            .Produces<IReadOnlyList<AiModelSettingDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost(string.Empty, CreateAiModel)
            .Produces<AiModelSettingDto>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPut("/{id}", UpdateAiModel)
            .Produces<AiModelSettingDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapDelete("/{id}", DeleteAiModel)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);

        return app;
    }

    private static async Task<IResult> ListAiModels(
        HttpContext http,
        IAiModelSettingsService service,
        CancellationToken cancellationToken)
    {
        http.Response.Headers.CacheControl = "no-store";
        return Results.Ok(await service.ListAsync(cancellationToken));
    }

    private static async Task<IResult> CreateAiModel(
        HttpContext http,
        CreateAiModelRequest request,
        IAiModelSettingsService service,
        CancellationToken cancellationToken)
    {
        http.Response.Headers.CacheControl = "no-store";
        var model = await service.CreateAsync(request, cancellationToken);
        return Results.Created($"/api/ai-models/{Uri.EscapeDataString(model.Id)}", model);
    }

    private static async Task<IResult> UpdateAiModel(
        HttpContext http,
        string id,
        UpdateAiModelRequest request,
        IAiModelSettingsService service,
        CancellationToken cancellationToken)
    {
        http.Response.Headers.CacheControl = "no-store";
        var model = await service.UpdateAsync(id, request, cancellationToken);
        return model is null ? Results.NotFound() : Results.Ok(model);
    }

    private static async Task<IResult> DeleteAiModel(
        string id,
        long expectedRevision,
        IAiModelSettingsService service,
        CancellationToken cancellationToken)
    {
        var deleted = await service.DeleteAsync(id, expectedRevision, cancellationToken);
        return deleted ? Results.NoContent() : Results.NotFound();
    }
}
