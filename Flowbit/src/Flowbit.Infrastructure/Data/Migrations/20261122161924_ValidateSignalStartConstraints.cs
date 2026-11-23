using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Validates <c>CK_signal_deliveries_kind_shape</c>, which AddSignalStartEvents added NOT
    /// VALID, and builds <c>IX_signal_deliveries_start</c>, one start per broadcast and family.
    /// </summary>
    /// <remarks>
    /// Both read the whole delivery table, so they run outside a transaction under locks that
    /// let reads and writes continue. Validating a valid constraint changes nothing, and a
    /// concurrent build that stopped partway leaves an invalid index, which this drops before
    /// building again, so a run that stops partway is finished by applying the migration again.
    /// </remarks>
    public partial class ValidateSignalStartConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(
                "ALTER TABLE flowbit.\"signal_deliveries\" VALIDATE CONSTRAINT \"CK_signal_deliveries_kind_shape\";",
                suppressTransaction: true);

            migrationBuilder.Sql(
                """
                DO $invalid$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM pg_index AS i
                        JOIN pg_class AS c ON c.oid = i.indexrelid
                        JOIN pg_namespace AS n ON n.oid = c.relnamespace
                        WHERE n.nspname = 'flowbit'
                          AND c.relname = 'IX_signal_deliveries_start'
                          AND NOT i.indisvalid) THEN
                        DROP INDEX flowbit."IX_signal_deliveries_start";
                    END IF;
                END
                $invalid$;
                """,
                suppressTransaction: true);

            migrationBuilder.Sql(
                "CREATE UNIQUE INDEX CONCURRENTLY IF NOT EXISTS \"IX_signal_deliveries_start\" "
                + "ON flowbit.\"signal_deliveries\" (\"BroadcastId\", \"WorkflowKey\") WHERE \"Kind\" = 'start';",
                suppressTransaction: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The same guard as AddSignalStartEvents.Down, checked first, so a downgrade that
            // AddSignalStartEvents would refuse changes nothing, the start index included.
            migrationBuilder.Sql(
                """
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
                """,
                suppressTransaction: true);

            migrationBuilder.Sql(
                "DROP INDEX CONCURRENTLY IF EXISTS flowbit.\"IX_signal_deliveries_start\";",
                suppressTransaction: true);
        }
    }
}
