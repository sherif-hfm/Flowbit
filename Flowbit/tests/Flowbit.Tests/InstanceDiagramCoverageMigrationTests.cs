using System.Diagnostics;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;
using Xunit.Abstractions;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class InstanceDiagramCoverageMigrationTests(PostgresApiFixture fixture, ITestOutputHelper output)
{
    private const string Previous = "20261109144226_RemoveSharedVariableRevisionState";
    private const string Columns = "20261110075414_AddInstanceNodeActivityCoverage";
    private const string Classification = "20261110075435_ClassifyInstanceNodeActivityCoverage";

    [Fact]
    public async Task ClassificationIsBatchedConservativeAndPreservesOldAndNewWriterDefaults()
    {
        await WithDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, Previous);
            await ExecuteAsync(connectionString, """
                INSERT INTO flowbit.workflow_definitions
                    ("Name", "WorkflowKey", "Version", "Definition", "IsPublished", "IsDefault", "CreatedAt")
                VALUES ('Coverage classification', 'coverage-classification', 1,
                    '{"id":"coverage-classification","name":"Coverage classification","flowNodes":[],"sequenceFlows":[],"variables":[],"lanes":[]}',
                    TRUE, FALSE, now());
                INSERT INTO flowbit.workflow_instances
                    ("WorkflowDefinitionId", "WorkflowKey", "Status", "StartedBy", "CreatedAt", "UpdatedAt", "FinishedAt", "HistoryPrunedAt")
                SELECT (SELECT "Id" FROM flowbit.workflow_definitions), 'coverage-classification', 'completed', n::text,
                    now() - interval '2 days', now() - interval '1 day', now() - interval '1 day',
                    CASE WHEN n = 5 THEN now() - interval '1 day' END
                FROM generate_series(1, 2008) n;
                INSERT INTO flowbit.execution_tokens
                    ("InstanceId", "NodeId", "NodeName", "NodeType", "Status", "CreatedAt", "UpdatedAt")
                SELECT "Id", 1, 'Start or legacy wait', 'userTask', 'completed', "CreatedAt", "UpdatedAt"
                FROM flowbit.workflow_instances;
                INSERT INTO flowbit.node_executions
                    ("InstanceId", "WorkflowDefinitionId", "ExecutionTokenId", "NodeId", "NodeName", "NodeType",
                     "ExecutionKind", "Status", "CompletionReason", "CreatedAt", "StartedAt", "UpdatedAt", "CompletedAt", "IsCutoverSeeded")
                SELECT i."Id", i."WorkflowDefinitionId", t."Id", 1, 'Recorded entry',
                    CASE i."StartedBy" WHEN '2' THEN 'messageStartEvent' WHEN '3' THEN 'timerStartEvent'
                        WHEN '4' THEN 'userTask' WHEN '8' THEN 'userTask' ELSE 'startEvent' END,
                    'node', 'completed', 'normal', i."CreatedAt", i."CreatedAt", i."UpdatedAt", i."UpdatedAt",
                    i."StartedBy" IN ('4','7')
                FROM flowbit.workflow_instances i JOIN flowbit.execution_tokens t ON t."InstanceId" = i."Id"
                WHERE i."StartedBy" <> '6';
                """);
            await MigrateAsync(connectionString, Columns);
            Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM flowbit.workflow_instances WHERE \"NodeActivityFromCreation\""));
            // Represents a still-running old API writer, which knows neither new column.
            await ExecuteAsync(connectionString, """
                INSERT INTO flowbit.workflow_instances
                    ("WorkflowDefinitionId", "WorkflowKey", "Status", "StartedBy", "CreatedAt", "UpdatedAt")
                SELECT "Id", "WorkflowKey", 'running', 'old-binary-new-instance', now(), now()
                FROM flowbit.workflow_definitions;
                """);
            Assert.True(await ScalarAsync<bool>(connectionString, "SELECT \"NodeActivityFromCreation\" FROM flowbit.workflow_instances WHERE \"StartedBy\"='old-binary-new-instance'"));
            // Observe actual changed rows and transaction boundaries, rather than
            // inferring bounded commits from migration SQL or total row count.
            await ExecuteAsync(connectionString, """
                CREATE TABLE public.coverage_classification_updates
                    (instance_id bigint PRIMARY KEY, transaction_id bigint NOT NULL);
                CREATE FUNCTION public.observe_coverage_classification() RETURNS trigger LANGUAGE plpgsql AS $$
                BEGIN
                    INSERT INTO public.coverage_classification_updates VALUES (NEW."Id", txid_current());
                    RETURN NEW;
                END $$;
                CREATE TRIGGER observe_coverage_classification
                    AFTER UPDATE OF "NodeActivityFromCreation" ON flowbit.workflow_instances
                    FOR EACH ROW WHEN (NOT OLD."NodeActivityFromCreation" AND NEW."NodeActivityFromCreation")
                    EXECUTE FUNCTION public.observe_coverage_classification();
                """);
            var timer = Stopwatch.StartNew();
            await MigrateAsync(connectionString, Classification);
            var committedBatchSizes = await ScalarAsync<long[]>(connectionString, """
                SELECT array_agg(changed_rows ORDER BY first_instance)
                FROM (SELECT count(*) AS changed_rows, min(instance_id) AS first_instance
                      FROM public.coverage_classification_updates GROUP BY transaction_id) batches
                """);
            Assert.Equal(new long[] { 995, 1000, 8 }, committedBatchSizes);
            Assert.All(committedBatchSizes, count => Assert.InRange(count, 1, 1000));
            output.WriteLine($"Classified 2,009 instances in {timer.Elapsed.TotalMilliseconds:F1} ms; observed committed update batches: {string.Join(", ", committedBatchSizes)}.");
            Assert.Equal(2004L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM flowbit.workflow_instances WHERE \"NodeActivityFromCreation\""));
            Assert.Equal(5L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM flowbit.workflow_instances WHERE \"StartedBy\" IN ('1','2','3','1001','2001') AND \"NodeActivityFromCreation\""));
            Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM flowbit.workflow_instances WHERE \"StartedBy\" IN ('4','5','6','7','8') AND \"NodeActivityFromCreation\""));
            Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM flowbit.workflow_instances WHERE \"NodeActivityPrunedAt\" IS NOT NULL"));
            Assert.True(await ScalarAsync<bool>(connectionString, "SELECT bool_and(\"UpdatedAt\" = \"FinishedAt\") FROM flowbit.workflow_instances WHERE \"Status\"='completed'"));

            await using (var db = Context(connectionString))
            {
                var definitionId = await db.WorkflowDefinitions.Select(row => row.Id).SingleAsync();
                var current = new WorkflowInstanceEntity { WorkflowDefinitionId = definitionId, WorkflowKey = "coverage-classification", StartedBy = "new-binary" };
                var explicitLegacy = new WorkflowInstanceEntity { WorkflowDefinitionId = definitionId, WorkflowKey = "coverage-classification", StartedBy = "explicit-legacy", NodeActivityFromCreation = false };
                db.WorkflowInstances.AddRange(current, explicitLegacy);
                await db.SaveChangesAsync();
                db.ChangeTracker.Clear();
                Assert.True((await db.WorkflowInstances.SingleAsync(row => row.Id == current.Id)).NodeActivityFromCreation);
                Assert.False((await db.WorkflowInstances.SingleAsync(row => row.Id == explicitLegacy.Id)).NodeActivityFromCreation);
            }
            // Repeating just the data migration is safe and does not reset newer true rows.
            await MigrateAsync(connectionString, Columns);
            await MigrateAsync(connectionString, Classification);
            Assert.Equal(2005L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM flowbit.workflow_instances WHERE \"NodeActivityFromCreation\""));
            Assert.Equal(2003L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM public.coverage_classification_updates"));
        });
    }

    [Fact]
    public async Task DowngradeCannotEraseNodePruningEvidence()
    {
        await WithDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, Classification);
            await ExecuteAsync(connectionString, """
                INSERT INTO flowbit.workflow_definitions
                    ("Name", "WorkflowKey", "Version", "Definition", "IsPublished", "IsDefault", "CreatedAt")
                VALUES ('Coverage downgrade', 'coverage-downgrade', 1,
                    '{"id":"coverage-downgrade","name":"Coverage downgrade","flowNodes":[],"sequenceFlows":[],"variables":[],"lanes":[]}',
                    FALSE, FALSE, now());
                INSERT INTO flowbit.workflow_instances
                    ("WorkflowDefinitionId", "WorkflowKey", "Status", "CreatedAt", "UpdatedAt", "FinishedAt", "HistoryPrunedAt", "NodeActivityPrunedAt")
                SELECT "Id", "WorkflowKey", 'completed', now(), now(), now(), now(), now() FROM flowbit.workflow_definitions;
                """);
            var error = await Assert.ThrowsAsync<PostgresException>(() => MigrateAsync(connectionString, Previous));
            Assert.Contains("Cannot downgrade retention coverage", error.MessageText);
            Assert.Equal(1L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM flowbit.workflow_instances WHERE \"NodeActivityPrunedAt\" IS NOT NULL"));
        });
    }

    private async Task WithDatabaseAsync(Func<string, Task> test)
    {
        var name = "diagram_coverage_" + Guid.NewGuid().ToString("N");
        var adminConnection = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "postgres" }.ConnectionString;
        await ExecuteAsync(adminConnection, $"CREATE DATABASE \"{name}\"");
        try { await test(new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = name }.ConnectionString); }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(adminConnection, $"DROP DATABASE \"{name}\" WITH (FORCE)");
        }
    }

    private static AppDbContext Context(string connectionString) => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql(connectionString, FlowbitDatabase.ConfigureProvider).Options);

    private static async Task MigrateAsync(string connectionString, string migration)
    {
        await using var db = Context(connectionString);
        await db.GetService<IMigrator>().MigrateAsync(migration);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }
}
