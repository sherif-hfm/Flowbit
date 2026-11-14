using System.Text.Json;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using Microsoft.Extensions.Logging;

namespace Flowbit.Service.Services;

/// <summary>
/// TASK-05D call adapter. A call body enters through its own <c>multiInstanceStart</c> job,
/// which pins one target for the whole body under the target family's lock before it
/// initializes the body. Admission (in the dispatch job) gives each item a reserved
/// invocation and its <c>callStart</c>. That <c>callStart</c> stages the item in a
/// parent-only transaction, then starts the child from the pin in a second transaction that
/// takes the family lock first; neither holds the family lock and a parent shared row
/// together, and every transaction that takes the family lock takes it before anything
/// else. The child's <c>callResume</c> saves the mapped result as the item's candidate,
/// then publishes it through the common coordinator, which settles the item. The child's
/// own terminal commit never writes parent rows. Calls use the fixed <c>callActivity</c>
/// actor throughout.
/// </summary>
public sealed partial class WorkflowEngineService
{
    private static readonly IReadOnlyDictionary<string, JsonElement> NoItemEnvironment =
        new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Queues a call body's entry. The token waits on its <c>multiInstanceStart</c> job, so
    /// no child starts inside the routing transaction, and the entry counts once against
    /// the automatic-activation guard. Routing can revisit a token that already waits here.
    /// </summary>
    private async Task EnterAutomaticCallBodyAsync(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        FlowNodeModel node,
        CancellationToken cancellationToken)
    {
        EnsureAutomaticMultiInstanceRuntimeAvailable(node);
        if (token.WaitState is not null || token.WaitingMultiInstanceExecutionId is not null)
        {
            return;
        }

        EnsureTokenCanEnterWait(token);
        var job = await EnqueueInstanceJobAsync(
            BuildJobCreate(
                instance,
                token,
                node,
                WorkflowJobKinds.MultiInstanceStart,
                WorkflowJobKinds.MultiInstanceStart,
                CallActivityActor(),
                dueAt: timeProvider.GetUtcNow()),
            cancellationToken,
            token,
            countAutomaticActivation: true);
        if (!await runtime.SetExecutionTokenWaitAsync(
                token.Id,
                token.ActivationId,
                WorkflowJobKinds.MultiInstanceStart,
                job.Id,
                null,
                cancellationToken))
        {
            throw new WorkflowConflictException(
                "The execution token changed while its call body's entry job was being created.");
        }
    }

