using Flowbit.Shared.Models;

namespace Flowbit.Service.Services;

/// <summary>
/// Admission limits for call activities. API and Worker replicas must use identical values,
/// because either can admit a call. Lowering a limit blocks new admissions that exceed it
/// without revoking reservations, cancelling work, or shortening an existing call's fence.
/// </summary>
public sealed class CallActivityOptions
{
    public const string SectionName = "WorkflowCallActivities";

    /// <summary>The largest depth a child may be admitted at. Roots have depth 0.</summary>
    public int MaxDepth { get; set; } = 8;

    /// <summary>
    /// The soft threshold for reserved unsettled calls under one root. Concurrent admission
    /// transactions can overshoot it, because each counts without a shared lock.
    /// </summary>
    public int MaxOpenInvocationsPerRoot { get; set; } = 256;

    public void Validate()
    {
        if (MaxDepth is < 1 or > CallActivityConstraints.MaxSupportedDepth)
        {
            throw new InvalidOperationException(
                $"{SectionName}:MaxDepth must be from 1 through {CallActivityConstraints.MaxSupportedDepth}.");
        }

        if (MaxOpenInvocationsPerRoot is < 1 or > 4096)
        {
            throw new InvalidOperationException(
                $"{SectionName}:MaxOpenInvocationsPerRoot must be from 1 through 4096.");
        }
    }
}
