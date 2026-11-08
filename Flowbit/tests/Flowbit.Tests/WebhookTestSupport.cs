using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Flowbit.Service.Models;
using Flowbit.Shared.Dtos;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Flowbit.Tests;

/// <summary>Generated, self-signed certificates that wrap test webhook key rings.</summary>
internal static class WebhookTestCertificates
{
    public static string Create(out string password)
    {
        password = Convert.ToHexString(RandomNumberGenerator.GetBytes(12));
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Flowbit webhook key ring (tests)",
            rsa,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(1));
        var path = Path.Combine(Path.GetTempPath(), $"flowbit-webhooks-{Guid.NewGuid():N}.pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
        return path;
    }

    public static void Delete(string? path)
    {
        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            File.Delete(path);
        }
    }
}

/// <summary>One recorded lifecycle event, read back for assertions.</summary>
internal sealed record CapturedLifecycleEvent(
    Guid Id,
    string Type,
    string Body,
    string SourceTable,
    long? SourceId,
    string? WorkflowKey,
    long? InstanceId,
    string[] RecipientUserIds,
    long[] TargetRevisionIds,
    DateTimeOffset OccurredAt)
{
    public JsonElement Data => JsonDocument.Parse(Body).RootElement.GetProperty("data");
}

/// <summary>
/// Direct database setup for capture tests. Capture is a deployment-wide switch, so every test that turns it
/// on restores the collection's baseline in a finally block.
/// </summary>
internal static class WebhookTestDatabase
{
    private static bool catchAll;
    private static Guid? catchAllSubscriptionId;
    private static long? catchAllRevisionId;
    private static Dictionary<string, long>? countsBeforeRebuild;
    private static readonly Dictionary<string, long> countsLostToRebuilds = new(StringComparer.Ordinal);

    public static async Task EnableCatchAllCaptureAsync(NpgsqlDataSource dataSource)
    {
        (catchAllSubscriptionId, catchAllRevisionId) = await CreateSubscriptionAsync(
            dataSource, LifecycleEventTypes.Subscribable, null, name: "producer sweep");
        await SetCaptureAsync(dataSource, true);
        catchAll = true;
    }

    /// <summary>Remembers the sweep's event counts before a test migrates the shared database down and up again.</summary>
    public static async Task CheckpointCatchAllCaptureAsync(NpgsqlDataSource dataSource)
    {
        if (catchAll)
        {
            countsBeforeRebuild = await CountEventsByTypeAsync(dataSource);
        }
    }

    /// <summary>
    /// Restores the producer sweep after a test migrated the shared database down and up again, which recreates
    /// the webhook tables empty and turns capture off. Counts from before the rebuild stay in the final report.
    /// </summary>
    public static async Task ReinstateCatchAllCaptureAsync(NpgsqlDataSource dataSource)
    {
        if (!catchAll)
        {
            return;
        }

        var exists = await CountEventsAsync(dataSource,
            "SELECT count(*) FROM flowbit.webhook_subscriptions WHERE \"Id\" = @id AND \"State\" = 'active'",
            ("id", catchAllSubscriptionId!.Value));
        if (exists == 0)
        {
            foreach (var (type, count) in countsBeforeRebuild ?? [])
            {
                countsLostToRebuilds[type] = countsLostToRebuilds.GetValueOrDefault(type) + count;
            }
            await EnableCatchAllCaptureAsync(dataSource);
        }
        countsBeforeRebuild = null;
        await SetCaptureAsync(dataSource, true);
    }

