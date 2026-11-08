using Flowbit.Infrastructure.Data;
using Flowbit.Shared.Dtos;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class DatabaseSchemaTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task AllFlowbitTablesAndMigrationHistoryUseFlowbitSchema()
    {
        await using var db = fixture.CreateDbContext();
        var mappedTables = db.Model.GetEntityTypes()
            .Select(entity => new
            {
                Name = entity.GetTableName(),
                Schema = entity.GetSchema()
            })
            .Where(table => table.Name is not null)
            .Distinct()
            .ToArray();

        Assert.Equal(67, mappedTables.Length);
        Assert.DoesNotContain(mappedTables, table => table.Name == "lifecycle_event_capture_state");
        Assert.Contains(mappedTables, table => table.Name == "call_activity_executions");
        Assert.Contains(mappedTables, table => table.Name == "call_activity_results");
        Assert.Contains(mappedTables, table => table.Name == "workflow_definition_call_targets");
        Assert.Contains(mappedTables, table => table.Name == "business_calendars");
        Assert.Contains(mappedTables, table => table.Name == "workflow_definition_business_calendars");
        Assert.Contains(mappedTables, table => table.Name == "lifecycle_events");
        Assert.Contains(mappedTables, table => table.Name == "webhook_deliveries");
        Assert.Contains(mappedTables, table => table.Name == "webhook_data_protection_keys");
        Assert.Contains(mappedTables, table => table.Name == "local_users");
        Assert.Contains(mappedTables, table => table.Name == "user_attribute_definitions");
        Assert.Contains(mappedTables, table => table.Name == "local_refresh_sessions");
        Assert.All(mappedTables, table => Assert.Equal(FlowbitDatabase.Schema, table.Schema));
        Assert.Contains(mappedTables, table => table.Name == "instance_variable_current_values");
        Assert.Contains(mappedTables, table => table.Name == "gateway_executions");
        Assert.Contains(mappedTables, table => table.Name == "gateway_branches");
        Assert.Contains(mappedTables, table => table.Name == "complex_gateway_states");
        Assert.DoesNotContain(mappedTables, table => table.Name == "parallel_gateway_executions");
        Assert.DoesNotContain(mappedTables, table => table.Name == "parallel_gateway_branches");
        Assert.Contains(mappedTables, table => table.Name == "node_executions");
        Assert.Contains(mappedTables, table => table.Name == "user_task_role_policies");
        Assert.Contains(mappedTables, table => table.Name == "user_delegations");
        Assert.Contains(mappedTables, table => table.Name == "workflow_delegation_policies");
        Assert.Contains(mappedTables, table => table.Name == "workflow_jobs");
        Assert.Contains(mappedTables, table => table.Name == "workflow_job_attempts");
        Assert.Contains(mappedTables, table => table.Name == "workflow_job_snapshots");
        Assert.Contains(mappedTables, table => table.Name == "workflow_incidents");
        Assert.Contains(mappedTables, table => table.Name == "retention_policies");
        Assert.Contains(mappedTables, table => table.Name == "retention_coordinator");
        Assert.Contains(mappedTables, table => table.Name == "timer_subscriptions");
        Assert.Contains(mappedTables, table => table.Name == "conditional_boundary_subscriptions");
        Assert.Contains(mappedTables, table => table.Name == "workflow_instance_version_changes");
        Assert.Contains(mappedTables, table => table.Name == "workflow_instance_version_change_batches");
        Assert.Contains(mappedTables, table => table.Name == "workflow_instance_version_change_batch_items");
        Assert.Contains(mappedTables, table => table.Name == "administrative_action_batches");
        Assert.Contains(mappedTables, table => table.Name == "administrative_action_batch_items");
        Assert.Contains(mappedTables, table => table.Name == "instance_variable_updates");
        Assert.Contains(mappedTables, table => table.Name == "instance_variable_update_batches");
        Assert.Contains(mappedTables, table => table.Name == "instance_variable_update_batch_items");
        Assert.Contains(mappedTables, table => table.Name == "instance_variable_update_batch_jobs");
        Assert.Contains(mappedTables, table => table.Name == "workflow_definition_user_task_conditions");
        Assert.Contains(mappedTables, table => table.Name == "shared_variables");
        Assert.Contains(mappedTables, table => table.Name == "shared_variable_current_values");
        Assert.Contains(mappedTables, table => table.Name == "shared_variable_revisions");
        Assert.Contains(mappedTables, table => table.Name == "shared_variable_revision_state");
        Assert.Contains(mappedTables, table => table.Name == "shared_variable_requests");
        Assert.Contains(mappedTables, table => table.Name == "shared_variable_clients");
        Assert.Contains(mappedTables, table => table.Name == "shared_variable_client_secrets");
        Assert.Contains(mappedTables, table => table.Name == "workflow_definition_shared_variable_bindings");
        Assert.DoesNotContain(mappedTables, table => table.Name == "workflow_definition_shared_variable_dependencies");
        Assert.DoesNotContain(mappedTables, table => table.Name == "shared_variable_wakes");
        Assert.DoesNotContain(mappedTables, table => table.Name == "shared_variable_wake_deliveries");
        Assert.DoesNotContain(mappedTables, table => table.Name == "shared_variable_wake_incidents");

        var expectedNames = mappedTables
            .Select(table => table.Name!)
            .Append(FlowbitDatabase.MigrationsHistoryTable)
            .Order(StringComparer.Ordinal)
            .ToArray();

        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand("""
            SELECT schemaname, tablename
            FROM pg_catalog.pg_tables
            WHERE tablename = ANY (@tableNames)
            ORDER BY schemaname, tablename
            """, connection);
        command.Parameters.AddWithValue("tableNames", expectedNames);

        var actual = new List<(string Schema, string Table)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            actual.Add((reader.GetString(0), reader.GetString(1)));
        }

        Assert.Equal(expectedNames.Length, actual.Count);
        Assert.All(actual, table => Assert.Equal(FlowbitDatabase.Schema, table.Schema));
        Assert.Equal(expectedNames, actual.Select(table => table.Table).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task WorkflowJobOperationsIndexesAreApplied()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT indexname
            FROM pg_catalog.pg_indexes
            WHERE schemaname = 'flowbit'
              AND tablename = 'workflow_jobs'
              AND indexname = ANY (@indexNames)
            ORDER BY indexname
            """,
            connection);
        var expected = new[]
        {
            "IX_workflow_jobs_status_updated_id",
            "IX_workflow_jobs_updated_id"
        };
        command.Parameters.AddWithValue("indexNames", expected);

        var actual = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            actual.Add(reader.GetString(0));
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public async Task UserTaskDueIndexAndThresholdSettingAreApplied()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using (var index = new NpgsqlCommand(
                         """
                         SELECT indexdef
                         FROM pg_catalog.pg_indexes
                         WHERE schemaname = 'flowbit'
                           AND tablename = 'user_tasks'
                           AND indexname = 'IX_user_tasks_Status_DueAt_Id'
                         """,
                         connection))
        {
            var definition = (string?)await index.ExecuteScalarAsync();
            Assert.NotNull(definition);
            Assert.Contains("(\"Status\", \"DueAt\", \"Id\")", definition, StringComparison.Ordinal);
        }

        await using var setting = new NpgsqlCommand(
            """
            SELECT "Value"
            FROM flowbit.engine_settings
            WHERE "Namespace" = 'UserTasks' AND "Key" = 'DueSoonThreshold'
            """,
            connection);
        Assert.Equal("P1D", (string?)await setting.ExecuteScalarAsync());
    }

    [Fact]
    public async Task BusinessCalendarTablesAndExampleCalendarAreApplied()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using (var index = new NpgsqlCommand(
                         """
                         SELECT indexdef
                         FROM pg_catalog.pg_indexes
                         WHERE schemaname = 'flowbit'
                           AND tablename = 'business_calendars'
                           AND indexname = 'IX_business_calendars_Name'
                         """,
                         connection))
        {
            var definition = (string?)await index.ExecuteScalarAsync();
            Assert.NotNull(definition);
            Assert.StartsWith("CREATE UNIQUE INDEX", definition, StringComparison.Ordinal);
        }

        await using (var foreignKeys = new NpgsqlCommand(
                         """
                         SELECT confdeltype::text
                         FROM pg_catalog.pg_constraint
                         WHERE conrelid = 'flowbit.workflow_definition_business_calendars'::regclass
                           AND contype = 'f'
                         """,
                         connection))
        {
            var deleteRules = new List<string>();
            await using var reader = await foreignKeys.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                deleteRules.Add(reader.GetString(0));
            }

            Assert.Equal(["c", "c"], deleteRules);
        }

        await using (var column = new NpgsqlCommand(
                         """
                         SELECT is_nullable, character_maximum_length
                         FROM information_schema.columns
                         WHERE table_schema = 'flowbit'
                           AND table_name = 'timer_subscriptions'
                           AND column_name = 'ScheduleCalendar'
                         """,
                         connection))
        {
            await using var reader = await column.ExecuteReaderAsync();
            Assert.True(await reader.ReadAsync());
            Assert.Equal("YES", reader.GetString(0));
            Assert.Equal(100, reader.GetInt32(1));
        }

        // The name is citext, so the seeded example calendar is found ignoring case.
        await using var example = new NpgsqlCommand(
            """
            SELECT "TimeZone", "WeeklyHours" -> 'monday' -> 0 ->> 'start', jsonb_array_length("Holidays")
            FROM flowbit.business_calendars
            WHERE "Name" = 'EXAMPLES.WEEKDAYS'
            """,
            connection);
        await using var row = await example.ExecuteReaderAsync();
        Assert.True(await row.ReadAsync());
        Assert.Equal("Etc/UTC", row.GetString(0));
        Assert.Equal("09:00", row.GetString(1));
        Assert.Equal(0, row.GetInt32(2));
    }

    [Fact]
    public async Task InstanceVersionChangeBatchClassificationCountsAreApplied()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT pg_get_constraintdef(constraint_row.oid)
            FROM pg_catalog.pg_constraint AS constraint_row
            WHERE constraint_row.connamespace = 'flowbit'::regnamespace
              AND constraint_row.conrelid =
                  'flowbit.workflow_instance_version_change_batches'::regclass
              AND constraint_row.conname =
                  'CK_workflow_instance_version_change_batches_counts'
              AND EXISTS (
                  SELECT 1
                  FROM information_schema.columns AS column_row
                  WHERE column_row.table_schema = 'flowbit'
                    AND column_row.table_name =
                        'workflow_instance_version_change_batches'
                    AND column_row.column_name = 'BlockedItemCount')
            """,
            connection);
        var definition = Assert.IsType<string>(await command.ExecuteScalarAsync());
        Assert.Contains(
            "\"BlockedItemCount\" <= \"IneligibleItemCount\"",
            definition,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"StaleItemCount\" <= (\"IneligibleItemCount\" + \"SkippedItemCount\")",
            definition,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LifecycleWebhookPartialIndexesAreApplied()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT index_class.relname,
                   index_row.indisunique,
                   (SELECT string_agg(attribute.attname::text, ',' ORDER BY key_column.ordinality)
                      FROM unnest(index_row.indkey) WITH ORDINALITY AS key_column(attnum, ordinality)
                      JOIN pg_catalog.pg_attribute AS attribute
                        ON attribute.attrelid = index_row.indrelid AND attribute.attnum = key_column.attnum),
                   pg_get_expr(index_row.indpred, index_row.indrelid)
            FROM pg_catalog.pg_index AS index_row
            JOIN pg_catalog.pg_class AS index_class ON index_class.oid = index_row.indexrelid
            WHERE index_class.relnamespace = 'flowbit'::regnamespace
              AND index_class.relname = ANY (@indexNames)
            ORDER BY index_class.relname
            """,
            connection);
        // Name, unique, columns, and fragments of the partial-index predicate.
        var expected = new (string Name, bool Unique, string Columns, string[] Predicate)[]
        {
            ("IX_lifecycle_events_pending_fanout", false, "RecordedAt,Id", ["\"FanoutCompletedAt\" IS NULL"]),
            ("IX_webhook_deliveries_due", false, "SubscriptionId,DueAt", ["'pending'", "'retryScheduled'"]),
            ("IX_webhook_deliveries_leases", false, "SubscriptionId,LeaseExpiresAt", ["'delivering'"]),
            ("IX_webhook_deliveries_outstanding_test", false, "SubscriptionId",
                ["\"IsTest\"", "'pending'", "'retryScheduled'", "'delivering'"]),
            ("IX_webhook_deliveries_settled", false, "SettledAt,Id", ["'succeeded'", "'discarded'"]),
            ("IX_webhook_signing_keys_current", true, "SubscriptionId", ["= 'current'"]),
            ("IX_webhook_signing_keys_pending_removal", false, "RemoveSecretNotBefore",
                ["\"ProtectedSecret\" IS NOT NULL", "'current'", "'previous'"]),
            ("IX_webhook_signing_keys_previous", true, "SubscriptionId", ["= 'previous'"]),
            ("IX_webhook_subscriptions_active_revision", true, "CurrentRevisionId", ["= 'active'"])
        };
        command.Parameters.AddWithValue("indexNames", expected.Select(index => index.Name).ToArray());

        var actual = new List<(string Name, bool Unique, string Columns, string Predicate)>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                actual.Add((reader.GetString(0), reader.GetBoolean(1), reader.GetString(2), reader.GetString(3)));
            }
        }

        Assert.Equal(
            expected.Select(index => index.Name).Order(StringComparer.Ordinal),
            actual.Select(index => index.Name));
        foreach (var index in expected)
        {
            var applied = actual.Single(item => item.Name == index.Name);
            Assert.True(index.Unique == applied.Unique, $"{index.Name} unique: {applied.Unique}");
            Assert.Equal(index.Columns, applied.Columns);
            Assert.All(index.Predicate, fragment => Assert.Contains(fragment, applied.Predicate, StringComparison.Ordinal));
        }
    }

    [Fact]
    public async Task SigningKeysAllowOneCurrentAndOnePreviousKeyPerSubscription()
    {
        var (subscriptionId, _) = await WebhookTestDatabase.CreateSubscriptionAsync(
            fixture.DataSource,
            [LifecycleEventTypes.InstanceStarted],
            $"schema-signing-keys-{Guid.NewGuid():N}",
            state: "paused");
        try
        {
            await InsertSigningKeyAsync(subscriptionId, "current");
            await InsertSigningKeyAsync(subscriptionId, "previous");
            // Retired material is not limited.
            await InsertSigningKeyAsync(subscriptionId, "revoked");
            await InsertSigningKeyAsync(subscriptionId, "revoked");

            var current = await Assert.ThrowsAsync<PostgresException>(() => InsertSigningKeyAsync(subscriptionId, "current"));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, current.SqlState);
            Assert.Equal("IX_webhook_signing_keys_current", current.ConstraintName);
            var previous = await Assert.ThrowsAsync<PostgresException>(() => InsertSigningKeyAsync(subscriptionId, "previous"));
            Assert.Equal(PostgresErrorCodes.UniqueViolation, previous.SqlState);
            Assert.Equal("IX_webhook_signing_keys_previous", previous.ConstraintName);

            // A current or previous key must hold its secret, and a previous key needs its overlap expiry.
            var missingSecret = await Assert.ThrowsAsync<PostgresException>(() =>
                InsertSigningKeyAsync(subscriptionId, "expired", withSecret: false, secretRemoved: false));
            Assert.Equal("CK_webhook_signing_keys_secret", missingSecret.ConstraintName);
            await InsertSigningKeyAsync(subscriptionId, "expired", withSecret: false, secretRemoved: true);
        }
        finally
        {
            await WebhookTestDatabase.SetSubscriptionStateAsync(fixture.DataSource, subscriptionId, "archived");
        }
    }

    [Fact]
    public async Task LifecycleWebhookCheckConstraintsAreApplied()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT table_class.relname, constraint_row.conname, pg_get_constraintdef(constraint_row.oid)
            FROM pg_catalog.pg_constraint AS constraint_row
            JOIN pg_catalog.pg_class AS table_class ON table_class.oid = constraint_row.conrelid
            WHERE constraint_row.connamespace = 'flowbit'::regnamespace
              AND constraint_row.contype = 'c'
              AND (table_class.relname LIKE 'webhook\_%' OR table_class.relname LIKE 'lifecycle\_event%')
            ORDER BY table_class.relname, constraint_row.conname
            """,
            connection);
        var actual = new List<(string Table, string Name, string Definition)>();
        await using (var reader = await command.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                actual.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            }
        }

        Assert.Equal(
            [
                ("lifecycle_events", "CK_lifecycle_events_body"),
                ("lifecycle_events", "CK_lifecycle_events_schema_version"),
                ("webhook_deliveries", "CK_webhook_deliveries_counters"),
                ("webhook_deliveries", "CK_webhook_deliveries_failure"),
                ("webhook_deliveries", "CK_webhook_deliveries_generation"),
                ("webhook_deliveries", "CK_webhook_deliveries_lease"),
                ("webhook_deliveries", "CK_webhook_deliveries_settlement"),
                ("webhook_deliveries", "CK_webhook_deliveries_state"),
                ("webhook_delivery_attempts", "CK_webhook_delivery_attempts_completion"),
                ("webhook_delivery_attempts", "CK_webhook_delivery_attempts_numbers"),
                ("webhook_signing_keys", "CK_webhook_signing_keys_overlap"),
                ("webhook_signing_keys", "CK_webhook_signing_keys_secret"),
                ("webhook_signing_keys", "CK_webhook_signing_keys_status"),
                ("webhook_subscription_dispatch_state", "CK_webhook_subscription_dispatch_state_backoff"),
                ("webhook_subscription_dispatch_state", "CK_webhook_subscription_dispatch_state_counters"),
                ("webhook_subscription_revisions", "CK_webhook_subscription_revisions_configuration_revision"),
                ("webhook_subscription_revisions", "CK_webhook_subscription_revisions_event_types"),
                ("webhook_subscriptions", "CK_webhook_subscriptions_archive"),
                ("webhook_subscriptions", "CK_webhook_subscriptions_max_concurrency"),
                ("webhook_subscriptions", "CK_webhook_subscriptions_pause_reason"),
                ("webhook_subscriptions", "CK_webhook_subscriptions_revision"),
                ("webhook_subscriptions", "CK_webhook_subscriptions_state")
            ],
            actual.Select(item => (item.Table, item.Name)));
        var definitions = actual.ToDictionary(item => item.Name, item => item.Definition, StringComparer.Ordinal);
        Assert.Contains("<= 16384", definitions["CK_lifecycle_events_body"], StringComparison.Ordinal);
        Assert.All(
            ["'current'", "'previous'", "'revoked'", "'expired'", "'retired'"],
            status => Assert.Contains(status, definitions["CK_webhook_signing_keys_status"], StringComparison.Ordinal));
        Assert.Contains("\"OverlapExpiresAt\" IS NOT NULL", definitions["CK_webhook_signing_keys_overlap"], StringComparison.Ordinal);
        Assert.Contains("\"SecretRemovedAt\" IS NULL", definitions["CK_webhook_signing_keys_secret"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task LifecycleCaptureTriggersAreDeferredConstraintTriggersOnInvokerFunctions()
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using (var triggers = new NpgsqlCommand(
                         """
                         SELECT trigger_row.tgname,
                                table_class.relname,
                                function_row.proname,
                                trigger_row.tgconstraint <> 0,
                                trigger_row.tgdeferrable,
                                trigger_row.tginitdeferred,
                                (trigger_row.tgtype & 1) = 1,
                                (trigger_row.tgtype & 2) = 0
                         FROM pg_catalog.pg_trigger AS trigger_row
                         JOIN pg_catalog.pg_class AS table_class ON table_class.oid = trigger_row.tgrelid
                         JOIN pg_catalog.pg_proc AS function_row ON function_row.oid = trigger_row.tgfoid
                         WHERE table_class.relnamespace = 'flowbit'::regnamespace
                           AND NOT trigger_row.tgisinternal
                           AND function_row.proname LIKE 'capture\_%\_lifecycle'
                         ORDER BY trigger_row.tgname
                         """,
                         connection))
        {
            var actual = new List<(string Trigger, string Table, string Function)>();
            await using var reader = await triggers.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                actual.Add((reader.GetString(0), reader.GetString(1), reader.GetString(2)));
                // A constraint trigger, DEFERRABLE INITIALLY DEFERRED, row-level, and AFTER the change.
                Assert.True(reader.GetBoolean(3), reader.GetString(0));
                Assert.True(reader.GetBoolean(4), reader.GetString(0));
                Assert.True(reader.GetBoolean(5), reader.GetString(0));
                Assert.True(reader.GetBoolean(6), reader.GetString(0));
                Assert.True(reader.GetBoolean(7), reader.GetString(0));
            }

            Assert.Equal(
                [
                    ("TR_user_delegations_lifecycle_insert", "user_delegations", "capture_user_delegation_lifecycle"),
                    ("TR_user_tasks_lifecycle_insert", "user_tasks", "capture_user_task_lifecycle"),
                    ("TR_user_tasks_lifecycle_update", "user_tasks", "capture_user_task_lifecycle"),
                    ("TR_workflow_incidents_lifecycle_insert", "workflow_incidents", "capture_workflow_incident_lifecycle"),
                    ("TR_workflow_instances_lifecycle_insert", "workflow_instances", "capture_workflow_instance_lifecycle"),
                    ("TR_workflow_instances_lifecycle_status", "workflow_instances", "capture_workflow_instance_lifecycle")
                ],
                actual);
        }

        await using var functions = new NpgsqlCommand(
            """
            SELECT function_row.proname, function_row.prosecdef, function_row.proconfig
            FROM pg_catalog.pg_proc AS function_row
            WHERE function_row.pronamespace = 'flowbit'::regnamespace
              AND function_row.proname = ANY (@names)
            ORDER BY function_row.proname
            """,
            connection);
        string[] names =
        [
            "audit_lifecycle_capture_setting",
            "capture_user_delegation_lifecycle",
            "capture_user_task_lifecycle",
            "capture_workflow_incident_lifecycle",
            "capture_workflow_instance_lifecycle",
            "guard_lifecycle_capture_setting",
            "lifecycle_capture_wanted",
            "lifecycle_event_targets",
            "lifecycle_event_timestamp",
            "lifecycle_json_text",
            "record_lifecycle_event",
            "record_webhook_test_event",
            "set_lifecycle_event_capture",
            "uuid_v7"
        ];
        functions.Parameters.AddWithValue("names", names);
        var found = new List<string>();
        await using (var reader = await functions.ExecuteReaderAsync())
        {
            while (await reader.ReadAsync())
            {
                var name = reader.GetString(0);
                found.Add(name);
                Assert.False(reader.GetBoolean(1), $"{name} must be SECURITY INVOKER.");
                Assert.Equal(["search_path=pg_catalog, pg_temp"], reader.GetFieldValue<string[]>(2));
            }
        }
        Assert.Equal(names, found);
    }

    [Fact]
    public async Task LifecycleEventBodyStringsAreBoundedByTheirSourceColumns()
    {
        // The capture functions copy these strings into event bodies. Their 300-character limits keep every
        // body under 5 KiB; widening one means rechecking the bound against CK_lifecycle_events_body.
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT table_name || '.' || column_name, character_maximum_length
            FROM information_schema.columns
            WHERE table_schema = 'flowbit'
              AND (table_name, column_name) IN (
                  ('workflow_instances', 'WorkflowKey'),
                  ('workflow_incidents', 'WorkflowKey'),
                  ('user_delegations', 'WorkflowKey'),
                  ('user_tasks', 'NodeExternalId'),
                  ('lifecycle_events', 'WorkflowKey'))
            """,
            connection);
        var limits = new List<(string Column, int Length)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            limits.Add((reader.GetString(0), reader.GetInt32(1)));
        }

        Assert.Equal(
            [
                ("lifecycle_events.WorkflowKey", 300),
                ("user_delegations.WorkflowKey", 300),
                ("user_tasks.NodeExternalId", 300),
                ("workflow_incidents.WorkflowKey", 300),
                ("workflow_instances.WorkflowKey", 300)
            ],
            limits.OrderBy(item => item.Column, StringComparer.Ordinal));
    }

    private async Task InsertSigningKeyAsync(
        Guid subscriptionId,
        string status,
        bool withSecret = true,
        bool secretRemoved = false)
    {
        await using var connection = await fixture.DataSource.OpenConnectionAsync();
        await using var command = new NpgsqlCommand(
            """
            INSERT INTO flowbit.webhook_signing_keys
                ("Id", "SubscriptionId", "ProtectedSecret", "Status", "CreatedAt", "CreatedBy", "OverlapExpiresAt",
                 "SecretRemovedAt")
            VALUES (gen_random_uuid(), @subscription, CASE WHEN @withSecret THEN '\x01'::bytea END, @status, now(),
                    'schema-tests', CASE WHEN @status = 'previous' THEN now() + interval '1 hour' END,
                    CASE WHEN @secretRemoved THEN now() END)
            """,
            connection);
        command.Parameters.AddWithValue("subscription", subscriptionId);
        command.Parameters.AddWithValue("status", status);
        command.Parameters.AddWithValue("withSecret", withSecret);
        command.Parameters.AddWithValue("secretRemoved", secretRemoved);
        await command.ExecuteNonQueryAsync();
    }
}
