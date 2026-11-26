using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Adds parallel signal delivery (EVENT-04G): each delivery job link names the job's share of
    /// its definition's deliveries, 0 of 1 for every existing link, which is the whole definition as
    /// before. Both columns change only the catalog; the check scans signal_broadcast_jobs once.
    /// The seeds add Signals.MaxDeliveryJobsPerDefinition and Signals.MinDeliveriesPerJob without
    /// overwriting a row an operator created, for example a MaxDeliveryJobsPerDefinition of 1 that
    /// keeps splitting off during a rolling upgrade.
    /// </summary>
    public partial class AddSignalDeliveryShares : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "ShareCount",
                schema: "flowbit",
                table: "signal_broadcast_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 1);

            migrationBuilder.AddColumn<int>(
                name: "ShareIndex",
                schema: "flowbit",
                table: "signal_broadcast_jobs",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddCheckConstraint(
                name: "CK_signal_broadcast_jobs_share",
                schema: "flowbit",
                table: "signal_broadcast_jobs",
                sql: "\"ShareCount\" BETWEEN 1 AND 32 AND \"ShareIndex\" >= 0 AND \"ShareIndex\" < \"ShareCount\"");

            migrationBuilder.Sql(SeedSql(
                "MaxDeliveryJobsPerDefinition",
                "2",
                "The most delivery jobs a signal broadcast gets for one workflow definition. A definition with at least twice Signals.MinDeliveriesPerJob recipients is split into up to this many jobs, each delivering the waits of its own instances at the same time; 1 turns splitting off. Default 2, at most 32; more jobs deliver faster while the database has capacity to spare, and delay other Worker activity when it has none. Read at each throw and at each retry of failed deliveries; a broadcast already delivering keeps its jobs. Size it with the Workers' FlowbitWorker:SignalShareConcurrency."));
            migrationBuilder.Sql(SeedSql(
                "MinDeliveriesPerJob",
                "500",
                "The fewest recipients a delivery job is created for when a signal broadcast's recipients of one workflow definition are split. A definition with n recipients gets n divided by this value jobs, rounded down, at least one and at most Signals.MaxDeliveryJobsPerDefinition. Default 500, at most 100000."));
        }

        /// <summary>Inserts one Signals setting unless a row with that key exists, as AddSignalCatalog does.</summary>
        private static string SeedSql(string key, string value, string description) =>
            $"""
            INSERT INTO flowbit.engine_settings
                ("Namespace", "Key", "Value", "Description", "CreatedAt", "UpdatedAt")
            SELECT 'Signals', '{key}', '{value}', '{description.Replace("'", "''")}', CURRENT_TIMESTAMP, CURRENT_TIMESTAMP
            WHERE NOT EXISTS
            (
                SELECT 1
                FROM flowbit.engine_settings
                WHERE
                    ("Namespace" = 'Signals' AND "Key" = '{key}')
                    OR
                    (BTRIM(COALESCE("Namespace", '')) = '' AND "Key" = 'Signals.{key}')
            )
            ON CONFLICT ("Namespace", "Key") DO NOTHING;
            """;

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Without the share columns, the lost-lease reconciliation of a share job would fail
            // every pending delivery of its definition, and an older Worker would run each share
            // job as a job for the whole definition. Let split broadcasts finish, or cancel them.
            migrationBuilder.Sql("""
                DO $guard$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM flowbit.workflow_jobs
                        WHERE "Kind" = 'signalBroadcastDeliver'
                          AND "Phase" = 'deliverShare'
                          AND "Status" NOT IN ('completed', 'cancelled', 'skipped')) THEN
                        RAISE EXCEPTION 'Share jobs of split signal broadcasts are not finished. Let those broadcasts finish, or cancel them, and retry any open incident of a share job so the job completes, before reverting AddSignalDeliveryShares; set Signals.MaxDeliveryJobsPerDefinition to 1 first so no new broadcast is split.';
                    END IF;
                END
                $guard$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_signal_broadcast_jobs_share",
                schema: "flowbit",
                table: "signal_broadcast_jobs");

            migrationBuilder.DropColumn(
                name: "ShareCount",
                schema: "flowbit",
                table: "signal_broadcast_jobs");

            migrationBuilder.DropColumn(
                name: "ShareIndex",
                schema: "flowbit",
                table: "signal_broadcast_jobs");

            migrationBuilder.Sql("""
                DELETE FROM flowbit.engine_settings
                WHERE ("Namespace" = 'Signals' AND "Key" = 'MaxDeliveryJobsPerDefinition' AND "Value" = '2')
                   OR ("Namespace" = 'Signals' AND "Key" = 'MinDeliveriesPerJob' AND "Value" = '500');
                """);
        }
    }
}
