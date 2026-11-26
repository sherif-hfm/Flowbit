using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Raises the hard cap on a signal broadcast's recipients from 100,000 to 250,000 (EVENT-04G),
    /// after every statement that touches a whole broadcast passed its gate at that size. It
    /// replaces CK_signal_broadcasts_counts, which scans signal_broadcasts once in this
    /// transaction, and updates the seeded description of Signals.MaxSubscriptionsPerBroadcast
    /// while it still reads as seeded. The setting's default stays 10,000.
    /// </summary>
    public partial class RaiseSignalBroadcastRecipientCap : Migration
    {
        private const string Counts =
            "\"DeliveredCount\" >= 0 AND \"SkippedCount\" >= 0 AND \"FailedCount\" >= 0 AND \"CancelledCount\" >= 0 AND \"DeliveredCount\" + \"SkippedCount\" + \"FailedCount\" + \"CancelledCount\" <= \"TotalDeliveryCount\"";

        private const string SeededDescription =
            "Maximum number of waiting signal subscriptions one broadcast may reach; a throw that would reach more is rejected. Invalid or missing values default to 10000, and values above 100000 are capped.";

        private const string RaisedDescription =
            "Maximum number of waiting signal subscriptions one broadcast may reach; a throw that would reach more is rejected. Invalid or missing values default to 10000, and values above 250000 are capped.";

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_signal_broadcasts_counts",
                schema: "flowbit",
                table: "signal_broadcasts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_signal_broadcasts_counts",
                schema: "flowbit",
                table: "signal_broadcasts",
                sql: "\"TotalDeliveryCount\" BETWEEN 0 AND 250000 AND " + Counts);

            migrationBuilder.Sql(DescriptionSql(SeededDescription, RaisedDescription));
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // The older check cannot hold a broadcast above 100,000. Retention removes such a
            // broadcast once it is finished and old enough, or an operator deletes it.
            migrationBuilder.Sql("""
                DO $guard$
                BEGIN
                    IF EXISTS (
                        SELECT 1
                        FROM flowbit.signal_broadcasts
                        WHERE "TotalDeliveryCount" > 100000) THEN
                        RAISE EXCEPTION 'A signal broadcast has more than 100,000 deliveries. Let it finish and remove it (retention, or delete it) before reverting RaiseSignalBroadcastRecipientCap; set Signals.MaxSubscriptionsPerBroadcast to at most 100000 first so no new broadcast passes the older limit.';
                    END IF;
                END
                $guard$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_signal_broadcasts_counts",
                schema: "flowbit",
                table: "signal_broadcasts");

            migrationBuilder.AddCheckConstraint(
                name: "CK_signal_broadcasts_counts",
                schema: "flowbit",
                table: "signal_broadcasts",
                sql: "\"TotalDeliveryCount\" BETWEEN 0 AND 100000 AND " + Counts);

            migrationBuilder.Sql(DescriptionSql(RaisedDescription, SeededDescription));
        }

        /// <summary>Changes the setting's description only while it is the one this release wrote.</summary>
        private static string DescriptionSql(string from, string to) =>
            $"""
            UPDATE flowbit.engine_settings
            SET "Description" = '{to.Replace("'", "''")}'
            WHERE "Namespace" = 'Signals'
              AND "Key" = 'MaxSubscriptionsPerBroadcast'
              AND "Description" = '{from.Replace("'", "''")}';
            """;
    }
}
