using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class HardenAutomaticMultiInstanceCore : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_workflow_jobs_MultiInstanceExecutionId",
                schema: "flowbit",
                table: "workflow_jobs",
                column: "MultiInstanceExecutionId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_multi_instance_executions_call_pin_shape",
                schema: "flowbit",
                table: "multi_instance_executions",
                sql: "(\"PinnedChildDefinitionId\" IS NULL AND \"PinnedChildStartEventId\" IS NULL AND \"PinState\" IS NULL AND (\"ActivityKind\" <> 'callActivity' OR \"TotalCount\" = 0)) OR (\"ActivityKind\" = 'callActivity' AND \"TotalCount\" > 0 AND \"PinnedChildDefinitionId\" IS NOT NULL AND \"PinnedChildStartEventId\" IS NOT NULL AND \"PinState\" IN ('pinned', 'released') AND (\"PinState\" = 'pinned') = (\"Status\" = 'active'))");

            migrationBuilder.AddCheckConstraint(
                name: "CK_call_activity_executions_automatic_item_pair",
                schema: "flowbit",
                table: "call_activity_executions",
                sql: "(\"AutomaticMultiInstanceItemId\" IS NULL) = (\"MultiInstanceExecutionId\" IS NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_workflow_jobs_MultiInstanceExecutionId",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_multi_instance_executions_call_pin_shape",
                schema: "flowbit",
                table: "multi_instance_executions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_call_activity_executions_automatic_item_pair",
                schema: "flowbit",
                table: "call_activity_executions");
        }
    }
}
