using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Flowbit.Infrastructure.Data;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class CronTimerScheduleMigrationTests(PostgresApiFixture fixture)
{
    private const string PreviousMigration = "20261108120000_ConsolidateLifecycleCaptureSettings";
    private const string TargetMigration = "20261109080332_AddCronTimerSchedules";
    private const string ValidationMigration = "20261109141358_ValidateCronTimerScheduleConstraints";

    private static readonly string[] CronConstraints =
    [
        "CK_timer_subscriptions_schedule_kind",
        "CK_timer_subscriptions_cron_shape",
        "CK_timer_subscriptions_cron_not_completed"
    ];

    [Fact]
    public async Task UpgradeKeepsExistingTimersAndValidatesEveryConstraint()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, PreviousMigration);
            var definitionId = await InsertDefinitionAsync(connectionString);
            await InsertSubscriptionAsync(connectionString, definitionId, 1, "timeCycle", "R/P1D", "active");
            await InsertSubscriptionAsync(connectionString, definitionId, 2, "timeDuration", "PT1H", "paused");
            await InsertSubscriptionAsync(connectionString, definitionId, 3, "timeDate", "2026-12-01T08:00:00Z", "completed");

            // The schema change commits in one transaction; the scan that validates its constraints is the next migration.
            await MigrateAsync(connectionString, TargetMigration);
            foreach (var constraint in CronConstraints)
            {
                Assert.False(await ConstraintValidatedAsync(connectionString, constraint), constraint);
            }

            await MigrateAsync(connectionString, ValidationMigration);

            Assert.Equal(
                ["timeCycle|R/P1D|active|", "timeDuration|PT1H|paused|", "timeDate|2026-12-01T08:00:00Z|completed|"],
                await ReadSubscriptionsAsync(connectionString));
            foreach (var constraint in CronConstraints)
            {
                Assert.True(await ConstraintValidatedAsync(connectionString, constraint), constraint);
            }

            // A missing zone is refused, not passed as an unknown comparison result.
            await AssertRefusedAsync(connectionString, definitionId, 10, "cron", null, null, "active", "CK_timer_subscriptions_cron_shape");
            await AssertRefusedAsync(connectionString, definitionId, 11, "cron", "  ", null, "active", "CK_timer_subscriptions_cron_shape");
            await AssertRefusedAsync(connectionString, definitionId, 12, "cron", "Europe/London", "weekdays", "active", "CK_timer_subscriptions_cron_shape");
            await AssertRefusedAsync(connectionString, definitionId, 13, "timeCycle", "Europe/London", null, "active", "CK_timer_subscriptions_cron_shape");
            await AssertRefusedAsync(connectionString, definitionId, 14, "cron", "Europe/London", null, "completed", "CK_timer_subscriptions_cron_not_completed");
            await AssertRefusedAsync(connectionString, definitionId, 15, "interval", null, null, "active", "CK_timer_subscriptions_schedule_kind");

            await InsertCronSubscriptionAsync(connectionString, definitionId, 20, "Europe/London", "active");
            await InsertCronSubscriptionAsync(connectionString, definitionId, 21, "Etc/UTC", "cancelled");
            Assert.Equal(5, (await ReadSubscriptionsAsync(connectionString)).Count);
        });
    }

    [Fact]
    public async Task AnInterruptedValidationIsFinishedByApplyingItAgain()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, PreviousMigration);
            var definitionId = await InsertDefinitionAsync(connectionString);
            // A row the new kind constraint refuses stops the validation after AddCronTimerSchedules has committed, as
            // a killed process or a statement timeout would.
            await ExecuteAsync(connectionString, """
                ALTER TABLE flowbit.timer_subscriptions DROP CONSTRAINT "CK_timer_subscriptions_schedule_kind"
                """);
            await InsertSubscriptionAsync(connectionString, definitionId, 1, "interval", "R/P1D", "active");
            await ExecuteAsync(connectionString, """
                ALTER TABLE flowbit.timer_subscriptions ADD CONSTRAINT "CK_timer_subscriptions_schedule_kind"
                    CHECK ("ScheduleKind" IN ('timeDate', 'timeDuration', 'timeCycle')) NOT VALID
                """);

            var error = await Assert.ThrowsAnyAsync<Exception>(() => MigrateAsync(connectionString, ValidationMigration));
            var stopped = error as PostgresException ?? error.InnerException as PostgresException;
            Assert.Equal(PostgresErrorCodes.CheckViolation, stopped?.SqlState);
            Assert.True(await MigrationAppliedAsync(connectionString, TargetMigration));
            Assert.False(await MigrationAppliedAsync(connectionString, ValidationMigration));

            await ExecuteAsync(connectionString, """DELETE FROM flowbit.timer_subscriptions WHERE "ScheduleKind" = 'interval'""");
            await MigrateAsync(connectionString, ValidationMigration);

            Assert.True(await MigrationAppliedAsync(connectionString, ValidationMigration));
            foreach (var constraint in CronConstraints)
            {
                Assert.True(await ConstraintValidatedAsync(connectionString, constraint), constraint);
            }
        });
    }

    [Fact]
    public async Task DowngradeRefusesWhileAnyCronRowRemainsAndChangesNothing()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, ValidationMigration);
            var definitionId = await InsertDefinitionAsync(connectionString);
            await InsertSubscriptionAsync(connectionString, definitionId, 1, "timeCycle", "R/P1D", "active");
            // A cancelled schedule is history too, so it blocks the rollback as an active one does.
            await InsertCronSubscriptionAsync(connectionString, definitionId, 2, "Europe/London", "cancelled");

            var error = await Assert.ThrowsAnyAsync<Exception>(() => MigrateAsync(connectionString, PreviousMigration));
            var refused = error as PostgresException ?? error.InnerException as PostgresException;
            Assert.NotNull(refused);
            Assert.Equal("55000", refused!.SqlState);
            Assert.True(await MigrationAppliedAsync(connectionString, TargetMigration));
            Assert.Equal(
                ["timeCycle|R/P1D|active|", "cron|0 8 * * 1-5|cancelled|Europe/London"],
                await ReadSubscriptionsAsync(connectionString));

            await ExecuteAsync(connectionString, """DELETE FROM flowbit.timer_subscriptions WHERE "ScheduleKind" = 'cron'""");
            await MigrateAsync(connectionString, PreviousMigration);

            Assert.False(await MigrationAppliedAsync(connectionString, TargetMigration));
            Assert.False(await TimerColumnExistsAsync(connectionString, "ScheduleTimeZone"));
            Assert.Equal(["timeCycle|R/P1D|active"], await ReadSubscriptionsAsync(connectionString, withZone: false));
            await AssertRefusedAsync(connectionString, definitionId, 3, "cron", null, null, "active", "CK_timer_subscriptions_schedule_kind", withZone: false);
        });
    }

    private static async Task<long> InsertDefinitionAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO flowbit.workflow_definitions
                ("Name", "Version", "Definition", "IsPublished", "WorkflowKey", "IsDefault")
            VALUES ('Cron migration', 1, '{}'::jsonb, false, 'cron-migration', false)
            RETURNING "Id"
            """,
            connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static Task InsertSubscriptionAsync(
        string connectionString, long definitionId, int nodeId, string kind, string expression, string status) =>
        InsertAsync(connectionString, definitionId, nodeId, kind, expression, zone: null, calendar: null, status, withZone: false);

    private static Task InsertCronSubscriptionAsync(string connectionString, long definitionId, int nodeId, string zone, string status) =>
        InsertAsync(connectionString, definitionId, nodeId, "cron", "0 8 * * 1-5", zone, calendar: null, status, withZone: true);

    private static async Task AssertRefusedAsync(
        string connectionString,
        long definitionId,
        int nodeId,
        string kind,
        string? zone,
        string? calendar,
        string status,
        string constraint,
        bool withZone = true)
    {
        var refused = await Assert.ThrowsAsync<PostgresException>(() =>
            InsertAsync(connectionString, definitionId, nodeId, kind, "0 8 * * 1-5", zone, calendar, status, withZone));
        Assert.Equal(PostgresErrorCodes.CheckViolation, refused.SqlState);
        Assert.Equal(constraint, refused.ConstraintName);
    }

    private static async Task InsertAsync(
        string connectionString,
        long definitionId,
        int nodeId,
        string kind,
        string expression,
        string? zone,
        string? calendar,
        string status,
        bool withZone)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO flowbit.timer_subscriptions
                ("WorkflowDefinitionId", "WorkflowKey", "ActivationId", "TimerNodeId", "TimerNodeName", "ScheduleKind",
                 "ScheduleExpression", "ScheduleCalendar", {(withZone ? "\"ScheduleTimeZone\", " : "")}"CancelActivity",
                 "Status", "NextDueAt", "Occurrence", "CompletedAt")
            VALUES (@definition, 'cron-migration', gen_random_uuid(), @node, 'Start', @kind,
                    @expression, @calendar, {(withZone ? "@zone, " : "")}false,
                    @status, now(), 0, CASE WHEN @status IN ('completed', 'cancelled') THEN now() END)
            """,
            connection);
        command.Parameters.AddWithValue("definition", definitionId);
        command.Parameters.AddWithValue("node", nodeId);
        command.Parameters.AddWithValue("kind", kind);
        command.Parameters.AddWithValue("expression", expression);
        command.Parameters.AddWithValue("calendar", (object?)calendar ?? DBNull.Value);
        command.Parameters.AddWithValue("status", status);
        if (withZone)
        {
            command.Parameters.AddWithValue("zone", (object?)zone ?? DBNull.Value);
        }

        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<List<string>> ReadSubscriptionsAsync(string connectionString, bool withZone = true)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            withZone
                ? """
                  SELECT concat_ws('|', "ScheduleKind", "ScheduleExpression", "Status", coalesce("ScheduleTimeZone", ''))
                  FROM flowbit.timer_subscriptions
                  ORDER BY "Id"
                  """
                : """
                  SELECT concat_ws('|', "ScheduleKind", "ScheduleExpression", "Status")
                  FROM flowbit.timer_subscriptions
                  ORDER BY "Id"
                  """,
            connection);
        var rows = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(reader.GetString(0));
        }

        return rows;
    }

    private static async Task<bool> ConstraintValidatedAsync(string connectionString, string constraint)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT convalidated
            FROM pg_constraint
            WHERE conname = @constraint
              AND conrelid = 'flowbit.timer_subscriptions'::regclass
            """,
            connection);
        command.Parameters.AddWithValue("constraint", constraint);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> MigrationAppliedAsync(string connectionString, string migration)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM flowbit."__EFMigrationsHistory"
                WHERE "MigrationId" = @migration)
            """,
            connection);
        command.Parameters.AddWithValue("migration", migration);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<bool> TimerColumnExistsAsync(string connectionString, string column)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'flowbit'
                  AND table_name = 'timer_subscriptions'
                  AND column_name = @column)
            """,
            connection);
        command.Parameters.AddWithValue("column", column);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task MigrateAsync(string connectionString, string targetMigration)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, FlowbitDatabase.ConfigureProvider)
            .Options;
        await using var context = new AppDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    private async Task WithIsolatedDatabaseAsync(Func<string, Task> test)
    {
        var databaseName = "cron_timer_migration_" + Guid.NewGuid().ToString("N");
        var adminBuilder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = "postgres"
        };
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        var databaseBuilder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = databaseName
        };
        try
        {
            await test(databaseBuilder.ConnectionString);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{databaseName}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
