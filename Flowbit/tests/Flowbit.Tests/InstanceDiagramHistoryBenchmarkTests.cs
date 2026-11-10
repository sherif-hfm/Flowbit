using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using Flowbit.Shared.Dtos;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class InstanceDiagramHistoryBenchmarkCollection : ICollectionFixture<PostgresApiFixture>
{
    public const string Name = "postgres-diagram-history-benchmark";
}

[Collection(InstanceDiagramHistoryBenchmarkCollection.Name)]
public sealed class InstanceDiagramHistoryBenchmarkTests(PostgresApiFixture fixture)
    : InstanceDiagramHistoryApiTestBase(fixture)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HundredThousandRetainedVisitsStayExactAndGraphSized(bool multiInstance)
    {
        var model = Model(multiInstance);
        if (multiInstance) AddAsyncSource(model);
        else model.SequenceFlows.Add(new() { Id = 202, SourceRef = 2, TargetRef = 2, Name = "Again" });
        var instance = await CreateAndStartAsync(model);
        if (multiInstance) await ProcessAsync(instance.Id, "asyncAfter");
        var artifacts = Path.Combine(RepositoryRoot(), "artifacts", "diagram-history",
            $"{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}-{(multiInstance ? "mi" : "loop")}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(artifacts);
        var small = await MeasureAsync(instance.Id);

        // This is an isolated load fixture for grouping/query plans. Runtime
        // traversal correctness is covered separately with real engine actions.
        // These rows do not serve a browser or acceptance workflow.
        await using var connection = await Fixture.DataSource.OpenConnectionAsync();
        await using (var seed = new NpgsqlCommand(multiInstance ? MultiInstanceLoadSql : LoopLoadSql, connection))
        {
            // Loading three related 100k-row tables is setup, not the measured
            // request. A full-suite database can need longer than the default.
            seed.CommandTimeout = 180;
            seed.Parameters.AddWithValue("instanceId", instance.Id);
            await seed.ExecuteNonQueryAsync();
        }
        await using (var analyze = new NpgsqlCommand("ANALYZE flowbit.node_executions", connection))
            await analyze.ExecuteNonQueryAsync();

        Fixture.CommandCounter.Reset(captureReaderCommandTexts: true);
        var result = await HistoryAsync(instance.Id);
        var sql = Assert.Single(Fixture.CommandCounter.ReaderCommandTexts);
        var measured = await MeasureAsync(instance.Id);
        Assert.Equal(multiInstance ? 101 : 100_000, result.Flows.Single(flow => flow.FlowId == (multiInstance ? 501 : 202)).TraversalCount);
        Assert.True(result.Nodes.Count <= model.FlowNodes.Count);
        Assert.True(result.Flows.Count <= model.SequenceFlows.Count);
        Assert.True(measured.Bytes < 2_000, $"Graph-sized response unexpectedly grew to {measured.Bytes} bytes.");

        string plan;
        await using (var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + sql, connection))
        {
            explain.Parameters.AddWithValue("instanceId", instance.Id);
            plan = (string)(await explain.ExecuteScalarAsync())!;
        }
        await File.WriteAllTextAsync(Path.Combine(artifacts, "query-plan.json"), plan);
        await File.WriteAllTextAsync(Path.Combine(artifacts, "summary.sql"), sql);
        var targetMet = measured.Milliseconds.Max() < 1_000;
        var enforceLatencyTarget = string.Equals(
            Environment.GetEnvironmentVariable("FLOWBIT_DIAGRAM_HISTORY_ENFORCE_LATENCY"),
            "1", StringComparison.Ordinal);
        await File.WriteAllTextAsync(Path.Combine(artifacts, "benchmark.json"), JsonSerializer.Serialize(new
        {
            scenario = multiInstance ? "multiInstance" : "loop",
            addedNodeExecutions = 100_000,
            totalNodeExecutions = multiInstance ? 100_004 : 100_002,
            postgres = connection.PostgreSqlVersion.ToString(),
            os = RuntimeInformation.OSDescription,
            runtime = RuntimeInformation.FrameworkDescription,
            processors = Environment.ProcessorCount,
            availableMemoryBytes = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes,
            small,
            large = measured,
            response = result,
            targetMilliseconds = 1_000,
            targetMet,
            enforceLatencyTarget,
            note = "Disposable postgres:17-alpine on local Docker; warmed in-process authenticated HTTP requests. Enforce the local timing target only in an isolated run. No new index or evidence truncation."
        }, new JsonSerializerOptions(JsonOptions) { WriteIndented = true }));
        Assert.True(!enforceLatencyTarget || targetMet,
            $"Warmed response exceeded the local 1-second validation target. Inspect {artifacts}.");
    }

    private async Task<BenchmarkMeasurement> MeasureAsync(long instanceId)
    {
        _ = await HistoryAsync(instanceId); // warm the complete HTTP/serializer/query path
        var times = new List<double>();
        var bytes = 0;
        for (var iteration = 0; iteration < 5; iteration++)
        {
            var stopwatch = Stopwatch.StartNew();
            using var response = await SendAsync(HttpMethod.Get, $"/api/instances/{instanceId}/diagram-history");
            response.EnsureSuccessStatusCode();
            var body = await response.Content.ReadAsByteArrayAsync();
            stopwatch.Stop();
            bytes = body.Length;
            times.Add(stopwatch.Elapsed.TotalMilliseconds);
        }
        return new BenchmarkMeasurement(bytes, times);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AGENTS.md"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository root was not found for benchmark evidence.");
    }

    private sealed record BenchmarkMeasurement(int Bytes, IReadOnlyList<double> Milliseconds);

    private const string LoopLoadSql = """
        INSERT INTO flowbit.node_executions
            ("InstanceId", "WorkflowDefinitionId", "ExecutionTokenId", "NodeId", "NodeName", "NodeType",
             "ExecutionKind", "Status", "CompletionReason", "EnteredViaFlowId", "ExitedViaFlowId",
             "CreatedAt", "StartedAt", "UpdatedAt", "CompletedAt", "IsCutoverSeeded")
        SELECT i."Id", i."WorkflowDefinitionId", t."Id", 2, 'Review', 'userTask',
               'node', 'completed', 'userAction', 202, 202, now(), now(), now(), now(), false
        FROM flowbit.workflow_instances i
        JOIN flowbit.execution_tokens t ON t."InstanceId" = i."Id"
        CROSS JOIN generate_series(1, 100000)
        WHERE i."Id" = @instanceId
        """;

    private const string MultiInstanceLoadSql = """
        WITH inserted_activations AS (
            INSERT INTO flowbit.multi_instance_executions
                ("InstanceId", "TokenId", "NodeId", "RolePolicyId", "Mode", "Source", "OnePerActor",
                 "ResultVariable", "Status", "TotalCount", "CompletedCount", "CancelledCount",
                 "CreatedAt", "UpdatedAt", "CompletedAt", "WinningFlowId", "CompletionReason")
            SELECT original."InstanceId", original."TokenId", 2, original."RolePolicyId", 'sequential', 'cardinality', false,
                   'results', 'completed', 1000, 1000, 0, now(), now(), now(), 201, 'all'
            FROM flowbit.multi_instance_executions original
            CROSS JOIN generate_series(1, 100)
            WHERE original."InstanceId" = @instanceId AND original."Status" = 'active'
            RETURNING "Id", "InstanceId", "TokenId", "RolePolicyId"
        ), inserted_tasks AS (
            INSERT INTO flowbit.user_tasks
                ("InstanceId", "TokenId", "NodeId", "NodeName", "Roles", "RolePolicyId", "RequiresClaim",
                 "RequiresAssignment", "Status", "CreatedAt", "UpdatedAt", "CompletedAt",
                 "MultiInstanceExecutionId", "ItemIndex", "SelectedFlowId", "CompletedBy", "CompletedByRoles")
            SELECT a."InstanceId", a."TokenId", 2, 'Review', ARRAY[]::text[], a."RolePolicyId", false,
                   false, 'completed', now(), now(), now(), a."Id", n, 201, 'load-fixture', ARRAY[]::text[]
            FROM inserted_activations a CROSS JOIN generate_series(0, 999) n
            RETURNING "Id", "InstanceId", "TokenId", "MultiInstanceExecutionId", "ItemIndex"
        )
        INSERT INTO flowbit.node_executions
            ("InstanceId", "WorkflowDefinitionId", "ExecutionTokenId", "UserTaskId", "MultiInstanceExecutionId",
             "ItemIndex", "NodeId", "NodeName", "NodeType", "ExecutionKind", "Status", "CompletionReason",
             "EnteredViaFlowId", "SelectedFlowId", "CreatedAt", "StartedAt", "UpdatedAt", "CompletedAt", "IsCutoverSeeded")
        SELECT t."InstanceId", i."WorkflowDefinitionId", t."TokenId", t."Id", t."MultiInstanceExecutionId",
               t."ItemIndex", 2, 'Review', 'userTask', 'userTaskItem', 'completed', 'multiInstanceItem',
               501, 201, now(), now(), now(), now(), false
        FROM inserted_tasks t JOIN flowbit.workflow_instances i ON i."Id" = t."InstanceId"
        """;
}
