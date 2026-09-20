using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Flowbit.Infrastructure.Data;
using Flowbit.Infrastructure.Entities;
using Flowbit.Service.Abstractions;
using Flowbit.Shared.Dtos;
using Flowbit.Shared.Models;
using Xunit;

namespace Flowbit.Tests;

/// <summary>
/// Characterizes the instance detail and execution projections: grouped
/// reader counts for the warm- and cold-cache detail paths and the
/// current-only (slim-ack) execution projection, projection-only reads,
/// scope/transaction visibility, and reader-count independence from
/// child-item/definition volumes. The detail baselines were recorded against
/// the engine before the detail/execution projection moved into
/// <c>WorkflowInstanceProjectionService</c>; the current-projection baseline
/// was recorded post-extraction against the same moved code (whose equivalence
/// is covered by the full suite). All counts must hold through the service.
/// </summary>
[Collection(PostgresApiCollection.Name)]
public sealed class InstanceProjectionTests(PostgresApiFixture fixture)
{
    // Reader commands for one full detail projection after the workflow
    // definition cache is warm (instance, variables, history, version changes,
    // variable-update audits, tokens, tasks, multi-instance executions,
    // grouped multi-instance progress, gateway executions, gateway branches,
    // complex gateway states, and grouped work summaries).
    private const int ExpectedDetailReaderCommands = 17;

    // Reader commands for the first detail projection on a workflow whose
    // immutable definition is not yet cached: the warm baseline plus the
    // one-time definition lookup (the version-change audit definitions are
    // then already cached by that lookup).
    private const int ExpectedDetailColdCacheReaderCommands = 18;

    // Reader commands for one current-only (slim-ack) execution projection:
    // current tokens, current user tasks, current multi-instance executions,
    // grouped multi-instance progress, current (active) gateway executions,
    // and complex gateway states. With no active gateway execution there is
    // no branch read, so this is the floor for the ack paths.
    private const int ExpectedCurrentReaderCommands = 7;

    [Fact]
    public async Task AuthenticatedDetailRemainsReadableWithoutListVisibility()
    {
        var seed = await SeedProjectionDatasetAsync(
            workflowKey: $"projection-read-scope-{Guid.NewGuid():N}",
            multiInstanceItemCount: 1, flowCountCount: 1,
            versionChangeAuditCount: 1, versionChangeDefinitionCount: 1);
        using var listRequest = ApiTestAuth.Authorize(new HttpRequestMessage(
            HttpMethod.Get, $"/api/instances?instanceId={seed.InstanceId}"),
            "unrelated-reader", "unrelated-role");
        listRequest.Headers.Add("X-Test-Suppress-Admin", "true");
        using var listResponse = await fixture.Client.SendAsync(listRequest);
        Assert.Equal(HttpStatusCode.OK, listResponse.StatusCode);
        var page = await listResponse.Content.ReadFromJsonAsync<PagedResult<InstanceSummaryDto>>();
        Assert.Empty(page!.Items);

        using var detailRequest = ApiTestAuth.Authorize(new HttpRequestMessage(
            HttpMethod.Get, $"/api/instances/{seed.InstanceId}"),
            "unrelated-reader", "unrelated-role");
        detailRequest.Headers.Add("X-Test-Suppress-Admin", "true");
        using var detailResponse = await fixture.Client.SendAsync(detailRequest);
        Assert.Equal(HttpStatusCode.OK, detailResponse.StatusCode);
        var detail = await detailResponse.Content.ReadFromJsonAsync<InstanceDetailDto>();
        Assert.Equal(seed.InstanceId, detail!.Id);
        Assert.Equal(seed.WorkflowKey, detail.Workflow.WorkflowKey);
    }

    [Fact]
    public async Task DetailProjectionKeepsTheRecordedReaderBaselineAndWritesNoRows()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var seed = await SeedProjectionDatasetAsync(
            workflowKey: $"projection-baseline-{suffix}",
            multiInstanceItemCount: 3,
            flowCountCount: 2,
            versionChangeAuditCount: 2,
            versionChangeDefinitionCount: 1);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var projections = scope.ServiceProvider.GetRequiredService<IWorkflowInstanceProjectionService>();

        // Warm the workflow definition cache so the measured projection excludes
        // the one-time immutable definition lookup.
        var warmup = await projections.GetDetailAsync(seed.InstanceId, CancellationToken.None);
        Assert.NotNull(warmup);

