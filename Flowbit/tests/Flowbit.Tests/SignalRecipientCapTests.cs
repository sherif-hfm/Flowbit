using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// The hard cap on a broadcast's recipients, raised to 250,000 by RaiseSignalBroadcastRecipientCap
/// (EVENT-04G) after every statement that touches a whole broadcast passed its gate at that size.
/// </summary>
public sealed class SignalRecipientCapSettingTests
{
    [Theory]
    [InlineData("100001", 100_001)]
    [InlineData("250000", 250_000)]
    [InlineData("250001", 250_000)]
    [InlineData("2147483647", 250_000)]
    [InlineData(null, 10_000)]
    public void MaxSubscriptionsPerBroadcast_IsCappedAt250000(string? value, int expected) =>
        Assert.Equal(expected, SignalSettings.ParseLimit(
            value,
            SignalSettings.DefaultMaxSubscriptionsPerBroadcast,
            SignalSettings.MaxSubscriptionsPerBroadcastCap));

    [Fact]
    public void TheDatabaseCheckHoldsTheSameCapAsTheSetting()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=localhost;Database=model-only", FlowbitDatabase.ConfigureProvider)
            .Options);
        var check = db.GetService<IDesignTimeModel>().Model.FindEntityType(typeof(SignalBroadcastEntity))!
            .GetCheckConstraints()
            .Single(constraint => constraint.ModelName == "CK_signal_broadcasts_counts");
        Assert.StartsWith(
            $"\"TotalDeliveryCount\" BETWEEN 0 AND {SignalSettings.MaxSubscriptionsPerBroadcastCap} ",
            check.Sql,
            StringComparison.Ordinal);
    }
}

/// <summary>
/// RaiseSignalBroadcastRecipientCap up, down, and up again, in a database of its own, because a
/// downgrade in the shared database would meet the rows other classes leave.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class SignalRecipientCapMigrationTests(PostgresApiFixture fixture)
{
    private const string Previous = "20261126122213_AddSignalDeliveryShares";

    [Fact]
    public async Task UpDownUp_HoldsTheCapRefusesDownAboveTheOldOneAndKeepsAnOperatorsDescription()
    {
        var databaseName = "signal_cap_migration_" + Guid.NewGuid().ToString("N");
        var admin = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "postgres" }.ConnectionString;
        await ExecuteAsync(admin, $"CREATE DATABASE \"{databaseName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName }.ConnectionString;
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.EnableDynamicJson();
        await using var dataSource = dataSourceBuilder.Build();
        try
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(dataSource, FlowbitDatabase.ConfigureProvider)
                .Options;
            await using var db = new AppDbContext(options);
            var migrator = db.GetService<IMigrator>();

            await migrator.MigrateAsync();
            Assert.Contains("values above 250000 are capped", await DescriptionAsync(connectionString), StringComparison.Ordinal);

            // The check takes a broadcast of 250,000 and refuses one more.
            var large = await InsertBroadcastAsync(connectionString, 250_000);
            var refused = await Assert.ThrowsAsync<PostgresException>(() => InsertBroadcastAsync(connectionString, 250_001));
            Assert.Equal("CK_signal_broadcasts_counts", refused.ConstraintName);

            // Down is refused while a broadcast is above the older limit.
            var blocked = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(Previous));
            Assert.Contains("A signal broadcast has more than 100,000 deliveries", blocked.MessageText, StringComparison.Ordinal);
            await ExecuteAsync(connectionString, $"""DELETE FROM flowbit.signal_broadcasts WHERE "Id" = {large}""");

            // Down: the older limit and description again.
            await migrator.MigrateAsync(Previous);
            Assert.Contains("values above 100000 are capped", await DescriptionAsync(connectionString), StringComparison.Ordinal);
            await InsertBroadcastAsync(connectionString, 100_000);
            var old = await Assert.ThrowsAsync<PostgresException>(() => InsertBroadcastAsync(connectionString, 100_001));
            Assert.Equal("CK_signal_broadcasts_counts", old.ConstraintName);

            // Up again keeps a description an operator wrote.
            await ExecuteAsync(connectionString,
                """UPDATE flowbit.engine_settings SET "Description" = 'Ours.' WHERE "Namespace" = 'Signals' AND "Key" = 'MaxSubscriptionsPerBroadcast'""");
            await migrator.MigrateAsync();
            Assert.Equal("Ours.", await DescriptionAsync(connectionString));
            await InsertBroadcastAsync(connectionString, 250_000);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(admin, $"DROP DATABASE \"{databaseName}\" WITH (FORCE)");
        }
    }

    private static async Task<long> InsertBroadcastAsync(string connectionString, int total)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO flowbit.signal_broadcasts
                ("SignalName", "WorkflowKey", "Status", "TotalDeliveryCount", "DeliveredCount", "SkippedCount",
                 "FailedCount", "CancelledCount", "RecipientsTruncated", "RecipientsJson",
                 "ThrownByKind", "ThrownBy", "ThrownByRolesJson", "CreatedAt", "UpdatedAt", "CompletedAt")
            VALUES ('cap.check', 'cap-check', '{SignalBroadcastStatuses.Completed}', {total}, {total}, 0, 0, 0, false, '[]',
                    '{SignalThrowerKinds.User}', 'cap-test', '[]', now(), now(), now())
            RETURNING "Id"
            """,
            connection);
        return (long)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<string?> DescriptionAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """SELECT "Description" FROM flowbit.engine_settings WHERE "Namespace" = 'Signals' AND "Key" = 'MaxSubscriptionsPerBroadcast'""",
            connection);
        return await command.ExecuteScalarAsync() as string;
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
