using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Service.Abstractions;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// EVENT-04C start deliveries beyond the main path: exactly once, cancellation, business-key
/// uniqueness under concurrency, retry against a new default, a broadcast without a payload, a
/// wait the new instance reaches, version changes, a managed client's view, and the schema.
/// </summary>
public sealed partial class SignalStartRuntimeTests
{
    [Fact]
    public async Task ExactlyOnce_ARepeatedRunOfAFinishedStartAndACancelledStartCreateNothing()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal);
        await CreateAsync(model);
        var broadcast = await ThrowApiAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: Payload(new { recallNumber = "R-1", product = new { name = "Kettle" } })));
        await ProcessDeliveryJobsAsync(broadcast.Id);
        var delivery = Assert.Single(await DeliveriesAsync(broadcast.Id));
        Assert.Equal(SignalDeliveryResultCodes.Started, delivery.ResultCode);

        // A second lease of the job runs the delivery again: it finds the delivery finished.
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var outcome = await scope.ServiceProvider.GetRequiredService<ISignalDeliveryExecutor>()
                .DeliverSignalAsync(delivery.Id, CancellationToken.None);
            Assert.Equal((SignalDeliveryStatuses.Delivered, SignalDeliveryResultCodes.Started), (outcome.Status, outcome.ResultCode));
        }
        Assert.Equal(1, await FamilyInstanceCountAsync(model.Id));
        Assert.Equal(delivery.InstanceId, Assert.Single(await DeliveriesAsync(broadcast.Id)).InstanceId);

        // A broadcast cancelled before its delivery starts nothing.
        var cancelled = await ThrowApiAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: Payload(new { recallNumber = "R-2", product = new { name = "Toaster" } })));
        using (var cancel = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{cancelled.Id}/cancel",
                   new CancelSignalBroadcastRequest("Not needed")))
        {
            Assert.True(cancel.IsSuccessStatusCode, await cancel.Content.ReadAsStringAsync());
        }
        await ProcessDeliveryJobsAsync(cancelled.Id);
        var cancelledDelivery = Assert.Single(await DeliveriesAsync(cancelled.Id));
        Assert.Equal((SignalDeliveryStatuses.Cancelled, (long?)null), (cancelledDelivery.Status, cancelledDelivery.InstanceId));
        Assert.Equal(1, await FamilyInstanceCountAsync(model.Id));
    }

    [Fact]
    public async Task BusinessKey_TwoStartsAtOnceStartOneAndActiveUniquenessFreesTheKeyWhenTheOwnerEnds()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal, businessKey: true);
        model.FlowNodes[0].BusinessKey!.Uniqueness = BusinessKeyUniqueness.Active;
        await CreateAsync(model);
        var payload = Payload(new { recallNumber = "R-KEY-7", product = new { name = "Kettle" } });
        var first = await ThrowApiAsync(new ThrowSignalRequest(signal, model.Id, Payload: payload));
        var second = await ThrowApiAsync(new ThrowSignalRequest(signal, model.Id, Payload: payload));

        // Both delivery jobs run at once.
        await PrioritizeDeliveryJobsAsync(first.Id);
        await PrioritizeDeliveryJobsAsync(second.Id);
        await Task.WhenAll(LeaseAndProcessOneAsync(), LeaseAndProcessOneAsync());
        await ProcessDeliveryJobsAsync(first.Id);
        await ProcessDeliveryJobsAsync(second.Id);

        var outcomes = (await DeliveriesAsync(first.Id)).Concat(await DeliveriesAsync(second.Id)).ToList();
        var started = Assert.Single(outcomes, item => item.ResultCode == SignalDeliveryResultCodes.Started);
        var skipped = Assert.Single(outcomes, item => item.ResultCode == SignalDeliveryResultCodes.BusinessKeyConflict);
        Assert.Equal((SignalDeliveryStatuses.Skipped, (long?)null), (skipped.Status, skipped.InstanceId));
        Assert.Equal($"Instance #{started.InstanceId} already has this start's business key.", skipped.ResultDescription);
        Assert.DoesNotContain("R-KEY-7", skipped.ResultDescription!, StringComparison.Ordinal);
        Assert.Equal(1, await FamilyInstanceCountAsync(model.Id));

        // Once the owner completes, the active key is free again.
        await TakeAsync(started.InstanceId!.Value, 2, 201);
        var third = await ThrowApiAsync(new ThrowSignalRequest(signal, model.Id, Payload: payload));
        await ProcessDeliveryJobsAsync(third.Id);
        Assert.Equal(SignalDeliveryResultCodes.Started, Assert.Single(await DeliveriesAsync(third.Id)).ResultCode);
        Assert.Equal(2, await FamilyInstanceCountAsync(model.Id));
    }

    [Fact]
    public async Task Retry_AFailedStartRunsTheFamilysNewDefault()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal);
        var v1 = await CreateAsync(model);
        var broadcast = await ThrowApiAsync(new ThrowSignalRequest(signal, model.Id, Payload: Payload(new { recallNumber = "R-9" })));
        await ProcessDeliveryJobsAsync(broadcast.Id);
        var failed = await BroadcastAsync(broadcast.Id);
        Assert.Equal((SignalBroadcastStatuses.CompletedWithIssues, 1), (failed.Status, failed.FailedCount));
        Assert.Equal(SignalDeliveryResultCodes.PayloadInvalid, Assert.Single(await DeliveriesAsync(broadcast.Id)).ResultCode);

        // Version 2 makes the product optional and becomes the default.
        var relaxed = RecallModel(signal, model.Id);
        relaxed.FlowNodes[0].Signal!.OutputMappings[1] = Mapping("product", "product.name", required: false, defaultValue: "Unknown");
        var v2 = await NewVersionAsync(v1, relaxed);
        await SetDefaultAsync(v2);
        using (var retry = await SendAsync(HttpMethod.Post, $"/api/signal-broadcasts/{broadcast.Id}/retry-failed",
                   new RetrySignalDeliveriesRequest(failed.UpdatedAt)))
        {
            Assert.True(retry.IsSuccessStatusCode, await retry.Content.ReadAsStringAsync());
            var retried = (await retry.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
            Assert.Equal(SignalBroadcastStatuses.Delivering, retried.Status);
        }
        var pending = Assert.Single(await DeliveriesAsync(broadcast.Id));
        Assert.Equal((SignalDeliveryStatuses.Pending, v2), (pending.Status, pending.WorkflowDefinitionId));

        await ProcessDeliveryJobsAsync(broadcast.Id);
        var delivered = Assert.Single(await DeliveriesAsync(broadcast.Id));
        Assert.Equal((SignalDeliveryStatuses.Delivered, SignalDeliveryResultCodes.Started, v2),
            (delivered.Status, delivered.ResultCode, delivered.WorkflowDefinitionId));
        await using var db = fixture.CreateDbContext();
        var instance = await db.WorkflowInstances.AsNoTracking().SingleAsync(item => item.Id == delivered.InstanceId);
        Assert.Equal(v2, instance.WorkflowDefinitionId);
        Assert.Equal("Unknown", await db.InstanceVariableCurrentValues.AsNoTracking()
            .Where(item => item.InstanceId == instance.Id && item.VariableName == "product")
            .Select(item => item.ValueJson.RootElement.GetString()).SingleAsync());
        Assert.Equal(SignalBroadcastStatuses.Completed, (await BroadcastAsync(broadcast.Id)).Status);
    }

    [Fact]
    public async Task NoPayload_StartsAWorkflowWhoseMappingsAllHaveDefaultsAndFailsARequiredMapping()
    {
        var signal = UniqueSignal();
        var defaults = RecallModel(signal);
        defaults.FlowNodes[0].Signal!.OutputMappings = [Mapping("reason", "reason", required: false, defaultValue: "Not stated")];
        await CreateAsync(defaults);
        var required = RecallModel(signal);
        await CreateAsync(required);

        var broadcast = await ThrowApiAsync(new ThrowSignalRequest(signal, AllWorkflows: true));
        Assert.Equal((2, 2), (broadcast.TotalDeliveryCount, broadcast.StartCount));
        await ProcessDeliveryJobsAsync(broadcast.Id);

        var deliveries = await DeliveriesAsync(broadcast.Id);
        Assert.Equal(SignalDeliveryResultCodes.Started, deliveries.Single(item => item.WorkflowKey == defaults.Id).ResultCode);
        var failed = deliveries.Single(item => item.WorkflowKey == required.Id);
        Assert.Equal((SignalDeliveryStatuses.Failed, SignalDeliveryResultCodes.PayloadInvalid, (long?)null),
            (failed.Status, failed.ResultCode, failed.InstanceId));
        Assert.Equal(0, await FamilyInstanceCountAsync(required.Id));
    }

    [Fact]
    public async Task AWaitTheNewInstanceReaches_IsNotARecipientOfTheBroadcastThatStartedIt()
    {
        var signal = UniqueSignal();
        var model = WaiterModel(signal);
        model.InitialEventId = null;
        model.FlowNodes[0] = new FlowNodeModel
        {
            Id = 1, Name = "Started", Type = BpmnFlowNodeTypes.SignalStartEvent, Signal = new SignalEventModel { Name = signal }
        };
        await CreateAsync(model);

        var broadcast = await ThrowApiAsync(new ThrowSignalRequest(signal, model.Id));
        Assert.Equal((1, 1), (broadcast.TotalDeliveryCount, broadcast.StartCount));
        await ProcessDeliveryJobsAsync(broadcast.Id);

        var delivery = Assert.Single(await DeliveriesAsync(broadcast.Id));
        Assert.Equal(SignalDeliveryResultCodes.Started, delivery.ResultCode);
        var finished = await BroadcastAsync(broadcast.Id);
        Assert.Equal((SignalBroadcastStatuses.Completed, 1, 1), (finished.Status, finished.TotalDeliveryCount, finished.DeliveredCount));
        await using var db = fixture.CreateDbContext();
        var subscription = await db.SignalSubscriptions.AsNoTracking()
            .SingleAsync(item => item.InstanceId == delivery.InstanceId);
        Assert.Equal((signal, 2, "active"), (subscription.SignalName, subscription.SignalNodeId, subscription.Status));

        // The next throw to the family freezes both: the started instance's wait and another start.
        var next = await ThrowApiAsync(new ThrowSignalRequest(signal, model.Id));
        Assert.Equal((2, 1), (next.TotalDeliveryCount, next.StartCount));
        var family = Assert.Single(next.RecipientsByWorkflowKey);
        Assert.Equal((model.Id, 2, 1), (family.WorkflowKey, family.Count, family.StartCount));
        await ProcessDeliveryJobsAsync(next.Id);
        var outcomes = await DeliveriesAsync(next.Id);
        Assert.Equal(
            [(SignalDeliveryKinds.Start, SignalDeliveryResultCodes.Started), (SignalDeliveryKinds.Wait, SignalDeliveryResultCodes.Caught)],
            outcomes.Select(item => (item.Kind, item.ResultCode!)).OrderBy(item => item.Kind, StringComparer.Ordinal));
    }

    [Fact]
    public async Task VersionChange_BetweenVersionsWithDifferentSignalStartsHasNoBlockerAndMovesNoSubscription()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal);
        var v1 = await CreateAsync(model);
        var broadcast = await ThrowApiAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: Payload(new { recallNumber = "R-3", product = new { name = "Kettle" } })));
        await ProcessDeliveryJobsAsync(broadcast.Id);
        var instanceId = Assert.Single(await DeliveriesAsync(broadcast.Id)).InstanceId!.Value;

        var renamed = RecallModel(UniqueSignal(), model.Id);
        renamed.FlowNodes[0].Signal!.OutputMappings.RemoveAt(2);
        var v2 = await NewVersionAsync(v1, renamed);
        using (var preview = await SendAsync(HttpMethod.Post, $"/api/instances/{instanceId}/version-change/preview",
                   new PreviewInstanceVersionChangeRequest(v2)))
        {
            Assert.True(preview.IsSuccessStatusCode, await preview.Content.ReadAsStringAsync());
            var result = (await preview.Content.ReadFromJsonAsync<InstanceVersionChangePreviewDto>(JsonOptions))!;
            Assert.Empty(result.Blockers);
            Assert.True(result.Compatible);
        }

        var row = Assert.Single(await StartRowsAsync(model.Id));
        Assert.Equal((signal, v1), (row.SignalName, row.WorkflowDefinitionId));
    }

    [Fact]
    public async Task Visibility_AManagedClientSeesItsStartsWithoutDescriptionsAndNotAnothersBroadcast()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal);
        await CreateAsync(model);
        var clientId = $"signal-start-{Guid.NewGuid():N}"[..24];
        string secret;
        using (var create = await SendAsync(HttpMethod.Post, "/api/shared-variable-clients",
                   new CreateSharedVariableClientRequest(clientId, "Signal start client",
                       [SharedVariableClientScopes.SignalsThrow, SharedVariableClientScopes.SignalsRead])))
        {
            Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
            secret = (await create.Content.ReadFromJsonAsync<CreateSharedVariableClientResult>(JsonOptions))!.ClientSecret;
        }

        async Task<SignalBroadcastDetailDto> ClientThrowAsync(object payload)
        {
            using var response = await SendAsClientAsync(HttpMethod.Post, "/api/signal-broadcasts", clientId, secret,
                new ThrowSignalRequest(signal, model.Id, Payload: Payload(payload)));
            Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
        }

        async Task<SignalDeliveryDto> ClientDeliveryAsync(long broadcastId)
        {
            using var response = await SendAsClientAsync(HttpMethod.Get,
                $"/api/signal-broadcasts/{broadcastId}/deliveries?kind=start", clientId, secret);
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            return Assert.Single((await response.Content.ReadFromJsonAsync<PagedResult<SignalDeliveryDto>>(JsonOptions))!.Items);
        }

        var started = await ClientThrowAsync(new { recallNumber = "R-C1", product = new { name = "Kettle" } });
        var failing = await ClientThrowAsync(new { recallNumber = "R-C2" });
        await ProcessDeliveryJobsAsync(started.Id);
        await ProcessDeliveryJobsAsync(failing.Id);

        var startedDelivery = await ClientDeliveryAsync(started.Id);
        Assert.Equal((SignalDeliveryKinds.Start, SignalDeliveryResultCodes.Started), (startedDelivery.Kind, startedDelivery.ResultCode));
        Assert.NotNull(startedDelivery.InstanceId);
        var failedDelivery = await ClientDeliveryAsync(failing.Id);
        Assert.Equal(SignalDeliveryResultCodes.PayloadInvalid, failedDelivery.ResultCode);
        Assert.Null(failedDelivery.ResultDescription);
        Assert.NotNull(Assert.Single(await DeliveriesAsync(failing.Id)).ResultDescription);

        // A broadcast the client did not throw is not found.
        var other = await ThrowApiAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: Payload(new { recallNumber = "R-C3", product = new { name = "Toaster" } })));
        using (var hidden = await SendAsClientAsync(HttpMethod.Get, $"/api/signal-broadcasts/{other.Id}/deliveries", clientId, secret))
        {
            Assert.Equal(HttpStatusCode.NotFound, hidden.StatusCode);
        }
        await ProcessDeliveryJobsAsync(other.Id);
    }

    [Fact]
    public async Task Schema_TheShapeCheckAndTheStartIndexRefuseInvalidStartRows()
    {
        var signal = UniqueSignal();
        var model = RecallModel(signal);
        await CreateAsync(model);
        var broadcast = await ThrowApiAsync(new ThrowSignalRequest(
            signal, model.Id, Payload: Payload(new { recallNumber = "R-S", product = new { name = "Kettle" } })));
        long pendingId;
        await using (var db = fixture.CreateDbContext())
        {
            pendingId = (await db.SignalDeliveries.AsNoTracking().SingleAsync(item => item.BroadcastId == broadcast.Id)).Id;
        }

        static async Task<string> RefusedAsync(Func<AppDbContext, Task> change, AppDbContext db)
        {
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => change(db));
            return Assert.IsType<Npgsql.PostgresException>(error.InnerException).SqlState;
        }

        // A delivered start must name its instance.
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal("23514", await RefusedAsync(async context =>
            {
                var row = await context.SignalDeliveries.SingleAsync(item => item.Id == pendingId);
                row.Status = SignalDeliveryStatuses.Delivered;
                row.CompletedAt = DateTimeOffset.UtcNow;
                await context.SaveChangesAsync();
            }, db));
        }

        // A second start of the same family in one broadcast.
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal("23505", await RefusedAsync(async context =>
            {
                var row = await context.SignalDeliveries.AsNoTracking().SingleAsync(item => item.Id == pendingId);
                row.Id = 0;
                context.SignalDeliveries.Add(row);
                await context.SaveChangesAsync();
            }, db));
        }

        // A start with a subscription, and a wait without one.
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal("23514", await RefusedAsync(async context =>
            {
                var row = await context.SignalDeliveries.AsNoTracking().SingleAsync(item => item.Id == pendingId);
                row.Id = 0;
                row.WorkflowKey = model.Id + "-other";
                row.SubscriptionId = 1;
                context.SignalDeliveries.Add(row);
                await context.SaveChangesAsync();
            }, db));
        }
        await using (var db = fixture.CreateDbContext())
        {
            Assert.Equal("23514", await RefusedAsync(async context =>
            {
                var row = await context.SignalDeliveries.AsNoTracking().SingleAsync(item => item.Id == pendingId);
                row.Id = 0;
                row.Kind = SignalDeliveryKinds.Wait;
                row.WorkflowKey = model.Id + "-other";
                context.SignalDeliveries.Add(row);
                await context.SaveChangesAsync();
            }, db));
        }

        await ProcessDeliveryJobsAsync(broadcast.Id);
        Assert.Equal(SignalDeliveryResultCodes.Started, Assert.Single(await DeliveriesAsync(broadcast.Id)).ResultCode);
    }

    [Fact]
    public async Task Migration_ChecksTheDeliveryShapeAllowsOneStartPerFamilyAndGuardsItsDowngrade()
    {
        const string previous = "20261121134938_AddSignalThrowEvents";
        const string latest = "20261122161924_ValidateSignalStartConstraints";
        var databaseName = "signal_start_migration_" + Guid.NewGuid().ToString("N");
        var adminBuilder = new Npgsql.NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "postgres" };
        await ExecuteSqlAsync(adminBuilder.ConnectionString, $"CREATE DATABASE \"{databaseName}\"");
        var connectionString = new Npgsql.NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName }.ConnectionString;
        try
        {
            await MigrateToAsync(connectionString, latest);
            var broadcastId = await ScalarAsync(connectionString, """
                INSERT INTO flowbit.signal_broadcasts
                    ("SignalName", "Status", "TotalDeliveryCount", "DeliveredCount", "SkippedCount", "FailedCount",
                     "CancelledCount", "RecipientsJson", "RecipientsTruncated", "ThrownByKind", "ThrownBy", "ThrownByRolesJson",
                     "AutomaticActivationCount")
                VALUES ('a.b', 'delivering', 2, 0, 0, 0, 0, '[]', false, 'user', 'tester', '[]', 0)
                RETURNING "Id"
                """);
            const string delivery = """
                INSERT INTO flowbit.signal_deliveries
                    ("BroadcastId", "Kind", "SubscriptionId", "InstanceId", "WorkflowDefinitionId", "WorkflowKey", "TokenId",
                     "ActivationId", "SignalNodeId", "AttachedToNodeId", "CancelActivity", "Status", "AttemptCount", "CompletedAt")
                VALUES ({0}, {1}, {2}, {3}, 1, {4}, {5}, {6}, 1, NULL, false, {7}, 0, {8})
                """;
            string Row(string kind, string subscription, string instance, string key, string token, string activation,
                string status = "'pending'", string completed = "NULL") =>
                string.Format(delivery, broadcastId, kind, subscription, instance, key, token, activation, status, completed);

            // The shape of each kind.
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteSqlAsync(connectionString,
                Row("'wait'", "NULL", "7", "'orders'", "8", "gen_random_uuid()")));
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteSqlAsync(connectionString,
                Row("'start'", "1", "NULL", "'orders'", "NULL", "NULL")));
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteSqlAsync(connectionString,
                Row("'start'", "NULL", "7", "'orders'", "NULL", "NULL")));
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteSqlAsync(connectionString,
                Row("'start'", "NULL", "NULL", "'orders'", "NULL", "NULL", "'delivered'", "now()")));
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteSqlAsync(connectionString,
                Row("'other'", "NULL", "NULL", "'orders'", "NULL", "NULL")));
            await ExecuteSqlAsync(connectionString, Row("'start'", "NULL", "NULL", "'orders'", "NULL", "NULL"));
            await ExecuteSqlAsync(connectionString, Row("'wait'", "1", "7", "'orders'", "8", "gen_random_uuid()"));

            // One start per broadcast and family.
            var duplicate = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteSqlAsync(connectionString,
                Row("'start'", "NULL", "NULL", "'orders'", "NULL", "NULL")));
            Assert.Equal("23505", duplicate.SqlState);
            await ExecuteSqlAsync(connectionString, Row("'start'", "NULL", "NULL", "'invoices'", "NULL", "NULL"));

            // The start count stays within the total.
            await Assert.ThrowsAsync<Npgsql.PostgresException>(() => ExecuteSqlAsync(connectionString,
                $"UPDATE flowbit.signal_broadcasts SET \"StartDeliveryCount\" = 3 WHERE \"Id\" = {broadcastId}"));

            // The downgrade refuses while a start delivery exists, before it changes anything: the start
            // index and both migrations stay. Then it succeeds and can be applied again.
            const string startIndex = "SELECT to_regclass('flowbit.\"IX_signal_deliveries_start\"')::text";
            const string applied = "SELECT count(*) FROM flowbit.\"__EFMigrationsHistory\" WHERE \"MigrationId\" LIKE '20261122%'";
            var error = await Assert.ThrowsAnyAsync<Exception>(() => MigrateToAsync(connectionString, previous));
            Assert.Contains("Signal start deliveries exist", error.ToString(), StringComparison.Ordinal);
            Assert.Equal("flowbit.\"IX_signal_deliveries_start\"", await ScalarAsync(connectionString, startIndex));
            Assert.Equal("2", await ScalarAsync(connectionString, applied));
            await ExecuteSqlAsync(connectionString, "DELETE FROM flowbit.signal_deliveries WHERE \"Kind\" = 'start'");

            // It refuses as well while a stored definition has a signal start.
            var definitionJson = JsonSerializer.Serialize(RecallModel("migration.start", "recall-notices"), JsonOptions)
                .Replace("'", "''", StringComparison.Ordinal);
            var definitionId = await ScalarAsync(connectionString, $"""
                INSERT INTO flowbit.workflow_definitions ("Name", "WorkflowKey", "Version", "Definition", "IsPublished", "IsDefault")
                VALUES ('Recall notice', 'recall-notices', 1, '{definitionJson}'::jsonb, false, false)
                RETURNING "Id"
                """);
            var definitionError = await Assert.ThrowsAnyAsync<Exception>(() => MigrateToAsync(connectionString, previous));
            Assert.Contains("Workflow definitions with a signal start event exist", definitionError.ToString(), StringComparison.Ordinal);
            Assert.Equal("flowbit.\"IX_signal_deliveries_start\"", await ScalarAsync(connectionString, startIndex));
            Assert.Equal("2", await ScalarAsync(connectionString, applied));
            await ExecuteSqlAsync(connectionString, $"DELETE FROM flowbit.workflow_definitions WHERE \"Id\" = {definitionId}");

            await MigrateToAsync(connectionString, previous);
            Assert.Null(await ScalarAsync(connectionString, "SELECT to_regclass('flowbit.signal_start_subscriptions')::text"));
            await MigrateToAsync(connectionString, latest);
            Assert.Equal("flowbit.signal_start_subscriptions", await ScalarAsync(connectionString,
                "SELECT to_regclass('flowbit.signal_start_subscriptions')::text"));
        }
        finally
        {
            Npgsql.NpgsqlConnection.ClearAllPools();
            await ExecuteSqlAsync(adminBuilder.ConnectionString, $"DROP DATABASE \"{databaseName}\" WITH (FORCE)");
        }
    }

    // Helpers

    private async Task<int> FamilyInstanceCountAsync(string workflowKey)
    {
        await using var db = fixture.CreateDbContext();
        return await db.WorkflowInstances.CountAsync(item => item.WorkflowKey == workflowKey);
    }

    private async Task PrioritizeDeliveryJobsAsync(long broadcastId)
    {
        await using var db = fixture.CreateDbContext();
        var open = await db.SignalBroadcastJobs.AsNoTracking()
            .Where(link => link.BroadcastId == broadcastId && link.JobId != null)
            .Select(link => link.JobId!.Value)
            .ToListAsync();
        foreach (var job in await db.WorkflowJobs.Where(job => open.Contains(job.Id)).ToListAsync())
        {
            job.Priority = 1_000_000;
            job.DueAt = DateTimeOffset.UtcNow.AddMinutes(-1);
        }
        await db.SaveChangesAsync();
    }

    private async Task<HttpResponseMessage> SendAsClientAsync(
        HttpMethod method, string path, string clientId, string secret, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        request.Headers.Add("X-Client-Id", clientId);
        request.Headers.Add("X-Client-Secret", secret);
        return await fixture.Client.SendAsync(request);
    }

    private static async Task MigrateToAsync(string connectionString, string targetMigration)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, FlowbitDatabase.ConfigureProvider)
            .Options;
        await using var context = new AppDbContext(options);
        await Microsoft.EntityFrameworkCore.Infrastructure.AccessorExtensions.GetService<Microsoft.EntityFrameworkCore.Migrations.IMigrator>(context)
            .MigrateAsync(targetMigration);
    }

    private static async Task ExecuteSqlAsync(string connectionString, string sql)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string?> ScalarAsync(string connectionString, string sql)
    {
        await using var connection = new Npgsql.NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new Npgsql.NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? null : Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture);
    }
}
