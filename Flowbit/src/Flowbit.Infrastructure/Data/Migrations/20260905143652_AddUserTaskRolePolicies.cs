using System;
using System.Collections.Generic;
using System.Text.Json;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddUserTaskRolePolicies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "RolePolicyId",
                schema: "flowbit",
                table: "user_tasks",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "RolePolicyId",
                schema: "flowbit",
                table: "multi_instance_executions",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "user_task_role_policies",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    NodeId = table.Column<int>(type: "integer", nullable: false),
                    Roles = table.Column<List<string>>(type: "text[]", nullable: false),
                    OutgoingFlowRolesJson = table.Column<JsonDocument>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_user_task_role_policies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_user_task_role_policies_workflow_definitions_WorkflowDefini~",
                        column: x => x.WorkflowDefinitionId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_user_task_role_policies_workflow_instances_InstanceId",
                        column: x => x.InstanceId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_user_tasks_RolePolicyId",
                schema: "flowbit",
                table: "user_tasks",
                column: "RolePolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_multi_instance_executions_RolePolicyId",
                schema: "flowbit",
                table: "multi_instance_executions",
                column: "RolePolicyId");

            migrationBuilder.CreateIndex(
                name: "IX_user_task_role_policies_InstanceId",
                schema: "flowbit",
                table: "user_task_role_policies",
                column: "InstanceId");

            migrationBuilder.CreateIndex(
                name: "IX_user_task_role_policies_WorkflowDefinitionId",
                schema: "flowbit",
                table: "user_task_role_policies",
                column: "WorkflowDefinitionId");

            migrationBuilder.AddForeignKey(
                name: "FK_multi_instance_executions_user_task_role_policies_RolePolic~",
                schema: "flowbit",
                table: "multi_instance_executions",
                column: "RolePolicyId",
                principalSchema: "flowbit",
                principalTable: "user_task_role_policies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_user_tasks_user_task_role_policies_RolePolicyId",
                schema: "flowbit",
                table: "user_tasks",
                column: "RolePolicyId",
                principalSchema: "flowbit",
                principalTable: "user_task_role_policies",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            // Freeze only open legacy work. Completed visits retain their original
            // history; no authorization history is fabricated for the cutover.
            migrationBuilder.Sql("""
                CREATE TEMP TABLE flowbit_role_policy_backfill ON COMMIT DROP AS
                WITH owners AS (
                    SELECT 'task'::text AS owner_kind, ut."Id" AS owner_id,
                           ut."InstanceId" AS instance_id, ut."NodeId" AS node_id,
                           ut."Roles" AS roles
                    FROM flowbit.user_tasks ut
                    WHERE ut."MultiInstanceExecutionId" IS NULL
                      AND ut."Status" IN ('active', 'pending')
                    UNION ALL
                    SELECT 'mi', mi."Id", mi."InstanceId", mi."NodeId",
                           ARRAY(SELECT jsonb_array_elements_text(
                               CASE WHEN jsonb_typeof(node -> 'roles') = 'array'
                                    THEN node -> 'roles' ELSE '[]'::jsonb END))
                    FROM flowbit.multi_instance_executions mi
                    JOIN flowbit.workflow_instances instance ON instance."Id" = mi."InstanceId"
                    JOIN flowbit.workflow_definitions definition ON definition."Id" = instance."WorkflowDefinitionId"
                    CROSS JOIN LATERAL jsonb_array_elements(definition."Definition" -> 'flowNodes') node
                    WHERE mi."Status" = 'active' AND node ->> 'id' = mi."NodeId"::text
                )
                SELECT nextval(pg_get_serial_sequence('flowbit.user_task_role_policies', 'Id')) AS policy_id,
                       owners.*, instance."WorkflowDefinitionId" AS definition_id,
                       COALESCE((
                           SELECT jsonb_object_agg(flow ->> 'id',
                               CASE WHEN jsonb_typeof(flow -> 'roles') = 'array'
                                    THEN flow -> 'roles' ELSE '[]'::jsonb END)
                           FROM jsonb_array_elements(definition."Definition" -> 'sequenceFlows') flow
                           WHERE flow ->> 'sourceRef' = owners.node_id::text
                             AND COALESCE(flow ->> 'isSelectable', 'true') <> 'false'
                             AND COALESCE(flow ->> 'isDefault', 'false') <> 'true'
                       ), '{}'::jsonb) AS flow_roles
                FROM owners
                JOIN flowbit.workflow_instances instance ON instance."Id" = owners.instance_id
                JOIN flowbit.workflow_definitions definition ON definition."Id" = instance."WorkflowDefinitionId";

                INSERT INTO flowbit.user_task_role_policies
                    ("Id", "InstanceId", "WorkflowDefinitionId", "NodeId", "Roles", "OutgoingFlowRolesJson")
                SELECT policy_id, instance_id, definition_id, node_id, roles, flow_roles
                FROM flowbit_role_policy_backfill;

                UPDATE flowbit.user_tasks task SET "RolePolicyId" = source.policy_id
                FROM flowbit_role_policy_backfill source
                WHERE source.owner_kind = 'task' AND source.owner_id = task."Id";

                UPDATE flowbit.multi_instance_executions execution SET "RolePolicyId" = source.policy_id
                FROM flowbit_role_policy_backfill source
                WHERE source.owner_kind = 'mi' AND source.owner_id = execution."Id";

                UPDATE flowbit.user_tasks task
                SET "RolePolicyId" = source.policy_id, "Roles" = source.roles
                FROM flowbit_role_policy_backfill source
                WHERE source.owner_kind = 'mi' AND source.owner_id = task."MultiInstanceExecutionId"
                  AND task."Status" IN ('active', 'pending');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_multi_instance_executions_user_task_role_policies_RolePolic~",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropForeignKey(
                name: "FK_user_tasks_user_task_role_policies_RolePolicyId",
                schema: "flowbit",
                table: "user_tasks");

            migrationBuilder.DropTable(
                name: "user_task_role_policies",
                schema: "flowbit");

            migrationBuilder.DropIndex(
                name: "IX_user_tasks_RolePolicyId",
                schema: "flowbit",
                table: "user_tasks");

            migrationBuilder.DropIndex(
                name: "IX_multi_instance_executions_RolePolicyId",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropColumn(
                name: "RolePolicyId",
                schema: "flowbit",
                table: "user_tasks");

            migrationBuilder.DropColumn(
                name: "RolePolicyId",
                schema: "flowbit",
                table: "multi_instance_executions");
        }
    }
}
