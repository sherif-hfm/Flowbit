using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// AddSignalDeliveryShares up, down, and up again (EVENT-04G), in a database of its own, because a
/// downgrade in the shared database would meet the rows other classes leave.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class SignalDeliveryShareMigrationTests(PostgresApiFixture fixture)
{
    private const string Previous = "20261124193857_AddSignalCatalog";

    [Fact]
    public async Task UpDownUp_KeepsLinksAsTheWholeDefinitionSeedsWithoutOverwritingAndRefusesWhileAShareJobRuns()
    {
        var databaseName = "signal_share_migration_" + Guid.NewGuid().ToString("N");
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
            Assert.Equal("2", await SettingAsync(db, "MaxDeliveryJobsPerDefinition"));
            Assert.Equal("500", await SettingAsync(db, "MinDeliveriesPerJob"));

            var model = SignalRuntimeTests.CatchModel("orders.cancelled");
            var definition = new WorkflowDefinitionEntity
            {
                Name = model.Name, WorkflowKey = model.Id, Version = 1, IsPublished = true,
                Definition = model, CreatedAt = DateTimeOffset.UtcNow
            };
            db.WorkflowDefinitions.Add(definition);
            await db.SaveChangesAsync();
            var broadcast = new SignalBroadcastEntity
            {
                SignalName = "orders.cancelled", WorkflowKey = model.Id, ThrownByKind = SignalThrowerKinds.User,
                ThrownBy = "migration-test", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow
            };
            db.SignalBroadcasts.Add(broadcast);
            await db.SaveChangesAsync();
            var whole = NewJob(definition, SignalBroadcastWriter.DeliverPhase);
            var share = NewJob(definition, SignalDeliveryShares.SharePhase);
            db.WorkflowJobs.AddRange(whole, share);
            await db.SaveChangesAsync();
            db.SignalBroadcastJobs.AddRange(
                NewLink(broadcast, definition, whole, 0, 1),
                NewLink(broadcast, definition, share, 2, 4));
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();

            // The check holds the share inside its count.
            foreach (var (index, count) in new[] { (4, 4), (-1, 4), (0, 0), (0, 33) })
            {
                var refused = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
                    $"""
                    INSERT INTO flowbit.signal_broadcast_jobs
                        ("BroadcastId", "WorkflowDefinitionId", "OriginalJobId", "Reason", "RequestedBy", "CreatedAt", "ShareIndex", "ShareCount")
                    VALUES ({broadcast.Id}, {definition.Id}, {900_000 + index * 100 + count}, 'throw', 'test', now(), {index}, {count})
                    """));
                Assert.Equal("CK_signal_broadcast_jobs_share", refused.ConstraintName);
            }

            // Down is refused while a share job is not finished.
            var blocked = await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync(Previous));
            Assert.Contains("Share jobs of split signal broadcasts are not finished", blocked.MessageText, StringComparison.Ordinal);
            await ExecuteAsync(connectionString,
                $"""UPDATE flowbit.workflow_jobs SET "Status" = 'completed', "CompletedAt" = now() WHERE "Id" = {share.Id}""");

            // An operator's value is kept on the way down; only the seeded values are removed.
            await ExecuteAsync(connectionString,
                """UPDATE flowbit.engine_settings SET "Value" = '1' WHERE "Namespace" = 'Signals' AND "Key" = 'MaxDeliveryJobsPerDefinition'""");
            await migrator.MigrateAsync(Previous);
            Assert.Equal("1", await ScalarAsync(connectionString,
                """SELECT "Value" FROM flowbit.engine_settings WHERE "Namespace" = 'Signals' AND "Key" = 'MaxDeliveryJobsPerDefinition'"""));
            Assert.Null(await ScalarAsync(connectionString,
                """SELECT "Value" FROM flowbit.engine_settings WHERE "Namespace" = 'Signals' AND "Key" = 'MinDeliveriesPerJob'"""));
            Assert.Null(await ScalarAsync(connectionString,
                """SELECT column_name::text FROM information_schema.columns WHERE table_schema = 'flowbit' AND table_name = 'signal_broadcast_jobs' AND column_name = 'ShareCount'"""));

            // Up again: the existing links read as the whole definition, and the seed keeps the operator's row.
            await migrator.MigrateAsync();
            var links = await db.SignalBroadcastJobs.AsNoTracking().Where(link => link.BroadcastId == broadcast.Id).ToListAsync();
            Assert.Equal(2, links.Count);
            Assert.All(links, link => Assert.Equal((0, 1), (link.ShareIndex, link.ShareCount)));
            Assert.Equal("1", await SettingAsync(db, "MaxDeliveryJobsPerDefinition"));
            Assert.Equal("500", await SettingAsync(db, "MinDeliveriesPerJob"));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(admin, $"DROP DATABASE \"{databaseName}\" WITH (FORCE)");
        }
    }

    private static WorkflowJobEntity NewJob(WorkflowDefinitionEntity definition, string phase) => new()
    {
        WorkflowDefinitionId = definition.Id,
        WorkflowKey = definition.WorkflowKey,
        ActivationId = Guid.NewGuid(),
        NodeId = 1,
        NodeName = "Start",
        NodeType = BpmnFlowNodeTypes.StartEvent,
        Kind = WorkflowJobKinds.SignalBroadcastDeliver,
        QueueClass = WorkflowJobClasses.Activity,
        Phase = phase,
        Status = WorkflowJobStatuses.Queued,
        MaxAttempts = 4,
        FailureHandling = WorkflowJobFailureHandling.RetryFirst,
        RetryDelays = [TimeSpan.FromSeconds(10)],
        DueAt = DateTimeOffset.UtcNow.AddYears(10),
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    private static SignalBroadcastJobEntity NewLink(
        SignalBroadcastEntity broadcast,
        WorkflowDefinitionEntity definition,
        WorkflowJobEntity job,
        int shareIndex,
        int shareCount) => new()
    {
        BroadcastId = broadcast.Id,
        WorkflowDefinitionId = definition.Id,
        JobId = job.Id,
        OriginalJobId = job.Id,
        ShareIndex = shareIndex,
        ShareCount = shareCount,
        Reason = SignalBroadcastJobReasons.Throw,
        RequestedBy = "migration-test",
        CreatedAt = DateTimeOffset.UtcNow
    };

    private static Task<string?> SettingAsync(AppDbContext db, string key) =>
        db.EngineSettings.AsNoTracking()
            .Where(setting => setting.Namespace == "Signals" && setting.Key == key)
            .Select(setting => setting.Value)
            .SingleOrDefaultAsync();

    private static async Task<string?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
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
