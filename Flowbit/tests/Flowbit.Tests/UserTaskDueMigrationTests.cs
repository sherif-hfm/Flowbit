using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Flowbit.Infrastructure.Data;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class UserTaskDueMigrationTests(PostgresApiFixture fixture)
{
    private const string PreviousMigration = "20261026114702_AddInstanceLastReactivationToken";
    private const string TargetMigration = "20261027075021_AddUserTaskDueDateAndPriority";

    [Fact]
    public async Task UpgradeSeedsTheThresholdAndDowngradeRemovesOnlyAnUnchangedRow()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, PreviousMigration);
            await MigrateAsync(connectionString, TargetMigration);
            Assert.Equal("P1D", await ReadThresholdAsync(connectionString));

            await MigrateAsync(connectionString, PreviousMigration);
            Assert.Null(await ReadThresholdAsync(connectionString));
            Assert.False(await UserTaskColumnExistsAsync(connectionString, "DueAt"));

            await MigrateAsync(connectionString, TargetMigration);
            await ExecuteAsync(
                connectionString,
                """
                UPDATE flowbit.engine_settings
                SET "Value" = 'PT8H'
                WHERE "Namespace" = 'UserTasks' AND "Key" = 'DueSoonThreshold'
                """);
            await MigrateAsync(connectionString, PreviousMigration);
            Assert.Equal("PT8H", await ReadThresholdAsync(connectionString));
        });
    }

    [Fact]
    public async Task UpgradeKeepsAnExistingThreshold()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, PreviousMigration);
            await ExecuteAsync(
                connectionString,
                """
                INSERT INTO flowbit.engine_settings
                    ("Namespace", "Key", "Value", "CreatedAt", "UpdatedAt")
                VALUES ('UserTasks', 'DueSoonThreshold', 'PT12H', now(), now())
                """);

            await MigrateAsync(connectionString, TargetMigration);

            Assert.Equal("PT12H", await ReadThresholdAsync(connectionString));
            Assert.True(await UserTaskColumnExistsAsync(connectionString, "DueAt"));
            Assert.True(await UserTaskColumnExistsAsync(connectionString, "Priority"));
        });
    }

    private static async Task<string?> ReadThresholdAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT "Value"
            FROM flowbit.engine_settings
            WHERE "Namespace" = 'UserTasks' AND "Key" = 'DueSoonThreshold'
            """,
            connection);
        return (string?)await command.ExecuteScalarAsync();
    }

    private static async Task<bool> UserTaskColumnExistsAsync(string connectionString, string column)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT EXISTS (
                SELECT 1
                FROM information_schema.columns
                WHERE table_schema = 'flowbit'
                  AND table_name = 'user_tasks'
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
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
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
        var databaseName = "user_task_due_" + Guid.NewGuid().ToString("N");
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
