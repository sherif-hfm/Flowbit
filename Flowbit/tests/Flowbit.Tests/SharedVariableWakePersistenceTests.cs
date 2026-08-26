using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
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

[Collection(PostgresApiCollection.Name)]
public sealed class SharedVariableWakePersistenceTests(PostgresApiFixture fixture)
{
    private const string PreviousSharedVariableMigration = "20260824174251_AddSharedVariables";
    private const string HardenedSharedVariableMigration = "20260825180852_HardenSharedVariablePersistence";

    [Fact]
    public async Task FailedExpansionReturnsToPendingAndCanBeLeasedAgain()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
        await using var transaction = await db.Database.BeginTransactionAsync();

        // Isolate acquisition order inside the rollback-only transaction.
        await db.SharedVariableWakes
            .Where(wake => wake.Status == SharedVariableWakeStatuses.Pending
                || wake.Status == SharedVariableWakeStatuses.Leased)
            .ExecuteUpdateAsync(setters => setters
            .SetProperty(wake => wake.Status, SharedVariableWakeStatuses.Cancelled)
            .SetProperty(wake => wake.LeaseToken, (Guid?)null)
            .SetProperty(wake => wake.LeasedBy, (string?)null)
            .SetProperty(wake => wake.LeaseExpiresAt, (DateTimeOffset?)null)
            .SetProperty(wake => wake.HeartbeatAt, (DateTimeOffset?)null)
            .SetProperty(wake => wake.CompletedAt, DateTimeOffset.UtcNow));

        var key = $"tests.retry.{Guid.NewGuid():N}";
        var created = await repository.CreateAsync(
            new SharedVariableCreateCommand(
                key,
                "number",
                IsArray: false,
                Nullable: false,
                Validation: null,
                Description: null,
                HasValue: true,
                JsonSerializer.SerializeToElement(1),
                new SharedVariableCallerRecord(
                    SharedVariableCallerKinds.System,
                    "shared-variable-wake-test",
                    [],
                    []),
                SharedVariableSources.System,
                RequestId: null,
                RequestFingerprint: null,
                Reason: null),
            CancellationToken.None);

