using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Lets a workflow throw a signal (EVENT-04B). A broadcast can name an instance and
    /// node as its thrower and carries the automatic chain count a delivery continues.
    /// The thrower instance is a plain column, as the scope's instance is. Every existing
    /// row has a user or client thrower, null thrower columns, and a count of 0, which the
    /// replaced check accepts, so nothing is backfilled. The partial index serves both
    /// open-broadcast budgets and the backlog gauge, and the seed adds the budget for
    /// throws from workflows without overwriting a row an operator created.
    /// </summary>
    public partial class AddSignalThrowEvents : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_signal_broadcasts_thrower",
                schema: "flowbit",
                table: "signal_broadcasts");

            migrationBuilder.AddColumn<int>(
                name: "AutomaticActivationCount",
                schema: "flowbit",
                table: "signal_broadcasts",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<long>(
                name: "ThrowerInstanceId",
                schema: "flowbit",
                table: "signal_broadcasts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "ThrowerNodeId",
                schema: "flowbit",
                table: "signal_broadcasts",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ThrowerNodeName",
                schema: "flowbit",
                table: "signal_broadcasts",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_signal_broadcasts_open_thrower",
                schema: "flowbit",
                table: "signal_broadcasts",
                column: "ThrownByKind",
                filter: "\"Status\" = 'delivering'");

            migrationBuilder.AddCheckConstraint(
                name: "CK_signal_broadcasts_thrower",
                schema: "flowbit",
                table: "signal_broadcasts",
                sql: "\"ThrownByKind\" IN ('user', 'client', 'instance') AND ((\"ThrownByKind\" = 'instance' AND \"ThrowerInstanceId\" IS NOT NULL AND \"ThrowerInstanceId\" > 0 AND \"ThrowerNodeId\" IS NOT NULL AND \"IdempotencyKey\" IS NULL AND \"AutomaticActivationCount\" >= 0) OR (\"ThrownByKind\" <> 'instance' AND \"ThrowerInstanceId\" IS NULL AND \"ThrowerNodeId\" IS NULL AND \"ThrowerNodeName\" IS NULL AND \"AutomaticActivationCount\" = 0))");

            migrationBuilder.Sql("""
                INSERT INTO flowbit.engine_settings
                    ("Namespace", "Key", "Value", "Description", "CreatedAt", "UpdatedAt")
                SELECT 'Signals', 'MaxOpenWorkflowBroadcasts', '10000',
                    'Soft limit on signal broadcasts thrown by workflows that are still delivering; a throw event or signal end event beyond it fails its step. Invalid or missing values default to 10000, and values above 100000 are capped. Broadcasts thrown through the API count toward Signals.MaxOpenBroadcasts instead.',
                    CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
                WHERE NOT EXISTS
                (
                    SELECT 1
                    FROM flowbit.engine_settings
                    WHERE
                        ("Namespace" = 'Signals' AND "Key" = 'MaxOpenWorkflowBroadcasts')
                        OR
                        (BTRIM(COALESCE("Namespace", '')) = '' AND "Key" = 'Signals.MaxOpenWorkflowBroadcasts')
                )
                ON CONFLICT ("Namespace", "Key") DO NOTHING;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The restored check accepts only user and client throwers, so a broadcast
            // thrown by an instance would violate it. Retention removes such broadcasts
            // once they are finished and old enough.
            migrationBuilder.Sql("""
                DO $guard$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM flowbit.signal_broadcasts
                        WHERE "ThrownByKind" = 'instance') THEN
                        RAISE EXCEPTION 'Signal broadcasts thrown by workflow instances exist. Remove them before reverting AddSignalThrowEvents.';
                    END IF;
                END
                $guard$;
                """);

            migrationBuilder.Sql("""
                DELETE FROM flowbit.engine_settings
                WHERE "Namespace" = 'Signals' AND "Key" = 'MaxOpenWorkflowBroadcasts';
                """);

            migrationBuilder.DropIndex(
                name: "IX_signal_broadcasts_open_thrower",
                schema: "flowbit",
                table: "signal_broadcasts");

            migrationBuilder.DropCheckConstraint(
                name: "CK_signal_broadcasts_thrower",
                schema: "flowbit",
                table: "signal_broadcasts");

            migrationBuilder.DropColumn(
                name: "AutomaticActivationCount",
                schema: "flowbit",
                table: "signal_broadcasts");

            migrationBuilder.DropColumn(
                name: "ThrowerInstanceId",
                schema: "flowbit",
                table: "signal_broadcasts");

            migrationBuilder.DropColumn(
                name: "ThrowerNodeId",
                schema: "flowbit",
                table: "signal_broadcasts");

            migrationBuilder.DropColumn(
                name: "ThrowerNodeName",
                schema: "flowbit",
                table: "signal_broadcasts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_signal_broadcasts_thrower",
                schema: "flowbit",
                table: "signal_broadcasts",
                sql: "\"ThrownByKind\" IN ('user', 'client')");
        }
    }
}
