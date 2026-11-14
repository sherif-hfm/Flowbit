using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
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

    // The hardening migration adds the restored job index and two immediately validated
    // checks. It must apply over a database that already runs work of every kind, keep it,
    // then refuse the shapes it exists to forbid, and roll back without touching a row.
    [Fact]
    public async Task HardeningOverPopulatedDataKeepsOpenJobsHumanItemsAndCallMessagesAndEnforcesItsChecks()
    {
        var databaseName = "automatic_mi_harden_" + Guid.NewGuid().ToString("N");
        var adminString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "postgres" }.ConnectionString;
        await ExecuteAsync(adminString, $"CREATE DATABASE \"{databaseName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName }.ConnectionString;
        const string index = "SELECT count(*) FROM pg_indexes WHERE schemaname = 'flowbit' AND indexname = 'IX_workflow_jobs_MultiInstanceExecutionId'";
        const string checks = """
            SELECT count(*) FROM pg_constraint WHERE convalidated AND conname IN
                ('CK_multi_instance_executions_call_pin_shape', 'CK_call_activity_executions_automatic_item_pair')
            """;
        // Everything the seed wrote, as one comparable line.
        const string census = """
            SELECT concat_ws('|',
                (SELECT string_agg("Kind" || ':' || "Status", ',' ORDER BY "Id") FROM flowbit.workflow_jobs),
                (SELECT string_agg("ActivityKind" || ':' || "Status" || ':' || "TotalCount", ',' ORDER BY "Id") FROM flowbit.multi_instance_executions),
                (SELECT count(*) FROM flowbit.user_tasks WHERE "MultiInstanceExecutionId" IS NOT NULL AND "Status" = 'active'),
                (SELECT string_agg("Status", ',' ORDER BY "Id") FROM flowbit.multi_instance_activity_items),
                (SELECT count(*) FROM flowbit.node_executions WHERE "ExecutionKind" = 'automaticActivityItem'),
                (SELECT string_agg("State", ',' ORDER BY "Id") FROM flowbit.call_activity_executions))
            """;
        try
        {
            await MigrateAsync(connectionString, "20261111144824_ValidateAutomaticMultiInstanceConstraints");
            Assert.Equal(0L, await ScalarAsync(connectionString, index));
            var seed = await SeedRunningWorkAsync(connectionString);
            var before = (string?)await ScalarAsync(connectionString, census);
            Assert.Equal(
                "multiInstanceItem:queued,multiInstanceItem:retry,multiInstanceDispatch:queued,callStart:queued,callResume:queued"
                + "|userTask:active:2,scriptTask:active:2,callActivity:active:0|2|active,active|2|queued,running",
                before);

            await MigrateAsync(connectionString, "20261111192010_HardenAutomaticMultiInstanceCore");
            Assert.Equal(1L, await ScalarAsync(connectionString, index));
            Assert.Equal(2L, await ScalarAsync(connectionString, checks));
            Assert.Equal(before, await ScalarAsync(connectionString, census));

            // A nonempty call body needs its pin, held exactly while it is active.
            var pinless = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
                $"UPDATE flowbit.multi_instance_executions SET \"TotalCount\" = 1 WHERE \"Id\" = {seed.EmptyCallBodyId}"));
            Assert.Equal("CK_multi_instance_executions_call_pin_shape", pinless.ConstraintName);
            await ExecuteAsync(connectionString, $"""
                UPDATE flowbit.multi_instance_executions SET "TotalCount" = 1, "PinnedChildDefinitionId" = {seed.DefinitionId},
                    "PinnedChildStartEventId" = 1, "PinState" = 'pinned' WHERE "Id" = {seed.EmptyCallBodyId}
                """);
            var released = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
                $"UPDATE flowbit.multi_instance_executions SET \"PinState\" = 'released' WHERE \"Id\" = {seed.EmptyCallBodyId}"));
            Assert.Equal("CK_multi_instance_executions_call_pin_shape", released.ConstraintName);
            // Only a call body holds a pin.
            var humanPin = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString, $"""
                UPDATE flowbit.multi_instance_executions SET "PinnedChildDefinitionId" = {seed.DefinitionId},
                    "PinnedChildStartEventId" = 1, "PinState" = 'pinned' WHERE "Id" = {seed.HumanExecutionId}
                """));
            Assert.Equal("CK_multi_instance_executions_call_pin_shape", humanPin.ConstraintName);
            // A call invocation names both its body and its item, or neither.
            var halfOwned = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
                $"UPDATE flowbit.call_activity_executions SET \"MultiInstanceExecutionId\" = {seed.EmptyCallBodyId} WHERE \"Id\" = {seed.QueuedCallId}"));
            Assert.Equal("CK_call_activity_executions_automatic_item_pair", halfOwned.ConstraintName);

            // Rolling back removes the index and both checks and keeps every row.
            await MigrateAsync(connectionString, "20261111144824_ValidateAutomaticMultiInstanceConstraints");
            Assert.Equal(0L, await ScalarAsync(connectionString, index));
            Assert.Equal(0L, await ScalarAsync(connectionString, checks));
            var afterRollback = (string?)await ScalarAsync(connectionString, census);
            Assert.Equal(before!.Replace("callActivity:active:0", "callActivity:active:1"), afterRollback);

            // The later migrations apply over the same running work.
            await MigrateAsync(connectionString, null);
            Assert.Equal(1L, await ScalarAsync(connectionString, index));
            Assert.Equal(2L, await ScalarAsync(connectionString, checks));
            Assert.Equal(true, await ScalarAsync(connectionString,
                "SELECT convalidated FROM pg_constraint WHERE conname = 'CK_workflow_jobs_multi_instance_start_shape'"));
            Assert.Equal(afterRollback, await ScalarAsync(connectionString, census));
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(adminString, $"DROP DATABASE \"{databaseName}\" WITH (FORCE)");
        }
    }

    private sealed record RunningWork(long DefinitionId, long HumanExecutionId, long EmptyCallBodyId, long QueuedCallId);

    // One running instance with a human multi-instance task and its two open items, a script
    // body with two admitted items and their queued and retrying jobs plus a dispatch wake,
    // an empty call body, a queued ordinary call with its callStart, and a running ordinary
    // call whose child's callResume is pending. Bodies and items are written by the
    // repository the application uses; the job and call-message rows by SQL.
    private static async Task<RunningWork> SeedRunningWorkAsync(string connectionString)
    {
        const string key = "mi-harden-populated";
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.EnableDynamicJson();
        await using var dataSource = dataSourceBuilder.Build();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(dataSource, FlowbitDatabase.ConfigureProvider).Options);
        var definition = new WorkflowDefinitionEntity { Name = key, WorkflowKey = key, Version = 1, IsPublished = true,
            Definition = new WorkflowModel { Id = key, Name = key } };
        db.WorkflowDefinitions.Add(definition);
        await db.SaveChangesAsync();
        var instance = new WorkflowInstanceEntity { WorkflowDefinitionId = definition.Id, WorkflowKey = key, Status = "running", StartedBy = "starter" };
        db.WorkflowInstances.Add(instance);
        await db.SaveChangesAsync();
        ExecutionTokenEntity Token(int nodeId, string type) =>
            new() { InstanceId = instance.Id, NodeId = nodeId, NodeName = "Node " + nodeId, NodeType = type };
        var humanToken = Token(2, "userTask");
        var bodyToken = Token(3, "scriptTask");
        var queuedCallToken = Token(4, "callActivity");
        var runningCallToken = Token(5, "callActivity");
        var emptyBodyToken = Token(6, "callActivity");
        db.ExecutionTokens.AddRange(humanToken, bodyToken, queuedCallToken, runningCallToken, emptyBodyToken);
        var child = new WorkflowInstanceEntity { WorkflowDefinitionId = definition.Id, WorkflowKey = key, Status = "running",
            StartedBy = "callActivity", CallRootInstanceId = instance.Id, CallDepth = 1 };
        db.WorkflowInstances.Add(child);
        await db.SaveChangesAsync();

        var human = new MultiInstanceExecutionEntity { InstanceId = instance.Id, TokenId = humanToken.Id, NodeId = 2,
            Mode = "parallel", Source = "cardinality", ResultVariable = "humanResults", TotalCount = 2 };
        db.MultiInstanceExecutions.Add(human);
        await db.SaveChangesAsync();
        db.UserTasks.AddRange(Enumerable.Range(0, 2).Select(index => new UserTaskEntity
        {
            InstanceId = instance.Id, TokenId = humanToken.Id, NodeId = 2, NodeName = "Approve", MultiInstanceExecutionId = human.Id, ItemIndex = index
        }));
        await db.SaveChangesAsync();

        var repository = new AutomaticMultiInstanceRepository(db);
        AutomaticMultiInstanceCreateRecord Body(ExecutionTokenEntity token, string kind, int count) => new()
        {
            InstanceId = instance.Id, TokenId = token.Id, WorkflowDefinitionId = definition.Id, ActivationId = token.ActivationId,
            NodeId = token.NodeId, NodeName = token.NodeName, ActivityKind = kind, Mode = "parallel", ResultVariable = "results",
            AggregateScope = "instance", AggregateValueVersion = 0, MaxInstances = 1000, EffectiveConcurrency = Math.Min(2, count), TotalCount = count,
            ResultBytes = 2, InputSnapshot = System.Text.Json.JsonSerializer.SerializeToElement(new { collection = Enumerable.Range(0, count).ToArray() })
        };
        long bodyId, emptyBodyId;
        await using (var transaction = await db.Database.BeginTransactionAsync())
        {
            var body = await repository.CreateAsync(Body(bodyToken, "scriptTask", 2), default);
            Assert.Equal(2, (await repository.AdmitAsync(body.Id, bodyToken.ActivationId, 1000, default)).Count);
            bodyId = body.Id;
            emptyBodyId = (await repository.CreateAsync(Body(emptyBodyToken, "callActivity", 0), default)).Id;
            await transaction.CommitAsync();
        }

        const string columns = """
            "InstanceId", "WorkflowDefinitionId", "WorkflowKey", "TokenId", "MultiInstanceExecutionId", "AutomaticMultiInstanceItemId",
            "MultiInstanceControlGeneration", "CallInvocationId", "ActivationId", "NodeId", "NodeName", "NodeType", "Kind", "QueueClass",
            "Phase", "Status", "Priority", "AttemptCount", "LeaseGeneration", "FailureHandling", "MaxAttempts", "RetryDelays", "DueAt",
            "CreatedAt", "UpdatedAt"
            """;
        await ExecuteAsync(connectionString, $"""
            INSERT INTO flowbit.workflow_jobs ({columns})
            SELECT item."InstanceId", body."WorkflowDefinitionId", '{key}', body."TokenId", body."Id", item."Id", NULL, NULL,
                body."ActivationId", body."NodeId", 'Node 3', 'scriptTask', 'multiInstanceItem', 'activity', 'multiInstanceItem',
                CASE item."ItemIndex" WHEN 0 THEN 'queued' ELSE 'retry' END, 0, item."ItemIndex", 0, 'boundaryFirst', 4,
                ARRAY[]::interval[], now(), now(), now()
            FROM flowbit.multi_instance_activity_items item
            JOIN flowbit.multi_instance_executions body ON body."Id" = item."MultiInstanceExecutionId"
            WHERE body."Id" = {bodyId} ORDER BY item."ItemIndex";
            INSERT INTO flowbit.workflow_jobs ({columns})
            SELECT "InstanceId", "WorkflowDefinitionId", '{key}', "TokenId", "Id", NULL, 0, NULL, "ActivationId", "NodeId", 'Node 3',
                'scriptTask', 'multiInstanceDispatch', 'control', 'multiInstanceDispatch', 'queued', 0, 0, 0, 'boundaryFirst', 4,
                ARRAY[]::interval[], now(), now(), now()
            FROM flowbit.multi_instance_executions WHERE "Id" = {bodyId};
            """);
        long CallId(string sql) => (long)ScalarAsync(connectionString, sql).GetAwaiter().GetResult()!;
        var queuedCallId = CallId($"""
            INSERT INTO flowbit.call_activity_executions
                ("ParentInstanceId", "ParentTokenId", "ActivationId", "ParentNodeId", "RootInstanceId", "ChildDepth", "ParentDefinitionId", "TargetWorkflowKey", "State")
            VALUES ({instance.Id}, {queuedCallToken.Id}, '{queuedCallToken.ActivationId}', 4, {instance.Id}, 1, {definition.Id}, 'child-family', 'queued')
            RETURNING "Id"
            """);
        var runningCallId = CallId($"""
            INSERT INTO flowbit.call_activity_executions
                ("ParentInstanceId", "ParentTokenId", "ActivationId", "ParentNodeId", "RootInstanceId", "ChildDepth", "ParentDefinitionId", "TargetWorkflowKey",
                 "State", "ChildInstanceId", "ChildDefinitionId", "ChildStartEventId", "CapacityReserved", "AdmittedAt")
            VALUES ({instance.Id}, {runningCallToken.Id}, '{runningCallToken.ActivationId}', 5, {instance.Id}, 1, {definition.Id}, 'child-family',
                'running', {child.Id}, {definition.Id}, 1, true, now())
            RETURNING "Id"
            """);
        await ExecuteAsync(connectionString, $"""
            INSERT INTO flowbit.workflow_jobs ({columns})
            VALUES ({instance.Id}, {definition.Id}, '{key}', {queuedCallToken.Id}, NULL, NULL, NULL, {queuedCallId}, '{queuedCallToken.ActivationId}',
                4, 'Node 4', 'callActivity', 'callStart', 'control', 'callStart', 'queued', 0, 0, 0, 'boundaryFirst', 4, ARRAY[]::interval[], now(), now(), now());
            INSERT INTO flowbit.workflow_jobs ({columns})
            VALUES ({child.Id}, {definition.Id}, '{key}', NULL, NULL, NULL, NULL, {runningCallId}, '{Guid.NewGuid()}',
                1, 'Child end', 'endEvent', 'callResume', 'control', 'callResume', 'queued', 0, 0, 0, 'boundaryFirst', 4, ARRAY[]::interval[], now(), now(), now());
            """);
        return new RunningWork(definition.Id, human.Id, emptyBodyId, queuedCallId);
    }

    private static async Task MigrateAsync(string connectionString, string? migration)
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
