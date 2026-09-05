using System.Text.Json;
using Flowbit.Infrastructure.Entities;
using Flowbit.Infrastructure.Data.Migrations;
using Flowbit.Infrastructure.Repositories;
using Flowbit.Service.Models;
using Flowbit.Shared.Models;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Xunit;

namespace Flowbit.Tests;

[Collection(PostgresApiCollection.Name)]
public sealed class UserTaskRolePolicyPersistenceTests(PostgresApiFixture fixture)
{
    [Fact]
    public async Task CutoverBackfillsOpenNormalAndMultiInstancePoliciesWithoutInventingCompletedPolicies()
    {
        await using var db = fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var definition = NewDefinition();
        db.WorkflowDefinitions.Add(definition);
        await db.SaveChangesAsync();
        var instance = new WorkflowInstanceEntity
        {
            WorkflowDefinitionId = definition.Id, WorkflowKey = definition.WorkflowKey, Status = "running"
        };
        db.WorkflowInstances.Add(instance);
        await db.SaveChangesAsync();
        var normalToken = new ExecutionTokenEntity
        {
            InstanceId = instance.Id, NodeId = 2, NodeName = "Normal", NodeType = "userTask"
        };
        var multiToken = new ExecutionTokenEntity
        {
            InstanceId = instance.Id, NodeId = 2, NodeName = "Multi", NodeType = "userTask"
        };
        db.ExecutionTokens.AddRange(normalToken, multiToken);
        await db.SaveChangesAsync();
        var normal = new UserTaskEntity
        {
            InstanceId = instance.Id, TokenId = normalToken.Id, NodeId = 2,
            NodeName = "Normal", Roles = ["legacy-snapshot"], Status = UserTaskStatuses.Pending
        };
        var multi = new MultiInstanceExecutionEntity
        {
            InstanceId = instance.Id, TokenId = multiToken.Id, NodeId = 2,
            Mode = "sequential", Source = "cardinality", ResultVariable = "results", TotalCount = 3
        };
        db.MultiInstanceExecutions.Add(multi);
        db.UserTasks.Add(normal);
        await db.SaveChangesAsync();
        var children = new[] { UserTaskStatuses.Completed, UserTaskStatuses.Active, UserTaskStatuses.Pending }
            .Select((status, index) => new UserTaskEntity
            {
                InstanceId = instance.Id, TokenId = multiToken.Id, NodeId = 2, NodeName = "Multi",
                Roles = ["authored"], MultiInstanceExecutionId = multi.Id, ItemIndex = index, Status = status
            }).ToArray();
        db.UserTasks.AddRange(children);
        await db.SaveChangesAsync();
        var backfill = Assert.Single(new AddUserTaskRolePolicies().UpOperations.OfType<SqlOperation>()).Sql;
        await using (var command = db.Database.GetDbConnection().CreateCommand())
        {
            command.Transaction = transaction.GetDbTransaction();
            command.CommandText = backfill;
            await command.ExecuteNonQueryAsync();
        }
        db.ChangeTracker.Clear();
        var runtime = new WorkflowRuntimeRepository(db);
        var normalRecord = await runtime.GetUserTaskAsync(normal.Id, false, CancellationToken.None);
        Assert.Equal(new[] { "legacy-snapshot" }, normalRecord!.RolePolicy!.Roles);
        Assert.Equal(new[] { "authored-flow" }, normalRecord.RolePolicy.OutgoingFlowRoles[10]);
        var multiRecord = await runtime.GetMultiInstanceAsync(multi.Id, false, CancellationToken.None);
        Assert.Equal(new[] { "authored" }, multiRecord!.RolePolicy!.Roles);
        var restoredChildren = await runtime.ListExecutionTasksAsync(multi.Id, CancellationToken.None);
        Assert.Null(restoredChildren[0].RolePolicyId);
        Assert.All(restoredChildren.Skip(1), child => Assert.Equal(multiRecord.RolePolicyId, child.RolePolicyId));
        Assert.Equal(2, await db.UserTaskRolePolicies.CountAsync(policy => policy.InstanceId == instance.Id));
    }

