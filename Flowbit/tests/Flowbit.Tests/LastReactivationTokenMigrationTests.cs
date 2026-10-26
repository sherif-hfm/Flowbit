using Flowbit.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class LastReactivationTokenMigrationTests(PostgresApiFixture fixture)
{
    private const string PreviousMigration = "20261021153025_AddLocalIdentity";

    [Fact]
    public async Task MigrationRecordsEachInstancesNewestReactivationTokenFromItsHistory()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, PreviousMigration);
            var seeded = await SeedAsync(connectionString);

            await MigrateAsync(connectionString);

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var command = new NpgsqlCommand("""
                SELECT "Id", "LastReactivationTokenId"
                FROM flowbit.workflow_instances
                ORDER BY "Id"
                """, connection);
            var recorded = new Dictionary<long, long?>();
            await using (var reader = await command.ExecuteReaderAsync())
            {
                while (await reader.ReadAsync())
                {
                    recorded[reader.GetInt64(0)] = reader.IsDBNull(1) ? null : reader.GetInt64(1);
                }
            }

            Assert.Equal(seeded.NewestReactivationTokenId, recorded[seeded.ReactivatedInstanceId]);
            Assert.Null(recorded[seeded.NeverReactivatedInstanceId]);
        });
    }

    private sealed record SeededInstances(
        long ReactivatedInstanceId,
        long NewestReactivationTokenId,
        long NeverReactivatedInstanceId);

    // One instance reactivated twice and one never reactivated, as they look before the migration.
    private static async Task<SeededInstances> SeedAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();

        await using var definition = new NpgsqlCommand("""
            INSERT INTO flowbit.workflow_definitions
                ("Name", "WorkflowKey", "Version", "Definition", "IsPublished", "IsDefault", "CreatedAt")
            VALUES
                ('Reactivation token migration', 'reactivation-token', 1, @definition, true, false, now())
            RETURNING "Id"
            """, connection, transaction);
        definition.Parameters.AddWithValue(
            "definition",
            NpgsqlDbType.Jsonb,
            """
            {"id":"reactivation-token","name":"Reactivation token migration","flowNodes":[],"sequenceFlows":[],"variables":[],"lanes":[]}
            """);
        var definitionId = (long)(await definition.ExecuteScalarAsync())!;

        async Task<long> InstanceAsync(string status)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO flowbit.workflow_instances
                    ("WorkflowDefinitionId", "WorkflowKey", "Status", "StartedBy", "CreatedAt", "UpdatedAt")
                VALUES (@definitionId, 'reactivation-token', @status, 'starter', now(), now())
                RETURNING "Id"
                """, connection, transaction);
            command.Parameters.AddWithValue("definitionId", definitionId);
            command.Parameters.AddWithValue("status", status);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        async Task<long> TokenAsync(long instanceId, string status)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO flowbit.execution_tokens
                    ("InstanceId", "NodeId", "NodeName", "NodeType", "Status", "CreatedAt", "UpdatedAt")
                VALUES (@instanceId, 2, 'Review', 'userTask', @status, now(), now())
                RETURNING "Id"
                """, connection, transaction);
            command.Parameters.AddWithValue("instanceId", instanceId);
            command.Parameters.AddWithValue("status", status);
            return (long)(await command.ExecuteScalarAsync())!;
        }

        async Task HistoryAsync(long instanceId, long tokenId, string note)
        {
            await using var command = new NpgsqlCommand("""
                INSERT INTO flowbit.instance_history
                    ("InstanceId", "WorkflowDefinitionId", "TokenId", "FromStepId", "ToStepId",
                     "PerformedBy", "Note", "PerformedAt")
                VALUES (@instanceId, @definitionId, @tokenId, 1, 2, 'operator', @note, now())
                """, connection, transaction);
            command.Parameters.AddWithValue("instanceId", instanceId);
            command.Parameters.AddWithValue("definitionId", definitionId);
            command.Parameters.AddWithValue("tokenId", tokenId);
            command.Parameters.AddWithValue("note", note);
            await command.ExecuteNonQueryAsync();
        }

        var reactivated = await InstanceAsync("running");
        var original = await TokenAsync(reactivated, "cancelled");
        await HistoryAsync(reactivated, original, "start");
        var firstReactivation = await TokenAsync(reactivated, "cancelled");
        await HistoryAsync(reactivated, firstReactivation, "instanceReactivated");
        var secondReactivation = await TokenAsync(reactivated, "active");
        await HistoryAsync(reactivated, secondReactivation, "instanceReactivated");

        var neverReactivated = await InstanceAsync("running");
        await HistoryAsync(neverReactivated, await TokenAsync(neverReactivated, "active"), "start");

        await transaction.CommitAsync();
        return new SeededInstances(reactivated, secondReactivation, neverReactivated);
    }

    private async Task WithIsolatedDatabaseAsync(Func<string, Task> test)
    {
        var databaseName = "reactivation_token_" + Guid.NewGuid().ToString("N");
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

    private static async Task MigrateAsync(string connectionString, string? targetMigration = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, FlowbitDatabase.ConfigureProvider)
            .Options;
        await using var context = new AppDbContext(options);
        var migrator = context.GetService<IMigrator>();
        await migrator.MigrateAsync(targetMigration);
    }
}
