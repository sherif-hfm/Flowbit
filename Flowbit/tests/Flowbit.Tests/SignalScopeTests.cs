using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
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
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// EVENT-04A: a throw can name the instance with one business key in a family, or one
/// instance. The freeze statement stays one statement without a lock, the broadcast
/// records its scope, and a repeated idempotency key compares the scope.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class SignalScopeTests(PostgresApiFixture fixture)
{
    private const string Actor = "signal-scope";
    private const string PreviousMigration = "20261116135758_ValidateSignalEventConstraints";
    private const string ScopeMigration = "20261120144115_AddSignalBroadcastScope";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task BusinessKeyScope_ReachesOnlyTheRunningInstanceWithTheKey()
    {
        var signal = UniqueSignal();
        var family = OrderFamily(signal, keyed: false);
        var unkeyedVersion = await CreateAsync(family);
        // Started before the family enabled business keys, so it has none.
        var unkeyed = await StartAsync(unkeyedVersion.Id, "ORD-A");
        var keyedVersion = await NewVersionAsync(unkeyedVersion.Id, OrderFamily(signal, keyed: true, family.Id));
        await SetDefaultAsync(keyedVersion.Id);
        var owner = await StartAsync(keyedVersion.Id, "ORD-A");
        var other = await StartAsync(keyedVersion.Id, "ORD-B");
        var elsewhere = await CreateAsync(OrderFamily(signal, keyed: true));
        var sameKeyElsewhere = await StartAsync(elsewhere.Id, "ORD-A");

        var otherCase = await ThrowAsync(new ThrowSignalRequest(signal, family.Id, BusinessKey: "ord-a"));
        Assert.Equal((SignalBroadcastStatuses.Completed, 0), (otherCase.Status, otherCase.TotalDeliveryCount));

        var broadcast = await ThrowAsync(new ThrowSignalRequest(
            signal, family.Id, Payload: Version(7), BusinessKey: "  ORD-A "));
        Assert.Equal((family.Id, false, "ORD-A", (long?)null, 1),
            (broadcast.WorkflowKey, broadcast.AllWorkflows, broadcast.BusinessKey, broadcast.InstanceId, broadcast.TotalDeliveryCount));
        Assert.Equal(owner, Assert.Single((await DeliveriesAsync(broadcast.Id)).Items).InstanceId);

        await ProcessDeliveryJobsAsync(broadcast.Id);
        Assert.Equal(SignalDeliveryResultCodes.Caught, Assert.Single((await DeliveriesAsync(broadcast.Id)).Items).ResultCode);
        Assert.Equal(0, await ActiveSubscriptionCountAsync(owner));
        foreach (var waiting in new[] { unkeyed, other, sameKeyElsewhere })
        {
            Assert.Equal(1, await ActiveSubscriptionCountAsync(waiting));
        }
    }

    [Fact]
    public async Task BusinessKeyScope_ReachesEveryWaitOfTheOwnerAndNoFinishedInstance()
    {
        var signal = UniqueSignal();
        var model = ParallelCatchesModel(signal);
        var created = await CreateAsync(model);
        var finished = await StartAsync(created.Id, "ORD-1");
        Assert.Equal(2, await ActiveSubscriptionCountAsync(finished));
        using (var cancel = await SendAsync(HttpMethod.Post, $"/api/instances/{finished}/cancel", new { }))
        {
            Assert.True(cancel.IsSuccessStatusCode, await cancel.Content.ReadAsStringAsync());
        }
        // With active uniqueness the cancelled instance releases its key.
        var owner = await StartAsync(created.Id, "ORD-1");

        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, BusinessKey: "ORD-1"));
        Assert.Equal(2, broadcast.TotalDeliveryCount);
        Assert.All((await DeliveriesAsync(broadcast.Id)).Items, delivery => Assert.Equal(owner, delivery.InstanceId));
    }

    [Fact]
    public async Task BusinessKeyScope_NeedsAFamilyThatEnabledBusinessKeys()
    {
        var signal = UniqueSignal();
        var model = OrderFamily(signal, keyed: false);
        var unkeyed = await CreateAsync(model);

        using (var refused = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts",
                   new ThrowSignalRequest(signal, model.Id, BusinessKey: "ORD-A")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains($"Workflow '{model.Id}' has not enabled business keys, so businessKey can match none of its instances.",
                await refused.Content.ReadAsStringAsync());
        }
        await using (var db = fixture.CreateDbContext())
        {
            Assert.False(await db.SignalBroadcasts.AnyAsync(item => item.SignalName == signal));
        }

        // The same family accepts a throw without the key, and the key once it is enabled.
        Assert.Equal(model.Id, (await ThrowAsync(new ThrowSignalRequest(signal, model.Id))).WorkflowKey);
        var keyed = await NewVersionAsync(unkeyed.Id, OrderFamily(signal, keyed: true, model.Id));
        await SetDefaultAsync(keyed.Id);
        Assert.Equal("ORD-A", (await ThrowAsync(new ThrowSignalRequest(signal, model.Id, BusinessKey: "ORD-A"))).BusinessKey);
    }

    [Fact]
    public async Task InstanceScope_ReachesOnlyTheNamedInstanceAndRecordsItsFamily()
    {
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        var created = await CreateAsync(model);
        var first = await StartAsync(created.Id, null);
        var second = await StartAsync(created.Id, null);

        var broadcast = await ThrowAsync(new ThrowSignalRequest(signal, Payload: Version(3), InstanceId: first));
        Assert.Equal((model.Id, false, (string?)null, (long?)first, 1),
            (broadcast.WorkflowKey, broadcast.AllWorkflows, broadcast.BusinessKey, broadcast.InstanceId, broadcast.TotalDeliveryCount));
        Assert.Equal(first, Assert.Single((await DeliveriesAsync(broadcast.Id)).Items).InstanceId);
        await ProcessDeliveryJobsAsync(broadcast.Id);
        Assert.Equal(0, await ActiveSubscriptionCountAsync(first));
        Assert.Equal(1, await ActiveSubscriptionCountAsync(second));

        // Running but no longer waiting, and finished: accepted with no deliveries and no job.
        var notWaiting = await ThrowAsync(new ThrowSignalRequest(signal, InstanceId: first));
        using (var cancel = await SendAsync(HttpMethod.Post, $"/api/instances/{second}/cancel", new { }))
        {
            Assert.True(cancel.IsSuccessStatusCode, await cancel.Content.ReadAsStringAsync());
        }
        var finished = await ThrowAsync(new ThrowSignalRequest(signal, InstanceId: second));
        await using var db = fixture.CreateDbContext();
        foreach (var empty in new[] { notWaiting, finished })
        {
            Assert.Equal((SignalBroadcastStatuses.Completed, 0), (empty.Status, empty.TotalDeliveryCount));
            Assert.False(await db.SignalBroadcastJobs.AnyAsync(link => link.BroadcastId == empty.Id));
        }

        // The family filter of the list finds an instance-scoped broadcast.
        using var list = await SendAsync(HttpMethod.Get,
            $"/api/signal-broadcasts?name={signal}&workflowKey={model.Id}&pageSize=200");
        var page = (await list.Content.ReadFromJsonAsync<PagedResult<SignalBroadcastSummaryDto>>(JsonOptions))!;
        Assert.Contains(page.Items, item => item.Id == broadcast.Id && item.InstanceId == first && !item.AllWorkflows);
    }

    [Fact]
    public async Task NarrowScopes_ReachOnlyOneOrdersBoundariesInTheShippedExample()
    {
        // The catalog's signal boundaries example, whose orderNumber is its business key.
        var model = JsonSerializer.Deserialize<WorkflowModel>(
            ExampleWorkflowData.Read("examples/signals/02-signal-boundaries-recall.json"), JsonOptions)!;
        Assert.Equal("orderNumber", model.FlowNodes.Single(node => node.Id == 1).BusinessKey!.Variable);
        var suffix = Guid.NewGuid().ToString("N");
        model.Id = $"signal-scope-example-{suffix}";
        var recall = $"test.scope-recall-{suffix}";
        var price = $"test.scope-price-{suffix}";
        model.FlowNodes.Single(node => node.Id == 3).Signal!.Name = recall;
        model.FlowNodes.Single(node => node.Id == 4).Signal!.Name = price;
        var created = await CreateAsync(model);
        var first = await StartAsync(created.Id, "ORD-2001");
        var second = await StartAsync(created.Id, "ORD-2002");

        // A price update by business key adds a branch to the first order only.
        var update = await ThrowAsync(new ThrowSignalRequest(
            price, model.Id, Payload: JsonSerializer.SerializeToElement(new { unitPrice = 13 }), BusinessKey: "ORD-2001"));
        Assert.Equal(1, update.TotalDeliveryCount);
        await ProcessDeliveryJobsAsync(update.Id);
        var branched = Assert.Single((await DeliveriesAsync(update.Id)).Items);
        Assert.Equal((first, SignalDeliveryResultCodes.Branched, false),
            (branched.InstanceId, branched.ResultCode, branched.CancelActivity));
        Assert.Equal(2, await ActiveSubscriptionCountAsync(first));
        Assert.Equal(2, await ActiveSubscriptionCountAsync(second));

        // A recall by instance ID interrupts the second order only.
        var stop = await ThrowAsync(new ThrowSignalRequest(
            recall, Payload: JsonSerializer.SerializeToElement(new { reason = "Supplier recall" }), InstanceId: second));
        Assert.Equal((1, model.Id), (stop.TotalDeliveryCount, stop.WorkflowKey));
        await ProcessDeliveryJobsAsync(stop.Id);
        var interrupted = Assert.Single((await DeliveriesAsync(stop.Id)).Items);
        Assert.Equal((second, SignalDeliveryResultCodes.Interrupted, true),
            (interrupted.InstanceId, interrupted.ResultCode, interrupted.CancelActivity));
        Assert.Equal(0, await ActiveSubscriptionCountAsync(second));
        Assert.Equal(2, await ActiveSubscriptionCountAsync(first));
    }

    [Theory]
    [InlineData("""{"name":"a.b","workflowKey":"x","businessKey":"  "}""", "businessKey cannot be blank; omit it to reach every instance of the workflow family.")]
    [InlineData("""{"name":"a.b","instanceId":0}""", "instanceId must be a positive whole number.")]
    [InlineData("""{"name":"a.b","instanceId":5,"workflowKey":"x"}""", "instanceId states the whole scope; do not combine it with workflowKey, allWorkflows, or businessKey.")]
    [InlineData("""{"name":"a.b","instanceId":5,"allWorkflows":true}""", "instanceId states the whole scope")]
    [InlineData("""{"name":"a.b","instanceId":5,"businessKey":"K"}""", "instanceId states the whole scope")]
    [InlineData("""{"name":"a.b","businessKey":"K"}""", "businessKey needs a workflowKey: a business key identifies an instance only within one workflow family.")]
    [InlineData("""{"name":"a.b","businessKey":"K","allWorkflows":true}""", "businessKey needs a workflowKey")]
    [InlineData("""{"name":"a.b"}""", "A signal must name one workflowKey, set allWorkflows to true, or name one instanceId.")]
    [InlineData("""{"name":"a.b","workflowKey":"x","allWorkflows":true}""", "A signal must name one workflowKey, set allWorkflows to true, or name one instanceId.")]
    [InlineData("""{"name":"a.b","instanceId":"987654321987"}""", "No workflow instance has the ID 987654321987.")]
    [InlineData("""{"name":"a.b","instanceId":1.5}""", "instanceId")]
    [InlineData("""{"name":"a.b","instanceId":"abc"}""", "instanceId")]
    [InlineData("""{"name":"a.b","workflowKey":"x","businessKey":5}""", "businessKey")]
    public async Task Throw_RefusesAnInvalidScopeWithItsMessage(string body, string expected)
    {
        using var response = await SendRawAsync("/api/signal-broadcasts", body);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(expected, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Throw_RefusesABusinessKeyLongerThan300Characters()
    {
        // 300 scalar values pass the length check even when one of them needs two UTF-16 units.
        var exact = new string('k', 299) + "\U0001F600";
        using (var accepted = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts",
                   new ThrowSignalRequest("a.b", "no-such-family-scope", BusinessKey: exact)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, accepted.StatusCode);
            Assert.Contains("No workflow definition has the key", await accepted.Content.ReadAsStringAsync());
        }

        using var refused = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts",
            new ThrowSignalRequest("a.b", "x", BusinessKey: exact + "k"));
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("businessKey can have at most 300 characters.", await refused.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Idempotency_TreatsAnotherScopeAsAnotherRequest()
    {
        var signal = UniqueSignal();
        var model = OrderFamily(signal, keyed: true);
        var created = await CreateAsync(model);
        var instance = await StartAsync(created.Id, "ORD-A");

        var key = $"scope-{Guid.NewGuid():N}";
        var first = await ThrowAsync(new ThrowSignalRequest(signal, model.Id, IdempotencyKey: key, BusinessKey: " ORD-A "));
        await AssertReplayAsync(new ThrowSignalRequest(signal, model.Id, IdempotencyKey: key, BusinessKey: "ORD-A"), first.Id);
        await AssertConflictAsync(new ThrowSignalRequest(signal, model.Id, IdempotencyKey: key, BusinessKey: "ORD-B"));
        await AssertConflictAsync(new ThrowSignalRequest(signal, model.Id, IdempotencyKey: key));
        await AssertConflictAsync(new ThrowSignalRequest(signal, IdempotencyKey: key, InstanceId: instance));

        var instanceKey = $"scope-{Guid.NewGuid():N}";
        var byInstance = await ThrowAsync(new ThrowSignalRequest(signal, IdempotencyKey: instanceKey, InstanceId: instance));
        await AssertReplayAsync(new ThrowSignalRequest(signal, IdempotencyKey: instanceKey, InstanceId: instance), byInstance.Id);
        await AssertConflictAsync(new ThrowSignalRequest(signal, IdempotencyKey: instanceKey, InstanceId: instance + 1_000_000));
        await AssertConflictAsync(new ThrowSignalRequest(signal, IdempotencyKey: instanceKey, AllWorkflows: true));

        async Task AssertReplayAsync(ThrowSignalRequest request, long expectedId)
        {
            using var replay = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts", request);
            Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
            Assert.Equal(expectedId, (await replay.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!.Id);
        }

        async Task AssertConflictAsync(ThrowSignalRequest request)
        {
            using var conflict = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts", request);
            Assert.Equal(HttpStatusCode.Conflict, conflict.StatusCode);
        }
    }

    [Fact]
    public async Task RequestHash_OfARequestWithoutTheNewFieldsIsUnchanged()
    {
        const string payload = """{"version":7,"note":"a"}""";
        const string canonical = """{"note":"a","version":7}""";
        Assert.Equal(
            Sha256($"policy.changed\nclaims\n{canonical}"),
            SignalBroadcastService.HashRequest("policy.changed", SignalBroadcastScope.Family("claims"), payload));
        Assert.Equal(
            Sha256("policy.changed\n*\nnull"),
            SignalBroadcastService.HashRequest("policy.changed", SignalBroadcastScope.AllWorkflows, null));
        // A key that contains a line break cannot imitate an instance line.
        Assert.NotEqual(
            SignalBroadcastService.HashRequest("a.b", SignalBroadcastScope.ForBusinessKey("x", "k\ninstance:5"), null),
            SignalBroadcastService.HashRequest("a.b", SignalBroadcastScope.ForBusinessKey("x", "k"), null));

        // A broadcast stored before the upgrade, with the hash of that time, still replays.
        var signal = UniqueSignal();
        var model = SignalRuntimeTests.CatchModel(signal);
        await CreateAsync(model);
        var key = $"stored-{Guid.NewGuid():N}";
        var stored = new SignalBroadcastEntity
        {
            SignalName = signal,
            WorkflowKey = model.Id,
            PayloadJson = payload,
            Status = SignalBroadcastStatuses.Completed,
            RecipientsJson = "[]",
            ThrownByKind = SignalThrowerKinds.User,
            ThrownBy = Actor,
            ThrownByRolesJson = "[\"admin\"]",
            IdempotencyKey = key,
            RequestHash = Sha256($"{signal}\n{model.Id}\n{canonical}"),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            CompletedAt = DateTimeOffset.UtcNow
        };
        await using (var db = fixture.CreateDbContext())
        {
            db.SignalBroadcasts.Add(stored);
            await db.SaveChangesAsync();
        }

        using var replay = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts",
            new ThrowSignalRequest(signal, model.Id, Payload: JsonDocument.Parse(payload).RootElement, IdempotencyKey: key));
        Assert.Equal(HttpStatusCode.OK, replay.StatusCode);
        var replayed = (await replay.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
        Assert.Equal((stored.Id, (string?)null, (long?)null), (replayed.Id, replayed.BusinessKey, replayed.InstanceId));
    }

    [Theory]
    [InlineData(SignalBroadcastScopeKind.BusinessKey)]
    [InlineData(SignalBroadcastScopeKind.Instance)]
    public async Task ScopedThrow_DoesNotWaitForARecipientWhoseInstanceIsLocked(SignalBroadcastScopeKind kind)
    {
        var signal = UniqueSignal();
        var model = OrderFamily(signal, keyed: true);
        var created = await CreateAsync(model);
        var instance = await StartAsync(created.Id, "ORD-LOCKED");

        await using var holder = fixture.CreateDbContext();
        await using var held = await holder.Database.BeginTransactionAsync();
        // Hold the instance and its tokens as a transition does.
        await holder.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM flowbit.workflow_instances WHERE \"Id\" = {instance} FOR UPDATE");
        await holder.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM flowbit.execution_tokens WHERE \"InstanceId\" = {instance} FOR UPDATE");

        var request = kind == SignalBroadcastScopeKind.Instance
            ? new ThrowSignalRequest(signal, Payload: Version(4), InstanceId: instance)
            : new ThrowSignalRequest(signal, model.Id, Payload: Version(4), BusinessKey: "ORD-LOCKED");
        var throwTask = ThrowAsync(request);
        Assert.Same(throwTask, await Task.WhenAny(throwTask, Task.Delay(TimeSpan.FromSeconds(15))));
        Assert.Equal(1, (await throwTask).TotalDeliveryCount);
        await held.RollbackAsync();
    }

    [Fact]
    public async Task Summary_CountsTheWaitsOfABusinessKeyOrAnInstance()
    {
        var signal = UniqueSignal();
        var model = OrderFamily(signal, keyed: true);
        var created = await CreateAsync(model);
        await StartAsync(created.Id, "ORD-A");
        var second = await StartAsync(created.Id, "ORD-B");
        var unkeyed = OrderFamily(signal, keyed: false);
        await CreateAsync(unkeyed);

        Assert.Equal(2, await CountAsync($"name={signal}&workflowKey={model.Id}"));
        Assert.Equal(1, await CountAsync($"name={signal}&workflowKey={model.Id}&businessKey=ORD-A"));
        Assert.Equal(1, await CountAsync($"name={signal}&workflowKey={model.Id}&businessKey=%20ORD-A%20"));
        Assert.Equal(0, await CountAsync($"name={signal}&workflowKey={model.Id}&businessKey=ord-a"));
        Assert.Equal(1, await CountAsync($"name={signal}&instanceId={second}"));
        // A blank value is no filter; an unknown instance and a family without keys count nothing.
        Assert.Equal(2, await CountAsync($"name={signal}&workflowKey={model.Id}&businessKey=%20"));
        Assert.Equal(0, await CountAsync($"name={signal}&instanceId=987654321987"));
        Assert.Equal(0, await CountAsync($"name={signal}&workflowKey={unkeyed.Id}&businessKey=ORD-A"));

        await AssertRefusedAsync($"name={signal}&businessKey=ORD-A",
            "businessKey needs a workflowKey: a business key identifies an instance only within one workflow family.");
        await AssertRefusedAsync($"name={signal}&instanceId={second}&workflowKey={model.Id}",
            "instanceId cannot be combined with workflowKey or businessKey.");
        await AssertRefusedAsync($"name={signal}&instanceId=0", "instanceId must be a positive whole number.");

        async Task<int> CountAsync(string query)
        {
            using var response = await SendAsync(HttpMethod.Get, $"/api/signal-subscriptions/summary?{query}");
            Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
            var summary = (await response.Content.ReadFromJsonAsync<SignalSubscriptionSummaryDto>(JsonOptions))!;
            return summary.Items.Sum(item => item.Count);
        }

        async Task AssertRefusedAsync(string query, string message)
        {
            using var response = await SendAsync(HttpMethod.Get, $"/api/signal-subscriptions/summary?{query}");
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains(message, await response.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task ManagedClient_ThrowsWithEachNewScopeAndReadsItsOwnScope()
    {
        var signal = UniqueSignal();
        var model = OrderFamily(signal, keyed: true);
        var created = await CreateAsync(model);
        var instance = await StartAsync(created.Id, "ORD-CLIENT");
        var clientId = $"scope-client-{Guid.NewGuid():N}"[..40];
        string secret;
        using (var create = await SendAsync(HttpMethod.Post, "/api/shared-variable-clients",
                   new CreateSharedVariableClientRequest(clientId, "Signal scope client",
                       [SharedVariableClientScopes.SignalsThrow, SharedVariableClientScopes.SignalsRead])))
        {
            Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
            secret = (await create.Content.ReadFromJsonAsync<CreateSharedVariableClientResult>(JsonOptions))!.ClientSecret;
        }

        var byKey = await ClientThrowAsync(new ThrowSignalRequest(signal, model.Id, BusinessKey: "ORD-CLIENT"));
        var byInstance = await ClientThrowAsync(new ThrowSignalRequest(signal, InstanceId: instance));
        Assert.Equal(("ORD-CLIENT", 1), (byKey.BusinessKey, byKey.TotalDeliveryCount));
        Assert.Equal((instance, model.Id, 1), (byInstance.InstanceId!.Value, byInstance.WorkflowKey, byInstance.TotalDeliveryCount));

        using var read = await SendAsClientAsync(HttpMethod.Get, $"/api/signal-broadcasts/{byInstance.Id}", clientId, secret);
        Assert.Equal(instance, (await read.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!.InstanceId);
        using var list = await SendAsClientAsync(HttpMethod.Get, "/api/signal-broadcasts?pageSize=200", clientId, secret);
        var page = (await list.Content.ReadFromJsonAsync<PagedResult<SignalBroadcastSummaryDto>>(JsonOptions))!;
        Assert.Equal(("ORD-CLIENT", (long?)null), page.Items.Where(item => item.Id == byKey.Id)
            .Select(item => (item.BusinessKey, item.InstanceId)).Single());

        async Task<SignalBroadcastDetailDto> ClientThrowAsync(ThrowSignalRequest request)
        {
            using var response = await SendAsClientAsync(HttpMethod.Post, "/api/signal-broadcasts", clientId, secret, request);
            Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
            return (await response.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
        }
    }

    [Fact]
    public async Task BusinessKey_NeverReachesAFlowbitLog()
    {
        var signal = UniqueSignal();
        var model = OrderFamily(signal, keyed: true);
        var created = await CreateAsync(model);
        var businessKey = $"PRIVATE-{Guid.NewGuid():N}";
        await StartAsync(created.Id, businessKey);

        // Another host on the same database whose loggers all write to the capture.
        var logs = new CapturingLoggerFactory();
        await using var host = fixture.Factory.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.AddSingleton<ILoggerFactory>(logs)));
        using var client = host.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

        using (var summary = await SendAsync(client, HttpMethod.Get,
                   $"/api/signal-subscriptions/summary?name={signal}&workflowKey={model.Id}&businessKey={businessKey}"))
        {
            Assert.True(summary.IsSuccessStatusCode, await summary.Content.ReadAsStringAsync());
        }
        long broadcastId;
        using (var thrown = await SendAsync(client, HttpMethod.Post, "/api/signal-broadcasts",
                   new ThrowSignalRequest(signal, model.Id, Payload: Version(1), BusinessKey: businessKey)))
        {
            Assert.Equal(HttpStatusCode.Accepted, thrown.StatusCode);
            broadcastId = (await thrown.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!.Id;
        }
        await ProcessDeliveryJobsAsync(broadcastId, host.Services);

        var flowbit = logs.Entries.Where(entry => entry.Category.StartsWith("Flowbit.", StringComparison.Ordinal)).ToArray();
        Assert.Contains(flowbit, entry => entry.Text.Contains("froze", StringComparison.Ordinal));
        Assert.DoesNotContain(flowbit, entry => entry.Text.Contains(businessKey, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Migration_GuardsItsDowngradeAndChecksTheScopeShape()
    {
        var databaseName = "signal_scope_migration_" + Guid.NewGuid().ToString("N");
        var adminBuilder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = "postgres" };
        await ExecuteAsync(adminBuilder.ConnectionString, $"CREATE DATABASE \"{databaseName}\"");
        var connectionString = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = databaseName }.ConnectionString;
        try
        {
            await MigrateAsync(connectionString, ScopeMigration);
            const string insert = """
                INSERT INTO flowbit.signal_broadcasts
                    ("SignalName", "WorkflowKey", "BusinessKey", "InstanceId", "Status", "TotalDeliveryCount",
                     "DeliveredCount", "SkippedCount", "FailedCount", "CancelledCount", "RecipientsJson",
                     "RecipientsTruncated", "ThrownByKind", "ThrownBy", "ThrownByRolesJson", "CompletedAt")
                VALUES ('a.b', {0}, {1}, {2}, 'completed', 0, 0, 0, 0, 0, '[]', false, 'user', 'admin', '[]', now())
                """;
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(connectionString, string.Format(insert, "'orders'", "'K'", "5")));
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(connectionString, string.Format(insert, "NULL", "'K'", "NULL")));
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(connectionString, string.Format(insert, "'orders'", "''", "NULL")));
            await Assert.ThrowsAsync<PostgresException>(() =>
                ExecuteAsync(connectionString, string.Format(insert, "'orders'", "NULL", "0")));
            await ExecuteAsync(connectionString, string.Format(insert, "NULL", "NULL", "NULL"));
            await ExecuteAsync(connectionString, string.Format(insert, "'orders'", "'K'", "NULL"));

            var error = await Assert.ThrowsAnyAsync<Exception>(() => MigrateAsync(connectionString, PreviousMigration));
            Assert.Contains("business-key or instance scope", error.ToString(), StringComparison.Ordinal);

            await ExecuteAsync(connectionString, "DELETE FROM flowbit.signal_broadcasts WHERE \"BusinessKey\" IS NOT NULL");
            await MigrateAsync(connectionString, PreviousMigration);
            await MigrateAsync(connectionString, ScopeMigration);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await ExecuteAsync(adminBuilder.ConnectionString, $"DROP DATABASE \"{databaseName}\" WITH (FORCE)");
        }
    }

    private static string UniqueSignal() => $"test.scope-{Guid.NewGuid():N}";

    private static JsonElement Version(int version) => JsonSerializer.SerializeToElement(new { version });

    private static byte[] Sha256(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    /// <summary>
    /// The signal catch workflow of <see cref="SignalRuntimeTests.CatchModel"/> with an
    /// order number to start, which is its business key when <paramref name="keyed"/>.
    /// </summary>
    private static WorkflowModel OrderFamily(string signal, bool keyed, string? workflowKey = null)
    {
        var model = SignalRuntimeTests.CatchModel(signal);
        if (workflowKey is not null)
        {
            model.Id = workflowKey;
        }
        var start = model.FlowNodes.Single(node => node.Id == 1);
        start.Variables =
        [
            new VariableModel { Id = 1, Name = "orderNumber", DataType = WorkflowVariableTypes.String, Required = true }
        ];
        if (keyed)
        {
            start.BusinessKey = new BusinessKeyModel { Variable = "orderNumber", Uniqueness = BusinessKeyUniqueness.Active };
        }
        return model;
    }

    /// <summary>A keyed order whose two parallel branches each wait for the same signal.</summary>
    private static WorkflowModel ParallelCatchesModel(string signal)
    {
        FlowNodeModel Catch(int id) => new()
        {
            Id = id,
            Name = $"Wait {id}",
            Type = BpmnFlowNodeTypes.IntermediateSignalCatchEvent,
            Signal = new SignalEventModel { Name = signal, OutputMappings = [] }
        };

        return new WorkflowModel
        {
            Id = $"signal-scope-{Guid.NewGuid():N}",
            Name = "Signal scope parallel",
            InitialEventId = 1,
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Start",
                    Type = BpmnFlowNodeTypes.StartEvent,
                    Variables =
                    [
                        new VariableModel { Id = 1, Name = "orderNumber", DataType = WorkflowVariableTypes.String, Required = true }
                    ],
                    BusinessKey = new BusinessKeyModel { Variable = "orderNumber", Uniqueness = BusinessKeyUniqueness.Active }
                },
                new FlowNodeModel { Id = 2, Name = "Split", Type = BpmnFlowNodeTypes.ParallelGateway },
                Catch(3),
                Catch(4),
                new FlowNodeModel { Id = 5, Name = "Join", Type = BpmnFlowNodeTypes.ParallelGateway },
                new FlowNodeModel { Id = 6, Name = "Review", Type = BpmnFlowNodeTypes.UserTask },
                new FlowNodeModel { Id = 7, Name = "End", Type = BpmnFlowNodeTypes.EndEvent }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
                new SequenceFlowModel { Id = 202, SourceRef = 2, TargetRef = 4 },
                new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 5 },
                new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 5 },
                new SequenceFlowModel { Id = 501, SourceRef = 5, TargetRef = 6 },
                new SequenceFlowModel { Id = 601, SourceRef = 6, TargetRef = 7, Name = "Done" }
            ]
        };
    }

    private async Task<WorkflowDetailDto> CreateAsync(WorkflowModel model)
    {
        using var create = await SendAsync(HttpMethod.Post, "/api/workflows", new CreateWorkflowRequest(model, true));
        Assert.True(create.StatusCode == HttpStatusCode.Created, await create.Content.ReadAsStringAsync());
        return (await create.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions))!;
    }

    private async Task<WorkflowDetailDto> NewVersionAsync(long workflowId, WorkflowModel model)
    {
        using var response = await SendAsync(HttpMethod.Put, $"/api/workflows/{workflowId}", new UpdateWorkflowRequest(model, true));
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<WorkflowDetailDto>(JsonOptions))!;
    }

    private async Task SetDefaultAsync(long workflowId)
    {
        using var response = await SendAsync(HttpMethod.Post, $"/api/workflows/{workflowId}/set-default");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private async Task<long> StartAsync(long workflowId, string? orderNumber)
    {
        var variables = orderNumber is null
            ? null
            : new Dictionary<string, JsonElement> { ["orderNumber"] = JsonSerializer.SerializeToElement(orderNumber) };
        using var start = await SendAsync(HttpMethod.Post, "/api/instances", new StartInstanceRequest(workflowId, null, null, variables));
        Assert.True(start.StatusCode == HttpStatusCode.Created, await start.Content.ReadAsStringAsync());
        using var document = JsonDocument.Parse(await start.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("id").GetInt64();
    }

    private async Task<SignalBroadcastDetailDto> ThrowAsync(ThrowSignalRequest request)
    {
        using var response = await SendAsync(HttpMethod.Post, "/api/signal-broadcasts", request);
        Assert.True(response.StatusCode == HttpStatusCode.Accepted, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<SignalBroadcastDetailDto>(JsonOptions))!;
    }

    private async Task<PagedResult<SignalDeliveryDto>> DeliveriesAsync(long id)
    {
        using var response = await SendAsync(HttpMethod.Get, $"/api/signal-broadcasts/{id}/deliveries?pageSize=200");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PagedResult<SignalDeliveryDto>>(JsonOptions))!;
    }

    private async Task<int> ActiveSubscriptionCountAsync(long instanceId)
    {
        await using var db = fixture.CreateDbContext();
        return await db.SignalSubscriptions.CountAsync(item =>
            item.InstanceId == instanceId && item.Status == SignalSubscriptionStatuses.Active);
    }

    private Task ProcessDeliveryJobsAsync(long broadcastId) =>
        new SignalRuntimeTests(fixture).ProcessDeliveryJobsAsync(broadcastId);

    /// <summary>Processes the broadcast's delivery jobs with another host's services.</summary>
    private async Task ProcessDeliveryJobsAsync(long broadcastId, IServiceProvider services)
    {
        for (var round = 0; round < 20; round++)
        {
            await using (var db = fixture.CreateDbContext())
            {
                var open = await db.SignalBroadcastJobs.AsNoTracking()
                    .Where(link => link.BroadcastId == broadcastId && link.JobId != null)
                    .Join(db.WorkflowJobs, link => link.JobId, job => job.Id, (_, job) => job)
                    .Where(job => job.Status == WorkflowJobStatuses.Queued || job.Status == WorkflowJobStatuses.Retry)
                    .Select(job => job.Id)
                    .ToListAsync();
                if (open.Count == 0)
                {
                    return;
                }

                foreach (var job in await db.WorkflowJobs.Where(job => open.Contains(job.Id)).ToListAsync())
                {
                    job.Priority = 1_000_000;
                    job.DueAt = DateTimeOffset.UtcNow.AddMinutes(-1);
                }
                await db.SaveChangesAsync();
            }

            await using var scope = services.CreateAsyncScope();
            var leases = await scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>().LeaseRunnableAsync(
                new WorkflowJobLeaseRequest($"signal-scope-test-{Guid.NewGuid():N}", 1, 1, 1, TimeSpan.FromMinutes(2)),
                CancellationToken.None);
            foreach (var lease in leases)
            {
                await scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>()
                    .ProcessAsync(lease, CancellationToken.None);
            }
        }

        Assert.Fail($"The delivery jobs of signal broadcast #{broadcastId} did not finish.");
    }

    private static async Task MigrateAsync(string connectionString, string targetMigration)
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(connectionString, FlowbitDatabase.ConfigureProvider)
            .Options;
        await using var context = new AppDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(targetMigration);
    }

    private static async Task ExecuteAsync(string connectionString, string sql)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body = null) =>
        SendAsync(fixture.Client, method, path, body);

    private static async Task<HttpResponseMessage> SendAsync(HttpClient client, HttpMethod method, string path, object? body = null)
    {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, Actor, ["admin"]);
        return await client.SendAsync(request);
    }

    private async Task<HttpResponseMessage> SendRawAsync(string path, string json)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        ApiTestAuth.Authorize(request, Actor, ["admin"]);
        return await fixture.Client.SendAsync(request);
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

    private sealed record CapturedLog(string Category, string Text);

    /// <summary>Records every log line with its structured values, whatever its level.</summary>
    private sealed class CapturingLoggerFactory : ILoggerFactory
    {
        private readonly ConcurrentQueue<CapturedLog> entries = new();

        public IReadOnlyList<CapturedLog> Entries => entries.ToArray();

        public ILogger CreateLogger(string categoryName) => new Logger(categoryName, entries);

        public void AddProvider(ILoggerProvider provider)
        {
        }

        public void Dispose()
        {
        }

        private sealed class Logger(string category, ConcurrentQueue<CapturedLog> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var values = state is IEnumerable<KeyValuePair<string, object?>> pairs
                    ? string.Join(" ", pairs.Select(pair => $"{pair.Key}={pair.Value}"))
                    : string.Empty;
                entries.Enqueue(new CapturedLog(category, $"{formatter(state, exception)} {values} {exception}"));
            }
        }
    }
}
