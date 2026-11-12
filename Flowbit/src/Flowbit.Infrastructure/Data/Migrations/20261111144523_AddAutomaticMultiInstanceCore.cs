using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAutomaticMultiInstanceCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<JsonDocument>(name: "CompletionJson", schema: "flowbit", table: "multi_instance_executions", type: "jsonb", nullable: true);
            migrationBuilder.AddCheckConstraint(name: "CK_multi_instance_executions_completion_size", schema: "flowbit", table: "multi_instance_executions",
                sql: "COALESCE(octet_length(\"CompletionJson\"::text), 0) <= 1048576");
            migrationBuilder.DropIndex(
                name: "IX_workflow_jobs_MultiInstanceExecutionId",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_node_executions_execution_kind",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_node_executions_multi_instance_shape",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_execution_tokens_wait_shape",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropIndex(
                name: "IX_call_activity_executions_ParentInstanceId_ParentTokenId_Act~",
                schema: "flowbit",
                table: "call_activity_executions");

            migrationBuilder.AddColumn<long>(
                name: "AutomaticMultiInstanceItemId",
                schema: "flowbit",
                table: "workflow_jobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MultiInstanceControlGeneration",
                schema: "flowbit",
                table: "workflow_jobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "PublicationFenceRefreshRequested",
                schema: "flowbit",
                table: "workflow_jobs",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<long>(
                name: "AutomaticMultiInstanceItemId",
                schema: "flowbit",
                table: "node_executions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ActivationId",
                schema: "flowbit",
                table: "multi_instance_executions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ActivityKind",
                schema: "flowbit",
                table: "multi_instance_executions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                defaultValue: "userTask");

            migrationBuilder.AddColumn<string>(
                name: "AdmissionBlockedReason",
                schema: "flowbit",
                table: "multi_instance_executions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AggregateScope",
                schema: "flowbit",
                table: "multi_instance_executions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AggregateSharedKey",
                schema: "flowbit",
                table: "multi_instance_executions",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AggregateValueVersion",
                schema: "flowbit",
                table: "multi_instance_executions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ControlGeneration",
                schema: "flowbit",
                table: "multi_instance_executions",
                type: "bigint",
                nullable: false,
                defaultValue: 0L);

            migrationBuilder.AddColumn<int>(
                name: "EffectiveConcurrency",
                schema: "flowbit",
                table: "multi_instance_executions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "FailedCount",
                schema: "flowbit",
                table: "multi_instance_executions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "InputSnapshotId",
                schema: "flowbit",
                table: "multi_instance_executions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MaxInstances",
                schema: "flowbit",
                table: "multi_instance_executions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Phase",
                schema: "flowbit",
                table: "multi_instance_executions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "PinState",
                schema: "flowbit",
                table: "multi_instance_executions",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "PinnedChildDefinitionId",
                schema: "flowbit",
                table: "multi_instance_executions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PinnedChildStartEventId",
                schema: "flowbit",
                table: "multi_instance_executions",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ResultBytes",
                schema: "flowbit",
                table: "multi_instance_executions",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "WorkflowDefinitionId",
                schema: "flowbit",
                table: "multi_instance_executions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "WaitingMultiInstanceExecutionId",
                schema: "flowbit",
                table: "execution_tokens",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "AutomaticMultiInstanceItemId",
                schema: "flowbit",
                table: "call_activity_executions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "MultiInstanceExecutionId",
                schema: "flowbit",
                table: "call_activity_executions",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddUniqueConstraint(
                name: "AK_multi_instance_executions_Id_InstanceId",
                schema: "flowbit",
                table: "multi_instance_executions",
                columns: new[] { "Id", "InstanceId" });

            migrationBuilder.CreateTable(
                name: "multi_instance_activity_items",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    MultiInstanceExecutionId = table.Column<long>(type: "bigint", nullable: false),
                    ItemIndex = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Phase = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    StagedInputJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    CandidateJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    ResultJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    SharedWriteCorrelationsJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    ErrorCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    ErrorDescription = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_multi_instance_activity_items", x => x.Id);
                    table.UniqueConstraint("AK_multi_instance_activity_items_Id_MultiInstanceExecutionId_I~", x => new { x.Id, x.MultiInstanceExecutionId, x.InstanceId });
                    table.CheckConstraint("CK_multi_instance_activity_items_index", "\"ItemIndex\" >= 0");
                    table.CheckConstraint("CK_multi_instance_activity_items_phase", "\"Phase\" IN ('execution', 'publication', 'settled')");
                    table.CheckConstraint("CK_multi_instance_activity_items_size", "COALESCE(octet_length(\"StagedInputJson\"::text), 0) + COALESCE(octet_length(\"CandidateJson\"::text), 0) <= 1048576 AND COALESCE(octet_length(\"ResultJson\"::text), 0) <= 1048576");
                    table.CheckConstraint("CK_multi_instance_activity_items_status", "\"Status\" IN ('pending', 'active', 'completed', 'failed', 'cancelled')");
                    table.CheckConstraint("CK_multi_instance_activity_items_timestamps", "(\"Status\" = 'pending' AND \"StartedAt\" IS NULL AND \"CompletedAt\" IS NULL) OR (\"Status\" = 'active' AND \"StartedAt\" IS NOT NULL AND \"CompletedAt\" IS NULL) OR (\"Status\" IN ('completed', 'failed', 'cancelled') AND \"CompletedAt\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_multi_instance_activity_items_multi_instance_executions_Mul~",
                        columns: x => new { x.MultiInstanceExecutionId, x.InstanceId },
                        principalSchema: "flowbit",
                        principalTable: "multi_instance_executions",
                        principalColumns: new[] { "Id", "InstanceId" },
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_jobs_automatic_control_slot",
                schema: "flowbit",
                table: "workflow_jobs",
                column: "MultiInstanceExecutionId",
                unique: true,
                filter: "\"Kind\" IN ('multiInstanceDispatch', 'multiInstanceComplete') AND \"Status\" NOT IN ('completed', 'cancelled', 'skipped')");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_jobs_AutomaticMultiInstanceItemId",
                schema: "flowbit",
                table: "workflow_jobs",
                column: "AutomaticMultiInstanceItemId",
                unique: true,
                filter: "\"AutomaticMultiInstanceItemId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_jobs_AutomaticMultiInstanceItemId_MultiInstanceExe~",
                schema: "flowbit",
                table: "workflow_jobs",
                columns: new[] { "AutomaticMultiInstanceItemId", "MultiInstanceExecutionId", "InstanceId" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_jobs_MultiInstanceExecutionId_MultiInstanceControl~",
                schema: "flowbit",
                table: "workflow_jobs",
                columns: new[] { "MultiInstanceExecutionId", "MultiInstanceControlGeneration" },
                unique: true,
                filter: "\"MultiInstanceControlGeneration\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_workflow_jobs_automatic_control_shape",
                schema: "flowbit",
                table: "workflow_jobs",
                sql: "(\"Kind\" NOT IN ('multiInstanceDispatch', 'multiInstanceComplete') AND \"MultiInstanceControlGeneration\" IS NULL) OR (\"Kind\" IN ('multiInstanceDispatch', 'multiInstanceComplete') AND \"MultiInstanceControlGeneration\" IS NOT NULL AND \"MultiInstanceControlGeneration\" >= 0 AND \"MultiInstanceExecutionId\" IS NOT NULL AND \"InstanceId\" IS NOT NULL AND \"TokenId\" IS NOT NULL AND \"QueueClass\" = 'control' AND \"UserTaskId\" IS NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_workflow_jobs_automatic_item_shape",
                schema: "flowbit",
                table: "workflow_jobs",
                sql: "(\"AutomaticMultiInstanceItemId\" IS NULL AND \"Kind\" <> 'multiInstanceItem') OR (\"AutomaticMultiInstanceItemId\" IS NOT NULL AND \"Kind\" = 'multiInstanceItem' AND \"MultiInstanceExecutionId\" IS NOT NULL AND \"InstanceId\" IS NOT NULL AND \"TokenId\" IS NOT NULL AND \"UserTaskId\" IS NULL AND \"QueueClass\" = 'activity')");

            migrationBuilder.CreateIndex(
                name: "IX_node_executions_AutomaticMultiInstanceItemId",
                schema: "flowbit",
                table: "node_executions",
                column: "AutomaticMultiInstanceItemId",
                unique: true,
                filter: "\"AutomaticMultiInstanceItemId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_node_executions_AutomaticMultiInstanceItemId_MultiInstanceE~",
                schema: "flowbit",
                table: "node_executions",
                columns: new[] { "AutomaticMultiInstanceItemId", "MultiInstanceExecutionId", "InstanceId" });

            migrationBuilder.AddCheckConstraint(
                name: "CK_node_executions_execution_kind",
                schema: "flowbit",
                table: "node_executions",
                sql: "\"ExecutionKind\" IN ('node', 'userTaskItem', 'automaticActivityItem')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_node_executions_multi_instance_shape",
                schema: "flowbit",
                table: "node_executions",
                sql: "(\"ExecutionKind\" = 'node' AND \"MultiInstanceExecutionId\" IS NULL AND \"ItemIndex\" IS NULL AND \"AutomaticMultiInstanceItemId\" IS NULL) OR (\"ExecutionKind\" = 'userTaskItem' AND \"UserTaskId\" IS NOT NULL AND \"MultiInstanceExecutionId\" IS NOT NULL AND \"ItemIndex\" IS NOT NULL AND \"AutomaticMultiInstanceItemId\" IS NULL) OR (\"ExecutionKind\" = 'automaticActivityItem' AND \"UserTaskId\" IS NULL AND \"MultiInstanceExecutionId\" IS NOT NULL AND \"ItemIndex\" IS NOT NULL AND \"AutomaticMultiInstanceItemId\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_multi_instance_executions_InputSnapshotId",
                schema: "flowbit",
                table: "multi_instance_executions",
                column: "InputSnapshotId");

            migrationBuilder.CreateIndex(
                name: "IX_multi_instance_executions_open_automatic_node",
                schema: "flowbit",
                table: "multi_instance_executions",
                columns: new[] { "InstanceId", "NodeId" },
                unique: true,
                filter: "\"ActivityKind\" <> 'userTask' AND \"Status\" = 'active'");

            migrationBuilder.CreateIndex(
                name: "IX_multi_instance_executions_PinnedChildDefinitionId",
                schema: "flowbit",
                table: "multi_instance_executions",
                column: "PinnedChildDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_multi_instance_executions_TokenId_ActivationId",
                schema: "flowbit",
                table: "multi_instance_executions",
                columns: new[] { "TokenId", "ActivationId" },
                unique: true,
                filter: "\"ActivityKind\" <> 'userTask'");

            migrationBuilder.CreateIndex(
                name: "IX_multi_instance_executions_WorkflowDefinitionId",
                schema: "flowbit",
                table: "multi_instance_executions",
                column: "WorkflowDefinitionId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_multi_instance_executions_activity_kind",
                schema: "flowbit",
                table: "multi_instance_executions",
                sql: "\"ActivityKind\" IN ('userTask', 'serviceTask', 'scriptTask', 'callActivity')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_multi_instance_executions_automatic_counts",
                schema: "flowbit",
                table: "multi_instance_executions",
                sql: "\"FailedCount\" >= 0 AND \"ResultBytes\" BETWEEN 0 AND 1048576 AND \"ControlGeneration\" >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "CK_multi_instance_executions_automatic_shape",
                schema: "flowbit",
                table: "multi_instance_executions",
                sql: "\"ActivityKind\" = 'userTask' OR (\"WorkflowDefinitionId\" IS NOT NULL AND \"ActivationId\" IS NOT NULL AND \"InputSnapshotId\" IS NOT NULL AND \"AggregateScope\" IS NOT NULL AND \"AggregateScope\" IN ('instance', 'shared') AND \"AggregateValueVersion\" IS NOT NULL AND \"EffectiveConcurrency\" IS NOT NULL AND \"EffectiveConcurrency\" BETWEEN 0 AND 1000 AND \"MaxInstances\" IS NOT NULL AND \"MaxInstances\" >= 1 AND \"TotalCount\" BETWEEN 0 AND \"MaxInstances\" AND \"RolePolicyId\" IS NULL AND NOT \"OnePerActor\" AND \"Phase\" IS NOT NULL AND \"Phase\" IN ('running', 'dispatch', 'completing', 'errorCompletion', 'settled') AND ((\"AggregateScope\" = 'instance' AND \"AggregateSharedKey\" IS NULL) OR (\"AggregateScope\" = 'shared' AND \"AggregateSharedKey\" IS NOT NULL)))");

            migrationBuilder.CreateIndex(
                name: "IX_execution_tokens_WaitingMultiInstanceExecutionId",
                schema: "flowbit",
                table: "execution_tokens",
                column: "WaitingMultiInstanceExecutionId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_execution_tokens_wait_shape",
                schema: "flowbit",
                table: "execution_tokens",
                sql: "(\"WaitState\" IS NULL AND \"WaitingJobId\" IS NULL AND \"WaitingTimerSubscriptionId\" IS NULL AND \"WaitingMultiInstanceExecutionId\" IS NULL) OR (\"WaitState\" IS NOT NULL AND (\"WaitingJobId\" IS NOT NULL OR \"WaitingTimerSubscriptionId\" IS NOT NULL OR (\"WaitState\" = 'automaticMultiInstance' AND \"WaitingMultiInstanceExecutionId\" IS NOT NULL)))");

            migrationBuilder.CreateIndex(
                name: "IX_call_activity_executions_AutomaticMultiInstanceItemId",
                schema: "flowbit",
                table: "call_activity_executions",
                column: "AutomaticMultiInstanceItemId",
                unique: true,
                filter: "\"AutomaticMultiInstanceItemId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_call_activity_executions_AutomaticMultiInstanceItemId_Multi~",
                schema: "flowbit",
                table: "call_activity_executions",
                columns: new[] { "AutomaticMultiInstanceItemId", "MultiInstanceExecutionId", "ParentInstanceId" });

            migrationBuilder.CreateIndex(
                name: "IX_call_activity_executions_ParentInstanceId_ParentTokenId_Act~",
                schema: "flowbit",
                table: "call_activity_executions",
                columns: new[] { "ParentInstanceId", "ParentTokenId", "ActivationId" },
                unique: true,
                filter: "\"AutomaticMultiInstanceItemId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_multi_instance_activity_items_MultiInstanceExecutionId_Inst~",
                schema: "flowbit",
                table: "multi_instance_activity_items",
                columns: new[] { "MultiInstanceExecutionId", "InstanceId" });

            migrationBuilder.CreateIndex(
                name: "IX_multi_instance_activity_items_MultiInstanceExecutionId_Item~",
                schema: "flowbit",
                table: "multi_instance_activity_items",
                columns: new[] { "MultiInstanceExecutionId", "ItemIndex" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_multi_instance_activity_items_MultiInstanceExecutionId_Stat~",
                schema: "flowbit",
                table: "multi_instance_activity_items",
                columns: new[] { "MultiInstanceExecutionId", "Status", "ItemIndex" });

            migrationBuilder.AddForeignKey(
                name: "FK_call_activity_executions_multi_instance_activity_items_Auto~",
                schema: "flowbit",
                table: "call_activity_executions",
                columns: new[] { "AutomaticMultiInstanceItemId", "MultiInstanceExecutionId", "ParentInstanceId" },
                principalSchema: "flowbit",
                principalTable: "multi_instance_activity_items",
                principalColumns: new[] { "Id", "MultiInstanceExecutionId", "InstanceId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_execution_tokens_multi_instance_executions_WaitingMultiInst~",
                schema: "flowbit",
                table: "execution_tokens",
                column: "WaitingMultiInstanceExecutionId",
                principalSchema: "flowbit",
                principalTable: "multi_instance_executions",
                principalColumn: "Id");

            migrationBuilder.AddForeignKey(
                name: "FK_multi_instance_executions_workflow_definitions_PinnedChildD~",
                schema: "flowbit",
                table: "multi_instance_executions",
                column: "PinnedChildDefinitionId",
                principalSchema: "flowbit",
                principalTable: "workflow_definitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_multi_instance_executions_workflow_definitions_WorkflowDefi~",
                schema: "flowbit",
                table: "multi_instance_executions",
                column: "WorkflowDefinitionId",
                principalSchema: "flowbit",
                principalTable: "workflow_definitions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_multi_instance_executions_workflow_job_snapshots_InputSnaps~",
                schema: "flowbit",
                table: "multi_instance_executions",
                column: "InputSnapshotId",
                principalSchema: "flowbit",
                principalTable: "workflow_job_snapshots",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_node_executions_multi_instance_activity_items_AutomaticMult~",
                schema: "flowbit",
                table: "node_executions",
                columns: new[] { "AutomaticMultiInstanceItemId", "MultiInstanceExecutionId", "InstanceId" },
                principalSchema: "flowbit",
                principalTable: "multi_instance_activity_items",
                principalColumns: new[] { "Id", "MultiInstanceExecutionId", "InstanceId" },
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_jobs_multi_instance_activity_items_AutomaticMultiI~",
                schema: "flowbit",
                table: "workflow_jobs",
                columns: new[] { "AutomaticMultiInstanceItemId", "MultiInstanceExecutionId", "InstanceId" },
                principalSchema: "flowbit",
                principalTable: "multi_instance_activity_items",
                principalColumns: new[] { "Id", "MultiInstanceExecutionId", "InstanceId" },
                onDelete: ReferentialAction.Restrict);
            // Keep the schema transaction short on the two large durable-work tables.
            // A separate resumable migration performs the existing-row scans.
            foreach (var check in migrationBuilder.Operations.OfType<AddCheckConstraintOperation>()
                .Where(check => check.Table is "workflow_jobs" or "node_executions").ToArray())
            {
                migrationBuilder.Operations.Remove(check);
                migrationBuilder.Sql($"ALTER TABLE flowbit.\"{check.Table}\" ADD CONSTRAINT \"{check.Name}\" CHECK ({check.Sql}) NOT VALID;");
            }
            migrationBuilder.Sql("""
                WITH defaults("Key", "Value", "Description") AS (
                    VALUES ('DefaultConcurrency', '4', 'Default admitted parallel automatic multi-instance items; effective range 1-1000.'),
                           ('MaxConcurrency', '1000', 'Cap on admitted automatic multi-instance items; separate from Worker concurrency.')
                )
                INSERT INTO flowbit.engine_settings ("Namespace", "Key", "Value", "Description", "CreatedAt", "UpdatedAt")
                SELECT 'Workflow.MultiInstance', defaults."Key", defaults."Value", defaults."Description", now(), now()
                FROM defaults WHERE NOT EXISTS (
                    SELECT 1 FROM flowbit.engine_settings existing WHERE
                    (existing."Namespace" = 'Workflow.MultiInstance' AND existing."Key" = defaults."Key") OR
                    (COALESCE(existing."Namespace", '') = '' AND existing."Key" = 'Workflow.MultiInstance.' || defaults."Key")
                ) ON CONFLICT ("Namespace", "Key") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM flowbit.multi_instance_executions WHERE "ActivityKind" <> 'userTask') THEN
                        RAISE EXCEPTION 'Cannot remove automatic multi-instance schema while automatic execution history exists.' USING ERRCODE = '55000';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropCheckConstraint(name: "CK_multi_instance_executions_completion_size", schema: "flowbit", table: "multi_instance_executions");
            migrationBuilder.DropColumn(name: "CompletionJson", schema: "flowbit", table: "multi_instance_executions");
            migrationBuilder.DropForeignKey(
                name: "FK_call_activity_executions_multi_instance_activity_items_Auto~",
                schema: "flowbit",
                table: "call_activity_executions");

            migrationBuilder.DropForeignKey(
                name: "FK_execution_tokens_multi_instance_executions_WaitingMultiInst~",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropForeignKey(
                name: "FK_multi_instance_executions_workflow_definitions_PinnedChildD~",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropForeignKey(
                name: "FK_multi_instance_executions_workflow_definitions_WorkflowDefi~",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropForeignKey(
                name: "FK_multi_instance_executions_workflow_job_snapshots_InputSnaps~",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropForeignKey(
                name: "FK_node_executions_multi_instance_activity_items_AutomaticMult~",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.DropForeignKey(
                name: "FK_workflow_jobs_multi_instance_activity_items_AutomaticMultiI~",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.DropTable(
                name: "multi_instance_activity_items",
                schema: "flowbit");

            migrationBuilder.DropIndex(
                name: "IX_workflow_jobs_automatic_control_slot",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.DropIndex(
                name: "IX_workflow_jobs_AutomaticMultiInstanceItemId",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.DropIndex(
                name: "IX_workflow_jobs_AutomaticMultiInstanceItemId_MultiInstanceExe~",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.DropIndex(
                name: "IX_workflow_jobs_MultiInstanceExecutionId_MultiInstanceControl~",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_workflow_jobs_automatic_control_shape",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_workflow_jobs_automatic_item_shape",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.DropIndex(
                name: "IX_node_executions_AutomaticMultiInstanceItemId",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.DropIndex(
                name: "IX_node_executions_AutomaticMultiInstanceItemId_MultiInstanceE~",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_node_executions_execution_kind",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_node_executions_multi_instance_shape",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.DropUniqueConstraint(
                name: "AK_multi_instance_executions_Id_InstanceId",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropIndex(
                name: "IX_multi_instance_executions_InputSnapshotId",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropIndex(
                name: "IX_multi_instance_executions_open_automatic_node",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropIndex(
                name: "IX_multi_instance_executions_PinnedChildDefinitionId",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropIndex(
                name: "IX_multi_instance_executions_TokenId_ActivationId",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropIndex(
                name: "IX_multi_instance_executions_WorkflowDefinitionId",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_multi_instance_executions_activity_kind",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_multi_instance_executions_automatic_counts",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_multi_instance_executions_automatic_shape",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropIndex(
                name: "IX_execution_tokens_WaitingMultiInstanceExecutionId",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropCheckConstraint(
                name: "CK_execution_tokens_wait_shape",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropIndex(
                name: "IX_call_activity_executions_AutomaticMultiInstanceItemId",
                schema: "flowbit",
                table: "call_activity_executions");

            migrationBuilder.DropIndex(
                name: "IX_call_activity_executions_AutomaticMultiInstanceItemId_Multi~",
                schema: "flowbit",
                table: "call_activity_executions");

            migrationBuilder.DropIndex(
                name: "IX_call_activity_executions_ParentInstanceId_ParentTokenId_Act~",
                schema: "flowbit",
                table: "call_activity_executions");

            migrationBuilder.DropColumn(
                name: "AutomaticMultiInstanceItemId",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.DropColumn(
                name: "MultiInstanceControlGeneration",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.DropColumn(
                name: "PublicationFenceRefreshRequested",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.DropColumn(
                name: "AutomaticMultiInstanceItemId",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.DropColumn(
                name: "ActivationId",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropColumn(
                name: "ActivityKind",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropColumn(
                name: "AdmissionBlockedReason",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropColumn(
                name: "AggregateScope",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropColumn(
                name: "AggregateSharedKey",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropColumn(
                name: "AggregateValueVersion",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropColumn(
                name: "ControlGeneration",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropColumn(
                name: "EffectiveConcurrency",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropColumn(
                name: "FailedCount",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropColumn(
                name: "InputSnapshotId",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropColumn(
                name: "MaxInstances",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropColumn(
                name: "Phase",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropColumn(
                name: "PinState",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropColumn(
                name: "PinnedChildDefinitionId",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropColumn(
                name: "PinnedChildStartEventId",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropColumn(
                name: "ResultBytes",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropColumn(
                name: "WorkflowDefinitionId",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropColumn(
                name: "WaitingMultiInstanceExecutionId",
                schema: "flowbit",
                table: "execution_tokens");

            migrationBuilder.DropColumn(
                name: "AutomaticMultiInstanceItemId",
                schema: "flowbit",
                table: "call_activity_executions");

            migrationBuilder.DropColumn(
                name: "MultiInstanceExecutionId",
                schema: "flowbit",
                table: "call_activity_executions");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_jobs_MultiInstanceExecutionId",
                schema: "flowbit",
                table: "workflow_jobs",
                column: "MultiInstanceExecutionId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_node_executions_execution_kind",
                schema: "flowbit",
                table: "node_executions",
                sql: "\"ExecutionKind\" IN ('node', 'userTaskItem')");

            migrationBuilder.AddCheckConstraint(
                name: "CK_node_executions_multi_instance_shape",
                schema: "flowbit",
                table: "node_executions",
                sql: "(\"ExecutionKind\" = 'node' AND \"MultiInstanceExecutionId\" IS NULL AND \"ItemIndex\" IS NULL) OR (\"ExecutionKind\" = 'userTaskItem' AND \"UserTaskId\" IS NOT NULL AND \"MultiInstanceExecutionId\" IS NOT NULL AND \"ItemIndex\" IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_execution_tokens_wait_shape",
                schema: "flowbit",
                table: "execution_tokens",
                sql: "(\"WaitState\" IS NULL AND \"WaitingJobId\" IS NULL AND \"WaitingTimerSubscriptionId\" IS NULL) OR (\"WaitState\" IS NOT NULL AND (\"WaitingJobId\" IS NOT NULL OR \"WaitingTimerSubscriptionId\" IS NOT NULL))");

            migrationBuilder.CreateIndex(
                name: "IX_call_activity_executions_ParentInstanceId_ParentTokenId_Act~",
                schema: "flowbit",
                table: "call_activity_executions",
                columns: new[] { "ParentInstanceId", "ParentTokenId", "ActivationId" },
                unique: true);
        }
    }
}
