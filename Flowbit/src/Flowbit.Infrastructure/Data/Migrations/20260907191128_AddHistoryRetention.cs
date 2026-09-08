using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddHistoryRetention : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "FinishedAt",
                schema: "flowbit",
                table: "workflow_instances",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HistoryPrunedAt",
                schema: "flowbit",
                table: "workflow_instances",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "HistoryPrunedAt",
                schema: "flowbit",
                table: "shared_variables",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "retention_coordinator",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<int>(type: "integer", nullable: false),
                    CurrentRunJson = table.Column<string>(type: "jsonb", nullable: true),
                    LastRunJson = table.Column<string>(type: "jsonb", nullable: true),
                    NextScheduledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    WorkerLastSeenAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseOwner = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    LeaseGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_retention_coordinator", x => x.Id);
                    table.CheckConstraint("CK_retention_coordinator_singleton", "\"Id\" = 1");
                });

            migrationBuilder.CreateTable(
                name: "retention_policies",
                schema: "flowbit",
                columns: table => new
                {
                    Category = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    RetentionDays = table.Column<int>(type: "integer", nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    IsInitialized = table.Column<bool>(type: "boolean", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_retention_policies", x => x.Category);
                    table.CheckConstraint("CK_retention_policies_days", "\"RetentionDays\" IS NULL OR \"RetentionDays\" BETWEEN 1 AND 36500");
                    table.CheckConstraint("CK_retention_policies_revision", "\"Revision\" > 0");
                });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instances_FinishedAt_Id",
                schema: "flowbit",
                table: "workflow_instances",
                columns: new[] { "FinishedAt", "Id" },
                filter: "\"FinishedAt\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_workflow_instances_retention_terminal",
                schema: "flowbit",
                table: "workflow_instances",
                sql: "\"HistoryPrunedAt\" IS NULL OR \"Status\" IN ('completed','cancelled','faulted')");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_instance_version_changes_InstanceId_Id",
                schema: "flowbit",
                table: "workflow_instance_version_changes",
                columns: new[] { "InstanceId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_revisions_CreatedAt_Id",
                schema: "flowbit",
                table: "shared_variable_revisions",
                columns: new[] { "CreatedAt", "Id" },
                filter: "\"InstanceId\" IS NULL");

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_revisions_InstanceId_Id",
                schema: "flowbit",
                table: "shared_variable_revisions",
                columns: new[] { "InstanceId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_sequence_flow_occurrences_InstanceId_Id",
                schema: "flowbit",
                table: "sequence_flow_occurrences",
                columns: new[] { "InstanceId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_node_executions_InstanceId_Id",
                schema: "flowbit",
                table: "node_executions",
                columns: new[] { "InstanceId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_instance_variables_InstanceId_Id",
                schema: "flowbit",
                table: "instance_variables",
                columns: new[] { "InstanceId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_instance_variable_updates_InstanceId_Id",
                schema: "flowbit",
                table: "instance_variable_updates",
                columns: new[] { "InstanceId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_instance_history_InstanceId_Id",
                schema: "flowbit",
                table: "instance_history",
                columns: new[] { "InstanceId", "Id" });

            // Existing terminal instances did not carry a finish timestamp.
            // Their last update is the conservative available baseline; running
            // instances remain unset until their next committed terminal change.
            migrationBuilder.Sql("""
                UPDATE flowbit.workflow_instances SET "FinishedAt"="UpdatedAt"
                WHERE "Status" IN ('completed','cancelled','faulted');

                INSERT INTO flowbit.retention_policies
                    ("Category","RetentionDays","Revision","UpdatedAt","UpdatedBy","IsInitialized")
                VALUES
                    ('workflowHistory',NULL,1,now(),NULL,TRUE),
                    ('variableHistory',NULL,1,now(),NULL,TRUE),
                    ('nodeActivity',NULL,1,now(),NULL,TRUE),
                    ('administrativeAudits',NULL,1,now(),NULL,TRUE),
                    ('sharedVariableHistory',NULL,1,now(),NULL,TRUE),
                    ('completedJobs',NULL,1,now(),NULL,FALSE),
                    ('resolvedIncidents',NULL,1,now(),NULL,FALSE)
                ON CONFLICT ("Category") DO NOTHING;

                INSERT INTO flowbit.retention_coordinator ("Id","NextScheduledAt","LeaseGeneration")
                VALUES (1,now(),0) ON CONFLICT ("Id") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (SELECT 1 FROM flowbit.workflow_instances WHERE "HistoryPrunedAt" IS NOT NULL)
                       OR EXISTS (SELECT 1 FROM flowbit.shared_variables WHERE "HistoryPrunedAt" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Cannot downgrade retention after history has been pruned. Restore a complete database backup; dropping retention markers cannot restore deleted history or safely permit legacy reactivation.';
                    END IF;
                END $$;
                """);
            migrationBuilder.DropTable(
                name: "retention_coordinator",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "retention_policies",
                schema: "flowbit");

            migrationBuilder.DropIndex(
                name: "IX_workflow_instances_FinishedAt_Id",
                schema: "flowbit",
                table: "workflow_instances");

            migrationBuilder.DropCheckConstraint(
                name: "CK_workflow_instances_retention_terminal",
                schema: "flowbit",
                table: "workflow_instances");

            migrationBuilder.DropIndex(
                name: "IX_workflow_instance_version_changes_InstanceId_Id",
                schema: "flowbit",
                table: "workflow_instance_version_changes");

            migrationBuilder.DropIndex(
                name: "IX_shared_variable_revisions_CreatedAt_Id",
                schema: "flowbit",
                table: "shared_variable_revisions");

            migrationBuilder.DropIndex(
                name: "IX_shared_variable_revisions_InstanceId_Id",
                schema: "flowbit",
                table: "shared_variable_revisions");

            migrationBuilder.DropIndex(
                name: "IX_sequence_flow_occurrences_InstanceId_Id",
                schema: "flowbit",
                table: "sequence_flow_occurrences");

            migrationBuilder.DropIndex(
                name: "IX_node_executions_InstanceId_Id",
                schema: "flowbit",
                table: "node_executions");

            migrationBuilder.DropIndex(
                name: "IX_instance_variables_InstanceId_Id",
                schema: "flowbit",
                table: "instance_variables");

            migrationBuilder.DropIndex(
                name: "IX_instance_variable_updates_InstanceId_Id",
                schema: "flowbit",
                table: "instance_variable_updates");

            migrationBuilder.DropIndex(
                name: "IX_instance_history_InstanceId_Id",
                schema: "flowbit",
                table: "instance_history");

            migrationBuilder.DropColumn(
                name: "FinishedAt",
                schema: "flowbit",
                table: "workflow_instances");

            migrationBuilder.DropColumn(
                name: "HistoryPrunedAt",
                schema: "flowbit",
                table: "workflow_instances");

            migrationBuilder.DropColumn(
                name: "HistoryPrunedAt",
                schema: "flowbit",
                table: "shared_variables");
        }
    }
}
