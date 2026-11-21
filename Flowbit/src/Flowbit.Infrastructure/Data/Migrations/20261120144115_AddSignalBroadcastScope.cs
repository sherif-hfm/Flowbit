using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Records a signal broadcast's business-key or instance scope. Both columns are
    /// nullable and null in every existing row, which keeps each row's meaning: one
    /// family, or every family. <c>InstanceId</c> has no foreign key, because the check
    /// would make a throw wait for a transaction holding the instance row.
    /// </summary>
    public partial class AddSignalBroadcastScope : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "BusinessKey",
                schema: "flowbit",
                table: "signal_broadcasts",
                type: "character varying(300)",
                maxLength: 300,
                nullable: true,
                collation: "C");

            migrationBuilder.AddColumn<long>(
                name: "InstanceId",
                schema: "flowbit",
                table: "signal_broadcasts",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddCheckConstraint(
                name: "CK_signal_broadcasts_scope",
                schema: "flowbit",
                table: "signal_broadcasts",
                sql: "(\"BusinessKey\" IS NULL OR \"InstanceId\" IS NULL) AND ((\"BusinessKey\" IS NULL AND \"InstanceId\" IS NULL) OR \"WorkflowKey\" IS NOT NULL) AND (\"BusinessKey\" IS NULL OR \"BusinessKey\" <> '') AND (\"InstanceId\" IS NULL OR \"InstanceId\" > 0)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Dropping the columns would turn a narrow broadcast into one that reached
            // its whole family, so the guard refuses instead of rewriting history.
            migrationBuilder.Sql("""
                DO $guard$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM flowbit.signal_broadcasts
                        WHERE "BusinessKey" IS NOT NULL OR "InstanceId" IS NOT NULL) THEN
                        RAISE EXCEPTION 'Signal broadcasts with a business-key or instance scope exist. Remove them before reverting AddSignalBroadcastScope.';
                    END IF;
                END
                $guard$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_signal_broadcasts_scope",
                schema: "flowbit",
                table: "signal_broadcasts");

            migrationBuilder.DropColumn(
                name: "BusinessKey",
                schema: "flowbit",
                table: "signal_broadcasts");

            migrationBuilder.DropColumn(
                name: "InstanceId",
                schema: "flowbit",
                table: "signal_broadcasts");
        }
    }
}
