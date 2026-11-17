using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds signal events (EVENT-04): <c>signal_subscriptions</c> for waits, and
    /// <c>signal_broadcasts</c>, <c>signal_deliveries</c>, and <c>signal_broadcast_jobs</c>
    /// for thrown signals and their delivery. Adds the completion reason
    /// <c>signalReceived</c>, makes the <c>signalBroadcastDeliver</c> job kind report no
    /// node in incident lifecycle events, and seeds the <c>signalBroadcasts</c> retention
    /// category and the three <c>Signals.*</c> engine settings without overwriting rows.
    /// </summary>
    /// <remarks>
    /// The replaced completion-reason check is added <c>NOT VALID</c>, so this migration
    /// takes a brief lock and does not scan <c>node_executions</c>.
    /// ValidateSignalEventConstraints validates it outside a transaction. Down refuses while
    /// any signal subscription, broadcast, delivery job, or node visit completed by a signal
    /// exists.
    /// </remarks>
    public partial class AddSignalEvents : Migration
    {
        internal const string CompletionReasonsBefore =
            "'normal', 'userAction', 'administrativeAction', 'messageDelivery', 'multiInstanceItem', 'multiInstanceCompleted', 'multiInstanceInterrupt', 'boundaryCaught', 'normalEnd', 'terminateEnd', 'errorEnd', 'instanceCancelled', 'gatewayScopeCancelled', 'gatewayJoinMerged', 'parallelFork', 'parallelJoin', 'inclusiveSplit', 'inclusiveMerge', 'complexActivation', 'complexReset', 'scopedInterrupt', 'scopedInterruptSkipped', 'timerFired', 'conditionalTriggered', 'callCompleted', 'callFailed'";

        private const string NodelessKindsBefore =
            "'instanceVersionChangeBatchPrepare', 'instanceVersionChangeBatchExecute',\n"
            + "                       'instanceVariableUpdateBatchPrepare', 'instanceVariableUpdateBatchExecute'";

        private const string NodelessKindsAfter =
            NodelessKindsBefore + ",\n                       'signalBroadcastDeliver'";

        private static string CompletionReasonCheck(string reasons) =>
            "((\"Status\" IN ('pending', 'active') AND \"CompletionReason\" IS NULL) OR "
            + "(\"Status\" IN ('completed', 'cancelled', 'faulted', 'merged') AND \"CompletionReason\" IN ("
            + reasons + ")))";

        // The body of AddOutboundLifecycleWebhooks' function, with the list of job kinds that
        // name an anchor node rather than an authored one as the only change.
        private static string CaptureIncidentFunction(string nodelessKinds) => """
            CREATE OR REPLACE FUNCTION flowbit.capture_workflow_incident_lifecycle()
            RETURNS trigger
            LANGUAGE plpgsql
            SECURITY INVOKER
            SET search_path = pg_catalog, pg_temp
            AS $function$
            DECLARE
                targets bigint[];
                token_id bigint;
                job_kind text;
                node_id integer;
                node_external_id text;
            BEGIN
                IF NEW."Status" <> 'open'
                   OR NOT flowbit.lifecycle_capture_wanted(ARRAY['incident.opened']) THEN
                    RETURN NULL;
                END IF;
                targets := flowbit.lifecycle_event_targets('incident.opened', NEW."WorkflowKey");
                IF cardinality(targets) = 0 THEN
                    RETURN NULL;
                END IF;
                SELECT job."TokenId", job."Kind"
                  INTO token_id, job_kind
                  FROM flowbit.workflow_jobs job
                 WHERE job."Id" = NEW."OriginalJobId";
                -- Version-change, variable-update batch, and signal delivery jobs name the initial
                -- start event only to anchor their row, so no authored node applies to them.
                -- LifecycleEventCaptureTests classifies every job kind, so a new kind cannot miss
                -- this list unnoticed.
                IF NEW."NodeId" > 0
                   AND COALESCE(job_kind, '') NOT IN (
                       __KINDS__) THEN
                    node_id := NEW."NodeId";
                    -- Definitions do not limit external ids, but tokens and tasks store at most 300
                    -- characters, so a longer one never belongs to a node that runs. It is reported as
                    -- null rather than truncated, which keeps every body far below its 16 KiB bound.
                    SELECT node ->> 'externalId'
                      INTO node_external_id
                      FROM flowbit.workflow_definitions definition
                     CROSS JOIN LATERAL jsonb_array_elements(
                         CASE WHEN jsonb_typeof(definition."Definition" -> 'flowNodes') = 'array'
                              THEN definition."Definition" -> 'flowNodes'
                              ELSE '[]'::jsonb END) node
                     WHERE definition."Id" = NEW."WorkflowDefinitionId"
                       AND node ->> 'id' = NEW."NodeId"::text
                       AND jsonb_typeof(node -> 'externalId') = 'string'
                       AND char_length(node ->> 'externalId') <= 300
                     LIMIT 1;
                END IF;
                PERFORM flowbit.record_lifecycle_event(
                    'incident.opened', NEW."CreatedAt", 'workflow_incidents', NEW."Id", NEW."WorkflowKey",
                    NEW."WorkflowDefinitionId", NEW."InstanceId", NULL,
                    '{"incidentId":' || NEW."Id"::text
                        || ',"jobId":' || NEW."OriginalJobId"::text
                        || ',"executionTokenId":' || COALESCE(token_id::text, 'null')
                        || ',"nodeId":' || COALESCE(node_id::text, 'null')
                        || ',"nodeExternalId":' || flowbit.lifecycle_json_text(node_external_id)
                        || ',"status":"open"}',
                    '{}'::text[], targets);
                RETURN NULL;
            END;
            $function$;
            """.Replace("__KINDS__", nodelessKinds, StringComparison.Ordinal);

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_node_executions_completion_reason",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.CreateTable(
                name: "signal_broadcasts",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SignalName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    WorkflowKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: true),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    TotalDeliveryCount = table.Column<int>(type: "integer", nullable: false),
                    DeliveredCount = table.Column<int>(type: "integer", nullable: false),
                    SkippedCount = table.Column<int>(type: "integer", nullable: false),
                    FailedCount = table.Column<int>(type: "integer", nullable: false),
                    CancelledCount = table.Column<int>(type: "integer", nullable: false),
                    RecipientsJson = table.Column<string>(type: "jsonb", nullable: false),
                    RecipientsTruncated = table.Column<bool>(type: "boolean", nullable: false),
                    ThrownByKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ThrownBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ThrownByRolesJson = table.Column<string>(type: "jsonb", nullable: false),
                    ThrownByClaimsJson = table.Column<string>(type: "jsonb", nullable: true),
                    IdempotencyKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true, collation: "C"),
                    RequestHash = table.Column<byte[]>(type: "bytea", nullable: true),
                    CancelledBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CancellationReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_signal_broadcasts", x => x.Id);
                    table.CheckConstraint("CK_signal_broadcasts_counts", "\"TotalDeliveryCount\" BETWEEN 0 AND 100000 AND \"DeliveredCount\" >= 0 AND \"SkippedCount\" >= 0 AND \"FailedCount\" >= 0 AND \"CancelledCount\" >= 0 AND \"DeliveredCount\" + \"SkippedCount\" + \"FailedCount\" + \"CancelledCount\" <= \"TotalDeliveryCount\"");
                    table.CheckConstraint("CK_signal_broadcasts_payload", "\"PayloadJson\" IS NULL OR (jsonb_typeof(\"PayloadJson\") = 'object' AND octet_length(\"PayloadJson\"::text) <= 262144)");
                    table.CheckConstraint("CK_signal_broadcasts_status", "\"Status\" IN ('delivering', 'completed', 'completedWithIssues', 'cancelled')");
                    table.CheckConstraint("CK_signal_broadcasts_terminal_time", "(\"Status\" = 'delivering' AND \"CompletedAt\" IS NULL) OR (\"Status\" <> 'delivering' AND \"CompletedAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_signal_broadcasts_thrower", "\"ThrownByKind\" IN ('user', 'client')");
                });

            migrationBuilder.CreateTable(
                name: "signal_subscriptions",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    WorkflowKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    TokenId = table.Column<long>(type: "bigint", nullable: false),
                    ActivationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SignalNodeId = table.Column<int>(type: "integer", nullable: false),
                    SignalNodeName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    AttachedToNodeId = table.Column<int>(type: "integer", nullable: true),
                    OutgoingFlowId = table.Column<int>(type: "integer", nullable: false),
                    SignalName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    CancelActivity = table.Column<bool>(type: "boolean", nullable: false),
                    Occurrence = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_signal_subscriptions", x => x.Id);
                    table.CheckConstraint("CK_signal_subscriptions_catch_consumes", "\"AttachedToNodeId\" IS NOT NULL OR \"CancelActivity\"");
                    table.CheckConstraint("CK_signal_subscriptions_occurrence", "\"Occurrence\" >= 0");
                    table.CheckConstraint("CK_signal_subscriptions_status", "\"Status\" IN ('active', 'completed', 'cancelled')");
                    table.CheckConstraint("CK_signal_subscriptions_terminal_time", "(\"Status\" = 'active' AND \"CompletedAt\" IS NULL) OR (\"Status\" IN ('completed', 'cancelled') AND \"CompletedAt\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_signal_subscriptions_execution_tokens_TokenId",
                        column: x => x.TokenId,
                        principalSchema: "flowbit",
                        principalTable: "execution_tokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_signal_subscriptions_workflow_definitions_WorkflowDefinitio~",
                        columns: x => new { x.WorkflowDefinitionId, x.WorkflowKey },
                        principalSchema: "flowbit",
                        principalTable: "workflow_definitions",
                        principalColumns: new[] { "Id", "WorkflowKey" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_signal_subscriptions_workflow_instances_InstanceId",
                        column: x => x.InstanceId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "signal_broadcast_jobs",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BroadcastId = table.Column<long>(type: "bigint", nullable: false),
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    JobId = table.Column<long>(type: "bigint", nullable: true),
                    OriginalJobId = table.Column<long>(type: "bigint", nullable: false),
                    Reason = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    RequestedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_signal_broadcast_jobs", x => x.Id);
                    table.CheckConstraint("CK_signal_broadcast_jobs_reason", "\"Reason\" IN ('throw', 'retry')");
                    table.ForeignKey(
                        name: "FK_signal_broadcast_jobs_signal_broadcasts_BroadcastId",
                        column: x => x.BroadcastId,
                        principalSchema: "flowbit",
                        principalTable: "signal_broadcasts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_signal_broadcast_jobs_workflow_jobs_JobId",
                        column: x => x.JobId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_jobs",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                });

            migrationBuilder.CreateTable(
                name: "signal_deliveries",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    BroadcastId = table.Column<long>(type: "bigint", nullable: false),
                    SubscriptionId = table.Column<long>(type: "bigint", nullable: false),
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    WorkflowKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    TokenId = table.Column<long>(type: "bigint", nullable: false),
                    ActivationId = table.Column<Guid>(type: "uuid", nullable: false),
                    SignalNodeId = table.Column<int>(type: "integer", nullable: false),
                    AttachedToNodeId = table.Column<int>(type: "integer", nullable: true),
                    CancelActivity = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    ResultCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ResultDescription = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_signal_deliveries", x => x.Id);
                    table.CheckConstraint("CK_signal_deliveries_attempts", "\"AttemptCount\" >= 0");
                    table.CheckConstraint("CK_signal_deliveries_status", "\"Status\" IN ('pending', 'delivered', 'skipped', 'failed', 'cancelled')");
                    table.CheckConstraint("CK_signal_deliveries_terminal_time", "(\"Status\" = 'pending' AND \"CompletedAt\" IS NULL) OR (\"Status\" <> 'pending' AND \"CompletedAt\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_signal_deliveries_signal_broadcasts_BroadcastId",
                        column: x => x.BroadcastId,
                        principalSchema: "flowbit",
                        principalTable: "signal_broadcasts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.Sql(
                "ALTER TABLE flowbit.node_executions ADD CONSTRAINT \"CK_node_executions_completion_reason\" CHECK ("
                + CompletionReasonCheck(CompletionReasonsBefore + ", 'signalReceived'")
                + ") NOT VALID;");

            migrationBuilder.CreateIndex(
                name: "IX_signal_broadcast_jobs_BroadcastId_Id",
                schema: "flowbit",
                table: "signal_broadcast_jobs",
                columns: new[] { "BroadcastId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_signal_broadcast_jobs_JobId",
                schema: "flowbit",
                table: "signal_broadcast_jobs",
                column: "JobId",
                unique: true,
                filter: "\"JobId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_signal_broadcast_jobs_OriginalJobId",
                schema: "flowbit",
                table: "signal_broadcast_jobs",
                column: "OriginalJobId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_signal_broadcasts_SignalName_CreatedAt_Id",
                schema: "flowbit",
                table: "signal_broadcasts",
                columns: new[] { "SignalName", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_signal_broadcasts_Status_UpdatedAt_Id",
                schema: "flowbit",
                table: "signal_broadcasts",
                columns: new[] { "Status", "UpdatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_signal_broadcasts_ThrownByKind_ThrownBy_CreatedAt_Id",
                schema: "flowbit",
                table: "signal_broadcasts",
                columns: new[] { "ThrownByKind", "ThrownBy", "CreatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_signal_broadcasts_ThrownByKind_ThrownBy_IdempotencyKey",
                schema: "flowbit",
                table: "signal_broadcasts",
                columns: new[] { "ThrownByKind", "ThrownBy", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_signal_deliveries_BroadcastId_Status_Id",
                schema: "flowbit",
                table: "signal_deliveries",
                columns: new[] { "BroadcastId", "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_signal_deliveries_BroadcastId_SubscriptionId",
                schema: "flowbit",
                table: "signal_deliveries",
                columns: new[] { "BroadcastId", "SubscriptionId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_signal_deliveries_BroadcastId_WorkflowDefinitionId_Status_Id",
                schema: "flowbit",
                table: "signal_deliveries",
                columns: new[] { "BroadcastId", "WorkflowDefinitionId", "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_signal_deliveries_InstanceId_Id",
                schema: "flowbit",
                table: "signal_deliveries",
                columns: new[] { "InstanceId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_signal_subscriptions_active_name",
                schema: "flowbit",
                table: "signal_subscriptions",
                columns: new[] { "SignalName", "WorkflowKey", "Id" },
                filter: "\"Status\" = 'active'");

            migrationBuilder.CreateIndex(
                name: "IX_signal_subscriptions_active_token",
                schema: "flowbit",
                table: "signal_subscriptions",
                columns: new[] { "InstanceId", "TokenId" },
                filter: "\"Status\" = 'active'");

            migrationBuilder.CreateIndex(
                name: "IX_signal_subscriptions_InstanceId_TokenId_ActivationId_Signal~",
                schema: "flowbit",
                table: "signal_subscriptions",
                columns: new[] { "InstanceId", "TokenId", "ActivationId", "SignalNodeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_signal_subscriptions_TokenId",
                schema: "flowbit",
                table: "signal_subscriptions",
                column: "TokenId");

            migrationBuilder.CreateIndex(
                name: "IX_signal_subscriptions_WorkflowDefinitionId_WorkflowKey",
                schema: "flowbit",
                table: "signal_subscriptions",
                columns: new[] { "WorkflowDefinitionId", "WorkflowKey" });

            migrationBuilder.Sql(CaptureIncidentFunction(NodelessKindsAfter));

            migrationBuilder.Sql("""
                INSERT INTO flowbit.retention_policies
                    ("Category", "RetentionDays", "Revision", "UpdatedAt", "UpdatedBy", "IsInitialized")
                VALUES ('signalBroadcasts', NULL, 1, now(), NULL, TRUE)
                ON CONFLICT ("Category") DO NOTHING;
                """);

            SeedSetting(
                migrationBuilder,
                "Signals",
                "RequiredRole",
                "admin",
                "Comma-separated roles allowed to throw, read, cancel, and retry signal broadcasts. Missing or blank means admin.");
            SeedSetting(
                migrationBuilder,
                "Signals",
                "MaxSubscriptionsPerBroadcast",
                "10000",
                "Maximum number of waiting signal subscriptions one broadcast may reach; a throw that would reach more is rejected. Invalid or missing values default to 10000, and values above 100000 are capped.");
            SeedSetting(
                migrationBuilder,
                "Signals",
                "MaxOpenBroadcasts",
                "20",
                "Soft limit on signal broadcasts that are still delivering; a throw beyond it is rejected. Invalid or missing values default to 20, and values above 1000 are capped.");
        }

        private static void SeedSetting(
            MigrationBuilder migrationBuilder,
            string ns,
            string key,
            string value,
            string description)
        {
            migrationBuilder.Sql($"""
                INSERT INTO flowbit.engine_settings
                    ("Namespace", "Key", "Value", "Description", "CreatedAt", "UpdatedAt")
                SELECT '{ns}', '{key}', '{value}', '{description.Replace("'", "''", StringComparison.Ordinal)}',
                    CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                WHERE NOT EXISTS
                (
                    SELECT 1
                    FROM flowbit.engine_settings
                    WHERE
                        ("Namespace" = '{ns}' AND "Key" = '{key}')
                        OR
                        (BTRIM(COALESCE("Namespace", '')) = '' AND "Key" = '{ns}.{key}')
                )
                ON CONFLICT ("Namespace", "Key") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $guard$
                BEGIN
                    IF EXISTS (SELECT 1 FROM flowbit.signal_subscriptions)
                       OR EXISTS (SELECT 1 FROM flowbit.signal_broadcasts)
                       OR EXISTS (SELECT 1 FROM flowbit.workflow_jobs WHERE "Kind" = 'signalBroadcastDeliver')
                       OR EXISTS (SELECT 1 FROM flowbit.node_executions WHERE "CompletionReason" = 'signalReceived') THEN
                        RAISE EXCEPTION 'Signal events are in use. Remove signal subscriptions, broadcasts, delivery jobs, and node visits completed by a signal before reverting AddSignalEvents.';
                    END IF;
                END
                $guard$;
                """);

            migrationBuilder.Sql(CaptureIncidentFunction(NodelessKindsBefore));

            migrationBuilder.Sql("""
                DELETE FROM flowbit.retention_policies WHERE "Category" = 'signalBroadcasts';
                DELETE FROM flowbit.engine_settings
                WHERE "Namespace" = 'Signals'
                  AND "Key" IN ('RequiredRole', 'MaxSubscriptionsPerBroadcast', 'MaxOpenBroadcasts');
                """);

            migrationBuilder.DropTable(
                name: "signal_broadcast_jobs",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "signal_deliveries",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "signal_subscriptions",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "signal_broadcasts",
                schema: "flowbit");

            migrationBuilder.DropCheckConstraint(
                name: "CK_node_executions_completion_reason",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_node_executions_completion_reason",
                schema: "flowbit",
                table: "node_executions",
                sql: CompletionReasonCheck(CompletionReasonsBefore));
        }
    }
}
