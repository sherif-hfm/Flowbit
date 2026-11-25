using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// AddSignalCatalog up, down, and up again (EVENT-04F), in a database of its own, because a
/// downgrade in the shared database would meet the rows other classes leave.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class SignalCatalogMigrationTests(PostgresApiFixture fixture)
{
    private const string Previous = "20261123194548_AddSignalBroadcastExpiry";

    [Fact]
    public async Task UpDownUp_BackfillsSeedsAndRefusesToDropEntries()
    {
        var databaseName = "signal_catalog_migration_" + Guid.NewGuid().ToString("N");
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

            // Before the catalog: a stored definition with signal events and an operator's mode.
            await migrator.MigrateAsync(Previous);
            var catchModel = SignalRuntimeTests.CatchModel("orders.cancelled");
            var definition = new WorkflowDefinitionEntity
            {
                Name = catchModel.Name, WorkflowKey = catchModel.Id, Version = 1, IsPublished = false,
                Definition = catchModel, CreatedAt = DateTimeOffset.UtcNow
            };
            db.WorkflowDefinitions.Add(definition);
            await db.SaveChangesAsync();
            await ExecuteAsync(connectionString,
                """INSERT INTO flowbit.engine_settings ("Namespace", "Key", "Value", "CreatedAt", "UpdatedAt") VALUES ('Signals', 'CatalogMode', 'required', now(), now())""");

            await migrator.MigrateAsync();
            Assert.Equal(
                [(definition.Id, 2, "orders.cancelled", "catch")],
                (await db.WorkflowDefinitionSignalNames.AsNoTracking()
                    .Select(row => new { row.WorkflowDefinitionId, row.NodeId, row.SignalName, row.Usage }).ToListAsync())
                .Select(row => (row.WorkflowDefinitionId, row.NodeId, row.SignalName, row.Usage)));
            Assert.Equal("required", await ModeAsync(db));

            db.SignalCatalogEntries.Add(new SignalCatalogEntryEntity
            {
                Name = "orders.cancelled",
                Fields = JsonSerializer.SerializeToElement(Array.Empty<object>()),
                ThrowRoles = JsonSerializer.SerializeToElement(Array.Empty<string>()),
                Status = SignalCatalogStatuses.Active,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
            var refused = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(Previous));
            Assert.Contains("Signal catalog entries exist", refused.MessageText, StringComparison.Ordinal);

            await db.SignalCatalogEntries.ExecuteDeleteAsync();
            await migrator.MigrateAsync(Previous);
            // An operator's value is kept on the way down; only the seeded value is removed.
            Assert.Equal("required", await ModeAsync(db));
            await ExecuteAsync(connectionString,
                """DELETE FROM flowbit.engine_settings WHERE "Namespace" = 'Signals' AND "Key" = 'CatalogMode'""");

            await migrator.MigrateAsync();
            Assert.Equal("optional", await ModeAsync(db));
            Assert.Single(await db.WorkflowDefinitionSignalNames.AsNoTracking().ToListAsync());

            await migrator.MigrateAsync(Previous);
            Assert.Null(await ModeAsync(db));
            await migrator.MigrateAsync();
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(admin, $"DROP DATABASE \"{databaseName}\" WITH (FORCE)");
        }
    }

    private static Task<string?> ModeAsync(AppDbContext db) =>
        db.EngineSettings.AsNoTracking()
            .Where(setting => setting.Namespace == "Signals" && setting.Key == "CatalogMode")
            .Select(setting => setting.Value)
            .SingleOrDefaultAsync();

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
}
