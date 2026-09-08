using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Abstractions;

public sealed record RetentionExecutionOptions
{
    public string WorkerId { get; init; } = Environment.MachineName;
    public int BatchSize { get; init; } = 250;
    public int MaxRunnableJobs { get; init; } = 100;
    public int MaxQueueLagSeconds { get; init; } = 30;
    public int StatementTimeoutSeconds { get; init; } = 5;
    public int LockTimeoutMilliseconds { get; init; } = 500;
}

public sealed record RetentionTickResult(bool HasActiveRun, bool DidWork, bool IsPaused,
    long DeletedRows = 0, string? Message = null)
{
    public string? Category { get; init; }
    public IReadOnlyDictionary<string, long> DeletedByTable { get; init; } = new Dictionary<string, long>();
}

public interface IRetentionRepository
{
    Task<RetentionStatusDto> GetAsync(CancellationToken cancellationToken);
    Task<RetentionPolicyDto> UpdatePolicyAsync(string category, UpdateRetentionPolicyRequest request,
        string actor, CancellationToken cancellationToken);
    Task<RetentionPreviewDto> PreviewAsync(PreviewRetentionRequest request, CancellationToken cancellationToken);
    Task<RetentionRunDto> RequestRunAsync(string actor, CancellationToken cancellationToken);
    Task InitializeAsync(int completedJobDays, int resolvedIncidentDays, CancellationToken cancellationToken);
    Task<RetentionTickResult> ProcessBatchAsync(RetentionExecutionOptions options, CancellationToken cancellationToken);
}
