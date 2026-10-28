using Flowbit.Service.Abstractions;
using Flowbit.Shared.Dtos;

namespace Flowbit.Api.Endpoints;

/// <summary>
/// Settings administrators' management of business calendars, which timer durations and user-task due dates can name so
/// that they count working time.
/// </summary>
public static class BusinessCalendarEndpoints
{
    public static IEndpointRouteBuilder MapBusinessCalendarEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/business-calendars")
            .WithTags("Business Calendars")
            .RequireAuthorization()
            .AddEndpointFilter<SettingsAuthorizationFilter>();

        group.MapGet(string.Empty, ListBusinessCalendars)
            .Produces<IReadOnlyList<BusinessCalendarDto>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        group.MapPost(string.Empty, CreateBusinessCalendar)
            .Produces<BusinessCalendarDto>(StatusCodes.Status201Created)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPut("/{id:long}", UpdateBusinessCalendar)
            .Produces<BusinessCalendarDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapDelete("/{id:long}", DeleteBusinessCalendar)
            .Produces(StatusCodes.Status204NoContent)
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden)
            .Produces(StatusCodes.Status404NotFound)
            .Produces(StatusCodes.Status409Conflict);
        group.MapPost("/preview", PreviewBusinessCalendar)
            .Produces<BusinessCalendarPreviewDto>()
            .Produces(StatusCodes.Status400BadRequest)
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);
        group.MapGet("/time-zones", ListBusinessCalendarTimeZones)
            .Produces<IReadOnlyList<string>>()
            .Produces(StatusCodes.Status401Unauthorized)
            .Produces(StatusCodes.Status403Forbidden);

        return app;
    }

    private static async Task<IResult> ListBusinessCalendars(
        IBusinessCalendarService service,
        CancellationToken cancellationToken) =>
        Results.Ok(await service.ListAsync(cancellationToken));

    private static async Task<IResult> CreateBusinessCalendar(
        CreateBusinessCalendarRequest request,
        IBusinessCalendarService service,
        CancellationToken cancellationToken)
    {
        var calendar = await service.CreateAsync(request, cancellationToken);
        return Results.Created($"/api/business-calendars/{calendar.Id}", calendar);
    }

    private static async Task<IResult> UpdateBusinessCalendar(
        long id,
        UpdateBusinessCalendarRequest request,
        IBusinessCalendarService service,
        CancellationToken cancellationToken)
    {
        var calendar = await service.UpdateAsync(id, request, cancellationToken);
        return calendar is null ? Results.NotFound() : Results.Ok(calendar);
    }

    private static async Task<IResult> DeleteBusinessCalendar(
        long id,
        DateTimeOffset expectedUpdatedAt,
        IBusinessCalendarService service,
        CancellationToken cancellationToken)
    {
        var deleted = await service.DeleteAsync(id, expectedUpdatedAt, cancellationToken);
        return deleted ? Results.NoContent() : Results.NotFound();
    }

    private static IResult PreviewBusinessCalendar(
        BusinessCalendarPreviewRequest request,
        IBusinessCalendarService service) =>
        Results.Ok(service.Preview(request));

    private static IResult ListBusinessCalendarTimeZones(IBusinessCalendarService service) =>
        Results.Ok(service.ListTimeZones());
}
