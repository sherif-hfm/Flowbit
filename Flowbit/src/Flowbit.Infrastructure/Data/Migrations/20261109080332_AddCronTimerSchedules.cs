using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Flowbit.Infrastructure.Data.Migrations
{
    /// <summary>
    /// Lets a timer start keep a cron schedule: the subscription stores the expression in ScheduleExpression and its IANA
    /// zone in the new ScheduleTimeZone column. Existing rows keep a null zone and their due times.
    /// </summary>
    /// <remarks>
    /// Adding a constraint locks timer_subscriptions exclusively until the transaction commits, and the table holds every
    /// instance timer while Workers keep firing them. The constraints are therefore added NOT VALID, which needs no scan,
    /// and ValidateCronTimerScheduleConstraints validates them afterwards outside a transaction, under a lock that lets
    /// reads and writes continue. Keeping that step out of this migration leaves this one a single transaction, which
    /// either commits with its history row or changes nothing. Existing rows satisfy all three constraints.
    /// </remarks>
    public partial class AddCronTimerSchedules : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ScheduleTimeZone",
                schema: "flowbit",
                table: "timer_subscriptions",
                type: "character varying(100)",
                maxLength: 100,
                nullable: true);

            // A cron schedule belongs to a timer start, which has no instance, token, or host; it runs in a named zone and
            // counts no business calendar. Other kinds have no zone. A NULL comparison would pass a CHECK, so COALESCE
            // treats it as a violation. A cron schedule ends only when it is cancelled, so the database refuses to
            // complete one, including from a Worker that cannot read cron.
            migrationBuilder.Sql("""
                ALTER TABLE flowbit.timer_subscriptions
                    DROP CONSTRAINT "CK_timer_subscriptions_schedule_kind";

                ALTER TABLE flowbit.timer_subscriptions
                    ADD CONSTRAINT "CK_timer_subscriptions_schedule_kind"
                    CHECK ("ScheduleKind" IN ('timeDate', 'timeDuration', 'timeCycle', 'cron')) NOT VALID;

                ALTER TABLE flowbit.timer_subscriptions
                    ADD CONSTRAINT "CK_timer_subscriptions_cron_shape"
                    CHECK (COALESCE(("ScheduleKind" = 'cron' AND btrim("ScheduleTimeZone") <> '' AND "ScheduleCalendar" IS NULL AND "InstanceId" IS NULL AND "TokenId" IS NULL AND "AttachedToNodeId" IS NULL) OR ("ScheduleKind" <> 'cron' AND "ScheduleTimeZone" IS NULL), FALSE)) NOT VALID;

                ALTER TABLE flowbit.timer_subscriptions
                    ADD CONSTRAINT "CK_timer_subscriptions_cron_not_completed"
                    CHECK ("ScheduleKind" <> 'cron' OR "Status" <> 'completed') NOT VALID;
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // A rollback must not delete cron history or strip zones to pass.
            migrationBuilder.Sql("""
                DO $flowbit$
                BEGIN
                    IF EXISTS
                    (
                        SELECT 1
                        FROM flowbit.timer_subscriptions
                        WHERE "ScheduleKind" = 'cron'
                    ) THEN
                        RAISE EXCEPTION USING
                            ERRCODE = '55000',
                            MESSAGE = 'Cannot remove cron timer schedules while cron timer subscriptions remain.',
                            DETAIL = 'No timer subscription was changed.',
                            HINT = 'Delete each workflow version with a cron timer start, which removes its subscriptions, or restore a compatible backup. A version that started instances cannot be deleted.';
                    END IF;
                END
                $flowbit$;
                """);

            migrationBuilder.DropCheckConstraint(
                name: "CK_timer_subscriptions_cron_not_completed",
                schema: "flowbit",
                table: "timer_subscriptions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_timer_subscriptions_cron_shape",
                schema: "flowbit",
                table: "timer_subscriptions");

            migrationBuilder.DropCheckConstraint(
                name: "CK_timer_subscriptions_schedule_kind",
                schema: "flowbit",
                table: "timer_subscriptions");

            migrationBuilder.DropColumn(
                name: "ScheduleTimeZone",
                schema: "flowbit",
                table: "timer_subscriptions");

            migrationBuilder.AddCheckConstraint(
                name: "CK_timer_subscriptions_schedule_kind",
                schema: "flowbit",
                table: "timer_subscriptions",
                sql: "\"ScheduleKind\" IN ('timeDate', 'timeDuration', 'timeCycle')");
        }
    }
}
