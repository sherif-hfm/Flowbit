extern alias FlowbitWorker;

using System.Reflection;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using TimerStartReconciliationService = FlowbitWorker::Flowbit.Worker.TimerStartReconciliationService;
using WorkerOptions = FlowbitWorker::Flowbit.Worker.WorkerOptions;
using WorkerTelemetry = FlowbitWorker::Flowbit.Worker.WorkerTelemetry;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class TimerStartReconciliationServiceTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task ReconciliationLeaderCreatesOneDurableOccurrenceForPublishedDefault()
    {
        var workflowKey = $"timer-reconcile-{Guid.NewGuid():N}";
        await using (var context = fixture.CreateDbContext())
        using (var cache = new MemoryCache(new MemoryCacheOptions()))
        {
            var definitions = new WorkflowDefinitionRepository(context, cache);
            await definitions.AddAsync(
                "Reconciliation timer",
                new WorkflowModel
                {
                    Id = workflowKey,
                    Name = "Reconciliation timer",
                    FlowNodes =
                    [
                        new FlowNodeModel
                        {
                            Id = 1,
                            Name = "Scheduled start",
                            Type = BpmnFlowNodeTypes.TimerStartEvent,
                            Timer = new TimerDefinitionModel { TimeDuration = "PT1H" }
                        }
                    ]
                },
                true,
                CancellationToken.None);
        }

        var options = new WorkerOptions { TimerStartReconcileBatchSize = 1000 };
        using var telemetry = new WorkerTelemetry();
        var service = new TimerStartReconciliationService(
            fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            options,
            TimeProvider.System,
            telemetry,
            NullLogger<TimerStartReconciliationService>.Instance);
        var reconcile = typeof(TimerStartReconciliationService).GetMethod(
            "ReconcileAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("ReconcileAsync was not found.");

        var invocation = reconcile.Invoke(service, [CancellationToken.None]);
        await Assert.IsAssignableFrom<Task>(invocation);

        await using var verification = fixture.CreateDbContext();
        var subscription = await verification.TimerSubscriptions
            .AsNoTracking()
            .SingleAsync(row => row.WorkflowKey == workflowKey);
        Assert.Equal(TimerSubscriptionStatuses.Active, subscription.Status);
        var occurrence = await verification.WorkflowJobs
            .AsNoTracking()
            .SingleAsync(job => job.TimerSubscriptionId == subscription.Id);
        Assert.Equal(WorkflowJobKinds.TimerStart, occurrence.Kind);
        Assert.Equal(WorkflowJobStatuses.Queued, occurrence.Status);
        Assert.Equal(subscription.NextDueAt, occurrence.ScheduledOccurrenceAt);
    }

    [Fact]
    public async Task TimerStartRejectsStoredLegacySharedConditionalBeforeInstanceCreation()
    {
        var workflowKey = $"timer-shared-conditional-{Guid.NewGuid():N}";
        var definition = new WorkflowModel
        {
            Id = workflowKey,
            Name = "Legacy shared conditional timer",
            InitialEventId = 1,
            Variables =
            [
                new VariableModel
                {
                    Id = 1,
                    Name = "releaseFlag",
                    Scope = VariableScopes.Shared,
                    SharedKey = $"tests.{workflowKey}",
                    Access = SharedVariableAccessModes.Read,
                    DataType = WorkflowVariableTypes.Boolean,
                    Nullable = false
                }
            ],
            FlowNodes =
            [
                new FlowNodeModel
                {
                    Id = 1,
                    Name = "Scheduled start",
                    Type = BpmnFlowNodeTypes.TimerStartEvent,
                    Timer = new TimerDefinitionModel { TimeDuration = "PT1H" }
                },
                new FlowNodeModel
                {
                    Id = 2,
                    Name = "Wait for release",
                    Type = BpmnFlowNodeTypes.IntermediateConditionalCatchEvent,
                    Conditional = new ConditionalDefinitionModel
                    {
                        Condition = "releaseFlag == true",
                        DeliveryMode = ConditionalEventDeliveryModes.Atomic
                    }
                },
                new FlowNodeModel
                {
                    Id = 3,
                    Name = "Done",
                    Type = BpmnFlowNodeTypes.EndEvent
                }
            ],
            SequenceFlows =
            [
                new SequenceFlowModel { Id = 101, SourceRef = 1, TargetRef = 2 },
                new SequenceFlowModel { Id = 201, SourceRef = 2, TargetRef = 3 }
            ]
        };

        try
        {
            await using (var seed = fixture.CreateDbContext())
            {
                seed.WorkflowDefinitions.Add(new WorkflowDefinitionEntity
                {
                    Name = definition.Name,
                    WorkflowKey = workflowKey,
                    Version = 1,
                    Definition = definition,
                    IsPublished = true,
                    IsDefault = true,
                    DefaultActivationId = Guid.NewGuid(),
                    DefaultActivatedAt = DateTimeOffset.UtcNow,
                    CreatedAt = DateTimeOffset.UtcNow
                });
                await seed.SaveChangesAsync();
            }

            var options = new WorkerOptions { TimerStartReconcileBatchSize = 1000 };
            using var telemetry = new WorkerTelemetry();
            var service = new TimerStartReconciliationService(
                fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
                options,
                TimeProvider.System,
                telemetry,
                NullLogger<TimerStartReconciliationService>.Instance);
            var reconcile = typeof(TimerStartReconciliationService).GetMethod(
                "ReconcileAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("ReconcileAsync was not found.");
            await Assert.IsAssignableFrom<Task>(
                reconcile.Invoke(service, [CancellationToken.None]));

            long jobId;
            await using (var promote = fixture.CreateDbContext())
            {
                var job = await promote.WorkflowJobs.SingleAsync(candidate =>
                    candidate.WorkflowKey == workflowKey
                    && candidate.Kind == WorkflowJobKinds.TimerStart);
                jobId = job.Id;
                job.Priority = int.MaxValue;
                job.DueAt = DateTimeOffset.UtcNow;
                job.MaxAttempts = 1;
                job.RetryDelays = [];
                await promote.SaveChangesAsync();
            }

            await using (var scope = fixture.Factory.Services.CreateAsyncScope())
            {
                var jobs = scope.ServiceProvider.GetRequiredService<IWorkflowJobRepository>();
                var lease = Assert.Single(await jobs.LeaseRunnableAsync(
                    new WorkflowJobLeaseRequest(
                        $"timer-shared-conditional-test:{Guid.NewGuid():N}",
                        MaxCount: 1,
                        MaxActivityCount: 0,
                        MaxPerInstance: 4,
                        LeaseDuration: TimeSpan.FromMinutes(1)),
                    CancellationToken.None));
                Assert.Equal(jobId, lease.Job.Id);
                var processor = scope.ServiceProvider.GetRequiredService<IWorkflowJobProcessor>();
                await processor.ProcessAsync(lease, CancellationToken.None);
            }

            await using var verify = fixture.CreateDbContext();
            Assert.False(await verify.WorkflowInstances.AnyAsync(instance =>
                instance.WorkflowKey == workflowKey));
            var incident = await verify.WorkflowIncidents.SingleAsync(candidate =>
                candidate.JobId == jobId);
            Assert.Equal("job_execution_failed", incident.Type);
            Assert.Contains(
                "cannot reference shared variable 'releaseFlag'",
                incident.Details,
                StringComparison.Ordinal);
        }
        finally
        {
            await using var cleanup = fixture.CreateDbContext();
            await cleanup.WorkflowIncidents
                .Where(incident => incident.WorkflowKey == workflowKey)
                .ExecuteDeleteAsync();
            await cleanup.WorkflowJobs
                .Where(job => job.WorkflowKey == workflowKey)
                .ExecuteDeleteAsync();
            await cleanup.TimerSubscriptions
                .Where(subscription => subscription.WorkflowKey == workflowKey)
                .ExecuteDeleteAsync();
            await cleanup.WorkflowInstances
                .Where(instance => instance.WorkflowKey == workflowKey)
                .ExecuteDeleteAsync();
            await cleanup.WorkflowDefinitions
                .Where(item => item.WorkflowKey == workflowKey)
                .ExecuteDeleteAsync();
        }
    }

    [Fact]
    public async Task ReconciliationScansOnlyFamiliesThatCanOwnTimerStarts()
    {
        // Lowercase alphanumeric keys sort the same under every collation.
        var prefix = $"zztimerscan{Guid.NewGuid():N}";
        var manualKey = prefix + "a";
        var timerKey = prefix + "b";
        try
        {
            await AddPublishedDefinitionAsync(manualKey, ManualStartNode());
            await AddPublishedDefinitionAsync(timerKey, TimerStartNode());
            using var telemetry = new WorkerTelemetry();
            var service = NewService(batchSize: 1, telemetry);

            // One family per pass, starting just before both keys. The manual
            // family sorts first but cannot own a timer start, so it must not
            // consume the pass.
            SetCursor(service, prefix);
            await ReconcileOnceAsync(service);

            await using var verification = fixture.CreateDbContext();
            var subscription = await verification.TimerSubscriptions
                .AsNoTracking()
                .SingleAsync(row => row.WorkflowKey == timerKey);
            Assert.Equal(TimerSubscriptionStatuses.Active, subscription.Status);
            Assert.False(await verification.TimerSubscriptions
                .AnyAsync(row => row.WorkflowKey == manualKey));
            Assert.Equal(timerKey, CursorField.GetValue(service));
        }
        finally
        {
            await DeleteFamiliesAsync(manualKey, timerKey);
        }
    }

    [Fact]
    public async Task ReconciliationCancelsATimerStartWhoseDefaultNoLongerHasOne()
    {
        var prefix = $"zztimerstale{Guid.NewGuid():N}";
        var workflowKey = prefix + "a";
        try
        {
            var timerVersionId = await AddPublishedDefinitionAsync(workflowKey, TimerStartNode());
            using var telemetry = new WorkerTelemetry();
            var service = NewService(batchSize: 1, telemetry);
            SetCursor(service, prefix);
            await ReconcileOnceAsync(service);

            long subscriptionId;
            await using (var armed = fixture.CreateDbContext())
            {
                var subscription = await armed.TimerSubscriptions
                    .AsNoTracking()
                    .SingleAsync(row => row.WorkflowKey == workflowKey);
                Assert.Equal(TimerSubscriptionStatuses.Active, subscription.Status);
                subscriptionId = subscription.Id;
                Assert.True(await armed.WorkflowJobs.AnyAsync(job =>
                    job.TimerSubscriptionId == subscriptionId
                    && job.Status == WorkflowJobStatuses.Queued));
            }

            // Switch the default without the repository's timer-start
            // retirement, as in the crash window reconciliation repairs. The
            // new default has no timer start, so only the open subscription
            // keeps this family in the reconciliation scan.
            var manualVersionId = await AddPublishedDefinitionAsync(workflowKey, ManualStartNode());
            await using (var switchDefault = fixture.CreateDbContext())
            {
                var activationId = Guid.NewGuid();
                var activatedAt = DateTimeOffset.UtcNow;
                await switchDefault.WorkflowDefinitions
                    .Where(row => row.Id == timerVersionId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(row => row.IsDefault, false)
                        .SetProperty(row => row.DefaultActivationId, (Guid?)null)
                        .SetProperty(row => row.DefaultActivatedAt, (DateTimeOffset?)null));
                await switchDefault.WorkflowDefinitions
                    .Where(row => row.Id == manualVersionId)
                    .ExecuteUpdateAsync(setters => setters
                        .SetProperty(row => row.IsDefault, true)
                        .SetProperty(row => row.DefaultActivationId, (Guid?)activationId)
                        .SetProperty(row => row.DefaultActivatedAt, (DateTimeOffset?)activatedAt));
            }

            SetCursor(service, prefix);
            await ReconcileOnceAsync(service);

            await using var verification = fixture.CreateDbContext();
            var stale = await verification.TimerSubscriptions
                .AsNoTracking()
                .SingleAsync(row => row.Id == subscriptionId);
            Assert.Equal(TimerSubscriptionStatuses.Cancelled, stale.Status);
            Assert.False(await verification.WorkflowJobs.AnyAsync(job =>
                job.TimerSubscriptionId == subscriptionId
                && job.Status != WorkflowJobStatuses.Cancelled));
            Assert.False(await verification.TimerSubscriptions.AnyAsync(row =>
                row.WorkflowKey == workflowKey
                && row.Status == TimerSubscriptionStatuses.Active));
        }
        finally
        {
            await DeleteFamiliesAsync(workflowKey);
        }
    }

    [Fact]
    public async Task LaterPassesReadOnlyKeyColumnsForAlreadyClassifiedDefaults()
    {
        var prefix = $"zztimerclassify{Guid.NewGuid():N}";
        var timerKey = prefix + "a";
        try
        {
            await AddPublishedDefinitionAsync(timerKey, TimerStartNode());
            using var telemetry = new WorkerTelemetry();
            var service = NewService(batchSize: 1, telemetry);

            fixture.CommandCounter.Reset(captureReaderCommandTexts: true);
            SetCursor(service, prefix);
            await ReconcileOnceAsync(service);
            var firstPass = fixture.CommandCounter.ReaderCommandTexts;
            fixture.CommandCounter.Reset(captureReaderCommandTexts: true);
            SetCursor(service, prefix);
            await ReconcileOnceAsync(service);
            var secondPass = fixture.CommandCounter.ReaderCommandTexts;
            fixture.CommandCounter.Reset();

            // Reading a definition document decompresses it. The first pass
            // classifies each newly observed default once; afterwards only the
            // bounded batch query, which locks its rows, reads documents.
            Assert.Contains(firstPass, ReadsDefinitionOutsideLockedBatch);
            Assert.DoesNotContain(secondPass, ReadsDefinitionOutsideLockedBatch);
            Assert.Contains(
                secondPass,
                text => text.Contains("FOR SHARE", StringComparison.Ordinal));
            await using var verification = fixture.CreateDbContext();
            Assert.Equal(
                TimerSubscriptionStatuses.Active,
                (await verification.TimerSubscriptions
                    .AsNoTracking()
                    .SingleAsync(row => row.WorkflowKey == timerKey)).Status);
        }
        finally
        {
            await DeleteFamiliesAsync(timerKey);
        }
    }

    private static bool ReadsDefinitionOutsideLockedBatch(string commandText) =>
        commandText.Contains("\"Definition\"", StringComparison.Ordinal)
        && !commandText.Contains("FOR SHARE", StringComparison.Ordinal);

    private static readonly MethodInfo ReconcileMethod =
        typeof(TimerStartReconciliationService).GetMethod(
            "ReconcileAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("ReconcileAsync was not found.");

    private static readonly FieldInfo CursorField =
        typeof(TimerStartReconciliationService).GetField(
            "_familyCursor",
            BindingFlags.Instance | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("_familyCursor was not found.");

    private TimerStartReconciliationService NewService(
        int batchSize,
        WorkerTelemetry telemetry) =>
        new(
            fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>(),
            new WorkerOptions { TimerStartReconcileBatchSize = batchSize },
            TimeProvider.System,
            telemetry,
            NullLogger<TimerStartReconciliationService>.Instance);

    private static void SetCursor(TimerStartReconciliationService service, string cursor) =>
        CursorField.SetValue(service, cursor);

    private static Task ReconcileOnceAsync(TimerStartReconciliationService service) =>
        Assert.IsAssignableFrom<Task>(ReconcileMethod.Invoke(service, [CancellationToken.None]));

    private async Task<long> AddPublishedDefinitionAsync(string workflowKey, FlowNodeModel entry)
    {
        await using var context = fixture.CreateDbContext();
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var definitions = new WorkflowDefinitionRepository(context, cache);
        var record = await definitions.AddAsync(
            workflowKey,
            new WorkflowModel
            {
                Id = workflowKey,
                Name = workflowKey,
                FlowNodes = [entry]
            },
            true,
            CancellationToken.None);
        return record.Id;
    }

    private static FlowNodeModel TimerStartNode() => new()
    {
        Id = 1,
        Name = "Scheduled start",
        Type = BpmnFlowNodeTypes.TimerStartEvent,
        Timer = new TimerDefinitionModel { TimeDuration = "PT1H" }
    };

    private static FlowNodeModel ManualStartNode() => new()
    {
        Id = 1,
        Name = "Manual start",
        Type = BpmnFlowNodeTypes.StartEvent
    };

    private async Task DeleteFamiliesAsync(params string[] workflowKeys)
    {
        await using var cleanup = fixture.CreateDbContext();
        await cleanup.WorkflowJobs
            .Where(job => workflowKeys.Contains(job.WorkflowKey))
            .ExecuteDeleteAsync();
        await cleanup.TimerSubscriptions
            .Where(subscription => workflowKeys.Contains(subscription.WorkflowKey))
            .ExecuteDeleteAsync();
        await cleanup.WorkflowDefinitions
            .Where(definition => workflowKeys.Contains(definition.WorkflowKey))
            .ExecuteDeleteAsync();
    }
}
