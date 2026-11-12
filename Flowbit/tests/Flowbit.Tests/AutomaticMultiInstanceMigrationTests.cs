using Flowbit.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class AutomaticMultiInstanceMigrationTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task PopulatedUpgradePreservesHumanBodiesAndOrdinaryCallsAndSeparatesValidation()
    {
        var databaseName = "automatic_mi_migration_" + Guid.NewGuid().ToString("N");
        var adminString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "postgres" }.ConnectionString;
        await ExecuteAsync(adminString, $"CREATE DATABASE \"{databaseName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName }.ConnectionString;
        try
        {
            await MigrateAsync(connectionString, "20261110075435_ClassifyInstanceNodeActivityCoverage");
            await ExecuteAsync(connectionString, """
                INSERT INTO flowbit.engine_settings ("Namespace", "Key", "Value", "CreatedAt", "UpdatedAt")
                VALUES ('Workflow.MultiInstance', 'DefaultConcurrency', '31', now(), now());
                WITH definition AS (
                    INSERT INTO flowbit.workflow_definitions ("Name", "Version", "Definition", "IsPublished", "WorkflowKey", "IsDefault")
                    VALUES ('Existing workflow', 1, '{}'::jsonb, false, 'mi-migration-existing', false) RETURNING "Id"
                ), instance AS (
                    INSERT INTO flowbit.workflow_instances ("WorkflowDefinitionId", "WorkflowKey", "Status", "StartedBy")
                    SELECT "Id", 'mi-migration-existing', 'running', 'starter' FROM definition RETURNING "Id", "WorkflowDefinitionId"
                ), token AS (
                    INSERT INTO flowbit.execution_tokens ("InstanceId", "NodeId", "NodeName", "NodeType", "Status")
                    SELECT "Id", 2, 'Existing human work', 'userTask', 'active' FROM instance RETURNING "Id", "InstanceId", "ActivationId"
                ), human AS (
                    INSERT INTO flowbit.multi_instance_executions
                        ("InstanceId", "TokenId", "NodeId", "Mode", "Source", "ResultVariable", "Status", "TotalCount", "CompletedCount", "CancelledCount")
                    SELECT "InstanceId", "Id", 2, 'parallel', 'collection', 'humanResults', 'active', 3, 1, 0 FROM token
                )
                INSERT INTO flowbit.call_activity_executions
                    ("ParentInstanceId", "ParentTokenId", "ActivationId", "ParentNodeId", "RootInstanceId", "ChildDepth", "ParentDefinitionId", "TargetWorkflowKey", "State")
                SELECT token."InstanceId", token."Id", token."ActivationId", 3, token."InstanceId", 1,
                    instance."WorkflowDefinitionId", 'child-family', 'queued' FROM token JOIN instance ON instance."Id" = token."InstanceId";
                """);
            await MigrateAsync(connectionString, "20261111144523_AddAutomaticMultiInstanceCore");
            Assert.Equal("31", await ScalarAsync(connectionString, """
                SELECT "Value" FROM flowbit.engine_settings WHERE "Namespace"='Workflow.MultiInstance' AND "Key"='DefaultConcurrency'
                """));
            Assert.Equal(0L, await ScalarAsync(connectionString, """
                SELECT count(*) FROM pg_constraint WHERE conname IN ('CK_workflow_jobs_automatic_control_shape',
                    'CK_workflow_jobs_automatic_item_shape', 'CK_node_executions_execution_kind', 'CK_node_executions_multi_instance_shape') AND convalidated
                """));
            Assert.Equal("userTask|3|1|humanResults", await ScalarAsync(connectionString, """
                SELECT concat_ws('|', "ActivityKind", "TotalCount", "CompletedCount", "ResultVariable") FROM flowbit.multi_instance_executions
                """));
            Assert.Equal(1L, await ScalarAsync(connectionString, """
                SELECT count(*) FROM flowbit.call_activity_executions WHERE "State"='queued' AND "AutomaticMultiInstanceItemId" IS NULL AND "MultiInstanceExecutionId" IS NULL
                """));
            Assert.Equal(0L, await ScalarAsync(connectionString, "SELECT count(*) FROM flowbit.multi_instance_activity_items"));
            await MigrateAsync(connectionString, "20261111144824_ValidateAutomaticMultiInstanceConstraints");
            Assert.Equal(4L, await ScalarAsync(connectionString, """
                SELECT count(*) FROM pg_constraint WHERE conname IN ('CK_workflow_jobs_automatic_control_shape',
                    'CK_workflow_jobs_automatic_item_shape', 'CK_node_executions_execution_kind', 'CK_node_executions_multi_instance_shape') AND convalidated
                """));
            // Reapplying a finished validation is harmless, and an all-legacy database can roll back.
            await MigrateAsync(connectionString, "20261111144824_ValidateAutomaticMultiInstanceConstraints");
            await MigrateAsync(connectionString, "20261110075435_ClassifyInstanceNodeActivityCoverage");
            Assert.Equal(1L, await ScalarAsync(connectionString, "SELECT count(*) FROM flowbit.multi_instance_executions"));
            Assert.Equal(1L, await ScalarAsync(connectionString, "SELECT count(*) FROM flowbit.call_activity_executions"));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(adminString, $"DROP DATABASE \"{databaseName}\" WITH (FORCE)");
        }
    }

    private static async Task MigrateAsync(string connectionString, string migration)
    {
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, FlowbitDatabase.ConfigureProvider).Options);
        await db.GetService<IMigrator>().MigrateAsync(migration);
    }
    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }
    private static async Task<object?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync();
    }
}
