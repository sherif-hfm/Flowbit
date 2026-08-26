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
}
