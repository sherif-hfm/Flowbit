using System.Text.Json;
using Flowbit.Service.Models;

namespace Flowbit.Service.Abstractions;

/// <summary>
/// Automatic-body storage. Mutations require the caller's ambient transaction and owning
/// instance/token locks; this repository acquires body/item locks after them. It never
/// calls activities or publishes process/shared variables.
/// </summary>
public interface IAutomaticMultiInstanceRepository
{
    Task<AutomaticMultiInstanceRecord> CreateAsync(AutomaticMultiInstanceCreateRecord create, CancellationToken cancellationToken);
    Task<AutomaticMultiInstanceRecord?> GetAsync(long executionId, bool forUpdate, CancellationToken cancellationToken);
    /// <summary>Fresh persisted ownership, ignoring tracked token/instance snapshots.
    /// The caller already holds the parent runtime and body locks.</summary>
    Task<bool> HasCurrentOwnerAsync(long executionId, Guid activationId, CancellationToken cancellationToken);
    Task<AutomaticMultiInstanceRecord?> FindOpenAsync(long instanceId, int nodeId, CancellationToken cancellationToken);
    Task<JsonElement?> GetInputSnapshotAsync(long executionId, CancellationToken cancellationToken);
    Task<IReadOnlyList<AutomaticMultiInstanceProgressRecord>> GetProgressAsync(long instanceId, CancellationToken cancellationToken);
    Task<AutomaticMultiInstanceItemPageRecord> GetItemsAsync(long executionId, int skip, int take, bool includePayloads, CancellationToken cancellationToken);
    Task<IReadOnlyList<AutomaticMultiInstanceItemResultRecord>> GetItemResultsAsync(long executionId, int skip, int take, CancellationToken cancellationToken);
    Task<AutomaticMultiInstanceItemRecord?> GetItemAsync(long executionId, long itemId, bool forUpdate, CancellationToken cancellationToken);
    Task<IReadOnlyList<AutomaticMultiInstanceItemRecord>> AdmitAsync(long executionId, Guid activationId, int count, CancellationToken cancellationToken);
    Task<bool> SaveStageAsync(long executionId, long itemId, Guid activationId, JsonElement stagedInput, CancellationToken cancellationToken);
    Task<bool> SaveCandidateAsync(long executionId, long itemId, Guid activationId, JsonElement candidate, CancellationToken cancellationToken);
    Task<bool> CompleteItemAsync(long executionId, long itemId, Guid activationId, JsonElement localResult, JsonElement? sharedWriteCorrelations, int resultBytes, CancellationToken cancellationToken);
    Task<bool> FailItemAsync(long executionId, long itemId, Guid activationId, string code, string description, CancellationToken cancellationToken,
        string ledgerCompletionReason = "boundaryCaught");
    Task<long> SetControlStateAsync(long executionId, Guid activationId, string phase, string? blockedReason, CancellationToken cancellationToken);
    Task<bool> SetAggregateVersionAsync(long executionId, Guid activationId, long valueVersion, CancellationToken cancellationToken);
    Task<bool> SettleAsync(long executionId, Guid activationId, string status, string? completionReason, CancellationToken cancellationToken);
    Task<int> CancelUnfinishedItemsAsync(long executionId, Guid activationId, string reason, CancellationToken cancellationToken);
    Task<bool> AcknowledgePublicationFenceRefreshAsync(long executionId, long jobId, Guid activationId, Guid leaseToken, long leaseGeneration, CancellationToken cancellationToken);
    Task<bool> BlockItemAsync(long executionId, long itemId, Guid activationId, string code, string description, CancellationToken cancellationToken);
    Task<int> SupersedeControlJobsAsync(long executionId, Guid activationId, long? exceptJobId, CancellationToken cancellationToken);
    Task<bool> EnterBodyWaitAsync(long executionId, Guid activationId, long? entryJobId, CancellationToken cancellationToken);
    Task<bool> ClearBodyWaitAsync(long executionId, Guid activationId, CancellationToken cancellationToken);
    Task<bool> SetAdmissionBlockedReasonAsync(long executionId, Guid activationId, string? reason, CancellationToken cancellationToken);
    Task<bool> SaveCompletionIntentAsync(long executionId, Guid activationId, JsonElement intent, CancellationToken cancellationToken);
}
