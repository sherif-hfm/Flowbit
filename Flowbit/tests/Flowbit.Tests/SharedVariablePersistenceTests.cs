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
            await AddCurrentCatalogReadColumnsAsync(connectionString);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SameKeyWriterWaitsUntilTheOwningTransactionFinishes(bool commit)
    {
        var key = $"tests.sequence-lock.{Guid.NewGuid():N}";
        var initial = await CreateInIndependentScopeAsync(key);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ct = timeout.Token;
        await using var ownerDb = fixture.CreateDbContext();
        await using var ownerTransaction = await ownerDb.Database.BeginTransactionAsync(ct);
        var staged = await new SharedVariableRepository(ownerDb).WriteAsync(
            WriteCommand(key, JsonSerializer.SerializeToElement(2),
                initial.Variable.Revision, initial.Variable.ValueRevision), ct);
        Assert.NotNull(staged);
        var ownerPid = ((NpgsqlConnection)ownerDb.Database.GetDbConnection()).ProcessID;

        await using var waitingDb = fixture.CreateDbContext();
        await waitingDb.Database.OpenConnectionAsync(ct);
        var waitingPid = ((NpgsqlConnection)waitingDb.Database.GetDbConnection()).ProcessID;
        var waitingWriter = new SharedVariableRepository(waitingDb).WriteAsync(
            new SharedVariableWriteCommand(key, JsonSerializer.SerializeToElement(3),
                DeleteValue: false, ExpectedRevision: null, TestCaller(), SharedVariableSources.System), ct);
        var released = false;
        try
        {
            await WaitForCatalogLockAsync(waitingPid, ownerPid, waitingWriter, ct);
            Assert.False(waitingWriter.IsCompleted);
            if (commit) await ownerTransaction.CommitAsync(ct);
            else await ownerTransaction.RollbackAsync(ct);
            released = true;

            var completed = await waitingWriter;
            Assert.NotNull(completed);
            Assert.Equal(3, completed.Variable.Value!.Value.GetInt32());
            await using var verify = fixture.CreateDbContext();
            var revisions = await verify.SharedVariableRevisions.AsNoTracking()
                .Where(row => row.SharedVariableId == initial.Variable.Id)
                .OrderBy(row => row.Revision).ToArrayAsync(ct);
            Assert.Equal(commit ? 3 : 2, revisions.Length);
            Assert.Equal(completed.Revision.Revision, revisions[^1].Revision);
            Assert.True(completed.Revision.Revision > staged.Revision.Revision);
        }
        finally
        {
            if (!released) await ownerTransaction.RollbackAsync(CancellationToken.None);
            // Drain the writer before disposing its connection, including assertion failures.
            try { await waitingWriter; }
            catch when (timeout.IsCancellationRequested || !released) { }
        }
    }

    [Fact]
    public async Task DifferentKeyWriteCommitsWhileAnotherWriteTransactionRemainsOpen()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var first = await CreateInIndependentScopeAsync($"tests.sequence-a.{suffix}");
        var second = await CreateInIndependentScopeAsync($"tests.sequence-b.{suffix}");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;
        await using var firstDb = fixture.CreateDbContext();
        await using var firstTransaction = await firstDb.Database.BeginTransactionAsync(ct);
        var firstWrite = await new SharedVariableRepository(firstDb).WriteAsync(
            WriteCommand(first.Variable.Key, JsonSerializer.SerializeToElement(2),
                first.Variable.Revision, first.Variable.ValueRevision), ct);
        Assert.NotNull(firstWrite);

        await using var secondDb = fixture.CreateDbContext();
        var secondWrite = await new SharedVariableRepository(secondDb).WriteAsync(
            WriteCommand(second.Variable.Key, JsonSerializer.SerializeToElement(3),
                second.Variable.Revision, second.Variable.ValueRevision), ct);
        Assert.NotNull(secondWrite);
        Assert.NotEqual(firstWrite.Revision.Revision, secondWrite.Revision.Revision);
        Assert.NotNull(firstDb.Database.CurrentTransaction);

        await using var verify = fixture.CreateDbContext();
        var values = await verify.SharedVariables.AsNoTracking()
            .Include(variable => variable.CurrentValue)
            .Where(variable => variable.Id == first.Variable.Id || variable.Id == second.Variable.Id)
            .ToDictionaryAsync(variable => variable.Id,
                variable => variable.CurrentValue!.ValueJson!.RootElement.GetInt32(), ct);
        Assert.Equal(1, values[first.Variable.Id]); // The first write is still uncommitted.
        Assert.Equal(3, values[second.Variable.Id]);
        await firstTransaction.RollbackAsync(ct);
    }

    [Fact]
    public async Task ConcurrentWritesWithTheSameExpectedRevisionCommitExactlyOneChange()
    {
        var key = $"tests.sequence-cas.{Guid.NewGuid():N}";
        var initial = await CreateInIndependentScopeAsync(key);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var firstWrite = WriteAfterStartAsync(2);
        var secondWrite = WriteAfterStartAsync(3);
        start.SetResult();
        var outcomes = await Task.WhenAll(firstWrite, secondWrite);
        var winner = Assert.Single(outcomes, outcome => outcome.Result is not null).Result!;
        Assert.Single(outcomes, outcome => outcome.Error is WorkflowConflictException);

        await using var verify = fixture.CreateDbContext();
        var current = await verify.SharedVariables.AsNoTracking()
            .Include(variable => variable.CurrentValue)
            .SingleAsync(variable => variable.Id == initial.Variable.Id);
        Assert.Equal(winner.Variable.Revision, current.CurrentRevision);
        Assert.Equal(winner.Variable.ValueRevision, current.ValueRevision);
        Assert.Equal(winner.Variable.Value!.Value.GetInt32(), current.CurrentValue!.ValueJson!.RootElement.GetInt32());
        var history = await verify.SharedVariableRevisions.AsNoTracking()
            .Where(revision => revision.SharedVariableId == initial.Variable.Id)
            .OrderBy(revision => revision.Revision).ToArrayAsync();
        Assert.Equal(2, history.Length);
        Assert.Equal(winner.Revision.Revision, history[^1].Revision);

        async Task<(SharedVariableMutationResult? Result, Exception? Error)> WriteAfterStartAsync(int value)
        {
            await start.Task;
            try
            {
                await using var writeScope = fixture.Factory.Services.CreateAsyncScope();
                var repository = writeScope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
                return (await repository.WriteAsync(
                    WriteCommand(key, JsonSerializer.SerializeToElement(value),
                        initial.Variable.Revision, initial.Variable.ValueRevision), timeout.Token), null);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SharedLocksRequireATransactionAndLockOnlyRequestedKeys(bool valueStamps)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var first = await CreateInIndependentScopeAsync($"tests.exact-lock.a.{suffix}");
        var second = await CreateInIndependentScopeAsync($"tests.exact-lock.b.{suffix}");
        var unused = await CreateInIndependentScopeAsync($"tests.exact-lock.z.{suffix}");
        await using var db = fixture.CreateDbContext();
        var repository = new SharedVariableRepository(db);
        await LockAsync([]); // An empty set does not acquire any locks.
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            LockAsync([first.Variable.Key]));
        Assert.Contains("ambient database transaction", failure.Message, StringComparison.Ordinal);

        await using var transaction = await db.Database.BeginTransactionAsync();
        await LockAsync([second.Variable.Key, first.Variable.Key, second.Variable.Key]);
        Assert.False(await CanAcquireCatalogLockNowaitAsync(fixture.DataSource, first.Variable.Key));
        Assert.False(await CanAcquireCatalogLockNowaitAsync(fixture.DataSource, second.Variable.Key));
        Assert.True(await CanAcquireCatalogLockNowaitAsync(fixture.DataSource, unused.Variable.Key));
        await transaction.RollbackAsync();
        Assert.True(await CanAcquireCatalogLockNowaitAsync(fixture.DataSource, first.Variable.Key));
        Assert.True(await CanAcquireCatalogLockNowaitAsync(fixture.DataSource, second.Variable.Key));

        async Task LockAsync(string[] keys)
        {
            if (valueStamps)
                _ = await repository.LockValueStampsAsync(keys, includeArchived: true, CancellationToken.None);
            else
                _ = await repository.LockCurrentAsync(keys, includeArchived: true, CancellationToken.None);
        }
    }

    private async Task<SharedVariableMutationResult> CreateInIndependentScopeAsync(string key)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
        return await repository.CreateAsync(
            CreateCommand(key, "number", nullable: false, validation: null,
                JsonSerializer.SerializeToElement(1)), CancellationToken.None);
    }

    private async Task WaitForCatalogLockAsync(
        int waitingPid, int ownerPid, Task waitingWriter, CancellationToken cancellationToken)
    {
        await using var observer = await fixture.DataSource.OpenConnectionAsync(cancellationToken);
        await using var command = new NpgsqlCommand("""
            SELECT EXISTS (
                SELECT 1 FROM pg_stat_activity
                WHERE pid = @waiting AND wait_event_type = 'Lock'
                  AND @owner = ANY(pg_blocking_pids(pid))
                  AND query ILIKE '%shared_variables%' AND query ILIKE '%FOR UPDATE%')
            """, observer);
        command.Parameters.AddWithValue("waiting", waitingPid);
        command.Parameters.AddWithValue("owner", ownerPid);
        while (!(bool)(await command.ExecuteScalarAsync(cancellationToken))!)
        {
            Assert.False(waitingWriter.IsCompleted, "The competing write must wait on the catalog row lock.");
            await Task.Delay(25, cancellationToken);
        }
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

    private static async Task AddCurrentCatalogReadColumnsAsync(string connectionString)
    {
        // This migration test intentionally keeps the old conditional-wake
        // schema while exercising today's repository. Add only its nullable
        // catalog projection column; do not apply newer migrations or alter the
        // legacy conditional-wake artifacts that this test is asserting.
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand("""
            ALTER TABLE flowbit.shared_variables
            ADD COLUMN "HistoryPrunedAt" timestamp with time zone NULL
            """, connection);
        await command.ExecuteNonQueryAsync();
    }

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