        var revision = await db.SharedVariableRevisions.SingleAsync(item =>
            item.SharedVariableId == created.Variable.Id
            && item.Revision == created.Revision.Revision);
        db.SharedVariableWakes.Add(new SharedVariableWakeEntity
        {
            SharedVariableId = created.Variable.Id,
            RevisionId = revision.Id,
            Revision = revision.Revision,
            Status = SharedVariableWakeStatuses.Pending,
            MaxAttempts = 25,
            AvailableAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        const string firstWorker = "retry-test-1";
        var first = Assert.Single(await repository.LeaseWakeExpansionsAsync(
            new SharedVariableWakeLeaseRequest(
                firstWorker,
                MaxCount: 10,
                TimeSpan.FromMinutes(1)),
            CancellationToken.None));

        var firstFence = new SharedVariableWakeFence(
            SharedVariableWakeWorkKinds.Expansion,
            first.Id,
            firstWorker,
            first.LeaseToken,
            first.LeaseGeneration);
        var initialLease = await db.SharedVariableWakes.AsNoTracking()
            .SingleAsync(wake => wake.Id == first.Id);
        Assert.NotNull(initialLease.HeartbeatAt);
        Assert.Equal(initialLease.UpdatedAt, initialLease.HeartbeatAt);
        Assert.True(await repository.HeartbeatWakeAsync(
            firstFence,
            TimeSpan.FromMinutes(1),
            CancellationToken.None));
        var heartbeated = await db.SharedVariableWakes.AsNoTracking()
            .SingleAsync(wake => wake.Id == first.Id);
        Assert.True(heartbeated.HeartbeatAt >= initialLease.HeartbeatAt);
        Assert.True(heartbeated.LeaseExpiresAt >= initialLease.LeaseExpiresAt);

        await repository.CompleteWakeExpansionAsync(
            firstFence,
            new SharedVariableWakeFailure("transient", "transient failure"),
            CancellationToken.None);

        var retry = await db.SharedVariableWakes.AsNoTracking()
            .SingleAsync(wake => wake.Id == first.Id);
        Assert.Equal(SharedVariableWakeStatuses.Pending, retry.Status);
        Assert.Null(retry.LeaseToken);
        Assert.Null(retry.LeasedBy);
        Assert.Null(retry.LeaseExpiresAt);
        Assert.Null(retry.HeartbeatAt);
        Assert.Equal("transient failure", retry.LastError);
        Assert.True(retry.AvailableAt > DateTimeOffset.UtcNow.AddMilliseconds(-100));
        await db.SharedVariableWakes
            .Where(wake => wake.Id == retry.Id)
            .ExecuteUpdateAsync(setters => setters.SetProperty(
                wake => wake.AvailableAt,
                DateTimeOffset.UtcNow.AddSeconds(-1)));

        const string secondWorker = "retry-test-2";
        var second = Assert.Single(await repository.LeaseWakeExpansionsAsync(
            new SharedVariableWakeLeaseRequest(
                secondWorker,
                MaxCount: 10,
                TimeSpan.FromMinutes(1)),
            CancellationToken.None));
        Assert.Equal(first.Id, second.Id);
        Assert.Equal(first.LeaseGeneration + 1, second.LeaseGeneration);
        Assert.Equal(first.AttemptCount + 1, second.AttemptCount);

        await repository.CompleteWakeExpansionAsync(
            new SharedVariableWakeFence(
                SharedVariableWakeWorkKinds.Expansion,
                second.Id,
                secondWorker,
                second.LeaseToken,
                second.LeaseGeneration),
            failure: null,
            CancellationToken.None);
        var completed = await db.SharedVariableWakes.AsNoTracking()
            .SingleAsync(wake => wake.Id == first.Id);
        Assert.Equal(SharedVariableWakeStatuses.Completed, completed.Status);
        Assert.NotNull(completed.CompletedAt);
        Assert.Null(completed.HeartbeatAt);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task NullableValueBypassesNonNullRuleAndOnlyEffectiveChangesAdvanceValueRevision()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var key = $"tests.value-revision.{Guid.NewGuid():N}";
        var jsonNull = JsonSerializer.SerializeToElement<object?>(null);

        var created = await repository.CreateAsync(
            CreateCommand(
                key,
                dataType: "number",
                nullable: true,
                validation: "value > 10",
                jsonNull),
            CancellationToken.None);

        Assert.True(created.Variable.HasValue);
        Assert.Equal(JsonValueKind.Null, created.Variable.Value!.Value.ValueKind);
        Assert.Equal(created.Revision.Revision, created.Variable.ValueRevision);

        var setNumber = await repository.WriteAsync(
            WriteCommand(
                key,
                JsonSerializer.SerializeToElement(12),
                created.Variable.Revision,
                created.Variable.ValueRevision),
            CancellationToken.None);
        Assert.NotNull(setNumber);
        Assert.True(setNumber.Revision.ValueChanged);
        Assert.Equal(setNumber.Revision.Revision, setNumber.Variable.ValueRevision);

        var setNull = await repository.WriteAsync(
            WriteCommand(
                key,
                jsonNull,
                setNumber.Variable.Revision,
                setNumber.Variable.ValueRevision),
            CancellationToken.None);
        Assert.NotNull(setNull);
        Assert.True(setNull.Revision.ValueChanged);
        Assert.True(setNull.Variable.HasValue);
        Assert.Equal(JsonValueKind.Null, setNull.Variable.Value!.Value.ValueKind);
        Assert.Equal(setNull.Revision.Revision, setNull.Variable.ValueRevision);

        var identicalNull = await repository.WriteAsync(
            WriteCommand(
                key,
                jsonNull,
                setNull.Variable.Revision,
                setNull.Variable.ValueRevision),
            CancellationToken.None);
        Assert.NotNull(identicalNull);
        Assert.False(identicalNull.Revision.ValueChanged);
        Assert.True(identicalNull.Variable.Revision > setNull.Variable.Revision);
        Assert.Equal(setNull.Variable.ValueRevision, identicalNull.Variable.ValueRevision);

        var projection = await db.SharedVariableCurrentValues.AsNoTracking()
            .SingleAsync(item => item.SharedVariableId == created.Variable.Id);
        Assert.False(projection.IsDeleted);
        Assert.Equal(JsonValueKind.Null, projection.ValueJson!.RootElement.ValueKind);
        Assert.Equal(setNull.Revision.Revision, projection.Revision);
        Assert.Equal(
            setNull.Revision.Revision,
            (await repository.LoadValueStampsAsync([key], includeArchived: true, CancellationToken.None))[key]
                .ValueRevision);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task LegacyProjectionWriteAdvancesValueRevisionThroughCompatibilityTrigger()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var key = $"tests.legacy-value-revision.{Guid.NewGuid():N}";
        var created = await repository.CreateAsync(
            CreateCommand(
                key,
                "number",
                nullable: false,
                validation: null,
                JsonSerializer.SerializeToElement(1)),
            CancellationToken.None);
        var nextRevision = await db.Database.SqlQueryRaw<long>(
                "SELECT flowbit.next_shared_variable_revision() AS \"Value\"")
            .SingleAsync();
        var now = DateTimeOffset.UtcNow;
        var legacyRevision = new SharedVariableRevisionEntity
        {
            SharedVariableId = created.Variable.Id,
            Revision = nextRevision,
            Operation = SharedVariableOperations.Set,
            ValueChanged = true,
            HasValue = true,
            ValueJson = JsonDocument.Parse("2"),
            CallerKind = SharedVariableCallerKinds.System,
            CallerId = "legacy-writer",
            Source = SharedVariableSources.System,
            CreatedAt = now
        };
        db.SharedVariableRevisions.Add(legacyRevision);
        await db.SaveChangesAsync();

        // Simulate a pre-ValueRevision binary: it advances the catalog CAS and
        // authoritative current projection, but never writes ValueRevision.
        await db.SharedVariables
            .Where(variable => variable.Id == created.Variable.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(variable => variable.CurrentRevision, nextRevision)
                .SetProperty(variable => variable.UpdatedAt, now));
        using var projectedValue = JsonDocument.Parse("2");
        await db.SharedVariableCurrentValues
            .Where(value => value.SharedVariableId == created.Variable.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(value => value.SourceRevisionId, legacyRevision.Id)
                .SetProperty(value => value.Revision, nextRevision)
                .SetProperty(value => value.ValueJson, projectedValue)
                .SetProperty(value => value.IsDeleted, false)
                .SetProperty(value => value.SetAt, now));

        var reloaded = await db.SharedVariables.AsNoTracking()
            .SingleAsync(variable => variable.Id == created.Variable.Id);
        Assert.Equal(nextRevision, reloaded.CurrentRevision);
        Assert.Equal(nextRevision, reloaded.ValueRevision);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ExpiredFinalExpansionAttemptOpensExactlyOneIncidentInsteadOfOverIncrementing()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await QuarantineOpenWakesAsync(db);

        var key = $"tests.expired-expansion.{Guid.NewGuid():N}";
        var created = await repository.CreateAsync(
            CreateCommand(key, "number", nullable: false, validation: null,
                JsonSerializer.SerializeToElement(1)),
            CancellationToken.None);
        var revision = await db.SharedVariableRevisions.SingleAsync(item =>
            item.SharedVariableId == created.Variable.Id
            && item.Revision == created.Revision.Revision);
        var wake = new SharedVariableWakeEntity
        {
            SharedVariableId = created.Variable.Id,
            RevisionId = revision.Id,
            Revision = revision.Revision,
            Status = SharedVariableWakeStatuses.Leased,
            LeaseToken = Guid.NewGuid(),
            LeaseGeneration = 1,
            LeasedBy = "crashed-worker",
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            HeartbeatAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            AttemptCount = 1,
            MaxAttempts = 1,
            AvailableAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            CreatedAt = DateTimeOffset.UtcNow.AddMinutes(-2),
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-1)
        };
        db.SharedVariableWakes.Add(wake);
        await db.SaveChangesAsync();

        var leased = await repository.LeaseWakeExpansionsAsync(
            new SharedVariableWakeLeaseRequest("recovery-worker", 10, TimeSpan.FromMinutes(1)),
            CancellationToken.None);
        Assert.Empty(leased);

        var escalated = await db.SharedVariableWakes.AsNoTracking().SingleAsync(item => item.Id == wake.Id);
        Assert.Equal(SharedVariableWakeStatuses.Incident, escalated.Status);
        Assert.Equal(1, escalated.AttemptCount);
        Assert.Null(escalated.LeaseToken);
        Assert.Null(escalated.LeasedBy);
        Assert.Null(escalated.LeaseExpiresAt);
        var incident = Assert.Single(await db.SharedVariableWakeIncidents.AsNoTracking()
            .Where(item => item.WakeId == wake.Id && item.Status == SharedVariableWakeIncidentStatuses.Open)
            .ToListAsync());
        Assert.Equal("leaseExpiredAfterMaxAttempts", incident.Type);

        Assert.Empty(await repository.LeaseWakeExpansionsAsync(
            new SharedVariableWakeLeaseRequest("second-recovery-worker", 10, TimeSpan.FromMinutes(1)),
            CancellationToken.None));
        Assert.Equal(1, await db.SharedVariableWakeIncidents.CountAsync(item =>
            item.OriginalWakeId == wake.Id && item.Status == SharedVariableWakeIncidentStatuses.Open));

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ExpiredFinalDeliveryAttemptOpensExactlyOneIncidentInsteadOfOverIncrementing()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await QuarantineOpenDeliveriesAsync(db);

        var suffix = Guid.NewGuid().ToString("N");
        var key = $"tests.expired-delivery.{suffix}";
        var created = await repository.CreateAsync(
            CreateCommand(key, "number", nullable: false, validation: null,
                JsonSerializer.SerializeToElement(1)),
            CancellationToken.None);
        var revision = await db.SharedVariableRevisions.SingleAsync(item =>
            item.SharedVariableId == created.Variable.Id
            && item.Revision == created.Revision.Revision);
        var now = DateTimeOffset.UtcNow;
        var definition = new WorkflowDefinitionEntity
        {
            Name = $"delivery-{suffix}",
            WorkflowKey = $"delivery-{suffix}",
            Version = 1,
            Definition = new WorkflowModel { Id = $"delivery-{suffix}", Name = $"delivery-{suffix}" },
            CreatedAt = now
        };
        db.WorkflowDefinitions.Add(definition);
        await db.SaveChangesAsync();
        var instance = new WorkflowInstanceEntity
        {
            WorkflowDefinitionId = definition.Id,
            WorkflowKey = definition.WorkflowKey,
            Status = WorkflowInstanceStatuses.Running,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.WorkflowInstances.Add(instance);
        await db.SaveChangesAsync();
        var token = new ExecutionTokenEntity
        {
            InstanceId = instance.Id,
            NodeId = 2,
            NodeName = "Conditional wait",
            NodeType = BpmnFlowNodeTypes.IntermediateConditionalCatchEvent,
            Status = ExecutionTokenStatuses.Active,
            ActivationId = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.ExecutionTokens.Add(token);
        await db.SaveChangesAsync();
        var wake = new SharedVariableWakeEntity
        {
            SharedVariableId = created.Variable.Id,
            RevisionId = revision.Id,
            Revision = revision.Revision,
            Status = SharedVariableWakeStatuses.Completed,
            MaxAttempts = 1,
            AvailableAt = now,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now
        };
        db.SharedVariableWakes.Add(wake);
        await db.SaveChangesAsync();
        var delivery = new SharedVariableWakeDeliveryEntity
        {
            WakeId = wake.Id,
            InstanceId = instance.Id,
            WorkflowDefinitionId = definition.Id,
            TokenId = token.Id,
            ActivationId = token.ActivationId,
            NodeId = token.NodeId,
            Status = SharedVariableWakeStatuses.Leased,
            LeaseToken = Guid.NewGuid(),
            LeaseGeneration = 1,
            LeasedBy = "crashed-delivery-worker",
            LeaseExpiresAt = now.AddMinutes(-1),
            HeartbeatAt = now.AddMinutes(-1),
            AttemptCount = 1,
            MaxAttempts = 1,
            AvailableAt = now.AddMinutes(-2),
            CreatedAt = now.AddMinutes(-2),
            UpdatedAt = now.AddMinutes(-1)
        };
        db.SharedVariableWakeDeliveries.Add(delivery);
        await db.SaveChangesAsync();

        Assert.Empty(await repository.LeaseWakeDeliveriesAsync(
            new SharedVariableWakeLeaseRequest("delivery-recovery", 10, TimeSpan.FromMinutes(1)),
            CancellationToken.None));
        var escalated = await db.SharedVariableWakeDeliveries.AsNoTracking()
            .SingleAsync(item => item.Id == delivery.Id);
        Assert.Equal(SharedVariableWakeStatuses.Incident, escalated.Status);
        Assert.Equal(1, escalated.AttemptCount);
        Assert.Null(escalated.LeaseToken);
        var incident = Assert.Single(await db.SharedVariableWakeIncidents.AsNoTracking()
            .Where(item => item.DeliveryId == delivery.Id
                           && item.Status == SharedVariableWakeIncidentStatuses.Open)
            .ToListAsync());
        Assert.Equal("leaseExpiredAfterMaxAttempts", incident.Type);

        Assert.Empty(await repository.LeaseWakeDeliveriesAsync(
            new SharedVariableWakeLeaseRequest("delivery-recovery-2", 10, TimeSpan.FromMinutes(1)),
            CancellationToken.None));
        Assert.Equal(1, await db.SharedVariableWakeIncidents.CountAsync(item =>
            item.OriginalDeliveryId == delivery.Id
            && item.Status == SharedVariableWakeIncidentStatuses.Open));

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task SequenceCutoverAllowsDifferentKeyWritesWithoutSingletonLockAndSameKeyCasStillSerializes()
    {
        await using (var cutoverScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var cutoverDb = cutoverScope.ServiceProvider.GetRequiredService<AppDbContext>();
            _ = await cutoverDb.Database.SqlQueryRaw<long>(
                    "SELECT flowbit.cutover_shared_variable_revision_sequence() AS \"Value\"")
                .SingleAsync();
        }

        await using var lockScope = fixture.Factory.Services.CreateAsyncScope();
        var lockDb = lockScope.ServiceProvider.GetRequiredService<AppDbContext>();
        await using var lockTransaction = await lockDb.Database.BeginTransactionAsync();
        _ = await lockDb.Database.SqlQueryRaw<short>(
                "SELECT \"Id\" AS \"Value\" FROM flowbit.shared_variable_revision_state WHERE \"Id\" = 1 FOR UPDATE")
            .SingleAsync();

        var suffix = Guid.NewGuid().ToString("N");
        var firstCreateTask = CreateInIndependentScopeAsync($"tests.sequence-a.{suffix}");
        var secondCreateTask = CreateInIndependentScopeAsync($"tests.sequence-b.{suffix}");
        var created = await Task.WhenAll(firstCreateTask, secondCreateTask)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.NotEqual(created[0].Revision.Revision, created[1].Revision.Revision);
        await lockTransaction.RollbackAsync();

        var casKey = $"tests.sequence-cas.{suffix}";
        var initial = await CreateInIndependentScopeAsync(casKey);
        var firstWrite = WriteInIndependentScopeAsync(casKey, 2, initial.Variable);
        var secondWrite = WriteInIndependentScopeAsync(casKey, 3, initial.Variable);
        var outcomes = await Task.WhenAll(firstWrite, secondWrite)
            .WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(outcomes, outcome => outcome.Result is not null);
        Assert.Single(outcomes, outcome => outcome.Error is WorkflowConflictException);

        await using var verifyScope = fixture.Factory.Services.CreateAsyncScope();
        var verifyDb = verifyScope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(
            "sequence",
            await verifyDb.SharedVariableRevisionStates
                .Where(item => item.Id == 1)
                .Select(item => item.AllocatorMode)
                .SingleAsync());

        async Task<SharedVariableMutationResult> CreateInIndependentScopeAsync(string key)
        {
            await using var createScope = fixture.Factory.Services.CreateAsyncScope();
            var repository = createScope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
            return await repository.CreateAsync(
                CreateCommand(key, "number", nullable: false, validation: null,
                    JsonSerializer.SerializeToElement(1)),
                CancellationToken.None);
        }

        async Task<(SharedVariableMutationResult? Result, Exception? Error)> WriteInIndependentScopeAsync(
            string key,
            int value,
            SharedVariableRecord expected)
        {
            try
            {
                await using var writeScope = fixture.Factory.Services.CreateAsyncScope();
                var repository = writeScope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
                var result = await repository.WriteAsync(
                    WriteCommand(
                        key,
                        JsonSerializer.SerializeToElement(value),
                        expected.Revision,
                        expected.ValueRevision),
                    CancellationToken.None);
                return (result, null);
            }
            catch (Exception exception)
            {
                return (null, exception);
            }
        }
    }

    [Fact]
    public async Task EffectiveWriteUsesDatabaseClockForInitialWakeAndNotifiesAfterCommit()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var key = $"tests.wake-clock.{suffix}";
        SharedVariableMutationResult created;
        await using (var setupScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var repository = setupScope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
            created = await repository.CreateAsync(
                CreateCommand(key, "number", nullable: false, validation: null,
                    JsonSerializer.SerializeToElement(1)),
                CancellationToken.None);
            var definition = new WorkflowDefinitionEntity
            {
                Name = $"wake-clock-{suffix}",
                WorkflowKey = $"wake-clock-{suffix}",
                Version = 1,
                Definition = new WorkflowModel { Id = $"wake-clock-{suffix}", Name = $"wake-clock-{suffix}" },
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.WorkflowDefinitions.Add(definition);
            await db.SaveChangesAsync();
            db.WorkflowDefinitionSharedVariableDependencies.Add(
                new WorkflowDefinitionSharedVariableDependencyEntity
                {
                    WorkflowDefinitionId = definition.Id,
                    SharedVariableId = created.Variable.Id,
                    SharedKey = key,
                    NodeId = 2,
                    NodeExternalId = "conditional-wait",
                    Kind = SharedVariableDependencyKinds.ConditionalCatch,
                    CreatedAt = DateTimeOffset.UtcNow
                });
            await db.SaveChangesAsync();
        }

        await using var listener = await fixture.DataSource.OpenConnectionAsync();
        var notification = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        listener.Notification += (_, args) => notification.TrySetResult(args.Payload);
        await using (var listen = new NpgsqlCommand("LISTEN flowbit_jobs", listener))
            await listen.ExecuteNonQueryAsync();
        using var waitCancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var waitForNotification = listener.WaitAsync(waitCancellation.Token);

        DateTimeOffset before;
        DateTimeOffset after;
        await using (var writeScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = writeScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var repository = writeScope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
            before = await db.Database.SqlQueryRaw<DateTimeOffset>(
                    "SELECT clock_timestamp() AS \"Value\"")
                .SingleAsync();
            var result = await repository.WriteAsync(
                WriteCommand(
                    key,
                    JsonSerializer.SerializeToElement(2),
                    created.Variable.Revision,
                    created.Variable.ValueRevision),
                CancellationToken.None);
            Assert.NotNull(result);
            after = await db.Database.SqlQueryRaw<DateTimeOffset>(
                    "SELECT clock_timestamp() AS \"Value\"")
                .SingleAsync();
        }

        await waitForNotification;
        Assert.Equal("shared-variable", await notification.Task.WaitAsync(TimeSpan.FromSeconds(1)));
        await using var verify = fixture.CreateDbContext();
        var wake = await verify.SharedVariableWakes.AsNoTracking()
            .SingleAsync(item => item.SharedVariableId == created.Variable.Id);
        Assert.InRange(wake.AvailableAt, before, after);
        Assert.Equal(wake.AvailableAt, wake.CreatedAt);
        Assert.Equal(wake.AvailableAt, wake.UpdatedAt);
    }

    [Fact]
    public async Task CompatibilityInsertTriggerDatabaseStampsOnlyInitialLegacyWakes()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
        await using var transaction = await db.Database.BeginTransactionAsync();

        var suffix = Guid.NewGuid().ToString("N");
        async Task<(SharedVariableMutationResult Variable, SharedVariableRevisionEntity Revision)> CreateSeedAsync(
            string label)
        {
            var created = await repository.CreateAsync(
                CreateCommand(
                    $"tests.legacy-wake-clock.{label}.{suffix}",
                    "number",
                    nullable: false,
                    validation: null,
                    JsonSerializer.SerializeToElement(1)),
                CancellationToken.None);
            var revision = await db.SharedVariableRevisions.SingleAsync(item =>
                item.SharedVariableId == created.Variable.Id
                && item.Revision == created.Revision.Revision);
            return (created, revision);
        }

        var plusHour = await CreateSeedAsync("plus-hour");
        var minusHour = await CreateSeedAsync("minus-hour");
        var nonInitial = await CreateSeedAsync("non-initial");
        var before = await db.Database.SqlQueryRaw<DateTimeOffset>(
                "SELECT clock_timestamp() AS \"Value\"")
            .SingleAsync();
        var plusHourSkew = before.AddHours(1);
        var minusHourSkew = before.AddHours(-1);
        var preservedSchedule = before.AddMinutes(10);

        db.SharedVariableWakes.AddRange(
            new SharedVariableWakeEntity
            {
                SharedVariableId = plusHour.Variable.Variable.Id,
                RevisionId = plusHour.Revision.Id,
                Revision = plusHour.Revision.Revision,
                Status = SharedVariableWakeStatuses.Pending,
                AttemptCount = 0,
                LeaseGeneration = 0,
                MaxAttempts = 25,
                AvailableAt = plusHourSkew,
                CreatedAt = plusHourSkew,
                UpdatedAt = plusHourSkew
            },
            new SharedVariableWakeEntity
            {
                SharedVariableId = minusHour.Variable.Variable.Id,
                RevisionId = minusHour.Revision.Id,
                Revision = minusHour.Revision.Revision,
                Status = SharedVariableWakeStatuses.Pending,
                AttemptCount = 0,
                LeaseGeneration = 0,
                MaxAttempts = 25,
                AvailableAt = minusHourSkew,
                CreatedAt = minusHourSkew,
                UpdatedAt = minusHourSkew
            },
            new SharedVariableWakeEntity
            {
                SharedVariableId = nonInitial.Variable.Variable.Id,
                RevisionId = nonInitial.Revision.Id,
                Revision = nonInitial.Revision.Revision,
                Status = SharedVariableWakeStatuses.Pending,
                AttemptCount = 1,
                LeaseGeneration = 1,
                MaxAttempts = 25,
                AvailableAt = preservedSchedule,
                CreatedAt = preservedSchedule,
                UpdatedAt = preservedSchedule
            });
        await db.SaveChangesAsync();
        var after = await db.Database.SqlQueryRaw<DateTimeOffset>(
                "SELECT clock_timestamp() AS \"Value\"")
            .SingleAsync();
        db.ChangeTracker.Clear();

        var wakes = await db.SharedVariableWakes.AsNoTracking()
            .Where(item => item.SharedVariableId == plusHour.Variable.Variable.Id
                           || item.SharedVariableId == minusHour.Variable.Variable.Id
                           || item.SharedVariableId == nonInitial.Variable.Variable.Id)
            .ToDictionaryAsync(item => item.SharedVariableId);
        foreach (var variableId in new[]
                 {
                     plusHour.Variable.Variable.Id,
                     minusHour.Variable.Variable.Id
                 })
        {
            var wake = wakes[variableId];
            Assert.InRange(wake.AvailableAt, before, after);
            Assert.Equal(wake.AvailableAt, wake.CreatedAt);
            Assert.Equal(wake.AvailableAt, wake.UpdatedAt);
        }

        var preserved = wakes[nonInitial.Variable.Variable.Id];
        Assert.Equal(preservedSchedule, preserved.AvailableAt);
        Assert.Equal(preservedSchedule, preserved.CreatedAt);
        Assert.Equal(preservedSchedule, preserved.UpdatedAt);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task ConcurrentRepositoriesLeaseOnlyOneOwnerForWakeGeneration()
    {
        long wakeId;
        await using (var setupScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var repository = setupScope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
            await QuarantineOpenWakesAsync(db);
            var seeded = await AddPendingWakeAsync(
                db,
                repository,
                $"tests.concurrent-wake-lease.{Guid.NewGuid():N}");
            wakeId = seeded.Wake.Id;
        }

        async Task<IReadOnlyList<SharedVariableWakeRecord>> LeaseAsync(string workerId)
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
            return await repository.LeaseWakeExpansionsAsync(
                new SharedVariableWakeLeaseRequest(
                    workerId,
                    MaxCount: 1,
                    TimeSpan.FromMinutes(1)),
                CancellationToken.None);
        }

        var acquisitions = await Task.WhenAll(
            LeaseAsync("generation-owner-a"),
            LeaseAsync("generation-owner-b"));
        var lease = Assert.Single(acquisitions.SelectMany(items => items));
        Assert.Equal(wakeId, lease.Id);

        string leasedBy;
        await using (var verify = fixture.CreateDbContext())
        {
            var persisted = await verify.SharedVariableWakes.AsNoTracking()
                .SingleAsync(item => item.Id == wakeId);
            Assert.Equal(SharedVariableWakeStatuses.Leased, persisted.Status);
            Assert.Equal(1, persisted.LeaseGeneration);
            Assert.Equal(1, persisted.AttemptCount);
            Assert.Equal(lease.LeaseToken, persisted.LeaseToken);
            Assert.Equal(lease.LeaseGeneration, persisted.LeaseGeneration);
            Assert.Equal(lease.Status, persisted.Status);
            leasedBy = Assert.IsType<string>(persisted.LeasedBy);
        }

        await using var completionScope = fixture.Factory.Services.CreateAsyncScope();
        var completionRepository = completionScope.ServiceProvider
            .GetRequiredService<ISharedVariableRepository>();
        var completion = await completionRepository.CompleteWakeExpansionAsync(
            ExpansionFence(lease, leasedBy),
            failure: null,
            CancellationToken.None);
        Assert.Equal(SharedVariableWakeFinalizationDispositions.Completed, completion.Disposition);
    }

    [Fact]
    public async Task HeartbeatPreventsStealAndReplacedGenerationRejectsStaleFinalization()
    {
        long wakeId;
        await using (var setupScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var repository = setupScope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
            await QuarantineOpenWakesAsync(db);
            var seeded = await AddPendingWakeAsync(
                db,
                repository,
                $"tests.wake-heartbeat-fence.{Guid.NewGuid():N}");
            wakeId = seeded.Wake.Id;
        }

        SharedVariableWakeRecord firstLease;
        await using (var firstScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var repository = firstScope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
            firstLease = Assert.Single(await repository.LeaseWakeExpansionsAsync(
                new SharedVariableWakeLeaseRequest(
                    "heartbeat-owner",
                    MaxCount: 1,
                    TimeSpan.FromSeconds(15)),
                CancellationToken.None));
            Assert.Equal(wakeId, firstLease.Id);
            Assert.True(await repository.HeartbeatWakeAsync(
                ExpansionFence(firstLease, "heartbeat-owner"),
                TimeSpan.FromMinutes(30),
                CancellationToken.None));
        }

        await using (var verifyHeartbeat = fixture.CreateDbContext())
        {
            var renewed = await verifyHeartbeat.SharedVariableWakes.AsNoTracking()
                .SingleAsync(item => item.Id == wakeId);
            Assert.NotNull(renewed.HeartbeatAt);
            Assert.True(renewed.LeaseExpiresAt > firstLease.LeaseExpiresAt);
            Assert.InRange(
                (renewed.UpdatedAt - renewed.HeartbeatAt!.Value).Duration(),
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(10));
        }

        await using (var competingScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var repository = competingScope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
            Assert.Empty(await repository.LeaseWakeExpansionsAsync(
                new SharedVariableWakeLeaseRequest(
                    "premature-stealer",
                    MaxCount: 1,
                    TimeSpan.FromMinutes(1)),
                CancellationToken.None));
        }

        await using (var expire = fixture.CreateDbContext())
        {
            await expire.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE flowbit.shared_variable_wakes
                SET "LeaseExpiresAt" = clock_timestamp() - interval '1 second'
                WHERE "Id" = {wakeId}
                """);
        }

        SharedVariableWakeRecord replacement;
        await using (var replacementScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var repository = replacementScope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
            replacement = Assert.Single(await repository.LeaseWakeExpansionsAsync(
                new SharedVariableWakeLeaseRequest(
                    "replacement-owner",
                    MaxCount: 1,
                    TimeSpan.FromMinutes(1)),
                CancellationToken.None));
        }
        Assert.Equal(wakeId, replacement.Id);
        Assert.Equal(firstLease.LeaseGeneration + 1, replacement.LeaseGeneration);
        Assert.NotEqual(firstLease.LeaseToken, replacement.LeaseToken);

        await using (var staleScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var repository = staleScope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
            var stale = await repository.CompleteWakeExpansionAsync(
                ExpansionFence(firstLease, "heartbeat-owner"),
                failure: null,
                CancellationToken.None);
            Assert.Equal(SharedVariableWakeFinalizationDispositions.LeaseLost, stale.Disposition);
        }

        await using var completionScope = fixture.Factory.Services.CreateAsyncScope();
        var completionRepository = completionScope.ServiceProvider
            .GetRequiredService<ISharedVariableRepository>();
        var completed = await completionRepository.CompleteWakeExpansionAsync(
            ExpansionFence(replacement, "replacement-owner"),
            failure: null,
            CancellationToken.None);
        Assert.Equal(SharedVariableWakeFinalizationDispositions.Completed, completed.Disposition);
    }

    [Fact]
    public async Task ExpansionPagingRejectsFenceThatExpiresWhileWaitingForRowLock()
    {
        SharedVariableWakeRecord lease;
        await using (var setupScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var repository = setupScope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
            await QuarantineOpenWakesAsync(db);
            var seeded = await AddPendingWakeAsync(
                db,
                repository,
                $"tests.blocked-expansion-page.{Guid.NewGuid():N}");
            lease = Assert.Single(await repository.LeaseWakeExpansionsAsync(
                new SharedVariableWakeLeaseRequest(
                    "blocked-page-owner",
                    MaxCount: 1,
                    TimeSpan.FromMinutes(1)),
                CancellationToken.None));
            Assert.Equal(seeded.Wake.Id, lease.Id);
        }

        var result = await RunBlockedPastLeaseExpiryAsync(
            "shared_variable_wakes",
            lease.Id,
            async () =>
            {
                await using var scope = fixture.Factory.Services.CreateAsyncScope();
                var repository = scope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
                return await repository.ExpandWakePageAsync(
                    ExpansionFence(lease, "blocked-page-owner"),
                    pageSize: 1,
                    CancellationToken.None);
            });
        Assert.Equal(
            SharedVariableWakeExpansionPageDispositions.LeaseLost,
            result.Disposition);

        await using var cleanup = fixture.CreateDbContext();
        await QuarantineOpenWakesAsync(cleanup);
    }

    [Fact]
    public async Task ExpansionFinalizerRejectsFenceThatExpiresWhileWaitingForRowLock()
    {
        SharedVariableWakeRecord lease;
        await using (var setupScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var repository = setupScope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
            await QuarantineOpenWakesAsync(db);
            var seeded = await AddPendingWakeAsync(
                db,
                repository,
                $"tests.blocked-expansion-finalize.{Guid.NewGuid():N}");
            lease = Assert.Single(await repository.LeaseWakeExpansionsAsync(
                new SharedVariableWakeLeaseRequest(
                    "blocked-expansion-finalizer",
                    MaxCount: 1,
                    TimeSpan.FromMinutes(1)),
                CancellationToken.None));
            Assert.Equal(seeded.Wake.Id, lease.Id);
        }

        var result = await RunBlockedPastLeaseExpiryAsync(
            "shared_variable_wakes",
            lease.Id,
            async () =>
            {
                await using var scope = fixture.Factory.Services.CreateAsyncScope();
                var repository = scope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
                return await repository.CompleteWakeExpansionAsync(
                    ExpansionFence(lease, "blocked-expansion-finalizer"),
                    failure: null,
                    CancellationToken.None);
            });
        Assert.Equal(SharedVariableWakeFinalizationDispositions.LeaseLost, result.Disposition);

        await using var cleanup = fixture.CreateDbContext();
        await QuarantineOpenWakesAsync(cleanup);
    }

    [Fact]
    public async Task DeliveryFinalizerRejectsFenceThatExpiresWhileWaitingForRowLock()
    {
        SharedVariableWakeDeliveryRecord lease;
        await using (var setupScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var repository = setupScope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
            await QuarantineOpenWakesAsync(db);
            await QuarantineOpenDeliveriesAsync(db);
            var suffix = Guid.NewGuid().ToString("N");
            var key = $"tests.blocked-delivery-finalize.{suffix}";
            var seeded = await AddPendingWakeAsync(db, repository, key);
            var runtime = await AddConditionalWaitAsync(
                db,
                seeded.Created.Variable.Id,
                key,
                suffix);
            var now = DateTimeOffset.UtcNow;
            var delivery = new SharedVariableWakeDeliveryEntity
            {
                WakeId = seeded.Wake.Id,
                InstanceId = runtime.Instance.Id,
                WorkflowDefinitionId = runtime.Definition.Id,
                TokenId = runtime.Token.Id,
                ActivationId = runtime.Token.ActivationId,
                NodeId = runtime.Token.NodeId,
                Status = SharedVariableWakeStatuses.Pending,
                MaxAttempts = 25,
                AvailableAt = now,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.SharedVariableWakeDeliveries.Add(delivery);
            await db.SaveChangesAsync();
            lease = Assert.Single(await repository.LeaseWakeDeliveriesAsync(
                new SharedVariableWakeLeaseRequest(
                    "blocked-delivery-finalizer",
                    MaxCount: 1,
                    TimeSpan.FromMinutes(1)),
                CancellationToken.None));
            Assert.Equal(delivery.Id, lease.Id);
        }

        var fence = new SharedVariableWakeFence(
            SharedVariableWakeWorkKinds.Delivery,
            lease.Id,
            "blocked-delivery-finalizer",
            lease.LeaseToken,
            lease.LeaseGeneration);
        var result = await RunBlockedPastLeaseExpiryAsync(
            "shared_variable_wake_deliveries",
            lease.Id,
            async () =>
            {
                await using var scope = fixture.Factory.Services.CreateAsyncScope();
                var repository = scope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
                return await repository.CompleteWakeDeliveryAsync(
                    fence,
                    failure: null,
                    CancellationToken.None);
            });
        Assert.Equal(SharedVariableWakeFinalizationDispositions.LeaseLost, result.Disposition);

        await using var cleanup = fixture.CreateDbContext();
        await QuarantineOpenDeliveriesAsync(cleanup);
        await QuarantineOpenWakesAsync(cleanup);
    }

    [Fact]
    public async Task ExpansionOverFiveHundredCandidatesResumesWithoutDuplicateDeliveries()
    {
        const int candidateCount = 501;
        long wakeId;
        long[] expectedTokenIds;
        await using (var setupScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var repository = setupScope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
            await QuarantineOpenWakesAsync(db);
            var suffix = Guid.NewGuid().ToString("N");
            var key = $"tests.wake-paging.{suffix}";
            var seeded = await AddPendingWakeAsync(db, repository, key);
            wakeId = seeded.Wake.Id;

            var now = DateTimeOffset.UtcNow;
            var definition = new WorkflowDefinitionEntity
            {
                Name = $"wake-paging-{suffix}",
                WorkflowKey = $"wake-paging-{suffix}",
                Version = 1,
                Definition = new WorkflowModel
                {
                    Id = $"wake-paging-{suffix}",
                    Name = $"wake-paging-{suffix}"
                },
                CreatedAt = now
            };
            db.WorkflowDefinitions.Add(definition);
            await db.SaveChangesAsync();
            db.WorkflowDefinitionSharedVariableDependencies.Add(
                new WorkflowDefinitionSharedVariableDependencyEntity
                {
                    WorkflowDefinitionId = definition.Id,
                    SharedVariableId = seeded.Created.Variable.Id,
                    SharedKey = key,
                    NodeId = 2,
                    NodeExternalId = "conditional-wait",
                    Kind = SharedVariableDependencyKinds.ConditionalCatch,
                    CreatedAt = now
                });
            var instances = Enumerable.Range(0, candidateCount)
                .Select(index => new WorkflowInstanceEntity
                {
                    WorkflowDefinitionId = definition.Id,
                    WorkflowKey = definition.WorkflowKey,
                    Status = WorkflowInstanceStatuses.Running,
                    CreatedAt = now,
                    UpdatedAt = now
                })
                .ToArray();
            db.WorkflowInstances.AddRange(instances);
            await db.SaveChangesAsync();
            var tokens = instances.Select(instance => new ExecutionTokenEntity
                {
                    InstanceId = instance.Id,
                    NodeId = 2,
                    NodeName = "Conditional wait",
                    NodeType = BpmnFlowNodeTypes.IntermediateConditionalCatchEvent,
                    Status = ExecutionTokenStatuses.Active,
                    ActivationId = Guid.NewGuid(),
                    CreatedAt = now,
                    UpdatedAt = now
                })
                .ToArray();
            db.ExecutionTokens.AddRange(tokens);
            await db.SaveChangesAsync();
            expectedTokenIds = tokens.Select(item => item.Id).Order().ToArray();
        }

        SharedVariableWakeRecord lease;
        await using (var leaseScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var repository = leaseScope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
            lease = Assert.Single(await repository.LeaseWakeExpansionsAsync(
                new SharedVariableWakeLeaseRequest(
                    "paged-expander",
                    MaxCount: 1,
                    TimeSpan.FromMinutes(5)),
                CancellationToken.None));
        }
        Assert.Equal(wakeId, lease.Id);
        var fence = ExpansionFence(lease, "paged-expander");

        SharedVariableWakeExpansionPageResult firstPage;
        await using (var firstPageScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var repository = firstPageScope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
            firstPage = Assert.IsType<SharedVariableWakeExpansionPageResult>(
                await repository.ExpandWakePageAsync(fence, 500, CancellationToken.None));
        }
        Assert.False(firstPage.IsComplete);
        Assert.Equal(500, firstPage.CreatedCount);

        SharedVariableWakeExpansionPageResult secondPage;
        await using (var secondPageScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var repository = secondPageScope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
            secondPage = Assert.IsType<SharedVariableWakeExpansionPageResult>(
                await repository.ExpandWakePageAsync(fence, 500, CancellationToken.None));
        }
        Assert.True(secondPage.IsComplete);
        Assert.Equal(1, secondPage.CreatedCount);
        Assert.True(secondPage.CursorTokenId > firstPage.CursorTokenId);

        await using (var verify = fixture.CreateDbContext())
        {
            var deliveries = await verify.SharedVariableWakeDeliveries.AsNoTracking()
                .Where(item => item.WakeId == wakeId)
                .OrderBy(item => item.TokenId)
                .Select(item => new { item.TokenId, item.ActivationId })
                .ToArrayAsync();
            Assert.Equal(candidateCount, deliveries.Length);
            Assert.Equal(candidateCount, deliveries.Distinct().Count());
            Assert.Equal(expectedTokenIds, deliveries.Select(item => item.TokenId).ToArray());
        }

        await using var completionScope = fixture.Factory.Services.CreateAsyncScope();
        var completionRepository = completionScope.ServiceProvider
            .GetRequiredService<ISharedVariableRepository>();
        var completed = await completionRepository.CompleteWakeExpansionAsync(
            fence,
            failure: null,
            CancellationToken.None);
        Assert.Equal(SharedVariableWakeFinalizationDispositions.Completed, completed.Disposition);
    }

    [Fact]
    public async Task ConcurrentIncidentRetryAndResolveHasOneFencedAuditedWinner()
    {
        const string originalDetails = "Original expansion failure diagnostics.";
        const string resolutionReason = "Operator confirmed that retry is unsafe.";
        long incidentId;
        long wakeId;
        await using (var setupScope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = setupScope.ServiceProvider.GetRequiredService<AppDbContext>();
            var repository = setupScope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
            await QuarantineOpenWakesAsync(db);
            var key = $"tests.incident-mutation-race.{Guid.NewGuid():N}";
            var seeded = await AddPendingWakeAsync(db, repository, key);
            var now = DateTimeOffset.UtcNow;
            seeded.Wake.Status = SharedVariableWakeStatuses.Incident;
            seeded.Wake.AttemptCount = seeded.Wake.MaxAttempts;
            seeded.Wake.LastError = originalDetails;
            seeded.Wake.UpdatedAt = now;
            var incident = new SharedVariableWakeIncidentEntity
            {
                WorkKind = SharedVariableWakeWorkKinds.Expansion,
                WakeId = seeded.Wake.Id,
                OriginalWakeId = seeded.Wake.Id,
                SharedVariableId = seeded.Created.Variable.Id,
                SharedKey = seeded.Created.Variable.Key,
                Revision = seeded.Revision.Revision,
                Type = "concurrentRecovery",
                Status = SharedVariableWakeIncidentStatuses.Open,
                Summary = "Expansion requires one operator decision.",
                Details = originalDetails,
                CreatedAt = now,
                UpdatedAt = now
            };
            db.SharedVariableWakeIncidents.Add(incident);
            await db.SaveChangesAsync();
            incidentId = incident.Id;
            wakeId = seeded.Wake.Id;
        }

        async Task<(SharedVariableWakeIncidentRecord? Result, Exception? Error)> RetryAsync()
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
            try
            {
                return (await repository.RetryWakeIncidentAsync(
                    incidentId,
                    "retry-admin",
                    CancellationToken.None), null);
            }
            catch (Exception exception)
            {
                return (null, exception);
            }
        }

        async Task<(SharedVariableWakeIncidentRecord? Result, Exception? Error)> ResolveAsync()
        {
            await using var scope = fixture.Factory.Services.CreateAsyncScope();
            var repository = scope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
            try
            {
                return (await repository.ResolveWakeIncidentAsync(
                    incidentId,
                    "resolve-admin",
                    resolutionReason,
                    CancellationToken.None), null);
            }
            catch (Exception exception)
            {
                return (null, exception);
            }
        }

        var outcomes = await Task.WhenAll(RetryAsync(), ResolveAsync());
        var winner = Assert.Single(outcomes, item => item.Error is null);
        Assert.NotNull(winner.Result);
        var loser = Assert.Single(outcomes, item => item.Error is not null);
        Assert.IsType<WorkflowConflictException>(loser.Error);

        await using var verify = fixture.CreateDbContext();
        var incidentState = await verify.SharedVariableWakeIncidents.AsNoTracking()
            .SingleAsync(item => item.Id == incidentId);
        var wakeState = await verify.SharedVariableWakes
            .SingleAsync(item => item.Id == wakeId);
        Assert.Equal(SharedVariableWakeIncidentStatuses.Resolved, incidentState.Status);
        Assert.Equal(originalDetails, incidentState.Details);
        Assert.NotNull(incidentState.ResolvedAt);
        if (incidentState.ResolvedBy == "retry-admin")
        {
            Assert.Equal("retryRequested", incidentState.ResolutionReason);
            Assert.Equal(SharedVariableWakeStatuses.Pending, wakeState.Status);
            Assert.Null(wakeState.CompletedAt);
        }
        else
        {
            Assert.Equal("resolve-admin", incidentState.ResolvedBy);
            Assert.Equal(resolutionReason, incidentState.ResolutionReason);
            Assert.Equal(SharedVariableWakeStatuses.Cancelled, wakeState.Status);
            Assert.Equal(resolutionReason, wakeState.LastError);
            Assert.NotNull(wakeState.CompletedAt);
        }
        Assert.Equal(1, await verify.SharedVariableWakeIncidents.CountAsync(item => item.Id == incidentId));

        if (wakeState.Status == SharedVariableWakeStatuses.Pending)
        {
            wakeState.Status = SharedVariableWakeStatuses.Cancelled;
            wakeState.CompletedAt = DateTimeOffset.UtcNow;
            wakeState.UpdatedAt = wakeState.CompletedAt.Value;
            await verify.SaveChangesAsync();
        }
    }

    [Fact]
    public async Task ResolvedIncidentRetainsOriginalWorkIdsAfterReferencedRowsAreCleaned()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await QuarantineOpenWakesAsync(db);
        await QuarantineOpenDeliveriesAsync(db);

        var suffix = Guid.NewGuid().ToString("N");
        var key = $"tests.incident-retention.{suffix}";
        var created = await repository.CreateAsync(
            CreateCommand(key, "number", nullable: false, validation: null,
                JsonSerializer.SerializeToElement(1)),
            CancellationToken.None);
        var revision = await db.SharedVariableRevisions.SingleAsync(item =>
            item.SharedVariableId == created.Variable.Id
            && item.Revision == created.Revision.Revision);
        var runtime = await AddConditionalWaitAsync(db, created.Variable.Id, key, suffix);
        var completedAt = DateTimeOffset.UtcNow.AddYears(-50);
        var wake = new SharedVariableWakeEntity
        {
            SharedVariableId = created.Variable.Id,
            RevisionId = revision.Id,
            Revision = revision.Revision,
            Status = SharedVariableWakeStatuses.Completed,
            MaxAttempts = 25,
            AvailableAt = completedAt,
            CreatedAt = completedAt,
            UpdatedAt = completedAt,
            CompletedAt = completedAt
        };
        db.SharedVariableWakes.Add(wake);
        await db.SaveChangesAsync();
        var delivery = new SharedVariableWakeDeliveryEntity
        {
            WakeId = wake.Id,
            InstanceId = runtime.Instance.Id,
            WorkflowDefinitionId = runtime.Definition.Id,
            TokenId = runtime.Token.Id,
            ActivationId = runtime.Token.ActivationId,
            NodeId = runtime.Token.NodeId,
            Status = SharedVariableWakeStatuses.Cancelled,
            MaxAttempts = 25,
            AvailableAt = completedAt,
            CreatedAt = completedAt,
            UpdatedAt = completedAt,
            CompletedAt = completedAt
        };
        db.SharedVariableWakeDeliveries.Add(delivery);
        await db.SaveChangesAsync();
        var resolvedAt = DateTimeOffset.UtcNow;
        var incident = new SharedVariableWakeIncidentEntity
        {
            WorkKind = SharedVariableWakeWorkKinds.Delivery,
            WakeId = wake.Id,
            DeliveryId = delivery.Id,
            OriginalWakeId = wake.Id,
            OriginalDeliveryId = delivery.Id,
            SharedVariableId = created.Variable.Id,
            SharedKey = key,
            Revision = revision.Revision,
            InstanceId = runtime.Instance.Id,
            WorkflowDefinitionId = runtime.Definition.Id,
            TokenId = runtime.Token.Id,
            ActivationId = runtime.Token.ActivationId,
            NodeId = runtime.Token.NodeId,
            Type = "retentionRegression",
            Status = SharedVariableWakeIncidentStatuses.Resolved,
            Summary = "Resolved incident retained for operator audit.",
            Details = "Original failure detail.",
            ResolutionReason = "Operator confirmed no retry was needed.",
            ResolvedBy = "retention-admin",
            CreatedAt = resolvedAt,
            UpdatedAt = resolvedAt,
            ResolvedAt = resolvedAt
        };
        db.SharedVariableWakeIncidents.Add(incident);
        await db.SaveChangesAsync();
        var originalWakeId = wake.Id;
        var originalDeliveryId = delivery.Id;

        var cleaned = await repository.CleanupWakeOutboxAsync(
            completedAt.AddSeconds(1),
            DateTimeOffset.MinValue,
            batchSize: 20,
            CancellationToken.None);

        Assert.Equal(1, cleaned.DeliveriesDeleted);
        Assert.Equal(1, cleaned.WakesDeleted);
        Assert.Equal(0, cleaned.IncidentsDeleted);
        Assert.False(await db.SharedVariableWakes.AnyAsync(item => item.Id == originalWakeId));
        Assert.False(await db.SharedVariableWakeDeliveries.AnyAsync(item => item.Id == originalDeliveryId));
        var detail = await repository.GetWakeIncidentAsync(incident.Id, CancellationToken.None);
        Assert.NotNull(detail);
        Assert.Null(detail.WakeId);
        Assert.Null(detail.DeliveryId);
        Assert.Equal(originalWakeId, detail.OriginalWakeId);
        Assert.Equal(originalDeliveryId, detail.OriginalDeliveryId);
        Assert.Equal("Original failure detail.", detail.Details);
        Assert.Equal("Operator confirmed no retry was needed.", detail.ResolutionReason);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task RetriedExpansionCreatesDeliveriesWithIndependentDefaultAttemptBudget()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await QuarantineOpenWakesAsync(db);
        await QuarantineOpenDeliveriesAsync(db);

        var suffix = Guid.NewGuid().ToString("N");
        var key = $"tests.expansion-budget.{suffix}";
        var created = await repository.CreateAsync(
            CreateCommand(key, "number", nullable: false, validation: null,
                JsonSerializer.SerializeToElement(1)),
            CancellationToken.None);
        var revision = await db.SharedVariableRevisions.SingleAsync(item =>
            item.SharedVariableId == created.Variable.Id
            && item.Revision == created.Revision.Revision);
        _ = await AddConditionalWaitAsync(db, created.Variable.Id, key, suffix);
        const string firstWorker = "expansion-budget-first";
        var wake = new SharedVariableWakeEntity
        {
            SharedVariableId = created.Variable.Id,
            RevisionId = revision.Id,
            Revision = revision.Revision,
            Status = SharedVariableWakeStatuses.Leased,
            LeaseToken = Guid.NewGuid(),
            LeaseGeneration = 1,
            LeasedBy = firstWorker,
            LeaseExpiresAt = DateTimeOffset.UtcNow.AddMinutes(5),
            HeartbeatAt = DateTimeOffset.UtcNow,
            AttemptCount = 25,
            MaxAttempts = 25,
            AvailableAt = DateTimeOffset.UtcNow,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.SharedVariableWakes.Add(wake);
        await db.SaveChangesAsync();

        var exhausted = await repository.CompleteWakeExpansionAsync(
            new SharedVariableWakeFence(
                SharedVariableWakeWorkKinds.Expansion,
                wake.Id,
                firstWorker,
                wake.LeaseToken.Value,
                wake.LeaseGeneration),
            new SharedVariableWakeFailure("syntheticFailure", "Expansion attempt 25 failed."),
            CancellationToken.None);
        Assert.Equal(SharedVariableWakeFinalizationDispositions.IncidentOpened, exhausted.Disposition);
        Assert.NotNull(exhausted.IncidentId);

        _ = await repository.RetryWakeIncidentAsync(
            exhausted.IncidentId.Value,
            "retry-admin",
            CancellationToken.None);
        const string retryWorker = "expansion-budget-retry";
        var retried = Assert.Single(await repository.LeaseWakeExpansionsAsync(
            new SharedVariableWakeLeaseRequest(retryWorker, 10, TimeSpan.FromMinutes(1)),
            CancellationToken.None));
        Assert.Equal(26, retried.AttemptCount);
        Assert.Equal(26, retried.MaxAttempts);
        var retryFence = new SharedVariableWakeFence(
            SharedVariableWakeWorkKinds.Expansion,
            retried.Id,
            retryWorker,
            retried.LeaseToken,
            retried.LeaseGeneration);
        var page = await repository.ExpandWakePageAsync(
            retryFence,
            pageSize: 50,
            CancellationToken.None);
        Assert.Equal(
            SharedVariableWakeExpansionPageDispositions.Page,
            page.Disposition);
        Assert.Equal(1, page.CreatedCount);
        var delivery = await db.SharedVariableWakeDeliveries.AsNoTracking()
            .SingleAsync(item => item.WakeId == wake.Id);
        Assert.Equal(0, delivery.AttemptCount);
        Assert.Equal(25, delivery.MaxAttempts);

        _ = await repository.CompleteWakeExpansionAsync(
            retryFence,
            failure: null,
            CancellationToken.None);
        const string deliveryWorker = "expansion-budget-delivery";
        var leasedDelivery = Assert.Single(await repository.LeaseWakeDeliveriesAsync(
            new SharedVariableWakeLeaseRequest(deliveryWorker, 10, TimeSpan.FromMinutes(1)),
            CancellationToken.None));
        var deliveryFence = new SharedVariableWakeFence(
            SharedVariableWakeWorkKinds.Delivery,
            leasedDelivery.Id,
            deliveryWorker,
            leasedDelivery.LeaseToken,
            leasedDelivery.LeaseGeneration);
        var initialDeliveryLease = await db.SharedVariableWakeDeliveries.AsNoTracking()
            .SingleAsync(item => item.Id == delivery.Id);
        Assert.NotNull(initialDeliveryLease.HeartbeatAt);
        Assert.True(await repository.HeartbeatWakeAsync(
            deliveryFence,
            TimeSpan.FromMinutes(1),
            CancellationToken.None));
        var heartbeatedDelivery = await db.SharedVariableWakeDeliveries.AsNoTracking()
            .SingleAsync(item => item.Id == delivery.Id);
        Assert.True(heartbeatedDelivery.HeartbeatAt >= initialDeliveryLease.HeartbeatAt);
        _ = await repository.CompleteWakeDeliveryAsync(
            deliveryFence,
            failure: null,
            CancellationToken.None);
        Assert.Null((await db.SharedVariableWakeDeliveries.AsNoTracking()
            .SingleAsync(item => item.Id == delivery.Id)).HeartbeatAt);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task AcquisitionEscalatesPostMigrationLegacyFailuresExactlyOnce()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        await QuarantineOpenWakesAsync(db);
        await QuarantineOpenDeliveriesAsync(db);

        var suffix = Guid.NewGuid().ToString("N");
        var key = $"tests.legacy-failure.{suffix}";
        var created = await repository.CreateAsync(
            CreateCommand(key, "number", nullable: false, validation: null,
                JsonSerializer.SerializeToElement(1)),
            CancellationToken.None);
        var revision = await db.SharedVariableRevisions.SingleAsync(item =>
            item.SharedVariableId == created.Variable.Id
            && item.Revision == created.Revision.Revision);
        var runtime = await AddConditionalWaitAsync(db, created.Variable.Id, key, suffix);
        var now = DateTimeOffset.UtcNow;
        var wake = new SharedVariableWakeEntity
        {
            SharedVariableId = created.Variable.Id,
            RevisionId = revision.Id,
            Revision = revision.Revision,
            Status = SharedVariableWakeStatuses.Failed,
            AttemptCount = 7,
            MaxAttempts = 25,
            LastError = "Legacy expansion failure detail.",
            AvailableAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.SharedVariableWakes.Add(wake);
        await db.SaveChangesAsync();
        var delivery = new SharedVariableWakeDeliveryEntity
        {
            WakeId = wake.Id,
            InstanceId = runtime.Instance.Id,
            WorkflowDefinitionId = runtime.Definition.Id,
            TokenId = runtime.Token.Id,
            ActivationId = runtime.Token.ActivationId,
            NodeId = runtime.Token.NodeId,
            Status = SharedVariableWakeStatuses.Failed,
            AttemptCount = 4,
            MaxAttempts = 25,
            LastError = "Legacy delivery failure detail.",
            AvailableAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.SharedVariableWakeDeliveries.Add(delivery);
        await db.SaveChangesAsync();

        Assert.Empty(await repository.LeaseWakeExpansionsAsync(
            new SharedVariableWakeLeaseRequest("legacy-expansion-sweep", 10, TimeSpan.FromMinutes(1)),
            CancellationToken.None));
        Assert.Empty(await repository.LeaseWakeDeliveriesAsync(
            new SharedVariableWakeLeaseRequest("legacy-delivery-sweep", 10, TimeSpan.FromMinutes(1)),
            CancellationToken.None));
        Assert.Empty(await repository.LeaseWakeExpansionsAsync(
            new SharedVariableWakeLeaseRequest("legacy-expansion-sweep-2", 10, TimeSpan.FromMinutes(1)),
            CancellationToken.None));
        Assert.Empty(await repository.LeaseWakeDeliveriesAsync(
            new SharedVariableWakeLeaseRequest("legacy-delivery-sweep-2", 10, TimeSpan.FromMinutes(1)),
            CancellationToken.None));

        var incidents = await db.SharedVariableWakeIncidents.AsNoTracking()
            .Where(item => item.OriginalWakeId == wake.Id)
            .OrderBy(item => item.WorkKind)
            .ToListAsync();
        Assert.Equal(2, incidents.Count);
        var deliveryIncident = Assert.Single(incidents, item =>
            item.WorkKind == SharedVariableWakeWorkKinds.Delivery);
        Assert.Equal(delivery.Id, deliveryIncident.OriginalDeliveryId);
        Assert.Equal("legacyFailure", deliveryIncident.Type);
        Assert.Equal("Legacy delivery failure detail.", deliveryIncident.Details);
        var expansionIncident = Assert.Single(incidents, item =>
            item.WorkKind == SharedVariableWakeWorkKinds.Expansion);
        Assert.Null(expansionIncident.OriginalDeliveryId);
        Assert.Equal("legacyFailure", expansionIncident.Type);
        Assert.Equal("Legacy expansion failure detail.", expansionIncident.Details);
        Assert.Equal(
            SharedVariableWakeStatuses.Incident,
            (await db.SharedVariableWakes.AsNoTracking().SingleAsync(item => item.Id == wake.Id)).Status);
        Assert.Equal(
            SharedVariableWakeStatuses.Incident,
            (await db.SharedVariableWakeDeliveries.AsNoTracking()
                .SingleAsync(item => item.Id == delivery.Id)).Status);

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task CompatibilityTriggerAdvancesValueFenceOnlyForEffectiveLegacyWrites()
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var repository = scope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var key = $"tests.mixed-value-fence.{Guid.NewGuid():N}";
        var created = await repository.CreateAsync(
            CreateCommand(key, "number", nullable: false, validation: null,
                JsonSerializer.SerializeToElement(1)),
            CancellationToken.None);
        var initialValueRevision = created.Variable.ValueRevision;

        var legacyNoOpRevisionNumber = await db.Database.SqlQueryRaw<long>(
                "SELECT flowbit.next_shared_variable_revision() AS \"Value\"")
            .SingleAsync();
        var legacyNoOpRevision = new SharedVariableRevisionEntity
        {
            SharedVariableId = created.Variable.Id,
            Revision = legacyNoOpRevisionNumber,
            Operation = SharedVariableOperations.Set,
            ValueChanged = false,
            HasValue = true,
            ValueJson = JsonDocument.Parse("1"),
            CallerKind = SharedVariableCallerKinds.System,
            CallerId = "legacy-writer",
            Source = SharedVariableSources.System,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SharedVariableRevisions.Add(legacyNoOpRevision);
        await db.SaveChangesAsync();
        await db.SharedVariableCurrentValues
            .Where(item => item.SharedVariableId == created.Variable.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.SourceRevisionId, legacyNoOpRevision.Id)
                .SetProperty(item => item.Revision, legacyNoOpRevisionNumber)
                .SetProperty(item => item.SetAt, DateTimeOffset.UtcNow));
        db.ChangeTracker.Clear();

        var afterLegacyNoOp = await db.SharedVariables.AsNoTracking()
            .SingleAsync(item => item.Id == created.Variable.Id);
        Assert.Equal(legacyNoOpRevisionNumber, afterLegacyNoOp.CurrentRevision);
        Assert.Equal(initialValueRevision, afterLegacyNoOp.ValueRevision);
        var newWriterNoOp = await repository.WriteAsync(
            new SharedVariableWriteCommand(
                key,
                JsonSerializer.SerializeToElement(1),
                DeleteValue: false,
                ExpectedRevision: legacyNoOpRevisionNumber,
                TestCaller(),
                SharedVariableSources.System,
                ExpectedValueRevision: initialValueRevision),
            CancellationToken.None);
        Assert.NotNull(newWriterNoOp);
        Assert.False(newWriterNoOp.Revision.ValueChanged);
        Assert.Equal(initialValueRevision, newWriterNoOp.Variable.ValueRevision);

        var legacyValueRevisionNumber = await db.Database.SqlQueryRaw<long>(
                "SELECT flowbit.next_shared_variable_revision() AS \"Value\"")
            .SingleAsync();
        var legacyValueRevision = new SharedVariableRevisionEntity
        {
            SharedVariableId = created.Variable.Id,
            Revision = legacyValueRevisionNumber,
            Operation = SharedVariableOperations.Set,
            ValueChanged = true,
            HasValue = true,
            ValueJson = JsonDocument.Parse("2"),
            CallerKind = SharedVariableCallerKinds.System,
            CallerId = "legacy-writer",
            Source = SharedVariableSources.System,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.SharedVariableRevisions.Add(legacyValueRevision);
        await db.SaveChangesAsync();
        using var legacyValue = JsonDocument.Parse("2");
        await db.SharedVariableCurrentValues
            .Where(item => item.SharedVariableId == created.Variable.Id)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(item => item.SourceRevisionId, legacyValueRevision.Id)
                .SetProperty(item => item.Revision, legacyValueRevisionNumber)
                .SetProperty(item => item.ValueJson, legacyValue)
                .SetProperty(item => item.IsDeleted, false)
                .SetProperty(item => item.SetAt, DateTimeOffset.UtcNow));
        db.ChangeTracker.Clear();

        var afterLegacyValueWrite = await db.SharedVariables.AsNoTracking()
            .SingleAsync(item => item.Id == created.Variable.Id);
        Assert.Equal(legacyValueRevisionNumber, afterLegacyValueWrite.CurrentRevision);
        Assert.Equal(legacyValueRevisionNumber, afterLegacyValueWrite.ValueRevision);
        await Assert.ThrowsAsync<WorkflowConflictException>(() => repository.WriteAsync(
            new SharedVariableWriteCommand(
                key,
                JsonSerializer.SerializeToElement(3),
                DeleteValue: false,
                ExpectedRevision: null,
                TestCaller(),
                SharedVariableSources.System,
                ExpectedValueRevision: initialValueRevision),
            CancellationToken.None));

        await transaction.RollbackAsync();
    }

    [Fact]
    public async Task LegacyAllocatorPrelockRequiresTransactionAndLocksKeysOnlyInLegacyMode()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, HardenedSharedVariableMigration);
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
            dataSourceBuilder.EnableDynamicJson();
            await using var dataSource = dataSourceBuilder.Build();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(dataSource, FlowbitDatabase.ConfigureProvider)
                .Options;
            var suffix = Guid.NewGuid().ToString("N");
            var firstKey = $"tests.allocator-prelock.a.{suffix}";
            var secondKey = $"tests.allocator-prelock.b.{suffix}";

            await using (var setup = new AppDbContext(options))
            {
                var repository = new SharedVariableRepository(setup);
                await repository.CreateAsync(
                    CreateCommand(firstKey, "number", false, null,
                        JsonSerializer.SerializeToElement(1)),
                    CancellationToken.None);
                await repository.CreateAsync(
                    CreateCommand(secondKey, "number", false, null,
                        JsonSerializer.SerializeToElement(1)),
                    CancellationToken.None);
                await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    repository.PrelockDefinitionKeysForLegacyAllocatorAsync(
                        [secondKey, firstKey],
                        CancellationToken.None));
            }

            await using (var legacy = new AppDbContext(options))
            await using (var legacyTransaction = await legacy.Database.BeginTransactionAsync())
            {
                var repository = new SharedVariableRepository(legacy);
                await repository.PrelockDefinitionKeysForLegacyAllocatorAsync(
                    [secondKey, firstKey, secondKey],
                    CancellationToken.None);
                Assert.False(await CanAcquireCatalogLockNowaitAsync(dataSource, firstKey));
                Assert.False(await CanAcquireCatalogLockNowaitAsync(dataSource, secondKey));
                await legacyTransaction.RollbackAsync();
            }

            await using (var cutover = new AppDbContext(options))
            {
                _ = await cutover.Database.SqlQueryRaw<long>(
                        "SELECT flowbit.cutover_shared_variable_revision_sequence() AS \"Value\"")
                    .SingleAsync();
            }

            await using (var sequence = new AppDbContext(options))
            await using (var sequenceTransaction = await sequence.Database.BeginTransactionAsync())
            {
                var repository = new SharedVariableRepository(sequence);
                await repository.PrelockDefinitionKeysForLegacyAllocatorAsync(
                    [secondKey, firstKey],
                    CancellationToken.None);
                Assert.True(await CanAcquireCatalogLockNowaitAsync(dataSource, firstKey));
                Assert.True(await CanAcquireCatalogLockNowaitAsync(dataSource, secondKey));
                await sequenceTransaction.RollbackAsync();
            }
        });
    }

    [Fact]
    public async Task MigrationBackfillRedactsListSummaryAndPreservesDetailOnlyForIncidentDetail()
    {
        const string legacyDiagnostic = "secret connector diagnostic: credential=value";
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, PreviousSharedVariableMigration);
            var key = $"tests.migration-incident-redaction.{Guid.NewGuid():N}";
            await using (var connection = new NpgsqlConnection(connectionString))
            {
                await connection.OpenAsync();
                await using var seed = new NpgsqlCommand(
                    """
                    WITH variable AS (
                        INSERT INTO flowbit.shared_variables (
                            "Key", "DataType", "IsArray", "Nullable", "Status",
                            "CurrentRevision", "CreatedByKind", "CreatedById",
                            "UpdatedByKind", "UpdatedById", "CreatedAt", "UpdatedAt")
                        VALUES (@key, 'number', FALSE, FALSE, 'active', 1,
                            'system', 'legacy-seed', 'system', 'legacy-seed',
                            clock_timestamp(), clock_timestamp())
                        RETURNING "Id"
                    ), revision AS (
                        INSERT INTO flowbit.shared_variable_revisions (
                            "SharedVariableId", "Revision", "Operation", "ValueChanged",
                            "ValueJson", "HasValue", "CallerKind", "CallerId", "Source",
                            "CreatedAt")
                        SELECT "Id", 1, 'create', FALSE, NULL, FALSE,
                            'system', 'legacy-seed', 'system', clock_timestamp()
                        FROM variable
                        RETURNING "Id", "SharedVariableId", "Revision"
                    )
                    INSERT INTO flowbit.shared_variable_wakes (
                        "SharedVariableId", "RevisionId", "Revision", "Status",
                        "LeaseGeneration", "AvailableAt", "AttemptCount", "LastError",
                        "CreatedAt", "UpdatedAt")
                    SELECT "SharedVariableId", "Id", "Revision", 'failed', 0,
                        clock_timestamp(), 25, @diagnostic,
                        clock_timestamp(), clock_timestamp()
                    FROM revision;

                    UPDATE flowbit.shared_variable_revision_state
                    SET "LastRevision" = 1,
                        "UpdatedAt" = clock_timestamp()
                    WHERE "Id" = 1;
                    """,
                    connection);
                seed.Parameters.AddWithValue("key", key);
                seed.Parameters.AddWithValue("diagnostic", legacyDiagnostic);
                await seed.ExecuteNonQueryAsync();
            }

            await MigrateAsync(connectionString, HardenedSharedVariableMigration);
            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
            dataSourceBuilder.EnableDynamicJson();
            await using var dataSource = dataSourceBuilder.Build();
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseNpgsql(dataSource, FlowbitDatabase.ConfigureProvider)
                .Options;
            await using var db = new AppDbContext(options);
            var repository = new SharedVariableRepository(db);
            var list = await repository.SearchWakeIncidentsAsync(
                new SharedVariableWakeIncidentQuery(SharedVariableWakeIncidentStatuses.Open,
                    SharedVariableWakeWorkKinds.Expansion, key, 0, 10),
                CancellationToken.None);
            var summary = Assert.Single(list.Items);
            Assert.Equal("Legacy shared-variable wake expansion failure.", summary.Summary);
            Assert.DoesNotContain(legacyDiagnostic, summary.Summary, StringComparison.Ordinal);
            Assert.Null(summary.Details);

            var detail = await repository.GetWakeIncidentAsync(summary.Id, CancellationToken.None);
            Assert.NotNull(detail);
            Assert.Equal(legacyDiagnostic, detail.Details);
        });
    }

    [Fact]
    public async Task DownMigrationDowngradesIncidentsAndReseedsLegacyAllocatorAfterCutover()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, PreviousSharedVariableMigration);
            await MigrateAsync(connectionString, HardenedSharedVariableMigration);
            long wakeId;
            long deliveryId;
            long sequenceHighWatermark;

            var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
            dataSourceBuilder.EnableDynamicJson();
            await using (var dataSource = dataSourceBuilder.Build())
            {
                var options = new DbContextOptionsBuilder<AppDbContext>()
                    .UseNpgsql(dataSource, FlowbitDatabase.ConfigureProvider)
                    .Options;
                await using var db = new AppDbContext(options);
                var repository = new SharedVariableRepository(db);
                var suffix = Guid.NewGuid().ToString("N");
                var key = $"tests.down-incident.{suffix}";
                var created = await repository.CreateAsync(
                    CreateCommand(key, "number", nullable: false, validation: null,
                        JsonSerializer.SerializeToElement(1)),
                    CancellationToken.None);
                var runtime = await AddConditionalWaitAsync(db, created.Variable.Id, key, suffix);

                _ = await db.Database.SqlQueryRaw<long>(
                        "SELECT flowbit.cutover_shared_variable_revision_sequence() AS \"Value\"")
                    .SingleAsync();
                var committedSequenceRevision = await db.Database.SqlQueryRaw<long>(
                        "SELECT flowbit.next_shared_variable_revision() AS \"Value\"")
                    .SingleAsync();
                sequenceHighWatermark = await db.Database.SqlQueryRaw<long>(
                        "SELECT flowbit.next_shared_variable_revision() AS \"Value\"")
                    .SingleAsync();
                var variable = await db.SharedVariables.SingleAsync(item => item.Id == created.Variable.Id);
                var revision = new SharedVariableRevisionEntity
                {
                    SharedVariableId = variable.Id,
                    Revision = committedSequenceRevision,
                    Operation = SharedVariableOperations.UpdateDescription,
                    ValueChanged = false,
                    HasValue = false,
                    CallerKind = SharedVariableCallerKinds.System,
                    CallerId = "down-migration-test",
                    Source = SharedVariableSources.System,
                    CreatedAt = DateTimeOffset.UtcNow
                };
                db.SharedVariableRevisions.Add(revision);
                variable.CurrentRevision = committedSequenceRevision;
                await db.SaveChangesAsync();

                var now = DateTimeOffset.UtcNow;
                var wake = new SharedVariableWakeEntity
                {
                    SharedVariableId = variable.Id,
                    RevisionId = revision.Id,
                    Revision = revision.Revision,
                    Status = SharedVariableWakeStatuses.Incident,
                    AttemptCount = 25,
                    MaxAttempts = 25,
                    LastError = "fallback expansion error",
                    AvailableAt = now,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.SharedVariableWakes.Add(wake);
                await db.SaveChangesAsync();
                var delivery = new SharedVariableWakeDeliveryEntity
                {
                    WakeId = wake.Id,
                    InstanceId = runtime.Instance.Id,
                    WorkflowDefinitionId = runtime.Definition.Id,
                    TokenId = runtime.Token.Id,
                    ActivationId = runtime.Token.ActivationId,
                    NodeId = runtime.Token.NodeId,
                    Status = SharedVariableWakeStatuses.Incident,
                    AttemptCount = 25,
                    MaxAttempts = 25,
                    LastError = "fallback delivery error",
                    AvailableAt = now,
                    CreatedAt = now,
                    UpdatedAt = now
                };
                db.SharedVariableWakeDeliveries.Add(delivery);
                await db.SaveChangesAsync();
                wakeId = wake.Id;
                deliveryId = delivery.Id;
                db.SharedVariableWakeIncidents.AddRange(
                    new SharedVariableWakeIncidentEntity
                    {
                        WorkKind = SharedVariableWakeWorkKinds.Expansion,
                        WakeId = wake.Id,
                        OriginalWakeId = wake.Id,
                        SharedVariableId = variable.Id,
                        SharedKey = key,
                        Revision = revision.Revision,
                        Type = "downExpansion",
                        Status = SharedVariableWakeIncidentStatuses.Open,
                        Summary = "Expansion rollback summary.",
                        Details = "Expansion incident detail retained as legacy error.",
                        CreatedAt = now,
                        UpdatedAt = now
                    },
                    new SharedVariableWakeIncidentEntity
                    {
                        WorkKind = SharedVariableWakeWorkKinds.Delivery,
                        DeliveryId = delivery.Id,
                        OriginalWakeId = wake.Id,
                        OriginalDeliveryId = delivery.Id,
                        SharedVariableId = variable.Id,
                        SharedKey = key,
                        Revision = revision.Revision,
                        InstanceId = runtime.Instance.Id,
                        WorkflowDefinitionId = runtime.Definition.Id,
                        TokenId = runtime.Token.Id,
                        ActivationId = runtime.Token.ActivationId,
                        NodeId = runtime.Token.NodeId,
                        Type = "downDelivery",
                        Status = SharedVariableWakeIncidentStatuses.Open,
                        Summary = "Delivery rollback summary.",
                        Details = "Delivery incident detail retained as legacy error.",
                        CreatedAt = now,
                        UpdatedAt = now
                    });
                await db.SaveChangesAsync();
            }

            await MigrateAsync(connectionString, PreviousSharedVariableMigration);

            await using var verify = new NpgsqlConnection(connectionString);
            await verify.OpenAsync();
            await using (var command = new NpgsqlCommand(
                """
                SELECT "Status", "LastError", "LeaseToken", "LeasedBy", "LeaseExpiresAt", "CompletedAt"
                FROM flowbit.shared_variable_wakes
                WHERE "Id" = @id
                """, verify))
            {
                command.Parameters.AddWithValue("id", wakeId);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(SharedVariableWakeStatuses.Failed, reader.GetString(0));
                Assert.Equal("Expansion incident detail retained as legacy error.", reader.GetString(1));
                Assert.True(reader.IsDBNull(2));
                Assert.True(reader.IsDBNull(3));
                Assert.True(reader.IsDBNull(4));
                Assert.True(reader.IsDBNull(5));
            }
            await using (var command = new NpgsqlCommand(
                """
                SELECT "Status", "LastError", "LeaseToken", "LeasedBy", "LeaseExpiresAt", "CompletedAt"
                FROM flowbit.shared_variable_wake_deliveries
                WHERE "Id" = @id
                """, verify))
            {
                command.Parameters.AddWithValue("id", deliveryId);
                await using var reader = await command.ExecuteReaderAsync();
                Assert.True(await reader.ReadAsync());
                Assert.Equal(SharedVariableWakeStatuses.Failed, reader.GetString(0));
                Assert.Equal("Delivery incident detail retained as legacy error.", reader.GetString(1));
                Assert.True(reader.IsDBNull(2));
                Assert.True(reader.IsDBNull(3));
                Assert.True(reader.IsDBNull(4));
                Assert.True(reader.IsDBNull(5));
            }
            long reseeded;
            await using (var command = new NpgsqlCommand(
                """
                UPDATE flowbit.shared_variable_revision_state
                SET "LastRevision" = "LastRevision" + 1,
                    "UpdatedAt" = clock_timestamp()
                WHERE "Id" = 1
                RETURNING "LastRevision"
                """, verify))
            {
                reseeded = Convert.ToInt64(await command.ExecuteScalarAsync());
            }
            Assert.True(reseeded > sequenceHighWatermark);
            await using var incidentTable = new NpgsqlCommand(
                "SELECT to_regclass('flowbit.shared_variable_wake_incidents') IS NULL",
                verify);
            Assert.True(Convert.ToBoolean(await incidentTable.ExecuteScalarAsync()));
            await using var wakeClockFunction = new NpgsqlCommand(
                "SELECT to_regprocedure('flowbit.stamp_initial_shared_variable_wake_clock()') IS NULL",
                verify);
            Assert.True(Convert.ToBoolean(await wakeClockFunction.ExecuteScalarAsync()));
        });
    }

    private static SharedVariableCreateCommand CreateCommand(
        string key,
        string dataType,
        bool nullable,
        string? validation,
        JsonElement value) => new(
        key,
        dataType,
        IsArray: false,
        Nullable: nullable,
        Validation: validation,
        Description: null,
        HasValue: true,
        value,
        TestCaller(),
        SharedVariableSources.System,
        RequestId: null,
        RequestFingerprint: null,
        Reason: null);

    private static SharedVariableWriteCommand WriteCommand(
        string key,
        JsonElement value,
        long expectedRevision,
        long expectedValueRevision) => new(
        key,
        value,
        DeleteValue: false,
        ExpectedRevision: expectedRevision,
        TestCaller(),
        SharedVariableSources.System,
        ExpectedValueRevision: expectedValueRevision);

    private static async Task<(
        SharedVariableMutationResult Created,
        SharedVariableRevisionEntity Revision,
        SharedVariableWakeEntity Wake)> AddPendingWakeAsync(
        AppDbContext db,
        ISharedVariableRepository repository,
        string key)
    {
        var created = await repository.CreateAsync(
            CreateCommand(
                key,
                "number",
                nullable: false,
                validation: null,
                JsonSerializer.SerializeToElement(1)),
            CancellationToken.None);
        var revision = await db.SharedVariableRevisions.SingleAsync(item =>
            item.SharedVariableId == created.Variable.Id
            && item.Revision == created.Revision.Revision);
        var now = DateTimeOffset.UtcNow;
        var wake = new SharedVariableWakeEntity
        {
            SharedVariableId = created.Variable.Id,
            RevisionId = revision.Id,
            Revision = revision.Revision,
            Status = SharedVariableWakeStatuses.Pending,
            MaxAttempts = 25,
            AvailableAt = now,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.SharedVariableWakes.Add(wake);
        await db.SaveChangesAsync();
        return (created, revision, wake);
    }

    private static SharedVariableWakeFence ExpansionFence(
        SharedVariableWakeRecord wake,
        string workerId) => new(
        SharedVariableWakeWorkKinds.Expansion,
        wake.Id,
        workerId,
        wake.LeaseToken,
        wake.LeaseGeneration);

    private static SharedVariableCallerRecord TestCaller() => new(
        SharedVariableCallerKinds.System,
        "shared-variable-persistence-test",
        [],
        []);

    private static async Task<(
        WorkflowDefinitionEntity Definition,
        WorkflowInstanceEntity Instance,
        ExecutionTokenEntity Token)> AddConditionalWaitAsync(
        AppDbContext db,
        long sharedVariableId,
        string sharedKey,
        string suffix)
    {
        var now = DateTimeOffset.UtcNow;
        var definition = new WorkflowDefinitionEntity
        {
            Name = $"shared-wait-{suffix}",
            WorkflowKey = $"shared-wait-{suffix}",
            Version = 1,
            Definition = new WorkflowModel
            {
                Id = $"shared-wait-{suffix}",
                Name = $"shared-wait-{suffix}"
            },
            CreatedAt = now
        };
        db.WorkflowDefinitions.Add(definition);
        await db.SaveChangesAsync();
        db.WorkflowDefinitionSharedVariableDependencies.Add(
            new WorkflowDefinitionSharedVariableDependencyEntity
            {
                WorkflowDefinitionId = definition.Id,
                SharedVariableId = sharedVariableId,
                SharedKey = sharedKey,
                NodeId = 2,
                NodeExternalId = "conditional-wait",
                Kind = SharedVariableDependencyKinds.ConditionalCatch,
                CreatedAt = now
            });
        var instance = new WorkflowInstanceEntity
        {
            WorkflowDefinitionId = definition.Id,
            WorkflowKey = definition.WorkflowKey,
            Status = WorkflowInstanceStatuses.Running,
            CreatedAt = now,
            UpdatedAt = now
        };
        db.WorkflowInstances.Add(instance);
        await db.SaveChangesAsync();
        var token = new ExecutionTokenEntity
        {
            InstanceId = instance.Id,
            NodeId = 2,
            NodeName = "Conditional wait",
            NodeType = BpmnFlowNodeTypes.IntermediateConditionalCatchEvent,
            Status = ExecutionTokenStatuses.Active,
            ActivationId = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now
        };
        db.ExecutionTokens.Add(token);
        await db.SaveChangesAsync();
        return (definition, instance, token);
    }

    private static async Task QuarantineOpenWakesAsync(AppDbContext db)
    {
        await db.SharedVariableWakes
            .Where(wake => wake.Status == SharedVariableWakeStatuses.Pending
                           || wake.Status == SharedVariableWakeStatuses.Leased)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(wake => wake.Status, SharedVariableWakeStatuses.Cancelled)
                .SetProperty(wake => wake.LeaseToken, (Guid?)null)
                .SetProperty(wake => wake.LeasedBy, (string?)null)
                .SetProperty(wake => wake.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(wake => wake.HeartbeatAt, (DateTimeOffset?)null)
                .SetProperty(wake => wake.CompletedAt, DateTimeOffset.UtcNow));
    }

    private static async Task QuarantineOpenDeliveriesAsync(AppDbContext db)
    {
        await db.SharedVariableWakeDeliveries
            .Where(delivery => delivery.Status == SharedVariableWakeStatuses.Pending
                               || delivery.Status == SharedVariableWakeStatuses.Leased)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(delivery => delivery.Status, SharedVariableWakeStatuses.Cancelled)
                .SetProperty(delivery => delivery.LeaseToken, (Guid?)null)
                .SetProperty(delivery => delivery.LeasedBy, (string?)null)
                .SetProperty(delivery => delivery.LeaseExpiresAt, (DateTimeOffset?)null)
                .SetProperty(delivery => delivery.HeartbeatAt, (DateTimeOffset?)null)
                .SetProperty(delivery => delivery.CompletedAt, DateTimeOffset.UtcNow));
    }

    private async Task<T> RunBlockedPastLeaseExpiryAsync<T>(
        string tableName,
        long workId,
        Func<Task<T>> operation)
    {
        if (tableName is not ("shared_variable_wakes" or "shared_variable_wake_deliveries"))
            throw new ArgumentOutOfRangeException(nameof(tableName));

        DateTimeOffset expiresAt;
        await using (var expiryConnection = await fixture.DataSource.OpenConnectionAsync())
        await using (var expiryCommand = new NpgsqlCommand(
            $"""
            UPDATE flowbit.{tableName}
            SET "LeaseExpiresAt" = clock_timestamp() + interval '1500 milliseconds'
            WHERE "Id" = @id
            RETURNING "LeaseExpiresAt"
            """,
            expiryConnection))
        {
            expiryCommand.Parameters.AddWithValue("id", workId);
            expiresAt = ReadDatabaseTimestamp(await expiryCommand.ExecuteScalarAsync());
        }

        await using var blockerConnection = await fixture.DataSource.OpenConnectionAsync();
        await using var blockerTransaction = await blockerConnection.BeginTransactionAsync();
        await using (var blockerCommand = new NpgsqlCommand(
            $"SELECT \"Id\" FROM flowbit.{tableName} WHERE \"Id\" = @id FOR UPDATE",
            blockerConnection,
            blockerTransaction))
        {
            blockerCommand.Parameters.AddWithValue("id", workId);
            Assert.Equal(workId, Convert.ToInt64(await blockerCommand.ExecuteScalarAsync()));
        }

        var operationTask = operation();
        await Task.Delay(TimeSpan.FromMilliseconds(100));
        Assert.False(operationTask.IsCompleted);

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using (var observerConnection = await fixture.DataSource.OpenConnectionAsync(timeout.Token))
        await using (var clockCommand = new NpgsqlCommand(
            "SELECT clock_timestamp()",
            observerConnection))
        {
            while (ReadDatabaseTimestamp(await clockCommand.ExecuteScalarAsync(timeout.Token)) <= expiresAt)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(25), timeout.Token);
            }
        }

        await blockerTransaction.CommitAsync();
        return await operationTask.WaitAsync(TimeSpan.FromSeconds(10));
    }

    private static DateTimeOffset ReadDatabaseTimestamp(object? value) => value switch
    {
        DateTimeOffset timestamp => timestamp,
        DateTime timestamp => new DateTimeOffset(DateTime.SpecifyKind(timestamp, DateTimeKind.Utc)),
        _ => throw new InvalidOperationException("PostgreSQL returned an unexpected timestamp value.")
    };

    private static async Task<bool> CanAcquireCatalogLockNowaitAsync(
        NpgsqlDataSource dataSource,
        string key)
    {
        await using var connection = await dataSource.OpenConnectionAsync();
        await using var transaction = await connection.BeginTransactionAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT "Id"
            FROM flowbit.shared_variables
            WHERE "Key" = @key
            FOR UPDATE NOWAIT
            """,
            connection,
            transaction);
        command.Parameters.AddWithValue("key", key);
        try
        {
            Assert.NotNull(await command.ExecuteScalarAsync());
            await transaction.RollbackAsync();
            return true;
        }
        catch (PostgresException exception)
            when (exception.SqlState == PostgresErrorCodes.LockNotAvailable)
        {
            return false;
        }
    }

    private async Task WithIsolatedDatabaseAsync(Func<string, Task> test)
    {
        var databaseName = "shared_persistence_" + Guid.NewGuid().ToString("N");
        var adminBuilder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = "postgres"
        };
        await using (var admin = new NpgsqlConnection(adminBuilder.ConnectionString))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE \"{databaseName}\"", admin);
            await create.ExecuteNonQueryAsync();
        }

        var databaseBuilder = new NpgsqlConnectionStringBuilder(fixture.ConnectionString)
        {
            Database = databaseName
        };
        try
        {
            await test(databaseBuilder.ConnectionString);
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString);
            await admin.OpenAsync();
            await using var drop = new NpgsqlCommand(
                $"DROP DATABASE \"{databaseName}\" WITH (FORCE)",
                admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task MigrateAsync(string connectionString, string targetMigration)
    {
        var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);
        dataSourceBuilder.EnableDynamicJson();
        await using var dataSource = dataSourceBuilder.Build();
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(dataSource, FlowbitDatabase.ConfigureProvider)
            .Options;
        await using var context = new AppDbContext(options);
        await context.GetService<IMigrator>().MigrateAsync(targetMigration);
    }
}
