using System.Text;
using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Microsoft.EntityFrameworkCore;

namespace Flowbit.Infrastructure.Repositories;

public sealed class AutomaticMultiInstanceRepository(AppDbContext db) : IAutomaticMultiInstanceRepository
{
    private const int PayloadLimit = 1024 * 1024;

    public async Task<AutomaticMultiInstanceRecord> CreateAsync(AutomaticMultiInstanceCreateRecord create, CancellationToken ct)
    {
        RequireTransaction();
        if (create.ActivityKind is not ("serviceTask" or "scriptTask" or "callActivity")
            || create.TotalCount < 0 || create.TotalCount > create.MaxInstances
            || create.MaxInstances < 1 || create.EffectiveConcurrency < 0
            || create.EffectiveConcurrency > Math.Min(1000, create.TotalCount)
            || create.TotalCount > 0 && create.EffectiveConcurrency == 0
            || create.ResultBytes is < 0 or > PayloadLimit)
            throw new ArgumentException("Invalid automatic multi-instance body contract.", nameof(create));
        // A nonempty call body pins one target before any item runs, so the
        // pinned version stays protected before its first child exists.
        var pinned = create.PinnedChildDefinitionId is not null || create.PinnedChildStartEventId is not null;
        if (pinned != (create.ActivityKind == "callActivity" && create.TotalCount > 0)
            || pinned && (create.PinnedChildDefinitionId is null || create.PinnedChildStartEventId is null))
            throw new ArgumentException("A nonempty call body requires its target pin, and only such a body may have one.", nameof(create));
        var existing = await db.MultiInstanceExecutions.AsNoTracking().SingleOrDefaultAsync(
            body => body.TokenId == create.TokenId && body.ActivationId == create.ActivationId && body.ActivityKind != "userTask", ct);
        if (existing is not null)
        {
            if (existing.InstanceId != create.InstanceId || existing.WorkflowDefinitionId != create.WorkflowDefinitionId
                || existing.NodeId != create.NodeId || existing.ActivityKind != create.ActivityKind)
                throw new InvalidOperationException("The automatic activation is already owned by another body contract.");
            return Map(existing);
        }
        var owner = await db.ExecutionTokens.AsNoTracking().SingleOrDefaultAsync(token => token.Id == create.TokenId
                && token.InstanceId == create.InstanceId && token.NodeId == create.NodeId && token.NodeType == create.ActivityKind
                && token.ActivationId == create.ActivationId && token.Status == "active"
                && token.Instance!.WorkflowDefinitionId == create.WorkflowDefinitionId && token.Instance.Status == "running", ct);
        if (owner is null)
            throw new InvalidOperationException("The automatic body token activation is no longer current.");
        var actor = create.ActivityKind == "callActivity" ? new NodeExecutionActorRecord("callActivity", []) : create.ActorContext;
        var actorJson = actor is null ? null : JsonSerializer.Serialize(actor);
        var snapshotSize = Size(create.InputSnapshot) + (actorJson is null ? 0 : Encoding.UTF8.GetByteCount(actorJson));
        // The causal actor shares the snapshot row's limit. Its few bytes can tip a source
        // that fits on its own, which is the same deterministic refusal as an oversized one.
        if (snapshotSize > PayloadLimit) throw new AutomaticMultiInstanceValueException(AutomaticMultiInstanceValues.SourceInvalid);
        var snapshot = new WorkflowJobSnapshotEntity
        {
            Kind = "automaticMultiInstance", VariablesJson = Document(create.InputSnapshot),
            InvocationJson = actorJson is null ? null : JsonDocument.Parse(actorJson),
            OutputVariableVersionsJson = JsonDocument.Parse("{}"), EvaluationTime = create.InitializedAt,
            SizeBytes = (int)snapshotSize, CreatedAt = create.InitializedAt
        };
        db.WorkflowJobSnapshots.Add(snapshot);
        var entity = new MultiInstanceExecutionEntity
        {
            InstanceId = create.InstanceId, TokenId = create.TokenId, NodeId = create.NodeId,
            WorkflowDefinitionId = create.WorkflowDefinitionId, ActivationId = create.ActivationId,
            ActivityKind = create.ActivityKind, InputSnapshot = snapshot, Mode = create.Mode,
            Source = "collection", ResultVariable = create.ResultVariable, AggregateScope = create.AggregateScope,
            AggregateSharedKey = create.AggregateSharedKey, AggregateValueVersion = create.AggregateValueVersion,
            MaxInstances = create.MaxInstances, EffectiveConcurrency = create.EffectiveConcurrency,
            TotalCount = create.TotalCount, ResultBytes = create.ResultBytes, Phase = "running",
            Status = "active", CreatedAt = create.InitializedAt, UpdatedAt = create.InitializedAt,
            PinnedChildDefinitionId = create.PinnedChildDefinitionId, PinnedChildStartEventId = create.PinnedChildStartEventId,
            PinState = pinned ? AutomaticMultiInstancePinStates.Pinned : null
        };
        db.MultiInstanceExecutions.Add(entity);
        await db.SaveChangesAsync(ct);
        // The owning instance/token lock serializes initialization. One set-based insert per
        // bounded chunk creates both descriptors and pending lifecycle visits without payload copies.
        var rolesJson = actor is null ? null : JsonSerializer.Serialize(actor.Roles);
        var claimsJson = actor?.AuditClaims is null ? null : JsonSerializer.Serialize(actor.AuditClaims);
        for (var start = 0; start < create.TotalCount; start += 250)
        {
            var end = Math.Min(create.TotalCount - 1, start + 249);
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                WITH inserted AS (
                    INSERT INTO flowbit.multi_instance_activity_items
                    ("InstanceId", "MultiInstanceExecutionId", "ItemIndex", "Status", "Phase", "CreatedAt", "UpdatedAt")
                    SELECT {create.InstanceId}, {entity.Id}, i, 'pending', 'execution', {create.InitializedAt}, {create.InitializedAt}
                    FROM generate_series({start}, {end}) i
                    RETURNING "Id", "ItemIndex"
                )
                INSERT INTO flowbit.node_executions
                ("InstanceId", "WorkflowDefinitionId", "ExecutionTokenId", "MultiInstanceExecutionId", "AutomaticMultiInstanceItemId",
                 "ItemIndex", "NodeId", "NodeName", "NodeExternalId", "NodeType", "ExecutionKind", "Status", "CreatedAt", "UpdatedAt", "IsCutoverSeeded",
                 "TriggeredBy", "TriggeredByRolesJson", "TriggeredByClaimsJson", "TriggeredActingFor", "TriggeredDelegationId",
                 "EnteredViaFlowId", "EntryGatewayBranchId")
                SELECT {create.InstanceId}, {create.WorkflowDefinitionId}, {create.TokenId}, {entity.Id}, "Id", "ItemIndex",
                 {create.NodeId}, {create.NodeName}, {create.NodeExternalId}, {create.ActivityKind}, 'automaticActivityItem', 'pending',
                 {create.InitializedAt}, {create.InitializedAt}, FALSE,
                 {actor?.User}, {rolesJson}::jsonb, {claimsJson}::jsonb, {actor?.ActingFor}, {actor?.DelegationId},
                 {owner.ArrivedViaFlowId}, {owner.GatewayBranchId} FROM inserted
                """, ct);
        }
        return Map(entity);
    }

    public async Task<AutomaticMultiInstanceRecord?> GetAsync(long id, bool forUpdate, CancellationToken ct)
    {
        var entity = forUpdate ? await LockBodyAsync(id, ct)
            : await db.MultiInstanceExecutions.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.ActivityKind != "userTask", ct);
        return entity is null ? null : Map(entity);
    }

    public Task<bool> HasCurrentOwnerAsync(long executionId, Guid activationId, CancellationToken ct)
    {
        RequireTransaction();
        return db.MultiInstanceExecutions.AsNoTracking().AnyAsync(body => body.Id == executionId
            && body.ActivityKind != "userTask" && body.Status == "active" && body.ActivationId == activationId
            && body.Instance!.Status == "running" && body.Instance.WorkflowDefinitionId == body.WorkflowDefinitionId
            && body.Token!.InstanceId == body.InstanceId && body.Token.Status == "active"
            && body.Token.NodeId == body.NodeId && body.Token.ActivationId == activationId, ct);
    }

    public async Task<AutomaticMultiInstanceRecord?> FindOpenAsync(long instanceId, int nodeId, CancellationToken ct)
    {
        var entity = await db.MultiInstanceExecutions.AsNoTracking().SingleOrDefaultAsync(
            x => x.InstanceId == instanceId && x.NodeId == nodeId && x.ActivityKind != "userTask" && x.Status == "active", ct);
        return entity is null ? null : Map(entity);
    }

    public async Task<JsonElement?> GetInputSnapshotAsync(long executionId, CancellationToken ct)
    {
        var snapshot = await db.MultiInstanceExecutions.AsNoTracking()
            .Where(x => x.Id == executionId && x.ActivityKind != "userTask")
            .Select(x => x.InputSnapshot!.VariablesJson).SingleOrDefaultAsync(ct);
        return snapshot?.RootElement.Clone();
    }

    public async Task<IReadOnlyList<AutomaticMultiInstanceProgressRecord>> GetProgressAsync(long instanceId, CancellationToken ct)
    {
        var bodies = await db.MultiInstanceExecutions.AsNoTracking()
            .Where(x => x.InstanceId == instanceId && x.ActivityKind != "userTask").OrderBy(x => x.Id).ToListAsync(ct);
        if (bodies.Count == 0) return [];
        // An item is blocked while its own work has an open incident: a REST/script
        // item job, or a call item's parent-owned callStart. This is read, never
        // written when the incident opens, because incidents open from fenced
        // processing, the lease sweeper, and direct enqueue alike, and writing body
        // rows from the job side would invert the runtime lock order.
        var incidentItems = await db.WorkflowIncidents.AsNoTracking()
            .Where(incident => incident.Status == "open" && incident.Job != null && incident.Job.InstanceId == instanceId
                && (incident.Job.Kind == "multiInstanceItem" && incident.Job.AutomaticMultiInstanceItemId != null
                    || incident.Job.Kind == "callStart" && incident.Job.CallInvocation != null
                        && incident.Job.CallInvocation.AutomaticMultiInstanceItemId != null))
            .Select(incident => incident.Job!.Kind == "multiInstanceItem"
                ? incident.Job.AutomaticMultiInstanceItemId!.Value
                : incident.Job.CallInvocation!.AutomaticMultiInstanceItemId!.Value)
            .Distinct().ToArrayAsync(ct);
        var counts = await db.MultiInstanceActivityItems.AsNoTracking().Where(x => x.InstanceId == instanceId)
            .GroupBy(x => x.MultiInstanceExecutionId)
            .Select(g => new { Id = g.Key,
                Active = g.Count(x => x.Status == "active" && x.ErrorCode == null && !incidentItems.Contains(x.Id)),
                Pending = g.Count(x => x.Status == "pending" && x.ErrorCode == null && !incidentItems.Contains(x.Id)),
                Blocked = g.Count(x => (x.Status == "active" || x.Status == "pending")
                    && (x.ErrorCode != null || incidentItems.Contains(x.Id))) })
            .ToDictionaryAsync(x => x.Id, ct);
        // One bounded, payload-free query for all current control incidents.
        // Item incidents remain in the item counts and per-item correlation.
        var controlIncidents = await db.WorkflowIncidents.AsNoTracking()
            .Where(incident => incident.Status == "open" && incident.Job != null
                && incident.Job.InstanceId == instanceId && incident.Job.Status == "incident"
                && (incident.Job.Kind == "multiInstanceDispatch" || incident.Job.Kind == "multiInstanceComplete")
                && incident.Job.MultiInstanceExecution != null && incident.Job.MultiInstanceExecution.Status == "active"
                && incident.Job.ActivationId == incident.Job.MultiInstanceExecution.ActivationId
                && incident.Job.MultiInstanceControlGeneration == incident.Job.MultiInstanceExecution.ControlGeneration)
            .GroupBy(incident => incident.Job!.MultiInstanceExecutionId!.Value)
            .Select(group => group.OrderByDescending(incident => incident.Id).Select(incident => new
                { BodyId = group.Key, incident.Id, incident.JobId, incident.Type }).First())
            .ToDictionaryAsync(incident => incident.BodyId, ct);
        return bodies.Select(body => new AutomaticMultiInstanceProgressRecord(Map(body) with
            {
                FailedCount = body.FailedCount + (counts.GetValueOrDefault(body.Id)?.Blocked ?? 0),
                AdmissionBlockedReason = body.AdmissionBlockedReason
                    ?? (body.Status == "active" && (counts.GetValueOrDefault(body.Id)?.Blocked ?? 0) > 0 ? "itemIncident" : null)
            },
            counts.GetValueOrDefault(body.Id)?.Active ?? 0, counts.GetValueOrDefault(body.Id)?.Pending ?? 0)
        {
            ControlJobId = controlIncidents.GetValueOrDefault(body.Id)?.JobId,
            ControlIncidentId = controlIncidents.GetValueOrDefault(body.Id)?.Id,
            ControlIncidentType = controlIncidents.GetValueOrDefault(body.Id)?.Type
        }).ToArray();
    }

    public async Task<AutomaticMultiInstanceItemPageRecord> GetItemsAsync(long executionId, int skip, int take, bool includePayloads, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        if (take is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(take));
        var query = db.MultiInstanceActivityItems.AsNoTracking().Where(x => x.MultiInstanceExecutionId == executionId);
        var total = await query.LongCountAsync(ct);
        // Projection keeps item payloads out of summary pages, including SQL transfer.
        var rows = await query.OrderBy(x => x.ItemIndex).Skip(skip).Take(take).Select(x => new MultiInstanceActivityItemEntity
        {
            Id = x.Id, InstanceId = x.InstanceId, MultiInstanceExecutionId = x.MultiInstanceExecutionId, ItemIndex = x.ItemIndex,
            Status = x.Status, Phase = x.Phase, StagedInputJson = includePayloads ? x.StagedInputJson : null,
            CandidateJson = includePayloads ? x.CandidateJson : null, ResultJson = includePayloads ? x.ResultJson : null,
            SharedWriteCorrelationsJson = includePayloads ? x.SharedWriteCorrelationsJson : null,
            ErrorCode = includePayloads ? x.ErrorCode : null, ErrorDescription = includePayloads ? x.ErrorDescription : null,
            CreatedAt = x.CreatedAt, UpdatedAt = x.UpdatedAt, StartedAt = x.StartedAt, CompletedAt = x.CompletedAt
        }).ToListAsync(ct);
        return new(rows.Select(Map).ToArray(), total);
    }

    public async Task<IReadOnlyList<AutomaticMultiInstanceItemResultRecord>> GetItemResultsAsync(long executionId, int skip, int take, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(skip);
        if (take is < 1 or > 200) throw new ArgumentOutOfRangeException(nameof(take));
        // Aggregate assembly must not materialize the independently bounded
        // staged input/candidate for every item just to read its local result.
        var rows = await db.MultiInstanceActivityItems.AsNoTracking()
            .Where(item => item.MultiInstanceExecutionId == executionId)
            .OrderBy(item => item.ItemIndex).Skip(skip).Take(take)
            .Select(item => new { item.ItemIndex, item.Status, item.ResultJson }).ToArrayAsync(ct);
        return rows.Select(item => new AutomaticMultiInstanceItemResultRecord(
            item.ItemIndex, item.Status, item.ResultJson?.RootElement.Clone())).ToArray();
    }

    public async Task<AutomaticMultiInstanceItemRecord?> GetItemAsync(long executionId, long itemId, bool forUpdate, CancellationToken ct)
    {
        if (forUpdate) await LockBodyAsync(executionId, ct);
        var item = forUpdate ? await LockItemAsync(executionId, itemId, ct)
            : await db.MultiInstanceActivityItems.AsNoTracking().SingleOrDefaultAsync(x => x.Id == itemId && x.MultiInstanceExecutionId == executionId, ct);
        return item is null ? null : Map(item) with
        {
            NodeExecutionId = await db.NodeExecutions.AsNoTracking()
                .Where(visit => visit.AutomaticMultiInstanceItemId == item.Id)
                .Select(visit => (long?)visit.Id).SingleOrDefaultAsync(ct)
        };
    }

    public async Task<IReadOnlyList<AutomaticMultiInstanceItemRecord>> AdmitAsync(long id, Guid activation, int count, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var body = await LockEligibleBodyAsync(id, activation, ct);
        if (body is null || body.CompletionJson is not null || body.Phase is "completing" or "errorCompletion" || body.AdmissionBlockedReason == "itemIncident") return [];
        var active = await db.MultiInstanceActivityItems.CountAsync(x => x.MultiInstanceExecutionId == id && x.Status == "active", ct);
        count = Math.Min(count, Math.Max(0, body.EffectiveConcurrency!.Value - active));
        var items = await db.MultiInstanceActivityItems.Where(x => x.MultiInstanceExecutionId == id && x.Status == "pending")
            .OrderBy(x => x.ItemIndex).Take(count).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var item in items) { item.Status = "active"; item.StartedAt = now; item.UpdatedAt = now; }
        await db.SaveChangesAsync(ct);
        var ids = items.Select(x => x.Id).ToArray();
        await db.NodeExecutions.Where(x => x.AutomaticMultiInstanceItemId != null && ids.Contains(x.AutomaticMultiInstanceItemId.Value))
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, "active").SetProperty(x => x.StartedAt, now).SetProperty(x => x.UpdatedAt, now), ct);
        return items.Select(Map).ToArray();
    }

    public async Task<bool> SaveStageAsync(long id, long itemId, Guid activation, JsonElement input, CancellationToken ct)
    {
        var body = await LockEligibleBodyAsync(id, activation, ct);
        if (body is null || body.CompletionJson is not null) return false;
        var item = await LockItemAsync(id, itemId, ct);
        if (item is null || item.Status != "active" || item.Phase != "execution") return false;
        if (item.StagedInputJson is not null) return true;
        CheckSize(input, item.CandidateJson?.RootElement);
        item.StagedInputJson = Document(input); item.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct); return true;
    }

    public async Task<bool> SaveCandidateAsync(long id, long itemId, Guid activation, JsonElement candidate, CancellationToken ct)
    {
        var body = await LockEligibleBodyAsync(id, activation, ct);
        if (body is null || body.CompletionJson is not null) return false;
        var item = await LockItemAsync(id, itemId, ct);
        if (item is null || item.Status != "active") return false;
        if (item.CandidateJson is not null) return true;
        CheckSize(candidate, item.StagedInputJson?.RootElement);
        item.CandidateJson = Document(candidate); item.Phase = "publication"; item.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct); return true;
    }

    public async Task<bool> CompleteItemAsync(long id, long itemId, Guid activation, JsonElement result, JsonElement? correlations, int bytes, CancellationToken ct)
    {
        var body = await LockEligibleBodyAsync(id, activation, ct);
        if (body is null || body.CompletionJson is not null) return false;
        var item = await LockItemAsync(id, itemId, ct);
        if (item is null || item.Status != "active" || item.Phase != "publication") return false;
        if (bytes < 0 || body.ResultBytes + (long)bytes > PayloadLimit || Size(result) > PayloadLimit)
            throw new ArgumentException("Automatic multi-instance result exceeds 1 MiB.", nameof(bytes));
        var now = DateTimeOffset.UtcNow;
        item.ResultJson = Document(result); item.SharedWriteCorrelationsJson = correlations.HasValue ? Document(correlations.Value) : null;
        item.ErrorCode = null; item.ErrorDescription = null;
        item.Status = "completed"; item.Phase = "settled"; item.UpdatedAt = now; item.CompletedAt = now;
        body.CompletedCount++; body.ResultBytes += bytes; body.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await FinishLedgerAsync(item.Id, "completed", "multiInstanceItem", null, null, now, ct);
        return true;
    }

    public async Task<bool> FailItemAsync(long id, long itemId, Guid activation, string code, string description, CancellationToken ct,
        string ledgerCompletionReason = "boundaryCaught")
    {
        if (ledgerCompletionReason is not ("boundaryCaught" or "multiInstanceInterrupt"))
            throw new ArgumentException("Unsupported item failure ledger reason.", nameof(ledgerCompletionReason));
        var body = await LockEligibleBodyAsync(id, activation, ct);
        if (body is null) return false;
        var item = await LockItemAsync(id, itemId, ct);
        if (item is null || item.Status != "active") return false;
        var now = DateTimeOffset.UtcNow;
        item.Status = "failed"; item.Phase = "settled"; item.ErrorCode = Limit(code, 100); item.ErrorDescription = Limit(description, 1000);
        item.CompletedAt = now; item.UpdatedAt = now; body.FailedCount++; body.UpdatedAt = now;
        await db.SaveChangesAsync(ct);
        await FinishLedgerAsync(item.Id, "faulted", ledgerCompletionReason, item.ErrorCode, item.ErrorDescription, now, ct);
        return true;
    }

    public async Task<bool> BlockItemAsync(long id, long itemId, Guid activation, string code, string description, CancellationToken ct)
    {
        var body = await LockEligibleBodyAsync(id, activation, ct);
        if (body is null) return false;
        var item = await LockItemAsync(id, itemId, ct);
        if (item is null || item.Status is not ("active" or "pending")) return false;
        // An admitted incident keeps its slot and active ledger visit. A failed admission
        // remains pending and consumes no slot. Neither case is a terminal item failure.
        item.ErrorCode = Limit(code, 100); item.ErrorDescription = Limit(description, 1000);
        item.UpdatedAt = DateTimeOffset.UtcNow;
        body.AdmissionBlockedReason = "itemIncident"; body.UpdatedAt = item.UpdatedAt;
        await db.SaveChangesAsync(ct); return true;
    }

    public async Task<int> SupersedeControlJobsAsync(long id, Guid activation, long? exceptJobId, CancellationToken ct)
    {
        if (await LockEligibleBodyAsync(id, activation, ct) is null) return 0;
        var jobs = await db.WorkflowJobs.FromSqlInterpolated($"""
            SELECT * FROM flowbit.workflow_jobs WHERE "MultiInstanceExecutionId" = {id} AND "ActivationId" = {activation}
            AND "Kind" IN ('multiInstanceDispatch', 'multiInstanceComplete') AND "Status" NOT IN ('completed', 'cancelled', 'skipped')
            AND ({exceptJobId}::bigint IS NULL OR "Id" <> {exceptJobId}) ORDER BY "Id" FOR UPDATE
            """).ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        foreach (var job in jobs)
        {
            job.Status = "cancelled"; job.CompletedAt = now; job.UpdatedAt = now;
            job.WorkerId = null; job.LeaseToken = null; job.LeaseExpiresAt = null; job.HeartbeatAt = null;
        }
        await db.SaveChangesAsync(ct);
        var ids = jobs.Select(x => x.Id).ToArray();
        await db.WorkflowIncidents.Where(x => x.JobId != null && ids.Contains(x.JobId.Value) && x.Status == "open")
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, "resolved").SetProperty(x => x.ResolvedBy, "system")
                .SetProperty(x => x.ResolvedAt, now).SetProperty(x => x.UpdatedAt, now), ct);
        await db.WorkflowJobAttempts.Where(x => ids.Contains(x.JobId) && (x.Status == "running" || x.Status == "resultReady"))
            .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, "cancelled").SetProperty(x => x.FinishedAt, now), ct);
        return jobs.Count;
    }

    public async Task<long> SetControlStateAsync(long id, Guid activation, string phase, string? blockedReason, CancellationToken ct)
    {
        var body = await LockEligibleBodyAsync(id, activation, ct);
        if (body is null) return -1;
        if (phase is not ("running" or "dispatch" or "completing" or "errorCompletion")) throw new ArgumentException("Unknown body phase.", nameof(phase));
        body.Phase = phase; body.AdmissionBlockedReason = blockedReason; body.ControlGeneration++; body.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct); return body.ControlGeneration;
    }

    public async Task<bool> SetAdmissionBlockedReasonAsync(long id, Guid activation, string? reason, CancellationToken ct)
    {
        var body = await LockEligibleBodyAsync(id, activation, ct);
        if (body is null) return false;
        body.AdmissionBlockedReason = reason;
        body.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct); return true;
    }

    public async Task<bool> SaveCompletionIntentAsync(long id, Guid activation, JsonElement intent, CancellationToken ct)
    {
        if (intent.ValueKind != JsonValueKind.Object || Size(intent) > PayloadLimit)
            throw new ArgumentException("Automatic completion intent must be an object bounded to 1 MiB.", nameof(intent));
        var body = await LockEligibleBodyAsync(id, activation, ct);
        if (body is null || body.CompletionJson is not null) return false;
        body.CompletionJson = Document(intent); body.Phase = "errorCompletion";
        body.AdmissionBlockedReason = null; body.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct); return true;
    }

    public async Task<bool> SetAggregateVersionAsync(long id, Guid activation, long version, CancellationToken ct)
    {
        var body = await LockEligibleBodyAsync(id, activation, ct);
        if (body is null) return false;
        body.AggregateValueVersion = version; body.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct); return true;
    }

    public async Task<bool> SettleAsync(long id, Guid activation, string status, string? reason, CancellationToken ct)
    {
        if (status is not ("completed" or "interrupted" or "cancelled")) throw new ArgumentException("Invalid terminal body status.", nameof(status));
        var body = await LockEligibleBodyAsync(id, activation, ct, requireLiveToken: status == "completed");
        if (body is null) return false;
        if (await db.MultiInstanceActivityItems.AnyAsync(x => x.MultiInstanceExecutionId == id && (x.Status == "pending" || x.Status == "active"), ct))
            throw new InvalidOperationException("Cannot settle an automatic body with unfinished items.");
        if (status == "completed" && body.CompletedCount != body.TotalCount) throw new InvalidOperationException("Automatic body has unsuccessful items.");
        body.Status = status; body.Phase = "settled"; body.AdmissionBlockedReason = null; body.CompletionReason = reason;
        body.CompletedAt = DateTimeOffset.UtcNow; body.UpdatedAt = body.CompletedAt.Value;
        if (body.PinState is not null) body.PinState = AutomaticMultiInstancePinStates.Released;
        await db.SaveChangesAsync(ct);
        // Staged inputs and candidates exist only to publish or recover an item.
        // Once the body settles nothing reads them, and each can be up to 1 MiB.
        await db.MultiInstanceActivityItems
            .Where(item => item.MultiInstanceExecutionId == id && (item.StagedInputJson != null || item.CandidateJson != null))
            .ExecuteUpdateAsync(update => update.SetProperty(item => item.StagedInputJson, (JsonDocument?)null)
                .SetProperty(item => item.CandidateJson, (JsonDocument?)null), ct);
        return true;
    }

    public async Task<int> CancelUnfinishedItemsAsync(long id, Guid activation, string reason, CancellationToken ct)
    {
        var body = await LockEligibleBodyAsync(id, activation, ct, requireLiveToken: false);
        if (body is null) return 0;
        var now = DateTimeOffset.UtcNow;
        var total = 0;
        while (true)
        {
            var ids = await db.MultiInstanceActivityItems.Where(x => x.MultiInstanceExecutionId == id && (x.Status == "pending" || x.Status == "active"))
                .OrderBy(x => x.Id).Take(250).Select(x => x.Id).ToArrayAsync(ct);
            if (ids.Length == 0) break;
            total += await db.MultiInstanceActivityItems.Where(x => ids.Contains(x.Id)).ExecuteUpdateAsync(u => u
                .SetProperty(x => x.Status, "cancelled").SetProperty(x => x.Phase, "settled")
                .SetProperty(x => x.UpdatedAt, now).SetProperty(x => x.CompletedAt, now), ct);
            await db.NodeExecutions.Where(x => x.AutomaticMultiInstanceItemId != null && ids.Contains(x.AutomaticMultiInstanceItemId.Value))
                .ExecuteUpdateAsync(u => u.SetProperty(x => x.Status, "cancelled").SetProperty(x => x.CompletionReason, reason)
                    .SetProperty(x => x.UpdatedAt, now).SetProperty(x => x.CompletedAt, now), ct);
        }
        body.CancelledCount += total; body.UpdatedAt = now;
        await db.SaveChangesAsync(ct); return total;
    }

    private async Task<MultiInstanceExecutionEntity?> LockBodyAsync(long id, CancellationToken ct)
    {
        RequireTransaction();
        var entity = await db.MultiInstanceExecutions.FromSqlInterpolated($"SELECT * FROM flowbit.multi_instance_executions WHERE \"Id\" = {id} AND \"ActivityKind\" <> 'userTask' FOR UPDATE").SingleOrDefaultAsync(ct);
        if (entity is not null) await db.Entry(entity).ReloadAsync(ct);
        return entity;
    }

    public async Task<bool> EnterBodyWaitAsync(long id, Guid activation, long? entryJobId, CancellationToken ct)
    {
        var (body, token) = await LockBodyTokenAsync(id, activation, ct);
        if (body is null || token is null || body.Status != "active") return false;
        if (token.CurrentNodeExecutionId is not null || token.CurrentNodeExecution is not null) return false;
        if (token.WaitingMultiInstanceExecutionId == id && token.WaitState == "automaticMultiInstance") return true;
        if (token.WaitingMultiInstanceExecutionId is not null || token.WaitingTimerSubscriptionId is not null
            || token.WaitingJobId != entryJobId || token.WaitState is not (null or "asyncBefore" or "multiInstanceStart")) return false;
        token.WaitState = "automaticMultiInstance"; token.WaitingMultiInstanceExecutionId = id;
        token.WaitingJobId = null; token.WaitingTimerSubscriptionId = null; token.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct); return true;
    }

    public async Task<bool> ClearBodyWaitAsync(long id, Guid activation, CancellationToken ct)
    {
        var (body, token) = await LockBodyTokenAsync(id, activation, ct);
        if (body is null || token is null || token.WaitState != "automaticMultiInstance" || token.WaitingMultiInstanceExecutionId != id) return false;
        token.WaitState = null; token.WaitingMultiInstanceExecutionId = null;
        token.WaitingJobId = null; token.WaitingTimerSubscriptionId = null; token.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct); return true;
    }

    private async Task<(MultiInstanceExecutionEntity?, ExecutionTokenEntity?)> LockBodyTokenAsync(long id, Guid activation, CancellationToken ct)
    {
        RequireTransaction();
        var tokenId = await db.MultiInstanceExecutions.AsNoTracking().Where(body => body.Id == id && body.ActivityKind != "userTask")
            .Select(body => (long?)body.TokenId).SingleOrDefaultAsync(ct);
        if (tokenId is null) return (null, null);
        var token = await db.ExecutionTokens.FromSqlInterpolated($"SELECT * FROM flowbit.execution_tokens WHERE \"Id\" = {tokenId.Value} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (token is null) return (null, null);
        await db.Entry(token).ReloadAsync(ct);
        var body = await LockBodyAsync(id, ct);
        if (body is null || body.ActivationId != activation || token.ActivationId != activation || token.Status != "active"
            || token.InstanceId != body.InstanceId || token.NodeId != body.NodeId) return (null, null);
        return (body, token);
    }

    public async Task<bool> AcknowledgePublicationFenceRefreshAsync(long id, long jobId, Guid activation, Guid leaseToken, long leaseGeneration, CancellationToken ct)
    {
        if (await LockEligibleBodyAsync(id, activation, ct) is null) return false;
        return await db.WorkflowJobs.Where(job => job.Id == jobId
                && (job.MultiInstanceExecutionId == id && job.ActivationId == activation
                    || job.Kind == "callResume" && job.CallInvocation != null
                        && job.CallInvocation.MultiInstanceExecutionId == id && job.CallInvocation.ActivationId == activation
                        && job.CallInvocation.ChildInstanceId == job.InstanceId && job.CallInvocation.State == "running")
                && job.LeaseToken == leaseToken && job.LeaseGeneration == leaseGeneration
                && (job.Status == "running" || job.Status == "resultReady") && job.LeaseExpiresAt > DateTimeOffset.UtcNow)
            .ExecuteUpdateAsync(u => u.SetProperty(job => job.PublicationFenceRefreshRequested, false), ct) == 1;
    }

    private async Task<MultiInstanceExecutionEntity?> LockEligibleBodyAsync(long id, Guid activation, CancellationToken ct, bool requireLiveToken = true)
    {
        var body = await LockBodyAsync(id, ct);
        if (body is null || body.Status != "active" || body.ActivationId != activation) return null;
        // Cancellation cleanup remains valid after token movement/termination;
        // it can only close old work, never publish or admit new work.
        if (!requireLiveToken) return body;
        // The caller holds instance/token locks before this body lock. Verify fresh
        // persisted ownership without taking an inverse-order token lock; a late
        // result cannot revive work after token movement even if cleanup is delayed.
        return await db.ExecutionTokens.AsNoTracking().AnyAsync(token => token.Id == body.TokenId
            && token.InstanceId == body.InstanceId && token.NodeId == body.NodeId && token.ActivationId == activation
            && token.Status == "active" && token.Instance!.Status == "running"
            && token.Instance.WorkflowDefinitionId == body.WorkflowDefinitionId, ct) ? body : null;
    }

    private async Task<MultiInstanceActivityItemEntity?> LockItemAsync(long id, long itemId, CancellationToken ct)
    {
        RequireTransaction();
        var item = await db.MultiInstanceActivityItems.FromSqlInterpolated($"SELECT * FROM flowbit.multi_instance_activity_items WHERE \"Id\" = {itemId} AND \"MultiInstanceExecutionId\" = {id} FOR UPDATE").SingleOrDefaultAsync(ct);
        if (item is not null) await db.Entry(item).ReloadAsync(ct);
        return item;
    }

    private Task<int> FinishLedgerAsync(long itemId, string status, string reason, string? code, string? description, DateTimeOffset now, CancellationToken ct) =>
        db.NodeExecutions.Where(x => x.AutomaticMultiInstanceItemId == itemId).ExecuteUpdateAsync(u => u
            .SetProperty(x => x.Status, status).SetProperty(x => x.CompletionReason, reason)
            .SetProperty(x => x.CompletedBy, x => x.TriggeredBy)
            .SetProperty(x => x.CompletedByRolesJson, x => x.TriggeredByRolesJson)
            .SetProperty(x => x.CompletedByClaimsJson, x => x.TriggeredByClaimsJson)
            .SetProperty(x => x.CompletedActingFor, x => x.TriggeredActingFor)
            .SetProperty(x => x.CompletedDelegationId, x => x.TriggeredDelegationId)
            .SetProperty(x => x.ErrorCode, code).SetProperty(x => x.ErrorDescription, description)
            .SetProperty(x => x.UpdatedAt, now).SetProperty(x => x.CompletedAt, now), ct);

    private void RequireTransaction()
    {
        if (db.Database.CurrentTransaction is null) throw new InvalidOperationException("Automatic multi-instance mutations require an ambient runtime transaction.");
    }

    // The measure the size check constraints use, so these guards and the database agree.
    private static long Size(JsonElement value) => JsonbText.ByteCount(value);
    private static void CheckSize(JsonElement first, JsonElement? second)
    {
        if (Size(first) + (second.HasValue ? Size(second.Value) : 0) > PayloadLimit)
            throw new ArgumentException("Automatic item staged input and candidate exceed 1 MiB.");
    }
    private static JsonDocument Document(JsonElement element) => JsonDocument.Parse(element.GetRawText());
    private static string Limit(string value, int length) => string.Concat(value.EnumerateRunes().Take(length).Select(x => x.ToString()));
    private static AutomaticMultiInstanceRecord Map(MultiInstanceExecutionEntity x) => new(x.Id, x.InstanceId, x.TokenId,
        x.WorkflowDefinitionId!.Value, x.ActivationId!.Value, x.NodeId, x.ActivityKind, x.Mode, x.Status, x.Phase!,
        x.ResultVariable, x.AggregateScope!, x.AggregateSharedKey, x.AggregateValueVersion!.Value, x.InputSnapshotId!.Value,
        x.MaxInstances!.Value, x.EffectiveConcurrency!.Value, x.TotalCount, x.CompletedCount, x.FailedCount, x.CancelledCount,
        x.ResultBytes, x.AdmissionBlockedReason, x.ControlGeneration, x.CreatedAt, x.UpdatedAt, x.CompletedAt)
        {
            CompletionIntent = x.CompletionJson?.RootElement.Clone(),
            PinnedChildDefinitionId = x.PinnedChildDefinitionId, PinnedChildStartEventId = x.PinnedChildStartEventId,
            PinState = x.PinState
        };
    private static AutomaticMultiInstanceItemRecord Map(MultiInstanceActivityItemEntity x) => new(x.Id, x.InstanceId,
        x.MultiInstanceExecutionId, x.ItemIndex, x.Status, x.Phase, x.StagedInputJson?.RootElement.Clone(),
        x.CandidateJson?.RootElement.Clone(), x.ResultJson?.RootElement.Clone(), x.SharedWriteCorrelationsJson?.RootElement.Clone(),
        x.ErrorCode, x.ErrorDescription, x.CreatedAt, x.UpdatedAt, x.StartedAt, x.CompletedAt);
}
