using System.Net;
using System.Net.Http.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using NpgsqlTypes;
using Xunit;
using static Flowbit.Tests.CallActivityHarness;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class CallActivityPersistenceTests(PostgresApiFixture fixture) : IAsyncLifetime
{
    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync() => harness.RetireLeftoverJobsAsync();

    private const string PreviousMigration = "20261027165217_AddBusinessCalendars";
    private readonly CallActivityHarness harness = new(fixture);

    [Fact]
    public async Task A_fresh_database_has_every_call_object()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString);
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();

            foreach (var table in new[] { "call_activity_executions", "call_activity_results", "workflow_definition_call_targets" })
            {
                Assert.True(await ExistsAsync(connection, "SELECT to_regclass(@name) IS NOT NULL", "flowbit." + table), table);
            }

            foreach (var constraint in new[]
                     {
                         "CK_call_activity_executions_admission",
                         "CK_call_activity_executions_child_resolution",
                         "CK_call_activity_executions_child_shape",
                         "CK_call_activity_executions_depth",
                         "CK_call_activity_executions_reservation",
                         "CK_call_activity_executions_settlement",
                         "CK_call_activity_executions_state",
                         "CK_call_activity_results_outcome",
                         "CK_workflow_instances_call_depth",
                         "CK_workflow_instances_call_shape"
                     })
            {
                Assert.True(
                    await ExistsAsync(connection, "SELECT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = @name)", constraint),
                    constraint);
            }

            foreach (var index in new[]
                     {
                         "IX_call_activity_executions_root_reserved",
                         "IX_call_activity_executions_ParentTokenId_State",
                         "IX_workflow_definition_call_targets_TargetWorkflowKey",
                         "IX_workflow_instances_CallRootInstanceId_Status",
                         "IX_workflow_jobs_CallInvocationId_Kind"
                     })
            {
                Assert.True(
                    await ExistsAsync(connection, "SELECT EXISTS (SELECT 1 FROM pg_indexes WHERE indexname = @name)", index),
                    index);
            }
        });
    }

    [Fact]
    public async Task An_upgrade_gives_existing_instances_only_root_defaults()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, PreviousMigration);
            var instanceId = await SeedInstanceAsync(connectionString, childShape: false);

            await MigrateAsync(connectionString);

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using var read = new NpgsqlCommand(
                """SELECT "CallRootInstanceId", "CallDepth" FROM flowbit.workflow_instances WHERE "Id" = @id""",
                connection);
            read.Parameters.AddWithValue("id", instanceId);
            await using var reader = await read.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.IsDBNull(0));
            Assert.Equal(0, reader.GetInt32(1));
            await reader.CloseAsync();

            await using var history = new NpgsqlCommand(
                "SELECT count(*) FROM flowbit.call_activity_executions",
                connection);
            Assert.Equal(0L, (long)(await history.ExecuteScalarAsync())!);
        });
    }

    [Fact]
    public async Task The_down_migration_refuses_live_call_data_and_runs_once_there_is_none()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString);
            var childId = await SeedInstanceAsync(connectionString, childShape: true);

            var refused = await Assert.ThrowsAsync<PostgresException>(() => MigrateAsync(connectionString, PreviousMigration));
            Assert.Contains("call", refused.MessageText, StringComparison.OrdinalIgnoreCase);

            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var clear = new NpgsqlCommand(
                             """DELETE FROM flowbit.workflow_instances WHERE "Id" = @id""",
                             connection))
            {
                clear.Parameters.AddWithValue("id", childId);
                await clear.ExecuteNonQueryAsync();
            }

            await MigrateAsync(connectionString, PreviousMigration);
            Assert.False(await ExistsAsync(
                connection,
                "SELECT to_regclass(@name) IS NOT NULL",
                "flowbit.call_activity_executions"));
        });
    }

    [Fact]
    public async Task Constraints_keep_reservations_states_and_identity_consistent()
    {
        var childKey = Key("child");
        await harness.PublishAsync(WaitingChild(childKey));
        var parent = await harness.PublishAsync(Parent(Key("parent"), childKey));
        var parentId = await harness.StartAsync(parent.Id);
        var queued = await harness.InvocationAsync(parentId);
        Assert.True(queued.CapacityReserved);

        async Task AssertRejectedAsync(string constraint, Func<AppDbContext, Task> change)
        {
            await using var db = fixture.CreateDbContext();
            var error = await Assert.ThrowsAnyAsync<Exception>(() => change(db));
            var postgres = error as PostgresException ?? error.InnerException as PostgresException;
            Assert.NotNull(postgres);
            Assert.Equal(constraint, postgres.ConstraintName);
        }

        IQueryable<Flowbit.Infrastructure.Entities.CallActivityExecutionEntity> Row(AppDbContext db) =>
            db.CallActivityExecutions.Where(call => call.Id == queued.Id);

        // resultReady is a read projection, so it can never be stored.
        await AssertRejectedAsync("CK_call_activity_executions_reservation", db => Row(db)
            .ExecuteUpdateAsync(set => set.SetProperty(call => call.State, "resultReady")));
        await AssertRejectedAsync("CK_call_activity_executions_depth", db => Row(db)
            .ExecuteUpdateAsync(set => set.SetProperty(call => call.ChildDepth, CallActivityConstraints.MaxSupportedDepth + 2)));
        await AssertRejectedAsync("CK_call_activity_executions_admission", db => Row(db)
            .ExecuteUpdateAsync(set => set.SetProperty(call => call.AdmittedAt, (DateTimeOffset?)null)));
        await AssertRejectedAsync("CK_call_activity_executions_child_shape", db => Row(db)
            .ExecuteUpdateAsync(set => set.SetProperty(call => call.State, CallInvocationStates.Running)));
        await AssertRejectedAsync("CK_call_activity_executions_reservation", db => Row(db)
            .ExecuteUpdateAsync(set => set
                .SetProperty(call => call.State, CallInvocationStates.Completed)
                .SetProperty(call => call.SettledAt, (DateTimeOffset?)DateTimeOffset.UtcNow)));
        await AssertRejectedAsync("CK_workflow_instances_call_depth", db => db.WorkflowInstances
            .Where(instance => instance.Id == parentId)
            .ExecuteUpdateAsync(set => set.SetProperty(instance => instance.CallDepth, CallActivityConstraints.MaxSupportedDepth + 1)));
        await AssertRejectedAsync("CK_workflow_instances_call_shape", db => db.WorkflowInstances
            .Where(instance => instance.Id == parentId)
            .ExecuteUpdateAsync(set => set.SetProperty(instance => instance.CallDepth, 2)));

        await harness.DrainAsync(parentId);
        var running = await harness.InvocationAsync(parentId);

        await AssertRejectedAsync("CK_call_activity_executions_reservation", db => Row(db)
            .ExecuteUpdateAsync(set => set.SetProperty(call => call.CapacityReserved, false)));

        // A second invocation can never own the same activation or child.
        await using (var db = fixture.CreateDbContext())
        {
            var duplicate = await db.CallActivityExecutions.AsNoTracking().SingleAsync(call => call.Id == running.Id);
            duplicate.Id = 0;
            db.CallActivityExecutions.Add(duplicate);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.UniqueViolation, (error.InnerException as PostgresException)?.SqlState);
        }

        // One job of each kind per invocation.
        await using (var db = fixture.CreateDbContext())
        {
            var start = await db.WorkflowJobs.AsNoTracking().SingleAsync(job =>
                job.CallInvocationId == running.Id && job.Kind == WorkflowJobKinds.CallStart);
            start.Id = 0;
            db.WorkflowJobs.Add(start);
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal("IX_workflow_jobs_CallInvocationId_Kind", (error.InnerException as PostgresException)?.ConstraintName);
        }
    }

    [Fact]
    public async Task A_closed_publication_gate_covers_both_features_on_every_path_and_leaves_drafts_alone()
    {
        var childKey = Key("child");
        var plain = await harness.PublishAsync(ImmediateChild(childKey));
        var callOnly = ImmediateChild(childKey);
        callOnly.FlowNodes.Single(node => node.Id == Start).CallOnly = true;
        WorkflowDetailDto openDraft;
        using (var created = await harness.SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(callOnly, false)))
        {
            Assert.Equal(HttpStatusCode.Created, created.StatusCode);
            openDraft = (await created.Content.ReadFromJsonAsync<WorkflowDetailDto>())!;
        }

        await using var gated = WithClosedGate();
        using var client = gated.CreateClient();
        var parentKey = Key("parent");
        var caller = Parent(parentKey, childKey);
        var parentDraft = await SendAsync(client, HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(caller, false));
        Assert.Equal(HttpStatusCode.Created, parentDraft.StatusCode);
        var draft = (await parentDraft.Content.ReadFromJsonAsync<WorkflowDetailDto>())!;

        // Create-with-publish, update-with-publish, publish, and set-default.
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await SendAsync(client, HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(Parent(Key("other"), childKey), true))).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await SendAsync(client, HttpMethod.Put, $"/api/workflows/{draft.Id}", new UpdateWorkflowRequest(caller, true))).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await SendAsync(client, HttpMethod.Post, $"/api/workflows/{draft.Id}/publish")).StatusCode);
        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await SendAsync(client, HttpMethod.Post, $"/api/workflows/{openDraft.Id}/publish")).StatusCode);
        Assert.Equal(
            HttpStatusCode.Created,
            (await SendAsync(client, HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(callOnly, false))).StatusCode);

        // Set-default on a version published before the gate closed.
        using (var published = await harness.SendAsync(HttpMethod.Post, $"/api/workflows/{openDraft.Id}/publish"))
        {
            Assert.True(published.IsSuccessStatusCode, await published.Content.ReadAsStringAsync());
        }

        await using (var db = fixture.CreateDbContext())
        {
            var defaultId = await db.WorkflowDefinitions.AsNoTracking()
                .Where(definition => definition.WorkflowKey == childKey && definition.IsDefault)
                .Select(definition => definition.Id)
                .SingleAsync();
            if (defaultId == openDraft.Id)
            {
                using var reset = await harness.SendAsync(HttpMethod.Post, $"/api/workflows/{plain.Id}/set-default");
                Assert.True(reset.IsSuccessStatusCode, await reset.Content.ReadAsStringAsync());
            }
        }

        Assert.Equal(
            HttpStatusCode.BadRequest,
            (await SendAsync(client, HttpMethod.Post, $"/api/workflows/{openDraft.Id}/set-default")).StatusCode);
    }

    [Fact]
    public async Task Deleting_a_default_is_refused_when_its_successor_is_gated_rather_than_skipping_it()
    {
        // Version 1 is call-only and version 2 is plain. Version 1 is the only other
        // published version, so a closed gate cannot promote it.
        var childKey = Key("child");
        var gatedSuccessor = ImmediateChild(childKey);
        gatedSuccessor.FlowNodes.Single(node => node.Id == Start).CallOnly = true;
        var first = await harness.PublishAsync(gatedSuccessor);
        var second = await harness.PublishAsync(ImmediateChild(childKey));
        using (var moved = await harness.SendAsync(HttpMethod.Post, $"/api/workflows/{second.Id}/set-default"))
        {
            Assert.True(moved.IsSuccessStatusCode, await moved.Content.ReadAsStringAsync());
        }

        await using var gated = WithClosedGate();
        using var client = gated.CreateClient();

        var preview = await SendAsync(client, HttpMethod.Get, $"/api/workflows/{childKey}/callers?deletingId={second.Id}");
        Assert.Equal(HttpStatusCode.OK, preview.StatusCode);
        var body = (await preview.Content.ReadFromJsonAsync<CallersPreviewDto>())!;
        Assert.False(body.DeletionAllowed);
        Assert.Contains(body.DeletionBlockers, blocker => blocker.Contains($"version {first.Version}", StringComparison.Ordinal));
        Assert.Equal(second.Id, body.EffectiveTargetId);

        var deleted = await SendAsync(client, HttpMethod.Delete, $"/api/workflows/{second.Id}");
        Assert.False(deleted.IsSuccessStatusCode);

        await using var db = fixture.CreateDbContext();
        var stillDefault = await db.WorkflowDefinitions.AsNoTracking()
            .SingleAsync(definition => definition.WorkflowKey == childKey && definition.IsDefault);
        Assert.Equal(second.Id, stillDefault.Id);
    }

    private WebApplicationFactory<Program> WithClosedGate() =>
        fixture.Factory.WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<DurableProcessingOptions>();
            services.AddSingleton(new DurableProcessingOptions { PublicationEnabled = false });
        }));

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, body.GetType());
        }

        ApiTestAuth.Authorize(request, "call-admin", "admin");
        return await client.SendAsync(request);
    }

    private static async Task<bool> ExistsAsync(NpgsqlConnection connection, string sql, string name)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddWithValue("name", name);
        return (bool)(await command.ExecuteScalarAsync())!;
    }

    private static async Task<long> SeedInstanceAsync(string connectionString, bool childShape)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var definition = new NpgsqlCommand("""
            INSERT INTO flowbit.workflow_definitions
                ("Name", "WorkflowKey", "Version", "Definition", "IsPublished", "IsDefault", "CreatedAt")
            VALUES ('Call migration', 'call-migration', 1, @definition, true, false, now())
            RETURNING "Id"
            """, connection);
        definition.Parameters.AddWithValue(
            "definition",
            NpgsqlDbType.Jsonb,
            """{"id":"call-migration","name":"Call migration","flowNodes":[],"sequenceFlows":[],"variables":[],"lanes":[]}""");
        var definitionId = (long)(await definition.ExecuteScalarAsync())!;
        var columns = childShape ? """, "CallRootInstanceId", "CallDepth" """ : string.Empty;
        var values = childShape ? ", 999, 1" : string.Empty;
        await using var instance = new NpgsqlCommand($"""
            INSERT INTO flowbit.workflow_instances
                ("WorkflowDefinitionId", "WorkflowKey", "Status", "StartedBy", "CreatedAt", "UpdatedAt"{columns})
            VALUES (@definitionId, 'call-migration', 'running', 'starter', now(), now(){values})
            RETURNING "Id"
            """, connection);
        instance.Parameters.AddWithValue("definitionId", definitionId);
        return (long)(await instance.ExecuteScalarAsync())!;
    }

    private static async Task MigrateAsync(string connectionString, string? targetMigration = null)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, FlowbitDatabase.ConfigureProvider)
            .Options;
        await using var context = new AppDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    private async Task WithIsolatedDatabaseAsync(Func<string, Task> test)
    {
        var databaseName = "call_activity_" + Guid.NewGuid().ToString("N");
        var adminBuilder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "postgres" };
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        try
        {
            await test(new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName }.ConnectionString);
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
