using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class SharedVariablePersistenceTests(PostgresApiFixture fixture)
{
    private const string HardenedSharedVariableMigration =
        "20260825180852_HardenSharedVariablePersistence";
    private const string RemovalMigration =
        "20260826165710_RemoveSharedVariableConditionalWakes";

    [Fact]
    public async Task RemovalMigrationAppliesRollsBackAndReappliesOnEmptyLegacyState()
    {
        await WithIsolatedDatabaseAsync(async connectionString =>
        {
            await MigrateAsync(connectionString, HardenedSharedVariableMigration);
            Assert.True(await LegacyConditionalWakeArtifactsExistAsync(connectionString));

            await MigrateAsync(connectionString, RemovalMigration);
            Assert.False(await LegacyConditionalWakeArtifactsExistAsync(connectionString));

            await MigrateAsync(connectionString, HardenedSharedVariableMigration);
            Assert.True(await LegacyConditionalWakeArtifactsExistAsync(connectionString));

            await MigrateAsync(connectionString, RemovalMigration);
            Assert.False(await LegacyConditionalWakeArtifactsExistAsync(connectionString));
        });
    }

    [Fact]
    public async Task RemovalMigrationRefusesNonEmptyLegacyStateBeforeDroppingAnything()
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
            long definitionId;
            SharedVariableMutationResult created;
            await using (var db = new AppDbContext(options))
            {
                var repository = new SharedVariableRepository(db);
                created = await repository.CreateAsync(
                    CreateCommand(
                        $"tests.removal-guard.{Guid.NewGuid():N}",
                        "boolean",
                        nullable: false,
                        validation: null,
                        JsonSerializer.SerializeToElement(false)),
                    CancellationToken.None);
                var now = DateTimeOffset.UtcNow;
                var definition = new WorkflowDefinitionEntity
                {
                    Name = "legacy shared conditional dependency",
                    WorkflowKey = $"tests-removal-guard-{Guid.NewGuid():N}",
                    Version = 1,
                    Definition = new Flowbit.Shared.Models.WorkflowModel
                    {
                        Id = $"tests-removal-guard-{Guid.NewGuid():N}",
                        Name = "legacy shared conditional dependency"
                    },
                    CreatedAt = now
                };
                db.WorkflowDefinitions.Add(definition);
                await db.SaveChangesAsync();
                definitionId = definition.Id;
            }

            await using (var connection = await dataSource.OpenConnectionAsync())
            await using (var command = new NpgsqlCommand(
                """
                INSERT INTO flowbit.workflow_definition_shared_variable_dependencies
                    ("WorkflowDefinitionId", "SharedVariableId", "SharedKey",
                     "NodeId", "Kind", "CreatedAt")
                VALUES (@definitionId, @sharedVariableId, @sharedKey,
                        2, 'conditionalCatch', clock_timestamp())
                """,
                connection))
            {
                command.Parameters.AddWithValue("definitionId", definitionId);
                command.Parameters.AddWithValue("sharedVariableId", created.Variable.Id);
                command.Parameters.AddWithValue("sharedKey", created.Variable.Key);
                await command.ExecuteNonQueryAsync();
            }

            var failure = await Assert.ThrowsAnyAsync<Exception>(
                () => MigrateAsync(connectionString, RemovalMigration));
            Assert.Contains(
                "Cannot remove shared-variable conditional wake infrastructure",
                failure.ToString(),
                StringComparison.Ordinal);
            Assert.True(await LegacyConditionalWakeArtifactsExistAsync(connectionString));
        });
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

    private static SharedVariableCallerRecord TestCaller() => new(
        SharedVariableCallerKinds.System,
        "shared-variable-persistence-test",
        [],
        []);

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

    private static async Task<bool> LegacyConditionalWakeArtifactsExistAsync(
        string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(
            """
            SELECT to_regclass('flowbit.shared_variable_wake_incidents') IS NOT NULL
               AND to_regclass('flowbit.shared_variable_wake_deliveries') IS NOT NULL
               AND to_regclass('flowbit.shared_variable_wakes') IS NOT NULL
               AND to_regclass('flowbit.workflow_definition_shared_variable_dependencies') IS NOT NULL
               AND to_regprocedure('flowbit.stamp_initial_shared_variable_wake_clock()') IS NOT NULL
            """,
            connection);
        return Convert.ToBoolean(await command.ExecuteScalarAsync());
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
