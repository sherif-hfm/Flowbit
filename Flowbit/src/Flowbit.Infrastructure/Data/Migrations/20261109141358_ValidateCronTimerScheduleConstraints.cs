using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Validates the three timer_subscriptions constraints that AddCronTimerSchedules added NOT VALID.
    /// </summary>
    /// <remarks>
    /// Validation scans the table, so each constraint is validated in its own statement outside a transaction, under a
    /// lock that lets reads and writes continue. EF records this migration only after the last statement, and
    /// validating a constraint that is already valid changes nothing, so a run that stops partway is finished by
    /// applying the migration again. This step has its own migration because a migration that also changes the schema
    /// in a transaction cannot be applied again once that transaction has committed.
    /// </remarks>
    public partial class ValidateCronTimerScheduleConstraints : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            foreach (var constraint in new[]
                     {
                         "CK_timer_subscriptions_schedule_kind",
                         "CK_timer_subscriptions_cron_shape",
                         "CK_timer_subscriptions_cron_not_completed"
                     })
            {
                migrationBuilder.Sql(
                    $"""
                    ALTER TABLE flowbit.timer_subscriptions
                        VALIDATE CONSTRAINT "{constraint}";
                    """,
                    suppressTransaction: true);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A validated constraint stays valid; reverting AddCronTimerSchedules removes the constraints.
        }
    }
}
