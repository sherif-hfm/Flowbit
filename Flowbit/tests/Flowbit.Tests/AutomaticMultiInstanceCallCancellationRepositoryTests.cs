using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Abstractions;
using Flowbit.Service.Models;
using Flowbit.Service.Services;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class AutomaticMultiInstanceCallCancellationRepositoryTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task Cancellation_queries_keep_automatic_and_ordinary_invocations_separate_and_omit_payloads()
    {
        await using var db = fixture.CreateDbContext();
        var key = $"automatic-call-cancel-{Guid.NewGuid():N}";
        var definition = new WorkflowDefinitionEntity { Name = key, WorkflowKey = key, Version = 1,
            Definition = new WorkflowModel { Id = key, Name = key } };
        db.WorkflowDefinitions.Add(definition);
        await db.SaveChangesAsync();
        var instance = new WorkflowInstanceEntity { WorkflowDefinitionId = definition.Id, WorkflowKey = key, Status = "running" };
        db.WorkflowInstances.Add(instance);
        await db.SaveChangesAsync();
        var token = new ExecutionTokenEntity { InstanceId = instance.Id, NodeId = 2, NodeName = "Calls", NodeType = "callActivity" };
        db.ExecutionTokens.Add(token);
        await db.SaveChangesAsync();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var bodies = new AutomaticMultiInstanceRepository(db);
        var body = await bodies.CreateAsync(new()
        {
            InstanceId = instance.Id, TokenId = token.Id, WorkflowDefinitionId = definition.Id,
            ActivationId = token.ActivationId, NodeId = 2, NodeName = "Calls", ActivityKind = "callActivity",
            Mode = "parallel", ResultVariable = "results", AggregateScope = "instance", AggregateValueVersion = 0,
            MaxInstances = 100, EffectiveConcurrency = 2, TotalCount = 2, ResultBytes = 2,
            PinnedChildDefinitionId = definition.Id, PinnedChildStartEventId = 1,
            InputSnapshot = JsonSerializer.SerializeToElement(new { collection = new[] { 1, 2 } })
        }, default);
        var items = await db.MultiInstanceActivityItems.Where(item => item.MultiInstanceExecutionId == body.Id)
            .OrderBy(item => item.Id).ToArrayAsync();
        CallActivityExecutionEntity Invocation(long? itemId, bool completed = false) => new()
        {
            ParentInstanceId = instance.Id, ParentTokenId = token.Id, ActivationId = token.ActivationId,
            ParentNodeId = token.NodeId, RootInstanceId = instance.Id, ChildDepth = 1, ParentDefinitionId = definition.Id,
            TargetWorkflowKey = "target", State = completed ? "completed" : "queued",
            SettledAt = completed ? DateTimeOffset.UtcNow : null,
            AutomaticMultiInstanceItemId = itemId, MultiInstanceExecutionId = itemId is null ? null : body.Id,
            ContractJson = JsonDocument.Parse("{\"workflowKey\":\"target\"}"),
            InputValuesJson = JsonDocument.Parse("{\"Values\":{\"secret\":123},\"Missing\":[]}")
        };
        var ordinary = Invocation(null);
        var automatic = Invocation(items[0].Id);
        db.CallActivityExecutions.AddRange(ordinary, automatic, Invocation(items[1].Id, completed: true));
        await db.SaveChangesAsync();
        var repository = new CallActivityRepository(db);
        var row = Assert.Single(await repository.ListUnsettledAutomaticForTokensAsync(instance.Id, [token.Id], default));
        Assert.Equal(automatic.Id, row.Id);
        Assert.Equal(items[0].Id, row.AutomaticMultiInstanceItemId);
        Assert.Equal(body.Id, row.MultiInstanceExecutionId);
        Assert.Null(row.Contract);
        Assert.Null(row.Inputs);
        Assert.Equal(ordinary.Id, Assert.Single(await repository.ListUnsettledForTokensAsync(instance.Id, [token.Id], default)).Id);
        Assert.Empty(await repository.ListUnsettledAutomaticForTokensAsync(instance.Id, [], default));
        Assert.Empty(await repository.ListUnsettledAutomaticForTokensAsync(instance.Id, [token.Id + 100000], default));
        Assert.Empty(await repository.ListUnsettledAutomaticForTokensAsync(instance.Id + 100000, [token.Id], default));
        // Deliberately roll back test-owned runtime state.
    }

    [Fact]
    public async Task Abandoning_a_running_item_call_queues_a_constraint_valid_cancel_message()
    {
        var key = $"automatic-call-abandon-{Guid.NewGuid():N}";
        long instanceId, tokenId, bodyId, invocationId;
        await using (var db = fixture.CreateDbContext())
        {
            var definition = new WorkflowDefinitionEntity { Name = key, WorkflowKey = key, Version = 1,
                Definition = new WorkflowModel { Id = key, Name = key } };
            db.WorkflowDefinitions.Add(definition);
            await db.SaveChangesAsync();
            var parent = new WorkflowInstanceEntity { WorkflowDefinitionId = definition.Id, WorkflowKey = key, Status = "running" };
            var child = new WorkflowInstanceEntity { WorkflowDefinitionId = definition.Id, WorkflowKey = key, Status = "running" };
            db.WorkflowInstances.AddRange(parent, child);
            await db.SaveChangesAsync();
            var token = new ExecutionTokenEntity { InstanceId = parent.Id, NodeId = 2, NodeName = "Calls", NodeType = "callActivity" };
            // Committed instances share the database with unfiltered list tests,
            // which require every instance to own an execution token.
            var childToken = new ExecutionTokenEntity { InstanceId = child.Id, NodeId = 1, NodeName = "Child", NodeType = "userTask" };
            db.ExecutionTokens.AddRange(token, childToken);
            await db.SaveChangesAsync();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var body = await new AutomaticMultiInstanceRepository(db).CreateAsync(new()
            {
                InstanceId = parent.Id, TokenId = token.Id, WorkflowDefinitionId = definition.Id,
                ActivationId = token.ActivationId, NodeId = 2, NodeName = "Calls", ActivityKind = "callActivity",
                Mode = "parallel", ResultVariable = "results", AggregateScope = "instance", AggregateValueVersion = 0,
                MaxInstances = 100, EffectiveConcurrency = 1, TotalCount = 1, ResultBytes = 2,
                PinnedChildDefinitionId = definition.Id, PinnedChildStartEventId = 1,
                InputSnapshot = JsonSerializer.SerializeToElement(new { collection = new[] { 1 } })
            }, default);
            var item = await db.MultiInstanceActivityItems.SingleAsync(row => row.MultiInstanceExecutionId == body.Id);
            var now = DateTimeOffset.UtcNow;
            var invocation = new CallActivityExecutionEntity
            {
                ParentInstanceId = parent.Id, ParentTokenId = token.Id, ActivationId = token.ActivationId,
                ParentNodeId = token.NodeId, RootInstanceId = parent.Id, ChildDepth = 1, ParentDefinitionId = definition.Id,
                TargetWorkflowKey = key, State = "running", CapacityReserved = true, AdmittedAt = now,
                ChildInstanceId = child.Id, ChildDefinitionId = definition.Id, ChildStartEventId = 1,
                AutomaticMultiInstanceItemId = item.Id, MultiInstanceExecutionId = body.Id,
                ContractJson = JsonDocument.Parse("{\"workflowKey\":\"target\"}")
            };
            db.CallActivityExecutions.Add(invocation);
            await db.SaveChangesAsync();
            await transaction.CommitAsync();
            (instanceId, tokenId, bodyId, invocationId) = (parent.Id, token.Id, body.Id, invocation.Id);
        }

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var engine = scope.ServiceProvider.GetRequiredService<WorkflowEngineService>();
            var unitOfWork = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var runtime = scope.ServiceProvider.GetRequiredService<IWorkflowRuntimeRepository>();
            var bodies = scope.ServiceProvider.GetRequiredService<IAutomaticMultiInstanceRepository>();
            await using var transaction = await unitOfWork.BeginTransactionAsync(default);
            var instance = (await runtime.GetInstanceAsync(instanceId, default))!;
            var body = (await bodies.GetAsync(bodyId, false, default))!;
            await engine.CreateAutomaticMultiInstanceLifecycle(instance, new WorkflowModel { Id = key, Name = key },
                    new ActorContext("tester", [], new Dictionary<string, string>()))
                .AbandonItemCallsAsync(body, default);
            await unitOfWork.SaveChangesAsync(default);
            await transaction.CommitAsync(default);
        }

        await using var verify = fixture.CreateDbContext();
        try
        {
            Assert.Equal("cancelled", (await verify.CallActivityExecutions.SingleAsync(call => call.Id == invocationId)).State);
            var message = await verify.WorkflowJobs.SingleAsync(job => job.CallInvocationId == invocationId && job.Kind == "callCancel");
            Assert.Equal(instanceId, message.InstanceId);
            Assert.Equal(tokenId, message.TokenId);
            Assert.Null(message.AutomaticMultiInstanceItemId);
            Assert.Null(message.MultiInstanceExecutionId);
        }
        finally
        {
            // The shared database also hosts worker lease tests; a queued
            // message from this test must never win their lease.
            await new WorkflowJobRepository(verify, fixture.DataSource).CancelByInstanceAsync(instanceId, "testCleanup", default);
        }
    }
}