    /// <summary>Recorded events per type, including those a schema rebuild removed during the run.</summary>
    public static async Task<IReadOnlyList<(string Type, long Count)>> CaptureReportAsync(NpgsqlDataSource dataSource)
    {
        var counts = await CountEventsByTypeAsync(dataSource);
        foreach (var (type, count) in countsLostToRebuilds)
        {
            counts[type] = counts.GetValueOrDefault(type) + count;
        }
        return [.. counts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => (pair.Key, pair.Value))];
    }

    private static async Task<Dictionary<string, long>> CountEventsByTypeAsync(NpgsqlDataSource dataSource)
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT \"Type\", count(*) FROM flowbit.lifecycle_events GROUP BY \"Type\"", connection);
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            counts[reader.GetString(0)] = reader.GetInt64(1);
        }
        return counts;
    }

    /// <summary>The targets a test's own subscriptions received, leaving out the producer sweep's catch-all.</summary>
    public static long[] WithoutCatchAll(IEnumerable<long> revisionIds) =>
        [.. revisionIds.Where(id => id != catchAllRevisionId)];

    /// <summary>Creates an active subscription with a current revision and dispatch state, bypassing the API.</summary>
    public static async Task<(Guid SubscriptionId, long RevisionId)> CreateSubscriptionAsync(
        NpgsqlDataSource dataSource,
        IReadOnlyList<string> eventTypes,
        string? workflowKey,
        string state = "active",
        string url = "https://receiver.example.com/flowbit",
        string name = "capture test",
        int maxConcurrency = 4)
    {
        var id = Guid.CreateVersion7();
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            INSERT INTO flowbit.webhook_subscriptions
                ("Id", "Name", "State", "PauseReason", "MaxConcurrency", "Revision", "CreatedAt", "CreatedBy", "UpdatedAt", "UpdatedBy")
            VALUES (@id, @name, @state, CASE WHEN @state = 'paused' THEN 'administrator' END, @concurrency, 1, now(), 'tests', now(), 'tests');

            WITH revision AS (
                INSERT INTO flowbit.webhook_subscription_revisions
                    ("SubscriptionId", "ConfigurationRevision", "EndpointUrl", "EventTypes", "WorkflowKey", "CreatedAt", "CreatedBy")
                VALUES (@id, 1, @url, @types, @key, now(), 'tests')
                RETURNING "Id")
            UPDATE flowbit.webhook_subscriptions SET "CurrentRevisionId" = (SELECT "Id" FROM revision) WHERE "Id" = @id;

            INSERT INTO flowbit.webhook_subscription_dispatch_state ("SubscriptionId", "ConsecutiveFailures", "BackoffSeconds", "UpdatedAt")
            VALUES (@id, 0, 0, now());

            SELECT "CurrentRevisionId" FROM flowbit.webhook_subscriptions WHERE "Id" = @id;
            """, connection);
        command.Parameters.AddWithValue("id", id);
        command.Parameters.AddWithValue("name", name);
        command.Parameters.AddWithValue("state", state);
        command.Parameters.AddWithValue("concurrency", maxConcurrency);
        command.Parameters.AddWithValue("url", url);
        command.Parameters.Add(new NpgsqlParameter("types", NpgsqlDbType.Array | NpgsqlDbType.Text) { Value = eventTypes.ToArray() });
        command.Parameters.Add(new NpgsqlParameter("key", NpgsqlDbType.Varchar) { Value = (object?)workflowKey ?? DBNull.Value });
        var revisionId = (long)(await command.ExecuteScalarAsync())!;
        return (id, revisionId);
    }

    public static async Task SetSubscriptionStateAsync(NpgsqlDataSource dataSource, Guid subscriptionId, string state)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            UPDATE flowbit.webhook_subscriptions
               SET "State" = @state,
                   "PauseReason" = CASE WHEN @state = 'paused' THEN 'administrator' END,
                   "ArchivedAt" = CASE WHEN @state = 'archived' THEN now() END,
                   "ArchivedBy" = CASE WHEN @state = 'archived' THEN 'tests' END,
                   "Revision" = "Revision" + 1
             WHERE "Id" = @id
            """, connection);
        command.Parameters.AddWithValue("id", subscriptionId);
        command.Parameters.AddWithValue("state", state);
        await command.ExecuteNonQueryAsync();
    }

    public static async Task SetCaptureAsync(NpgsqlDataSource dataSource, bool enabled)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT flowbit.set_lifecycle_event_capture(@enabled, 'tests')", connection);
        command.Parameters.AddWithValue("enabled", enabled);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Returns capture to the collection's baseline: on only in the opt-in producer sweep.</summary>
    public static Task RestoreCaptureAsync(NpgsqlDataSource dataSource) => SetCaptureAsync(dataSource, catchAll);

    public static async Task<IReadOnlyList<CapturedLifecycleEvent>> ReadEventsAsync(
        NpgsqlDataSource dataSource,
        long revisionId)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT "Id", "Type", convert_from("Body", 'UTF8'), "SourceTable", "SourceId", "WorkflowKey", "InstanceId",
                   "RecipientUserIds", "TargetRevisionIds", "OccurredAt", "Body"
            FROM flowbit.lifecycle_events
            WHERE @revision = ANY("TargetRevisionIds")
            ORDER BY "RecordedAt", "Id"
            """, connection);
        command.Parameters.AddWithValue("revision", revisionId);
        var events = new List<CapturedLifecycleEvent>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var bytes = reader.GetFieldValue<byte[]>(10);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "Bodies have no BOM.");
            Assert.True(bytes.Length <= WebhookConstraints.MaxBodyBytes);
            events.Add(new CapturedLifecycleEvent(
                reader.GetGuid(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetInt64(4),
                reader.IsDBNull(5) ? null : reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetInt64(6),
                reader.GetFieldValue<string[]>(7),
                reader.GetFieldValue<long[]>(8),
                reader.GetFieldValue<DateTimeOffset>(9)));
        }
        return events;
    }

    public static async Task<long> CountEventsAsync(NpgsqlDataSource dataSource, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (parameterName, value) in parameters)
        {
            command.Parameters.AddWithValue(parameterName, value);
        }
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public static async Task ExecuteAsync(NpgsqlDataSource dataSource, string sql, params (string Name, object Value)[] parameters)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (parameterName, value) in parameters)
        {
            command.Parameters.AddWithValue(parameterName, value);
        }
        await command.ExecuteNonQueryAsync();
    }
}

/// <summary>
/// Golden-body checks: every field the catalog lists is present in catalog order, nothing else appears, and the
/// body parses into its typed record.
/// </summary>
internal static class LifecycleEventBodies
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static void AssertMatchesCatalog(CapturedLifecycleEvent captured)
    {
        using var document = JsonDocument.Parse(captured.Body);
        var root = document.RootElement;
        Assert.Equal(
            LifecycleEventCatalog.EnvelopeFields.Select(field => field.Name),
            root.EnumerateObject().Select(property => property.Name));
        var entry = LifecycleEventCatalog.Find(captured.Type);
        Assert.NotNull(entry);
        Assert.Equal(
            entry.DataFields.Select(field => field.Name),
            root.GetProperty("data").EnumerateObject().Select(property => property.Name));
        foreach (var field in entry.DataFields.Where(field => !field.Nullable))
        {
            Assert.NotEqual(JsonValueKind.Null, root.GetProperty("data").GetProperty(field.Name).ValueKind);
        }
        Assert.Equal(captured.Id.ToString("D"), root.GetProperty("eventId").GetString());
        Assert.Equal(captured.Type, root.GetProperty("type").GetString());
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(7, captured.Id.Version);
        // The body is compact, so the stored bytes are exactly what receivers verify.
        Assert.DoesNotContain(" : ", captured.Body, StringComparison.Ordinal);
        var timestamp = root.GetProperty("timestamp").GetString()!;
        Assert.Matches(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}\.\d{6}Z$", timestamp);
        Assert.Equal(captured.OccurredAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'"), timestamp);
        ParseTyped(captured);
    }

    public static object ParseTyped(CapturedLifecycleEvent captured) => captured.Type switch
    {
        LifecycleEventTypes.InstanceReactivated =>
            Deserialize<LifecycleEventEnvelopeDto<InstanceReactivatedEventDataDto>>(captured.Body),
        LifecycleEventTypes.InstanceStarted or LifecycleEventTypes.InstanceCompleted
            or LifecycleEventTypes.InstanceFaulted or LifecycleEventTypes.InstanceCancelled =>
            Deserialize<LifecycleEventEnvelopeDto<InstanceLifecycleEventDataDto>>(captured.Body),
        LifecycleEventTypes.TaskAssigned => Deserialize<LifecycleEventEnvelopeDto<TaskAssignedEventDataDto>>(captured.Body),
        LifecycleEventTypes.TaskCancelled => Deserialize<LifecycleEventEnvelopeDto<TaskCancelledEventDataDto>>(captured.Body),
        LifecycleEventTypes.TaskCreated or LifecycleEventTypes.TaskCompleted =>
            Deserialize<LifecycleEventEnvelopeDto<TaskLifecycleEventDataDto>>(captured.Body),
        LifecycleEventTypes.DelegationGranted =>
            Deserialize<LifecycleEventEnvelopeDto<DelegationGrantedEventDataDto>>(captured.Body),
        LifecycleEventTypes.IncidentOpened =>
            Deserialize<LifecycleEventEnvelopeDto<IncidentOpenedEventDataDto>>(captured.Body),
        LifecycleEventTypes.WebhookTest => Deserialize<LifecycleEventEnvelopeDto<WebhookTestEventDataDto>>(captured.Body),
        _ => throw new InvalidOperationException($"Unknown lifecycle event type {captured.Type}.")
    };

    public static T Deserialize<T>(string body) =>
        JsonSerializer.Deserialize<T>(Encoding.UTF8.GetBytes(body), Json)
        ?? throw new InvalidOperationException("Empty lifecycle event body.");
}
