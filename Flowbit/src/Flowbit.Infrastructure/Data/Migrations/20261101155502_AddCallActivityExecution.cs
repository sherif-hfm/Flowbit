using System;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCallActivityExecution : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_node_executions_completion_reason",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.AddColumn<long>(
                name: "CallInvocationId",
                schema: "flowbit",
                table: "workflow_jobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "CallDepth",
                schema: "flowbit",
                table: "workflow_instances",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "CallRootInstanceId",
                schema: "flowbit",
                table: "workflow_instances",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "call_activity_executions",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    ParentInstanceId = table.Column<long>(type: "bigint", nullable: false),
                    ParentTokenId = table.Column<long>(type: "bigint", nullable: false),
                    ActivationId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParentNodeId = table.Column<int>(type: "integer", nullable: false),
                    ParentNodeExecutionId = table.Column<long>(type: "bigint", nullable: true),
                    RootInstanceId = table.Column<long>(type: "bigint", nullable: false),
                    ChildDepth = table.Column<int>(type: "integer", nullable: false),
                    ParentDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    TargetWorkflowKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    ChildDefinitionId = table.Column<long>(type: "bigint", nullable: true),
                    ChildStartEventId = table.Column<int>(type: "integer", nullable: true),
                    ChildInstanceId = table.Column<long>(type: "bigint", nullable: true),
                    State = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CapacityReserved = table.Column<bool>(type: "boolean", nullable: false, defaultValue: false),
                    ContractJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    InputValuesJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FailureDescription = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ChildFaultCode = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CausedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    AdmittedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SettledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SnapshotsPrunedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_call_activity_executions", x => x.Id);
                    table.CheckConstraint("CK_call_activity_executions_admission", "NOT \"CapacityReserved\" OR \"AdmittedAt\" IS NOT NULL");
                    table.CheckConstraint("CK_call_activity_executions_child_resolution", "(\"ChildInstanceId\" IS NULL AND \"ChildDefinitionId\" IS NULL AND \"ChildStartEventId\" IS NULL) OR (\"ChildInstanceId\" IS NOT NULL AND \"ChildDefinitionId\" IS NOT NULL AND \"ChildStartEventId\" IS NOT NULL)");
                    table.CheckConstraint("CK_call_activity_executions_child_shape", "(\"State\" = 'running' AND \"ChildInstanceId\" IS NOT NULL) OR \"State\" <> 'running'");
                    table.CheckConstraint("CK_call_activity_executions_depth", "\"ChildDepth\" BETWEEN 1 AND 33");
                    table.CheckConstraint("CK_call_activity_executions_reservation", "(\"State\" = 'queued' AND \"ChildInstanceId\" IS NULL) OR (\"State\" = 'running' AND \"CapacityReserved\") OR (\"State\" IN ('completed', 'failed', 'cancelled') AND NOT \"CapacityReserved\")");
                    table.CheckConstraint("CK_call_activity_executions_settlement", "(\"State\" IN ('queued', 'running') AND \"SettledAt\" IS NULL) OR (\"State\" IN ('completed', 'failed', 'cancelled') AND \"SettledAt\" IS NOT NULL)");
                    table.CheckConstraint("CK_call_activity_executions_state", "\"State\" IN ('queued', 'running', 'completed', 'failed', 'cancelled')");
                    table.ForeignKey(
                        name: "FK_call_activity_executions_execution_tokens_ParentTokenId",
                        column: x => x.ParentTokenId,
                        principalSchema: "flowbit",
                        principalTable: "execution_tokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_call_activity_executions_node_executions_ParentNodeExecutio~",
                        column: x => x.ParentNodeExecutionId,
                        principalSchema: "flowbit",
                        principalTable: "node_executions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_call_activity_executions_workflow_definitions_ChildDefiniti~",
                        column: x => x.ChildDefinitionId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_call_activity_executions_workflow_definitions_ParentDefinit~",
                        column: x => x.ParentDefinitionId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_call_activity_executions_workflow_instances_ChildInstanceId",
                        column: x => x.ChildInstanceId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_call_activity_executions_workflow_instances_ParentInstanceId",
                        column: x => x.ParentInstanceId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workflow_definition_call_targets",
                schema: "flowbit",
                columns: table => new
                {
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    NodeId = table.Column<int>(type: "integer", nullable: false),
                    TargetWorkflowKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_definition_call_targets", x => new { x.WorkflowDefinitionId, x.NodeId });
                    table.ForeignKey(
                        name: "FK_workflow_definition_call_targets_workflow_definitions_Workf~",
                        column: x => x.WorkflowDefinitionId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "call_activity_results",
                schema: "flowbit",
                columns: table => new
                {
                    ChildInstanceId = table.Column<long>(type: "bigint", nullable: false),
                    InvocationId = table.Column<long>(type: "bigint", nullable: false),
                    Outcome = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    OutputValuesJson = table.Column<JsonDocument>(type: "jsonb", nullable: true),
                    FailureCode = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    FailureDescription = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ChildFaultCode = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    CapturedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_call_activity_results", x => x.ChildInstanceId);
                    table.CheckConstraint("CK_call_activity_results_outcome", "\"Outcome\" IN ('completed', 'faulted', 'cancelled')");
                    table.ForeignKey(
                        name: "FK_call_activity_results_call_activity_executions_InvocationId",
                        column: x => x.InvocationId,
                        principalSchema: "flowbit",
                        principalTable: "call_activity_executions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_call_activity_results_workflow_instances_ChildInstanceId",
                        column: x => x.ChildInstanceId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_jobs_CallInvocationId_Kind",
                schema: "flowbit",
                table: "workflow_jobs",
                columns: new[] { "CallInvocationId", "Kind" },
                unique: true,
                filter: "\"CallInvocationId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instances_CallRootInstanceId_Status",
                schema: "flowbit",
                table: "workflow_instances",
                columns: new[] { "CallRootInstanceId", "Status" },
                filter: "\"CallRootInstanceId\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_workflow_instances_call_depth",
                schema: "flowbit",
                table: "workflow_instances",
                sql: "\"CallDepth\" BETWEEN 0 AND 32");

            migrationBuilder.AddCheckConstraint(
                name: "CK_workflow_instances_call_shape",
                schema: "flowbit",
                table: "workflow_instances",
                sql: "(\"CallRootInstanceId\" IS NULL AND \"CallDepth\" = 0) OR (\"CallRootInstanceId\" IS NOT NULL AND \"CallDepth\" > 0)");

            migrationBuilder.AddCheckConstraint(
                name: "CK_node_executions_completion_reason",
                schema: "flowbit",
                table: "node_executions",
                sql: "((\"Status\" IN ('pending', 'active') AND \"CompletionReason\" IS NULL) OR (\"Status\" IN ('completed', 'cancelled', 'faulted', 'merged') AND \"CompletionReason\" IN ('normal', 'userAction', 'administrativeAction', 'messageDelivery', 'multiInstanceItem', 'multiInstanceCompleted', 'multiInstanceInterrupt', 'boundaryCaught', 'normalEnd', 'terminateEnd', 'errorEnd', 'instanceCancelled', 'gatewayScopeCancelled', 'gatewayJoinMerged', 'parallelFork', 'parallelJoin', 'inclusiveSplit', 'inclusiveMerge', 'complexActivation', 'complexReset', 'scopedInterrupt', 'scopedInterruptSkipped', 'timerFired', 'conditionalTriggered', 'callCompleted', 'callFailed')))");

            migrationBuilder.CreateIndex(
                name: "IX_call_activity_executions_ChildDefinitionId",
                schema: "flowbit",
                table: "call_activity_executions",
                column: "ChildDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_call_activity_executions_ChildInstanceId",
                schema: "flowbit",
                table: "call_activity_executions",
                column: "ChildInstanceId",
                unique: true,
                filter: "\"ChildInstanceId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_call_activity_executions_ParentDefinitionId",
                schema: "flowbit",
                table: "call_activity_executions",
                column: "ParentDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_call_activity_executions_ParentInstanceId_Id",
                schema: "flowbit",
                table: "call_activity_executions",
                columns: new[] { "ParentInstanceId", "Id" },
                descending: new[] { false, true });

            migrationBuilder.CreateIndex(
                name: "IX_call_activity_executions_ParentInstanceId_ParentTokenId_Act~",
                schema: "flowbit",
                table: "call_activity_executions",
                columns: new[] { "ParentInstanceId", "ParentTokenId", "ActivationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_call_activity_executions_ParentNodeExecutionId",
                schema: "flowbit",
                table: "call_activity_executions",
                column: "ParentNodeExecutionId");

            migrationBuilder.CreateIndex(
                name: "IX_call_activity_executions_ParentTokenId_State",
                schema: "flowbit",
                table: "call_activity_executions",
                columns: new[] { "ParentTokenId", "State" },
                filter: "\"State\" IN ('queued', 'running')");

            migrationBuilder.CreateIndex(
                name: "IX_call_activity_executions_root_reserved",
                schema: "flowbit",
                table: "call_activity_executions",
                column: "RootInstanceId",
                filter: "\"CapacityReserved\" AND \"State\" IN ('queued', 'running')");

            migrationBuilder.CreateIndex(
                name: "IX_call_activity_results_InvocationId",
                schema: "flowbit",
                table: "call_activity_results",
                column: "InvocationId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_workflow_definition_call_targets_TargetWorkflowKey",
                schema: "flowbit",
                table: "workflow_definition_call_targets",
                column: "TargetWorkflowKey");

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_jobs_call_activity_executions_CallInvocationId",
                schema: "flowbit",
                table: "workflow_jobs",
                column: "CallInvocationId",
                principalSchema: "flowbit",
                principalTable: "call_activity_executions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping these tables would silently cut live parents from their
            // children. After use, rollback is a forward repair or a database restore.
            migrationBuilder.Sql(
                """
                DO $$
                BEGIN
                    IF EXISTS (
                           SELECT 1
                           FROM flowbit.call_activity_executions
                           LIMIT 1)
                       OR EXISTS (
                           SELECT 1
                           FROM flowbit.workflow_instances
                           WHERE "CallRootInstanceId" IS NOT NULL
                           LIMIT 1)
                       OR EXISTS (
                           SELECT 1
                           FROM flowbit.workflow_jobs
                           WHERE "Kind" IN ('callStart', 'callResume', 'callCancel')
                           LIMIT 1) THEN
                        RAISE EXCEPTION
                            'Cannot remove call-activity storage while call instances, invocations, or call jobs exist. Repair forward or restore a database backup.';
                    END IF;
                END;
                $$;
                """);

            migrationBuilder.DropForeignKey(
                name: "FK_workflow_jobs_call_activity_executions_CallInvocationId",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.DropTable(
                name: "call_activity_results",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "workflow_definition_call_targets",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "call_activity_executions",
                schema: "flowbit");

            migrationBuilder.DropIndex(
                name: "IX_workflow_jobs_CallInvocationId_Kind",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.DropIndex(
                name: "IX_workflow_instances_CallRootInstanceId_Status",
                schema: "flowbit",
                table: "workflow_instances");

            migrationBuilder.DropCheckConstraint(
                name: "CK_workflow_instances_call_depth",
                schema: "flowbit",
                table: "workflow_instances");

            migrationBuilder.DropCheckConstraint(
                name: "CK_workflow_instances_call_shape",
                schema: "flowbit",
                table: "workflow_instances");

            migrationBuilder.DropCheckConstraint(
                name: "CK_node_executions_completion_reason",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.DropColumn(
                name: "CallInvocationId",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.DropColumn(
                name: "CallDepth",
                schema: "flowbit",
                table: "workflow_instances");

            migrationBuilder.DropColumn(
                name: "CallRootInstanceId",
                schema: "flowbit",
                table: "workflow_instances");

            migrationBuilder.AddCheckConstraint(
                name: "CK_node_executions_completion_reason",
                schema: "flowbit",
                table: "node_executions",
                sql: "((\"Status\" IN ('pending', 'active') AND \"CompletionReason\" IS NULL) OR (\"Status\" IN ('completed', 'cancelled', 'faulted', 'merged') AND \"CompletionReason\" IN ('normal', 'userAction', 'administrativeAction', 'messageDelivery', 'multiInstanceItem', 'multiInstanceCompleted', 'multiInstanceInterrupt', 'boundaryCaught', 'normalEnd', 'terminateEnd', 'errorEnd', 'instanceCancelled', 'gatewayScopeCancelled', 'gatewayJoinMerged', 'parallelFork', 'parallelJoin', 'inclusiveSplit', 'inclusiveMerge', 'complexActivation', 'complexReset', 'scopedInterrupt', 'scopedInterruptSkipped', 'timerFired', 'conditionalTriggered')))");
        }
    }
}
