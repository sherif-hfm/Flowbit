using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using NpgsqlTypes;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class LifecycleCaptureSettingsMigrationTests(PostgresApiFixture fixture)
{
    private const string PreviousMigration = "20261107154545_AddOutboundLifecycleWebhooks";
    private const string TargetMigration = "20261108120000_ConsolidateLifecycleCaptureSettings";
    private const string CapturePredicate = "\"Namespace\" = 'Webhooks' AND \"Key\" = 'CaptureEnabled'";
    private const string CaptureGate = "SELECT flowbit.lifecycle_capture_wanted(ARRAY['instance.started'])";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpgradeAndDowngradePreserveCaptureStateAndKnownProvenance(bool enabled)
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, PreviousMigration);
            await ExecuteAsync(connectionString, """
                UPDATE flowbit.lifecycle_event_capture_state
                SET "Enabled" = @enabled, "ChangedAt" = '2026-10-01T12:30:00Z', "ChangedBy" = 'original-operator'
                WHERE "Id" = 1;
                """, new NpgsqlParameter("enabled", enabled));

            await MigrateAsync(connectionString, TargetMigration);

            var captured = await ReadSettingAsync(connectionString);
            Assert.Equal(enabled ? "true" : "false", captured.Value);
            Assert.Equal(DateTimeOffset.Parse("2026-10-01T12:30:00Z"), captured.CreatedAt);
            Assert.Equal(captured.CreatedAt, captured.UpdatedAt);
            Assert.Null(captured.CreatedBy);
            Assert.Equal("original-operator", captured.UpdatedBy);
            Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM flowbit.webhook_management_audit"));
            Assert.Equal(0L, await ScalarAsync<long>(connectionString, """
                SELECT count(*) FROM flowbit.engine_settings
                WHERE ("CreatedBy" IS NOT NULL OR "UpdatedBy" IS NOT NULL)
                  AND NOT ("Namespace" = 'Webhooks' AND "Key" = 'CaptureEnabled')
                """));
            Assert.Equal(0L, await ScalarAsync<long>(connectionString, """
                SELECT count(*) FROM pg_catalog.pg_class c JOIN pg_catalog.pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'flowbit' AND c.relname = 'lifecycle_event_capture_state'
                """));

            // Rollback carries the current setting value and operator, including edits after the upgrade.
            await ExecuteAsync(connectionString,
                "SELECT flowbit.set_lifecycle_event_capture(@enabled, 'rollback-operator')",
                new NpgsqlParameter("enabled", !enabled));
            var changed = await ReadSettingAsync(connectionString);
            await MigrateAsync(connectionString, PreviousMigration);
            Assert.Equal(!enabled, await ScalarAsync<bool>(connectionString,
                "SELECT \"Enabled\" FROM flowbit.lifecycle_event_capture_state WHERE \"Id\" = 1"));
            Assert.Equal(changed.UpdatedAt.UtcDateTime, await ScalarAsync<DateTime>(connectionString,
                "SELECT \"ChangedAt\" FROM flowbit.lifecycle_event_capture_state WHERE \"Id\" = 1"));
            Assert.Equal("rollback-operator", await ScalarAsync<string>(connectionString,
                "SELECT \"ChangedBy\" FROM flowbit.lifecycle_event_capture_state WHERE \"Id\" = 1"));
            Assert.Equal(0L, await ScalarAsync<long>(connectionString, """
                SELECT count(*) FROM information_schema.columns
                WHERE table_schema = 'flowbit' AND table_name = 'engine_settings' AND column_name IN ('CreatedBy', 'UpdatedBy')
                """));

            await MigrateAsync(connectionString, TargetMigration);
            Assert.Equal(changed with { CreatedAt = changed.UpdatedAt }, await ReadSettingAsync(connectionString));
        });
    }

    [Fact]
    public async Task FreshMigrationGuardsTheSettingAndAuditsOnlyActualToggles()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, TargetMigration);
            var seeded = await ReadSettingAsync(connectionString);
            Assert.Equal("false", seeded.Value);
            Assert.Null(seeded.CreatedBy);
            Assert.Null(seeded.UpdatedBy);

            var invalidStatements = new[]
            {
                "UPDATE flowbit.engine_settings SET \"Value\" = 'TRUE' WHERE " + CapturePredicate,
                "UPDATE flowbit.engine_settings SET \"Value\" = 'not-a-boolean' WHERE " + CapturePredicate,
                "UPDATE flowbit.engine_settings SET \"Value\" = 'true', \"UpdatedBy\" = NULL WHERE " + CapturePredicate,
                "UPDATE flowbit.engine_settings SET \"Value\" = 'true', \"UpdatedBy\" = ' ' WHERE " + CapturePredicate,
                "DELETE FROM flowbit.engine_settings WHERE " + CapturePredicate,
                "UPDATE flowbit.engine_settings SET \"Key\" = 'MovedCapture' WHERE " + CapturePredicate,
                "UPDATE flowbit.engine_settings SET \"Namespace\" = 'Other' WHERE " + CapturePredicate,
                "INSERT INTO flowbit.engine_settings (\"Namespace\", \"Key\", \"Value\") VALUES (NULL, 'Webhooks.CaptureEnabled', 'false')",
                "INSERT INTO flowbit.engine_settings (\"Namespace\", \"Key\", \"Value\") VALUES ('webhooks', 'captureenabled', 'false')",
                "INSERT INTO flowbit.engine_settings (\"Namespace\", \"Key\", \"Value\") VALUES (' Webhooks ', 'CaptureEnabled', 'false')",
                "INSERT INTO flowbit.engine_settings (\"Namespace\", \"Key\", \"Value\") VALUES (E'\\tWebhooks', 'CaptureEnabled', 'false')",
                "INSERT INTO flowbit.engine_settings (\"Namespace\", \"Key\", \"Value\") VALUES ('Webhooks', E'CaptureEnabled\\n', 'false')",
                "INSERT INTO flowbit.engine_settings (\"Namespace\", \"Key\", \"Value\") VALUES (U&'\\00A0Webhooks\\00A0', 'CaptureEnabled', 'false')",
                "INSERT INTO flowbit.engine_settings (\"Namespace\", \"Key\", \"Value\") VALUES (E'\\t\\n', 'Webhooks.CaptureEnabled', 'false')"
            };
            foreach (var statement in invalidStatements)
            {
                var exception = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString, statement));
                Assert.Equal(PostgresErrorCodes.CheckViolation, exception.SqlState);
                Assert.Equal(seeded, await ReadSettingAsync(connectionString));
            }

            // Ordinary metadata editing still works, and an already-future timestamp must not let the SQL setter
            // reuse an optimistic concurrency token or move it backwards.
            await ExecuteAsync(connectionString, """
                UPDATE flowbit.engine_settings
                SET "Description" = 'Edited description', "UpdatedAt" = '2100-01-01T00:00:00Z', "UpdatedBy" = 'description-editor'
                WHERE "Namespace" = 'Webhooks' AND "Key" = 'CaptureEnabled';
                """);
            Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM flowbit.webhook_management_audit"));
            await ExecuteAsync(connectionString, "SELECT flowbit.set_lifecycle_event_capture(true, ' first-operator ')");
            var first = await ReadSettingAsync(connectionString);
            Assert.Equal("true", first.Value);
            Assert.Equal("first-operator", first.UpdatedBy);
            Assert.Equal(DateTimeOffset.Parse("2100-01-01T00:00:00Z").AddTicks(10), first.UpdatedAt);
            Assert.Equal(seeded.CreatedAt, first.CreatedAt);
            Assert.Null(first.CreatedBy);

            await ExecuteAsync(connectionString, "SELECT flowbit.set_lifecycle_event_capture(true, 'unchanged-operator')");
            Assert.Equal(first, await ReadSettingAsync(connectionString));
            await ExecuteAsync(connectionString, """
                UPDATE flowbit.engine_settings
                SET "Value" = 'false', "UpdatedAt" = "UpdatedAt" + interval '1 microsecond', "UpdatedBy" = 'second-operator'
                WHERE "Namespace" = 'Webhooks' AND "Key" = 'CaptureEnabled';
                """);
            Assert.Equal(2L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM flowbit.webhook_management_audit"));
            Assert.Equal("captureEnabled:first-operator:true,captureDisabled:second-operator:false",
                await ScalarAsync<string>(connectionString, """
                    SELECT string_agg("Operation" || ':' || "Actor" || ':' || ("ChangesJson"->>'enabled'), ',' ORDER BY "Id")
                    FROM flowbit.webhook_management_audit
                    """));

            // Guard enforcement and auditing are transactional with the settings write.
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync();
            await using (var transaction = await connection.BeginTransactionAsync())
            {
                await using var command = new NpgsqlCommand("SELECT flowbit.set_lifecycle_event_capture(true, 'rolled-back')", connection, transaction);
                await command.ExecuteNonQueryAsync();
                await transaction.RollbackAsync();
            }
            Assert.Equal("false", (await ReadSettingAsync(connectionString)).Value);
            Assert.Equal(2L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM flowbit.webhook_management_audit"));
        });
    }

    [Fact]
    public async Task SqlCaptureWritesRejectWhitespaceOnlyActorsAndNormalizeValidActors()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, TargetMigration);
            var seeded = await ReadSettingAsync(connectionString);
            foreach (var actor in new[] { "\t", "\r\n", "\v\f", "\u00A0", " \t\n\u0085\u1680\u2000\u2001\u2002\u2003\u2004\u2005\u2006\u2007\u2008\u2009\u200A\u2028\u2029\u202F\u205F\u3000" })
            {
                var helperException = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
                    "SELECT flowbit.set_lifecycle_event_capture(true, @actor)", new NpgsqlParameter("actor", actor)));
                Assert.Contains("operator_name must be a nonblank name", helperException.MessageText, StringComparison.Ordinal);
                Assert.Equal(seeded, await ReadSettingAsync(connectionString));

                var guardException = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(connectionString,
                    "UPDATE flowbit.engine_settings SET \"Value\" = 'true', \"UpdatedBy\" = @actor, \"UpdatedAt\" = now() WHERE " + CapturePredicate,
                    new NpgsqlParameter("actor", actor)));
                Assert.Equal(PostgresErrorCodes.CheckViolation, guardException.SqlState);
                Assert.Equal(seeded, await ReadSettingAsync(connectionString));
                Assert.Equal(0L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM flowbit.webhook_management_audit"));
            }

            await ExecuteAsync(connectionString, "SELECT flowbit.set_lifecycle_event_capture(true, @actor)",
                new NpgsqlParameter("actor", "\t\u00A0 first-operator \u3000\n"));
            var enabled = await ReadSettingAsync(connectionString);
            Assert.Equal("first-operator", enabled.UpdatedBy);
            Assert.Equal("first-operator", await ScalarAsync<string>(connectionString,
                "SELECT \"Actor\" FROM flowbit.webhook_management_audit ORDER BY \"Id\" DESC LIMIT 1"));

            await ExecuteAsync(connectionString,
                "UPDATE flowbit.engine_settings SET \"Value\" = 'false', \"UpdatedBy\" = @actor, \"UpdatedAt\" = now() WHERE " + CapturePredicate,
                new NpgsqlParameter("actor", "\n\u2003 second-operator \u202F\t"));
            var disabled = await ReadSettingAsync(connectionString);
            Assert.Equal("second-operator", disabled.UpdatedBy);
            Assert.Equal(seeded.CreatedAt, disabled.CreatedAt);
            Assert.Null(disabled.CreatedBy);
            Assert.Equal("second-operator", await ScalarAsync<string>(connectionString,
                "SELECT \"Actor\" FROM flowbit.webhook_management_audit ORDER BY \"Id\" DESC LIMIT 1"));
            Assert.Equal(2L, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM flowbit.webhook_management_audit"));
        });
    }

    [Fact]
    public async Task CaptureGateAndWorkerSnapshotFailClosedForMissingOrMalformedConfiguration()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, TargetMigration);
            await AddMatchingSubscriptionAsync(connectionString);
            await using var source = new WebhookDataSource(connectionString, 4);
            var dispatch = new WebhookDispatchRepository(source);

            await AssertCaptureAsync(false);
            await ExecuteAsync(connectionString, "SELECT flowbit.set_lifecycle_event_capture(true, 'capture-test')");
            await AssertCaptureAsync(true);
            Assert.False(await ScalarAsync<bool>(connectionString,
                "SELECT flowbit.lifecycle_capture_wanted(ARRAY['instance.completed'])"));

            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var transaction = await connection.BeginTransactionAsync();
                await using var command = new NpgsqlCommand("SET LOCAL flowbit.lifecycle_events = 'off'; " + CaptureGate, connection, transaction);
                Assert.Equal(false, await command.ExecuteScalarAsync());
                await transaction.RollbackAsync();
            }
            await AssertCaptureAsync(true);

            // Bypass both write triggers only inside this disposable database to test the defensive readers.
            await ExecuteAsync(connectionString, """
                ALTER TABLE flowbit.engine_settings DISABLE TRIGGER "TR_engine_settings_capture_guard";
                ALTER TABLE flowbit.engine_settings DISABLE TRIGGER "TR_engine_settings_capture_audit";
                """);
            foreach (var invalid in new[] { "not-a-boolean", "", " TRUE " })
            {
                await ExecuteAsync(connectionString,
                    "UPDATE flowbit.engine_settings SET \"Value\" = @value WHERE " + CapturePredicate,
                    new NpgsqlParameter("value", invalid));
                await AssertCaptureAsync(false);
            }
            await ExecuteAsync(connectionString, "DELETE FROM flowbit.engine_settings WHERE " + CapturePredicate);
            await AssertCaptureAsync(false);

            async Task AssertCaptureAsync(bool expected)
            {
                Assert.Equal(expected, await ScalarAsync<bool>(connectionString, CaptureGate));
                Assert.Equal(expected, (await dispatch.GetQueueSnapshotAsync(CancellationToken.None)).CaptureEnabled);
            }
        });
    }

    [Fact]
    public async Task DowngradeRestoresTheLegacySetterGateAndToggleOnlyAuditing()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, TargetMigration);
            await AddMatchingSubscriptionAsync(connectionString);
            await ExecuteAsync(connectionString, "SELECT flowbit.set_lifecycle_event_capture(true, 'before-downgrade')");
            await MigrateAsync(connectionString, PreviousMigration);
            Assert.True(await ScalarAsync<bool>(connectionString, CaptureGate));
            var auditBefore = await ScalarAsync<long>(connectionString, "SELECT count(*) FROM flowbit.webhook_management_audit");

            await ExecuteAsync(connectionString, "SELECT flowbit.set_lifecycle_event_capture(false, 'legacy-disable')");
            Assert.False(await ScalarAsync<bool>(connectionString, CaptureGate));
            await ExecuteAsync(connectionString, "SELECT flowbit.set_lifecycle_event_capture(true, 'legacy-enable')");
            Assert.True(await ScalarAsync<bool>(connectionString, CaptureGate));
            Assert.False(await ScalarAsync<bool>(connectionString,
                "SELECT flowbit.lifecycle_capture_wanted(ARRAY['instance.completed'])"));
            var changedAt = await ScalarAsync<DateTime>(connectionString,
                "SELECT \"ChangedAt\" FROM flowbit.lifecycle_event_capture_state WHERE \"Id\" = 1");

            await ExecuteAsync(connectionString, "SELECT flowbit.set_lifecycle_event_capture(true, 'legacy-noop')");
            Assert.Equal(changedAt, await ScalarAsync<DateTime>(connectionString,
                "SELECT \"ChangedAt\" FROM flowbit.lifecycle_event_capture_state WHERE \"Id\" = 1"));
            Assert.Equal("legacy-enable", await ScalarAsync<string>(connectionString,
                "SELECT \"ChangedBy\" FROM flowbit.lifecycle_event_capture_state WHERE \"Id\" = 1"));
            Assert.Equal(auditBefore + 2, await ScalarAsync<long>(connectionString, "SELECT count(*) FROM flowbit.webhook_management_audit"));

            await MigrateAsync(connectionString, TargetMigration);
            Assert.True(await ScalarAsync<bool>(connectionString, CaptureGate));
            var restored = await ReadSettingAsync(connectionString);
            Assert.Equal("true", restored.Value);
            Assert.Equal(changedAt, restored.UpdatedAt.UtcDateTime);
            Assert.Equal(restored.UpdatedAt, restored.CreatedAt);
            Assert.Equal("legacy-enable", restored.UpdatedBy);
            Assert.Null(restored.CreatedBy);
        });
    }

    [Fact]
    public async Task UpgradeRejectsExistingCanonicalAndEquivalentKeysWithoutChangingTheOldSwitch()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, PreviousMigration);
            (string? Namespace, string Key)[] conflicts =
            [
                ("Webhooks", "CaptureEnabled"),
                (null, "Webhooks.CaptureEnabled"),
                ("webhooks", "captureenabled"),
                (" Webhooks ", "CaptureEnabled"),
                ("\tWebhooks", "CaptureEnabled"),
                ("Webhooks", "CaptureEnabled\n"),
                ("\u00A0Webhooks\u00A0", "CaptureEnabled"),
                ("\t\n", "Webhooks.CaptureEnabled")
            ];
            foreach (var conflict in conflicts)
            {
                await ExecuteAsync(connectionString, """
                    INSERT INTO flowbit.engine_settings ("Namespace", "Key", "Value") VALUES (@namespace, @key, 'true')
                    """, new NpgsqlParameter("namespace", NpgsqlDbType.Text) { Value = (object?)conflict.Namespace ?? DBNull.Value },
                    new NpgsqlParameter("key", conflict.Key));
                var exception = await Assert.ThrowsAsync<PostgresException>(() => MigrateAsync(connectionString, TargetMigration));
                Assert.Contains("equivalent alias already exists", exception.MessageText, StringComparison.Ordinal);
                Assert.False(await ScalarAsync<bool>(connectionString,
                    "SELECT \"Enabled\" FROM flowbit.lifecycle_event_capture_state WHERE \"Id\" = 1"));
                Assert.Equal(0L, await ScalarAsync<long>(connectionString, """
                    SELECT count(*) FROM information_schema.columns
                    WHERE table_schema = 'flowbit' AND table_name = 'engine_settings' AND column_name = 'CreatedBy'
                    """));
                await ExecuteAsync(connectionString, """
                    DELETE FROM flowbit.engine_settings WHERE "Namespace" IS NOT DISTINCT FROM @namespace AND "Key" = @key
                    """, new NpgsqlParameter("namespace", NpgsqlDbType.Text) { Value = (object?)conflict.Namespace ?? DBNull.Value },
                    new NpgsqlParameter("key", conflict.Key));
            }
            await MigrateAsync(connectionString, TargetMigration);
            Assert.Equal("false", (await ReadSettingAsync(connectionString)).Value);
        });
    }

    private sealed record CaptureSetting(string Value, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt, string? CreatedBy, string? UpdatedBy);

    private static Task AddMatchingSubscriptionAsync(string connectionString) => ExecuteAsync(connectionString, """
        INSERT INTO flowbit.webhook_subscriptions
            ("Id", "Name", "State", "MaxConcurrency", "Revision", "CreatedAt", "CreatedBy", "UpdatedAt", "UpdatedBy")
        VALUES ('00000000-0000-0000-0000-000000000001', 'capture-setting-test', 'active', 1, 1, now(), 'test', now(), 'test');
        WITH revision AS (
            INSERT INTO flowbit.webhook_subscription_revisions
                ("SubscriptionId", "ConfigurationRevision", "EndpointUrl", "EventTypes", "CreatedAt", "CreatedBy")
            VALUES ('00000000-0000-0000-0000-000000000001', 1, 'https://receiver.example.com/flowbit', ARRAY['instance.started'], now(), 'test')
            RETURNING "Id")
        UPDATE flowbit.webhook_subscriptions SET "CurrentRevisionId" = (SELECT "Id" FROM revision)
        WHERE "Id" = '00000000-0000-0000-0000-000000000001';
        INSERT INTO flowbit.webhook_subscription_dispatch_state ("SubscriptionId", "ConsecutiveFailures", "BackoffSeconds", "UpdatedAt")
        VALUES ('00000000-0000-0000-0000-000000000001', 0, 0, now());
        """);

    private static async Task<CaptureSetting> ReadSettingAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            SELECT "Value", "CreatedAt", "UpdatedAt", "CreatedBy", "UpdatedBy"
            FROM flowbit.engine_settings WHERE "Namespace" = 'Webhooks' AND "Key" = 'CaptureEnabled'
            """, connection);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        var result = new CaptureSetting(reader.GetString(0), reader.GetFieldValue<DateTimeOffset>(1),
            reader.GetFieldValue<DateTimeOffset>(2), reader.IsDBNull(3) ? null : reader.GetString(3), reader.IsDBNull(4) ? null : reader.GetString(4));
        Assert.False(await reader.ReadAsync());
        return result;
    }

    private static async Task ExecuteAsync(string connectionString, string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<T> ScalarAsync<T>(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        return (T)(await command.ExecuteScalarAsync())!;
    }

    private async Task WithIsolatedDatabaseAsync(Func<string, Task> test)
    {
        var databaseName = "capture_settings_" + Guid.NewGuid().ToString("N");
        var adminBuilder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "postgres" };
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName };
            await test(builder.ConnectionString);
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

    private static async Task MigrateAsync(string connectionString, string targetMigration)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connectionString, FlowbitDatabase.ConfigureProvider).Options;
        await using var context = new AppDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(targetMigration);
    }
}
