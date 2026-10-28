using System.Text.Json;

namespace Flowbit.Infrastructure.Entities;

public sealed class BusinessCalendarEntity
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string? Description { get; set; }

    public string TimeZone { get; set; } = string.Empty;

    public JsonElement WeeklyHours { get; set; }

    public JsonElement Holidays { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<WorkflowDefinitionBusinessCalendarEntity> DefinitionReferences { get; set; } = [];
}

/// <summary>A calendar that a workflow definition version names, written when the version is saved or published.</summary>
public sealed class WorkflowDefinitionBusinessCalendarEntity
{
    public long WorkflowDefinitionId { get; set; }

    public long BusinessCalendarId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public WorkflowDefinitionEntity WorkflowDefinition { get; set; } = null!;

    public BusinessCalendarEntity BusinessCalendar { get; set; } = null!;
}
