using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql.EntityFrameworkCore.PostgreSQL.Metadata;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Lets a broadcast signal start a workflow (EVENT-04C). <c>signal_start_subscriptions</c>
    /// holds one row per family and signal name while the family's published default has a
    /// signal start with that name; the operations that change a default keep it in step. A
    /// delivery gains a kind: a <c>wait</c> keeps its subscription, instance, token, and
    /// activation, and a <c>start</c> has none of them until it creates an instance. A
    /// broadcast counts its starts. Every existing delivery is a wait with all four columns,
    /// and every existing broadcast has no start, so nothing is backfilled.
    /// </summary>
    /// <remarks>
    /// The delivery shape check is added NOT VALID, and the one-start-per-family index is
    /// left out: both read the whole delivery table, so ValidateSignalStartConstraints adds
    /// them outside a transaction without blocking writes. The broadcast count's check reads
    /// one row per broadcast and is validated here.
    /// </remarks>
    public partial class AddSignalStartEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<long>(
                name: "TokenId",
                schema: "flowbit",
                table: "signal_deliveries",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "SubscriptionId",
                schema: "flowbit",
                table: "signal_deliveries",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<long>(
                name: "InstanceId",
                schema: "flowbit",
                table: "signal_deliveries",
                type: "bigint",
                nullable: true,
                oldClrType: typeof(long),
                oldType: "bigint");

            migrationBuilder.AlterColumn<Guid>(
                name: "ActivationId",
                schema: "flowbit",
                table: "signal_deliveries",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "Kind",
                schema: "flowbit",
                table: "signal_deliveries",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "wait");

            migrationBuilder.AddColumn<int>(
                name: "StartDeliveryCount",
                schema: "flowbit",
                table: "signal_broadcasts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.CreateTable(
                name: "signal_start_subscriptions",
                schema: "flowbit",
                columns: table => new
                {
                    Id = table.Column<long>(type: "bigint", nullable: false)
                        .Annotation("Npgsql:ValueGenerationStrategy", NpgsqlValueGenerationStrategy.IdentityByDefaultColumn),
                    WorkflowKey = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    SignalName = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false, collation: "C"),
                    WorkflowDefinitionId = table.Column<long>(type: "bigint", nullable: false),
                    SignalNodeId = table.Column<int>(type: "integer", nullable: false),
                    SignalNodeName = table.Column<string>(type: "character varying(300)", maxLength: 300, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false, defaultValueSql: "now()")
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_signal_start_subscriptions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_signal_start_subscriptions_workflow_definitions_WorkflowDef~",
                        columns: x => new { x.WorkflowDefinitionId, x.WorkflowKey },
                        principalSchema: "flowbit",
                        principalTable: "workflow_definitions",
                        principalColumns: new[] { "Id", "WorkflowKey" },
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.Sql(
                "ALTER TABLE flowbit.\"signal_deliveries\" ADD CONSTRAINT \"CK_signal_deliveries_kind_shape\" CHECK ("
                + "\"Kind\" IN ('wait', 'start') AND ((\"Kind\" = 'wait' AND \"SubscriptionId\" IS NOT NULL AND \"InstanceId\" IS NOT NULL AND \"TokenId\" IS NOT NULL AND \"ActivationId\" IS NOT NULL) OR (\"Kind\" = 'start' AND \"SubscriptionId\" IS NULL AND \"TokenId\" IS NULL AND \"ActivationId\" IS NULL AND \"AttachedToNodeId\" IS NULL AND NOT \"CancelActivity\" AND ((\"Status\" = 'delivered') = (\"InstanceId\" IS NOT NULL))))"
                + ") NOT VALID;");

            migrationBuilder.AddCheckConstraint(
                name: "CK_signal_broadcasts_start_count",
                schema: "flowbit",
                table: "signal_broadcasts",
                sql: "\"StartDeliveryCount\" BETWEEN 0 AND \"TotalDeliveryCount\"");

            migrationBuilder.CreateIndex(
                name: "IX_signal_start_subscriptions_SignalName_WorkflowKey",
                schema: "flowbit",
                table: "signal_start_subscriptions",
                columns: new[] { "SignalName", "WorkflowKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_signal_start_subscriptions_WorkflowDefinitionId_WorkflowKey",
                schema: "flowbit",
                table: "signal_start_subscriptions",
                columns: new[] { "WorkflowDefinitionId", "WorkflowKey" });

            migrationBuilder.CreateIndex(
                name: "IX_signal_start_subscriptions_WorkflowKey",
                schema: "flowbit",
                table: "signal_start_subscriptions",
                column: "WorkflowKey");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The restored NOT NULL columns would reject a start delivery, and nothing would
            // deliver a signal start of a stored definition. Retention removes finished
            // broadcasts with their deliveries once they are old enough.
            migrationBuilder.Sql("""
                DO $guard$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM flowbit.signal_deliveries
                        WHERE "Kind" = 'start') THEN
                        RAISE EXCEPTION 'Signal start deliveries exist. Remove their broadcasts before reverting AddSignalStartEvents.';
                    END IF;
                    IF EXISTS (
                        SELECT 1
                        FROM flowbit.workflow_definitions
                        WHERE "Definition" @> '{"flowNodes":[{"type":"signalStartEvent"}]}'::jsonb) THEN
                        RAISE EXCEPTION 'Workflow definitions with a signal start event exist. Delete them before reverting AddSignalStartEvents.';
                    END IF;
                END
                $guard$;
                """);

            migrationBuilder.DropTable(
                name: "signal_start_subscriptions",
                schema: "flowbit");

            migrationBuilder.Sql("DROP INDEX IF EXISTS flowbit.\"IX_signal_deliveries_start\";");

            migrationBuilder.DropCheckConstraint(
                name: "CK_signal_deliveries_kind_shape",
                schema: "flowbit",
                table: "signal_deliveries");

            migrationBuilder.DropCheckConstraint(
                name: "CK_signal_broadcasts_start_count",
                schema: "flowbit",
                table: "signal_broadcasts");

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "flowbit",
                table: "signal_deliveries");

            migrationBuilder.DropColumn(
                name: "StartDeliveryCount",
                schema: "flowbit",
                table: "signal_broadcasts");

            migrationBuilder.AlterColumn<long>(
                name: "TokenId",
                schema: "flowbit",
                table: "signal_deliveries",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "SubscriptionId",
                schema: "flowbit",
                table: "signal_deliveries",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<long>(
                name: "InstanceId",
                schema: "flowbit",
                table: "signal_deliveries",
                type: "bigint",
                nullable: false,
                defaultValue: 0L,
                oldClrType: typeof(long),
                oldType: "bigint",
                oldNullable: true);

            migrationBuilder.AlterColumn<Guid>(
                name: "ActivationId",
                schema: "flowbit",
                table: "signal_deliveries",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