    [Fact]
    public async Task NormalTaskPolicyIsPersistedHydratedAndReplacedWithoutChangingOwnershipOrEntryHistory()
    {
        await using var db = fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var definition = NewDefinition();
        db.WorkflowDefinitions.Add(definition);
        await db.SaveChangesAsync();
        var runtime = new WorkflowRuntimeRepository(db);
        var instance = await runtime.AddInstanceAsync(definition.Id, definition.WorkflowKey,
            null, null, null, Snapshot(1, "startEvent"), "starter", [], CancellationToken.None);
        var token = await runtime.AddExecutionTokenAsync(instance.Id, Snapshot(2, "userTask"),
            null, 1, new NodeExecutionActorRecord("starter", []), CancellationToken.None);
        var taskId = await db.UserTasks.Where(task => task.TokenId == token.Id)
            .Select(task => task.Id).SingleAsync();
        db.ChangeTracker.Clear();

        var task = await runtime.GetUserTaskAsync(taskId, false, CancellationToken.None);
        Assert.NotNull(task!.RolePolicy);
        Assert.Equal(new[] { "initial" }, task.Roles);
        Assert.Equal(new[] { "approve" }, task.RolePolicy.OutgoingFlowRoles[10]);
        Assert.Equal("alice", task.Assignee);
        var initialPolicyId = task.RolePolicyId!.Value;

        await runtime.GetInstanceForUpdateAsync(instance.Id, false, CancellationToken.None);
        await runtime.GetExecutionTokenAsync(token.Id, true, CancellationToken.None);
        await runtime.GetUserTaskAsync(taskId, true, CancellationToken.None);
        var replacement = await runtime.ReplaceUserTaskRolePolicyAsync(instance.Id, taskId, null,
            Policy("replacement", "review"), CancellationToken.None);
        Assert.NotEqual(initialPolicyId, replacement.Id);
        db.ChangeTracker.Clear();
        var updated = await runtime.GetUserTaskAsync(taskId, false, CancellationToken.None);
        Assert.Equal(replacement.Id, updated!.RolePolicyId);
        Assert.Equal(new[] { "replacement" }, updated.Roles);
        Assert.Equal(new[] { "review" }, updated.RolePolicy!.OutgoingFlowRoles[10]);
        Assert.Equal("alice", updated.Assignee);
        Assert.Equal(new[] { "initial" }, (await runtime.GetRolePolicyAsync(initialPolicyId, CancellationToken.None))!.Roles);
        var entryRoles = await db.NodeExecutions.Where(entry => entry.UserTaskId == taskId)
            .Select(entry => entry.NodeRolesJson).SingleAsync();
        Assert.Equal("initial", entryRoles!.RootElement[0].GetString());
    }

