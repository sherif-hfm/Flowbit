using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class SharedVariableRevisionStateMigrationTests(PostgresApiFixture fixture)
{
    private const string PreviousMigration = "20261109141358_ValidateCronTimerScheduleConstraints";
    private const string TargetMigration = "RemoveSharedVariableRevisionState";
    private const string AllocateRevision = "SELECT flowbit.next_shared_variable_revision()";

    [Fact]
    public async Task FreshMigrationRemovesLegacyObjectsAndAllocatesFromOne()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, TargetMigration);
            await AssertSequenceOnlySchemaAsync(connectionString);
            Assert.Equal(1L, await ScalarAsync<long>(connectionString, AllocateRevision));
            Assert.Equal(2L, await ScalarAsync<long>(connectionString, AllocateRevision));
        });
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(100L)]
    public async Task LegacyUpgradePreservesDataAndUsesTheGreatestRevisionFloor(long legacyCounter)
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, PreviousMigration);
            var persistedRevision = await CreateDataAsync(connectionString);
            Assert.Equal("legacy", await ScalarAsync<string>(connectionString,
                "SELECT \"AllocatorMode\" FROM flowbit.shared_variable_revision_state WHERE \"Id\" = 1"));
            await ExecuteAsync(connectionString,
                "UPDATE flowbit.shared_variable_revision_state SET \"LastRevision\" = @revision WHERE \"Id\" = 1",
                new NpgsqlParameter("revision", legacyCounter));
            var before = await ReadSharedDataAsync(connectionString);

            await MigrateAsync(connectionString, TargetMigration);

            await AssertSequenceOnlySchemaAsync(connectionString);
            Assert.Equal(before, await ReadSharedDataAsync(connectionString));
            Assert.Equal(Math.Max(legacyCounter, persistedRevision) + 1,
                await ScalarAsync<long>(connectionString, AllocateRevision));
            var replay = await WithRepositoryAsync(connectionString,
                repository => repository.CreateAsync(CreateCommand(), CancellationToken.None));
            Assert.True(replay.IsIdempotentReplay);
            Assert.Equal(before, await ReadSharedDataAsync(connectionString));
        });
    }

    [Fact]
    public async Task SequenceUpgradeDoesNotReuseAllocationsFromRolledBackTransactions()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, PreviousMigration);
            var persistedRevision = await CreateDataAsync(connectionString);
            await ExecuteAsync(connectionString, "SELECT flowbit.cutover_shared_variable_revision_sequence()");
            var committedAllocation = await ScalarAsync<long>(connectionString, AllocateRevision);
            var rolledBackAllocation = await AllocateAndRollBackAsync(connectionString);
            Assert.True(committedAllocation > persistedRevision);
            Assert.True(rolledBackAllocation > committedAllocation);
            var before = await ReadSharedDataAsync(connectionString);

            await MigrateAsync(connectionString, TargetMigration);

            Assert.Equal(before, await ReadSharedDataAsync(connectionString));
            Assert.Equal(rolledBackAllocation + 1, await ScalarAsync<long>(connectionString, AllocateRevision));
            await AssertSequenceOnlySchemaAsync(connectionString);
        });
    }

    [Fact]
    public async Task UpgradeDowngradeAndReupgradePreserveDataAndSequenceProgress()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, PreviousMigration);
            await CreateDataAsync(connectionString);
            var before = await ReadSharedDataAsync(connectionString);
            await MigrateAsync(connectionString, TargetMigration);
            var beforeDowngrade = await AllocateAndRollBackAsync(connectionString);

            await MigrateAsync(connectionString, PreviousMigration);

            Assert.Equal(before, await ReadSharedDataAsync(connectionString));
            Assert.Equal("sequence", await ScalarAsync<string>(connectionString,
                "SELECT \"AllocatorMode\" FROM flowbit.shared_variable_revision_state WHERE \"Id\" = 1"));
            Assert.Equal(beforeDowngrade, await ScalarAsync<long>(connectionString,
                "SELECT \"LastRevision\" FROM flowbit.shared_variable_revision_state WHERE \"Id\" = 1"));
            Assert.Equal(beforeDowngrade,
                await ScalarAsync<long>(connectionString, "SELECT flowbit.cutover_shared_variable_revision_sequence()"));
            var guard = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
                "UPDATE flowbit.shared_variable_revision_state SET \"LastRevision\" = \"LastRevision\" + 1 WHERE \"Id\" = 1"));
            Assert.Equal("55000", guard.SqlState);
            var afterDowngrade = await ScalarAsync<long>(connectionString, AllocateRevision);
            Assert.Equal(beforeDowngrade + 1, afterDowngrade);

            await MigrateAsync(connectionString, TargetMigration);

            await AssertSequenceOnlySchemaAsync(connectionString);
            Assert.Equal(before, await ReadSharedDataAsync(connectionString));
            Assert.Equal(afterDowngrade + 1, await ScalarAsync<long>(connectionString, AllocateRevision));
        });
    }

    private static async Task<long> CreateDataAsync(string connectionString) =>
        await WithRepositoryAsync(connectionString, async repository =>
        {
            var created = await repository.CreateAsync(CreateCommand(), CancellationToken.None);
            var updated = await repository.WriteAsync(new SharedVariableWriteCommand(
                created.Variable.Key,
                JsonSerializer.SerializeToElement(20),
                DeleteValue: false,
                ExpectedRevision: created.Variable.Revision,
                Caller(),
                SharedVariableSources.System,
                RequestId: "write-request",
                RequestFingerprint: "write-value-20",
                ExpectedValueRevision: created.Variable.ValueRevision), CancellationToken.None);
            Assert.NotNull(updated);
            return updated.Revision.Revision;
        });

    private static SharedVariableCreateCommand CreateCommand() => new(
        "tests.allocator-migration",
        "number",
        IsArray: false,
        Nullable: false,
        Validation: null,
        Description: "Preserve catalog, current value, history, and requests",
        HasValue: true,
        JsonSerializer.SerializeToElement(10),
        Caller(),
        SharedVariableSources.System,
        RequestId: "create-request",
        RequestFingerprint: "create-value-10",
        Reason: null);

    private static SharedVariableCallerRecord Caller() => new(
        SharedVariableCallerKinds.System, "allocator-migration-test", [], []);

    private static async Task<T> WithRepositoryAsync<T>(
        string connectionString, Func<SharedVariableRepository, Task<T>> action)
    {
        var builder = new NpgsqlDataSourceBuilder(connectionString);
        builder.EnableDynamicJson();
        await using var dataSource = builder.Build();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(dataSource, FlowbitDatabase.ConfigureProvider).Options;
        await using var db = new AppDbContext(options);
        return await action(new SharedVariableRepository(db));
    }

    private static Task<string> ReadSharedDataAsync(string connectionString) => ScalarAsync<string>(connectionString,
        """
        SELECT jsonb_build_object(
            'catalog', (SELECT jsonb_agg(to_jsonb(v) ORDER BY "Id") FROM flowbit.shared_variables v),
            'values', (SELECT jsonb_agg(to_jsonb(v) ORDER BY "SharedVariableId") FROM flowbit.shared_variable_current_values v),
            'history', (SELECT jsonb_agg(to_jsonb(v) ORDER BY "Id") FROM flowbit.shared_variable_revisions v),
            'requests', (SELECT jsonb_agg(to_jsonb(v) ORDER BY "Id") FROM flowbit.shared_variable_requests v))::text
        """);

    private static async Task AssertSequenceOnlySchemaAsync(string connectionString)
    {
        Assert.True(await ScalarAsync<bool>(connectionString,
            "SELECT to_regclass('flowbit.shared_variable_revision_state') IS NULL"));
        Assert.True(await ScalarAsync<bool>(connectionString,
            "SELECT to_regclass('flowbit.shared_variable_revision_seq') IS NOT NULL"));
        Assert.True(await ScalarAsync<bool>(connectionString,
            "SELECT to_regprocedure('flowbit.next_shared_variable_revision()') IS NOT NULL"));
        Assert.True(await ScalarAsync<bool>(connectionString,
            "SELECT to_regprocedure('flowbit.cutover_shared_variable_revision_sequence()') IS NULL"));
        Assert.True(await ScalarAsync<bool>(connectionString,
            "SELECT to_regprocedure('flowbit.guard_legacy_shared_variable_revision_update()') IS NULL"));
        Assert.Equal(0L, await ScalarAsync<long>(connectionString,
            "SELECT count(*) FROM pg_trigger WHERE tgname = 'tr_shared_variable_revision_state_guard_legacy_update'"));
    }

    private static async Task<long> AllocateAndRollBackAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(AllocateRevision, connection, transaction);
        var revision = (long)(await command.ExecuteScalarAsync())!;
        await transaction.RollbackAsync();
        return revision;
    }

    private static async Task ExecuteAsync(string connectionString, string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task WithIsolatedDatabaseAsync(Func<string, Task> test)
    {
        var databaseName = "revision_state_" + Guid.NewGuid().ToString("N");
        var adminBuilder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "postgres" };
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName };
            await test(builder.ConnectionString);
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

    private static async Task MigrateAsync(string connectionString, string targetMigration)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, FlowbitDatabase.ConfigureProvider).Options;
        await using var context = new AppDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(targetMigration);
    }
}
