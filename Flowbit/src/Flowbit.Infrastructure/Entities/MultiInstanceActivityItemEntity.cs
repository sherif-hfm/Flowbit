using System.Text.Json;

namespace Flowbit.Infrastructure.Entities;

/// <summary>Durable automatic work identity; never deleted by job or ledger retention.</summary>
public sealed class MultiInstanceActivityItemEntity
{
    public long Id { get; set; }
    public long InstanceId { get; set; }
    public long MultiInstanceExecutionId { get; set; }
    public MultiInstanceExecutionEntity? Execution { get; set; }
    public int ItemIndex { get; set; }
    public string Status { get; set; } = "pending";
    public string Phase { get; set; } = "execution";
    public JsonDocument? StagedInputJson { get; set; }
    public JsonDocument? CandidateJson { get; set; }
    public JsonDocument? ResultJson { get; set; }
    public JsonDocument? SharedWriteCorrelationsJson { get; set; }
    public string? ErrorCode { get; set; }
    public string? ErrorDescription { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
}