    [Fact]
    public async Task MultiInstancePolicyIsSharedAndReplacementOnlyRepointsUnfinishedChildrenAndParent()
    {
        await using var db = fixture.CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync();
        var definition = NewDefinition();
        db.WorkflowDefinitions.Add(definition);
        await db.SaveChangesAsync();
        var runtime = new WorkflowRuntimeRepository(db);
        var instance = await runtime.AddInstanceAsync(definition.Id, definition.WorkflowKey,
            null, null, null, Snapshot(1, "startEvent"), "starter", [], CancellationToken.None);
        var token = await runtime.AddExecutionTokenAsync(instance.Id,
            Snapshot(2, "userTask") with { IsMultiInstance = true }, null, 1,
            new NodeExecutionActorRecord("starter", []), CancellationToken.None);
        var execution = await runtime.AddMultiInstanceAsync(instance.Id, token.Id,
            Snapshot(2, "userTask") with { IsMultiInstance = true },
            new MultiInstanceModel
            {
                Mode = "sequential", Source = "cardinality", CardinalityExpression = "3",
                ResultVariable = "results"
            }, [null, null, null], [10], new NodeExecutionActorRecord("starter", []), CancellationToken.None);
        var initialPolicyId = execution.RolePolicyId!.Value;
        var items = await db.UserTasks.Where(task => task.MultiInstanceExecutionId == execution.Id)
            .OrderBy(task => task.ItemIndex).ToListAsync();
        Assert.All(items, task => Assert.Equal(initialPolicyId, task.RolePolicyId));
        items[0].Status = UserTaskStatuses.Completed;
        items[0].CompletedAt = DateTimeOffset.UtcNow;
        items[1].Status = UserTaskStatuses.Active;
        items[1].ClaimedBy = "alice";
        items[2].Assignee = "bob";
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        await runtime.GetInstanceForUpdateAsync(instance.Id, false, CancellationToken.None);
        await runtime.GetExecutionTokenAsync(token.Id, true, CancellationToken.None);
        var locked = await runtime.GetMultiInstanceAsync(execution.Id, true, CancellationToken.None);
        Assert.Equal(new[] { "approve" }, locked!.RolePolicy!.OutgoingFlowRoles[10]);
        var replacement = await runtime.ReplaceUserTaskRolePolicyAsync(instance.Id, null, execution.Id,
            Policy("replacement", "review"), CancellationToken.None);
        db.ChangeTracker.Clear();
        var children = await runtime.ListExecutionTasksAsync(execution.Id, CancellationToken.None);
        Assert.Equal(initialPolicyId, children[0].RolePolicyId);
        Assert.Equal(new[] { "initial" }, children[0].Roles);
        Assert.All(children.Skip(1), child =>
        {
            Assert.Equal(replacement.Id, child.RolePolicyId);
            Assert.Equal(new[] { "replacement" }, child.Roles);
            Assert.Equal(new[] { "review" }, child.RolePolicy!.OutgoingFlowRoles[10]);
        });
        Assert.Equal("alice", children[1].ClaimedBy);
        Assert.Equal("bob", children[2].Assignee);
        var parent = await runtime.GetMultiInstanceAsync(execution.Id, false, CancellationToken.None);
        Assert.Equal(replacement.Id, parent!.RolePolicyId);
        Assert.Equal(new[] { "replacement" }, parent.RolePolicy!.Roles);
        var progress = await runtime.GetMultiInstanceProgressAsync([execution.Id], CancellationToken.None);
        Assert.NotNull(progress[execution.Id].Execution.RolePolicy);

        var pending = await runtime.ListManageableUserTasksAsync(["role-manager"], null,
            instance.Id, null, null, null, null, null, null, null, null,
            1, 20, CancellationToken.None, "pending");
        Assert.Equal(1, pending.TotalCount);
        Assert.Equal(UserTaskStatuses.Pending, Assert.Single(pending.Items).Status);
        var open = await runtime.ListManageableUserTasksAsync(["assigner"], null,
            instance.Id, null, null, null, null, null, null, null, null,
            1, 20, CancellationToken.None, "open");
        Assert.Equal(1, open.TotalCount);
    }

    private static ResolvedUserTaskRolePolicy Policy(string role, string flowRole) =>
        new([role], new Dictionary<int, IReadOnlyList<string>> { [10] = [flowRole] });

    private static CurrentNodeSnapshot Snapshot(int id, string type) =>
        new(id, type, null, type, ["initial"], false, false, type == "userTask" ? "alice" : null)
        {
            RolePolicy = type == "userTask" ? Policy("initial", "approve") : null
        };

    private static WorkflowDefinitionEntity NewDefinition()
    {
        var key = $"role-policy-persistence-{Guid.NewGuid():N}";
        return new WorkflowDefinitionEntity
        {
            Name = key, WorkflowKey = key, Version = 1, IsPublished = true,
            Definition = new WorkflowModel
            {
                Id = key, Name = key, TaskAssignmentRoles = ["assigner"], TaskRoleManagementRoles = ["role-manager"],
                FlowNodes = [new() { Id = 1, Type = "startEvent" }, new() { Id = 2, Type = "userTask", Roles = ["authored"] }, new() { Id = 3, Type = "endEvent" }],
                SequenceFlows = [new() { Id = 1, SourceRef = 1, TargetRef = 2 }, new() { Id = 10, SourceRef = 2, TargetRef = 3, Roles = ["authored-flow"] }]
            }
        };
    }
}