        fixture.CommandCounter.Reset();
        var detail = await projections.GetDetailAsync(seed.InstanceId, CancellationToken.None);
        var readerCommands = fixture.CommandCounter.ReaderCommands;

        Assert.NotNull(detail);
        Assert.Equal(ExpectedDetailReaderCommands, readerCommands);

        // The projection only reads: it writes no history, variable, audit, or
        // workflow rows even though the read-only scope disposed cleanly.
        await using var verify = fixture.CreateDbContext();
        Assert.Equal(3, await verify.InstanceHistory.CountAsync(
            history => history.Instance!.WorkflowDefinition!.WorkflowKey == seed.WorkflowKey));
        Assert.Equal(2, await verify.InstanceVariables.CountAsync(
            variable => variable.Instance!.WorkflowDefinition!.WorkflowKey == seed.WorkflowKey));
        Assert.Equal(2, await verify.WorkflowInstanceVersionChanges.CountAsync(
            change => change.Instance!.WorkflowDefinition!.WorkflowKey == seed.WorkflowKey));
        Assert.Equal(1, await verify.InstanceVariableUpdates.CountAsync(
            audit => audit.Instance!.WorkflowDefinition!.WorkflowKey == seed.WorkflowKey));
    }

    [Fact]
    public async Task DetailProjectionColdCacheReaderCountIncludesTheDefinitionLookup()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var seed = await SeedProjectionDatasetAsync(
            workflowKey: $"projection-cold-{suffix}",
            multiInstanceItemCount: 3,
            flowCountCount: 2,
            versionChangeAuditCount: 2,
            versionChangeDefinitionCount: 1);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var projections = scope.ServiceProvider.GetRequiredService<IWorkflowInstanceProjectionService>();

        // The workflow key is unique per run, so the process-wide definition
        // cache is cold: the first detail projection adds the immutable
        // definition lookup to the warm baseline.
        fixture.CommandCounter.Reset();
        var detail = await projections.GetDetailAsync(seed.InstanceId, CancellationToken.None);
        var coldCommands = fixture.CommandCounter.ReaderCommands;

        Assert.NotNull(detail);
        Assert.Equal(ExpectedDetailColdCacheReaderCommands, coldCommands);

        // The immediately-following projection is exactly the warm baseline.
        fixture.CommandCounter.Reset();
        Assert.NotNull(await projections.GetDetailAsync(seed.InstanceId, CancellationToken.None));
        Assert.Equal(ExpectedDetailReaderCommands, fixture.CommandCounter.ReaderCommands);
    }

    [Fact]
    public async Task CurrentProjectionKeepsTheRecordedReaderBaseline()
    {
        var suffix = Guid.NewGuid().ToString("N");
        var seed = await SeedProjectionDatasetAsync(
            workflowKey: $"projection-current-{suffix}",
            multiInstanceItemCount: 3,
            flowCountCount: 2,
            versionChangeAuditCount: 2,
            versionChangeDefinitionCount: 1);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var runtime = scope.ServiceProvider.GetRequiredService<IWorkflowRuntimeRepository>();
        var projections = scope.ServiceProvider
            .GetRequiredService<IWorkflowInstanceProjectionService>();

        var instance = await runtime.GetInstanceAsync(seed.InstanceId, CancellationToken.None);
        Assert.NotNull(instance);

        // Warm up, then measure the current-only (slim-ack) execution
        // projection used by start/message/action/administrative responses.
        _ = await projections.BuildExecutionAsync(instance, includeHistory: false, CancellationToken.None);
        fixture.CommandCounter.Reset();
        var projection = await projections.BuildExecutionAsync(
            instance, includeHistory: false, CancellationToken.None);
        var currentCommands = fixture.CommandCounter.ReaderCommands;

        Assert.Equal(ExpectedCurrentReaderCommands, currentCommands);
        Assert.Single(projection.ExecutionPositions);
        Assert.Single(projection.MultiInstances);
        Assert.Empty(projection.GatewayExecutions);
        Assert.Single(projection.ComplexGatewayStates);
        Assert.Null(projection.Completion);
    }

    [Fact]
    public async Task DetailProjectionReaderCountIsIndependentOfChildItemAndDefinitionCounts()
    {
        var smallSeed = await SeedProjectionDatasetAsync(
            workflowKey: $"projection-small-{Guid.NewGuid():N}",
            multiInstanceItemCount: 1,
            flowCountCount: 1,
            versionChangeAuditCount: 1,
            versionChangeDefinitionCount: 1);
        var largeSeed = await SeedProjectionDatasetAsync(
            workflowKey: $"projection-large-{Guid.NewGuid():N}",
            multiInstanceItemCount: 6,
            flowCountCount: 5,
            versionChangeAuditCount: 5,
            versionChangeDefinitionCount: 3);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var projections = scope.ServiceProvider.GetRequiredService<IWorkflowInstanceProjectionService>();

        // Warm the definition caches and take the first reading as the baseline.
        _ = await projections.GetDetailAsync(smallSeed.InstanceId, CancellationToken.None);
        fixture.CommandCounter.Reset();
        var smallDetail = await projections.GetDetailAsync(smallSeed.InstanceId, CancellationToken.None);
        var smallCommands = fixture.CommandCounter.ReaderCommands;

        _ = await projections.GetDetailAsync(largeSeed.InstanceId, CancellationToken.None);
        fixture.CommandCounter.Reset();
        var largeDetail = await projections.GetDetailAsync(largeSeed.InstanceId, CancellationToken.None);
        var largeCommands = fixture.CommandCounter.ReaderCommands;

        Assert.NotNull(smallDetail);
        Assert.NotNull(largeDetail);
        Assert.Equal(smallCommands, largeCommands);
        Assert.Equal(1, largeDetail.MultiInstances.Count);
        Assert.Equal(1, largeDetail.ExecutionPositions.Count);
    }

    [Fact]
    public async Task ProjectionReadsSeeFlushedStateInsideTheCallerTransactionAndRollbackLeavesNoRows()
    {
        var workflowKey = $"projection-scope-{Guid.NewGuid():N}";
        var now = DateTimeOffset.UtcNow;
        var workflowId = await SeedDefinitionAsync(workflowKey, now);

        long transientInstanceId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var projections = scope.ServiceProvider.GetRequiredService<IWorkflowInstanceProjectionService>();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            await using var transaction = await unitOfWork.BeginTransactionAsync(CancellationToken.None);
            var instance = new WorkflowInstanceEntity
            {
                WorkflowDefinitionId = workflowId,
                WorkflowKey = workflowKey,
                Status = "running",
                StartedBy = "starter",
                CreatedAt = now,
                UpdatedAt = now
            };
            db.WorkflowInstances.Add(instance);
            await unitOfWork.SaveChangesAsync(CancellationToken.None);
            transientInstanceId = instance.Id;

            db.ExecutionTokens.Add(new ExecutionTokenEntity
            {
                InstanceId = instance.Id,
                NodeId = 2,
                NodeName = "Review",
                NodeExternalId = "REVIEW",
                NodeType = BpmnFlowNodeTypes.UserTask,
                Status = ExecutionTokenStatuses.Active,
                CreatedAt = now,
                UpdatedAt = now
            });
            db.InstanceHistory.Add(new InstanceHistoryEntity
            {
                InstanceId = instance.Id,
                WorkflowDefinitionId = workflowId,
                FromStepId = 1,
                ToStepId = 2,
                PerformedBy = "starter",
                Note = "start",
                PerformedAt = now
            });
            db.InstanceVariables.Add(new InstanceVariableEntity
            {
                InstanceId = instance.Id,
                VariableName = "amount",
                ValueJson = JsonDocument.Parse("10"),
                SetBy = "starter",
                SetAt = now
            });
            db.InstanceVariableUpdates.Add(new InstanceVariableUpdateAuditEntity
            {
                InstanceId = instance.Id,
                WorkflowDefinitionId = workflowId,
                PerformedBy = "admin",
                PerformedByRolesJson = JsonDocument.Parse("""["admin"]"""),
                Reason = "in-flight correction",
                RequestedVariablesJson = JsonDocument.Parse("""[]"""),
                ResultJson = JsonDocument.Parse("""[]"""),
                PerformedAt = now
            });
            await unitOfWork.SaveChangesAsync(CancellationToken.None);

            // The scoped projection service reads the flushed, uncommitted state
            // through the same DbContext without creating or committing work.
            var detail = await projections.GetDetailAsync(instance.Id, CancellationToken.None);
            Assert.NotNull(detail);
            Assert.Equal(1, detail.History.Count);
            Assert.Equal(1, detail.Variables.Count);
            Assert.Single(detail.VariableUpdates);

            // Disposing without committing rolls the writes back.
        }

        await using var freshScope = fixture.Factory.Services.CreateAsyncScope();
        var freshProjections = freshScope.ServiceProvider
            .GetRequiredService<IWorkflowInstanceProjectionService>();
        Assert.Null(await freshProjections.GetDetailAsync(transientInstanceId, CancellationToken.None));
    }

    private sealed record ProjectionSeed(string WorkflowKey, long WorkflowId, long InstanceId);

    private async Task<ProjectionSeed> SeedProjectionDatasetAsync(
        string workflowKey,
        int multiInstanceItemCount,
        int flowCountCount,
        int versionChangeAuditCount,
        int versionChangeDefinitionCount)
    {
        var now = DateTimeOffset.UtcNow;
        var workflowId = await SeedDefinitionAsync(workflowKey, now);
        var auditDefinitionIds = new List<long> { workflowId };
        for (var index = 0; index < Math.Max(0, versionChangeDefinitionCount - 1); index++)
        {
            auditDefinitionIds.Add(await SeedDefinitionAsync($"{workflowKey}-v{index}", now));
        }

        await using var setup = fixture.CreateDbContext();
        var instance = new WorkflowInstanceEntity
        {
            WorkflowDefinitionId = workflowId,
            WorkflowKey = workflowKey,
            Status = "running",
            StartedBy = "starter",
            CreatedAt = now,
            UpdatedAt = now
        };
        setup.WorkflowInstances.Add(instance);
        await setup.SaveChangesAsync();

        var tokenId = new ExecutionTokenEntity
        {
            InstanceId = instance.Id,
            NodeId = 2,
            NodeName = "Review",
            NodeExternalId = "REVIEW",
            NodeType = BpmnFlowNodeTypes.UserTask,
            Status = ExecutionTokenStatuses.Active,
            ArrivedViaFlowId = 101,
            CreatedAt = now,
            UpdatedAt = now
        };
        setup.ExecutionTokens.Add(tokenId);
        await setup.SaveChangesAsync();

        var multiInstance = new MultiInstanceExecutionEntity
        {
            InstanceId = instance.Id,
            TokenId = tokenId.Id,
            NodeId = 2,
            Mode = "parallel",
            Source = "collection",
            ResultVariable = "miResult",
            Status = MultiInstanceExecutionStatuses.Active,
            TotalCount = multiInstanceItemCount,
            CompletedCount = multiInstanceItemCount - 1,
            CreatedAt = now,
            UpdatedAt = now
        };
        setup.MultiInstanceExecutions.Add(multiInstance);
        await setup.SaveChangesAsync();

        for (var index = 0; index < multiInstanceItemCount; index++)
        {
            setup.UserTasks.Add(new UserTaskEntity
            {
                InstanceId = instance.Id,
                TokenId = tokenId.Id,
                NodeId = 2,
                NodeName = "Review",
                NodeExternalId = "REVIEW",
                Roles = ["approver"],
                Status = index < multiInstanceItemCount - 1
                    ? UserTaskStatuses.Completed
                    : UserTaskStatuses.Active,
                MultiInstanceExecutionId = multiInstance.Id,
                ItemIndex = index,
                ItemValueJson = JsonDocument.Parse(JsonSerializer.Serialize($"item-{index}")),
                SelectedFlowId = index < multiInstanceItemCount - 1 ? 201 : null,
                CompletedBy = index < multiInstanceItemCount - 1 ? "actor" : null,
                CompletedAt = index < multiInstanceItemCount - 1 ? now.AddMinutes(index + 1) : null,
                CreatedAt = now,
                UpdatedAt = now
            });
        }

        for (var index = 0; index < flowCountCount; index++)
        {
            setup.MultiInstanceFlowCounts.Add(new MultiInstanceFlowCountEntity
            {
                ExecutionId = multiInstance.Id,
                FlowId = 201 + index,
                CompletedCount = index + 1
            });
        }

        setup.ExecutionTokens.Add(new ExecutionTokenEntity
        {
            InstanceId = instance.Id,
            NodeId = 3,
            NodeName = "Done",
            NodeType = BpmnFlowNodeTypes.EndEvent,
            Status = ExecutionTokenStatuses.Merged,
            TerminationReason = "gatewayJoinMerged",
            CreatedAt = now,
            UpdatedAt = now
        });

        for (var index = 0; index < 3; index++)
        {
            setup.InstanceHistory.Add(new InstanceHistoryEntity
            {
                InstanceId = instance.Id,
                WorkflowDefinitionId = workflowId,
                FromStepId = 1,
                ToStepId = 2,
                PerformedBy = index % 2 == 0 ? "starter" : "approver",
                Note = index == 0 ? "start" : "multiInstanceItem",
                TokenId = tokenId.Id,
                MultiInstanceExecutionId = index == 0 ? null : multiInstance.Id,
                ItemIndex = index == 0 ? null : index - 1,
                PerformedAt = now.AddMinutes(index)
            });
        }

        setup.InstanceVariables.Add(new InstanceVariableEntity
        {
            InstanceId = instance.Id,
            VariableName = "amount",
            ValueJson = JsonDocument.Parse("1250"),
            SetBy = "starter",
            SetAt = now
        });
        setup.InstanceVariables.Add(new InstanceVariableEntity
        {
            InstanceId = instance.Id,
            VariableName = "approved",
            ValueJson = JsonDocument.Parse("true"),
            SetBy = "approver",
            SetAt = now.AddMinutes(1)
        });

        for (var index = 0; index < versionChangeAuditCount; index++)
        {
            var sourceId = auditDefinitionIds[index % auditDefinitionIds.Count];
            var targetId = auditDefinitionIds[(index + 1) % auditDefinitionIds.Count];
            setup.WorkflowInstanceVersionChanges.Add(new WorkflowInstanceVersionChangeEntity
            {
                InstanceId = instance.Id,
                SourceWorkflowDefinitionId = sourceId,
                TargetWorkflowDefinitionId = targetId,
                ChangedBy = "workflow-admin",
                ChangedByRolesJson = JsonDocument.Parse("""["admin"]"""),
                Reason = "planned correction",
                ChangedAt = now.AddMinutes(index)
            });
        }

        setup.InstanceVariableUpdates.Add(new InstanceVariableUpdateAuditEntity
        {
            InstanceId = instance.Id,
            WorkflowDefinitionId = workflowId,
            PerformedBy = "admin",
            PerformedByRolesJson = JsonDocument.Parse("""["admin"]"""),
            Reason = "corrected amount",
            RequestedVariablesJson = JsonDocument.Parse("""[{"name":"amount","value":1250}]"""),
            ResultJson = JsonDocument.Parse(
                """[{"name":"amount","outcome":"created","variableId":1,"value":1250}]"""),
            PerformedAt = now.AddMinutes(2)
        });

        var gatewayExecution = new GatewayExecutionEntity
        {
            InstanceId = instance.Id,
            GatewayNodeId = 4,
            GatewayType = "parallelGateway",
            Direction = GatewayExecutionDirections.Split,
            SelectedFlowIds = [202, 203],
            Status = GatewayExecutionStatuses.Joined,
            CompletionReason = "normal",
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now
        };
        setup.GatewayExecutions.Add(gatewayExecution);
        await setup.SaveChangesAsync();

        setup.GatewayBranches.Add(new GatewayBranchEntity
        {
            ExecutionId = gatewayExecution.Id,
            OriginatingFlowId = 202,
            Ordinal = 0,
            Status = GatewayBranchStatuses.Merged,
            CreatedAt = now,
            UpdatedAt = now,
            CompletedAt = now
        });

        setup.ComplexGatewayStates.Add(new ComplexGatewayStateEntity
        {
            InstanceId = instance.Id,
            GatewayNodeId = 5,
            Phase = ComplexGatewayStatePhases.WaitingForStart,
            Cycle = 1,
            ContributingFlowIds = [301],
            RemainingFlowIds = [],
            DrainingTokenIds = [],
            UpdatedAt = now
        });

        await setup.SaveChangesAsync();
        return new ProjectionSeed(workflowKey, workflowId, instance.Id);
    }

    private async Task<long> SeedDefinitionAsync(string key, DateTimeOffset now)
    {
        await using var setup = fixture.CreateDbContext();
        var definition = new WorkflowDefinitionEntity
        {
            Name = key,
            WorkflowKey = key,
            Version = 1,
            IsPublished = true,
            Definition = new WorkflowModel
            {
                Id = key,
                Name = key,
                InitialEventId = 1,
                FlowNodes =
                [
                    new FlowNodeModel
                    {
                        Id = 1,
                        Name = "Start",
                        Type = BpmnFlowNodeTypes.StartEvent
                    },
                    new FlowNodeModel
                    {
                        Id = 2,
                        Name = "Review",
                        ExternalId = "REVIEW",
                        Type = BpmnFlowNodeTypes.UserTask
                    },
                    new FlowNodeModel
                    {
                        Id = 3,
                        Name = "Done",
                        Type = BpmnFlowNodeTypes.EndEvent
                    }
                ]
            },
            CreatedAt = now
        };
        setup.WorkflowDefinitions.Add(definition);
        await setup.SaveChangesAsync();
        return definition.Id;
    }
}
