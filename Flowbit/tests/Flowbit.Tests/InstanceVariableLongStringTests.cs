using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// A string instance variable longer than one B-tree index entry (2,704 bytes) used to fail
/// every write, because the current-value projection indexed each whole lowercased value.
/// The projection now indexes an MD5 of it, and case-insensitive search compares the full
/// value after matching the hash.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class InstanceVariableLongStringTests(PostgresApiFixture fixture)
{
    private const string HashIndex = "IX_iv_current_name_root_string_ci_hash_instance";
    private const string RemovedIndex = "IX_iv_current_name_root_string_ci_instance";

    // Hex digests do not compress, so this is the size PostgreSQL would have indexed.
    private static string IncompressibleText(int bytes, string seed)
    {
        var text = new StringBuilder();
        for (var block = 0; text.Length < bytes; block++)
            text.Append(Convert.ToHexStringLower(MD5.HashData(Encoding.UTF8.GetBytes(seed + block))));
        return text.ToString(0, bytes);
    }

    [Fact]
    public async Task Long_start_and_script_strings_save_and_case_insensitive_search_still_matches_exactly()
    {
        var key = $"long-strings-{Guid.NewGuid():N}";
        var definition = new WorkflowModel
        {
            Id = key, Name = key, InitialEventId = 1,
            Variables = [new() { Id = 1, Name = "note", DataType = "string", Nullable = true }],
            FlowNodes =
            [
                new() { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent,
                    Variables = [new() { Id = 10, Name = "memo", DataType = "string", Required = true }] },
                // A synchronous ordinary script writes 7 KB of text in the start transaction.
                new() { Id = 2, Name = "Write note", Type = BpmnFlowNodeTypes.ScriptTask, ScriptFormat = ScriptFormats.JavaScript,
                    Script = "var parts = []; var x = 12345;\n" +
                             "for (var i = 0; i < 1200; i++) { x = (x * 1103515245 + 12345) % 2147483648; parts.push(x.toString(36)); }\n" +
                             "execution.setVariable('note', parts.join(''));" },
                new() { Id = 3, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
                new() { Id = 4, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new() { Id = 101, SourceRef = 1, TargetRef = 2 },
                new() { Id = 201, SourceRef = 2, TargetRef = 3 },
                new() { Id = 301, SourceRef = 3, TargetRef = 4, Name = "Done" }
            ]
        };
        var published = await PublishAsync(definition);
        var memo = IncompressibleText(6_400, key);
        var longId = await StartAsync(published.Id, memo);
        var shortId = await StartAsync(published.Id, "Short memo");

        await using (var db = fixture.CreateDbContext())
        {
            var stored = await db.InstanceVariableCurrentValues.AsNoTracking()
                .Where(row => row.InstanceId == longId).ToDictionaryAsync(row => row.VariableName, row => row.ValueJson.RootElement.GetString());
            Assert.Equal(memo, stored["memo"]);
            Assert.True(stored["note"]!.Length > 6_000);
            Assert.Equal(WorkflowInstanceStatuses.Running,
                (await db.WorkflowInstances.SingleAsync(row => row.Id == longId)).Status);
        }

        Assert.Equal([longId], await SearchAsync(key, "memo", memo.ToUpperInvariant()));
        Assert.Equal([shortId], await SearchAsync(key, "memo", "SHORT MEMO"));
        // Same hash prefix of work, different text: no match.
        Assert.Empty(await SearchAsync(key, "memo", memo[..^1]));
        // The script is deterministic, so both instances hold the same long note.
        Assert.Equal([longId, shortId], await SearchAsync(key, "note",
            (await NoteAsync(longId)).ToUpperInvariant()));
    }

    [Fact]
    public async Task Migrated_projection_indexes_a_hash_that_the_case_insensitive_predicate_can_use()
    {
        var databaseName = "long_string_index_" + Guid.NewGuid().ToString("N");
        var admin = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "postgres" };
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", connection).ExecuteNonQueryAsync();
        }
        var isolated = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName }.ConnectionString;
        try
        {
            await using (var context = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                             .UseNpgsql(isolated, FlowbitDatabase.ConfigureProvider).Options))
                await context.GetService<IMigrator>().MigrateAsync();

            await using var connection = new NpgsqlConnection(isolated);
            await connection.OpenAsync();
            var indexes = new List<string>();
            await using (var read = new NpgsqlCommand(
                             "SELECT indexname FROM pg_indexes WHERE schemaname = 'flowbit' AND tablename = 'instance_variable_current_values'",
                             connection))
            await using (var reader = await read.ExecuteReaderAsync())
                while (await reader.ReadAsync()) indexes.Add(reader.GetString(0));
            Assert.Contains(HashIndex, indexes);
            Assert.DoesNotContain(RemovedIndex, indexes);

            // Enough distinct values of one name that the hash is the selective index.
            await new NpgsqlCommand(
                """
                WITH definition_row AS (
                    INSERT INTO flowbit.workflow_definitions ("Name", "WorkflowKey", "Version", "Definition", "IsPublished", "IsDefault", "CreatedAt")
                    VALUES ('long strings', 'long-strings', 1, '{"id":"long-strings","name":"long strings"}', false, false, now())
                    RETURNING "Id"),
                instances AS (
                    INSERT INTO flowbit.workflow_instances ("WorkflowDefinitionId", "WorkflowKey", "Status", "StartedBy", "CreatedAt", "UpdatedAt")
                    SELECT definition_row."Id", 'long-strings', 'running', 'index-test', now(), now()
                    FROM definition_row, generate_series(1, 3000)
                    RETURNING "Id")
                INSERT INTO flowbit.instance_variables ("InstanceId", "VariableName", "ValueJson", "SetAt")
                SELECT "Id", 'note', to_jsonb(repeat(md5("Id"::text), 100)), now() FROM instances;
                ANALYZE flowbit.instance_variable_current_values;
                """, connection).ExecuteNonQueryAsync();

            // The predicate exactly as VariableFilterSqlCompiler writes it for $eqIgnoreCase.
            await using var explain = new NpgsqlCommand(
                """
                EXPLAIN SELECT v."InstanceId" FROM flowbit.instance_variable_current_values v
                WHERE v."VariableName" = 'note'
                  AND jsonb_typeof(v."ValueJson") = 'string'
                  AND md5(lower((v."ValueJson" #>> ARRAY[]::text[]))) = md5(lower(@value))
                  AND lower((v."ValueJson" #>> ARRAY[]::text[])) = lower(@value)
                """, connection);
            explain.Parameters.AddWithValue("value", "NOT-A-STORED-VALUE");
            var plan = new StringBuilder();
            await using (var reader = await explain.ExecuteReaderAsync())
                while (await reader.ReadAsync()) plan.AppendLine(reader.GetString(0));
            Assert.Contains(HashIndex, plan.ToString());
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var connection = new NpgsqlConnection(admin.ConnectionString);
            await connection.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE \"{databaseName}\" WITH (FORCE)", connection).ExecuteNonQueryAsync();
        }
    }

    [Fact]
    public void Case_insensitive_filter_matches_the_hash_before_comparing_the_full_value()
    {
        using var document = JsonDocument.Parse("""{ "memo": { "$eqIgnoreCase": "Value" } }""");
        var where = new StringBuilder();
        var arguments = new List<(string Name, object Value)>();
        VariableFilterSqlCompiler.Append(where, arguments, VariableFilterParser.Parse(document.RootElement), "w.\"Id\"");

        var sql = where.ToString();
        Assert.Contains("md5(lower((current_variable_0.\"ValueJson\" #>> ARRAY[]::text[]))) = md5(lower(@advancedVariableFilter1))", sql);
        Assert.Contains("lower((current_variable_0.\"ValueJson\" #>> ARRAY[]::text[])) = lower(@advancedVariableFilter1)", sql);
        Assert.Equal(2, arguments.Count);
    }

    private async Task<WorkflowDetailDto> PublishAsync(WorkflowModel definition)
    {
        var request = ApiTestAuth.Authorize(new HttpRequestMessage(HttpMethod.Post, "/api/workflows")
        {
            Content = JsonContent.Create(new CreateWorkflowRequest(definition, true))
        }, "long-string-admin", "admin");
        using var response = await fixture.Client.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.Created, text);
        return JsonSerializer.Deserialize<WorkflowDetailDto>(text, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
    }

    private async Task<long> StartAsync(long workflowId, string memo)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var started = await scope.ServiceProvider.GetRequiredService<IWorkflowEngineService>().StartInstanceSlimAsync(
            workflowId, null, new ActorContext("starter", [], new Dictionary<string, string>()), null,
            new Dictionary<string, JsonElement> { ["memo"] = JsonSerializer.SerializeToElement(memo) },
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase), CancellationToken.None);
        return started.Id;
    }

    private async Task<string> NoteAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        return (await db.InstanceVariableCurrentValues.AsNoTracking()
            .SingleAsync(row => row.InstanceId == instanceId && row.VariableName == "note")).ValueJson.RootElement.GetString()!;
    }

    private async Task<long[]> SearchAsync(string workflowKey, string variable, string value)
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(new Dictionary<string, object>
        {
            [variable] = new Dictionary<string, string> { ["$eqIgnoreCase"] = value }
        }));
        await using var context = fixture.CreateDbContext();
        var result = await new WorkflowRuntimeRepository(context).ListInstancesAsync(
            status: null, instanceId: null, workflowId: null, workflowKey, businessKey: null, nodeId: null,
            nodeExternalId: null, VariableFilterParser.Parse(document.RootElement),
            [new InstanceSortCriterion(InstanceSortField.Id, SortDirection.Ascending)],
            InstanceListAuthorization.Global, cursor: null, includeVariables: false, page: 1, pageSize: 50,
            CancellationToken.None);
        return result.Items.Select(item => item.Id).ToArray();
    }
}