    /// <summary>
    /// Initializes a call body. The target family's lock comes before the parent's runtime
    /// locks, as for every child start. The published default and its default plain start
    /// are resolved and checked here, once per body, together with the call depth; the
    /// coordinator keeps the pin only for a nonempty collection, and a nonempty body without
    /// one fails before any body, item, or invocation exists.
    /// </summary>
    private async Task ProcessAutomaticCallEntryAsync(
        WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence,
        CancellationToken cancellationToken)
    {
        var coordinator = automaticMultiInstanceCoordinator
            ?? throw new WorkflowJobInvariantException("The call body entry has no body coordinator.");
        // Immutable, so the target family can be read before any lock is taken.
        var seedNode = (await GetWorkflowAsync(lease.Job.WorkflowDefinitionId, cancellationToken)).Definition
            .FlowNodes.SingleOrDefault(node => node.Id == lease.Job.NodeId);
        if (seedNode is null || !CallActivityRules.IsCallBody(seedNode) || seedNode.CallActivity is null)
        {
            throw new WorkflowJobInvariantException("The multiInstanceStart job no longer names a call body.");
        }

        EnsureAutomaticMultiInstanceRuntimeAvailable(seedNode);
        var targetKey = seedNode.CallActivity.WorkflowKey;
        string? failureCode = null;
        string? failureDescription = null;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            await definitions.LockFamilyForStartAsync(targetKey, cancellationToken);
            var instance = await GetInstanceForJobUpdateAsync(lease.Job.InstanceId!.Value, cancellationToken)
                ?? throw new WorkflowConflictException("The workflow instance no longer exists.");
            var token = await runtime.GetExecutionTokenAsync(lease.Job.TokenId!.Value, true, cancellationToken);
            var job = await jobs.GetForUpdateAsync(lease.Job.Id, cancellationToken)
                ?? throw new WorkflowConflictException("The workflow job no longer exists.");
            EnsureJobFence(job, fence);
            token = RequireFencedToken(instance, job, token);
            var definition = (await GetWorkflowAsync(instance.WorkflowDefinitionId, cancellationToken)).Definition;
            var node = GetFlowNode(definition, token.NodeId);
            if (!IsInstalledAutomaticBodyOfKind(node, lease.Job.NodeType) || !CallActivityRules.IsCallBody(node)
                || !string.Equals(node.CallActivity?.WorkflowKey, targetKey, StringComparison.Ordinal))
            {
                throw new WorkflowJobInvariantException("The call body entry no longer names its call body.");
            }

            // Every item starts at the same depth, so one check covers them all.
            var childDepth = instance.CallDepth + 1;
            WorkflowDefinitionRecord? target = null;
            string? targetFailure = null;
            if (childDepth > CallLimits.MaxDepth)
            {
                targetFailure = CallFailureCodes.DepthExceeded;
                failureDescription = $"Call depth {childDepth} exceeds the limit of {CallLimits.MaxDepth}.";
            }
            else
            {
                target = await definitions.GetDefaultByWorkflowKeyAsync(targetKey, cancellationToken);
                var problems = CallTargetReadiness.FindCallTargetProblems(definition, node, target?.Definition);
                if (problems.Count > 0)
                {
                    targetFailure = problems[0].Code;
                    failureDescription = string.Join(" ", problems.Select(problem => problem.Message));
                }
            }

            var actor = CallActivityActor();
            var create = await CreateAutomaticBodyRecordAsync(instance, token, node, definition,
                AutomaticCallInstanceInputNames(node), cancellationToken);
            if (targetFailure is null)
            {
                create = create with
                {
                    PinnedChildDefinitionId = target!.Id,
                    PinnedChildStartEventId = target.Definition.InitialEventId!.Value
                };
            }

            try
            {
                await coordinator.InitializeBodyAsync(create, definition, actor, fence,
                    BuildJobCreate(instance, token, node, WorkflowJobKinds.MultiInstanceDispatch,
                        WorkflowJobKinds.MultiInstanceDispatch, actor, timeProvider.GetUtcNow()),
                    CreateAutomaticMultiInstanceLifecycle(instance, definition, actor), cancellationToken,
                    targetFailure);
            }
            catch (AutomaticMultiInstanceValueException error)
            {
                failureCode = error.Code;
                if (error.Code != targetFailure)
                {
                    failureDescription = null;
                }
            }

            if (failureCode is null)
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }
        }

        unitOfWork.DiscardChanges();
        logger.LogInformation(
            "Call body entry job {JobId} failed before any item existed: {FailureCode}.",
            lease.Job.Id,
            failureCode);
        await HandleAutomaticEntryFailureAsync(lease, fence, failureCode, cancellationToken, failureDescription);
    }

    /// <summary>
    /// The instance variables a call body freezes: its input mapping sources and the names
    /// its local output defaults and validations reference. Item sources, the collection,
    /// the aggregate target, and shared aliases are never frozen here.
    /// </summary>
    internal static IReadOnlyList<string> AutomaticCallInstanceInputNames(FlowNodeModel node)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mapping in node.CallActivity?.InputMappings ?? [])
        {
            if (mapping is not null)
            {
                names.Add(mapping.SourceVariable);
            }
        }

        AddAutomaticLocalOutputReferences(node, names);
        return AutomaticInstanceInputNames(node, names);
    }

    /// <summary>What the dispatch job needs to give each admitted call item its invocation and callStart.</summary>
    private AutomaticCallAdmission BuildAutomaticCallAdmission(
        WorkflowInstanceRecord instance,
        ExecutionTokenRecord token,
        FlowNodeModel node) =>
        new(
            new CallInvocationCreateRecord(
                instance.Id,
                token.Id,
                token.ActivationId,
                node.Id,
                null,
                instance.CallRootInstanceId ?? instance.Id,
                instance.CallDepth + 1,
                instance.WorkflowDefinitionId,
                node.CallActivity!.WorkflowKey,
                node.CallActivity,
                null,
                CapacityReserved: true,
                CallActivityActorName),
            BuildJobCreate(
                instance,
                token,
                node,
                WorkflowJobKinds.CallStart,
                WorkflowJobKinds.CallStart,
                CallActivityActor(),
                timeProvider.GetUtcNow()),
            CallLimits.MaxOpenInvocationsPerRoot);

    private sealed record AutomaticCallItemOwner(
        WorkflowInstanceRecord Instance,
        ExecutionTokenRecord Token,
        AutomaticMultiInstanceRecord Body,
        AutomaticMultiInstanceItemRecord Item,
        WorkflowModel Definition,
        FlowNodeModel Node,
        AutomaticMultiInstanceItemFence Fence)
    {
        public WorkflowJobCreateRecord ControlTemplate { get; init; } = null!;
    }

    // The parent instance, token, body, item, then the job: the item-owner lock order. A
    // callResume belongs to the child, but only the parent's rows are locked here.
    /// <summary>
    /// The configuration, setting, and system values a call body's local-output defaults and
    /// validations reference, captured when an item first stages and kept for its retries, as
    /// REST and script items do. Mappings cannot read context, so nothing else is captured.
    /// The item acts as the fixed <c>callActivity</c> actor and has no job or claim context.
    /// </summary>
    private async Task<IReadOnlyDictionary<string, JsonElement>> AutomaticCallItemEnvironmentAsync(
        AutomaticCallItemOwner owner,
        DateTimeOffset stagedAt,
        CancellationToken cancellationToken)
    {
        var referenced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        AddAutomaticLocalOutputReferences(owner.Node, referenced);
        referenced.RemoveWhere(name => !IsAutomaticContextName(name)
            || name.StartsWith("mi.", StringComparison.OrdinalIgnoreCase));
        if (referenced.Count == 0)
        {
            return NoItemEnvironment;
        }

        await LoadSettingsAsync(cancellationToken);
        return BuildContextMap(CallActivityActor(), owner.Instance with
            {
                ActiveTokenId = owner.Token.Id,
                CurrentStepId = owner.Token.NodeId,
                CurrentNodeExecutionId = owner.Token.CurrentNodeExecutionId
            }, owner.Definition, owner.Node, stagedAt)
            .Where(pair => referenced.Contains(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
    }

    // A closed switch is a host setting, not a broken job: the item keeps its slot and resumes
    // from its incident on an open host. It is checked once the job is known to own live work,
    // so stale work closes quietly on any host.
    private void EnsureAutomaticCallItemHostOpen(int nodeId)
    {
        if (automaticMultiInstanceOptions?.Enabled != true)
        {
            throw new WorkflowDomainException(
                $"Automatic multi-instance node #{nodeId} cannot run on this host: automatic multi-instance runs only on a Development host with the release switch open.");
        }
    }

    private async Task<AutomaticCallItemOwner> LockAutomaticCallItemOwnerAsync(
        WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence,
        CallInvocationRecord seed,
        CancellationToken cancellationToken)
    {
        if (automaticMultiInstances is null || automaticMultiInstanceCoordinator is null
            || seed.MultiInstanceExecutionId is not long bodyId
            || seed.AutomaticMultiInstanceItemId is not long itemId)
        {
            throw new WorkflowJobInvariantException("The call item job has no body or item ownership.");
        }

        var instance = await GetInstanceForJobUpdateAsync(seed.ParentInstanceId, cancellationToken)
            ?? throw new WorkflowConflictException("The call item's parent instance no longer exists.");
        var token = await runtime.GetExecutionTokenAsync(seed.ParentTokenId, true, cancellationToken);
        var body = await automaticMultiInstances.GetAsync(bodyId, true, cancellationToken);
        if (instance.Status != WorkflowInstanceStatuses.Running || token is null || body is null
            || token.Status != ExecutionTokenRecordStatuses.Active || token.ActivationId != seed.ActivationId
            || token.WaitingMultiInstanceExecutionId != bodyId || body.TokenId != token.Id
            || body.Status != MultiInstanceRecordStatuses.Active || body.ActivationId != token.ActivationId
            || body.WorkflowDefinitionId != instance.WorkflowDefinitionId)
        {
            throw new WorkflowConflictException("The call item no longer owns its body.");
        }

        var item = await automaticMultiInstances.GetItemAsync(bodyId, itemId, true, cancellationToken)
            ?? throw new WorkflowConflictException("The call item no longer exists.");
        var job = await jobs.GetForUpdateAsync(lease.Job.Id, cancellationToken)
            ?? throw new WorkflowConflictException("The call item job no longer exists.");
        EnsureJobFence(job, fence);
        var definition = (await GetWorkflowAsync(body.WorkflowDefinitionId, cancellationToken)).Definition;
        var node = GetFlowNode(definition, body.NodeId);
        if (!CallActivityRules.IsCallBody(node)
            || !IsInstalledAutomaticBodyOfKind(node, BpmnFlowNodeTypes.CallActivity))
        {
            throw new WorkflowJobInvariantException("The call item no longer names a call body.");
        }

        var actor = CallActivityActor();
        return new AutomaticCallItemOwner(instance, token, body, item, definition, node,
            new AutomaticMultiInstanceItemFence(fence, seed.Id))
        {
            ControlTemplate = BuildJobCreate(instance, token, node, WorkflowJobKinds.MultiInstanceDispatch,
                WorkflowJobKinds.MultiInstanceDispatch, actor, timeProvider.GetUtcNow())
        };
    }

    /// <summary>
    /// One item's callStart, in up to two transactions. The stage transaction is parent
    /// only: it captures the item's shared inputs and output fences on the item row and the
    /// mapped inputs on the invocation, then commits. The start transaction takes the family
    /// lock first, fences the item again from fresh state, and creates at most one child from
    /// the pin with the saved inputs; it reads and locks no parent shared row. A retry after
    /// a committed stage skips straight to the start.
    /// </summary>
    private async Task ProcessAutomaticCallItemStartAsync(
        WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence,
        CallInvocationRecord seed,
        CancellationToken cancellationToken)
    {
        if (!AutomaticMultiInstanceRules.IsAdapterInstalled(lease.Job.NodeType))
        {
            throw new WorkflowJobInvariantException("The automatic call item adapter is not installed.");
        }

        var coordinator = automaticMultiInstanceCoordinator!;
        CallFailure? failure = null;
        string? refusal = null;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            var owner = await LockAutomaticCallItemOwnerAsync(lease, fence, seed, cancellationToken);
            _ = await coordinator.RequireCallItemAsync(owner.Body.Id, owner.Item.Id, owner.Body.ActivationId,
                owner.Fence, requireDelivery: false, cancellationToken);
            EnsureAutomaticCallItemHostOpen(owner.Node.Id);
            var invocation = await CallActivities.GetInvocationAsync(seed.Id, cancellationToken)
                ?? throw new WorkflowJobInvariantException($"Call invocation #{seed.Id} no longer exists.");
            if (invocation.Inputs is null)
            {
                try
                {
                    var stagedAt = timeProvider.GetUtcNow();
                    var context = await coordinator.CaptureItemContextAsync(owner.Body.Id, owner.Item.Id,
                        owner.Body.ActivationId, owner.Fence, owner.Definition,
                        await AutomaticCallItemEnvironmentAsync(owner, stagedAt, cancellationToken), stagedAt,
                        cancellationToken);
                    var captured = CaptureCallInputs(invocation.Contract ?? owner.Node.CallActivity!,
                        AutomaticCallItemSources(owner.Node, context));
                    failure = captured.Failure;
                    if (failure is null)
                    {
                        await CallActivities.SetInputsAsync(invocation.Id, captured.Snapshot!, cancellationToken);
                    }
                }
                catch (AutomaticMultiInstanceValueException error)
                {
                    // A staged input over the item limit is the same on every attempt.
                    refusal = error.Code;
                }
            }

            if (failure is null && refusal is null)
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
        }

        // A failed stage commits nothing: its retry captures the values current then.
        unitOfWork.DiscardChanges();
        if (refusal is not null)
        {
            await RefuseAutomaticCallItemAsync(lease, fence, seed, refusal, cancellationToken);
            return;
        }

        if (failure is not null)
        {
            await ApplyAutomaticCallItemFailureAsync(lease, fence, seed, failure, start: true, cancellationToken);
            return;
        }

        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            // The family lock comes first on every attempt, as for every child start.
            await definitions.LockFamilyForStartAsync(seed.TargetWorkflowKey, cancellationToken);
            var owner = await LockAutomaticCallItemOwnerAsync(lease, fence, seed, cancellationToken);
            _ = await coordinator.RequireCallItemAsync(owner.Body.Id, owner.Item.Id, owner.Body.ActivationId,
                owner.Fence, requireDelivery: false, cancellationToken);
            var invocation = await CallActivities.GetInvocationAsync(seed.Id, cancellationToken)
                ?? throw new WorkflowJobInvariantException($"Call invocation #{seed.Id} no longer exists.");
            if (invocation.Inputs is null || !invocation.CapacityReserved || invocation.ChildInstanceId is not null)
            {
                throw new WorkflowConflictException($"Call invocation #{seed.Id} is not ready to start its child.");
            }

            failure = await TryCreateAutomaticCallChildAsync(owner.Body, invocation, cancellationToken);
            if (failure is null)
            {
                var linked = await CallActivities.GetInvocationAsync(seed.Id, cancellationToken) ?? invocation;
                await AddCallHistoryAsync(owner.Instance.Id, owner.Token.Id, owner.Node.Id,
                    InstanceHistoryNotes.CallChildStarted, CallActivityActor(), linked, null, cancellationToken);
                if (!await jobs.CompleteAsync(fence, cancellationToken))
                {
                    throw new WorkflowConflictException("The call item's callStart lease was lost before completion.");
                }

                await unitOfWork.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                CallActivityRuntimeTelemetry.RecordStartQueueAge(timeProvider.GetUtcNow() - lease.Job.CreatedAt);
                logger.LogInformation(
                    "Call item invocation {InvocationId} of body {ExecutionId} started child instance {ChildInstanceId} (definition {ChildDefinitionId}).",
                    seed.Id,
                    owner.Body.Id,
                    linked.ChildInstanceId,
                    linked.ChildDefinitionId);
                return;
            }
        }

        // Nothing from the failed attempt persists, including the child.
        unitOfWork.DiscardChanges();
        await ApplyAutomaticCallItemFailureAsync(lease, fence, seed, failure, start: true, cancellationToken);
    }

    // The values an item's input mappings read: frozen instance inputs, the item context, the
    // frozen collection under every alias of its key, and the shared values this item staged.
    // An item never reads its own aggregate target.
    private static Dictionary<string, JsonElement> AutomaticCallItemSources(
        FlowNodeModel node,
        AutomaticMultiInstanceItemContext context)
    {
        var sources = context.Values.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        sources.Remove(node.MultiInstance!.ResultVariable ?? string.Empty);
        return sources;
    }

    // Starts the pinned version with the saved inputs. Start legality, the input contract,
    // and business-key uniqueness are checked again for every child.
    private async Task<CallFailure?> TryCreateAutomaticCallChildAsync(
        AutomaticMultiInstanceRecord body,
        CallInvocationRecord invocation,
        CancellationToken cancellationToken)
    {
        if (body.PinnedChildDefinitionId is not long pinnedId || body.PinnedChildStartEventId is not int pinnedStart)
        {
            throw new WorkflowJobInvariantException($"Call body #{body.Id} has no target pin.");
        }

        var values = invocation.Inputs!.Values.ToDictionary(
            pair => pair.Key,
            pair => pair.Value,
            StringComparer.OrdinalIgnoreCase);
        try
        {
            var target = await GetWorkflowAsync(pinnedId, cancellationToken);
            ValidateVariableValues(GetFlowNode(target.Definition, pinnedStart).Variables, values);
        }
        catch (WorkflowDomainException error)
        {
            return new CallFailure(CallFailureCodes.InputInvalid, error.Message);
        }

        try
        {
            _ = await StartInstanceCoreAsync(
                pinnedId,
                null,
                CallActivityActor(),
                pinnedStart,
                values,
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
                cancellationToken,
                joinAmbientTransaction: true,
                forceDurableActivities: true,
                callLink: new CallChildLinkRecord(invocation.Id, invocation.RootInstanceId, invocation.ChildDepth));
            return null;
        }
        catch (BusinessKeyConflictException error)
        {
            return new CallFailure(
                CallFailureCodes.BusinessKeyConflict,
                $"{error.Message} It is owned by instance #{error.ExistingInstanceId}, which a call never attaches to.");
        }
        catch (WorkflowDomainException error)
        {
            return new CallFailure(CallFailureCodes.StartFailed, error.Message);
        }
    }

    /// <summary>
    /// Delivers a child's result to its item: save the mapped candidate in one committed
    /// transaction, then publish it in another, so a later conflict never loses the only
    /// record of a finished child. A late result, after its invocation or body settled, is
    /// acknowledged without writing anything.
    /// </summary>
    private async Task ProcessAutomaticCallItemResumeAsync(
        WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence,
        CallInvocationRecord seed,
        CancellationToken cancellationToken)
    {
        var coordinator = automaticMultiInstanceCoordinator
            ?? throw new WorkflowJobInvariantException("The call item delivery has no body coordinator.");
        CallFailure? failure = null;
        string? refusal = null;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            var parent = await GetInstanceForJobUpdateAsync(seed.ParentInstanceId, cancellationToken)
                ?? throw new WorkflowJobInvariantException($"The parent of call invocation #{seed.Id} no longer exists.");
            var current = await CallActivities.GetInvocationAsync(seed.Id, cancellationToken)
                ?? throw new WorkflowJobInvariantException($"Call invocation #{seed.Id} no longer exists.");
            if (CallInvocationStates.IsSettled(current.State) || parent.Status != WorkflowInstanceStatuses.Running)
            {
                var stale = await jobs.GetForUpdateAsync(lease.Job.Id, cancellationToken)
                    ?? throw new WorkflowConflictException("The workflow job no longer exists.");
                EnsureJobFence(stale, fence);
                if (!await jobs.CompleteAsync(fence, cancellationToken))
                {
                    throw new WorkflowConflictException("The call-resume job lease was lost before completion.");
                }

                await unitOfWork.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                return;
            }

            var owner = await LockAutomaticCallItemOwnerAsync(lease, fence, seed, cancellationToken);
            var (_, item) = await coordinator.RequireCallItemAsync(owner.Body.Id, owner.Item.Id,
                owner.Body.ActivationId, owner.Fence, requireDelivery: true, cancellationToken);
            EnsureAutomaticCallItemHostOpen(owner.Node.Id);
            var result = await CallActivities.GetResultAsync(current.ChildInstanceId!.Value, cancellationToken)
                ?? throw new WorkflowJobInvariantException($"Call invocation #{seed.Id} has a result job but no result.");
            if (result.Outcome != CallResultOutcomes.Completed)
            {
                failure = new CallFailure(
                    result.FailureCode ?? CallFailureCodes.StartFailed,
                    result.FailureDescription ?? $"Child instance #{result.ChildInstanceId} did not complete.",
                    result.ChildFaultCode);
            }
            else if (item.Candidate is null)
            {
                var context = await coordinator.CaptureItemContextAsync(owner.Body.Id, owner.Item.Id,
                    owner.Body.ActivationId, owner.Fence, owner.Definition, NoItemEnvironment,
                    timeProvider.GetUtcNow(), cancellationToken);
                AutomaticMultiInstanceCandidate? candidate = null;
                try
                {
                    candidate = MapAutomaticCallItem(owner.Node, owner.Definition, context, result);
                }
                catch (AutomaticCallItemException error)
                {
                    failure = new CallFailure(CallFailureCodes.OutputInvalid, error.Message);
                }

                if (candidate is not null)
                {
                    try
                    {
                        await coordinator.SaveCandidateAsync(owner.Body.Id, owner.Item.Id, owner.Body.ActivationId,
                            owner.Fence, owner.Definition, candidate, cancellationToken);
                    }
                    catch (AutomaticMultiInstanceValueException error) when (error.Code == AutomaticMultiInstanceValues.ResultTooLarge)
                    {
                        refusal = error.Code;
                    }
                    catch (Exception error) when (error is AutomaticMultiInstanceValueException or WorkflowDomainException)
                    {
                        failure = new CallFailure(CallFailureCodes.OutputInvalid,
                            $"Call activity #{owner.Node.Id} item result does not satisfy its declarations.");
                    }
                }
            }

            if (failure is null && refusal is null)
            {
                await unitOfWork.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }
        }

        unitOfWork.DiscardChanges();
        if (refusal is not null)
        {
            await RefuseAutomaticCallItemAsync(lease, fence, seed, refusal, cancellationToken);
            return;
        }

        if (failure is not null)
        {
            await ApplyAutomaticCallItemFailureAsync(lease, fence, seed, failure, start: false, cancellationToken);
            return;
        }

        await PublishAutomaticCallItemAsync(lease, fence, seed, cancellationToken);
    }

    /// <summary>
    /// Publishes the saved candidate, settles the item and its invocation, and acknowledges
    /// the child's message in one transaction. The coordinator queues the next dispatch or
    /// the completion job; nothing is admitted here.
    /// </summary>
    private async Task PublishAutomaticCallItemAsync(
        WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence,
        CallInvocationRecord seed,
        CancellationToken cancellationToken)
    {
        string? refusal = null;
        var invalid = false;
        var conflict = false;
        await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
        {
            var owner = await LockAutomaticCallItemOwnerAsync(lease, fence, seed, cancellationToken);
            try
            {
                await automaticMultiInstanceCoordinator!.PublishCandidateAsync(owner.Body.Id, owner.Item.Id,
                    owner.Body.ActivationId, owner.Fence, owner.Definition, CallActivityActor(),
                    owner.ControlTemplate, cancellationToken);
            }
            catch (AutomaticMultiInstancePublicationConflictException)
            {
                conflict = true;
            }
            catch (AutomaticMultiInstanceValueException error) when (error.Code == AutomaticMultiInstanceValues.ResultTooLarge)
            {
                refusal = error.Code;
            }
            catch (Exception error) when (error is AutomaticMultiInstanceValueException or WorkflowDomainException)
            {
                // A local validation that reads a shared alias can fail only at publication.
                // The saved candidate cannot change, so this is its output failure.
                invalid = true;
            }

            if (!conflict && refusal is null && !invalid)
            {
                var invocation = await CallActivities.GetInvocationAsync(seed.Id, cancellationToken) ?? seed;
                if (!await CallActivities.SettleAsync(seed.Id, CallInvocationStates.Completed, null, null, null, cancellationToken))
                {
                    throw new WorkflowConflictException($"Call invocation #{seed.Id} was settled during delivery.");
                }

                await AddCallHistoryAsync(owner.Instance.Id, owner.Token.Id, owner.Node.Id,
                    InstanceHistoryNotes.CallCompleted, CallActivityActor(), invocation, null, cancellationToken);
                if (!await jobs.CompleteAsync(fence, cancellationToken))
                {
                    throw new WorkflowConflictException("The call-resume job lease was lost before completion.");
                }

                await unitOfWork.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                logger.LogInformation(
                    "Call item invocation {InvocationId} published the result of child instance {ChildInstanceId} to body {ExecutionId}.",
                    seed.Id,
                    invocation.ChildInstanceId,
                    owner.Body.Id);
                return;
            }
        }

        unitOfWork.DiscardChanges();
        if (conflict)
        {
            WorkflowJobRuntimeTelemetry.RecordConflict();
            // The candidate stays saved: an authorized retry publishes it against refreshed
            // fences without running the child again. The incident opens only while the item
            // still owns its delivery; a sibling's failure may have cancelled it meanwhile.
            WorkflowIncidentRecord? incident;
            await using (var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken))
            {
                var owner = await LockAutomaticCallItemOwnerAsync(lease, fence, seed, cancellationToken);
                await automaticMultiInstanceCoordinator!.RequireCallItemAsync(owner.Body.Id, owner.Item.Id,
                    owner.Body.ActivationId, owner.Fence, requireDelivery: true, cancellationToken);
                incident = await jobs.OpenIncidentAsync(fence, "output_version_conflict",
                    $"Call item shared output conflict at node #{seed.ParentNodeId}.",
                    "A shared output changed after this item staged. Retry publishes the saved result against the current value and can replace a newer one; the child does not run again.",
                    cancellationToken);
                await unitOfWork.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
            }

            if (incident is not null)
            {
                WorkflowJobRuntimeTelemetry.RecordIncident();
            }

            return;
        }

        if (refusal is not null)
        {
            await RefuseAutomaticCallItemAsync(lease, fence, seed, refusal, cancellationToken);
            return;
        }

        await ApplyAutomaticCallItemFailureAsync(lease, fence, seed,
            new CallFailure(CallFailureCodes.OutputInvalid,
                $"Call activity #{seed.ParentNodeId} item result does not satisfy its declarations at publication."),
            start: false, cancellationToken);
    }

    /// <summary>
    /// A call item failure. The whole-body catch-all error boundary takes it at once; the
    /// core stops the body and later publishes the partial result. Without a boundary, a
    /// child that faulted or was cancelled on its own stops the body and faults the parent,
    /// and every other failure waits in an incident on its callStart or callResume, which
    /// blocks the item and pauses admission.
    /// </summary>
    private async Task ApplyAutomaticCallItemFailureAsync(
        WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence,
        CallInvocationRecord seed,
        CallFailure failure,
        bool start,
        CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var owner = await LockAutomaticCallItemOwnerAsync(lease, fence, seed, cancellationToken);
        var invocation = await CallActivities.GetInvocationAsync(seed.Id, cancellationToken) ?? seed;
        var outcome = TaskExecutionOutcome.Fail(failure.Diagnostic);
        var boundary = SelectErrorBoundary(owner.Definition, owner.Node.Id, outcome).Boundary;
        var childOutcome = failure.Code is CallFailureCodes.ChildFaulted or CallFailureCodes.ChildCancelled;
        var lifecycle = CreateAutomaticMultiInstanceLifecycle(owner.Instance, owner.Definition, CallActivityActor());
        if (boundary is not null || childOutcome)
        {
            var stopped = boundary is not null
                ? await automaticMultiInstanceCoordinator!.CaptureItemFailureAsync(owner.Body.Id, owner.Item.Id,
                    owner.Body.ActivationId, owner.Fence,
                    new AutomaticMultiInstanceCompletionIntent(boundary.Id, failure.Code, owner.Item.Id),
                    CallActivityActor(), owner.ControlTemplate, lifecycle, cancellationToken,
                    failure.Description, failure.ChildFaultCode)
                : await automaticMultiInstanceCoordinator!.FailCallItemAsync(owner.Body.Id, owner.Item.Id,
                    owner.Body.ActivationId, owner.Fence, failure.Code, failure.Description, failure.ChildFaultCode,
                    CallActivityActor(), owner.ControlTemplate, lifecycle, cancellationToken);
            if (!stopped)
            {
                throw new WorkflowConflictException("Another failure already stopped this call body.");
            }

            await AddCallHistoryAsync(owner.Instance.Id, owner.Token.Id, owner.Node.Id,
                InstanceHistoryNotes.CallFailed, CallActivityActor(), invocation, failure, cancellationToken);
            if (!await jobs.CompleteAsync(fence, cancellationToken))
            {
                throw new WorkflowConflictException("The call item job lease was lost before completion.");
            }
        }
        else
        {
            // The failure was found in an earlier transaction. A sibling's failure may have
            // cancelled this item since, and a cancelled item gets no incident.
            await automaticMultiInstanceCoordinator!.RequireCallItemAsync(owner.Body.Id, owner.Item.Id,
                owner.Body.ActivationId, owner.Fence, requireDelivery: !start, cancellationToken);
            await CallActivities.RecordBlockedAsync(seed.Id, failure.Code, failure.Description, cancellationToken);
            if (await jobs.OpenIncidentAsync(
                    fence,
                    failure.Code,
                    start
                        ? $"Call activity #{owner.Node.Id} item {owner.Item.ItemIndex} could not start its child."
                        : $"Call activity #{owner.Node.Id} item {owner.Item.ItemIndex} could not deliver its result.",
                    LimitJobFailure(failure.Description),
                    cancellationToken) is null)
            {
                throw new WorkflowConflictException("The call item job lease was lost before its incident was created.");
            }

            WorkflowJobRuntimeTelemetry.RecordIncident();
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        logger.LogInformation(
            "Call item invocation {InvocationId} failed: {FailureCode} (boundary {BoundaryTaken}).",
            seed.Id,
            failure.Code,
            boundary is not null);
    }

    // A result retrying cannot change: the body stops at once and publishes nothing, and the
    // item's invocation settles as failed in this transaction, so no cancel message reaches
    // a child that has already finished.
    private async Task RefuseAutomaticCallItemAsync(
        WorkflowJobLeaseRecord lease,
        WorkflowJobFence fence,
        CallInvocationRecord seed,
        string code,
        CancellationToken cancellationToken)
    {
        await using var transaction = await unitOfWork.BeginTransactionAsync(cancellationToken);
        var owner = await LockAutomaticCallItemOwnerAsync(lease, fence, seed, cancellationToken);
        var invocation = await CallActivities.GetInvocationAsync(seed.Id, cancellationToken) ?? seed;
        if (!await automaticMultiInstanceCoordinator!.RefuseItemResultAsync(owner.Body.Id, owner.Item.Id,
                owner.Body.ActivationId, owner.Fence, code, CallActivityActor(), owner.ControlTemplate,
                CreateAutomaticMultiInstanceLifecycle(owner.Instance, owner.Definition, CallActivityActor()),
                cancellationToken))
        {
            throw new WorkflowConflictException("Another failure already stopped this call body.");
        }

        await AddCallHistoryAsync(owner.Instance.Id, owner.Token.Id, owner.Node.Id, InstanceHistoryNotes.CallFailed,
            CallActivityActor(), invocation, new CallFailure(code, "The call item's result was refused."), cancellationToken);
        if (!await jobs.CompleteAsync(fence, cancellationToken))
        {
            throw new WorkflowConflictException("The call item job lease was lost before completion.");
        }

        await unitOfWork.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Maps a finished child's frozen outputs into the candidate the coordinator saves: each
    /// target is a declared local output or a readWrite shared alias outside the body's
    /// collection, aggregate, and error targets. A missing optional output leaves a local's
    /// default and writes no shared value; an explicit null must satisfy the target. Throws
    /// <see cref="AutomaticCallItemException"/> for every output failure, so no partial
    /// batch is saved.
    /// </summary>
    internal static AutomaticMultiInstanceCandidate MapAutomaticCallItem(
        FlowNodeModel node,
        WorkflowModel definition,
        AutomaticMultiInstanceItemContext context,
        CallResultRecord result)
    {
        var multi = node.MultiInstance!;
        var owner = $"Call activity #{node.Id} item";
        var outputs = (multi.OutputVariables ?? []).Where(output => output is not null).ToList();
        var locals = outputs.Select(AutomaticMultiInstanceRules.ToVariable)
            .ToDictionary(variable => variable.Name!, StringComparer.OrdinalIgnoreCase);
        var shared = definition.Variables
            .Where(variable => variable.Scope == VariableScopes.Shared && !string.IsNullOrWhiteSpace(variable.Name))
            .GroupBy(variable => variable.Name!, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var reserved = new[] { multi.CollectionVariable, multi.ResultVariable }
            .Concat(ErrorBoundaryMatchRules.GetErrorBoundaries(definition, node.Id).Select(boundary => boundary.ErrorVariable))
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var reservedKeys = shared.Values.Where(binding => reserved.Contains(binding.Name!))
            .Select(binding => binding.SharedKey).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        var produced = result.Outputs?.Values ?? new Dictionary<string, JsonElement>();
        var itemLocals = context.LocalVariables.ToDictionary(pair => pair.Key, pair => pair.Value.Clone(), StringComparer.OrdinalIgnoreCase);
        var sharedWrites = new Dictionary<string, JsonElement>(StringComparer.OrdinalIgnoreCase);
        var mapped = new List<string>();
        foreach (var mapping in node.CallActivity?.OutputMappings ?? [])
        {
            if (mapping is null || !produced.TryGetValue(mapping.Variable, out var value))
            {
                continue;
            }

            VariableModel target;
            var isLocal = locals.TryGetValue(mapping.Variable, out var local);
            if (isLocal)
            {
                target = local!;
            }
            else if (shared.TryGetValue(mapping.Variable, out var binding)
                     && binding.Access == SharedVariableAccessModes.ReadWrite
                     && !reserved.Contains(binding.Name!)
                     && !(binding.SharedKey is not null && reservedKeys.Contains(binding.SharedKey)))
            {
                target = binding;
            }
            else
            {
                throw new AutomaticCallItemException(
                    $"{owner} cannot write '{mapping.Variable}'; an item writes only its declared local outputs or readWrite shared aliases.");
            }

            if (value.ValueKind == JsonValueKind.Null)
            {
                if (!target.Nullable)
                {
                    throw new AutomaticCallItemException(
                        $"{owner} output target '{target.Name}' is not nullable, but the result is null.");
                }
            }
            else if (!TypedOutputValueValidator.IsValid(value, target.DataType, target.IsArray))
            {
                throw new AutomaticCallItemException(
                    $"{owner} output target '{target.Name}' must be {TypedOutputValueValidator.DescribeExpected(target.DataType, target.IsArray)}.");
            }

            if (isLocal)
            {
                itemLocals[target.Name!] = value.Clone();
            }
            else
            {
                try
                {
                    SharedVariableValueValidator.Validate(target.SharedKey ?? target.Name, target.DataType,
                        target.IsArray, target.Nullable, target.Validation, value);
                }
                catch (WorkflowDomainException error)
                {
                    throw new AutomaticCallItemException(error.Message);
                }

                sharedWrites[target.Name!] = value.Clone();
            }

            mapped.Add(target.Name!);
        }

        var validationContext = AutomaticValidationContext(outputs.Select(output => output.Validation),
            context.Values, itemLocals, sharedWrites);
        var finalContext = new Dictionary<string, JsonElement>(validationContext, StringComparer.OrdinalIgnoreCase);
        foreach (var pair in itemLocals.Concat(sharedWrites))
        {
            finalContext[pair.Key] = pair.Value;
        }

        try
        {
            _ = ValidateAutomaticLocalResult(outputs, itemLocals, finalContext);
        }
        catch (AutomaticMultiInstanceValueException)
        {
            throw new AutomaticCallItemException($"{owner} left a non-nullable local output without a value.");
        }
        catch (WorkflowDomainException error)
        {
            throw new AutomaticCallItemException(error.Message);
        }

        return new AutomaticMultiInstanceCandidate(
            itemLocals,
            sharedWrites,
            // Staging fenced every possible shared target; the candidate keeps exactly
            // the aliases this item writes.
            sharedWrites.Keys.ToDictionary(alias => alias,
                alias => context.SharedOutputValueVersions.TryGetValue(alias, out var version)
                    ? version
                    : throw new AutomaticCallItemException($"{owner} cannot write '{alias}': it has no output fence."),
                StringComparer.OrdinalIgnoreCase),
            validationContext,
            mapped);
    }

    /// <summary>A call item output failure: a result that breaks its targets' declarations.</summary>
    internal sealed class AutomaticCallItemException(string message) : Exception(message);
}
