namespace Flowbit.Shared.Dtos;

public static class RetentionCategories
{
    public const string WorkflowHistory = "workflowHistory";
    public const string VariableHistory = "variableHistory";
    public const string NodeActivity = "nodeActivity";
    public const string AdministrativeAudits = "administrativeAudits";
    public const string SharedVariableHistory = "sharedVariableHistory";
    public const string CompletedJobs = "completedJobs";
    public const string ResolvedIncidents = "resolvedIncidents";
    public static readonly string[] All = [WorkflowHistory, VariableHistory, NodeActivity,
        AdministrativeAudits, SharedVariableHistory, CompletedJobs, ResolvedIncidents];
}

public sealed record RetentionPolicyDto(string Category, int? RetentionDays, long Revision,
    DateTimeOffset UpdatedAt, string? UpdatedBy, bool IsInitialized);

public sealed record UpdateRetentionPolicyRequest(int? RetentionDays, long ExpectedRevision);

public sealed record PreviewRetentionRequest(string Category, int? RetentionDays);

public sealed record RetentionTablePreviewDto(string Table, long EligibleCount, long ProtectedCount);

public sealed record RetentionPreviewDto(string Category, DateTimeOffset? Cutoff,
    IReadOnlyList<RetentionTablePreviewDto> Tables, bool IsLowerBound, DateTimeOffset ObservedAt);

public sealed record RetentionCategoryProgressDto(string Category, DateTimeOffset? Cutoff,
    long PolicyRevision, string Status, long DeletedRows, long ProtectedRows,
    string? Message);

public sealed record RetentionRunDto(Guid Id, string Status, DateTimeOffset RequestedAt,
    string? RequestedBy, DateTimeOffset? StartedAt, DateTimeOffset? CompletedAt,
    string? Message, IReadOnlyList<RetentionCategoryProgressDto> Categories);

public sealed record RetentionStatusDto(IReadOnlyList<RetentionPolicyDto> Policies,
    RetentionRunDto? CurrentRun, RetentionRunDto? LastRun, DateTimeOffset? NextScheduledAt,
    DateTimeOffset? WorkerLastSeenAt);
