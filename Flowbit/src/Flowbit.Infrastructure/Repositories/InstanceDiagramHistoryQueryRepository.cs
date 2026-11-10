using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Flowbit.Infrastructure.Repositories;

/// <summary>
/// A single PostgreSQL statement supplies a consistent, non-locking snapshot.
/// Only grouped evidence crosses the database boundary; no rich activity joins,
/// paging, rollups, or actor/variable data participate in this read.
/// </summary>
public sealed class InstanceDiagramHistoryQueryRepository(AppDbContext dbContext)
    : IInstanceDiagramHistoryQueryRepository
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private const string SnapshotSql = """
        WITH identity AS MATERIALIZED (
            SELECT i."Id" AS "InstanceId", i."WorkflowDefinitionId" AS "WorkflowId",
                   d."Version" AS "WorkflowVersion", i."UpdatedAt" AS "InstanceUpdatedAt",
                   statement_timestamp() AS "AsOf", d."Definition"::text AS "DefinitionJson",
                   i."NodeActivityFromCreation", i."NodeActivityPrunedAt", i."HistoryPrunedAt"
            FROM flowbit.workflow_instances i
            JOIN flowbit.workflow_definitions d ON d."Id" = i."WorkflowDefinitionId"
            WHERE i."Id" = @instanceId
        ), visits AS MATERIALIZED (
            SELECT ne."NodeId", ne."ExecutionKind", ne."EnteredViaFlowId",
                   ne."ExitedViaFlowId", ne."MultiInstanceExecutionId"
            FROM flowbit.node_executions ne
            JOIN identity i ON i."InstanceId" = ne."InstanceId"
                           AND i."WorkflowId" = ne."WorkflowDefinitionId"
        ), mi_activations AS MATERIALIZED (
            SELECT "MultiInstanceExecutionId", min("NodeId") AS "NodeId",
                   min("EnteredViaFlowId") AS "FlowId",
                   count(DISTINCT "NodeId") > 1 AS "UnmappedNode",
                   count(DISTINCT "EnteredViaFlowId")
                     + CASE WHEN bool_or("EnteredViaFlowId" IS NULL) THEN 1 ELSE 0 END > 1 AS "ConflictingEntry"
            FROM visits
            WHERE "ExecutionKind" = 'userTaskItem'
            GROUP BY "MultiInstanceExecutionId"
        ), exits AS (
            SELECT "NodeId", "ExitedViaFlowId" AS "FlowId", count(*) AS "Count"
            FROM visits
            WHERE "ExecutionKind" = 'node' AND "ExitedViaFlowId" IS NOT NULL
            GROUP BY "NodeId", "ExitedViaFlowId"
        ), entries AS (
            SELECT "NodeId", "EnteredViaFlowId" AS "FlowId", count(*) AS "Count"
            FROM visits
            WHERE "ExecutionKind" = 'node' AND "EnteredViaFlowId" IS NOT NULL
            GROUP BY "NodeId", "EnteredViaFlowId"
        ), mi_entries AS (
            SELECT "NodeId", "FlowId", count(*) AS "Count"
            FROM mi_activations
            WHERE NOT "UnmappedNode" AND NOT "ConflictingEntry" AND "FlowId" IS NOT NULL
            GROUP BY "NodeId", "FlowId"
        )
        SELECT i.*,
               EXISTS (SELECT 1 FROM flowbit.node_executions seeded
                       WHERE seeded."InstanceId" = i."InstanceId" AND seeded."IsCutoverSeeded") AS "HasCutoverSeeded",
               EXISTS (SELECT 1 FROM mi_activations WHERE "ConflictingEntry") AS "HasConflictingEntries",
               EXISTS (SELECT 1 FROM mi_activations WHERE "UnmappedNode") AS "HasUnmappedMultiInstanceNodes",
               COALESCE((SELECT array_agg(DISTINCT "NodeId" ORDER BY "NodeId") FROM visits), ARRAY[]::integer[]) AS "ReachedNodeIds",
               COALESCE((SELECT jsonb_agg(to_jsonb(e) ORDER BY e."NodeId", e."FlowId") FROM exits e), '[]'::jsonb)::text AS "ExitsJson",
               COALESCE((SELECT jsonb_agg(to_jsonb(e) ORDER BY e."NodeId", e."FlowId") FROM entries e), '[]'::jsonb)::text AS "EntriesJson",
               COALESCE((SELECT jsonb_agg(to_jsonb(e) ORDER BY e."NodeId", e."FlowId") FROM mi_entries e), '[]'::jsonb)::text AS "MultiInstanceEntriesJson"
        FROM identity i
        """;

    public async Task<InstanceDiagramHistorySnapshot?> GetAsync(long instanceId, CancellationToken cancellationToken)
    {
        var row = await dbContext.Database.SqlQueryRaw<SnapshotRow>(
                SnapshotSql, new NpgsqlParameter("instanceId", instanceId))
            .SingleOrDefaultAsync(cancellationToken);
        if (row is null) return null;
        var definition = JsonSerializer.Deserialize<WorkflowModel>(row.DefinitionJson, JsonOptions)
            ?? throw new InvalidOperationException("The instance workflow definition is unavailable.");
        // Match the definition shown by instance detail, including older JSONB
        // snapshots. This model belongs only to this read, never to the cache.
        WorkflowModelMigrator.Normalize(definition);
        return new InstanceDiagramHistorySnapshot
        {
            InstanceId = row.InstanceId,
            WorkflowId = row.WorkflowId,
            WorkflowVersion = row.WorkflowVersion,
            InstanceUpdatedAt = row.InstanceUpdatedAt,
            AsOf = row.AsOf,
            Definition = definition,
            NodeActivityFromCreation = row.NodeActivityFromCreation,
            NodeActivityPrunedAt = row.NodeActivityPrunedAt,
            HistoryPrunedAt = row.HistoryPrunedAt,
            HasCutoverSeeded = row.HasCutoverSeeded,
            HasConflictingEntries = row.HasConflictingEntries,
            HasUnmappedMultiInstanceNodes = row.HasUnmappedMultiInstanceNodes,
            ReachedNodeIds = row.ReachedNodeIds,
            Exits = ReadEvidence(row.ExitsJson),
            Entries = ReadEvidence(row.EntriesJson),
            MultiInstanceEntries = ReadEvidence(row.MultiInstanceEntriesJson)
        };
    }

    private static InstanceDiagramTraversalEvidence[] ReadEvidence(string json) =>
        JsonSerializer.Deserialize<InstanceDiagramTraversalEvidence[]>(json, JsonOptions)
        ?? throw new InvalidOperationException("The grouped instance evidence is unavailable.");

    private sealed class SnapshotRow
    {
        public long InstanceId { get; init; }
        public long WorkflowId { get; init; }
        public int WorkflowVersion { get; init; }
        public DateTimeOffset InstanceUpdatedAt { get; init; }
        public DateTimeOffset AsOf { get; init; }
        public string DefinitionJson { get; init; } = string.Empty;
        public bool NodeActivityFromCreation { get; init; }
        public DateTimeOffset? NodeActivityPrunedAt { get; init; }
        public DateTimeOffset? HistoryPrunedAt { get; init; }
        public bool HasCutoverSeeded { get; init; }
        public bool HasConflictingEntries { get; init; }
        public bool HasUnmappedMultiInstanceNodes { get; init; }
        public int[] ReachedNodeIds { get; init; } = [];
        public string ExitsJson { get; init; } = "[]";
        public string EntriesJson { get; init; } = "[]";
        public string MultiInstanceEntriesJson { get; init; } = "[]";
    }
}
