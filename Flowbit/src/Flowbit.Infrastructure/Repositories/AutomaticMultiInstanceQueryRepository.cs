using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Flowbit.Infrastructure.Repositories;

/// <summary>Bounded automatic-item reads; raw staging and candidate JSON never enter a DTO.</summary>
public sealed class AutomaticMultiInstanceQueryRepository(AppDbContext db) : IAutomaticMultiInstanceQueryRepository
{
    private const string PayloadScopeSql = """
        (@global OR EXISTS (
            SELECT 1 FROM jsonb_array_elements_text(
                CASE WHEN jsonb_typeof(d."Definition" -> 'taskAssignmentRoles') = 'array'
                     THEN d."Definition" -> 'taskAssignmentRoles' ELSE '[]'::jsonb END) role
            WHERE lower(role) = ANY(@roles)))
        """;
    private const string FromSql = """
        FROM flowbit.multi_instance_activity_items i
        JOIN flowbit.multi_instance_executions e ON e."Id" = i."MultiInstanceExecutionId" AND e."InstanceId" = i."InstanceId"
        JOIN flowbit.workflow_instances w ON w."Id" = e."InstanceId"
        JOIN flowbit.workflow_definitions d ON d."Id" = e."WorkflowDefinitionId"
        LEFT JOIN LATERAL (
            SELECT j."Id" FROM flowbit.workflow_jobs j
            WHERE j."AutomaticMultiInstanceItemId" = i."Id" AND j."InstanceId" = i."InstanceId"
            ORDER BY j."Id" DESC LIMIT 1
        ) job ON TRUE
        LEFT JOIN LATERAL (
            SELECT incident."Id" FROM flowbit.workflow_incidents incident
            JOIN flowbit.workflow_jobs j ON j."Id" = incident."JobId"
            WHERE j."AutomaticMultiInstanceItemId" = i."Id" AND j."InstanceId" = i."InstanceId" AND incident."Status" = 'open'
            ORDER BY incident."Id" DESC LIMIT 1
        ) incident ON TRUE
        LEFT JOIN LATERAL (
            SELECT n."Id" FROM flowbit.node_executions n
            WHERE n."AutomaticMultiInstanceItemId" = i."Id" AND n."InstanceId" = i."InstanceId"
            ORDER BY n."Id" DESC LIMIT 1
        ) ledger ON TRUE
        """;

    private const string SummarySql = """
        i."Id", i."InstanceId", i."MultiInstanceExecutionId", i."ItemIndex", e."ActivityKind",
        i."Status", i."Phase", i."CreatedAt", i."UpdatedAt", i."StartedAt", i."CompletedAt",
        job."Id" AS "JobId", incident."Id" AS "IncidentId", ledger."Id" AS "NodeExecutionId"
        """;

    // Only constant SQL fragments are composed; every input is a typed parameter.
#pragma warning disable EF1002
    public async Task<PagedResult<AutomaticMultiInstanceItemSummaryDto>?> ListItemsAsync(long executionId,
        int page, int pageSize, NodeExecutionAuthorization authorization, CancellationToken cancellationToken)
    {
        var exists = await db.MultiInstanceExecutions.AsNoTracking()
            .AnyAsync(body => body.Id == executionId && body.ActivityKind != "userTask", cancellationToken);
        if (!exists) return null;
        var count = await db.MultiInstanceActivityItems.AsNoTracking()
            .Where(item => item.MultiInstanceExecutionId == executionId && item.Execution!.ActivityKind != "userTask")
            .LongCountAsync(cancellationToken);
        var rows = await db.Database.SqlQueryRaw<ItemRow>($"""
            SELECT {SummarySql}, {PayloadScopeSql} AS "CanReadPayload", NULL::text AS "ItemJson", NULL::text AS "ResultJson",
                   NULL::text AS "ErrorCode", NULL::text AS "ErrorDescription"
            {FromSql}
            WHERE e."Id" = @executionId AND e."ActivityKind" <> 'userTask'
            ORDER BY i."ItemIndex", i."Id"
            LIMIT @take OFFSET @skip
            """, new NpgsqlParameter("executionId", executionId), new NpgsqlParameter("take", pageSize),
            new NpgsqlParameter("skip", (long)(page - 1) * pageSize),
            new NpgsqlParameter("global", authorization.IsGlobalReader),
            new NpgsqlParameter("roles", authorization.LowerCallerRoles.ToArray())).ToListAsync(cancellationToken);
        return new PagedResult<AutomaticMultiInstanceItemSummaryDto>(rows.Select(ToSummary).ToList(), page, pageSize, count);
    }

    public async Task<AutomaticMultiInstanceItemDetailDto?> GetItemAsync(long executionId, long itemId,
        NodeExecutionAuthorization authorization, CancellationToken cancellationToken)
    {
        var row = await db.Database.SqlQueryRaw<ItemRow>($"""
            SELECT {SummarySql}, TRUE AS "CanReadPayload", (snapshot."VariablesJson" -> 'collection' -> i."ItemIndex")::text AS "ItemJson",
                   i."ResultJson"::text AS "ResultJson", i."ErrorCode", i."ErrorDescription"
            {FromSql}
            LEFT JOIN flowbit.workflow_job_snapshots snapshot ON snapshot."Id" = e."InputSnapshotId"
            WHERE e."Id" = @executionId AND i."Id" = @itemId AND e."ActivityKind" <> 'userTask'
              AND {PayloadScopeSql}
            """, new NpgsqlParameter("executionId", executionId), new NpgsqlParameter("itemId", itemId),
            new NpgsqlParameter("global", authorization.IsGlobalReader),
            new NpgsqlParameter("roles", authorization.LowerCallerRoles.ToArray())).SingleOrDefaultAsync(cancellationToken);
        return row is null ? null : new AutomaticMultiInstanceItemDetailDto(ToSummary(row), Parse(row.ItemJson),
            Parse(row.ResultJson), row.ErrorCode, row.ErrorDescription);
    }
#pragma warning restore EF1002

    private static AutomaticMultiInstanceItemSummaryDto ToSummary(ItemRow row) => new(row.Id, row.InstanceId,
        row.MultiInstanceExecutionId, row.ItemIndex, row.ActivityKind, row.Status, row.Phase, row.CreatedAt,
        row.UpdatedAt, row.StartedAt, row.CompletedAt, row.JobId, row.IncidentId, row.NodeExecutionId, row.CanReadPayload);
    private static JsonElement? Parse(string? value)
    {
        if (value is null) return null;
        using var document = JsonDocument.Parse(value);
        return document.RootElement.Clone();
    }

    private sealed class ItemRow
    {
        public long Id { get; set; }
        public long InstanceId { get; set; }
        public long MultiInstanceExecutionId { get; set; }
        public int ItemIndex { get; set; }
        public string ActivityKind { get; set; } = string.Empty;
        public string Status { get; set; } = string.Empty;
        public string Phase { get; set; } = string.Empty;
        public DateTimeOffset CreatedAt { get; set; }
        public DateTimeOffset UpdatedAt { get; set; }
        public DateTimeOffset? StartedAt { get; set; }
        public DateTimeOffset? CompletedAt { get; set; }
        public long? JobId { get; set; }
        public long? IncidentId { get; set; }
        public long? NodeExecutionId { get; set; }
        public bool CanReadPayload { get; set; }
        public string? ItemJson { get; set; }
        public string? ResultJson { get; set; }
        public string? ErrorCode { get; set; }
        public string? ErrorDescription { get; set; }
    }
}
