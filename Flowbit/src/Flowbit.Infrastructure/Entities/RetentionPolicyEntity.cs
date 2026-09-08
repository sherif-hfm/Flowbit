namespace Flowbit.Infrastructure.Entities;

public sealed class RetentionPolicyEntity
{
    public string Category { get; set; } = string.Empty;
    public int? RetentionDays { get; set; }
    public long Revision { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedBy { get; set; }
    public bool IsInitialized { get; set; }
}
