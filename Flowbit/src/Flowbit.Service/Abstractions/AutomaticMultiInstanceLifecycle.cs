using Flowbit.Service.Models;

namespace Flowbit.Service.Abstractions;

/// <summary>
/// Graph-side operations of the shared body lifecycle. Implementations run in
/// the coordinator caller's transaction and already-held instance/token locks.
/// They must never commit, invoke an item, or enter another existing instance.
/// Adapters provide activity work, not these graph transitions.
/// </summary>
public interface IAutomaticMultiInstanceLifecycle
{
    Task InstallHostTimersAsync(AutomaticMultiInstanceRecord body, CancellationToken cancellationToken);
    Task DrainVariableNotificationsAsync(AutomaticMultiInstanceRecord body, CancellationToken cancellationToken);
    Task CloseHostSubscriptionsAsync(AutomaticMultiInstanceRecord body, CancellationToken cancellationToken);
    Task AbandonItemCallsAsync(AutomaticMultiInstanceRecord body, CancellationToken cancellationToken);
    Task ContinueAsync(AutomaticMultiInstanceRecord body, AutomaticMultiInstanceCompletion completion,
        CancellationToken cancellationToken);
}

/// <summary>
/// Persisted winning failure. Diagnostics contain stable codes, never item values. A
/// boundary means an adapter selected that error route, and completion publishes a
/// partial result first. A null boundary means a result retrying cannot change: completion
/// publishes nothing, then takes the body's catch-all error boundary or faults the instance.
/// </summary>
public sealed record AutomaticMultiInstanceCompletionIntent(int? BoundaryNodeId, string FailureCode, long? FailedItemId);

/// <summary>
/// A deterministic aggregate failure is terminal graph work, not a request to
/// rerun successful items. With an existing error route it reports omission of
/// the partial aggregate; otherwise the engine selects an error boundary or faults.
/// </summary>
public sealed record AutomaticMultiInstanceCompletion(
    AutomaticMultiInstanceCompletionIntent? ErrorRoute, bool AggregatePublished, string? AggregateFailureCode);
