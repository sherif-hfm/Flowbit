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

    // TASK-05D: the entry job's shape check is added NOT VALID next to a populated ordinary
    // call and its pending callStart, validated separately, refuses an entry job that names a
    // body, and leaves with its migration.
    [Fact]
    public async Task MultiInstanceStartShapeIsAddedNotValidThenValidatedAndRefusesABodyCorrelation()
    {
        var databaseName = "automatic_mi_start_shape_" + Guid.NewGuid().ToString("N");
        var adminString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "postgres" }.ConnectionString;
        await ExecuteAsync(adminString, $"CREATE DATABASE \"{databaseName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName }.ConnectionString;
        const string constraint = "SELECT convalidated FROM pg_constraint WHERE conname = 'CK_workflow_jobs_multi_instance_start_shape'";
        try
        {
            await MigrateAsync(connectionString, "20261112162436_HashInstanceVariableStringIndex");
            await ExecuteAsync(connectionString, """
                WITH definition AS (
                    INSERT INTO flowbit.workflow_definitions ("Name", "Version", "Definition", "IsPublished", "WorkflowKey", "IsDefault")
                    VALUES ('Existing caller', 1, '{}'::jsonb, false, 'mi-start-shape', false) RETURNING "Id"
                ), instance AS (
                    INSERT INTO flowbit.workflow_instances ("WorkflowDefinitionId", "WorkflowKey", "Status", "StartedBy")
                    SELECT "Id", 'mi-start-shape', 'running', 'starter' FROM definition RETURNING "Id", "WorkflowDefinitionId"
                ), token AS (
                    INSERT INTO flowbit.execution_tokens ("InstanceId", "NodeId", "NodeName", "NodeType", "Status")
                    SELECT "Id", 3, 'Ordinary call', 'callActivity', 'active' FROM instance RETURNING "Id", "InstanceId", "ActivationId"
                ), call AS (
                    INSERT INTO flowbit.call_activity_executions
                        ("ParentInstanceId", "ParentTokenId", "ActivationId", "ParentNodeId", "RootInstanceId", "ChildDepth", "ParentDefinitionId", "TargetWorkflowKey", "State")
                    SELECT token."InstanceId", token."Id", token."ActivationId", 3, token."InstanceId", 1,
                        instance."WorkflowDefinitionId", 'child-family', 'queued' FROM token JOIN instance ON instance."Id" = token."InstanceId"
                    RETURNING "Id", "ParentInstanceId", "ParentTokenId", "ActivationId", "ParentDefinitionId"
                )
                INSERT INTO flowbit.workflow_jobs ("InstanceId", "WorkflowDefinitionId", "WorkflowKey", "TokenId", "CallInvocationId",
                    "ActivationId", "NodeId", "NodeName", "NodeType", "Kind", "QueueClass", "Phase", "Status", "Priority", "AttemptCount", "LeaseGeneration", "FailureHandling", "MaxAttempts",
                    "RetryDelays", "DueAt", "CreatedAt", "UpdatedAt")
                SELECT "ParentInstanceId", "ParentDefinitionId", 'mi-start-shape', "ParentTokenId", "Id", "ActivationId", 3,
                    'Ordinary call', 'callActivity', 'callStart', 'control', 'callStart', 'queued', 0, 0, 0, 'boundaryFirst', 4, ARRAY[]::interval[], now(), now(), now()
                FROM call;
                """);
            await MigrateAsync(connectionString, "20261113145806_AddMultiInstanceStartJobShape");
            Assert.Equal(false, await ScalarAsync(connectionString, constraint));
            await MigrateAsync(connectionString, "20261113145843_ValidateMultiInstanceStartJobShape");
            Assert.Equal(true, await ScalarAsync(connectionString, constraint));
            // Reapplying a finished validation is harmless.
            await MigrateAsync(connectionString, "20261113145843_ValidateMultiInstanceStartJobShape");
            Assert.Equal(1L, await ScalarAsync(connectionString,
                "SELECT count(*) FROM flowbit.workflow_jobs WHERE \"Kind\" = 'callStart' AND \"Status\" = 'queued'"));

            // An entry job runs on the control queue; only this check refuses one on the activity queue.
            const string entry = """
                INSERT INTO flowbit.workflow_jobs ("InstanceId", "WorkflowDefinitionId", "WorkflowKey", "TokenId",
                    "ActivationId", "NodeId", "NodeName", "NodeType", "Kind", "QueueClass", "Phase", "Status", "Priority", "AttemptCount", "LeaseGeneration", "FailureHandling", "MaxAttempts",
                    "RetryDelays", "DueAt", "CreatedAt", "UpdatedAt")
                SELECT token."InstanceId", instance."WorkflowDefinitionId", 'mi-start-shape', token."Id", token."ActivationId", 2,
                    'Call body', 'callActivity', 'multiInstanceStart', {0}, 'multiInstanceStart', 'queued', 0, 0, 0, 'boundaryFirst', 4,
                    ARRAY[]::interval[], now(), now(), now()
                FROM flowbit.execution_tokens token JOIN flowbit.workflow_instances instance ON instance."Id" = token."InstanceId";
                """;
            var refused = await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(connectionString, string.Format(entry, "'activity'")));
            Assert.Equal("CK_workflow_jobs_multi_instance_start_shape", refused.ConstraintName);
            await ExecuteAsync(connectionString, string.Format(entry, "'control'"));

            // Rolling back the shape check removes it and keeps every row.
            await ExecuteAsync(connectionString, "DELETE FROM flowbit.workflow_jobs WHERE \"Kind\" = 'multiInstanceStart'");
            await MigrateAsync(connectionString, "20261112162436_HashInstanceVariableStringIndex");
            Assert.Null(await ScalarAsync(connectionString, constraint));
            Assert.Equal(1L, await ScalarAsync(connectionString, "SELECT count(*) FROM flowbit.workflow_jobs"));
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
