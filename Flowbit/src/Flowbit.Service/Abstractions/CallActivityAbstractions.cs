using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;

namespace Flowbit.Service.Abstractions;

/// <summary>
/// Persistence for call invocations and their results. Invocation writes happen only
/// while the caller holds the parent's instance lock, and a result is written once while
/// the caller holds the child's. Every read takes no lock, and every write takes effect
/// at once, so a later count in the same transaction sees it.
/// </summary>
public interface ICallActivityRepository
{
    Task<CallInvocationRecord> AddInvocationAsync(
        CallInvocationCreateRecord create,
        CancellationToken cancellationToken);

    Task<CallInvocationRecord?> GetInvocationAsync(
        long invocationId,
        CancellationToken cancellationToken);

    Task<CallInvocationRecord?> GetForActivationAsync(
        long parentTokenId,
        Guid activationId,
        CancellationToken cancellationToken);

    Task<CallInvocationRecord?> GetByChildAsync(
        long childInstanceId,
        CancellationToken cancellationToken);

    /// <summary>A child's invocation without its captured contract and values.</summary>
    Task<CallInvocationRecord?> GetSummaryByChildAsync(
        long childInstanceId,
        CancellationToken cancellationToken);

    /// <summary>
    /// The unsettled invocations owned by tokens being cancelled, without their captured
    /// contracts and values. It runs no query when every token is tracked and none rests on
    /// a call, so instances outside a call tree pay nothing.
    /// </summary>
    Task<IReadOnlyList<CallInvocationRecord>> ListUnsettledForTokensAsync(
        long parentInstanceId,
        IReadOnlyCollection<long> tokenIds,
        CancellationToken cancellationToken);

    Task<bool> HasUnsettledOutgoingAsync(
        long parentInstanceId,
        CancellationToken cancellationToken);

    /// <summary>Whether any invocation anywhere under a root is unsettled.</summary>
    Task<bool> HasUnsettledUnderRootAsync(
        long rootInstanceId,
        CancellationToken cancellationToken);

    Task<bool> HasRunningDescendantsAsync(
        long rootInstanceId,
        CancellationToken cancellationToken);

    /// <summary>Counts the reserved unsettled invocations of a root, including this transaction's own.</summary>
    Task<int> CountReservedUnsettledAsync(
        long rootInstanceId,
        CancellationToken cancellationToken);

    Task<bool> SetInputsAsync(
        long invocationId,
        CallValueSnapshot inputs,
        CancellationToken cancellationToken);

    Task<bool> ReserveAsync(
        long invocationId,
        CancellationToken cancellationToken);

    /// <summary>Records why an unsettled call is blocked, without changing its state.</summary>
    Task<bool> RecordBlockedAsync(
        long invocationId,
        string failureCode,
        string failureDescription,
        CancellationToken cancellationToken);

    /// <summary>Links the child being created and marks the invocation running.</summary>
    Task<bool> LinkChildAsync(
        long invocationId,
        long childInstanceId,
        long childDefinitionId,
        int childStartEventId,
        CancellationToken cancellationToken);

    /// <summary>
    /// Settles an unsettled invocation and releases its reservation. It returns false when
    /// the invocation was already settled.
    /// </summary>
    Task<bool> SettleAsync(
        long invocationId,
        string state,
        string? failureCode,
        string? failureDescription,
        string? childFaultCode,
        CancellationToken cancellationToken);

    Task AddResultAsync(
        CallResultRecord result,
        CancellationToken cancellationToken);

    Task<CallResultRecord?> GetResultAsync(
        long childInstanceId,
        CancellationToken cancellationToken);

    Task<PagedResult<CallInvocationListRow>> ListOutgoingAsync(
        long parentInstanceId,
        int page,
        int pageSize,
        CancellationToken cancellationToken);

    Task<CallCountsRecord> GetOutgoingCountsAsync(
        long parentInstanceId,
        CancellationToken cancellationToken);

    /// <summary>The unsettled invocation each listed token owns, for execution positions.</summary>
    Task<IReadOnlyDictionary<long, CallInvocationRecord>> GetUnsettledByTokensAsync(
        long parentInstanceId,
        IReadOnlyCollection<long> tokenIds,
        CancellationToken cancellationToken);

    /// <summary>The child instance ids that already have a result row.</summary>
    Task<IReadOnlySet<long>> ListChildrenWithResultAsync(
        IReadOnlyCollection<long> childInstanceIds,
        CancellationToken cancellationToken);

    /// <summary>Pages published versions whose call nodes target a family.</summary>
    Task<PagedResult<WorkflowCallerRecord>> ListPublishedCallersAsync(
        string targetWorkflowKey,
        int page,
        int pageSize,
        CancellationToken cancellationToken);
}
