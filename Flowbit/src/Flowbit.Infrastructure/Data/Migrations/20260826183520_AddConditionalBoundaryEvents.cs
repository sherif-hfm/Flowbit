using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddConditionalBoundaryEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<long>(
                name: "ConditionalBoundaryOccurrence",
                schema: "flowbit",
                table: "workflow_jobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "ConditionalBoundarySubscriptionId",
                schema: "flowbit",
                table: "workflow_jobs",
                type: "bigint",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "conditional_boundary_subscriptions",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    WorkflowKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    HostTokenId = table.Column<long>(type: "bigint", nullable: false),
                    HostActivationId = table.Column<Guid>(type: "uuid", nullable: false),
                    BoundaryNodeId = table.Column<int>(type: "integer", nullable: false),
                    BoundaryNodeName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    AttachedToNodeId = table.Column<int>(type: "integer", nullable: false),
                    OutgoingFlowId = table.Column<int>(type: "integer", nullable: false),
                    Condition = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: false),
                    DeliveryMode = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CancelActivity = table.Column<bool>(type: "boolean", nullable: false),
                    IsConditionTrue = table.Column<bool>(type: "boolean", nullable: false),
                    Occurrence = table.Column<long>(type: "bigint", nullable: false),
                    Status = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_conditional_boundary_subscriptions", x => x.Id);
                    table.CheckConstraint("CK_conditional_boundary_subscriptions_delivery_mode", "\"DeliveryMode\" IN ('atomic', 'durableAsync')");
                    table.CheckConstraint("CK_conditional_boundary_subscriptions_occurrence", "\"Occurrence\" >= 0");
                    table.CheckConstraint("CK_conditional_boundary_subscriptions_status", "\"Status\" IN ('active', 'completed', 'cancelled')");
                    table.CheckConstraint("CK_conditional_boundary_subscriptions_terminal_time", "(\"Status\" = 'active' AND \"CompletedAt\" IS NULL) OR (\"Status\" IN ('completed', 'cancelled') AND \"CompletedAt\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_conditional_boundary_subscriptions_execution_tokens_HostTok~",
                        column: x => x.HostTokenId,
                        principalSchema: "flowbit",
                        principalTable: "execution_tokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_conditional_boundary_subscriptions_workflow_definitions_Wor~",
                        columns: x => new { x.WorkflowDefinitionId, x.WorkflowKey },
                        principalSchema: "flowbit",
                        principalTable: "workflow_definitions",
                        principalColumns: new[] { "Id", "WorkflowKey" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_conditional_boundary_subscriptions_workflow_instances_Insta~",
                        column: x => x.InstanceId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_jobs_ConditionalBoundarySubscriptionId_Conditional~",
                schema: "flowbit",
                table: "workflow_jobs",
                columns: new[] { "ConditionalBoundarySubscriptionId", "ConditionalBoundaryOccurrence" },
                unique: true,
                filter: "\"ConditionalBoundarySubscriptionId\" IS NOT NULL AND \"ConditionalBoundaryOccurrence\" IS NOT NULL");

            migrationBuilder.AddCheckConstraint(
                name: "CK_workflow_jobs_conditional_boundary_occurrence",
                schema: "flowbit",
                table: "workflow_jobs",
                sql: "(\"ConditionalBoundarySubscriptionId\" IS NULL AND \"ConditionalBoundaryOccurrence\" IS NULL) OR (\"ConditionalBoundarySubscriptionId\" IS NOT NULL AND \"ConditionalBoundaryOccurrence\" > 0)");

            migrationBuilder.CreateIndex(
                name: "IX_conditional_boundary_subscriptions_HostTokenId",
                schema: "flowbit",
                table: "conditional_boundary_subscriptions",
                column: "HostTokenId");

            migrationBuilder.CreateIndex(
                name: "IX_conditional_boundary_subscriptions_InstanceId_BoundaryNodeI~",
                schema: "flowbit",
                table: "conditional_boundary_subscriptions",
                columns: new[] { "InstanceId", "BoundaryNodeId", "HostTokenId", "Id" },
                filter: "\"Status\" = 'active'");

            migrationBuilder.CreateIndex(
                name: "IX_conditional_boundary_subscriptions_InstanceId_HostTokenId_~1",
                schema: "flowbit",
                table: "conditional_boundary_subscriptions",
                columns: new[] { "InstanceId", "HostTokenId", "HostActivationId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_conditional_boundary_subscriptions_InstanceId_HostTokenId_H~",
                schema: "flowbit",
                table: "conditional_boundary_subscriptions",
                columns: new[] { "InstanceId", "HostTokenId", "HostActivationId", "BoundaryNodeId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_conditional_boundary_subscriptions_WorkflowDefinitionId_Wor~",
                schema: "flowbit",
                table: "conditional_boundary_subscriptions",
                columns: new[] { "WorkflowDefinitionId", "WorkflowKey" });

            migrationBuilder.AddForeignKey(
                name: "FK_workflow_jobs_conditional_boundary_subscriptions_Conditiona~",
                schema: "flowbit",
                table: "workflow_jobs",
                column: "ConditionalBoundarySubscriptionId",
                principalSchema: "flowbit",
                principalTable: "conditional_boundary_subscriptions",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_workflow_jobs_conditional_boundary_subscriptions_Conditiona~",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.DropTable(
                name: "conditional_boundary_subscriptions",
                schema: "flowbit");

            migrationBuilder.DropIndex(
                name: "IX_workflow_jobs_ConditionalBoundarySubscriptionId_Conditional~",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.DropCheckConstraint(
                name: "CK_workflow_jobs_conditional_boundary_occurrence",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.DropColumn(
                name: "ConditionalBoundaryOccurrence",
                schema: "flowbit",
                table: "workflow_jobs");

            migrationBuilder.DropColumn(
                name: "ConditionalBoundarySubscriptionId",
                schema: "flowbit",
                table: "workflow_jobs");
        }
    }
}
