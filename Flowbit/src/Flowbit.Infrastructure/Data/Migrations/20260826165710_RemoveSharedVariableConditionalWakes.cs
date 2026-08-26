using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveSharedVariableConditionalWakes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                """
                DO $flowbit$
                BEGIN
                    IF EXISTS (
                           SELECT 1
                           FROM flowbit.workflow_definition_shared_variable_dependencies
                           LIMIT 1)
                       OR EXISTS (
                           SELECT 1
                           FROM flowbit.shared_variable_wakes
                           LIMIT 1)
                       OR EXISTS (
                           SELECT 1
                           FROM flowbit.shared_variable_wake_deliveries
                           LIMIT 1)
                       OR EXISTS (
                           SELECT 1
                           FROM flowbit.shared_variable_wake_incidents
                           LIMIT 1) THEN
                        RAISE EXCEPTION
                            'Cannot remove shared-variable conditional wake infrastructure while legacy dependency, wake, delivery, or incident rows exist. Drain the legacy state before retrying the migration.';
                    END IF;
                END;
                $flowbit$;
                """);

            migrationBuilder.DropTable(
                name: "shared_variable_wake_incidents",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "shared_variable_wake_deliveries",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "shared_variable_wakes",
                schema: "flowbit");

            migrationBuilder.DropTable(
                name: "workflow_definition_shared_variable_dependencies",
                schema: "flowbit");

            migrationBuilder.Sql(
                "DROP FUNCTION IF EXISTS flowbit.stamp_initial_shared_variable_wake_clock();");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "shared_variable_wakes",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    RevisionId = table.Column<long>(type: "bigint", nullable: false),
                    SharedVariableId = table.Column<long>(type: "bigint", nullable: false),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    AvailableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    ExpansionCursorTokenId = table.Column<long>(type: "bigint", nullable: false),
                    HeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeasedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 25),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shared_variable_wakes", x => x.Id);
                    table.CheckConstraint("CK_shared_variable_wakes_attempts", "\"AttemptCount\" >= 0");
                    table.CheckConstraint("CK_shared_variable_wakes_completion_shape", "(\"Status\" IN ('completed', 'cancelled') AND \"CompletedAt\" IS NOT NULL) OR (\"Status\" NOT IN ('completed', 'cancelled') AND \"CompletedAt\" IS NULL)");
                    table.CheckConstraint("CK_shared_variable_wakes_cursor", "\"ExpansionCursorTokenId\" >= 0");
                    table.CheckConstraint("CK_shared_variable_wakes_lease_shape", "(\"Status\" = 'leased' AND \"LeaseToken\" IS NOT NULL AND \"LeasedBy\" IS NOT NULL AND \"LeaseExpiresAt\" IS NOT NULL AND \"HeartbeatAt\" IS NOT NULL) OR (\"Status\" <> 'leased' AND \"LeaseToken\" IS NULL AND \"LeasedBy\" IS NULL AND \"LeaseExpiresAt\" IS NULL AND \"HeartbeatAt\" IS NULL)");
                    table.CheckConstraint("CK_shared_variable_wakes_max_attempts", "\"MaxAttempts\" > 0 AND \"AttemptCount\" <= \"MaxAttempts\"");
                    table.CheckConstraint("CK_shared_variable_wakes_status", "\"Status\" IN ('pending', 'leased', 'completed', 'failed', 'cancelled', 'incident')");
                    table.ForeignKey(
                        name: "FK_shared_variable_wakes_shared_variable_revisions_RevisionId_~",
                        columns: x => new { x.RevisionId, x.SharedVariableId, x.Revision },
                        principalSchema: "flowbit",
                        principalTable: "shared_variable_revisions",
                        principalColumns: new[] { "Id", "SharedVariableId", "Revision" },
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_shared_variable_wakes_shared_variables_SharedVariableId",
                        column: x => x.SharedVariableId,
                        principalSchema: "flowbit",
                        principalTable: "shared_variables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "workflow_definition_shared_variable_dependencies",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    SharedVariableId = table.Column<long>(type: "bigint", nullable: false),
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    Kind = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    NodeExternalId = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    NodeId = table.Column<int>(type: "integer", nullable: false),
                    SharedKey = table.Column<string>(type: "citext", maxLength: 300, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_workflow_definition_shared_variable_dependencies", x => x.Id);
                    table.CheckConstraint("CK_workflow_definition_shared_variable_dependencies_kind", "\"Kind\" IN ('conditionalCatch')");
                    table.ForeignKey(
                        name: "FK_workflow_definition_shared_variable_dependencies_shared_var~",
                        column: x => x.SharedVariableId,
                        principalSchema: "flowbit",
                        principalTable: "shared_variables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_workflow_definition_shared_variable_dependencies_workflow_d~",
                        column: x => x.WorkflowDefinitionId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "shared_variable_wake_deliveries",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    InstanceId = table.Column<long>(type: "bigint", nullable: false),
                    TokenId = table.Column<long>(type: "bigint", nullable: false),
                    WakeId = table.Column<long>(type: "bigint", nullable: false),
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    ActivationId = table.Column<Guid>(type: "uuid", nullable: false),
                    AttemptCount = table.Column<int>(type: "integer", nullable: false),
                    AvailableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    HeartbeatAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastError = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseGeneration = table.Column<long>(type: "bigint", nullable: false),
                    LeaseToken = table.Column<Guid>(type: "uuid", nullable: true),
                    LeasedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false, defaultValue: 25),
                    NodeId = table.Column<int>(type: "integer", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shared_variable_wake_deliveries", x => x.Id);
                    table.CheckConstraint("CK_shared_variable_wake_deliveries_attempts", "\"AttemptCount\" >= 0");
                    table.CheckConstraint("CK_shared_variable_wake_deliveries_completion_shape", "(\"Status\" IN ('completed', 'cancelled') AND \"CompletedAt\" IS NOT NULL) OR (\"Status\" NOT IN ('completed', 'cancelled') AND \"CompletedAt\" IS NULL)");
                    table.CheckConstraint("CK_shared_variable_wake_deliveries_lease_shape", "(\"Status\" = 'leased' AND \"LeaseToken\" IS NOT NULL AND \"LeasedBy\" IS NOT NULL AND \"LeaseExpiresAt\" IS NOT NULL AND \"HeartbeatAt\" IS NOT NULL) OR (\"Status\" <> 'leased' AND \"LeaseToken\" IS NULL AND \"LeasedBy\" IS NULL AND \"LeaseExpiresAt\" IS NULL AND \"HeartbeatAt\" IS NULL)");
                    table.CheckConstraint("CK_shared_variable_wake_deliveries_max_attempts", "\"MaxAttempts\" > 0 AND \"AttemptCount\" <= \"MaxAttempts\"");
                    table.CheckConstraint("CK_shared_variable_wake_deliveries_status", "\"Status\" IN ('pending', 'leased', 'completed', 'failed', 'cancelled', 'incident')");
                    table.ForeignKey(
                        name: "FK_shared_variable_wake_deliveries_execution_tokens_TokenId",
                        column: x => x.TokenId,
                        principalSchema: "flowbit",
                        principalTable: "execution_tokens",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_shared_variable_wake_deliveries_shared_variable_wakes_WakeId",
                        column: x => x.WakeId,
                        principalSchema: "flowbit",
                        principalTable: "shared_variable_wakes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_shared_variable_wake_deliveries_workflow_definitions_Workfl~",
                        column: x => x.WorkflowDefinitionId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_definitions",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_shared_variable_wake_deliveries_workflow_instances_Instance~",
                        column: x => x.InstanceId,
                        principalSchema: "flowbit",
                        principalTable: "workflow_instances",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "shared_variable_wake_incidents",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    DeliveryId = table.Column<long>(type: "bigint", nullable: true),
                    SharedVariableId = table.Column<long>(type: "bigint", nullable: false),
                    WakeId = table.Column<long>(type: "bigint", nullable: true),
                    ActivationId = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    Details = table.Column<string>(type: "character varying(4000)", maxLength: 4000, nullable: true),
                    InstanceId = table.Column<long>(type: "bigint", nullable: true),
                    NodeId = table.Column<int>(type: "integer", nullable: true),
                    OriginalDeliveryId = table.Column<long>(type: "bigint", nullable: true),
                    OriginalWakeId = table.Column<long>(type: "bigint", nullable: false),
                    ResolutionReason = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ResolvedBy = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: true),
                    Revision = table.Column<long>(type: "bigint", nullable: false),
                    SharedKey = table.Column<string>(type: "citext", maxLength: 300, nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Summary = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: false),
                    TokenId = table.Column<long>(type: "bigint", nullable: true),
                    Type = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()"),
                    WorkKind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_shared_variable_wake_incidents", x => x.Id);
                    table.CheckConstraint("CK_shared_variable_wake_incidents_resolution_shape", "(\"Status\" = 'open' AND \"ResolvedAt\" IS NULL AND \"ResolvedBy\" IS NULL AND \"ResolutionReason\" IS NULL) OR (\"Status\" = 'resolved' AND \"ResolvedAt\" IS NOT NULL AND \"ResolvedBy\" IS NOT NULL)");
                    table.CheckConstraint("CK_shared_variable_wake_incidents_status", "\"Status\" IN ('open', 'resolved')");
                    table.CheckConstraint("CK_shared_variable_wake_incidents_target_shape", "(\"WorkKind\" = 'expansion' AND \"OriginalDeliveryId\" IS NULL AND \"DeliveryId\" IS NULL AND (\"Status\" <> 'open' OR \"WakeId\" IS NOT NULL)) OR (\"WorkKind\" = 'delivery' AND \"OriginalDeliveryId\" IS NOT NULL AND (\"Status\" <> 'open' OR \"DeliveryId\" IS NOT NULL))");
                    table.CheckConstraint("CK_shared_variable_wake_incidents_work_kind", "\"WorkKind\" IN ('expansion', 'delivery')");
                    table.ForeignKey(
                        name: "FK_shared_variable_wake_incidents_shared_variable_wake_deliver~",
                        column: x => x.DeliveryId,
                        principalSchema: "flowbit",
                        principalTable: "shared_variable_wake_deliveries",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_shared_variable_wake_incidents_shared_variable_wakes_WakeId",
                        column: x => x.WakeId,
                        principalSchema: "flowbit",
                        principalTable: "shared_variable_wakes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_shared_variable_wake_incidents_shared_variables_SharedVaria~",
                        column: x => x.SharedVariableId,
                        principalSchema: "flowbit",
                        principalTable: "shared_variables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_deliveries_expired_lease",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries",
                columns: new[] { "LeaseExpiresAt", "Id" },
                filter: "\"Status\" = 'leased' AND \"LeaseExpiresAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_deliveries_InstanceId_Status_Id",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries",
                columns: new[] { "InstanceId", "Status", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_deliveries_open_predecessor",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries",
                columns: new[] { "TokenId", "ActivationId", "WakeId", "Id" },
                filter: "\"Status\" IN ('pending', 'leased')");

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_deliveries_pending_available",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries",
                columns: new[] { "AvailableAt", "Id" },
                filter: "\"Status\" = 'pending'");

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_deliveries_Status_AvailableAt_Id",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries",
                columns: new[] { "Status", "AvailableAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_deliveries_terminal_cleanup",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries",
                columns: new[] { "CompletedAt", "Id" },
                filter: "\"Status\" IN ('completed', 'cancelled')");

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_deliveries_WakeId_TokenId_ActivationId",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries",
                columns: new[] { "WakeId", "TokenId", "ActivationId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_deliveries_WorkflowDefinitionId",
                schema: "flowbit",
                table: "shared_variable_wake_deliveries",
                column: "WorkflowDefinitionId");

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_incidents_DeliveryId",
                schema: "flowbit",
                table: "shared_variable_wake_incidents",
                column: "DeliveryId",
                unique: true,
                filter: "\"Status\" = 'open' AND \"WorkKind\" = 'delivery'");

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_incidents_OriginalWakeId_OriginalDeliv~",
                schema: "flowbit",
                table: "shared_variable_wake_incidents",
                columns: new[] { "OriginalWakeId", "OriginalDeliveryId", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_incidents_SharedVariableId",
                schema: "flowbit",
                table: "shared_variable_wake_incidents",
                column: "SharedVariableId");

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_incidents_Status_UpdatedAt_Id",
                schema: "flowbit",
                table: "shared_variable_wake_incidents",
                columns: new[] { "Status", "UpdatedAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wake_incidents_WakeId",
                schema: "flowbit",
                table: "shared_variable_wake_incidents",
                column: "WakeId",
                unique: true,
                filter: "\"Status\" = 'open' AND \"WorkKind\" = 'expansion'");

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wakes_expired_lease",
                schema: "flowbit",
                table: "shared_variable_wakes",
                columns: new[] { "LeaseExpiresAt", "Revision", "Id" },
                filter: "\"Status\" = 'leased' AND \"LeaseExpiresAt\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wakes_pending_available",
                schema: "flowbit",
                table: "shared_variable_wakes",
                columns: new[] { "AvailableAt", "Revision", "Id" },
                filter: "\"Status\" = 'pending'");

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wakes_RevisionId",
                schema: "flowbit",
                table: "shared_variable_wakes",
                column: "RevisionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wakes_RevisionId_SharedVariableId_Revision",
                schema: "flowbit",
                table: "shared_variable_wakes",
                columns: new[] { "RevisionId", "SharedVariableId", "Revision" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wakes_SharedVariableId",
                schema: "flowbit",
                table: "shared_variable_wakes",
                column: "SharedVariableId");

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wakes_Status_AvailableAt_Id",
                schema: "flowbit",
                table: "shared_variable_wakes",
                columns: new[] { "Status", "AvailableAt", "Id" });

            migrationBuilder.CreateIndex(
                name: "IX_shared_variable_wakes_terminal_cleanup",
                schema: "flowbit",
                table: "shared_variable_wakes",
                columns: new[] { "CompletedAt", "Id" },
                filter: "\"Status\" IN ('completed', 'cancelled')");

            migrationBuilder.CreateIndex(
                name: "IX_workflow_definition_shared_variable_dependencies_SharedVari~",
                schema: "flowbit",
                table: "workflow_definition_shared_variable_dependencies",
                columns: new[] { "SharedVariableId", "WorkflowDefinitionId", "NodeId" });

            migrationBuilder.CreateIndex(
                name: "IX_workflow_definition_shared_variable_dependencies_WorkflowDe~",
                schema: "flowbit",
                table: "workflow_definition_shared_variable_dependencies",
                columns: new[] { "WorkflowDefinitionId", "NodeId", "SharedVariableId", "Kind" },
                unique: true);

            migrationBuilder.Sql(
                """
                CREATE OR REPLACE FUNCTION flowbit.stamp_initial_shared_variable_wake_clock()
                RETURNS trigger
                LANGUAGE plpgsql
                AS $function$
                DECLARE
                    database_now timestamp with time zone;
                BEGIN
                    IF NEW."Status" = 'pending'
                       AND NEW."AttemptCount" = 0
                       AND NEW."LeaseGeneration" = 0 THEN
                        database_now := clock_timestamp();
                        NEW."AvailableAt" := database_now;
                        NEW."CreatedAt" := database_now;
                        NEW."UpdatedAt" := database_now;
                    END IF;
                    RETURN NEW;
                END;
                $function$;

                CREATE TRIGGER TR_shared_variable_wakes_initial_insert_clock
                BEFORE INSERT
                ON flowbit.shared_variable_wakes
                FOR EACH ROW
                EXECUTE FUNCTION flowbit.stamp_initial_shared_variable_wake_clock();
                """);
        }
    }
}
