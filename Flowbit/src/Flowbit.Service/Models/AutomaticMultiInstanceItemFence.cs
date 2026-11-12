namespace Flowbit.Service.Models;

/// <summary>
/// Service/script items use their own job. Call adapters supply the invocation
/// plus a parent callStart or child callResume lease, under the parent's runtime
/// locks. They retain ownership of invocation settlement and message completion.
/// </summary>
public sealed record AutomaticMultiInstanceItemFence(WorkflowJobFence Job, long? CallInvocationId = null)
{
    public long JobId => Job.JobId;
    public Guid LeaseToken => Job.LeaseToken;
    public long LeaseGeneration => Job.LeaseGeneration;
    public string WorkerId => Job.WorkerId;
    public static implicit operator AutomaticMultiInstanceItemFence(WorkflowJobFence job) => new(job);
    public static implicit operator WorkflowJobFence(AutomaticMultiInstanceItemFence fence) => fence.Job;
}
