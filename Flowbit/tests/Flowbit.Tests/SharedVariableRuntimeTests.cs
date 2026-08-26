using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class SharedVariableRuntimeTests(PostgresApiFixture fixture)
{
    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web);

    [Fact]
    public async Task AsyncServiceSnapshotPersistsFrozenSharedInputRevisionSeparatelyFromOutputFence()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var sharedKey = $"tests.fx-rate.{suffix}";
        using (var create = await SendAsync(
                   HttpMethod.Post,
                   "/api/shared-variables",
                   new CreateSharedVariableRequest(
                       sharedKey,
                       WorkflowVariableTypes.Number,
                       IsArray: false,
                       Nullable: false,
                       HasValue: true,
                       JsonSerializer.SerializeToElement(3.75m))))
        {
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        }

        long sharedRevision;
        await using (var catalog = fixture.CreateDbContext())
        {
            sharedRevision = (await catalog.SharedVariables.SingleAsync(variable =>
                variable.Key == sharedKey)).CurrentRevision;
        }

        var workflowId = await CreateWorkflowAsync(
            CreateAsyncServiceReader($"shared-async-service-{suffix}", sharedKey));
        var instance = await StartAsync(workflowId);

        long jobId;
        await using (var before = fixture.CreateDbContext())
        {
            jobId = (await before.WorkflowJobs.SingleAsync(job =>
                job.InstanceId == instance.Id
                && job.Kind == WorkflowJobKinds.AsyncBefore)).Id;
        }

        await ProcessWorkflowJobAsync(jobId, maxActivityCount: 1);

        long snapshotId;
        await using (var completed = fixture.CreateDbContext())
        {
            var job = await completed.WorkflowJobs.SingleAsync(item => item.Id == jobId);
            Assert.Equal(WorkflowJobStatuses.Completed, job.Status);
            snapshotId = Assert.IsType<long>(job.SnapshotId);
            Assert.NotNull((await completed.WorkflowJobSnapshots.SingleAsync(snapshot =>
                snapshot.Id == snapshotId)).SharedVariableRevisionsJson);
        }

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>();
        var snapshotRecord = await jobs.GetSnapshotAsync(
            snapshotId,
            CancellationToken.None);
        Assert.NotNull(snapshotRecord);
        var sharedRevisions = snapshotRecord!.SharedVariableRevisions;
        Assert.NotNull(sharedRevisions);
        Assert.Equal(sharedRevision, Assert.Single(sharedRevisions!).Value);
        Assert.Equal(sharedRevision, sharedRevisions["fxRate"]);
        Assert.Equal(3.75m, snapshotRecord.Variables["fxRate"].GetDecimal());
        Assert.Contains("decision", snapshotRecord.OutputVariableVersions.Keys);
        Assert.DoesNotContain("fxRate", snapshotRecord.OutputVariableVersions.Keys);
    }

    [Fact]
    public async Task AsyncServiceSnapshotFencesSharedOutputByValueRevision()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var sharedKey = $"tests.async-output.{suffix}";
        using (var create = await SendAsync(
                   HttpMethod.Post,
                   "/api/shared-variables",
                   new CreateSharedVariableRequest(
                       sharedKey,
                       WorkflowVariableTypes.String,
                       IsArray: false,
                       Nullable: false,
                       HasValue: true,
                       JsonSerializer.SerializeToElement("initial"))))
        {
            Assert.Equal(HttpStatusCode.Created, create.StatusCode);
        }

        long stagedValueRevision;
        await using (var catalog = fixture.CreateDbContext())
        {
            stagedValueRevision = (await catalog.SharedVariables.SingleAsync(variable =>
                variable.Key == sharedKey)).ValueRevision;
        }

        var workflowId = await CreateWorkflowAsync(
            CreateAsyncServiceWriter($"shared-async-output-{suffix}", sharedKey));
        var instance = await StartAsync(workflowId);
        long jobId;
        await using (var before = fixture.CreateDbContext())
        {
            jobId = (await before.WorkflowJobs.SingleAsync(job =>
                job.InstanceId == instance.Id
                && job.Kind == WorkflowJobKinds.AsyncBefore)).Id;
        }

        await ProcessWorkflowJobAsync(jobId, maxActivityCount: 1);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>();
        long snapshotId;
        await using (var completed = fixture.CreateDbContext())
        {
            snapshotId = Assert.IsType<long>((await completed.WorkflowJobs
                .SingleAsync(item => item.Id == jobId)).SnapshotId);
        }
        var snapshot = Assert.IsType<WorkflowJobSnapshotRecord>(
            await jobs.GetSnapshotAsync(snapshotId, CancellationToken.None));

        Assert.NotNull(snapshot.SharedOutputValueVersions);
        Assert.Equal(
            stagedValueRevision,
            Assert.Single(snapshot.SharedOutputValueVersions!).Value);
        Assert.Equal(stagedValueRevision, snapshot.SharedOutputValueVersions["decision"]);
        Assert.Contains("decision", snapshot.OutputVariableVersions.Keys);
    }

    [Theory]
    [InlineData("description", false)]
    [InlineData("lifecycleRoundTrip", false)]
    [InlineData("identicalValue", false)]
    [InlineData("setValue", true)]
    [InlineData("unsetValue", true)]
    [InlineData("legacyDescription", true)]
    public async Task AsyncServiceFinalizationUsesValueRevisionWithLegacyCatalogFallback(
        string mutation,
        bool expectsConflict)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var sharedKey = $"tests.async-fence-{mutation}.{suffix}";
        await CreateSharedVariableAsync(
            sharedKey,
            WorkflowVariableTypes.String,
            nullable: false,
            JsonSerializer.SerializeToElement("initial"));
        var workflowId = await CreateWorkflowAsync(
            CreateAsyncServiceWriter($"shared-async-fence-{mutation}-{suffix}", sharedKey));
        var instance = await StartAsync(workflowId);

        long jobId;
        await using (var before = fixture.CreateDbContext())
        {
            jobId = (await before.WorkflowJobs.SingleAsync(job =>
                job.InstanceId == instance.Id
                && job.Kind == WorkflowJobKinds.AsyncBefore)).Id;
        }

        var block = fixture.ServiceInvocations.BlockNext("/typed-output-success");
        var processing = ProcessWorkflowJobAsync(jobId, maxActivityCount: 1);
        await block.WaitUntilEnteredAsync(CancellationToken.None);
        try
        {
            if (string.Equals(mutation, "legacyDescription", StringComparison.Ordinal))
            {
                await using var legacy = fixture.CreateDbContext();
                var snapshotId = Assert.IsType<long>((await legacy.WorkflowJobs
                    .SingleAsync(job => job.Id == jobId)).SnapshotId);
                var snapshot = await legacy.WorkflowJobSnapshots
                    .SingleAsync(item => item.Id == snapshotId);
                snapshot.SharedOutputValueVersionsJson = null;
                await legacy.SaveChangesAsync();
            }

            await ApplySharedOutputFenceMutationAsync(
                sharedKey,
                string.Equals(mutation, "legacyDescription", StringComparison.Ordinal)
                    ? "description"
                    : mutation);
        }
        finally
        {
            block.Release();
        }
        await processing;

        await using var completed = fixture.CreateDbContext();
        var job = await completed.WorkflowJobs.SingleAsync(item => item.Id == jobId);
        var persistedInstance = await completed.WorkflowInstances
            .SingleAsync(item => item.Id == instance.Id);
        if (expectsConflict)
        {
            Assert.Equal(WorkflowJobStatuses.Incident, job.Status);
            Assert.Equal(WorkflowInstanceStatuses.Running, persistedInstance.Status);
            Assert.Contains(
                await completed.WorkflowIncidents
                    .Where(item => item.JobId == jobId)
                    .ToListAsync(),
                incident => incident.Type == "output_version_conflict"
                    && incident.Status == WorkflowIncidentStatuses.Open);
        }
        else
        {
            Assert.Equal(WorkflowJobStatuses.Completed, job.Status);
            Assert.Equal(WorkflowInstanceStatuses.Completed, persistedInstance.Status);
            var shared = await completed.SharedVariables
                .Include(item => item.CurrentValue)
                .SingleAsync(item => item.Key == sharedKey);
            Assert.Equal("approved", shared.CurrentValue!.ValueJson!.RootElement.GetString());
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ScriptSharedNullableNullSkipsNonNullRule(bool asyncBefore)
    {
        var suffix = Guid.NewGuid().ToString("N");
        var sharedKey = $"tests.nullable-script.{suffix}";
        await CreateSharedVariableAsync(
            sharedKey,
            WorkflowVariableTypes.String,
            nullable: true,
            JsonSerializer.SerializeToElement("initial"),
            "Len(value) > 0");
        var workflowId = await CreateWorkflowAsync(
            CreateNullableSharedScript(
                $"nullable-script-{asyncBefore}-{suffix}",
                sharedKey,
                asyncBefore));

        var instance = await StartAsync(workflowId);
        if (asyncBefore)
        {
            long jobId;
            await using (var before = fixture.CreateDbContext())
            {
                jobId = (await before.WorkflowJobs.SingleAsync(job =>
                    job.InstanceId == instance.Id
                    && job.Kind == WorkflowJobKinds.AsyncBefore)).Id;
            }
            await ProcessWorkflowJobAsync(jobId, maxActivityCount: 1);
        }

        await using var db = fixture.CreateDbContext();
        Assert.Equal(
            WorkflowInstanceStatuses.Completed,
            (await db.WorkflowInstances.SingleAsync(item => item.Id == instance.Id)).Status);
        var variable = await db.SharedVariables.SingleAsync(item => item.Key == sharedKey);
        var current = await db.SharedVariableCurrentValues.SingleAsync(item =>
            item.SharedVariableId == variable.Id);
        Assert.Equal(JsonValueKind.Null, current.ValueJson!.RootElement.ValueKind);
    }

    [Fact]
    public async Task ScriptSharedValidationFailureFollowsBoundaryWithoutPartialWrites()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var firstKey = $"tests.boundary-first.{suffix}";
        var secondKey = $"tests.boundary-second.{suffix}";
        await CreateSharedVariableAsync(
            firstKey,
            WorkflowVariableTypes.String,
            nullable: false,
            JsonSerializer.SerializeToElement("first-initial"),
            "Len(value) > 0");
        await CreateSharedVariableAsync(
            secondKey,
            WorkflowVariableTypes.String,
            nullable: false,
            JsonSerializer.SerializeToElement("second-initial"),
            "Len(value) > 0");
        var workflowId = await CreateWorkflowAsync(
            CreateBoundarySharedScript(
                $"shared-boundary-{suffix}",
                firstKey,
                secondKey));

        var instance = await StartAsync(workflowId);

        Assert.Equal(WorkflowInstanceStatuses.Completed, instance.Status);
        Assert.Contains(instance.Variables, variable =>
            string.Equals(variable.VariableName, "scriptError", StringComparison.OrdinalIgnoreCase)
            && variable.Value.ValueKind == JsonValueKind.String
            && variable.Value.GetString()!.Contains("failed validation", StringComparison.OrdinalIgnoreCase));
        await using var db = fixture.CreateDbContext();
        var variables = await db.SharedVariables
            .Include(variable => variable.CurrentValue)
            .Where(variable => variable.Key == firstKey || variable.Key == secondKey)
            .ToListAsync();
        var values = variables.ToDictionary(
            variable => variable.Key,
            variable => variable.CurrentValue!.ValueJson!.RootElement.Clone());
        Assert.Equal("first-initial", values[firstKey].GetString());
        Assert.Equal("second-initial", values[secondKey].GetString());
    }

    [Fact]
    public async Task AsyncServiceRejectedSharedStatusFollowsBoundaryWithoutPersistingMappedOutput()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var statusKey = $"tests.service-status.{suffix}";
        await CreateSharedVariableAsync(
            statusKey,
            WorkflowVariableTypes.Number,
            nullable: false,
            JsonSerializer.SerializeToElement(0),
            "value != 200");
        var workflowId = await CreateWorkflowAsync(
            CreateServiceStatusBoundary(
                $"shared-service-status-{suffix}",
                statusKey));
        var instance = await StartAsync(workflowId);

        long jobId;
        await using (var before = fixture.CreateDbContext())
        {
            jobId = (await before.WorkflowJobs.SingleAsync(job =>
                job.InstanceId == instance.Id
                && job.Kind == WorkflowJobKinds.AsyncBefore)).Id;
        }
        await ProcessWorkflowJobAsync(jobId, maxActivityCount: 1);

        await using var db = fixture.CreateDbContext();
        Assert.Equal(
            WorkflowJobStatuses.Completed,
            (await db.WorkflowJobs.SingleAsync(job => job.Id == jobId)).Status);
        Assert.Equal(
            WorkflowInstanceStatuses.Completed,
            (await db.WorkflowInstances.SingleAsync(item => item.Id == instance.Id)).Status);

        var currentValues = await db.InstanceVariableCurrentValues
            .Where(item => item.InstanceId == instance.Id)
            .ToDictionaryAsync(
                item => item.VariableName,
                item => item.ValueJson.RootElement.Clone(),
                StringComparer.OrdinalIgnoreCase);
        Assert.Equal("initial", currentValues["decision"].GetString());
        Assert.Contains(
            "failed validation",
            currentValues["serviceError"].GetString(),
            StringComparison.OrdinalIgnoreCase);

        var shared = await db.SharedVariables
            .Include(item => item.CurrentValue)
            .SingleAsync(item => item.Key == statusKey);
        Assert.Equal(0, shared.CurrentValue!.ValueJson!.RootElement.GetInt32());
        Assert.DoesNotContain(
            await db.SharedVariableRevisions
                .Where(item => item.SharedVariableId == shared.Id)
                .ToListAsync(),
            revision => revision.InstanceId == instance.Id);
        Assert.Equal(
            NodeExecutionCompletionReasons.BoundaryCaught,
            (await db.NodeExecutions.SingleAsync(item =>
                item.InstanceId == instance.Id
                && item.NodeId == 2)).CompletionReason);
    }

    [Fact]
    public async Task ReverseSharedLockOrderIsRejectedAndLegacyDefinitionFailsClosedAtRuntime()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var lowerKey = $"tests.lock.a.{suffix}";
        var higherKey = $"tests.lock.z.{suffix}";
        await CreateSharedVariableAsync(
            lowerKey,
            WorkflowVariableTypes.String,
            nullable: false,
            JsonSerializer.SerializeToElement("lower-initial"));
        await CreateSharedVariableAsync(
            higherKey,
            WorkflowVariableTypes.String,
            nullable: false,
            JsonSerializer.SerializeToElement("higher-initial"));
        var definition = CreateSharedLockOrderWorkflow(
            $"shared-lock-legacy-{suffix}",
            firstKey: higherKey,
            secondKey: lowerKey);

        using (var rejected = await SendAsync(
                   HttpMethod.Post,
                   "/api/workflows",
                   new CreateWorkflowRequest(definition, true)))
        {
            Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
            var body = await rejected.Content.ReadAsStringAsync();
            Assert.Contains("monotonic", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains(lowerKey, body, StringComparison.Ordinal);
            Assert.Contains(higherKey, body, StringComparison.Ordinal);
        }

        long legacyDefinitionId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var repository = scope.ServiceProvider
                .GetRequiredService<IWorkflowDefinitionRepository>();
            var legacy = await repository.AddAsync(
                definition.Name,
                definition,
                isPublished: true,
                CancellationToken.None);
            legacyDefinitionId = legacy.Id;
        }

        using (var start = await SendAsync(
                   HttpMethod.Post,
                   "/api/instances?detail=full",
                   new StartInstanceRequest(
                       legacyDefinitionId,
                       null,
                       null,
                       new Dictionary<string, JsonElement>())))
        {
            Assert.Equal(HttpStatusCode.Conflict, start.StatusCode);
            var body = await start.Content.ReadAsStringAsync();
            Assert.Contains("cannot execute safely", body, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("monotonic", body, StringComparison.OrdinalIgnoreCase);
        }

        await using var verify = fixture.CreateDbContext();
        Assert.False(await verify.WorkflowInstances.AnyAsync(instance =>
            instance.WorkflowDefinitionId == legacyDefinitionId));
    }

    [Fact]
    public async Task LegacyAllocatorPrelocksDefinitionKeysBeforeFirstWorkflowWrite()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var lowerKey = $"tests.prelock.a.{suffix}";
        var higherKey = $"tests.prelock.z.{suffix}";
        await CreateSharedVariableAsync(
            lowerKey,
            WorkflowVariableTypes.String,
            nullable: false,
            JsonSerializer.SerializeToElement("lower-initial"));
        await CreateSharedVariableAsync(
            higherKey,
            WorkflowVariableTypes.String,
            nullable: false,
            JsonSerializer.SerializeToElement("higher-initial"));
        var workflowId = await CreateWorkflowAsync(
            CreateSharedLockOrderWorkflow(
                $"shared-prelock-{suffix}",
                firstKey: lowerKey,
                secondKey: higherKey));

        string originalAllocatorMode;
        await using (var allocator = fixture.CreateDbContext())
        {
            originalAllocatorMode = await allocator.SharedVariableRevisionStates
                .Where(state => state.Id == 1)
                .Select(state => state.AllocatorMode)
                .SingleAsync();
            if (string.Equals(
                    originalAllocatorMode,
                    "sequence",
                    StringComparison.Ordinal))
            {
                // Another test may already have exercised the irreversible
                // cutover function. Re-enter legacy mode only for this
                // collection-serialized compatibility scenario; the function
                // remains sequence-backed and mode is restored in finally.
                await allocator.Database.ExecuteSqlRawAsync(
                    "UPDATE flowbit.shared_variable_revision_state "
                    + "SET \"AllocatorMode\" = 'legacy' WHERE \"Id\" = 1");
            }
        }
        try
        {
            await using var legacyWriter = fixture.CreateDbContext();
            await using var legacyTransaction =
                await legacyWriter.Database.BeginTransactionAsync();
            _ = await legacyWriter.SharedVariables
                .FromSqlInterpolated(
                    $"""SELECT * FROM flowbit.shared_variables WHERE "Key" = {higherKey} FOR UPDATE""")
                .SingleAsync();

            var startTask = StartAsync(workflowId);
            try
            {
                await WaitForBlockedSharedVariableLockAsync();

                // This simulates a mixed-version singleton writer: it already
                // owns the higher catalog key and now takes the legacy allocator.
                // Directly advance the row because a prior suite test may have
                // permanently replaced the allocator function with nextval.
                await legacyWriter.Database.ExecuteSqlRawAsync(
                    "UPDATE flowbit.shared_variable_revision_state "
                    + "SET \"LastRevision\" = \"LastRevision\" + 1 WHERE \"Id\" = 1");
                await legacyTransaction.CommitAsync();
            }
            catch
            {
                await legacyTransaction.RollbackAsync();
                throw;
            }

            var instance = await startTask.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal(WorkflowInstanceStatuses.Completed, instance.Status);
            await using var verify = fixture.CreateDbContext();
            var values = await verify.SharedVariables
                .Include(variable => variable.CurrentValue)
                .Where(variable => variable.Key == lowerKey || variable.Key == higherKey)
                .ToDictionaryAsync(
                    variable => variable.Key,
                    variable => variable.CurrentValue!.ValueJson!.RootElement.GetString());
            Assert.Equal("first-updated", values[lowerKey]);
            Assert.Equal("second-updated", values[higherKey]);
        }
        finally
        {
            if (string.Equals(
                    originalAllocatorMode,
                    "sequence",
                    StringComparison.Ordinal))
            {
                await using var allocator = fixture.CreateDbContext();
                await allocator.Database.ExecuteSqlRawAsync(
                    "UPDATE flowbit.shared_variable_revision_state "
                    + "SET \"AllocatorMode\" = 'sequence' WHERE \"Id\" = 1");
            }
        }
    }

    private async Task CreateSharedVariableAsync(
        string key,
        string dataType,
        bool nullable,
        JsonElement value,
        string? validation = null)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/shared-variables",
            new CreateSharedVariableRequest(
                key,
                dataType,
                IsArray: false,
                nullable,
                HasValue: true,
                value,
                validation));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
    }

    private async Task ApplySharedOutputFenceMutationAsync(
        string key,
        string mutation)
    {
        if (string.Equals(mutation, "lifecycleRoundTrip", StringComparison.Ordinal))
        {
            await using var db = fixture.CreateDbContext();
            var variable = await db.SharedVariables.SingleAsync(item => item.Key == key);
            var initialValueRevision = variable.ValueRevision;
            var archiveRevision = await db.Database.SqlQueryRaw<long>(
                    "SELECT flowbit.next_shared_variable_revision() AS \"Value\"")
                .SingleAsync();
            var reactivateRevision = await db.Database.SqlQueryRaw<long>(
                    "SELECT flowbit.next_shared_variable_revision() AS \"Value\"")
                .SingleAsync();
            var now = DateTimeOffset.UtcNow;
            db.SharedVariableRevisions.AddRange(
                new SharedVariableRevisionEntity
                {
                    SharedVariableId = variable.Id,
                    Revision = archiveRevision,
                    Operation = SharedVariableOperations.Archive,
                    ValueChanged = false,
                    HasValue = false,
                    CallerKind = SharedVariableCallerKinds.System,
                    CallerId = "async-fence-test",
                    Source = SharedVariableSources.System,
                    CreatedAt = now
                },
                new SharedVariableRevisionEntity
                {
                    SharedVariableId = variable.Id,
                    Revision = reactivateRevision,
                    Operation = SharedVariableOperations.Reactivate,
                    ValueChanged = false,
                    HasValue = false,
                    CallerKind = SharedVariableCallerKinds.System,
                    CallerId = "async-fence-test",
                    Source = SharedVariableSources.System,
                    CreatedAt = now
                });
            variable.Status = SharedVariableStatuses.Active;
            variable.ArchivedAt = null;
            variable.CurrentRevision = reactivateRevision;
            variable.UpdatedAt = now;
            await db.SaveChangesAsync();
            Assert.Equal(initialValueRevision, variable.ValueRevision);
            return;
        }

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<ISharedVariableRepository>();
        var current = Assert.IsType<SharedVariableRecord>(await repository.GetByKeyAsync(
            key,
            includeArchived: true,
            CancellationToken.None));
        var value = mutation switch
        {
            "description" or "identicalValue" => current.Value?.Clone(),
            "setValue" => JsonSerializer.SerializeToElement("concurrent"),
            "unsetValue" => null,
            _ => throw new ArgumentOutOfRangeException(nameof(mutation), mutation, null)
        };
        var result = Assert.IsType<SharedVariableMutationResult>(await repository.WriteAsync(
            new SharedVariableWriteCommand(
                key,
                value,
                DeleteValue: string.Equals(mutation, "unsetValue", StringComparison.Ordinal),
                ExpectedRevision: current.Revision,
                new SharedVariableCallerRecord(
                    SharedVariableCallerKinds.System,
                    "async-fence-test",
                    [],
                    []),
                SharedVariableSources.System,
                Description: string.Equals(mutation, "description", StringComparison.Ordinal)
                    ? "metadata changed while the service was running"
                    : null,
                DescriptionOnly: string.Equals(mutation, "description", StringComparison.Ordinal)),
            CancellationToken.None));
        if (mutation is "description" or "identicalValue")
        {
            Assert.Equal(current.ValueRevision, result.Variable.ValueRevision);
        }
        else
        {
            Assert.NotEqual(current.ValueRevision, result.Variable.ValueRevision);
        }
    }

    private async Task WaitForBlockedSharedVariableLockAsync()
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        await using var observer = fixture.CreateDbContext();
        await observer.Database.OpenConnectionAsync();
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var command = observer.Database.GetDbConnection().CreateCommand();
            command.CommandText =
                """
                SELECT count(*)
                FROM pg_stat_activity
                WHERE wait_event_type = 'Lock'
                  AND position('FLOWBIT.SHARED_VARIABLES' in upper(query)) > 0
                  AND position('FOR UPDATE' in upper(query)) > 0
                """;
            var blocked = Convert.ToInt32(await command.ExecuteScalarAsync());
            if (blocked > 0)
            {
                return;
            }
            await Task.Delay(25);
        }

        throw new TimeoutException(
            "The workflow start did not block on the shared dependency row lock.");
    }

    private async Task ProcessWorkflowJobAsync(long jobId, int maxActivityCount = 0)
    {
        await using (var db = fixture.CreateDbContext())
        {
            await db.WorkflowJobs
                .Where(job => job.Id == jobId)
                .ExecuteUpdateAsync(setters => setters
                    .SetProperty(job => job.Priority, int.MaxValue)
                    .SetProperty(job => job.DueAt, DateTimeOffset.UtcNow));
        }
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var jobs = scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>();
        var processor = scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>();
        var lease = Assert.Single(await jobs.LeaseRunnableAsync(
            new WorkflowJobLeaseRequest(
                $"shared-runtime-job:{Guid.NewGuid():N}",
                MaxCount: 1,
                MaxActivityCount: maxActivityCount,
                MaxPerInstance: 4,
                LeaseDuration: TimeSpan.FromMinutes(1)),
            CancellationToken.None));
        Assert.Equal(jobId, lease.Job.Id);
        await processor.ProcessAsync(lease, CancellationToken.None);
    }

    private async Task<long> CreateWorkflowAsync(WorkflowModel definition)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/workflows",
            new CreateWorkflowRequest(definition, true));
        Assert.True(
            response.StatusCode == HttpStatusCode.Created,
            $"Expected workflow creation to return 201 but received {(int)response.StatusCode}: "
            + await response.Content.ReadAsStringAsync());
        return (await ReadAsync<WorkflowDetailDto>(response)).Id;
    }

    private async Task<InstanceDetailDto> StartAsync(long workflowId)
    {
        using var response = await SendAsync(
            HttpMethod.Post,
            "/api/instances?detail=full",
            new StartInstanceRequest(
                workflowId,
                null,
                null,
                new Dictionary<string, JsonElement>()));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        return await ReadAsync<InstanceDetailDto>(response);
    }

    private Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        object? body = null,
        string user = "shared-runtime-admin")
    {
        var request = new HttpRequestMessage(method, path);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body, options: JsonOptions);
        }
        ApiTestAuth.Authorize(request, user, "admin");
        return fixture.Client.SendAsync(request);
    }

    private static async Task<T> ReadAsync<T>(HttpResponseMessage response) =>
        await response.Content.ReadFromJsonAsync<T>(JsonOptions)
        ?? throw new InvalidOperationException("Response body was empty.");

    private static WorkflowModel CreateAsyncServiceReader(
        string workflowKey,
        string sharedKey) => new()
    {
        Id = workflowKey,
        Name = workflowKey,
        InitialEventId = 1,
        Variables =
        [
            new VariableModel
            {
                Id = 1,
                Name = "fxRate",
                Scope = VariableScopes.Shared,
                SharedKey = sharedKey,
                Access = SharedVariableAccessModes.Read,
                DataType = WorkflowVariableTypes.Number,
                Nullable = false
            },
            new VariableModel
            {
                Id = 2,
                Name = "decision",
                DataType = WorkflowVariableTypes.String,
                DefaultValue = JsonSerializer.SerializeToElement("initial")
            }
        ],
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Read rate",
                Type = BpmnFlowNodeTypes.ServiceTask,
                AsyncBefore = true,
                Service = new ServiceTaskModel
                {
                    Url = "https://tests.local/typed-output-success",
                    Method = "GET",
                    Headers =
                    [
                        new ServiceHeaderModel { Name = "X-Fx-Rate", Value = "${fxRate}" }
                    ],
                    OutputMappings =
                    [
                        new ServiceOutputMappingModel
                        {
                            Variable = "decision",
                            Path = "result.decision",
                            Required = true,
                            DataType = WorkflowVariableTypes.String
                        }
                    ]
                }
            },
            new FlowNodeModel { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, Name = "Fetch", SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, Name = "Done", SourceRef = 2, TargetRef = 3 }
        ]
    };

    private static WorkflowModel CreateAsyncServiceWriter(
        string workflowKey,
        string sharedKey) => new()
    {
        Id = workflowKey,
        Name = workflowKey,
        InitialEventId = 1,
        Variables =
        [
            new VariableModel
            {
                Id = 1,
                Name = "decision",
                Scope = VariableScopes.Shared,
                SharedKey = sharedKey,
                Access = SharedVariableAccessModes.ReadWrite,
                DataType = WorkflowVariableTypes.String,
                Nullable = false
            }
        ],
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Write decision",
                Type = BpmnFlowNodeTypes.ServiceTask,
                AsyncBefore = true,
                Service = new ServiceTaskModel
                {
                    Url = "https://tests.local/typed-output-success",
                    Method = "GET",
                    OutputMappings =
                    [
                        new ServiceOutputMappingModel
                        {
                            Variable = "decision",
                            Path = "result.decision",
                            Required = true,
                            DataType = WorkflowVariableTypes.String
                        }
                    ]
                }
            },
            new FlowNodeModel { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 }
        ]
    };

    private static WorkflowModel CreateServiceStatusBoundary(
        string workflowKey,
        string statusKey) => new()
    {
        Id = workflowKey,
        Name = workflowKey,
        InitialEventId = 1,
        Variables =
        [
            new VariableModel
            {
                Id = 1,
                Name = "decision",
                DataType = WorkflowVariableTypes.String,
                Nullable = false,
                DefaultValue = JsonSerializer.SerializeToElement("initial")
            },
            new VariableModel
            {
                Id = 2,
                Name = "httpStatus",
                Scope = VariableScopes.Shared,
                SharedKey = statusKey,
                Access = SharedVariableAccessModes.ReadWrite,
                DataType = WorkflowVariableTypes.Number,
                Nullable = false,
                Validation = "value != 200"
            },
            new VariableModel
            {
                Id = 3,
                Name = "serviceError",
                DataType = WorkflowVariableTypes.String,
                Nullable = true
            }
        ],
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Reject status",
                Type = BpmnFlowNodeTypes.ServiceTask,
                AsyncBefore = true,
                Service = new ServiceTaskModel
                {
                    Url = "https://tests.local/typed-output-success",
                    Method = "GET",
                    StatusVariable = "httpStatus",
                    OutputMappings =
                    [
                        new ServiceOutputMappingModel
                        {
                            Variable = "decision",
                            Path = "result.decision",
                            Required = true,
                            DataType = WorkflowVariableTypes.String
                        }
                    ]
                }
            },
            new FlowNodeModel { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent },
            new FlowNodeModel
            {
                Id = 4,
                Name = "Service error",
                Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
                AttachedToRef = 2,
                ErrorVariable = "serviceError"
            }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
            new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 3 }
        ]
    };

    private static WorkflowModel CreateNullableSharedScript(
        string workflowKey,
        string sharedKey,
        bool asyncBefore) => new()
    {
        Id = workflowKey,
        Name = workflowKey,
        InitialEventId = 1,
        Variables =
        [
            new VariableModel
            {
                Id = 1,
                Name = "result",
                Scope = VariableScopes.Shared,
                SharedKey = sharedKey,
                Access = SharedVariableAccessModes.ReadWrite,
                DataType = WorkflowVariableTypes.String,
                Nullable = true,
                Validation = "Len(value) > 0"
            }
        ],
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Write null",
                Type = BpmnFlowNodeTypes.ScriptTask,
                AsyncBefore = asyncBefore,
                ScriptFormat = ScriptFormats.NCalc,
                Assignments =
                [
                    new AssignmentModel { Variable = "result", Expression = "null" }
                ]
            },
            new FlowNodeModel { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 }
        ]
    };

    private static WorkflowModel CreateBoundarySharedScript(
        string workflowKey,
        string firstKey,
        string secondKey) => new()
    {
        Id = workflowKey,
        Name = workflowKey,
        InitialEventId = 1,
        Variables =
        [
            new VariableModel
            {
                Id = 1,
                Name = "first",
                Scope = VariableScopes.Shared,
                SharedKey = firstKey,
                Access = SharedVariableAccessModes.ReadWrite,
                DataType = WorkflowVariableTypes.String,
                Nullable = false,
                Validation = "Len(value) > 0"
            },
            new VariableModel
            {
                Id = 2,
                Name = "second",
                Scope = VariableScopes.Shared,
                SharedKey = secondKey,
                Access = SharedVariableAccessModes.ReadWrite,
                DataType = WorkflowVariableTypes.String,
                Nullable = false,
                Validation = "Len(value) > 0"
            },
            new VariableModel
            {
                Id = 3,
                Name = "scriptError",
                DataType = WorkflowVariableTypes.String,
                Nullable = true
            }
        ],
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Reject staged batch",
                Type = BpmnFlowNodeTypes.ScriptTask,
                ScriptFormat = ScriptFormats.NCalc,
                Assignments =
                [
                    new AssignmentModel { Variable = "first", Expression = "'changed'" },
                    new AssignmentModel { Variable = "second", Expression = "''" }
                ]
            },
            new FlowNodeModel { Id = 3, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent },
            new FlowNodeModel
            {
                Id = 4,
                Name = "Script error",
                Type = BpmnFlowNodeTypes.ErrorBoundaryEvent,
                AttachedToRef = 2,
                ErrorVariable = "scriptError"
            }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
            new SequenceFlowModel { Id = 401, SourceRef = 4, TargetRef = 3 }
        ]
    };

    private static WorkflowModel CreateSharedLockOrderWorkflow(
        string workflowKey,
        string firstKey,
        string secondKey) => new()
    {
        Id = workflowKey,
        Name = workflowKey,
        InitialEventId = 1,
        Variables =
        [
            new VariableModel
            {
                Id = 1,
                Name = "firstShared",
                Scope = VariableScopes.Shared,
                SharedKey = firstKey,
                Access = SharedVariableAccessModes.ReadWrite,
                DataType = WorkflowVariableTypes.String,
                Nullable = false
            },
            new VariableModel
            {
                Id = 2,
                Name = "secondShared",
                Scope = VariableScopes.Shared,
                SharedKey = secondKey,
                Access = SharedVariableAccessModes.ReadWrite,
                DataType = WorkflowVariableTypes.String,
                Nullable = false
            }
        ],
        FlowNodes =
        [
            new FlowNodeModel { Id = 1, Name = "Start", Type = BpmnFlowNodeTypes.StartEvent },
            new FlowNodeModel
            {
                Id = 2,
                Name = "Write first",
                Type = BpmnFlowNodeTypes.ScriptTask,
                ScriptFormat = ScriptFormats.NCalc,
                Assignments =
                [
                    new AssignmentModel
                    {
                        Variable = "firstShared",
                        Expression = "'first-updated'"
                    }
                ]
            },
            new FlowNodeModel
            {
                Id = 3,
                Name = "Write second",
                Type = BpmnFlowNodeTypes.ScriptTask,
                ScriptFormat = ScriptFormats.NCalc,
                Assignments =
                [
                    new AssignmentModel
                    {
                        Variable = "secondShared",
                        Expression = "'second-updated'"
                    }
                ]
            },
            new FlowNodeModel { Id = 4, Name = "Done", Type = BpmnFlowNodeTypes.EndEvent }
        ],
        SequenceFlows =
        [
            new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
            new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 },
            new SequenceFlowModel { Id = 301, SourceRef = 3, TargetRef = 4 }
        ]
    };
}
